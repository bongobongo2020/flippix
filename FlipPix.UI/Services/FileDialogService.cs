using System;
using System.Threading.Tasks;
using System.Windows;
using Forms = System.Windows.Forms;
using Microsoft.Win32;

namespace FlipPix.UI.Services
{
    /// <summary>
    /// Default implementation of IFileDialogService using standard Windows dialogs.
    /// </summary>
    public class FileDialogService : IFileDialogService
    {
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

                if (!string.IsNullOrEmpty(initialDirectory) && System.IO.Directory.Exists(initialDirectory))
                    dialog.InitialDirectory = initialDirectory;

                return dialog.ShowDialog() == true ? dialog.FileName : null;
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

                if (!string.IsNullOrEmpty(initialDirectory) && System.IO.Directory.Exists(initialDirectory))
                    dialog.InitialDirectory = initialDirectory;

                return dialog.ShowDialog() == true ? dialog.FileNames : Array.Empty<string>();
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

                if (!string.IsNullOrEmpty(initialDirectory) && System.IO.Directory.Exists(initialDirectory))
                    dialog.InitialDirectory = initialDirectory;

                return dialog.ShowDialog() == true ? dialog.FileName : null;
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

                if (!string.IsNullOrEmpty(initialDirectory) && System.IO.Directory.Exists(initialDirectory))
                    dialog.SelectedPath = initialDirectory;

                return dialog.ShowDialog() == Forms.DialogResult.OK ? dialog.SelectedPath : null;
            }).Task;
        }
    }
}
