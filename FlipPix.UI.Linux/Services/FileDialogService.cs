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
    private static string? _lastUsedDirectory;
    private static TopLevel? _topLevel;

    public static void SetTopLevel(TopLevel topLevel) => _topLevel = topLevel;

    private static string? EffectiveDirectory(string? hint)
        => _lastUsedDirectory ?? (string.IsNullOrEmpty(hint) ? null : hint);

    private static void RememberDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            _lastUsedDirectory = dir;
    }

    private static async Task<IStorageFolder?> GetStartFolderAsync(string? hint)
    {
        if (_topLevel == null) return null;
        var dir = EffectiveDirectory(hint);
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

    public async Task<string?> OpenFileDialogAsync(string title, string filter, string? initialDirectory = null)
    {
        if (_topLevel == null) return null;

        if (IsImageFilter(filter) && _topLevel is Window ownerWindow)
        {
            var picker = new ImagePickerWindow(EffectiveDirectory(initialDirectory));
            var result = await picker.ShowDialog<string?>(ownerWindow);
            if (!string.IsNullOrEmpty(result)) RememberDirectory(result);
            return result;
        }

        var files = await _topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = ParseFilter(filter),
            SuggestedStartLocation = await GetStartFolderAsync(initialDirectory)
        });
        var path = files.FirstOrDefault()?.Path.LocalPath;
        RememberDirectory(path);
        return path;
    }

    public async Task<string[]> OpenFilesDialogAsync(string title, string filter, string? initialDirectory = null)
    {
        if (_topLevel == null) return Array.Empty<string>();
        var files = await _topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter = ParseFilter(filter),
            SuggestedStartLocation = await GetStartFolderAsync(initialDirectory)
        });
        var paths = files.Select(f => f.Path.LocalPath).ToArray();
        if (paths.Length > 0) RememberDirectory(paths[0]);
        return paths;
    }

    public async Task<string?> SaveFileDialogAsync(string title, string filter, string defaultFileName, string? initialDirectory = null)
    {
        if (_topLevel == null) return null;
        var file = await _topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            FileTypeChoices = ParseFilter(filter),
            SuggestedFileName = defaultFileName,
            SuggestedStartLocation = await GetStartFolderAsync(initialDirectory)
        });
        var path = file?.Path.LocalPath;
        RememberDirectory(path);
        return path;
    }

    public async Task<string?> OpenFolderDialogAsync(string title, string? initialDirectory = null, bool showNewFolderButton = false)
    {
        if (_topLevel == null) return null;
        var folders = await _topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = await GetStartFolderAsync(initialDirectory)
        });
        var path = folders.FirstOrDefault()?.Path.LocalPath;
        RememberDirectory(path);
        return path;
    }
}
