using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlipPix.Mobile.Services;
using FlipPix.Remote.Contracts;

namespace FlipPix.Mobile.ViewModels;

/// <summary>One square on the library grid. Its thumbnail is fetched the first time the grid shows it.</summary>
public sealed partial class LibraryItemVm : ObservableObject
{
    private Bitmap? _thumb;
    private bool _requested;

    public LibraryItemVm(LibraryItemDto dto) => Dto = dto;

    public LibraryItemDto Dto { get; }
    public bool IsVideo => Dto.Kind == MediaKinds.Video;
    public DateTime LocalDay => Dto.Modified.ToLocalTime().Date;

    public Bitmap? Thumb
    {
        get
        {
            if (!_requested)
            {
                _requested = true;
                _ = LoadAsync();
            }
            return _thumb;
        }
    }

    private async Task LoadAsync()
    {
        var bitmap = await ImageLoader.LoadAsync(Dto.ThumbUrl, 360);
        if (bitmap == null)
        {
            _requested = false; // try again next time it scrolls into view
            return;
        }
        _thumb = bitmap;
        OnPropertyChanged(nameof(Thumb));
    }
}

/// <summary>A row of the grid: up to three squares, and the day's heading when it starts a new day.</summary>
public sealed class LibraryRow
{
    public LibraryRow(string? header, IReadOnlyList<LibraryItemVm> cells)
    {
        Header = header;
        Cells = cells;
    }

    public string? Header { get; }
    public bool HasHeader => Header != null;
    public IReadOnlyList<LibraryItemVm> Cells { get; }
}

public sealed partial class FolderChip : ObservableObject
{
    public FolderChip(string? name, string label)
    {
        Name = name;
        Label = label;
    }

    /// <summary>Null is "every folder".</summary>
    public string? Name { get; }
    public string Label { get; }
    [ObservableProperty] private bool _isOn;
}

/// <summary>
/// Everything in the computer's output folder, newest first, a page at a time as the grid scrolls.
/// Pictures and videos from every tab of the desktop are here, not only what this phone made.
/// </summary>
public sealed partial class LibraryViewModel : ObservableObject
{
    private const int PageSize = 90;
    private int _columns = 3;

    private readonly List<LibraryItemVm> _items = new();
    private readonly Action<IReadOnlyList<ViewerEntry>, int> _openViewer;
    private int _total;
    private int _generation;
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;
    private bool _stale;

    public LibraryViewModel(Action<IReadOnlyList<ViewerEntry>, int> openViewer)
    {
        _openViewer = openViewer;
        Folders.Add(new FolderChip(null, "All folders") { IsOn = true });
        AppServices.Jobs.Changed += () =>
        {
            // Something just finished on the computer: the next look at the library should show it.
            if (AppServices.Jobs.Jobs.Any(j => j.IsDone && j.Finished > _loadedAt)) _stale = true;
        };
    }

    public ObservableCollection<LibraryRow> Rows { get; } = new();

