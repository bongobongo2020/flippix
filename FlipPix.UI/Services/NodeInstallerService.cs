using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FlipPix.Core.Interfaces;
using FlipPix.Core.Models;
using FlipPix.Core.Services;

namespace FlipPix.UI.Services
{
    /// <summary>Outcome of installing a single custom-node pack.</summary>
    public enum NodeInstallResult { Installed, AlreadyPresent, NoRepo, Failed }

    /// <summary>
    /// Installs missing ComfyUI custom-node packs: resolves the git repo that provides a node
    /// (built-in <see cref="NodeCatalog"/> first, then the running ComfyUI-Manager's node map),
    /// clones it into the local ComfyUI's custom_nodes folder, installs its Python requirements
    /// against the install's Python, and restarts ComfyUI so the new nodes load. Mirrors the way
    /// scripts/setup-comfyui-fresh.ps1 installs nodes, so app-side installs match a fresh setup.
    /// Local installs only (a node has to be cloned into ComfyUI's own folder and the server
    /// restarted); remote servers are reported as not auto-installable.
    /// </summary>
    public class NodeInstallerService
    {
        private readonly SettingsService _settingsService;
        private readonly IAppLogger _logger;
        private readonly HttpClient _managerClient;

        // Cached ComfyUI-Manager node map: node class -> (repoUrl, packTitle). Populated lazily from
        // /customnode/getmappings so we don't refetch (it's large) for each missing node.
        private Dictionary<string, (string repo, string title)>? _managerMap;

        public NodeInstallerService(SettingsService settingsService, IAppLogger logger)
        {
            _settingsService = settingsService;
            _logger = logger;
            // Generous ceiling: a Manager git-URL install (clone + requirements) can take minutes.
            // Short operations (probes, reboot, getmappings) bound themselves with their own CTS.
            _managerClient = new HttpClient { Timeout = TimeSpan.FromMinutes(12) };
        }

        private ComfyUISettings Settings => _settingsService.Settings;

