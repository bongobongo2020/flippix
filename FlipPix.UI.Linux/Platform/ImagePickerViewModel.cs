using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels;

public class ImagePickerItem : INotifyPropertyChanged
{
    private Bitmap? _thumbnail;
    private bool _isSelected;

    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public bool IsDirectory { get; init; }
    public bool IsVideo { get; init; }

    /// <summary>Shown while there is no thumbnail.</summary>
    public string PlaceholderGlyph => IsVideo ? "🎬" : "🖼";

    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; OnPropertyChanged(); }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    // Thumbnail bookkeeping, touched only on the UI thread.
    // Rows can be re-flowed while realized, so this counts the shown rows holding the item.
    internal int ShownCount;
    internal bool IsOnScreen => ShownCount > 0;
    internal bool IsQueued;
    internal bool IsReading;
    internal bool CacheChecked;
    internal bool ThumbFailed;
    internal bool IsCurrent;

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One line of the grid. The grid is a virtualized list of rows so that a folder of
/// thousands of images only builds the controls for the rows on screen.</summary>
public class ImagePickerRow
{
    public IReadOnlyList<ImagePickerItem> Cells { get; init; } = Array.Empty<ImagePickerItem>();
}

/// <summary>
/// Backs the image browse dialog. Folders shared from ChromeOS (/mnt/chromeos/...) are reached
/// over a VM file-sharing channel where every open and stat costs a round trip of a second or
/// more, so nothing here touches the filesystem on the UI thread: the listing runs in the
/// background, thumbnails are fetched only for rows on screen, several at a time, and each one
/// is kept in ~/.cache/FlipPix/thumbnails so the folder opens instantly the next time.
/// </summary>
public class ImagePickerViewModel : INotifyPropertyChanged, IDisposable
{
    /// <summary>List videos as well as images (the folder browser; the image picker doesn't).</summary>
    public bool IncludeVideos { get; init; }

    private string EmptyText => IncludeVideos ? "No pictures, videos or folders here." : "No images or folders here.";

    /// <summary>A file to select and scroll to once the folder is shown ("show in folder").</summary>
    public string? RevealPath { get; set; }

    /// <summary>Asks the view to scroll the item at this index into view.</summary>
    public event EventHandler<int>? ScrollToItemRequested;

    /// <summary>Source files read at once. The share is latency-bound, so overlapping reads is
    /// what makes it fast; more than this only splits the bandwidth.</summary>
    private const int MaxConcurrentReads = 6;

    private string _currentPath = "";
    private ImagePickerItem? _selectedItem;
    private string _selectedPath = "";
    private bool _isRefreshing;
    private string _statusText = "";
    private int _columns = 6;
    private IReadOnlyList<ImagePickerItem> _items = Array.Empty<ImagePickerItem>();
    private IReadOnlyList<ImagePickerRow> _rows = Array.Empty<ImagePickerRow>();
    private CancellationTokenSource? _loadCts;

    // Pending thumbnail work, UI thread only. Generation is LIFO so the rows that most recently
    // scrolled into view are served first; checks of cached thumbnails against the source run
    // only when nothing is waiting to be generated.
    private readonly List<ImagePickerItem> _generateQueue = new();
    private readonly Queue<(ImagePickerItem Item, ThumbMeta Meta)> _validateQueue = new();
    private int _activeReads;

    public IReadOnlyList<ImagePickerItem> Items
    {
        get => _items;
        private set { _items = value; OnPropertyChanged(); }
    }

    public IReadOnlyList<ImagePickerRow> Rows
    {
        get => _rows;
        private set { _rows = value; OnPropertyChanged(); }
    }

    public string CurrentPath
    {
        get => _currentPath;
        set
        {
            if (_currentPath == value) return;
            _currentPath = value;
            OnPropertyChanged();
            LoadDirectory(value, fallbackToHome: false);
        }
    }

