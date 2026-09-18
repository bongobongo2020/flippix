using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FlipPix.Core.Interfaces;
using FlipPix.Core.Models;
using FlipPix.Core.Services;

namespace FlipPix.UI.Linux.Services
{
    /// <summary>Outcome of installing a single model.</summary>
    public enum InstallResult { Downloaded, Copied, Registered, NotFound, Failed }

    /// <summary>
    /// Installs missing ComfyUI model files: downloads them from the <see cref="ModelCatalog"/>,
    /// copies them in from a folder the user points at, or (fallback) registers that folder with
    /// ComfyUI via extra_model_paths.yaml. Resolves the correct target sub-folder under ComfyUI's
    /// models directory for both local and remote (UNC) installs.
    /// </summary>
    public class ModelInstallerService
    {
        private readonly SettingsService _settingsService;
        private readonly IAppLogger _logger;
        private readonly HttpClient _downloadClient;

        // Standard ComfyUI models sub-folders, used to mirror a located file's layout when the
        // category couldn't be inferred from the workflow.
        private static readonly HashSet<string> CategoryFolders = new(StringComparer.OrdinalIgnoreCase)
        {
            "checkpoints", "loras", "unet", "diffusion_models", "vae", "clip", "text_encoders",
            "controlnet", "upscale_models", "clip_vision", "style_models", "embeddings",
            "vae_approx", "gligen", "hypernetworks", "photomaker", "diffusers", "configs"
        };

        public ModelInstallerService(SettingsService settingsService, IAppLogger logger)
        {
            _settingsService = settingsService;
            _logger = logger;
            // Dedicated client: model files are multi-GB, so no per-request timeout (cancellation
            // is driven by the CancellationToken instead).
            _downloadClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
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

        /// <summary>
        /// The writable ComfyUI "models" folder to install into, or null if it can't be determined
        /// (e.g. a remote server whose RemoteModelsFolderPath hasn't been configured yet).
        /// </summary>
        public string? ResolveModelsRoot()
        {
            if (IsRemoteServer())
            {
                var remote = Settings.RemoteModelsFolderPath;
                return !string.IsNullOrWhiteSpace(remote) && Directory.Exists(remote) ? remote : null;
            }

            // Local: ComfyUI install's models folder, or the Windows folder exposed to a WSL ComfyUI.
            var comfy = Settings.ComfyUIFolderPath;
            if (!string.IsNullOrWhiteSpace(comfy))
            {
                var models = Path.Combine(comfy, "models");
                if (Directory.Exists(models)) return models;
            }
            var wsl = Settings.WslModelsFolderPath;
            return !string.IsNullOrWhiteSpace(wsl) && Directory.Exists(wsl) ? wsl : null;
        }

        /// <summary>
        /// Records a models root the user pointed at. For remote installs this is persisted as
        /// RemoteModelsFolderPath so it isn't asked again. Returns true if the folder is usable.
        /// </summary>
        public bool TrySetModelsRoot(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return false;
            if (IsRemoteServer())
            {
                Settings.RemoteModelsFolderPath = folder;
                _settingsService.SaveSettings(Settings);
            }
            return true;
        }

        public IReadOnlyList<string> PersistedSourceFolders =>
            Settings.UserModelSourceFolders ?? new List<string>();

        /// <summary>Remembers a folder the user located models in (most-recent first), persisted.</summary>
        public void RememberSourceFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
            Settings.UserModelSourceFolders ??= new List<string>();
            Settings.UserModelSourceFolders.RemoveAll(f =>
                string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
            Settings.UserModelSourceFolders.Insert(0, folder);
            // Keep the list short.
            if (Settings.UserModelSourceFolders.Count > 10)
                Settings.UserModelSourceFolders =
                    Settings.UserModelSourceFolders.Take(10).ToList();
            _settingsService.SaveSettings(Settings);
        }

        public bool HasDownloadUrl(MissingModelInfo m) => ModelCatalog.HasUrl(m.Name);

        /// <summary>
        /// Finds <paramref name="m"/> by filename anywhere under <paramref name="folder"/>.
        /// Prefers a copy whose parent folder matches the model's category. Null if not found.
        /// </summary>
        public string? FindInFolder(string folder, MissingModelInfo m)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;
            try
            {
                var matches = Directory.EnumerateFiles(folder, m.FileName, SearchOption.AllDirectories)
                    .Take(50).ToList();
                if (matches.Count == 0) return null;
                if (!string.IsNullOrEmpty(m.Category))
                {
                    var byCategory = matches.FirstOrDefault(p =>
                        (Directory.GetParent(p)?.Name ?? "").Equals(m.Category, StringComparison.OrdinalIgnoreCase)
                        || p.Replace('\\', '/').IndexOf("/" + m.Category + "/", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (byCategory != null) return byCategory;
                }
                return matches[0];
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Search for {m.FileName} in {folder} failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Computes the absolute install path for a model under <paramref name="modelsRoot"/>,
        /// using the inferred category or, failing that, mirroring the source file's layout.
        /// </summary>
        public string ComputeTargetPath(string modelsRoot, MissingModelInfo m, string? sourcePath)
        {
            if (!string.IsNullOrEmpty(m.Category))
                return Path.Combine(modelsRoot, NormalizeRel(m.Category + "/" + m.Name.Replace('\\', '/')));

            if (!string.IsNullOrEmpty(sourcePath))
            {
                var segs = sourcePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                int idx = -1;
                for (int i = segs.Length - 2; i >= 0; i--)
                    if (CategoryFolders.Contains(segs[i])) { idx = i; break; }
                if (idx >= 0)
                    return Path.Combine(modelsRoot, NormalizeRel(string.Join('/', segs.Skip(idx))));
            }

            return Path.Combine(modelsRoot, NormalizeRel(m.Name.Replace('\\', '/')));
        }

        private static string NormalizeRel(string rel) =>
            rel.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

        /// <summary>Downloads a catalog-known model into the models root. Reports (bytesDone, totalBytes).</summary>
        public async Task<InstallResult> DownloadAsync(
            string modelsRoot, MissingModelInfo m,
            IProgress<(long done, long total)>? progress, CancellationToken ct)
        {
            var url = ModelCatalog.GetUrl(m.Name);
            if (string.IsNullOrEmpty(url)) return InstallResult.NotFound;

            var target = ComputeTargetPath(modelsRoot, m, null);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var tmp = target + ".part";

                using var resp = await _downloadClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? -1L;

                await using (var src = await resp.Content.ReadAsStreamAsync(ct))
                await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[1 << 20];
                    long done = 0;
                    int read;
                    while ((read = await src.ReadAsync(buffer, ct)) > 0)
                    {
                        await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                        done += read;
                        progress?.Report((done, total));
                    }
                }

                if (File.Exists(target)) File.Delete(target);
                File.Move(tmp, target);
                _logger.LogInfo($"Downloaded {m.FileName} -> {target}");
                return InstallResult.Downloaded;
            }
            catch (OperationCanceledException)
            {
                TryDelete(target + ".part");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Download of {m.FileName} failed");
                TryDelete(target + ".part");
                return InstallResult.Failed;
            }
        }

        /// <summary>Copies a located model file into the models root, preserving its category layout.</summary>
        public async Task<InstallResult> CopyAsync(
            string modelsRoot, MissingModelInfo m, string sourcePath,
            IProgress<(long done, long total)>? progress, CancellationToken ct)
        {
            var target = ComputeTargetPath(modelsRoot, m, sourcePath);
            try
            {
                if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(target),
                        StringComparison.OrdinalIgnoreCase))
                    return InstallResult.Copied; // already in place

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var tmp = target + ".part";
                var total = new FileInfo(sourcePath).Length;

                await using (var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[1 << 20];
                    long done = 0;
                    int read;
                    while ((read = await src.ReadAsync(buffer, ct)) > 0)
                    {
                        await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                        done += read;
                        progress?.Report((done, total));
                    }
                }

                if (File.Exists(target)) File.Delete(target);
                File.Move(tmp, target);
                _logger.LogInfo($"Copied {m.FileName} -> {target}");
                return InstallResult.Copied;
            }
            catch (OperationCanceledException)
            {
                TryDelete(target + ".part");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Copy of {m.FileName} failed");
                TryDelete(target + ".part");
                return InstallResult.Failed;
            }
        }

