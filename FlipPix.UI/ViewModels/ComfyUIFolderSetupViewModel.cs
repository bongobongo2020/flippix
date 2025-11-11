using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using FlipPix.Core.Services;
using Forms = System.Windows.Forms;

namespace FlipPix.UI.ViewModels
{
    public class ComfyUIFolderSetupViewModel : INotifyPropertyChanged
    {
        private readonly SettingsService _settingsService;
        private string _folderPath = string.Empty;
        private string _validationMessage = string.Empty;
        private System.Windows.Media.Brush _validationMessageColor = System.Windows.Media.Brushes.Red;
        private bool _canSave = false;
        private string _outputFolderInfo = string.Empty;
        private System.Windows.Visibility _outputFolderInfoVisibility = System.Windows.Visibility.Collapsed;

        public event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler<bool>? CloseRequested;

        public ComfyUIFolderSetupViewModel(SettingsService settingsService)
        {
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));

            BrowseFolderCommand = new DelegateCommand(BrowseFolder);
            SaveCommand = new DelegateCommand(Save, () => CanSave);
            CancelCommand = new DelegateCommand(Cancel);

            // Pre-fill with existing path if available
            if (!string.IsNullOrEmpty(_settingsService.Settings.ComfyUIFolderPath))
            {
                FolderPath = _settingsService.Settings.ComfyUIFolderPath;
            }
        }

        public string FolderPath
        {
            get => _folderPath;
            set
            {
                if (_folderPath != value)
                {
                    _folderPath = value;
                    OnPropertyChanged();
                    ValidateFolderPath();
                }
            }
        }

        public string ValidationMessage
        {
            get => _validationMessage;
            set
            {
                _validationMessage = value;
                OnPropertyChanged();
            }
        }

        public System.Windows.Media.Brush ValidationMessageColor
        {
            get => _validationMessageColor;
            set
            {
                _validationMessageColor = value;
                OnPropertyChanged();
            }
        }

        public bool CanSave
        {
            get => _canSave;
            set
            {
                _canSave = value;
                OnPropertyChanged();
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public string OutputFolderInfo
        {
            get => _outputFolderInfo;
            set
            {
                _outputFolderInfo = value;
                OnPropertyChanged();
            }
        }

        public System.Windows.Visibility OutputFolderInfoVisibility
        {
            get => _outputFolderInfoVisibility;
            set
            {
                _outputFolderInfoVisibility = value;
                OnPropertyChanged();
            }
        }

        public ICommand BrowseFolderCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand CancelCommand { get; }

        private void BrowseFolder()
        {
            using (var folderDialog = new Forms.FolderBrowserDialog())
            {
                folderDialog.Description = "Select the root folder of your ComfyUI installation";
                folderDialog.ShowNewFolderButton = false;

                if (!string.IsNullOrEmpty(FolderPath) && Directory.Exists(FolderPath))
                {
                    folderDialog.SelectedPath = FolderPath;
                }

                if (folderDialog.ShowDialog() == Forms.DialogResult.OK)
                {
                    FolderPath = folderDialog.SelectedPath;
                }
            }
        }

        private void ValidateFolderPath()
        {
            if (string.IsNullOrWhiteSpace(FolderPath))
            {
                ValidationMessage = "Please select a folder.";
                ValidationMessageColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 100, 100));
                CanSave = false;
                OutputFolderInfoVisibility = System.Windows.Visibility.Collapsed;
                return;
            }

            if (!Directory.Exists(FolderPath))
            {
                ValidationMessage = "The selected folder does not exist.";
                ValidationMessageColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 100, 100));
                CanSave = false;
                OutputFolderInfoVisibility = System.Windows.Visibility.Collapsed;
                return;
            }

            var outputFolder = Path.Combine(FolderPath, "output");
            if (!Directory.Exists(outputFolder))
            {
                ValidationMessage = "Error: 'output' folder not found in the selected ComfyUI folder.";
                ValidationMessageColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 100, 100));
                CanSave = false;
                OutputFolderInfoVisibility = System.Windows.Visibility.Collapsed;
                return;
            }

            ValidationMessage = "Folder validated successfully!";
            ValidationMessageColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 255, 100));
            CanSave = true;
            OutputFolderInfo = $"Output folder: {outputFolder}";
            OutputFolderInfoVisibility = System.Windows.Visibility.Visible;
        }

        private void Save()
        {
            if (_settingsService.ValidateAndSetComfyUIFolder(FolderPath))
            {
                CloseRequested?.Invoke(this, true);
            }
            else
            {
                ValidationMessage = "Failed to save settings. Please try again.";
                ValidationMessageColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 100, 100));
            }
        }

        private void Cancel()
        {
            CloseRequested?.Invoke(this, false);
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    // Simple command implementation to avoid conflicts with RelayCommand
    public class DelegateCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool>? _canExecute;

        public DelegateCommand(Action execute, Func<bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged
        {
            add => System.Windows.Input.CommandManager.RequerySuggested += value;
            remove => System.Windows.Input.CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

        public void Execute(object? parameter) => _execute();
    }
}