    public ImagePickerItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (_selectedItem != null) _selectedItem.IsSelected = false;
            _selectedItem = value;
            if (_selectedItem != null) _selectedItem.IsSelected = true;
            if (_selectedItem?.IsDirectory == false)
                SelectedPath = _selectedItem.FullPath;
            OnPropertyChanged();
        }
    }

    public string SelectedPath
    {
        get => _selectedPath;
        set { _selectedPath = value; OnPropertyChanged(); }
    }

    /// <summary>True while a saved listing is on screen and the live one is still being read.</summary>
    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set { _isRefreshing = value; OnPropertyChanged(); }
    }

    /// <summary>Shown over the grid while it is loading or empty; blank otherwise.</summary>
    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; OnPropertyChanged(); }
    }

    /// <summary>Tiles per row, set by the window from its width.</summary>
    public int Columns
    {
        get => _columns;
        set
        {
            value = Math.Max(1, value);
            if (_columns == value) return;
            _columns = value;
            RebuildRows();
        }
    }

    public bool CanGoUp => !string.IsNullOrEmpty(Directory.GetParent(_currentPath)?.FullName);

    public RelayCommand GoUpCommand { get; }

    public ImagePickerViewModel()
    {
        GoUpCommand = new RelayCommand(GoUp, () => CanGoUp);
    }

    public void Initialize(string? startDirectory)
    {
        // Directory.Exists on a share is itself a slow round trip, so the start folder is not
        // checked here; LoadDirectory falls back to the home folder if it has gone.
        var dir = string.IsNullOrEmpty(startDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : startDirectory;
        _currentPath = dir;
        OnPropertyChanged(nameof(CurrentPath));
        LoadDirectory(dir, fallbackToHome: true);
    }

    private void GoUp()
    {
        var parent = Directory.GetParent(_currentPath)?.FullName;
        if (!string.IsNullOrEmpty(parent))
            CurrentPath = parent;
    }

    public void NavigateTo(ImagePickerItem item)
    {
        if (item.IsDirectory)
            CurrentPath = item.FullPath;
        else
            SelectedItem = item;
    }

    /// <summary>
    /// Shows <paramref name="path"/>. A folder opened before appears at once from the saved
    /// listing while the live one is read in the background (a share can take many seconds just
    /// to list), then the grid is updated in place if anything changed. With
    /// <paramref name="fallbackToHome"/>, a folder that no longer exists opens the home folder.
    /// </summary>
    public async void LoadDirectory(string path, bool fallbackToHome)
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        foreach (var old in _items) old.IsCurrent = false;
        _generateQueue.Clear();
        _validateQueue.Clear();
        SelectedItem = null;
        Items = Array.Empty<ImagePickerItem>();
        Rows = Array.Empty<ImagePickerRow>();
        GoUpCommand.NotifyCanExecuteChanged();
        IsRefreshing = false;
        StatusText = "Loading folder…";

        var cached = await Task.Run(() => ListingCache.TryLoad(path));
        if (ct.IsCancellationRequested) return;
        if (cached != null)
        {
            ShowEntries(path, cached);
            StatusText = _items.Count == 0 ? EmptyText : "";
            IsRefreshing = true;
        }
        FolderOpened?.Invoke(this, EventArgs.Empty);

        List<DirEntry> entries;
        try
        {
            entries = await RunBlocking(() => ListDirectory(path, ct));
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            IsRefreshing = false;
            if (ex is DirectoryNotFoundException) ListingCache.Forget(path);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (fallbackToHome && ex is DirectoryNotFoundException && path != home)
            {
                CurrentPath = home;
                return;
            }
            if (cached == null || ex is DirectoryNotFoundException)
            {
                foreach (var old in _items) old.IsCurrent = false;
                Items = Array.Empty<ImagePickerItem>();
                Rows = Array.Empty<ImagePickerRow>();
                StatusText = ex is DirectoryNotFoundException
                    ? "This folder doesn't exist."
                    : "This folder can't be opened.";
            }
            return;
        }
        if (ct.IsCancellationRequested) return;

        IsRefreshing = false;
        if (cached == null || !cached.SequenceEqual(entries))
        {
            ShowEntries(path, entries);
            _ = Task.Run(() => ListingCache.Save(path, entries));
        }
        StatusText = _items.Count == 0 ? EmptyText : "";
    }

    /// <summary>Raised when a newly opened folder is first shown, so the view scrolls to the top.
    /// A later in-place refresh of the same folder doesn't raise it.</summary>
    public event EventHandler? FolderOpened;

    /// <summary>
    /// Puts <paramref name="entries"/> in the grid. Items already shown for the same file are
    /// kept, with their thumbnails, so a refresh after the saved listing doesn't flicker.
    /// </summary>
    private void ShowEntries(string path, IReadOnlyList<DirEntry> entries)
    {
        var existing = new Dictionary<string, ImagePickerItem>(StringComparer.Ordinal);
        foreach (var item in _items)
            existing[item.FullPath] = item;

        var items = new List<ImagePickerItem>(entries.Count);
        foreach (var entry in entries)
        {
            var fullPath = Path.Combine(path, entry.Name);
            var isVideo = !entry.IsDirectory && InAppViewers.IsVideo(entry.Name);
            if (isVideo && !IncludeVideos) continue;
            if (existing.Remove(fullPath, out var keep) && keep.IsDirectory == entry.IsDirectory)
                items.Add(keep);
            else
                items.Add(new ImagePickerItem { Name = entry.Name, FullPath = fullPath, IsDirectory = entry.IsDirectory, IsVideo = isVideo, IsCurrent = true });
        }
        foreach (var gone in existing.Values)
        {
            gone.IsCurrent = false;
            if (ReferenceEquals(gone, _selectedItem)) SelectedItem = null;
        }

        Items = items;
        RebuildRows();

        if (RevealPath != null)
        {
            var index = items.FindIndex(i => string.Equals(i.FullPath, RevealPath, StringComparison.Ordinal));
            if (index >= 0)
            {
                RevealPath = null;
                SelectedItem = items[index];
                ScrollToItemRequested?.Invoke(this, index);
            }
        }
    }

    /// <summary>
    /// One pass over the directory, sorted folders first. GetDirectories + GetFiles walked it
    /// twice. Throws if the folder can't be read.
    /// </summary>
    private static List<DirEntry> ListDirectory(string path, CancellationToken ct)
    {
        var dirs = new List<DirEntry>();
        var files = new List<DirEntry>();
        // The raw enumerable reads only the names and the directory flag readdir already returns;
        // EnumerateFileSystemInfos went back to the share for every entry and took minutes.
        // AttributesToSkip = 0 for the same reason: testing attributes would stat every entry.
        // Dotfiles are left out by name instead, as in the system file chooser.
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        var raw = new FileSystemEnumerable<DirEntry>(
            path,
            (ref FileSystemEntry e) => new DirEntry(e.FileName.ToString(), e.IsDirectory),
            options);
        foreach (var entry in raw)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Name.StartsWith('.')) continue;
            if (entry.IsDirectory)
                dirs.Add(entry);
            // Videos are listed (and cached) too; ShowEntries leaves them out of the image picker.
            else if (InAppViewers.IsImage(entry.Name) || InAppViewers.IsVideo(entry.Name))
                files.Add(entry);
        }

        dirs.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        dirs.AddRange(files);
        return dirs;
    }

    private void RebuildRows()
    {
        var rows = new List<ImagePickerRow>((_items.Count + _columns - 1) / _columns);
        for (int i = 0; i < _items.Count; i += _columns)
            rows.Add(new ImagePickerRow { Cells = _items.Skip(i).Take(_columns).ToArray() });
        Rows = rows;
    }

    // ── Thumbnails ────────────────────────────────────────────────────────────────────────

    /// <summary>The window calls this when a row is realized on screen.</summary>
    public void RowShown(ImagePickerRow row)
    {
        foreach (var item in row.Cells)
        {
            item.ShownCount++;
            if (item.IsDirectory || !item.IsCurrent || item.ThumbFailed) continue;
            if (item.Thumbnail != null || item.IsReading) continue;

            if (!item.CacheChecked)
            {
                item.CacheChecked = true;
                _ = LoadCachedAsync(item);
            }
            else
            {
                Enqueue(item);
            }
        }
        Pump();
    }

    /// <summary>The window calls this when a row scrolls out and its controls are recycled.</summary>
    public void RowHidden(ImagePickerRow row)
    {
        foreach (var item in row.Cells)
            item.ShownCount = Math.Max(0, item.ShownCount - 1);
    }

    private void Enqueue(ImagePickerItem item)
    {
        if (item.IsQueued)
            _generateQueue.Remove(item);
        item.IsQueued = true;
        _generateQueue.Add(item);
    }

    /// <summary>
    /// Shows the cached thumbnail if there is one (a local read, so instant), then queues a
    /// check that the source hasn't changed. Without a cached copy the source is queued.
    /// </summary>
    private async Task LoadCachedAsync(ImagePickerItem item)
    {
        var cached = await Task.Run(() => ThumbnailCache.TryLoad(item.FullPath));
        if (!item.IsCurrent) return;

        if (cached != null)
        {
            item.Thumbnail = cached.Value.Bitmap;
            _validateQueue.Enqueue((item, cached.Value.Meta));
        }
        else if (item.IsOnScreen)
        {
            Enqueue(item);
        }
        // Otherwise it scrolled away before the cache answered; the next RowShown queues it.
        Pump();
    }

    private void Pump()
    {
        while (_activeReads < MaxConcurrentReads)
        {
            ImagePickerItem? next = null;
            while (_generateQueue.Count > 0)
            {
                var candidate = _generateQueue[^1];
                _generateQueue.RemoveAt(_generateQueue.Count - 1);
                candidate.IsQueued = false;
                if (candidate.IsCurrent && candidate.IsOnScreen && candidate.Thumbnail == null && !candidate.IsReading)
                {
                    next = candidate;
                    break;
                }
            }

            if (next != null)
            {
                _ = GenerateAsync(next, null);
                continue;
            }

            if (_validateQueue.Count == 0) return;
            var (item, meta) = _validateQueue.Dequeue();
            if (item.IsCurrent && !item.IsReading)
                _ = GenerateAsync(item, meta);
        }
    }

    /// <summary>Reads the source and makes its thumbnail. With <paramref name="cachedMeta"/>,
    /// only re-reads the image if the file differs from when it was cached.</summary>
    private async Task GenerateAsync(ImagePickerItem item, ThumbMeta? cachedMeta)
    {
        _activeReads++;
        item.IsReading = true;
        try
        {
            var bmp = await RunBlocking(() => ThumbnailCache.Create(item.FullPath, cachedMeta, () => !item.IsCurrent));
            if (bmp != null && item.IsCurrent)
                item.Thumbnail = bmp;
        }
        catch
        {
            // Unreadable or not an image Skia can decode: keep the placeholder, don't retry.
            item.ThumbFailed = true;
        }
        finally
        {
            item.IsReading = false;
            _activeReads--;
            Pump();
        }
    }

    /// <summary>
    /// Runs a read of the (possibly very slow) source folder on its own thread. On the shared
    /// pool, reads stalled for minutes would hold the few pool threads and hold up the quick
    /// local work, such as showing thumbnails that are already cached.
    /// </summary>
    private static Task<T> RunBlocking<T>(Func<T> work)
        => Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public void Dispose()
    {
        _loadCts?.Cancel();
        foreach (var item in _items) item.IsCurrent = false;
        _generateQueue.Clear();
        _validateQueue.Clear();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A folder or image file in a listing.</summary>
internal readonly record struct DirEntry(string Name, bool IsDirectory);

/// <summary>
/// The last listing seen for each folder, in memory and under ~/.cache/FlipPix/listings, so a
/// folder opens instantly while its live listing is read again.
/// </summary>
internal static class ListingCache
{
    private const string Header = "flippix-listing-1";
    private static readonly Dictionary<string, List<DirEntry>> Memory = new(StringComparer.Ordinal);

    private static string FileFor(string folder)
        => Path.Combine(Directory.CreateDirectory(Path.Combine(UserPaths.CacheDir, "listings")).FullName,
                        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(folder))) + ".txt");

    public static List<DirEntry>? TryLoad(string folder)
    {
        lock (Memory)
            if (Memory.TryGetValue(folder, out var hit)) return hit;
        try
        {
            var file = FileFor(folder);
            if (!File.Exists(file)) return null;
            var lines = File.ReadAllLines(file);
            if (lines.Length < 2 || lines[0] != Header || lines[1] != folder) return null;
            var entries = new List<DirEntry>(lines.Length - 2);
            for (int i = 2; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Length < 3 || line[1] != '\t' || (line[0] != 'd' && line[0] != 'f')) return null;
                entries.Add(new DirEntry(line[2..], line[0] == 'd'));
            }
            lock (Memory) Memory[folder] = entries;
            return entries;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string folder, List<DirEntry> entries)
    {
        lock (Memory) Memory[folder] = entries;
        // A name with a line break can't be written one per line; such a folder just isn't saved.
        if (folder.Contains('\n') || entries.Any(e => e.Name.Contains('\n') || e.Name.Contains('\r'))) return;
        try
        {
            var file = FileFor(folder);
            var tmp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllLines(tmp, new[] { Header, folder }
                .Concat(entries.Select(e => (e.IsDirectory ? "d\t" : "f\t") + e.Name)));
            File.Move(tmp, file, overwrite: true);
        }
        catch
        {
            // The cache is an optimisation; without it the folder is listed live as before.
        }
    }

    public static void Forget(string folder)
    {
        lock (Memory) Memory.Remove(folder);
        try { File.Delete(FileFor(folder)); } catch { }
    }
}

