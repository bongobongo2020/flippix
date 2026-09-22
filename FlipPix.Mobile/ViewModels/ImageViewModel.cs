using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlipPix.Mobile.Services;

namespace FlipPix.Mobile.ViewModels;

public partial class ImageViewModel : ObservableObject
{
    private const string PolishSystem =
        "You turn a short picture idea into one vivid image prompt for a photorealistic image model. " +
        "Describe the subject, what they are doing, the setting, the light, the lens and the mood in " +
        "plain concrete language, 60 to 110 words, one paragraph. Keep every detail the user gave and " +
        "invent nothing that contradicts it. Reply with the prompt only: no title, no quotes, no preamble.";

    private CancellationTokenSource? _cts;

    public IReadOnlyList<ImageLook> Looks => ImageLook.All;
    public ObservableCollection<ImageTile> Tiles { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MakeCommand), nameof(PolishCommand))]
    private string _prompt = "";

    [ObservableProperty] private ImageLook _look = ImageLook.All[0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPortrait), nameof(IsSquare), nameof(IsLandscape))]
    private ImageShape _shape = ImageShape.Portrait;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MakeLabel), nameof(IsOne), nameof(IsTwo), nameof(IsFour))]
    private int _count = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(MakeCommand), nameof(PolishCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MakeCommand), nameof(PolishCommand))]
    private bool _isPolishing;

    [ObservableProperty] private string? _notice;
    [ObservableProperty] private string _busyText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsViewerOpen))]
    private ImageTile? _viewing;

    public bool IsViewerOpen => Viewing != null;
    public bool IsIdle => !IsBusy;
    public bool HasTiles => Tiles.Count > 0;
    public bool NoTiles => Tiles.Count == 0;
    public bool CanPolish => AppServices.Settings.LlmUrl.Length > 0;
    public bool IsPortrait => Shape == ImageShape.Portrait;
    public bool IsSquare => Shape == ImageShape.Square;
    public bool IsLandscape => Shape == ImageShape.Landscape;
    public bool IsOne => Count == 1;
    public bool IsTwo => Count == 2;
    public bool IsFour => Count == 4;
    public string MakeLabel => Count == 1 ? "Make image" : $"Make {Count} images";

    public ImageViewModel() => Tiles.CollectionChanged += (_, _) =>
    {
        OnPropertyChanged(nameof(HasTiles));
        OnPropertyChanged(nameof(NoTiles));
    };

    /// <summary>Settings may have gained or lost an LLM since this page was built.</summary>
    public void Refresh() => OnPropertyChanged(nameof(CanPolish));

    [RelayCommand] private void PickLook(ImageLook look) => Look = look;
    [RelayCommand] private void PickShape(ImageShape shape) => Shape = shape;
    [RelayCommand] private void PickCount(string n) => Count = int.Parse(n);

    private bool CanMake() => !IsBusy && !IsPolishing && !string.IsNullOrWhiteSpace(Prompt);

    [RelayCommand(CanExecute = nameof(CanMake))]
    private async Task MakeAsync()
    {
        Notice = null;
        if (!AppServices.Settings.IsComfyConfigured)
        {
            Notice = "Add your ComfyUI address in Settings, then try again.";
            return;
        }

        // Every tile goes on the sheet at once, newest first, so the whole order is visible.
        var batch = Enumerable.Range(0, Count).Select(_ => new ImageTile
        {
            Prompt = Prompt.Trim(), Look = Look, Shape = Shape, Seed = Workflows.RandomSeed(),
        }).ToList();
        for (var i = batch.Count - 1; i >= 0; i--) Tiles.Insert(0, batch[i]);

        IsBusy = true;
        _cts = new CancellationTokenSource();
        try
        {
            for (var i = 0; i < batch.Count; i++)
            {
                BusyText = batch.Count == 1 ? "Developing" : $"Developing {i + 1} of {batch.Count}";
                await DevelopAsync(batch[i], _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            foreach (var t in batch.Where(t => t.IsPending)) { t.State = TileState.Failed; t.Status = "Stopped"; }
        }
        finally
        {
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private static async Task DevelopAsync(ImageTile tile, CancellationToken ct)
    {
        tile.State = TileState.Developing;
        tile.Status = "Sending to the server";
        try
        {
            var graph = tile.Look.Build(tile.Prompt, tile.Shape, tile.Seed);
            var outputs = await AppServices.Comfy.RunAsync(graph, (v, max) => Dispatcher.UIThread.Post(() =>
            {
                tile.Progress = (double)v / max;
                tile.Status = $"Step {v} of {max}";
            }), ct);

            var image = outputs.FirstOrDefault(o => !o.IsVideo)
                ?? throw new InvalidOperationException("The server finished but saved no picture.");
            tile.Status = "Fetching";
            var bytes = await AppServices.Comfy.DownloadAsync(image, ct);
            using var ms = new MemoryStream(bytes);
            // Decoded to screen size: a 2560px PNG at full size is 26 MB of bitmap per tile.
            tile.Picture = Bitmap.DecodeToWidth(ms, 1440);
            tile.Progress = 1;
            tile.State = TileState.Done;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            tile.State = TileState.Failed;
            tile.Status = FirstLine(ex.Message);
        }
    }

    [RelayCommand] private void Stop() => _cts?.Cancel();

    [RelayCommand(CanExecute = nameof(CanMake))]
    private async Task PolishAsync()
    {
        IsPolishing = true;
        Notice = null;
        try
        {
            Prompt = await LlmClient.ChatAsync(AppServices.Settings, PolishSystem, Prompt.Trim(), maxTokens: 400);
        }
        catch (Exception ex)
        {
            Notice = FirstLine(ex.Message);
        }
        finally
        {
            IsPolishing = false;
        }
    }

    /// <summary>A finished tile opens full screen; a failed one is dismissed by the same tap.</summary>
    [RelayCommand]
    private void Open(ImageTile tile)
    {
        if (tile.IsDone) Viewing = tile;
        else if (tile.IsFailed) Remove(tile);
    }
    [RelayCommand] private void CloseViewer() => Viewing = null;

    /// <summary>Puts the viewed picture's prompt, look and shape back in the composer.</summary>
    [RelayCommand]
    private void ReusePrompt()
    {
        if (Viewing == null) return;
        Prompt = Viewing.Prompt;
        Look = Viewing.Look;
        Shape = Viewing.Shape;
        Viewing = null;
    }

    [RelayCommand]
    private void Remove(ImageTile tile)
    {
        if (tile.IsPending) return;
        if (Viewing == tile) Viewing = null;
        Tiles.Remove(tile);
        tile.Picture?.Dispose();
    }

    internal static string FirstLine(string s)
    {
        var line = s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? s;
        return line.Length > 160 ? line[..160] + "…" : line;
    }
}
