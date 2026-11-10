using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace FlipPix.ComfyUI.Setup;

/// <summary>A server model folder and the path that reaches it from this PC.</summary>
public sealed record FolderMapping(string ServerPath, string LocalPath);

/// <summary>
/// Finds how this PC can reach a remote ComfyUI's model folders: a mapped network drive or a share on the
/// server whose folders line up with the absolute paths the server reports in /internal/folder_paths
/// (e.g. server "/mnt/storage1/ai-models/loras" = "\\10.0.0.10\ai-models\loras" = "Z:\loras"). With one,
/// FlipPix can drop downloaded or copied models straight where ComfyUI looks, with no SSH.
/// </summary>
public static class ModelShareLocator
{
    /// <summary>
    /// For each model category the server reports, the first of its folders this PC can reach.
    /// <paramref name="hints"/> are local folders the user already pointed FlipPix at (remote models or
    /// output folder); they are tried as roots too. A local server's folders map to themselves.
    /// </summary>
    public static async Task<Dictionary<string, FolderMapping>> LocateAsync(
        string serverBaseUrl, IReadOnlyDictionary<string, List<string>> serverFolders,
        IEnumerable<string?> hints, CancellationToken ct)
    {
        var result = new Dictionary<string, FolderMapping>(StringComparer.OrdinalIgnoreCase);
        if (!Uri.TryCreate(serverBaseUrl, UriKind.Absolute, out var uri)) return result;
        var host = uri.Host;
        bool local = IsLocalHost(host);

        var serverIps = local ? Array.Empty<IPAddress>() : await ResolveAsync(host, ct);
        var roots = local ? new List<(string server, string local)>() : await CandidateRootsAsync(host, serverIps, serverFolders, hints, ct);

        foreach (var (category, paths) in serverFolders)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var serverPath in paths)
            {
                string? found = null;
                if (local)
                {
                    if (Directory.Exists(serverPath)) found = serverPath;
                }
                else
                {
                    foreach (var candidate in MapCandidates(serverPath, roots))
                        if (await ExistsAsync(candidate, ct)) { found = candidate; break; }
                }
                if (found != null)
                {
                    result[category] = new FolderMapping(serverPath, found);
                    break;
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Local paths <paramref name="serverPath"/> may be reachable at, given known (server root, local root)
    /// pairs. Pure, for tests.
    /// </summary>
    public static IEnumerable<string> MapCandidates(string serverPath, IEnumerable<(string server, string local)> roots)
    {
        var sp = serverPath.Replace('\\', '/').TrimEnd('/');
        foreach (var (server, localRoot) in roots)
        {
            var sr = server.Replace('\\', '/').TrimEnd('/');
            if (sp.Equals(sr, StringComparison.OrdinalIgnoreCase))
                yield return localRoot;
            else if (sp.StartsWith(sr + "/", StringComparison.OrdinalIgnoreCase))
                yield return Path.Combine(localRoot, sp[(sr.Length + 1)..].Replace('/', '\\'));
        }
    }

    /// <summary>
    /// The share names a server path could be published under: each segment of the path, paired with the
    /// server prefix it would stand for. "/mnt/storage1/ai-models/loras" gives ("mnt" = "/mnt"),
    /// ("storage1" = "/mnt/storage1"), ("ai-models" = "/mnt/storage1/ai-models"), ... Pure, for tests.
    /// </summary>
    public static IEnumerable<(string shareName, string serverPrefix)> ShareGuesses(string serverPath)
    {
        var p = serverPath.Replace('\\', '/');
        var drive = "";
        if (p.Length >= 2 && p[1] == ':') { drive = p[..2]; p = p[2..]; }
        var segs = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var prefix = drive;
        foreach (var s in segs)
        {
            prefix += "/" + s;
            yield return (s, prefix);
        }
    }

    private static async Task<List<(string server, string local)>> CandidateRootsAsync(
        string host, IPAddress[] serverIps, IReadOnlyDictionary<string, List<string>> serverFolders,
        IEnumerable<string?> hints, CancellationToken ct)
    {
        // (server prefix, local root, preference): a mapped drive letter reads better than a raw UNC path.
        var roots = new List<(string server, string local, int pref)>();
        var allServerPaths = serverFolders.Values.SelectMany(v => v).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // A share's name only says which server folder it is when exactly one server folder ends in it:
        // "\\host\ai-models" is "/mnt/storage1/ai-models", but "\\host\models" could be any of several
        // installs' models folders, so an ambiguous name is never guessed.
        void AddByShareSegments(IReadOnlyList<string> shareSegs, string localRoot, int pref)
        {
            var prefixes = PrefixesEndingWith(shareSegs, allServerPaths);
            if (prefixes.Count == 1) roots.Add((prefixes[0], localRoot, pref));
        }

        // 1) Shares guessed from the server's own paths: \\host\<segment>, each name probed once.
        var shareNames = allServerPaths.SelectMany(ShareGuesses).Select(g => g.shareName)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(60).ToList();
        var probes = shareNames.Select(async name => (name, ok: await ExistsAsync($@"\\{host}\{name}", ct)));
        foreach (var (name, ok) in await Task.WhenAll(probes))
            if (ok) AddByShareSegments(new[] { name }, $@"\\{host}\{name}", 2);

        // 2) Drives mapped to a share on the server.
        var drives = new List<(string drive, string[] segs)>();
        foreach (var (drive, unc) in MappedNetworkDrives())
        {
            if (!await SameHostAsync(unc, host, serverIps, ct)) continue;
            var segs = UncSegments(unc);
            if (segs.Length == 0) continue;
            drives.Add((drive, segs));
            AddByShareSegments(segs, drive, 0);
        }

        // 3) Folders the user already pointed FlipPix at (remote models / output folder ...).
        foreach (var raw in hints)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var hint = raw.Trim().TrimEnd('\\', '/');
            if (hint.Length == 2 && hint[1] == ':') hint += "\\";
            if (!await ExistsAsync(hint, ct)) continue;

            string[]? segs = null;
            if (hint.StartsWith(@"\\") && await SameHostAsync(hint, host, serverIps, ct))
                segs = UncSegments(hint);
            else
            {
                var d = drives.FirstOrDefault(x => hint.StartsWith(x.drive, StringComparison.OrdinalIgnoreCase));
                if (d.drive != null)
                    segs = d.segs.Concat(hint[d.drive.Length..].Split('\\', '/', StringSplitOptions.RemoveEmptyEntries)).ToArray();
            }

            if (segs != null && segs.Length > 0) AddByShareSegments(segs, hint, 1);
            else
            {
                // A folder that isn't on a known share (e.g. mounted some other way): line it up by the
                // folder names inside it, and only when that points at one server folder.
                var aligned = AlignByTail(hint, allServerPaths);
                if (aligned.Count == 1) roots.Add((aligned[0], hint, 1));
            }
        }

        return roots
            .OrderBy(r => r.pref)
            .ThenByDescending(r => r.server.Length)
            .Select(r => (r.server, r.local))
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// The server folders whose path holds <paramref name="tail"/> as consecutive folder names, cut just
    /// after them: tail ["ai-models"] and "/mnt/storage1/ai-models/loras" give "/mnt/storage1/ai-models".
    /// Distinct, in first-seen order. Pure, for tests.
    /// </summary>
    public static List<string> PrefixesEndingWith(IReadOnlyList<string> tail, IEnumerable<string> serverPaths)
    {
        var result = new List<string>();
        if (tail.Count == 0) return result;
        foreach (var sp in serverPaths)
        {
            var norm = sp.Replace('\\', '/');
            var segs = norm.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i + tail.Count <= segs.Length; i++)
            {
                bool match = true;
                for (int k = 0; k < tail.Count && match; k++)
                    match = segs[i + k].Equals(tail[k], StringComparison.OrdinalIgnoreCase);
                if (!match) continue;
                var prefix = (norm.StartsWith("/") ? "/" : "") + string.Join('/', segs.Take(i + tail.Count));
                if (!result.Contains(prefix, StringComparer.OrdinalIgnoreCase)) result.Add(prefix);
                break;
            }
        }
        return result;
    }

    // "\\host\share\sub" -> ["share", "sub"]
    private static string[] UncSegments(string unc)
    {
        var parts = unc.TrimStart('\\').Split('\\', '/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 1 ? Array.Empty<string>() : parts[1..];
    }

    /// <summary>
    /// The server folders a local folder could mirror, judged by the folder names inside it: local "D:\x"
    /// named "ai-models" and holding "loras", with server "/mnt/storage1/ai-models/loras", gives
    /// "/mnt/storage1/ai-models". Lists <paramref name="localRoot"/>; otherwise pure.
    /// </summary>
    internal static List<string> AlignByTail(string localRoot, IEnumerable<string> serverPaths)
    {
        var result = new List<string>();
        HashSet<string> subdirs;
        try
        {
            subdirs = new HashSet<string>(Directory.EnumerateDirectories(localRoot).Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase);
        }
        catch { return result; }

        var localName = Path.GetFileName(localRoot.TrimEnd('\\', '/'));
        foreach (var sp in serverPaths)
        {
            var segs = sp.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int i = segs.Length - 1; i >= 1; i--)
            {
                // localRoot == server folder segs[0..i) when segs[i] is one of its subfolders and,
                // if localRoot has a name of its own, that name is segs[i-1].
                if (!subdirs.Contains(segs[i])) continue;
                if (!string.IsNullOrEmpty(localName) && !localName.Equals(segs[i - 1], StringComparison.OrdinalIgnoreCase)) continue;
                var prefix = (sp.StartsWith("/") ? "/" : "") + string.Join('/', segs.Take(i));
                if (!result.Contains(prefix, StringComparer.OrdinalIgnoreCase)) result.Add(prefix);
                break;
            }
        }
        return result;
    }

    /// <summary>True when this PC can create and delete a file in <paramref name="folder"/>.</summary>
    public static bool CanWrite(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, $".flippix-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    public static bool IsLocalHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1" || host == "::1"
        || host == "[::1]" || host == "0.0.0.0"
        || host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);

    // A missing share on a reachable host can take several seconds to fail; never let one stall the scan.
    private static async Task<bool> ExistsAsync(string path, CancellationToken ct)
    {
        var probe = Task.Run(() => Directory.Exists(path), ct);
        var done = await Task.WhenAny(probe, Task.Delay(TimeSpan.FromSeconds(6), ct));
        return done == probe && probe.Result;
    }

    private static async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var ip)) return new[] { ip };
        try { return await Dns.GetHostAddressesAsync(host, ct); }
        catch { return Array.Empty<IPAddress>(); }
    }

    private static async Task<bool> SameHostAsync(string unc, string host, IPAddress[] serverIps, CancellationToken ct)
    {
        var uncHost = UncHost(unc);
        if (uncHost.Equals(host, StringComparison.OrdinalIgnoreCase)) return true;
        if (serverIps.Length == 0) return false;
        var ips = await ResolveAsync(uncHost, ct);
        return ips.Any(a => serverIps.Contains(a));
    }

    private static string UncHost(string unc)
    {
        var s = unc.TrimStart('\\');
        var i = s.IndexOf('\\');
        return i < 0 ? s : s[..i];
    }

    /// <summary>Mapped network drives as (drive root "Z:\", UNC "\\host\share"). Windows only.</summary>
    public static List<(string drive, string unc)> MappedNetworkDrives()
    {
        var list = new List<(string, string)>();
        if (!OperatingSystem.IsWindows()) return list;
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Network) continue;
                var name = d.Name.TrimEnd('\\');
                var sb = new StringBuilder(512);
                int len = sb.Capacity;
                if (WNetGetConnection(name, sb, ref len) == 0 && sb.Length > 0)
                    list.Add((d.Name, sb.ToString()));
            }
            catch { /* a disconnected drive */ }
        }
        return list;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);
}