/// <summary>Size and modified time of a source image when its thumbnail was made.</summary>
internal readonly record struct ThumbMeta(long Length, long WriteTicks);

/// <summary>
/// Thumbnails on local disk under ~/.cache/FlipPix/thumbnails, one PNG per source path plus a
/// sidecar holding the source's size and modified time so a changed file is re-made.
/// </summary>
internal static class ThumbnailCache
{
    private const int ThumbWidth = 200;

    private static string Dir => Directory.CreateDirectory(Path.Combine(UserPaths.CacheDir, "thumbnails")).FullName;

    private static string Key(string sourcePath)
        => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(sourcePath)));

    public static (Bitmap Bitmap, ThumbMeta Meta)? TryLoad(string sourcePath)
    {
        try
        {
            var basePath = Path.Combine(Dir, Key(sourcePath));
            var metaPath = basePath + ".meta";
            var pngPath = basePath + ".png";
            if (!File.Exists(metaPath) || !File.Exists(pngPath)) return null;

            var parts = File.ReadAllText(metaPath).Split('|');
            if (parts.Length != 2
                || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var length)
                || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
                return null;

            using var stream = File.OpenRead(pngPath);
            return (new Bitmap(stream), new ThumbMeta(length, ticks));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads <paramref name="sourcePath"/> and returns a new thumbnail, saving it to the cache.
    /// Returns null if the file matches <paramref name="cachedMeta"/> (the cached one stands),
    /// if it can't be decoded, or if <paramref name="abandoned"/> turns true mid-read.
    /// </summary>
    public static Bitmap? Create(string sourcePath, ThumbMeta? cachedMeta, Func<bool> abandoned)
    {
        if (InAppViewers.IsVideo(sourcePath))
            return CreateForVideo(sourcePath, cachedMeta);

        byte[] data;
        ThumbMeta meta;
        // bufferSize 1 = unbuffered: FileStream's default 4 KB buffer would split the read into
        // hundreds of round trips on a share. One open + fstat, then reads as large as the file.
        using (var fs = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1))
        {
            meta = new ThumbMeta(fs.Length, File.GetLastWriteTimeUtc(fs.SafeFileHandle).Ticks);
            if (cachedMeta == meta) return null;

            data = new byte[meta.Length];
            int read = 0;
            while (read < data.Length)
            {
                if (abandoned()) return null;
                int n = fs.Read(data, read, Math.Min(4 * 1024 * 1024, data.Length - read));
                if (n == 0) break;
                read += n;
            }
            if (read < data.Length) Array.Resize(ref data, read);
        }

        Bitmap bmp;
        using (var ms = new MemoryStream(data, writable: false))
            bmp = Bitmap.DecodeToWidth(ms, ThumbWidth, BitmapInterpolationMode.MediumQuality);

        Save(sourcePath, bmp, meta);
        return bmp;
    }

    /// <summary>A frame from early in the video, via ffmpeg, which reads only the start of the file.
    /// Null if ffmpeg isn't installed or can't decode it.</summary>
    private static Bitmap? CreateForVideo(string sourcePath, ThumbMeta? cachedMeta)
    {
        var info = new FileInfo(sourcePath);
        var meta = new ThumbMeta(info.Length, info.LastWriteTimeUtc.Ticks);
        if (cachedMeta == meta) return null;

        var ffmpeg = MediaTools.FFmpegPath;
        if (ffmpeg == null) throw new InvalidOperationException("ffmpeg is not installed");

        // A second in skips a fade from black; a clip shorter than that gets its first frame.
        var png = ExtractFrame(ffmpeg, sourcePath, "1") ?? ExtractFrame(ffmpeg, sourcePath, "0")
                  ?? throw new InvalidDataException("ffmpeg produced no frame");
        Bitmap bmp;
        using (var ms = new MemoryStream(png, writable: false))
            bmp = new Bitmap(ms);
        Save(sourcePath, bmp, meta);
        return bmp;
    }

    private static byte[]? ExtractFrame(string ffmpeg, string sourcePath, string seekSeconds)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-v", "error", "-ss", seekSeconds, "-i", sourcePath, "-frames:v", "1",
                     "-vf", $"scale={ThumbWidth}:-2", "-f", "image2pipe", "-vcodec", "png", "pipe:1" })
            psi.ArgumentList.Add(arg);

        using var process = System.Diagnostics.Process.Start(psi);
        if (process == null) return null;
        process.ErrorDataReceived += (_, _) => { }; // drain stderr so ffmpeg can't block on it
        process.BeginErrorReadLine();
        using var output = new MemoryStream();
        // Bounded: on a stalled share ffmpeg can wait on the file indefinitely.
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
        if (!copy.Wait(120_000) || !process.WaitForExit(10_000))
        {
            try { process.Kill(); } catch { }
            return null;
        }
        return output.Length > 0 ? output.ToArray() : null;
    }

    private static void Save(string sourcePath, Bitmap bmp, ThumbMeta meta)
    {
        try
        {
            var basePath = Path.Combine(Dir, Key(sourcePath));
            var tmp = basePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            bmp.Save(tmp);
            File.Move(tmp, basePath + ".png", overwrite: true);
            File.WriteAllText(basePath + ".meta",
                string.Create(CultureInfo.InvariantCulture, $"{meta.Length}|{meta.WriteTicks}"));
        }
        catch
        {
            // The cache is an optimisation; a failed write just means re-reading next time.
        }
    }
}
