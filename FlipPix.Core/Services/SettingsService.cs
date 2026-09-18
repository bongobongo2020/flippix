using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using FlipPix.Core.Interfaces;
using FlipPix.Core.Models;

namespace FlipPix.Core.Services
{
    public class SettingsService
    {
        private readonly string _settingsFilePath;
        private readonly ReaderWriterLockSlim _lock = new();
        private IAppLogger? _logger;
        private ComfyUISettings _settings;

        public SettingsService()
        {
            var appDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FlipPix"
            );
            Directory.CreateDirectory(appDataFolder);
            _settingsFilePath = Path.Combine(appDataFolder, "settings.json");

            _settings = LoadSettings();
        }

        public ComfyUISettings Settings => _settings;

        public void SetLogger(IAppLogger logger) => _logger = logger;

        public ComfyUISettings LoadSettings()
        {
            _lock.EnterReadLock();
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    var json = File.ReadAllText(_settingsFilePath);
                    var settings = JsonSerializer.Deserialize<ComfyUISettings>(json);
                    if (settings != null)
                    {
                        // Older settings files only had a list of plain URLs; fold those into
                        // named server profiles so the UI always has something to show.
                        settings.LMStudioSettings?.EnsureProfiles();
                        return settings;
                    }
                }
            }
            catch (Exception)
            {
                // If loading fails, return default settings
            }
            finally
            {
                _lock.ExitReadLock();
            }

            return new ComfyUISettings();
        }

        public void SaveSettings(ComfyUISettings settings)
        {
            _lock.EnterWriteLock();
            try
            {
                _settings = settings;
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true
                };
                var json = JsonSerializer.Serialize(settings, options);
                File.WriteAllText(_settingsFilePath, json);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to save settings: {ex.Message}", ex);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        private const string GlobalBrowseKey = "__global__";

        /// <summary>
        /// Returns the remembered browse folder for <paramref name="key"/>, falling back to the
        /// global last-used folder. Null if neither is set or the folder no longer exists.
        /// </summary>
        public string? GetLastBrowseFolder(string? key)
        {
            _lock.EnterReadLock();
            try
            {
                var folders = _settings.LastBrowseFolders;
                if (folders != null)
                {
                    if (!string.IsNullOrEmpty(key) && folders.TryGetValue(key, out var dir) && Directory.Exists(dir))
                        return dir;
                    if (folders.TryGetValue(GlobalBrowseKey, out var global) && Directory.Exists(global))
                        return global;
                }
            }
            finally
            {
                _lock.ExitReadLock();
            }
            return null;
        }

        /// <summary>
        /// Records <paramref name="folder"/> as the last-used browse folder for <paramref name="key"/>
        /// (and as the global fallback) and persists it. Cheap no-op if the folder is invalid.
        /// </summary>
        public void SetLastBrowseFolder(string? key, string? folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;

            _lock.EnterWriteLock();
            try
            {
                _settings.LastBrowseFolders ??= new Dictionary<string, string>();
                if (!string.IsNullOrEmpty(key))
                    _settings.LastBrowseFolders[key] = folder;
                _settings.LastBrowseFolders[GlobalBrowseKey] = folder;

                var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_settingsFilePath, json);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to persist last browse folder: {ex.Message}");
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public bool IsComfyUIFolderConfigured()
        {
            // Check if we have a local ComfyUI folder configured
            if (!string.IsNullOrEmpty(_settings.ComfyUIFolderPath) && Directory.Exists(_settings.ComfyUIFolderPath))
            {
                return true;
            }

            // Check if we have a remote server configuration (no local folder needed)
            if (!string.IsNullOrEmpty(_settings.BaseUrl) && !string.IsNullOrEmpty(_settings.RemoteOutputFolderPath))
            {
                try
                {
                    // For remote servers, just check if we can parse the URL and the output folder exists
                    var uri = new Uri(_settings.BaseUrl);
                    var isRemote = !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
                                   !uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
                                   !uri.Host.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase);

                    if (isRemote && !string.IsNullOrEmpty(_settings.RemoteOutputFolderPath))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Invalid URL, return false
                }
            }

            return false;
        }

        /// <summary>
        /// Looks for a ComfyUI install (e.g. one created by the FlipPix installer at
        /// %USERPROFILE%\ComfyUI_FlipPix\...\ComfyUI, or a standard portable build) in a few
        /// well-known locations so a fresh install doesn't force the user to browse for it.
        /// Returns the ComfyUI root (the folder holding main.py + an output folder) or null.
        /// Deliberately narrow and fast - no broad drive scans (network drives can stall).
        /// </summary>
        public string? TryAutoDetectComfyUIFolder()
        {
            try
            {
                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                // Folders that may directly contain a "<portable>\ComfyUI" install; scanned one
                // level deep (the installer lays down ComfyUI_windows_portable\ComfyUI inside).
                var scanRoots = new List<string>();
                if (!string.IsNullOrEmpty(userProfile))
                {
                    scanRoots.Add(Path.Combine(userProfile, "ComfyUI_FlipPix")); // installer default
                    scanRoots.Add(userProfile);
                }

                foreach (var root in scanRoots)
                {
                    if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;

                    // <root>\ComfyUI directly
                    var direct = Path.Combine(root, "ComfyUI");
                    if (IsComfyUIInstall(direct))
                    {
                        _logger?.LogInfo($"Auto-detected ComfyUI install: {direct}");
                        return direct;
                    }

                    // <root>\<portable>\ComfyUI
                    IEnumerable<string> subdirs;
                    try { subdirs = Directory.EnumerateDirectories(root); }
                    catch { continue; }

                    foreach (var sub in subdirs)
                    {
                        var comfy = Path.Combine(sub, "ComfyUI");
                        if (IsComfyUIInstall(comfy))
                        {
                            _logger?.LogInfo($"Auto-detected ComfyUI install: {comfy}");
                            return comfy;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Auto-detect ComfyUI folder failed: {ex.Message}");
            }
            return null;
        }

        // A folder is a usable ComfyUI root if it has ComfyUI's entry point and the output
        // folder (the latter is also what ValidateAndSetComfyUIFolder requires).
        private static bool IsComfyUIInstall(string comfyDir)
        {
            return Directory.Exists(comfyDir)
                && File.Exists(Path.Combine(comfyDir, "main.py"))
                && Directory.Exists(Path.Combine(comfyDir, "output"));
        }

        /// <summary>
        /// Finds the launch script (run_nvidia_gpu.bat / run_cpu.bat) for the configured local
        /// ComfyUI install. Portable builds keep it in the portable root - the parent of the
        /// ComfyUI folder. Returns the full path or null.
        /// </summary>
        public string? TryDetectComfyUIStartScript()
        {
            try
            {
                var comfy = _settings.ComfyUIFolderPath;
                if (string.IsNullOrEmpty(comfy) || !Directory.Exists(comfy)) return null;

                var portableRoot = Directory.GetParent(comfy)?.FullName;
                var searchDirs = new[] { portableRoot, comfy };
                var preferred = new[] { "run_nvidia_gpu.bat", "run_cpu.bat" };

                foreach (var dir in searchDirs)
                {
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;

                    foreach (var name in preferred)
                    {
                        var p = Path.Combine(dir, name);
                        if (File.Exists(p)) return p;
                    }

                    // Fall back to any run_*.bat in that folder.
                    try
                    {
                        var anyRun = Directory.EnumerateFiles(dir, "run_*.bat").FirstOrDefault();
                        if (anyRun != null) return anyRun;
                    }
                    catch { /* ignore unreadable dir */ }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Detect ComfyUI start script failed: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Ensures <see cref="ComfyUISettings.ComfyUIRestartScriptPath"/> points at a real launch
        /// script for the local install, auto-detecting and persisting it when missing. This is
        /// what lets the app auto-start a locally-installed ComfyUI instead of asking the user to
        /// browse for it. Returns true if a usable script path is now configured.
        /// </summary>
        public bool EnsureRestartScriptConfigured()
        {
            if (!string.IsNullOrEmpty(_settings.ComfyUIRestartScriptPath)
                && File.Exists(_settings.ComfyUIRestartScriptPath))
            {
                return true;
            }

            var script = TryDetectComfyUIStartScript();
            if (script == null) return false;

            _settings.ComfyUIRestartScriptPath = script;
            SaveSettings(_settings);
            _logger?.LogInfo($"Auto-configured ComfyUI start script: {script}");
            return true;
        }

        public bool ValidateAndSetComfyUIFolder(string folderPath)
        {
            try
            {
                _logger?.LogInfo($"ValidateAndSetComfyUIFolder called with path: {folderPath}");

                if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
                {
                    _logger?.LogWarning($"Validation failed - Invalid or non-existent folder: {folderPath}");
                    return false;
                }

                // Check if the output folder exists
                var outputFolder = Path.Combine(folderPath, "output");
                _logger?.LogInfo($"Checking for output folder: {outputFolder}");

                if (!Directory.Exists(outputFolder))
                {
                    _logger?.LogWarning($"Validation failed - Output folder does not exist: {outputFolder}");
                    return false;
                }

                _settings.ComfyUIFolderPath = folderPath;
                _settings.OutputFolderPath = outputFolder;

                // Remember how to launch this local install so the app can auto-start ComfyUI
                // later instead of prompting the user to locate it.
                if (string.IsNullOrEmpty(_settings.ComfyUIRestartScriptPath)
                    || !File.Exists(_settings.ComfyUIRestartScriptPath))
                {
                    var script = TryDetectComfyUIStartScript();
                    if (script != null)
                    {
                        _settings.ComfyUIRestartScriptPath = script;
                        _logger?.LogInfo($"Auto-configured ComfyUI start script: {script}");
                    }
                }

                _logger?.LogInfo($"Saving settings to: {_settingsFilePath}");
                SaveSettings(_settings);

                _logger?.LogInfo("Settings saved successfully");
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, $"Exception in ValidateAndSetComfyUIFolder");
                throw;
            }
        }
    }
}
