using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using FlipPix.UI.Linux.Windows;

namespace FlipPix.UI.Linux.Services;

public class FileDialogService : IFileDialogService
{
    private static TopLevel? _topLevel;

    private readonly FlipPix.Core.Services.SettingsService _settingsService;

    public FileDialogService(FlipPix.Core.Services.SettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    public static void SetTopLevel(TopLevel topLevel) => _topLevel = topLevel;

    /// <summary>
    /// Where a browse button should open. The folder last used for this <paramref name="persistKey"/>
    /// wins, so each button reopens where it was, and the choice survives a restart because
    /// SettingsService writes it to settings.json.
    /// </summary>
    private string? EffectiveDirectory(string? hint, string? persistKey)
        => _settingsService.GetLastBrowseFolder(persistKey)
           ?? (string.IsNullOrEmpty(hint) ? null : hint);

    private void RememberDirectory(string? path, string? persistKey)
    {
        if (string.IsNullOrEmpty(path)) return;
        var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            _settingsService.SetLastBrowseFolder(persistKey, dir);
    }

    private async Task<IStorageFolder?> GetStartFolderAsync(string? hint, string? persistKey)
    {
        if (_topLevel == null) return null;
        var dir = EffectiveDirectory(hint, persistKey);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
        return await _topLevel.StorageProvider.TryGetFolderFromPathAsync(dir);
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
            var picker = new ImagePickerWindow(EffectiveDirectory(initialDirectory, persistKey));
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
