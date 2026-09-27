using System.Text;
using FlipPix.Remote.Contracts;

namespace FlipPix.Remote.Library;

/// <summary>One picture or video in the output folder, as the phone's library shows it.</summary>
public sealed record LibraryEntry(
    string Id,
    string RelativePath,
    string FullPath,
    string Name,
    string Folder,
    string Kind,
    long Size,
    DateTime ModifiedUtc,
    /// <summary>For a video: the first-frame PNG Video Helper Suite writes beside it, when there is one.</summary>
    string? PosterPath);

/// <summary>
/// Everything the output folder holds, newest first, read in the background.
///
/// <para>The output folder is usually a network share (Z:\output here) and a full walk of ~2,500 files
/// took 9 s over SMB, so requests never wait for a scan: they get the last one, and a stale index
/// starts a new scan behind them. <see cref="Scanning"/> tells the phone to look again shortly.</para>
///
/// <para>Video Helper Suite writes three files per video: <c>x.mp4</c> (silent), <c>x-audio.mp4</c> (with
/// the sound, when there is any) and <c>x.png</c> (the first frame, carrying the workflow). They are one
/// thing to a person, so they become one entry: the audio version when it exists, with the PNG as its
/// poster and not as a picture of its own.</para>
/// </summary>
public sealed class LibraryIndex
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);
    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp" };
    private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".webm", ".mov", ".mkv" };

    private readonly Func<string> _root;
    private readonly object _lock = new();
    private IReadOnlyList<LibraryEntry> _entries = Array.Empty<LibraryEntry>();
    private Dictionary<string, LibraryEntry> _byId = new();
    private string _scannedRoot = "";
    private DateTime _scannedAt = DateTime.MinValue;
    private Task? _scan;
    private string? _problem;
    // Files added by Include, kept so a scan that began before they existed doesn't drop them.
    private readonly List<LibraryEntry> _included = new();
    private bool _invalidatedDuringScan;

    /// <param name="root">The output folder as the desktop's settings resolve it right now.</param>
    public LibraryIndex(Func<string> root) => _root = root;

    /// <summary>Raised on the scanning thread with the newest entries after each scan.</summary>
    public event Action<IReadOnlyList<LibraryEntry>>? Scanned;

    public bool Scanning { get { lock (_lock) return _scan != null; } }
    public string CurrentRoot => _root();

    /// <summary>Forget the age of the last scan, so the next request looks again (a job just saved something).</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _scannedAt = DateTime.MinValue;
            if (_scan != null) _invalidatedDuringScan = true;
        }
    }

    /// <summary>Starts a scan when the index is stale or the folder setting changed. Never waits for it.</summary>
    public void EnsureFresh()
    {
        var root = _root();
        lock (_lock)
        {
            if (_scan != null) return;
            if (root == _scannedRoot && DateTime.UtcNow - _scannedAt < StaleAfter) return;
            _invalidatedDuringScan = false;
            _scan = Task.Run(() => ScanInto(root));
        }
    }

    /// <summary>Waits for a running scan, at most <paramref name="wait"/>. For the first request after startup.</summary>
    public async Task WaitForScanAsync(TimeSpan wait)
    {
        Task? scan;
        lock (_lock) scan = _scan;
        if (scan != null) await Task.WhenAny(scan, Task.Delay(wait));
    }

    private void ScanInto(string root)
    {
        IReadOnlyList<LibraryEntry> entries = Array.Empty<LibraryEntry>();
        string? problem = null;
        try
        {
            if (string.IsNullOrWhiteSpace(root))
                problem = "The desktop has no output folder in its Settings.";
            else if (!Directory.Exists(root))
                problem = $"The desktop can't reach its output folder ({root}).";
            else
                entries = Scan(root);
        }
        catch (Exception ex)
        {
            problem = "The output folder couldn't be read: " + ex.Message;
        }

        lock (_lock)
        {
            // A failed rescan of the same folder keeps what was already known rather than emptying the library.
            if (problem == null || root != _scannedRoot || _entries.Count == 0)
            {
                var ids = entries.Select(e => e.Id).ToHashSet();
                var late = _included.Where(e => !ids.Contains(e.Id) && File.Exists(e.FullPath)).ToList();
                if (late.Count > 0)
                {
                    var merged = late.Concat(entries).ToList();
                    merged.Sort((a, b) => b.ModifiedUtc.CompareTo(a.ModifiedUtc));
                    entries = merged;
                }
                _entries = entries;
                _byId = entries.ToDictionary(e => e.Id);
            }
            _problem = problem;
            _scannedRoot = root;
            // Something was saved while this scan ran: the next request looks again.
            _scannedAt = _invalidatedDuringScan ? DateTime.MinValue : DateTime.UtcNow;
            _invalidatedDuringScan = false;
            _scan = null;
            entries = _entries;
        }
        try { Scanned?.Invoke(entries); } catch (Exception) { /* a listener's problem */ }
    }

    internal static IReadOnlyList<LibraryEntry> Scan(string root)
    {
        root = Path.GetFullPath(root);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            ReturnSpecialDirectories = false,
        };

        // Directory enumeration already carries size and time, so this is one listing per folder
        // rather than one stat per file.
        var files = new DirectoryInfo(root).EnumerateFiles("*", options)
            .Where(f => ImageExt.Contains(f.Extension) || VideoExt.Contains(f.Extension))
            .ToList();

        var result = new List<LibraryEntry>(files.Count);
        foreach (var dir in files.GroupBy(f => f.DirectoryName ?? "", StringComparer.OrdinalIgnoreCase))
        {
            var byName = dir.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
            var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Videos first, so their sidecars are known before the pictures are listed.
            foreach (var f in dir.Where(f => VideoExt.Contains(f.Extension)).OrderBy(f => f.Name.Length))
            {
                if (consumed.Contains(f.Name)) continue;
                var stem = Path.GetFileNameWithoutExtension(f.Name);
                var ext = f.Extension;
                var baseStem = stem.EndsWith("-audio", StringComparison.OrdinalIgnoreCase) ? stem[..^6] : stem;

                var silent = byName.GetValueOrDefault(baseStem + ext);
                var voiced = byName.GetValueOrDefault(baseStem + "-audio" + ext);
                var primary = voiced ?? silent ?? f;
                if (silent != null) consumed.Add(silent.Name);
                if (voiced != null) consumed.Add(voiced.Name);

                var poster = byName.GetValueOrDefault(baseStem + ".png");
                if (poster != null) consumed.Add(poster.Name);

                result.Add(Entry(root, primary, MediaKinds.Video, poster?.FullName));
            }

            foreach (var f in dir.Where(f => ImageExt.Contains(f.Extension)))
                if (!consumed.Contains(f.Name))
                    result.Add(Entry(root, f, MediaKinds.Image, null));
        }

        result = Deduplicate(result);
        result.Sort((a, b) => b.ModifiedUtc.CompareTo(a.ModifiedUtc));
        return result;
    }

    /// <summary>
    /// The desktop copies finished videos into named folders beside ComfyUI's own (MiniMaxI2V/ next to
    /// minimax_i2v/, H3Express/ next to h3_express/), keeping the size and the timestamp to the tick. Those
    /// are one video, so one entry: the copy with a poster frame, which thumbnails without ffmpeg.
    /// </summary>
    private static List<LibraryEntry> Deduplicate(List<LibraryEntry> entries) =>
        entries.GroupBy(e => (e.Kind, e.Size, e.ModifiedUtc.Ticks))
            .Select(g => g.OrderByDescending(e => e.PosterPath != null).ThenBy(e => e.RelativePath, StringComparer.Ordinal).First())
            .ToList();

    private static LibraryEntry Entry(string root, FileInfo f, string kind, string? poster)
    {
        var rel = Path.GetRelativePath(root, f.FullName).Replace('\\', '/');
        var slash = rel.IndexOf('/');
        return new LibraryEntry(IdOf(rel), rel, f.FullName, f.Name, slash > 0 ? rel[..slash] : "", kind,
            f.Length, f.LastWriteTimeUtc, poster);
    }

    /// <summary>A stable, URL-safe id: the relative path, base64url-encoded. It is checked on the way back in.</summary>
    public static string IdOf(string relativePath) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(relativePath.Replace('\\', '/')))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? PathOf(string id)
    {
        try
        {
            var s = id.Replace('-', '+').Replace('_', '/');
            s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
            return Encoding.UTF8.GetString(Convert.FromBase64String(s));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The entry for an id, from the index or, for a file saved since the last scan, from disk. Null
    /// when it isn't a media file inside the output folder: an id can't reach anywhere else.
    /// </summary>
    public LibraryEntry? Find(string id)
    {
        lock (_lock)
            if (_byId.TryGetValue(id, out var known) && File.Exists(known.FullPath)) return known;

        var rel = PathOf(id);
        var root = _root();
        if (rel == null || string.IsNullOrWhiteSpace(root)) return null;
        var full = Path.GetFullPath(Path.Combine(root, rel));
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return null;
        var info = new FileInfo(full);
        if (!info.Exists) return null;
        var kind = VideoExt.Contains(info.Extension) ? MediaKinds.Video
            : ImageExt.Contains(info.Extension) ? MediaKinds.Image : null;
        if (kind == null) return null;

        string? poster = null;
        if (kind == MediaKinds.Video)
        {
            var stem = Path.GetFileNameWithoutExtension(info.Name);
            if (stem.EndsWith("-audio", StringComparison.OrdinalIgnoreCase)) stem = stem[..^6];
            var png = Path.Combine(info.DirectoryName ?? "", stem + ".png");
            if (File.Exists(png)) poster = png;
        }
        return Entry(Path.GetFullPath(root), info, kind, poster);
    }

    /// <summary>
    /// Puts a file a job just saved at the top of the index at once, so the phone's library shows it
    /// without waiting for the next full scan of a slow network folder.
    /// </summary>
    public void Include(string relativePath) => Include(relativePath, 0);

    private void Include(string relativePath, int attempt)
    {
        var entry = Find(IdOf(relativePath));
        if (entry == null)
        {
            // Over SMB, a file ComfyUI wrote a moment ago can read as missing for several seconds
            // (the client caches "not found"). Look again a few times before leaving it to the next scan.
            if (attempt < 4)
                _ = Task.Delay(TimeSpan.FromSeconds(4)).ContinueWith(_ => Include(relativePath, attempt + 1), TaskScheduler.Default);
            return;
        }
        lock (_lock)
        {
            _included.Add(entry);
            if (_included.Count > 50) _included.RemoveAt(0);
            if (_byId.ContainsKey(entry.Id)) return;
            var list = new List<LibraryEntry>(_entries.Count + 1) { entry };
            list.AddRange(_entries);
            list.Sort((a, b) => b.ModifiedUtc.CompareTo(a.ModifiedUtc));
            _entries = list;
            _byId[entry.Id] = entry;
        }
    }

    public (IReadOnlyList<LibraryEntry> Items, int Total, IReadOnlyList<LibraryFolderDto> Folders, string? Problem)
        Query(string? kind, string? folder, int offset, int limit)
    {
        IReadOnlyList<LibraryEntry> all;
        string? problem;
        lock (_lock)
        {
            all = _entries;
            problem = _problem;
        }

        // Folders are listed by their newest item, so the one being worked in comes first.
        var folders = all.GroupBy(e => e.Folder, StringComparer.OrdinalIgnoreCase)
            .Select(g => new LibraryFolderDto { Name = g.First().Folder, Count = g.Count() })
            .ToList();

        IEnumerable<LibraryEntry> q = all;
        if (kind is MediaKinds.Image or MediaKinds.Video) q = q.Where(e => e.Kind == kind);
        if (folder != null) q = q.Where(e => string.Equals(e.Folder, folder, StringComparison.OrdinalIgnoreCase));
        var list = q as IList<LibraryEntry> ?? q.ToList();
        var page = list.Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, 200)).ToList();
        return (page, list.Count, folders, problem);
    }
}
