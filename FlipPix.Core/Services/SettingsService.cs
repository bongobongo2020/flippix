using System;
using System.Collections.Generic;
using System.IO;
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
