using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using FlipPix.Mobile.Services;
using FlipPix.Remote.Contracts;

namespace FlipPix.Mobile.ViewModels;

/// <summary>
/// A job on the computer, as the phone shows it. Updated in place from each <see cref="JobDto"/> so a
/// card never flickers or loses its scroll position when progress arrives.
/// </summary>
public partial class JobVm : ObservableObject
{
    public JobVm(JobDto dto)
    {
        Id = dto.Id;
        Kind = dto.Kind;
        Request = dto.Request;
        Update(dto);
    }

    public string Id { get; }
    public string Kind { get; }
    public JobRequest Request { get; }
    public ObservableCollection<JobItemVm> Items { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive), nameof(IsQueued), nameof(IsRunning), nameof(IsDone), nameof(IsFailed),
        nameof(IsStopped), nameof(IsProblem), nameof(ShowPlay), nameof(ShowRetry), nameof(CanRemove))]
    private string _state = JobStates.Queued;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private double _ratio = 1;
    [ObservableProperty] private string? _script;
    [ObservableProperty] private string _elapsed = "";
    [ObservableProperty] private bool _showScript;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRetry))]
    private bool _canRetry;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private JobItemVm? _selected;

    private string? _posterUrl;
    private Bitmap? _poster;
    private bool _posterRequested;

    public DateTimeOffset Created { get; private set; }
    public DateTimeOffset? Started { get; private set; }
    public DateTimeOffset? Finished { get; private set; }

    public bool IsActive => JobStates.IsActive(State);
    public bool IsQueued => State == JobStates.Queued;
    public bool IsRunning => State == JobStates.Running;
    public bool IsDone => State == JobStates.Done;
    public bool IsFailed => State == JobStates.Failed;
    public bool IsStopped => State == JobStates.Stopped;
    public bool IsProblem => IsFailed || IsStopped;
    public bool HasSelection => Selected != null;
    public bool HasItems => Items.Count > 0;
    public int DoneCount => Items.Count(i => i.IsDone);
    /// <summary>Finished shots can be watched while the rest are still being made.</summary>
    public bool ShowPlay => DoneCount > 0;
    public bool ShowRetry => CanRetry && !IsActive;
    public bool CanRemove => !IsActive;

    public string LookName => Looks.NameOf(Request.Look);

    public string LengthLabel => Kind switch
    {
        JobKinds.Video => $"{Request.Seconds} s",
        JobKinds.Story => $"{Request.Clips} shots · {FormatLength(Request.Clips * 10)}",
        _ => Request.Count == 1 ? "1 picture" : $"{Request.Count} pictures",
    };

    /// <summary>The first reference photo, shown behind a card until something is rendered.</summary>
    public Bitmap? Poster
    {
        get
        {
            if (!_posterRequested && _posterUrl != null)
            {
                _posterRequested = true;
                _ = LoadPosterAsync(_posterUrl);
            }
            return _poster;
        }
    }

    private int _posterTries;

    private async Task LoadPosterAsync(string url)
    {
        var bitmap = await ImageLoader.LoadAsync(url, 720);
        if (url != _posterUrl) return;
        if (bitmap == null)
        {
            if (++_posterTries > 4) return;
            await Task.Delay(TimeSpan.FromSeconds(3 * _posterTries));
            if (url != _posterUrl || _poster != null) return;
            _posterRequested = false;
            OnPropertyChanged(nameof(Poster));
            return;
        }
        _poster = bitmap;
        OnPropertyChanged(nameof(Poster));
    }

    public void Update(JobDto dto)
    {
        State = dto.State;
        Title = dto.Title;
        Status = dto.Status;
        Progress = dto.Progress;
        Ratio = dto.Ratio > 0 ? dto.Ratio : 1;
        Script = dto.Script;
        CanRetry = dto.CanRetry;
        Created = dto.Created;
        Started = dto.Started;
        Finished = dto.Finished;

        if (dto.PosterUrl != _posterUrl)
        {
            _posterUrl = dto.PosterUrl;
            _posterRequested = false;
            OnPropertyChanged(nameof(Poster));
        }

        for (var i = 0; i < dto.Items.Count; i++)
        {
            var d = dto.Items[i];
            var vm = Items.FirstOrDefault(x => x.Index == d.Index);
            if (vm == null) Items.Insert(Math.Min(i, Items.Count), vm = new JobItemVm(this, d.Index));
            vm.Update(d);
        }
        foreach (var gone in Items.Where(x => dto.Items.All(d => d.Index != x.Index)).ToList()) Items.Remove(gone);

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(DoneCount));
        OnPropertyChanged(nameof(ShowPlay));
        Tick();
    }

    /// <summary>The clock on a working card, "1:24"; the finished status says how long it took.</summary>
    public void Tick()
    {
        Elapsed = IsRunning && Started is { } s ? Clock(DateTimeOffset.Now - s) : "";
    }

    public static string Clock(TimeSpan t) =>
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}:{t.Seconds:00}" : $"{Math.Max(0, t.Seconds)} s";

    private static string FormatLength(int seconds) =>
        seconds >= 60 ? (seconds % 60 == 0 ? $"{seconds / 60} min" : $"{seconds / 60}:{seconds % 60:00}") : $"{seconds} s";
}

