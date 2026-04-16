using System;
using System.IO;
using System.Threading.Tasks;
using Forms = System.Windows.Forms;

namespace FlipPix.UI.Services
{
    /// <summary>
    /// Default implementation of IFileDialogService using standard Windows dialogs.
    /// Remembers the last browsed folder across all callers for the lifetime of the app.
    /// </summary>
    public class FileDialogService : IFileDialogService
    {
        private static string? _lastUsedDirectory;

        private static string? EffectiveDirectory(string? callerHint)
            => _lastUsedDirectory ?? (string.IsNullOrEmpty(callerHint) ? null : callerHint);

        private static void RememberDirectory(string? path)
        {
            if (string.IsNullOrEmpty(path)) return;
            var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                _lastUsedDirectory = dir;
        }

        public Task<string?> OpenFileDialogAsync(string title, string filter, string? initialDirectory = null)
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

                var startDir = EffectiveDirectory(initialDirectory);
                if (!string.IsNullOrEmpty(startDir) && Directory.Exists(startDir))
                    dialog.InitialDirectory = startDir;

                if (dialog.ShowDialog() != true) return null;
                RememberDirectory(dialog.FileName);
                return dialog.FileName;
            }).Task;
        }

        public Task<string[]> OpenFilesDialogAsync(string title, string filter, string? initialDirectory = null)
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

                var startDir = EffectiveDirectory(initialDirectory);
                if (!string.IsNullOrEmpty(startDir) && Directory.Exists(startDir))
                    dialog.InitialDirectory = startDir;

                if (dialog.ShowDialog() != true) return Array.Empty<string>();
                RememberDirectory(dialog.FileNames.Length > 0 ? dialog.FileNames[0] : null);
                return dialog.FileNames;
            }).Task;
        }

        public Task<string?> SaveFileDialogAsync(string title, string filter, string defaultFileName, string? initialDirectory = null)
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

                var startDir = EffectiveDirectory(initialDirectory);
                if (!string.IsNullOrEmpty(startDir) && Directory.Exists(startDir))
                    dialog.InitialDirectory = startDir;

                if (dialog.ShowDialog() != true) return null;
                RememberDirectory(dialog.FileName);
                return dialog.FileName;
            }).Task;
        }

        public Task<string?> OpenFolderDialogAsync(string title, string? initialDirectory = null, bool showNewFolderButton = false)
        {
            return System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                using var dialog = new Forms.FolderBrowserDialog
                {
                    Description = title,
                    ShowNewFolderButton = showNewFolderButton
                };

                var startDir = EffectiveDirectory(initialDirectory);
                if (!string.IsNullOrEmpty(startDir) && Directory.Exists(startDir))
                    dialog.SelectedPath = startDir;

                if (dialog.ShowDialog() != Forms.DialogResult.OK) return null;
                RememberDirectory(dialog.SelectedPath);
                return dialog.SelectedPath;
            }).Task;
        }
    }
}
