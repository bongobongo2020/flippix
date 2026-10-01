using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlipPix.Mobile.Services;
using FlipPix.Remote.Contracts;

namespace FlipPix.Mobile.ViewModels;

/// <summary>A way of making a picture, named for what it looks like rather than the model behind it.</summary>
public sealed record LookOption(string Key, string Name, string Blurb);

public static class Looks
{
    /// <summary>The same keys the computer's ImageLook table uses.</summary>
    public static IReadOnlyList<LookOption> All { get; } = new[]
    {
        new LookOption("photo", "Photo", "Real camera look. Fast, upscaled 2×."),
        new LookOption("dream", "Dream", "Rewrites a short idea into a rich scene first."),
        new LookOption("portrait", "Detail", "Slow and careful. Skin, fabric, texture."),
    };

    public static string NameOf(string? key) => All.FirstOrDefault(l => l.Key == key)?.Name ?? "Photo";
}

public enum ImageShape { Portrait, Square, Landscape }

public partial class ImageViewModel : ObservableObject
{
    private readonly Action<IReadOnlyList<ViewerEntry>, int> _openViewer;

    public ImageViewModel(Action<IReadOnlyList<ViewerEntry>, int> openViewer)
    {
        _openViewer = openViewer;
        AppServices.Jobs.Changed += Sync;
        // The computer may offer fewer looks (the iOS Companion makes only Photo).
        ServerInfo.Current.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ServerInfo.Looks)) return;
            OnPropertyChanged(nameof(LookOptions));
            if (!LookOptions.Contains(Look)) Look = LookOptions.FirstOrDefault() ?? Looks.All[0];
        };
        _ = ServerInfo.Current.RefreshAsync();
    }

    public IReadOnlyList<LookOption> LookOptions => ServerInfo.Current.Looks;

    /// <summary>Every picture this phone asked for, newest first: waiting, developing and done.</summary>
    public ObservableCollection<JobItemVm> Tiles { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MakeCommand), nameof(PolishCommand))]
    private string _prompt = "";

    [ObservableProperty] private LookOption _look = Looks.All[0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPortrait), nameof(IsSquare), nameof(IsLandscape))]
    private ImageShape _shape = ImageShape.Portrait;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MakeLabel), nameof(IsOne), nameof(IsTwo), nameof(IsFour))]
    private int _count = 1;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MakeCommand), nameof(PolishCommand))]
    [NotifyPropertyChangedFor(nameof(IsHelping))]
    private bool _isPolishing;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MakeCommand), nameof(PolishCommand))]
    [NotifyPropertyChangedFor(nameof(IsHelping))]
    private bool _isReadingPhoto;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MakeCommand))]
    private bool _isSending;

    [ObservableProperty] private string? _notice;
    [ObservableProperty] private string _activeText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoTiles))]
    private bool _hasTiles;

    [ObservableProperty] private bool _hasActive;

    public bool NoTiles => !HasTiles;
    public bool IsHelping => IsPolishing || IsReadingPhoto;
    public bool IsPortrait => Shape == ImageShape.Portrait;
    public bool IsSquare => Shape == ImageShape.Square;
    public bool IsLandscape => Shape == ImageShape.Landscape;
    public bool IsOne => Count == 1;
    public bool IsTwo => Count == 2;
    public bool IsFour => Count == 4;
    public string MakeLabel => Count == 1 ? "Make image" : $"Make {Count} images";

    [RelayCommand] private void PickShape(ImageShape shape) => Shape = shape;
    [RelayCommand] private void PickCount(string n) => Count = int.Parse(n);

    /// <summary>From the viewer: a prompt (and the look and shape it was made with) back in the composer.</summary>
    public void UsePrompt(string prompt, string? look, string? shape)
    {
        Prompt = prompt;
        if (look != null) Look = LookOptions.FirstOrDefault(l => l.Key == look) ?? Look;
        if (shape != null) Shape = ShapeOf(shape);
        Notice = null;
    }

    private bool CanMake() => !IsSending && !IsHelping && !string.IsNullOrWhiteSpace(Prompt);

    [RelayCommand(CanExecute = nameof(CanMake))]
    private async Task MakeAsync()
    {
        Notice = null;
        IsSending = true;
        try
        {
            await AppServices.Jobs.CreateAsync(new JobRequest
            {
                Kind = JobKinds.Image,
                Prompt = Prompt.Trim(),
                Look = Look.Key,
                Shape = ShapeKey(Shape),
                Count = Count,
            });
        }
        catch (RemoteException ex)
        {
            Notice = ex.Message;
        }
        finally
        {
            IsSending = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanMake))]
    private async Task PolishAsync()
    {
        IsPolishing = true;
        Notice = null;
        try
        {
            Prompt = await AppServices.Remote.AssistAsync(new AssistRequest { Task = AssistTasks.Polish, Text = Prompt.Trim() });
        }
        catch (RemoteException ex)
        {
            Notice = ex.Message;
        }
        finally
        {
            IsPolishing = false;
        }
    }

    /// <summary>A phone photo in, a prompt for a picture like it out, written by the computer's vision model.</summary>
    public async Task PromptFromPhotoAsync(Func<Task<Stream>> open)
    {
        IsReadingPhoto = true;
        Notice = null;
        try
        {
            ReferencePicture photo;
            await using (var stream = await open())
                photo = await Task.Run(() => ReferencePicture.FromStream(stream));
            using (photo)
            {
                var slot = PictureSlot.FromPhone(photo);
                var reference = await slot.ReferenceAsync();
                Prompt = await AppServices.Remote.AssistAsync(new AssistRequest { Task = AssistTasks.ImagePrompt, Picture = reference });
            }
        }
        catch (RemoteException ex)
        {
            Notice = ex.Message;
        }
        catch (Exception ex)
        {
            Notice = "That photo couldn't be opened: " + FirstLine(ex.Message);
        }
        finally
        {
            IsReadingPhoto = false;
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        foreach (var job in AppServices.Jobs.Jobs.Where(j => j.Kind == JobKinds.Image && j.IsActive).ToList())
        {
            try { await AppServices.Jobs.CancelAsync(job); }
            catch (RemoteException ex) { Notice = ex.Message; }
        }
    }

    /// <summary>A finished tile opens full screen; a failed one is tried again.</summary>
    [RelayCommand]
    private async Task OpenAsync(JobItemVm tile)
    {
        if (tile.IsDone)
        {
            var done = Tiles.Where(t => t.IsDone).ToList();
            _openViewer(done.Select(ViewerEntry.FromJobItem).ToList(), done.IndexOf(tile));
        }
        else if (tile.IsFailed && tile.Job.ShowRetry)
        {
            try { await AppServices.Jobs.RetryAsync(tile.Job); }
            catch (RemoteException ex) { Notice = ex.Message; }
        }
    }

    /// <summary>Takes a finished set of pictures off this page. They stay in the Library.</summary>
    [RelayCommand]
    private async Task ClearFinishedAsync()
    {
        foreach (var job in AppServices.Jobs.Jobs.Where(j => j.Kind == JobKinds.Image && !j.IsActive).ToList())
        {
            try { await AppServices.Jobs.RemoveAsync(job); }
            catch (RemoteException ex) { Notice = ex.Message; return; }
        }
    }

    private void Sync()
    {
        var jobs = AppServices.Jobs.Jobs.Where(j => j.Kind == JobKinds.Image).ToList();
        CollectionSync.Apply(Tiles, jobs.SelectMany(j => j.Items).ToList());
        HasTiles = Tiles.Count > 0;

        var active = jobs.Where(j => j.IsActive).ToList();
        HasActive = active.Count > 0;
        var running = active.FirstOrDefault(j => j.IsRunning);
        var waiting = active.Count(j => j.IsQueued);
        ActiveText = running != null
            ? running.Status + (waiting > 0 ? $" · {waiting} more waiting" : "")
            : waiting > 0 ? (waiting == 1 ? "Waiting for the computer" : $"{waiting} waiting for the computer") : "";
    }

    public static string ShapeKey(ImageShape s) => s switch
    {
        ImageShape.Square => "square",
        ImageShape.Landscape => "landscape",
        _ => "portrait",
    };

    public static ImageShape ShapeOf(string? key) => key switch
    {
        "square" => ImageShape.Square,
        "landscape" => ImageShape.Landscape,
        _ => ImageShape.Portrait,
    };

    internal static string FirstLine(string s)
    {
        var line = s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? s;
        return line.Length > 160 ? line[..160] + "…" : line;
    }
}

/// <summary>Brings an observable list in line with a wanted order, touching only what changed.</summary>
public static class CollectionSync
{
    public static void Apply<T>(ObservableCollection<T> target, IReadOnlyList<T> wanted) where T : class
    {
        for (var i = 0; i < wanted.Count; i++)
        {
            var item = wanted[i];
            var at = target.IndexOf(item);
            if (at < 0) target.Insert(i, item);
            else if (at != i) target.Move(at, i);
        }
        while (target.Count > wanted.Count) target.RemoveAt(target.Count - 1);
    }
}
