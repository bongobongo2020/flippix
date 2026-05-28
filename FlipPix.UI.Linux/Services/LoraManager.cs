using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlipPix.Core.Interfaces;
using FlipPix.Core.Services;
using YamlDotNet.Serialization;

namespace FlipPix.UI.Linux.Services
{
    /// <summary>
    /// Service for managing LoRA model discovery and path resolution
    /// </summary>
    public class LoraManager
    {
        private readonly FlipPix.Core.Services.SettingsService _settingsService;
        private readonly IAppLogger _logger;

        public LoraManager(FlipPix.Core.Services.SettingsService settingsService, IAppLogger logger)
        {
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Get list of available LoRA model names from the ComfyUI models directory
        /// </summary>
        /// <param name="includeExtensions">Whether to include file extensions in the result</param>
        /// <returns>List of LoRA model names</returns>
        public List<string> GetAvailableLoras(bool includeExtensions = false)
        {
            var loras = new List<string>();

            try
            {
                var loraPath = ResolveLoraPath();
                if (string.IsNullOrEmpty(loraPath) || !Directory.Exists(loraPath))
                {
                    _logger.LogInfo($"LoRA directory not found: {loraPath ?? "null"}");
                    return loras;
                }

                // Get all LoRA files (.safetensors, .pt, .pth, .bin)
                var extensions = new[] { "*.safetensors", "*.pt", "*.pth", "*.bin" };
                var files = extensions.SelectMany(ext => Directory.GetFiles(loraPath, ext, SearchOption.AllDirectories));

                foreach (var file in files)
                {
                    // Get relative path from base loras folder (e.g., "zimage\amateur_photography_zimage_v1")
                    var relativePath = Path.GetRelativePath(loraPath, file);
                    var relativePathNoExt = Path.ChangeExtension(relativePath, null);

                    // Check for paired files (e.g., model.safetensors and model.png)
                    var hasPreview = File.Exists(Path.ChangeExtension(file, ".png")) ||
                                   File.Exists(Path.Combine(Path.GetDirectoryName(file) ?? "", Path.GetFileNameWithoutExtension(file) + ".png"));

                    // Normalize path separators to backslash for ComfyUI compatibility
                    var normalizedPath = relativePath.Replace('/', '\\');
                    var normalizedPathNoExt = relativePathNoExt.Replace('/', '\\');

                    // Add either filename with extension or without
                    loras.Add(includeExtensions ? normalizedPath : normalizedPathNoExt);
                }

                loras = loras.Distinct().OrderBy(n => n).ToList();
                _logger.LogInfo($"Found {loras.Count} LoRA models in {loraPath}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error loading LoRA models: {ex.Message}");
            }

            return loras;
        }

        /// <summary>
        /// Resolve the full path to a LoRA model by name
        /// </summary>
        /// <param name="loraName">Name of the LoRA model (with or without extension)</param>
        /// <returns>Full path to the LoRA model, or null if not found</returns>
        public string? ResolveLoraPath(string loraName)
        {
            if (string.IsNullOrWhiteSpace(loraName))
            {
                return null;
            }

            try
            {
                var loraDir = ResolveLoraPath();
                if (string.IsNullOrEmpty(loraDir) || !Directory.Exists(loraDir))
                {
                    return null;
                }

                // If loraName already has an extension, try exact match first
                if (Path.HasExtension(loraName))
                {
                    var fullPath = Path.Combine(loraDir, loraName);
                    if (File.Exists(fullPath))
                    {
                        return fullPath;
                    }
                }

                // Try with various extensions
                var extensions = new[] { ".safetensors", ".pt", ".pth", ".bin" };

                // Handle subfolder paths (e.g., "zimage\lora_name")
                string pathWithoutExt;
                if (loraName.Contains('\\') || loraName.Contains('/'))
                {
                    // For paths with subfolders, just remove the extension
                    pathWithoutExt = Path.ChangeExtension(loraName, null);
                }
                else
                {
                    // For simple filenames, use GetFileNameWithoutExtension
                    pathWithoutExt = Path.GetFileNameWithoutExtension(loraName) ?? loraName;
                }

                foreach (var ext in extensions)
                {
                    var fullPath = Path.Combine(loraDir, pathWithoutExt + ext);
                    if (File.Exists(fullPath))
                    {
                        return fullPath;
                    }
                }

                _logger.LogInfo($"LoRA model not found: {loraName}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error resolving LoRA path for {loraName}: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Resolve the LoRA models directory path using ComfyUI configuration
        /// </summary>
        /// <returns>Full path to the LoRA models directory, or null if not found</returns>
        public string? ResolveLoraPath()
        {
            try
            {
                var comfyUIPath = _settingsService.Settings?.ComfyUIFolderPath;
                if (string.IsNullOrEmpty(comfyUIPath))
                {
                    _logger.LogInfo("ComfyUI installation path not configured");
                    return null;
                }

                // Try to find LoRA path from extra_model_paths.yaml first
                var extraModelPathsFile = Path.Combine(comfyUIPath, "extra_model_paths.yaml");
                if (File.Exists(extraModelPathsFile))
                {
                    var loraPath = GetLoraPathFromExtraModelPaths(extraModelPathsFile);
                    if (!string.IsNullOrEmpty(loraPath) && Directory.Exists(loraPath))
                    {
                        _logger.LogInfo($"Found LoRA path from extra_model_paths.yaml: {loraPath}");
                        return loraPath;
                    }
                }

                // Fall back to default path
                var defaultLoraPath = Path.Combine(comfyUIPath, "models", "loras");
                if (Directory.Exists(defaultLoraPath))
                {
                    _logger.LogInfo($"Using default LoRA path: {defaultLoraPath}");
                    return defaultLoraPath;
                }

                _logger.LogInfo($"Default LoRA path not found: {defaultLoraPath}");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error resolving LoRA path: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Extract LoRA path from extra_model_paths.yaml configuration
        /// </summary>
        /// <param name="yamlFilePath">Path to the extra_model_paths.yaml file</param>
        /// <returns>LoRA directory path if found, null otherwise</returns>
        private string? GetLoraPathFromExtraModelPaths(string yamlFilePath)
        {
            try
            {
                var yamlContent = File.ReadAllText(yamlFilePath);
                var deserializer = new DeserializerBuilder().Build();
                var yamlData = deserializer.Deserialize<Dictionary<string, object>>(yamlContent);

                if (yamlData == null)
                {
                    return null;
                }

                string basePath = string.Empty;
                string lorasRelativePath = string.Empty;

                // Look for comfyui section with base_path and loras
                if (yamlData.ContainsKey("comfyui"))
                {
                    var comfyuiSectionObject = yamlData["comfyui"];
                    var comfyuiSection = comfyuiSectionObject as Dictionary<object, object>;

                    if (comfyuiSection != null)
                    {
                        var comfyuiStringDict = new Dictionary<string, object>();
                        foreach (var kvp in comfyuiSection)
                        {
                            if (kvp.Key != null)
                            {
                                comfyuiStringDict[kvp.Key.ToString() ?? string.Empty] = kvp.Value;
                            }
                        }

                        if (comfyuiStringDict.ContainsKey("base_path"))
                        {
                            basePath = comfyuiStringDict["base_path"]?.ToString() ?? string.Empty;
                        }

                        if (comfyuiStringDict.ContainsKey("loras"))
                        {
                            lorasRelativePath = comfyuiStringDict["loras"]?.ToString() ?? string.Empty;
                        }
                    }
                }

                // Also check for direct loras key at root level
                if (string.IsNullOrEmpty(lorasRelativePath) && yamlData.ContainsKey("loras"))
                {
                    lorasRelativePath = yamlData["loras"]?.ToString() ?? string.Empty;
                }

                if (!string.IsNullOrEmpty(lorasRelativePath))
                {
                    string fullLoraPath;
                    if (!string.IsNullOrEmpty(basePath))
                    {
                        fullLoraPath = Path.Combine(basePath, lorasRelativePath);
                    }
                    else
                    {
                        fullLoraPath = lorasRelativePath;
                    }

                    fullLoraPath = fullLoraPath.Replace('/', Path.DirectorySeparatorChar);

                    if (Directory.Exists(fullLoraPath))
                    {
                        return fullLoraPath;
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error reading extra_model_paths.yaml: {ex.Message}");
                return null;
            }
        }
    }
}