    /// <summary>
    /// Tiles per row: 3 on a phone; the iPad sets it from the grid's width, so a rotation or a Split
    /// View resize regroups the rows it already has instead of fetching again.
    /// </summary>
    public int Columns
    {
        get => _columns;
        set
        {
            value = Math.Clamp(value, 2, 12);
            if (value == _columns) return;
            _columns = value;
            OnPropertyChanged();
            if (_items.Count > 0) RebuildRows(0);
        }
    }
    public ObservableCollection<FolderChip> Folders { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAll), nameof(IsPictures), nameof(IsVideos))]
    private string _kind = "";

    [ObservableProperty] private string? _folder;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string? _problem;
    [ObservableProperty] private string _countText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private bool _isEmpty;

    public bool IsAll => Kind == "";
    public bool IsPictures => Kind == MediaKinds.Image;
    public bool IsVideos => Kind == MediaKinds.Video;
    public bool ShowEmpty => IsEmpty && !IsLoading && !IsScanning && Problem == null;

    /// <summary>Called when the page comes into view: reloads if it is old or something new was made.</summary>
    public void OnShown()
    {
        if (!AppServices.Remote.IsConfigured) return;
        if (_stale || DateTimeOffset.Now - _loadedAt > TimeSpan.FromMinutes(2) || _items.Count == 0) _ = RefreshAsync();
    }

    /// <summary>Forgets everything (a different computer was paired).</summary>
    public void Reset()
    {
        _generation++;
        _items.Clear();
        Rows.Clear();
        _total = 0;
        _loadedAt = DateTimeOffset.MinValue;
        while (Folders.Count > 1) Folders.RemoveAt(1);
        Folders[0].IsOn = true;
        Folder = null;
        Problem = null;
        IsEmpty = false;
        CountText = "";
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var generation = ++_generation;
        IsLoading = true;
        Problem = null;
        try
        {
            // The computer answers from its last scan and scans again behind it; a first scan of a big
            // network folder can take a few seconds, so ask again until it has something.
            LibraryPage page;
            var tries = 0;
            while (true)
            {
                page = await AppServices.Remote.LibraryAsync(Kind, Folder, 0, PageSize);
                if (generation != _generation) return;
                IsScanning = page.Scanning && page.Total == 0;
                if (!IsScanning || ++tries > 10) break;
                await Task.Delay(2000);
                if (generation != _generation) return;
            }

            _stale = false;
            _loadedAt = DateTimeOffset.Now;
            _items.Clear();
            _items.AddRange(page.Items.Select(i => new LibraryItemVm(i)));
            _total = page.Total;
            Problem = page.Problem;
            UpdateFolders(page.Folders);
            RebuildRows(0);
            FollowUpIfMissing(generation);
        }
        catch (RemoteException ex)
        {
            if (generation == _generation) Problem = ex.Message;
        }
        finally
        {
            if (generation == _generation)
            {
                IsLoading = false;
                IsScanning = false;
                UpdateCounts();
            }
        }
    }

    private int _followUps;

    /// <summary>
    /// Something this phone made in the last minute isn't listed yet: the computer can take a few
    /// seconds to see a new file on a network share. Look again shortly, a few times at most.
    /// </summary>
    private void FollowUpIfMissing(int generation)
    {
        if (Folder != null || Kind != "") return; // a filtered view may leave it out on purpose
        var listed = _items.Select(i => i.Dto.Id).ToHashSet();
        var recent = AppServices.Jobs.Jobs
            .Where(j => j.Finished is { } f && DateTimeOffset.Now - f < TimeSpan.FromMinutes(1))
            .SelectMany(j => j.Items)
            .Any(i => i.IsDone && i.LibraryId != null && !listed.Contains(i.LibraryId));
        if (!recent || _followUps >= 4) { _followUps = 0; return; }
        _followUps++;
        DispatcherTimer.RunOnce(() =>
        {
            if (generation == _generation && !IsLoading) _ = RefreshAsync();
        }, TimeSpan.FromSeconds(5));
    }

    /// <summary>The next page, when the grid nears its end.</summary>
    public async Task LoadMoreAsync()
    {
        if (IsLoading || _items.Count >= _total) return;
        var generation = _generation;
        IsLoading = true;
        try
        {
            var page = await AppServices.Remote.LibraryAsync(Kind, Folder, _items.Count, PageSize);
            if (generation != _generation) return;
            var known = _items.Select(i => i.Dto.Id).ToHashSet();
            var added = page.Items.Where(i => known.Add(i.Id)).Select(i => new LibraryItemVm(i)).ToList();
            var from = _items.Count;
            _items.AddRange(added);
            _total = page.Total;
            RebuildRows(from);
        }
        catch (RemoteException) { /* the next scroll asks again */ }
        finally
        {
            if (generation == _generation)
            {
                IsLoading = false;
                UpdateCounts();
            }
        }
    }

    [RelayCommand]
    private Task PickKind(string kind)
    {
        if (Kind == kind) return Task.CompletedTask;
        Kind = kind;
        return RefreshAsync();
    }

    [RelayCommand]
    private Task PickFolder(FolderChip chip)
    {
        foreach (var c in Folders) c.IsOn = c == chip;
        if (Folder == chip.Name) return Task.CompletedTask;
        Folder = chip.Name;
        return RefreshAsync();
    }

    [RelayCommand]
    private void Open(LibraryItemVm item)
    {
        var entries = _items.Select(ViewerEntry.FromLibrary).ToList();
        _openViewer(entries, _items.IndexOf(item));
    }

    private void UpdateFolders(IReadOnlyList<LibraryFolderDto> folders)
    {
        // Folder chips come from the unfiltered scan; keep the chosen one even if a filter empties it.
        var wanted = folders.Select(f => f.Name).ToList();
        if (Folder != null && !wanted.Contains(Folder)) wanted.Add(Folder);
        for (var i = Folders.Count - 1; i >= 1; i--)
            if (!wanted.Contains(Folders[i].Name!)) Folders.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
        {
            var name = wanted[i];
            var at = -1;
            for (var k = 1; k < Folders.Count; k++) if (Folders[k].Name == name) { at = k; break; }
            if (at < 0) Folders.Insert(Math.Min(i + 1, Folders.Count), new FolderChip(name, name.Length == 0 ? "Top folder" : name));
            else if (at != i + 1 && i + 1 < Folders.Count) Folders.Move(at, i + 1);
        }
        foreach (var c in Folders) c.IsOn = c.Name == Folder;
    }

    /// <summary>
    /// Regroups from the day of item <paramref name="from"/> onward. Earlier rows are left alone, so
    /// loading the next page never moves what is already on screen.
    /// </summary>
    private void RebuildRows(int from)
    {
        if (from == 0)
        {
            Rows.Clear();
        }
        else
        {
            // The last day on screen may continue into the new page: take its rows off and redo them.
            var day = _items[from - 1].LocalDay;
            while (Rows.Count > 0 && Rows[^1].Cells[0].LocalDay == day) Rows.RemoveAt(Rows.Count - 1);
            while (from > 0 && _items[from - 1].LocalDay == day) from--;
        }

        var i = from;
        while (i < _items.Count)
        {
            var day = _items[i].LocalDay;
            var first = true;
            while (i < _items.Count && _items[i].LocalDay == day)
            {
                var cells = new List<LibraryItemVm>(_columns);
                while (cells.Count < _columns && i < _items.Count && _items[i].LocalDay == day) cells.Add(_items[i++]);
                Rows.Add(new LibraryRow(first ? DayLabel(day) : null, cells));
                first = false;
            }
        }
    }

    private void UpdateCounts()
    {
        IsEmpty = _items.Count == 0;
        var noun = Kind switch { MediaKinds.Image => "picture", MediaKinds.Video => "video", _ => "item" };
        CountText = _total == 0 ? "" : $"{_total.ToString("N0", CultureInfo.CurrentCulture)} {noun}{(_total == 1 ? "" : "s")}";
        OnPropertyChanged(nameof(ShowEmpty));
    }

    public static string DayLabel(DateTime day)
    {
        var today = DateTime.Today;
        if (day == today) return "Today";
        if (day == today.AddDays(-1)) return "Yesterday";
        if (day > today.AddDays(-7)) return day.ToString("dddd", CultureInfo.CurrentCulture);
        return day.Year == today.Year
            ? day.ToString("d MMMM", CultureInfo.CurrentCulture)
            : day.ToString("d MMMM yyyy", CultureInfo.CurrentCulture);
    }
}
