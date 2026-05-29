using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Linux.Models;
using FlipPix.UI.Linux.Services;

namespace FlipPix.UI.Linux.ViewModels
{
    public abstract class BasePromptViewModel : ObservableObject
    {
        protected readonly IPromptService _promptService;
        protected readonly IAppLogger _logger;
        protected readonly string _promptType;

        private List<SavedPrompt> _savedPrompts = new();
        private SavedPrompt? _selectedSavedPrompt;

        protected BasePromptViewModel(IPromptService promptService, IAppLogger logger, string promptType)
        {
            _promptService = promptService ?? throw new ArgumentNullException(nameof(promptService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _promptType = promptType ?? throw new ArgumentNullException(nameof(promptType));

            // Initialize commands
            SavePromptCommand = new RelayCommand(SavePrompt, CanSavePrompt);
            DeletePromptCommand = new RelayCommand(DeletePrompt, CanDeletePrompt);

            LoadSavedPrompts();
        }

        // Prompt History Properties
        public virtual List<SavedPrompt> SavedPrompts
        {
            get => _savedPrompts;
            set => _savedPrompts = value ?? new List<SavedPrompt>();
        }

        public virtual SavedPrompt? SelectedSavedPrompt
        {
            get => _selectedSavedPrompt;
            set
            {
                if (SetProperty(ref _selectedSavedPrompt, value) && value != null)
                {
                    LoadPromptFromSaved(value);
                }
            }
        }

        // Commands
        public virtual ICommand SavePromptCommand { get; protected set; }
        public virtual ICommand DeletePromptCommand { get; protected set; }

        // Abstract properties that derived classes must implement
        public abstract string CurrentPromptText { get; }
        public abstract int AspectRatioIndex { get; set; }
        public abstract int Steps { get; set; }
        public abstract double Cfg { get; set; }
        public abstract long Seed { get; set; }
        public abstract double Denoise { get; set; }

        // Optional: Override this if the ViewModel has additional data to save
        public virtual Dictionary<string, object> GetAdditionalPromptData()
        {
            return new Dictionary<string, object>();
        }

        // Optional: Override this if the ViewModel has additional data to load
        public virtual void LoadAdditionalPromptData(Dictionary<string, object> data)
        {
            // Default implementation does nothing
        }

        protected virtual void LoadSavedPrompts()
        {
            try
            {
                SavedPrompts = _promptService.LoadPrompts(_promptType);
                _logger.LogInfo($"Loaded {SavedPrompts.Count} saved prompts for {_promptType}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error loading saved prompts for {_promptType}: {ex.Message}");
                SavedPrompts = new List<SavedPrompt>();
            }
        }

        public virtual bool CanSavePrompt()
        {
            return !string.IsNullOrWhiteSpace(CurrentPromptText);
        }

        public virtual void SavePrompt()
        {
            try
            {
                var autoName = _promptService.GenerateAutoName(CurrentPromptText, SavedPrompts);

                var newPrompt = new SavedPrompt
                {
                    Name = autoName,
                    Prompt = CurrentPromptText,
                    AspectRatioIndex = AspectRatioIndex,
                    Steps = Steps,
                    Cfg = Cfg,
                    Seed = Seed,
                    Denoise = Denoise,
                    CreatedAt = DateTime.Now,
                    LastUsed = DateTime.Now,
                    UseCount = 1
                };

                // Save additional data if provided
                var additionalData = GetAdditionalPromptData();
                if (additionalData.Any())
                {
                    newPrompt.AdditionalData = additionalData;
                }

                _promptService.SavePrompt(_promptType, newPrompt);

                // Reload prompts to get updated list
                LoadSavedPrompts();

                // Select the saved prompt
                var savedPrompt = SavedPrompts.FirstOrDefault(p => p.Id == newPrompt.Id);
                if (savedPrompt != null)
                {
                    SelectedSavedPrompt = savedPrompt;
                }

                OnPromptSaved(autoName);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error saving prompt: {ex.Message}");
                OnPromptError("Error saving prompt");
            }
        }

        public virtual bool CanDeletePrompt()
        {
            return SelectedSavedPrompt != null;
        }

        public virtual void DeletePrompt()
        {
            if (SelectedSavedPrompt == null) return;

            try
            {
                var promptName = SelectedSavedPrompt.Name;
                var promptId = SelectedSavedPrompt.Id;

                _promptService.DeletePrompt(_promptType, promptId);

                // Reload prompts
                LoadSavedPrompts();

                // Clear selection
                SelectedSavedPrompt = null;

                OnPromptDeleted(promptName);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error deleting prompt: {ex.Message}");
                OnPromptError("Error deleting prompt");
            }
        }

        protected virtual void LoadPromptFromSaved(SavedPrompt savedPrompt)
        {
            try
            {
                // This method should be implemented by derived classes
                // to set their specific properties
                OnPromptLoaded(savedPrompt);

                // Update usage statistics and move to top
                savedPrompt.LastUsed = DateTime.Now;
                savedPrompt.UseCount++;

                // Update in service
                _promptService.SavePrompt(_promptType, savedPrompt);

                // Reload to get updated order
                LoadSavedPrompts();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error loading prompt: {ex.Message}");
                OnPromptError("Error loading prompt");
            }
        }

        // Abstract methods for derived classes to implement
        protected abstract void OnPromptSaved(string promptName);
        protected abstract void OnPromptDeleted(string promptName);
        protected abstract void OnPromptLoaded(SavedPrompt savedPrompt);
        protected abstract void OnPromptError(string error);
    }
}