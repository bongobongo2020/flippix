using System;
using System.IO;
using System.Threading.Tasks;
using FlipPix.Core.Services;
using Forms = System.Windows.Forms;

namespace FlipPix.UI.Services
{
    /// <summary>
    /// Default implementation of IFileDialogService using standard Windows dialogs.
    /// Remembers the last browsed folder per <c>persistKey</c> (and a global fallback),
    /// persisted to settings.json so each browse button reopens where it was last used,
    /// surviving app restarts.
    /// </summary>
    public class FileDialogService : IFileDialogService
    {
        private readonly SettingsService _settingsService;

        public FileDialogService(SettingsService settingsService)
        {
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        }

        private string? EffectiveDirectory(string? callerHint, string? persistKey)
            => _settingsService.GetLastBrowseFolder(persistKey)
               ?? (string.IsNullOrEmpty(callerHint) ? null : callerHint);

        private void RememberDirectory(string? path, string? persistKey)
        {
            if (string.IsNullOrEmpty(path)) return;
            var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            _settingsService.SetLastBrowseFolder(persistKey, dir);
        }

        public Task<string?> OpenFileDialogAsync(string title, string filter, string? initialDirectory = null, string? persistKey = null)
        {
            return System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = title,
                    Filter = filter,
                    CheckFileExists = true,
                    CheckPathExists = true
                };

                var startDir = EffectiveDirectory(initialDirectory, persistKey);
                if (!string.IsNullOrEmpty(startDir) && Directory.Exists(startDir))
                    dialog.InitialDirectory = startDir;

                if (dialog.ShowDialog() != true) return null;
                RememberDirectory(dialog.FileName, persistKey);
                return dialog.FileName;
            }).Task;
        }

        public Task<string[]> OpenFilesDialogAsync(string title, string filter, string? initialDirectory = null, string? persistKey = null)
        {
            return System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = title,
                    Filter = filter,
                    CheckFileExists = true,
                    CheckPathExists = true,
                    Multiselect = true
                };

                var startDir = EffectiveDirectory(initialDirectory, persistKey);
                if (!string.IsNullOrEmpty(startDir) && Directory.Exists(startDir))
                    dialog.InitialDirectory = startDir;

                if (dialog.ShowDialog() != true) return Array.Empty<string>();
                RememberDirectory(dialog.FileNames.Length > 0 ? dialog.FileNames[0] : null, persistKey);
                return dialog.FileNames;
            }).Task;
        }

        public Task<string?> SaveFileDialogAsync(string title, string filter, string defaultFileName, string? initialDirectory = null, string? persistKey = null)
        {
            return System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = title,
                    Filter = filter,
                    FileName = defaultFileName,
                    CheckPathExists = true
                };

                var startDir = EffectiveDirectory(initialDirectory, persistKey);
                if (!string.IsNullOrEmpty(startDir) && Directory.Exists(startDir))
                    dialog.InitialDirectory = startDir;

                if (dialog.ShowDialog() != true) return null;
                RememberDirectory(dialog.FileName, persistKey);
                return dialog.FileName;
            }).Task;
        }

        public Task<string?> OpenFolderDialogAsync(string title, string? initialDirectory = null, bool showNewFolderButton = false, string? persistKey = null)
        {
            return System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                using var dialog = new Forms.FolderBrowserDialog
                {
                    Description = title,
                    ShowNewFolderButton = showNewFolderButton
                };

                var startDir = EffectiveDirectory(initialDirectory, persistKey);
                if (!string.IsNullOrEmpty(startDir) && Directory.Exists(startDir))
                    dialog.SelectedPath = startDir;

                if (dialog.ShowDialog() != Forms.DialogResult.OK) return null;
                RememberDirectory(dialog.SelectedPath, persistKey);
                return dialog.SelectedPath;
            }).Task;
        }
    }
}
