using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using FlipPix.UI.Linux.Windows;

namespace FlipPix.UI.Services;

public class FileDialogService : IFileDialogService
{
    private readonly FlipPix.Core.Services.SettingsService _settingsService;

    public FileDialogService(FlipPix.Core.Services.SettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    /// <summary>
    /// The window a dialog belongs to, resolved per call: the focused window, else the main
    /// window. Resolving it here (rather than having each window register itself) means no
    /// window can be left out and silently turn every browse button into a no-op.
    /// </summary>
    private static TopLevel? _topLevel
        => Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow
            : null;

    /// <summary>
    /// Where a browse button should open. The folder last used for this <paramref name="persistKey"/>
    /// wins, so each button reopens where it was, and the choice survives a restart because
    /// SettingsService writes it to settings.json.
    /// </summary>
    private string? EffectiveDirectory(string? hint, string? persistKey)
        => _settingsService.GetLastBrowseFolder(persistKey)
           ?? (string.IsNullOrEmpty(hint) ? null : hint);

    /// <summary>
    /// Saves the folder in the background. The existence checks here and in SettingsService are
    /// round trips to the disk, and on a ChromeOS share each can block for seconds, so they must
    /// not run on the UI thread.
    /// </summary>
    private void RememberDirectory(string? path, string? persistKey)
    {
        if (string.IsNullOrEmpty(path)) return;
        _ = Task.Run(() =>
        {
            try
            {
                var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    _settingsService.SetLastBrowseFolder(persistKey, dir);
            }
            catch
            {
                // Only the remembered start folder is lost.
            }
        });
    }

    private async Task<IStorageFolder?> GetStartFolderAsync(string? hint, string? persistKey)
    {
        var topLevel = _topLevel;
        if (topLevel == null) return null;
        var dir = await Task.Run(() =>
        {
            var d = EffectiveDirectory(hint, persistKey);
            return string.IsNullOrEmpty(d) || !Directory.Exists(d) ? null : d;
        });
        if (dir == null) return null;
        return await topLevel.StorageProvider.TryGetFolderFromPathAsync(dir);
    }

    private static List<FilePickerFileType> ParseFilter(string filter)
    {
        var parts = filter.Split('|');
        var result = new List<FilePickerFileType>();
        for (int i = 0; i + 1 < parts.Length; i += 2)
        {
            var name = parts[i];
            var patterns = parts[i + 1].Split(';').Select(p => p.Trim()).ToArray();
            result.Add(new FilePickerFileType(name) { Patterns = patterns });
        }
        return result;
    }

    private static bool IsImageFilter(string filter)
    {
        var lower = filter.ToLowerInvariant();
        return lower.Contains(".png") || lower.Contains(".jpg") || lower.Contains(".jpeg")
            || lower.Contains(".webp") || lower.Contains(".bmp");
    }

    public async Task<string?> OpenFileDialogAsync(string title, string filter, string? initialDirectory = null, string? persistKey = null)
    {
        if (_topLevel == null) return null;

        if (IsImageFilter(filter) && _topLevel is Window ownerWindow)
        {
            // Unchecked: the picker lists the folder in the background and falls back to the home
            // folder if it has gone, so the window opens without waiting on the disk.
            var start = _settingsService.PeekLastBrowseFolder(persistKey)
                        ?? (string.IsNullOrEmpty(initialDirectory) ? null : initialDirectory);
            var picker = new ImagePickerWindow(start);
            var result = await picker.ShowDialog<string?>(ownerWindow);
            if (!string.IsNullOrEmpty(result)) RememberDirectory(result, persistKey);
            return result;
        }

        var files = await _topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = ParseFilter(filter),
            SuggestedStartLocation = await GetStartFolderAsync(initialDirectory, persistKey)
        });
        var path = files.FirstOrDefault()?.Path.LocalPath;
        RememberDirectory(path, persistKey);
        return path;
    }

    public async Task<string[]> OpenFilesDialogAsync(string title, string filter, string? initialDirectory = null, string? persistKey = null)
    {
        if (_topLevel == null) return Array.Empty<string>();
        var files = await _topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter = ParseFilter(filter),
            SuggestedStartLocation = await GetStartFolderAsync(initialDirectory, persistKey)
        });
        var paths = files.Select(f => f.Path.LocalPath).ToArray();
        if (paths.Length > 0) RememberDirectory(paths[0], persistKey);
        return paths;
    }

    public async Task<string?> SaveFileDialogAsync(string title, string filter, string defaultFileName, string? initialDirectory = null, string? persistKey = null)
    {
        if (_topLevel == null) return null;
        var file = await _topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            FileTypeChoices = ParseFilter(filter),
            SuggestedFileName = defaultFileName,
            SuggestedStartLocation = await GetStartFolderAsync(initialDirectory, persistKey)
        });
        var path = file?.Path.LocalPath;
        RememberDirectory(path, persistKey);
        return path;
    }

    public async Task<string?> OpenFolderDialogAsync(string title, string? initialDirectory = null, bool showNewFolderButton = false, string? persistKey = null)
    {
        if (_topLevel == null) return null;
        var folders = await _topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = await GetStartFolderAsync(initialDirectory, persistKey)
        });
        var path = folders.FirstOrDefault()?.Path.LocalPath;
        RememberDirectory(path, persistKey);
        return path;
    }
}
