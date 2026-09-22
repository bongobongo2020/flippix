using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlipPix.Mobile.Controls;
using FlipPix.Mobile.Services;

namespace FlipPix.Mobile.ViewModels;

public enum TakeState { Writing, Uploading, Rendering, Done, Failed }

/// <summary>One video: from writing its script to the file on the server.</summary>
public partial class VideoTake : ObservableObject
{
    public required string Idea { get; init; }
    public required int Seconds { get; init; }
    public required string Aspect { get; init; }
    public required Bitmap Poster { get; init; }
    public required IReadOnlyList<ReferencePicture> Pictures { get; init; }

    /// <summary>Width over height of the finished frame, so the card has the video's shape.</summary>
    public double Ratio => VideoRecipe.AspectRatioOf(Aspect);
    public string LengthLabel => $"{Seconds} s";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDone), nameof(IsFailed), nameof(IsWorking))]
    private TakeState _state = TakeState.Writing;

    [ObservableProperty] private string _status = "Writing the scene";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _elapsed = "";
    [ObservableProperty] private string? _script;
    [ObservableProperty] private string? _url;
    [ObservableProperty] private bool _showScript;

    public bool IsDone => State == TakeState.Done;
    public bool IsFailed => State == TakeState.Failed;
    public bool IsWorking => State is TakeState.Writing or TakeState.Uploading or TakeState.Rendering;
}

public partial class VideoViewModel : ObservableObject
{
    private CancellationTokenSource? _cts;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch _watch = new();
    private VideoTake? _running;

