using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;

namespace FlipPix.UI.Services
{
    public interface IPromptService
    {
        List<SavedPrompt> LoadPrompts(string promptType);
        void SavePrompts(string promptType, List<SavedPrompt> prompts);
        SavedPrompt? GetPromptById(string promptType, string id);
        void SavePrompt(string promptType, SavedPrompt prompt);
        void DeletePrompt(string promptType, string id);
        string GenerateAutoName(string promptText, List<SavedPrompt> existingPrompts);
    }

    public class PromptService : IPromptService
    {
        private readonly IAppLogger _logger;
        private readonly ConcurrentDictionary<string, List<SavedPrompt>> _promptCache = new();
        private readonly object _fileLock = new();

        public PromptService(IAppLogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public List<SavedPrompt> LoadPrompts(string promptType)
        {
            return _promptCache.GetOrAdd(promptType, LoadFromFile);
        }

        private List<SavedPrompt> LoadFromFile(string promptType)
        {
            try
            {
                var promptHistoryPath = GetPromptHistoryPath(promptType);

                if (File.Exists(promptHistoryPath))
                {
                    var json = File.ReadAllText(promptHistoryPath);
                    var prompts = JsonSerializer.Deserialize<List<SavedPrompt>>(json);
                    if (prompts != null)
                    {
                        var sortedPrompts = prompts.OrderByDescending(p => p.LastUsed).ToList();
                        _logger.LogInfo($"Loaded {sortedPrompts.Count} saved prompts for {promptType}");
                        return sortedPrompts;
                    }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(promptHistoryPath)!);
                _logger.LogInfo($"Created new prompt history file for {promptType}");
                return new List<SavedPrompt>();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error loading saved prompts for {promptType}: {ex.Message}");
                return new List<SavedPrompt>();
            }
        }

        public void SavePrompts(string promptType, List<SavedPrompt> prompts)
        {
            try
            {
                var promptHistoryPath = GetPromptHistoryPath(promptType);
                Directory.CreateDirectory(Path.GetDirectoryName(promptHistoryPath)!);

                var json = JsonSerializer.Serialize(prompts, new JsonSerializerOptions { WriteIndented = true });

                // Use lock to prevent concurrent file writes
                lock (_fileLock)
                {
                    File.WriteAllText(promptHistoryPath, json);
                }

                // Update cache
                _promptCache.AddOrUpdate(promptType, prompts, (key, old) => prompts);
                _logger.LogInfo($"Saved {prompts.Count} prompts for {promptType}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error saving prompts for {promptType}: {ex.Message}");
            }
        }

        public SavedPrompt? GetPromptById(string promptType, string id)
        {
            var prompts = LoadPrompts(promptType);
            return prompts.FirstOrDefault(p => p.Id == id);
        }

        public void SavePrompt(string promptType, SavedPrompt prompt)
        {
            var prompts = LoadPrompts(promptType);

            // Check if a prompt with this name already exists
            var existingPrompt = prompts.FirstOrDefault(p => p.Name.Equals(prompt.Name, StringComparison.OrdinalIgnoreCase));
            if (existingPrompt != null)
            {
                // Update existing prompt
                existingPrompt.Prompt = prompt.Prompt;
                existingPrompt.AspectRatioIndex = prompt.AspectRatioIndex;
                existingPrompt.Steps = prompt.Steps;
                existingPrompt.Cfg = prompt.Cfg;
                existingPrompt.Seed = prompt.Seed;
                existingPrompt.Denoise = prompt.Denoise;
                existingPrompt.LastUsed = DateTime.Now;
                existingPrompt.UseCount++;
            }
            else
            {
                // Add new prompt
                prompts.Insert(0, prompt);
            }

            // Keep only the most recent 50 prompts
            if (prompts.Count > 50)
            {
                prompts = prompts.Take(50).ToList();
            }

            SavePrompts(promptType, prompts);
        }

        public void DeletePrompt(string promptType, string id)
        {
            var prompts = LoadPrompts(promptType);
            var promptToDelete = prompts.FirstOrDefault(p => p.Id == id);

            if (promptToDelete != null)
            {
                prompts.Remove(promptToDelete);
                SavePrompts(promptType, prompts);
                _logger.LogInfo($"Deleted prompt '{promptToDelete.Name}' for {promptType}");
            }
        }

        public string GenerateAutoName(string promptText, List<SavedPrompt> existingPrompts)
        {
            if (string.IsNullOrWhiteSpace(promptText))
                return "Untitled Prompt";

            // Take first 40 characters of the prompt
            var shortPrompt = promptText.Length > 40 ? promptText.Substring(0, 40) + "..." : promptText;

            // Clean up the name - remove newlines and multiple spaces
            var cleanName = shortPrompt.Replace("\n", " ").Replace("\r", "");
            while (cleanName.Contains("  "))
            {
                cleanName = cleanName.Replace("  ", " ");
            }

            // Capitalize first letter
            if (cleanName.Length > 0)
            {
                cleanName = char.ToUpper(cleanName[0]) + cleanName.Substring(1);
            }

            // Check if this name already exists
            var baseName = cleanName;
            var counter = 1;
            while (existingPrompts.Any(p => p.Name.Equals(cleanName, StringComparison.OrdinalIgnoreCase)))
            {
                cleanName = $"{baseName} ({counter})";
                counter++;
            }

            return cleanName;
        }

        private string GetPromptHistoryPath(string promptType)
        {
            // Store prompts in %APPDATA%/FlipPix/prompts for persistence
            var promptsFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FlipPix",
                "prompts"
            );
            Directory.CreateDirectory(promptsFolder);
            return Path.Combine(promptsFolder, $"prompt_history_{promptType}.json");
        }
    }
}