        /// <summary>True if <paramref name="folder"/> looks like a ComfyUI "models" root (has standard subfolders).</summary>
        public bool LooksLikeModelsRoot(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return false;
            try
            {
                return Directory.EnumerateDirectories(folder)
                    .Select(d => Path.GetFileName(d))
                    .Count(name => CategoryFolders.Contains(name)) >= 2;
            }
            catch { return false; }
        }

        /// <summary>
        /// Fallback when copying isn't possible: registers <paramref name="folder"/> as an extra
        /// model path in the local ComfyUI's extra_model_paths.yaml (merged, under a "flippix" key).
        /// ComfyUI must be restarted to pick it up. Returns true on success. Local installs only.
        /// </summary>
        public bool RegisterExtraModelPath(string folder)
        {
            if (IsRemoteServer())
            {
                _logger.LogWarning("Cannot register extra_model_paths.yaml for a remote ComfyUI.");
                return false;
            }
            var comfy = Settings.ComfyUIFolderPath;
            if (string.IsNullOrWhiteSpace(comfy) || !Directory.Exists(comfy)) return false;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return false;

            try
            {
                var yamlPath = Path.Combine(comfy, "extra_model_paths.yaml");
                var basePath = folder.Replace('\\', '/');

                // List each standard subfolder that exists so ComfyUI maps the right types.
                var lines = new List<string> { "flippix:", $"  base_path: {basePath}" };
                bool any = false;
                foreach (var sub in Directory.EnumerateDirectories(folder)
                             .Select(Path.GetFileName)
                             .Where(n => n != null && CategoryFolders.Contains(n!)))
                {
                    lines.Add($"  {sub}: {sub}");
                    any = true;
                }
                if (!any)
                {
                    // Flat folder of weights: expose it for the common types.
                    foreach (var t in new[] { "checkpoints", "loras", "diffusion_models", "vae", "text_encoders" })
                        lines.Add($"  {t}: ");
                }

                var block = string.Join(Environment.NewLine, lines) + Environment.NewLine;

                if (File.Exists(yamlPath))
                {
                    var existing = File.ReadAllText(yamlPath);
                    if (existing.Contains($"base_path: {basePath}"))
                    {
                        _logger.LogInfo("extra_model_paths.yaml already registers this folder.");
                        return true;
                    }
                    File.AppendAllText(yamlPath,
                        Environment.NewLine + "# Added by FlipPix" + Environment.NewLine + block);
                }
                else
                {
                    File.WriteAllText(yamlPath,
                        "# Created by FlipPix" + Environment.NewLine + block);
                }

                _logger.LogInfo($"Registered extra model path: {basePath} -> {yamlPath}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write extra_model_paths.yaml");
                return false;
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
        }
    }
}
