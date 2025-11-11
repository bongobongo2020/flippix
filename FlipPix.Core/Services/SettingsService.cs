using System;
using System.IO;
using System.Text.Json;
using FlipPix.Core.Models;

namespace FlipPix.Core.Services
{
    public class SettingsService
    {
        private readonly string _settingsFilePath;
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

        public ComfyUISettings LoadSettings()
        {
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

            return new ComfyUISettings();
        }

        public void SaveSettings(ComfyUISettings settings)
        {
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
        }

        public bool IsComfyUIFolderConfigured()
        {
            return !string.IsNullOrEmpty(_settings.ComfyUIFolderPath) &&
                   Directory.Exists(_settings.ComfyUIFolderPath);
        }

        public bool ValidateAndSetComfyUIFolder(string folderPath)
        {
            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
            {
                return false;
            }

            // Check if the output folder exists
            var outputFolder = Path.Combine(folderPath, "output");
            if (!Directory.Exists(outputFolder))
            {
                return false;
            }

            _settings.ComfyUIFolderPath = folderPath;
            _settings.OutputFolderPath = outputFolder;
            SaveSettings(_settings);
            return true;
        }
    }
}
