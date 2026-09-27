using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlipPix.Mobile.Services;
using FlipPix.Remote.Contracts;

namespace FlipPix.Mobile.ViewModels;

/// <summary>Something the full-screen viewer can show: a library file, a job's output, or a film.</summary>
public sealed record ViewerEntry
{
    public required string Kind { get; init; }
    public required string Title { get; init; }
    public string Subtitle { get; init; } = "";
    public string? ThumbUrl { get; init; }
    public string? PreviewUrl { get; init; }
    public string? FileUrl { get; init; }
    public string FileName { get; init; } = "flippix";
    /// <summary>Set when the file is in the computer's output folder: it can be read, described or animated.</summary>
    public string? LibraryId { get; init; }
    /// <summary>A film: its finished shots, played back to back.</summary>
    public IReadOnlyList<string>? Playlist { get; init; }
    /// <summary>What made it, when this phone asked for it.</summary>
    public string? Prompt { get; init; }
    public string? Look { get; init; }
    public string? Shape { get; init; }

    public bool IsVideo => Kind == MediaKinds.Video;

    public static ViewerEntry FromLibrary(LibraryItemVm item) => new()
    {
        Kind = item.Dto.Kind,
        Title = item.Dto.Name,
        Subtitle = Describe(item.Dto),
        ThumbUrl = item.Dto.ThumbUrl,
        PreviewUrl = item.Dto.PreviewUrl,
        FileUrl = item.Dto.FileUrl,
        FileName = item.Dto.Name,
        LibraryId = item.Dto.Id,
    };

    public static ViewerEntry FromJobItem(JobItemVm item) => new()
    {
        Kind = item.MediaKind,
        Title = item.Job.Title,
        Subtitle = item.Job.Kind switch
        {
            JobKinds.Image => Looks.NameOf(item.Job.Request.Look) + " look",
            JobKinds.Story => $"Shot {item.Number} of {item.Job.Items.Count}",
            _ => item.Job.LengthLabel,
        },
        ThumbUrl = item.ThumbUrl,
        PreviewUrl = item.PreviewUrl ?? item.ThumbUrl,
        FileUrl = item.FileUrl,
        FileName = $"flippix-{item.Job.Id}-{item.Number}{(item.IsVideo ? ".mp4" : ".png")}",
        LibraryId = item.LibraryId,
        Prompt = item.Job.Kind == JobKinds.Image ? item.Job.Request.Prompt : null,
        Look = item.Job.Kind == JobKinds.Image ? item.Job.Request.Look : null,
        Shape = item.Job.Kind == JobKinds.Image ? item.Job.Request.Shape : null,
    };

    private static string Describe(LibraryItemDto d)
    {
        var when = d.Modified.ToLocalTime();
        var day = LibraryViewModel.DayLabel(when.Date);
        var folder = d.Folder.Length == 0 ? "" : " · " + d.Folder;
        return $"{day}, {when.ToString("t", CultureInfo.CurrentCulture)}{folder}";
    }
}

/// <summary>
/// The full-screen viewer: one picture or video at a time, swiped through, with what can be done
/// with it next. Reading a picture (Describe, Make similar) goes to the computer's writing assistant.
/// </summary>
public sealed partial class ViewerViewModel : ObservableObject
{
    private IReadOnlyList<ViewerEntry> _entries = Array.Empty<ViewerEntry>();
    private int _load;