    public ObservableCollection<ReferencePicture> Pictures { get; } = new();
    public ObservableCollection<VideoTake> Takes { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MakeCommand))]
    private string _idea = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFive), nameof(IsTen), nameof(IsFifteen))]
    private int _seconds = 10;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(MakeCommand))]
    private bool _isBusy;

    [ObservableProperty] private string? _notice;
    [ObservableProperty] private string _busyText = "";

    /// <summary>A picked photo is being rotated, shrunk and encoded; shown as a placeholder tile.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddPicture))]
    [NotifyCanExecuteChangedFor(nameof(MakeCommand))]
    private bool _isPreparing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlayerOpen))]
    private VideoTake? _playing;

    public bool IsPlayerOpen => Playing != null;
    public bool PlayerSupported => VideoSurface.IsSupported;
    public bool IsIdle => !IsBusy;
    public bool IsFive => Seconds == 5;
    public bool IsTen => Seconds == 10;
    public bool IsFifteen => Seconds == 15;
    public bool CanAddPicture => !IsPreparing && Pictures.Count < VideoRecipe.MaxReferences;
    public bool HasPictures => Pictures.Count > 0;
    public bool NoTakes => Takes.Count == 0;
    public bool HasLlm => AppServices.Settings.LlmUrl.Length > 0;
    public string PictureHint => Pictures.Count switch
    {
        0 => "Photos of the people and the place. Up to four.",
        VideoRecipe.MaxReferences => "Four is the most the model takes. Tap one to remove it.",
        _ => $"{Pictures.Count} of {VideoRecipe.MaxReferences}. The first photo sets the frame's shape.",
    };

    public VideoViewModel()
    {
        Pictures.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CanAddPicture));
            OnPropertyChanged(nameof(HasPictures));
            OnPropertyChanged(nameof(PictureHint));
            MakeCommand.NotifyCanExecuteChanged();
        };
        Takes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(NoTakes));
        _clock.Tick += (_, _) =>
        {
            if (_running != null) _running.Elapsed = FormatElapsed(_watch.Elapsed);
        };
    }

    public void Refresh() => OnPropertyChanged(nameof(HasLlm));

    /// <summary>Called by the view with the streams the system photo picker returned.</summary>
    public async Task AddPicturesAsync(IEnumerable<Func<Task<Stream>>> openers)
    {
        Notice = null;
        IsPreparing = true;
        try
        {
            foreach (var open in openers)
            {
                if (Pictures.Count >= VideoRecipe.MaxReferences) break;
                try
                {
                    await using var stream = await open();
                    // Rotating and shrinking a 50 MP photo is real CPU work; keep it off the UI thread.
                    Pictures.Add(await Task.Run(() => ReferencePicture.FromStream(stream)));
                }
                catch (Exception ex)
                {
                    Notice = "That photo couldn't be opened: " + ImageViewModel.FirstLine(ex.Message);
                }
            }
        }
        finally
        {
            IsPreparing = false;
        }
    }

    // Takes keep their own pictures, so a photo removed here is still what an earlier take was made from.
    [RelayCommand] private void RemovePicture(ReferencePicture picture) => Pictures.Remove(picture);

    [RelayCommand] private void PickSeconds(string s) => Seconds = int.Parse(s);

    private bool CanMake() => !IsBusy && !IsPreparing && Pictures.Count > 0;

    [RelayCommand(CanExecute = nameof(CanMake))]
    private Task MakeAsync()
    {
        var first = Pictures[0];
        var take = new VideoTake
        {
            Idea = Idea.Trim(), Seconds = Seconds,
            Aspect = VideoRecipe.AspectFor(first.Width, first.Height),
            Poster = first.Thumbnail, Pictures = Pictures.ToList(),
        };
        return RunAsync(take, script: null);
    }

    /// <summary>The same script again on a new seed: another performance of the same scene.</summary>
    [RelayCommand]
    private Task RetakeAsync(VideoTake source)
    {
        if (IsBusy || source.Script == null) return Task.CompletedTask;
        var take = new VideoTake
        {
            Idea = source.Idea, Seconds = source.Seconds, Aspect = source.Aspect,
            Poster = source.Poster, Pictures = source.Pictures,
        };
        return RunAsync(take, source.Script);
    }

    private async Task RunAsync(VideoTake take, string? script)
    {
        Notice = null;
        if (!AppServices.Settings.IsComfyConfigured)
        {
            Notice = "Add your ComfyUI address in Settings, then try again.";
            return;
        }

        Takes.Insert(0, take);
        IsBusy = true;
        _running = take;
        _watch.Restart();
        _clock.Start();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            // 1. The script. Written by the LLM from the pictures when there is one.
            BusyText = "Writing the scene";
            take.State = TakeState.Writing;
            take.Script = script ?? await WriteScriptAsync(take, ct);

            // 2. The pictures, uploaded once each and remembered.
            BusyText = "Sending photos";
            take.State = TakeState.Uploading;
            take.Status = "Sending photos to the server";
            var names = new List<string>();
            foreach (var p in take.Pictures)
            {
                p.UploadedName ??= await AppServices.Comfy.UploadJpegAsync(p.Jpeg, ct);
                names.Add(p.UploadedName);
            }

            // 3. The render: a draft pass, then the finish at twice the size. Progress restarts per pass.
            BusyText = "Filming";
            take.State = TakeState.Rendering;
            take.Status = "Waiting for the server";
            var graph = VideoRecipe.Build(names, take.Script, take.Seconds, take.Aspect,
                Workflows.RandomSeed(), $"FlipPixMobile/video_{DateTime.Now:yyyyMMdd_HHmmss}");
            // Stages arrive as separate progress runs: the draft sampler, the finish sampler, then a
            // long post-process run (audio and encode). A step count that restarts or changes size is
            // the next stage.
            var stage = 0;
            var (lastValue, lastMax) = (0, 0);
            var outputs = await AppServices.Comfy.RunAsync(graph, (v, max) => Dispatcher.UIThread.Post(() =>
            {
                if (lastMax != 0 && (v < lastValue || max != lastMax)) stage++;
                (lastValue, lastMax) = (v, max);
                var within = (double)v / max;
                (take.Progress, take.Status) = stage switch
                {
                    0 => (0.45 * within, $"Drafting, step {v} of {max}"),
                    1 => (0.45 + 0.45 * within, $"Finishing, step {v} of {max}"),
                    _ => (0.9 + 0.1 * within, "Adding the final touches"),
                };
            }), ct);

            var video = outputs.FirstOrDefault(o => o.IsVideo)
                ?? throw new InvalidOperationException("The server finished but saved no video.");
            take.Url = AppServices.Comfy.ViewUrl(video);
            take.Progress = 1;
            take.Status = $"Made in {FormatElapsed(_watch.Elapsed)}";
            take.State = TakeState.Done;
        }
        catch (OperationCanceledException)
        {
            take.State = TakeState.Failed;
            take.Status = "Stopped";
        }
        catch (Exception ex)
        {
            take.State = TakeState.Failed;
            take.Status = ImageViewModel.FirstLine(ex.Message);
        }
        finally
        {
            _clock.Stop();
            _running = null;
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private static async Task<string> WriteScriptAsync(VideoTake take, CancellationToken ct)
    {
        var settings = AppServices.Settings;
        if (settings.LlmUrl.Length == 0)
            return VideoRecipe.ScriptWithoutLlm(take.Pictures.Count, take.Seconds, take.Idea);

        take.Status = "Writing the scene from your photos";
        var reply = await LlmClient.ChatAsync(settings, VideoRecipe.SystemPrompt(),
            VideoRecipe.Request(take.Pictures.Count, take.Seconds, take.Idea),
            take.Pictures.Select(p => p.Jpeg).ToList(), maxTokens: 3000, temperature: 0.7, ct: ct);
        var script = VideoRecipe.CleanScript(reply);
        if (!script.Contains("detailed_description", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The LLM didn't write a usable scene. Is its model a vision model?");
        return script;
    }

    [RelayCommand] private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private void Open(VideoTake take)
    {
        if (take.IsDone) Playing = take;
        else if (take.IsFailed) Takes.Remove(take);
        else take.ShowScript = !take.ShowScript;
    }

    [RelayCommand] private void ClosePlayer() => Playing = null;
    [RelayCommand] private void ToggleScript(VideoTake take) => take.ShowScript = !take.ShowScript;

    [RelayCommand]
    private void RemoveTake(VideoTake take)
    {
        if (take.IsWorking) return;
        if (Playing == take) Playing = null;
        Takes.Remove(take);
    }

    private static string FormatElapsed(TimeSpan t) =>
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}:{t.Seconds:00}" : $"{t.Seconds} s";
}