        /// <summary>True when the configured ComfyUI server is not on this machine.</summary>
        public bool IsRemoteServer()
        {
            try
            {
                var host = new Uri(Settings.BaseUrl).Host;
                return !(host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                      || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                      || host.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }

        /// <summary>The local ComfyUI custom_nodes folder to clone into, or null if unavailable (e.g. remote).</summary>
        public string? ResolveCustomNodesDir()
        {
            if (IsRemoteServer()) return null;
            var comfy = Settings.ComfyUIFolderPath;
            if (string.IsNullOrWhiteSpace(comfy) || !Directory.Exists(comfy)) return null;
            return Path.Combine(comfy, "custom_nodes");
        }

        /// <summary>True when packs can be cloned+installed locally (local server with a known ComfyUI folder).</summary>
        public bool CanInstallLocally() => ResolveCustomNodesDir() != null;

        /// <summary>
        /// True when the pack that provides <paramref name="node"/> already exists in the local
        /// custom_nodes folder. Since the node's class is still missing from /object_info, that means
        /// the pack is installed but failing to import (a missing dependency), so reinstalling it
        /// won't help — the resolver uses this to avoid an endless install/restart loop. Local only;
        /// returns false for a remote server or when the providing repo isn't known.
        /// </summary>
        public bool IsPackPresent(MissingNodeInfo node)
        {
            var dir = ResolveCustomNodesDir();
            if (dir == null || node == null || string.IsNullOrEmpty(node.RepoUrl)) return false;
            var name = NodeCatalog.PackNameFromRepo(node.RepoUrl);
            if (string.IsNullOrEmpty(name)) return false;
            // A pack Manager has disabled is named "<pack>.disabled"; treat that as present too.
            foreach (var candidate in new[] { name, name + ".disabled" })
            {
                try
                {
                    var p = Path.Combine(dir, candidate);
                    if (Directory.Exists(p) && Directory.EnumerateFileSystemEntries(p).Any()) return true;
                }
                catch { /* ignore unreadable entries */ }
            }
            return false;
        }

        /// <summary>True if a usable git executable is on PATH.</summary>
        public bool GitAvailable()
        {
            try
            {
                var code = RunProcess("git", "--version", null, null, TimeSpan.FromSeconds(10));
                return code == 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// Fills in <see cref="MissingNodeInfo.RepoUrl"/>/<see cref="MissingNodeInfo.PackName"/> for any
        /// node not already resolved by the offline catalog, by querying the running ComfyUI-Manager's
        /// node map. Best-effort — leaves them empty when Manager isn't reachable or doesn't know the node.
        /// </summary>
        public async Task ResolveReposAsync(IReadOnlyList<MissingNodeInfo> missing, CancellationToken ct)
        {
            if (missing == null || missing.All(m => !string.IsNullOrEmpty(m.RepoUrl))) return;

            var map = await GetManagerMapAsync(ct);
            if (map == null) return;

            foreach (var m in missing)
            {
                if (!string.IsNullOrEmpty(m.RepoUrl)) continue;
                if (map.TryGetValue(m.ClassType, out var hit) && !string.IsNullOrEmpty(hit.repo))
                {
                    m.RepoUrl = hit.repo;
                    m.PackName = !string.IsNullOrEmpty(hit.title)
                        ? hit.title
                        : NodeCatalog.PackNameFromRepo(hit.repo);
                }
            }
        }

        /// <summary>
        /// Installs the pack that provides <paramref name="node"/>. For a local ComfyUI this is a plain
        /// git clone into custom_nodes — the safe path: it only writes files and never runs pip (a
        /// node's requirements can pull a CPU-only torch that shadows the portable CUDA build and stops
        /// ComfyUI from starting). For a remote server, where we can't reach custom_nodes on disk, it
        /// asks ComfyUI-Manager to install by git URL. Reports progress via <paramref name="log"/>.
        /// </summary>
        public async Task<NodeInstallResult> InstallAsync(
            MissingNodeInfo node, IProgress<string>? log, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(node.RepoUrl)) return NodeInstallResult.NoRepo;
            var packName = NodeCatalog.PackNameFromRepo(node.RepoUrl);

            // Preferred path: clone the pack into the local custom_nodes ourselves. Cloning is safe
            // (files only, no pip); most catalog packs are pure-Python and need nothing else. We do
            // NOT run pip on the node's requirements — that's what previously pulled a CPU torch and
            // broke ComfyUI. Packs that genuinely need Python deps get them from ComfyUI-Manager's
            // own (torch-safe) requirements install when it re-scans custom_nodes on restart.
            var customDir = ResolveCustomNodesDir();
            if (customDir != null && GitAvailable())
            {
                try
                {
                    Directory.CreateDirectory(customDir);
                    var dest = Path.Combine(customDir, packName);

                    if (Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any())
                    {
                        log?.Report($"{packName} already present — updating.");
                        await Task.Run(() => RunProcess("git", $"-C \"{dest}\" pull --ff-only", customDir, log, TimeSpan.FromMinutes(3)), ct);
                        return NodeInstallResult.AlreadyPresent;
                    }

                    log?.Report($"Cloning {packName} ...");
                    var clone = await Task.Run(
                        () => RunProcess("git", $"clone --depth 1 \"{node.RepoUrl}\" \"{dest}\"", customDir, log, TimeSpan.FromMinutes(10)), ct);
                    if (clone != 0 || !Directory.Exists(dest))
                    {
                        _logger.LogError($"git clone failed for {node.RepoUrl} (exit {clone})");
                        return NodeInstallResult.Failed;
                    }

                    if (File.Exists(Path.Combine(dest, "requirements.txt")))
                        log?.Report($"Cloned {packName}. It has Python requirements — if the node still errors after " +
                                    "restart, install them via ComfyUI-Manager (Install Missing Custom Nodes).");
                    else
                        log?.Report($"Cloned {packName}.");

                    return NodeInstallResult.Installed;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Local clone of {node.RepoUrl} failed");
                    // fall through to the Manager path as a last resort
                }
            }

            // Remote server (or no local git): ask ComfyUI-Manager to install it. Note Manager blocks
            // git-URL installs unless allow_git_url_install=true in its config.ini, so this may be
            // Forbidden — in which case we tell the user how to proceed.
            var mgr = await TryManagerInstallGitUrlAsync(node.RepoUrl, log, ct);
            if (mgr == ManagerInstall.Ok)
            {
                log?.Report($"{packName}: installed via ComfyUI-Manager.");
                return NodeInstallResult.Installed;
            }
            if (mgr == ManagerInstall.Forbidden)
            {
                log?.Report($"{packName}: ComfyUI-Manager blocks git-URL installs (allow_git_url_install=false). " +
                            "Install it from Manager's node list, or set allow_git_url_install=true in ComfyUI-Manager's config.ini.");
                return NodeInstallResult.Failed;
            }
            if (customDir == null)
                log?.Report($"{packName}: can't reach a local custom_nodes folder and ComfyUI-Manager isn't installing it. " +
                            $"Install it manually from {node.RepoUrl}.");
            else
                log?.Report($"{packName}: git isn't available to clone the pack. Install it via ComfyUI-Manager.");
            return NodeInstallResult.Failed;
        }

        private enum ManagerInstall { Ok, Forbidden, Unavailable }

        /// <summary>
        /// Asks the running ComfyUI-Manager to install a pack by git URL (POST /customnode/install/git_url,
        /// JSON body {"url": ...}). Manager clones it and installs its requirements with its own
        /// torch-safe pip handling. Returns Ok on success, Forbidden if Manager's security level blocks
        /// git-URL installs, or Unavailable if Manager isn't reachable / doesn't expose the endpoint.
        /// </summary>
        private async Task<ManagerInstall> TryManagerInstallGitUrlAsync(string repoUrl, IProgress<string>? log, CancellationToken ct)
        {
            try
            {
                var baseUrl = Settings.BaseUrl.TrimEnd('/');
                log?.Report($"Asking ComfyUI-Manager to install {NodeCatalog.PackNameFromRepo(repoUrl)} ...");
                using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                reqCts.CancelAfter(TimeSpan.FromMinutes(10)); // clone + requirements can be slow
                using var body = new StringContent(
                    JsonSerializer.Serialize(new { url = repoUrl }), Encoding.UTF8, "application/json");
                var resp = await _managerClient.PostAsync(baseUrl + "/customnode/install/git_url", body, reqCts.Token);
                if (resp.IsSuccessStatusCode) return ManagerInstall.Ok;
                if ((int)resp.StatusCode == 403)
                {
                    _logger.LogWarning("ComfyUI-Manager git_url install returned 403 (allow_git_url_install disabled).");
                    return ManagerInstall.Forbidden;
                }
                _logger.LogInfo($"ComfyUI-Manager git_url install returned {(int)resp.StatusCode}; falling back to local clone.");
                return ManagerInstall.Unavailable;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogInfo($"ComfyUI-Manager git_url install not available: {ex.Message}");
                return ManagerInstall.Unavailable;
            }
        }

        /// <summary>
        /// Installs a single, specific pip package (e.g. "nvidia-vfx") into the local ComfyUI's Python
        /// to fix an installed-but-broken pack whose import fails only for a missing module. This is a
        /// TARGETED install of a known package — never a requirements.txt sweep — and it runs with
        /// PYTHONNOUSERSITE=1 so pip can't leak into the per-user site-packages (which is how a stray
        /// CPU torch previously shadowed the portable CUDA build). Local installs only.
        /// </summary>
        public async Task<NodeInstallResult> InstallPipDependencyAsync(
            MissingNodeInfo node, IProgress<string>? log, CancellationToken ct)
        {
            if (node == null || string.IsNullOrEmpty(node.PipPackage)) return NodeInstallResult.NoRepo;
            if (ResolveCustomNodesDir() == null) return NodeInstallResult.NoRepo; // remote: we can't install into its Python

            var python = ResolveEmbeddedPython();
            if (python == null)
            {
                log?.Report($"Couldn't find ComfyUI's Python to install {node.PipPackage}. Install it manually into the ComfyUI Python.");
                return NodeInstallResult.Failed;
            }

            // Specific package + extra index; -U to prefer the newest matching (abi3) wheel.
            var args = $"-m pip install -U --no-build-isolation \"{node.PipPackage}\"";
            if (!string.IsNullOrEmpty(node.PipIndexUrl))
                args += $" --extra-index-url {node.PipIndexUrl}";

            // The critical guard: no per-user site install, so this can never shadow the portable env.
            var env = new Dictionary<string, string> { ["PYTHONNOUSERSITE"] = "1" };

            log?.Report($"Installing {node.PipPackage} for {node.ClassType} (this can be a large download)...");
            var code = await Task.Run(
                () => RunProcess(python, args, Path.GetDirectoryName(python), log, TimeSpan.FromMinutes(30), env), ct);
            if (code != 0)
            {
                log?.Report($"{node.PipPackage} install failed (exit {code}). See the log; you may need to install it manually.");
                return NodeInstallResult.Failed;
            }
            log?.Report($"Installed {node.PipPackage}.");
            return NodeInstallResult.Installed;
        }

        /// <summary>
        /// Finds the local ComfyUI install's Python interpreter (portable python_embeded, or a venv).
        /// Used only for the targeted dependency install above. Null if it can't be located.
        /// </summary>
        private string? ResolveEmbeddedPython()
        {
            var comfy = Settings.ComfyUIFolderPath;
            if (string.IsNullOrWhiteSpace(comfy) || !Directory.Exists(comfy)) return null;
            var portableRoot = Directory.GetParent(comfy)?.FullName;
            var candidates = new[]
            {
                portableRoot != null ? Path.Combine(portableRoot, "python_embeded", "python.exe") : null,
                Path.Combine(comfy, "venv", "Scripts", "python.exe"),
                portableRoot != null ? Path.Combine(portableRoot, "venv", "Scripts", "python.exe") : null,
                Path.Combine(comfy, ".venv", "Scripts", "python.exe"),
            };
            foreach (var c in candidates)
                if (!string.IsNullOrEmpty(c) && File.Exists(c)) return c;
            return null;
        }

        /// <summary>
        /// Restarts ComfyUI so freshly-installed custom nodes load, via ComfyUI-Manager's in-place
        /// reboot (which re-scans custom_nodes), then waits until it's fully ready again. Returns true
        /// only when the server was confirmed to cycle and come back with all nodes loaded — so a
        /// caller retrying the workflow can trust the new nodes are present. Requires ComfyUI-Manager
        /// (part of the FlipPix stack); otherwise reports that a manual restart is needed.
        /// </summary>
        public async Task<bool> RestartComfyUIAsync(Action<string>? status, CancellationToken ct)
        {
            status?.Invoke("Restarting ComfyUI to load the new nodes...");

            var triggered = await TryManagerRebootAsync(ct);
            if (!triggered)
            {
                status?.Invoke("Couldn't reboot ComfyUI automatically (ComfyUI-Manager not reachable). " +
                               "Restart ComfyUI manually, then click Retry.");
                return false;
            }

            // Confirm the reboot actually took effect: the server should drop, then come back ready.
            // Requiring the "down" transition avoids a false success where the reboot didn't happen.
            var wentDown = await WaitForServerDownAsync(TimeSpan.FromSeconds(45), ct);
            if (!wentDown)
            {
                status?.Invoke("ComfyUI didn't restart as expected. Restart it manually, then click Retry.");
                return false;
            }

            status?.Invoke("Waiting for ComfyUI to come back up (loading nodes)...");
            var timeout = TimeSpan.FromSeconds(Math.Max(60, Settings.ComfyUIStartupTimeoutSeconds));
            var ready = await WaitForServerReadyAsync(timeout, ct);
            if (!ready)
            {
                status?.Invoke("ComfyUI is taking a while to come back. Once it's up, click Retry.");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Asks ComfyUI-Manager to reboot ComfyUI in-place (POST /manager/reboot, with older-version
        /// fallbacks). A reboot tears the HTTP connection down, so a dropped/timed-out request is
        /// treated as "reboot initiated". Returns true if a reboot was plausibly triggered.
        /// </summary>
        private async Task<bool> TryManagerRebootAsync(CancellationToken ct)
        {
            var baseUrl = Settings.BaseUrl.TrimEnd('/');
            var attempts = new (string method, string path)[]
            {
                ("POST", "/manager/reboot"),
                ("GET",  "/api/manager/reboot"),
                ("GET",  "/manager/reboot"),
            };

            foreach (var (method, path) in attempts)
            {
                try
                {
                    using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    reqCts.CancelAfter(TimeSpan.FromSeconds(8));
                    using var req = new HttpRequestMessage(
                        method == "POST" ? HttpMethod.Post : HttpMethod.Get, baseUrl + path);
                    var resp = await _managerClient.SendAsync(req, reqCts.Token);
                    if (resp.IsSuccessStatusCode)
                    {
                        _logger.LogInfo($"ComfyUI-Manager reboot triggered via {method} {path}.");
                        return true;
                    }
                    // 404/405 -> wrong endpoint for this Manager version; try the next.
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    // Connection reset / timeout: the server is going down — reboot took effect.
                    _logger.LogInfo($"ComfyUI-Manager reboot request to {path} dropped the connection (reboot initiated).");
                    return true;
                }
            }
            return false;
        }

        private async Task<bool> WaitForServerDownAsync(TimeSpan timeout, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (!await ProbeAsync("/system_stats", TimeSpan.FromSeconds(2), ct)) return true;
                await Task.Delay(500, ct);
            }
            return false;
        }

        private async Task<bool> WaitForServerReadyAsync(TimeSpan timeout, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                // /object_info only answers once every node has finished loading.
                if (await ProbeAsync("/object_info", TimeSpan.FromSeconds(5), ct)) return true;
                await Task.Delay(2000, ct);
            }
            return false;
        }

        private async Task<bool> ProbeAsync(string path, TimeSpan timeout, CancellationToken ct)
        {
            try
            {
                using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                reqCts.CancelAfter(timeout);
                var resp = await _managerClient.GetAsync(
                    Settings.BaseUrl.TrimEnd('/') + path, HttpCompletionOption.ResponseHeadersRead, reqCts.Token);
                return resp.IsSuccessStatusCode;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return false; }
        }

        // --- ComfyUI-Manager node map ---

        private async Task<Dictionary<string, (string repo, string title)>?> GetManagerMapAsync(CancellationToken ct)
        {
            if (_managerMap != null) return _managerMap;
            try
            {
                var baseUrl = Settings.BaseUrl.TrimEnd('/');
                // ComfyUI-Manager exposes the extension→node map here (keyed by repo URL). "nickname"
                // mode returns the same class-name lists keyed by reference.
                var url = $"{baseUrl}/customnode/getmappings?mode=nickname";
                var json = await _managerClient.GetStringAsync(url, ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

                var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
                foreach (var entry in doc.RootElement.EnumerateObject())
                {
                    // Value shape: [ [ "ClassA", "ClassB", ... ], { "title_aux": "Pack Name", ... } ]
                    var val = entry.Value;
                    if (val.ValueKind != JsonValueKind.Array || val.GetArrayLength() == 0) continue;

                    var repo = entry.Name;
                    if (!repo.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;

                    var title = "";
                    if (val.GetArrayLength() > 1 && val[1].ValueKind == JsonValueKind.Object
                        && val[1].TryGetProperty("title_aux", out var t) && t.ValueKind == JsonValueKind.String)
                        title = t.GetString() ?? "";

                    var names = val[0];
                    if (names.ValueKind != JsonValueKind.Array) continue;
                    foreach (var n in names.EnumerateArray())
                    {
                        if (n.ValueKind != JsonValueKind.String) continue;
                        var cls = n.GetString();
                        if (!string.IsNullOrEmpty(cls) && !map.ContainsKey(cls!))
                            map[cls!] = (repo, title);
                    }
                }
                _logger.LogInfo($"ComfyUI-Manager node map: {map.Count} node classes.");
                _managerMap = map;
                return _managerMap;
            }
            catch (Exception ex)
            {
                _logger.LogInfo($"Could not read ComfyUI-Manager node map (not installed / unreachable): {ex.Message}");
                return null;
            }
        }

        // --- process helper ---

        /// <summary>
        /// Runs a console process, streaming stdout/stderr lines to <paramref name="log"/>, and returns
        /// its exit code (or -1 on timeout/failure to start). Synchronous; call via Task.Run.
        /// </summary>
        private int RunProcess(string fileName, string arguments, string? workingDir, IProgress<string>? log, TimeSpan timeout,
            IDictionary<string, string>? env = null)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                if (!string.IsNullOrEmpty(workingDir)) psi.WorkingDirectory = workingDir;
                if (env != null)
                    foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

                using var proc = new Process { StartInfo = psi };
                var sb = new StringBuilder();
                DataReceivedEventHandler onData = (_, e) =>
                {
                    if (e.Data == null) return;
                    sb.AppendLine(e.Data);
                };
                proc.OutputDataReceived += onData;
                proc.ErrorDataReceived += onData;

                if (!proc.Start())
                {
                    _logger.LogWarning($"Failed to start process: {fileName} {arguments}");
                    return -1;
                }
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                    log?.Report($"{Path.GetFileName(fileName)} timed out after {timeout.TotalMinutes:0} min.");
                    return -1;
                }
                proc.WaitForExit(); // flush async readers

                var output = sb.ToString();
                if (output.Length > 0)
                    _logger.LogInfo($"[{Path.GetFileName(fileName)}] {output.Trim()}");
                return proc.ExitCode;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Process error ({fileName} {arguments}): {ex.Message}");
                return -1;
            }
        }
    }
}