    /// <summary>Set by the shell: puts a prompt (and optionally a look and shape) in the Image page.</summary>
    public Action<string, string?, string?>? UsePromptHandler { get; set; }
    /// <summary>Set by the shell: adds a picture to the Video page's references.</summary>
    public Action<PictureSlot>? AnimateHandler { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVideo), nameof(IsImage), nameof(CanRead), nameof(CanAnimate), nameof(HasKnownPrompt),
        nameof(CanSave), nameof(Counter))]
    private ViewerEntry? _current;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private Bitmap? _picture;
    [ObservableProperty] private IReadOnlyList<string>? _videoSources;
    [ObservableProperty] private int _index;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowActions))]
    private bool _isWorking;

    [ObservableProperty] private string _workingText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowActions), nameof(HasDescription))]
    private string? _description;

    [ObservableProperty] private string? _notice;
    [ObservableProperty] private string? _knownPrompt;

    public bool IsVideo => Current?.IsVideo == true;
    public bool IsImage => Current is { IsVideo: false };
    public bool CanRead => IsImage && Current?.LibraryId != null;
    public bool CanAnimate => IsImage && Current?.LibraryId != null;
    public bool CanSave => Current?.FileUrl != null && Current.Playlist == null;
    public bool HasKnownPrompt => KnownPrompt != null;
    public bool HasDescription => Description != null;
    public bool ShowActions => !IsWorking && Description == null;
    public bool HasMany => _entries.Count > 1;
    public string Counter => _entries.Count > 1 ? $"{Index + 1} of {_entries.Count}" : "";

    public void Open(IReadOnlyList<ViewerEntry> entries, int index)
    {
        if (entries.Count == 0) return;
        _entries = entries;
        OnPropertyChanged(nameof(HasMany));
        Show(Math.Clamp(index, 0, entries.Count - 1));
        IsOpen = true;
    }

    [RelayCommand]
    public void Close()
    {
        IsOpen = false;
        VideoSources = null; // releases the native player
        Picture = null;
        Current = null;
        _load++;
    }

    [RelayCommand]
    public void Next()
    {
        if (Index + 1 < _entries.Count) Show(Index + 1);
    }

    [RelayCommand]
    public void Previous()
    {
        if (Index > 0) Show(Index - 1);
    }

    private void Show(int index)
    {
        var load = ++_load;
        Index = index;
        var entry = _entries[index];
        Current = entry;
        Description = null;
        Notice = null;
        IsWorking = false;
        KnownPrompt = entry.Prompt;
        OnPropertyChanged(nameof(HasKnownPrompt));
        Picture = null;

        if (entry.IsVideo)
        {
            VideoSources = entry.Playlist is { Count: > 0 } list
                ? list.Select(AppServices.Remote.MediaUrl).ToList()
                : entry.FileUrl != null ? new[] { AppServices.Remote.MediaUrl(entry.FileUrl) } : null;
        }
        else
        {
            VideoSources = null;
            _ = LoadPictureAsync(entry, load);
        }
        if (entry.Prompt == null && entry.LibraryId != null) _ = LoadRecipeAsync(entry, load);
    }

    /// <summary>The small thumbnail first (usually already in memory), then the sharp preview over it.</summary>
    private async Task LoadPictureAsync(ViewerEntry entry, int load)
    {
        if (entry.ThumbUrl != null)
        {
            var thumb = await ImageLoader.LoadAsync(entry.ThumbUrl, 360);
            if (load == _load && Picture == null) Picture = thumb;
        }
        var sharp = await ImageLoader.LoadAsync(entry.PreviewUrl ?? entry.ThumbUrl, 1440);
        if (load == _load && sharp != null) Picture = sharp;
    }

    /// <summary>If this phone made it, the computer still knows the prompt: offer to use it again.</summary>
    private async Task LoadRecipeAsync(ViewerEntry entry, int load)
    {
        try
        {
            var detail = await AppServices.Remote.LibraryDetailAsync(entry.LibraryId!);
            if (load != _load) return;
            if (detail.Prompt != null && detail.Look != null)
            {
                _entries = _entries.Select(e => e == entry ? e with { Prompt = detail.Prompt, Look = detail.Look, Shape = detail.Shape } : e).ToList();
                Current = _entries[Index];
                KnownPrompt = detail.Prompt;
                OnPropertyChanged(nameof(HasKnownPrompt));
            }
        }
        catch (RemoteException) { /* nothing to offer */ }
    }

    [RelayCommand]
    private Task Describe() => ReadAsync(AssistTasks.Describe, "Looking at the picture", text => Description = text);

    [RelayCommand]
    private Task MakeSimilar() => ReadAsync(AssistTasks.ImagePrompt, "Writing a prompt from the picture", text =>
    {
        var shape = Current?.Shape;
        Close();
        UsePromptHandler?.Invoke(text, null, shape);
    });

    [RelayCommand]
    private void UsePrompt()
    {
        var entry = Current;
        if (entry?.Prompt == null) return;
        Close();
        UsePromptHandler?.Invoke(entry.Prompt, entry.Look, entry.Shape);
    }

    [RelayCommand]
    private void UseDescription()
    {
        var text = Description;
        if (text == null) return;
        Close();
        UsePromptHandler?.Invoke(text, null, null);
    }

    [RelayCommand]
    private void HideDescription() => Description = null;

    [RelayCommand]
    private void Animate()
    {
        var entry = Current;
        if (entry?.LibraryId == null) return;
        var slot = PictureSlot.FromLibrary(entry.LibraryId, Picture);
        Close();
        AnimateHandler?.Invoke(slot);
    }

    private async Task ReadAsync(string task, string working, Action<string> done)
    {
        var entry = Current;
        if (entry?.LibraryId == null || IsWorking) return;
        var load = _load;
        IsWorking = true;
        WorkingText = working;
        Notice = null;
        try
        {
            var text = await AppServices.Remote.AssistAsync(new AssistRequest { Task = task, Picture = "library:" + entry.LibraryId });
            if (load == _load) done(text);
        }
        catch (RemoteException ex)
        {
            if (load == _load) Notice = ex.Message;
        }
        finally
        {
            if (load == _load) IsWorking = false;
        }
    }

    /// <summary>Called by the view with the stream the system save sheet opened.</summary>
    public async Task SaveToAsync(Stream target)
    {
        var entry = Current;
        if (entry?.FileUrl == null) return;
        var load = _load;
        IsWorking = true;
        WorkingText = $"Saving the {(entry.IsVideo ? "video" : "picture")} to your {DeviceInfo.Noun}";
        Notice = null;
        try
        {
            await AppServices.Remote.DownloadToAsync(entry.FileUrl, target);
            if (load == _load) Notice = "Saved.";
        }
        catch (RemoteException ex)
        {
            if (load == _load) Notice = ex.Message;
        }
        finally
        {
            if (load == _load) IsWorking = false;
        }
    }
}