/// <summary>One picture, video or story shot inside a job.</summary>
public partial class JobItemVm : ObservableObject
{
    public JobItemVm(JobVm job, int index)
    {
        Job = job;
        Index = index;
    }

    public JobVm Job { get; }
    public int Index { get; }
    public int Number => Index + 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDone), nameof(IsFailed), nameof(IsWorking), nameof(IsWaiting), nameof(IsPending))]
    private string _state = ItemStates.Waiting;

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string? _label;
    [ObservableProperty] private string? _text;
    [ObservableProperty] private bool _isSelected;

    public string MediaKind { get; private set; } = MediaKinds.Image;
    public string? FileUrl { get; private set; }
    public string? ThumbUrl { get; private set; }
    public string? PreviewUrl { get; private set; }
    public string? LibraryId { get; private set; }

    public bool IsDone => State == ItemStates.Done;
    public bool IsFailed => State == ItemStates.Failed;
    public bool IsWorking => State == ItemStates.Working;
    public bool IsWaiting => State == ItemStates.Waiting;
    public bool IsPending => IsWaiting || IsWorking;
    public bool IsVideo => MediaKind == MediaKinds.Video;

    private Bitmap? _picture;
    private string? _pictureFor;

    /// <summary>
    /// The finished picture (or a video's first frame), fetched the first time anything shows it.
    /// A picture tile is half the screen wide, so it gets the preview rather than the small thumbnail.
    /// </summary>
    public Bitmap? Picture
    {
        get
        {
            var url = MediaKind == MediaKinds.Image ? PreviewUrl ?? ThumbUrl : ThumbUrl;
            if (url != null && url != _pictureFor)
            {
                _pictureFor = url;
                _ = LoadPictureAsync(url, MediaKind == MediaKinds.Image ? 720 : 360);
            }
            return _picture;
        }
    }

    private int _pictureTries;

    private async Task LoadPictureAsync(string url, int width)
    {
        var bitmap = await ImageLoader.LoadAsync(url, width);
        if (url != _pictureFor) return;
        if (bitmap == null)
        {
            // Asked the moment the item finished, the file can still be being written. Ask again shortly.
            if (++_pictureTries > 4) return;
            await Task.Delay(TimeSpan.FromSeconds(3 * _pictureTries));
            if (url != _pictureFor || _picture != null) return;
            _pictureFor = null;
            OnPropertyChanged(nameof(Picture));
            return;
        }
        _picture = bitmap;
        OnPropertyChanged(nameof(Picture));
    }

    public void Update(JobItemDto d)
    {
        State = d.State;
        Status = d.Status;
        Progress = d.Progress;
        Label = d.Label;
        Text = d.Text;
        MediaKind = d.MediaKind;
        var changed = FileUrl != d.FileUrl || ThumbUrl != d.ThumbUrl;
        FileUrl = d.FileUrl;
        ThumbUrl = d.ThumbUrl;
        PreviewUrl = d.PreviewUrl;
        LibraryId = d.LibraryId;
        if (changed) OnPropertyChanged(nameof(Picture));
    }
}
