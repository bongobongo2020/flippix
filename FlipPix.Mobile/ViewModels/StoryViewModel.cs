using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlipPix.Mobile.Controls;
using FlipPix.Mobile.Services;
using FlipPix.UI.Services;

namespace FlipPix.Mobile.ViewModels;

public enum ClipState { Waiting, Filming, Done, Failed }

/// <summary>One shot of a film: its beat, the scene written for it, and the rendered file.</summary>
public partial class StoryClip : ObservableObject
{
    public required int Number { get; init; }
    public required string Beat { get; init; }
    public required string Script { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDone), nameof(IsFailed), nameof(IsFilming), nameof(IsWaiting))]
    private ClipState _state;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private string? _url;
    [ObservableProperty] private string _status = "Waiting";
    [ObservableProperty] private bool _isSelected;

    public bool IsDone => State == ClipState.Done;
    public bool IsFailed => State == ClipState.Failed;
    public bool IsFilming => State == ClipState.Filming;
    public bool IsWaiting => State == ClipState.Waiting;
}

public enum FilmStage { Casting, Writing, Filming, Done, Failed, Stopped }

/// <summary>One story being turned into a film, from the cast to the last clip.</summary>
public partial class StoryFilm : ObservableObject
{
    public required string Title { get; init; }
    public required string Story { get; init; }
    public required int ClipCount { get; init; }
    public ObservableCollection<StoryClip> Clips { get; } = new();
    public List<ReferencePicture> Cast { get; } = new();

    [ObservableProperty] private Bitmap? _poster;
    [ObservableProperty] private string _aspect = "16:9 (Widescreen)";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorking), nameof(HasClips), nameof(IsFailed), nameof(CanRetry), nameof(ShowPlay))]
    private FilmStage _stage = FilmStage.Casting;

    [ObservableProperty] private string _status = "Casting";
    [ObservableProperty] private string _elapsed = "";
    [ObservableProperty] private double _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private StoryClip? _selected;

    public bool IsWorking => Stage is FilmStage.Casting or FilmStage.Writing or FilmStage.Filming;
    public bool IsFailed => Stage == FilmStage.Failed;
    public bool HasClips => Clips.Count > 0;
    public bool HasSelection => Selected != null;
    public int DoneCount => Clips.Count(c => c.IsDone);
    public bool CanPlay => DoneCount > 0;
    public bool ShowPlay => CanPlay; // finished clips can be watched while the rest are filmed
    /// <summary>Shots were written but not all were filmed: they can be filmed again as they are.</summary>
    public bool CanRetry => !IsWorking && Clips.Count > 0 && DoneCount < Clips.Count;
    public double Ratio => VideoRecipe.AspectRatioOf(Aspect);
    public string LengthLabel => $"{ClipCount} clips · {ClipCount * StoryRecipe.ClipSeconds} s";

    public StoryFilm()
    {
        Clips.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(HasClips)); Refresh(); };
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(DoneCount));
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(ShowPlay));
        OnPropertyChanged(nameof(CanRetry));
    }

    partial void OnAspectChanged(string value) => OnPropertyChanged(nameof(Ratio));
}

public partial class StoryViewModel : ObservableObject
{
    private CancellationTokenSource? _cts;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch _watch = new();
    private StoryFilm? _running;

    public ObservableCollection<ReferencePicture> Pictures { get; } = new();
    public ObservableCollection<StoryFilm> Films { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MakeCommand))]
    private string _story = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShort), nameof(IsMinute), nameof(IsLong))]
    private int _clipCount = 6;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(MakeCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddPicture))]
    [NotifyCanExecuteChangedFor(nameof(MakeCommand))]
    private bool _isPreparing;

    [ObservableProperty] private string? _notice;
    [ObservableProperty] private string _busyText = "";

    /// <summary>The playlist on screen: every finished clip of a film, or just one clip.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlayerOpen))]
    private IReadOnlyList<string>? _playing;

    [ObservableProperty] private string _playingTitle = "";

    public bool IsPlayerOpen => Playing != null;
    public bool PlayerSupported => VideoSurface.IsSupported;
    public bool IsIdle => !IsBusy;
    public bool IsShort => ClipCount == 3;
    public bool IsMinute => ClipCount == 6;
    public bool IsLong => ClipCount == 12;
    public bool HasLlm => AppServices.Settings.LlmUrl.Length > 0;
    public bool NoFilms => Films.Count == 0;
    public bool CanAddPicture => !IsPreparing && Pictures.Count < StoryRecipe.MaxCast;
    public string CastHint => Pictures.Count == 0
        ? "Optional. Photos of the people in your story. Leave it empty and a portrait is made from the story."
        : $"{Pictures.Count} of {StoryRecipe.MaxCast}. The first person in the story is the first photo.";

    public StoryViewModel()
    {
        Pictures.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CanAddPicture));
            OnPropertyChanged(nameof(CastHint));
        };
        Films.CollectionChanged += (_, _) => OnPropertyChanged(nameof(NoFilms));
        _clock.Tick += (_, _) => { if (_running != null) _running.Elapsed = Clock(_watch.Elapsed); };
    }

    public void Refresh() => OnPropertyChanged(nameof(HasLlm));

    public async Task AddPicturesAsync(IEnumerable<Func<Task<Stream>>> openers)
    {
        Notice = null;
        IsPreparing = true;
        try
        {
            foreach (var open in openers)
            {
                if (Pictures.Count >= StoryRecipe.MaxCast) break;
                try
                {
                    await using var stream = await open();
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

    [RelayCommand] private void RemovePicture(ReferencePicture p) => Pictures.Remove(p);
    [RelayCommand] private void PickLength(string clips) => ClipCount = int.Parse(clips);

    private bool CanMake() => !IsBusy && !IsPreparing && !string.IsNullOrWhiteSpace(Story);

    [RelayCommand(CanExecute = nameof(CanMake))]
    private async Task MakeAsync()
    {
        Notice = null;
        if (!AppServices.Settings.IsComfyConfigured)
        {
            Notice = "Add your ComfyUI address in Settings, then try again.";
            return;
        }
        if (!HasLlm)
        {
            Notice = "A story needs a writing assistant to divide it into shots. Add one in Settings.";
            return;
        }

        var text = Story.Trim();
        var film = new StoryFilm { Title = TitleOf(text), Story = text, ClipCount = ClipCount };
        film.Cast.AddRange(Pictures);
        Films.Insert(0, film);

        IsBusy = true;
        _running = film;
        _watch.Restart();
        _clock.Start();
        _cts = new CancellationTokenSource();
        try
        {
            await CastAsync(film, _cts.Token);
            await WriteAsync(film, _cts.Token);
            await FilmAsync(film, film.Clips.ToList(), _cts.Token);
        }
        catch (OperationCanceledException)
        {
            film.Stage = FilmStage.Stopped;
            film.Status = film.DoneCount > 0 ? $"Stopped after {film.DoneCount} clip(s)" : "Stopped";
        }
        catch (Exception ex)
        {
            film.Stage = FilmStage.Failed;
            film.Status = ImageViewModel.FirstLine(ex.Message);
        }
        finally
        {
            Finish();
        }
    }

    /// <summary>Films the clips of a film that did not come out, from the scenes already written.</summary>
    [RelayCommand]
    private async Task RetryClipsAsync(StoryFilm film)
    {
        var redo = film.Clips.Where(c => !c.IsDone).ToList();
        if (IsBusy || redo.Count == 0 || film.Cast.All(c => c.UploadedName == null)) return;
        IsBusy = true;
        _running = film;
        _watch.Restart();
        _clock.Start();
        _cts = new CancellationTokenSource();
        try
        {
            await FilmAsync(film, redo, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            film.Stage = FilmStage.Stopped;
            film.Status = "Stopped";
        }
        finally
        {
            Finish();
        }
    }

    private void Finish()
    {
        _clock.Stop();
        _running = null;
        IsBusy = false;
        _cts?.Dispose();
        _cts = null;
    }

    // ── 1. The cast ─────────────────────────────────────────────────────────────────────────────

    private List<string> _descriptions = new();

    private async Task CastAsync(StoryFilm film, CancellationToken ct)
    {
        var settings = AppServices.Settings;
        film.Stage = FilmStage.Casting;
        BusyText = "Casting";

        if (film.Cast.Count == 0)
        {
            // No photos: write a portrait of the lead in the story's setting and have the Photo look
            // take it. It becomes picture 1 and every clip is cast from it.
            film.Status = "Writing a portrait of your lead";
            var prompt = (await LlmClient.ChatAsync(settings, StoryRecipe.CastPhotoSystem, film.Story,
                Array.Empty<byte[]>(), 300, 0.7, ct)).Trim().Trim('"');
            film.Status = "Photographing your lead";
            var photo = ImageLook.All.First(l => l.Key == "photo");
            var outputs = await AppServices.Comfy.RunAsync(
                photo.Build(prompt, ImageShape.Landscape, Workflows.RandomSeed()),
                (v, max) => Dispatcher.UIThread.Post(() => film.Status = $"Photographing your lead, step {v} of {max}"),
                ct);
            var image = outputs.FirstOrDefault(o => !o.IsVideo)
                ?? throw new InvalidOperationException("The portrait came back empty.");
            using var ms = new MemoryStream(await AppServices.Comfy.DownloadAsync(image, ct));
            var cast = await Task.Run(() => ReferencePicture.FromStream(ms), ct);
            film.Cast.Add(cast);
            _descriptions = new List<string> { prompt };
        }
        else
        {
            // A line per photo, read once by the vision model and then handed to every call as text:
            // cheaper than attaching the pictures to every clip, and identical in every clip by design.
            _descriptions = new List<string>();
            for (var i = 0; i < film.Cast.Count; i++)
            {
                film.Status = $"Looking at photo {i + 1} of {film.Cast.Count}";
                var line = await LlmClient.ChatAsync(settings, StoryRecipe.DescribeSystem,
                    $"Describe <Picture {i + 1}>.", new[] { film.Cast[i].Jpeg }, 200, 0.4, ct);
                _descriptions.Add(line.Trim());
            }
        }

        film.Poster = film.Cast[0].Thumbnail;
        film.Aspect = VideoRecipe.AspectFor(film.Cast[0].Width, film.Cast[0].Height);

        film.Status = "Sending the cast to the server";
        foreach (var p in film.Cast) p.UploadedName ??= await AppServices.Comfy.UploadJpegAsync(p.Jpeg, ct);
    }

    // ── 2. The shots ────────────────────────────────────────────────────────────────────────────

    private async Task WriteAsync(StoryFilm film, CancellationToken ct)
    {
        var settings = AppServices.Settings;
        var lm = new LMStudioService(settings);
        film.Stage = FilmStage.Writing;
        BusyText = "Writing the shots";
        film.Status = $"Dividing the story into {film.ClipCount} shots";

        var (setting, beats) = await StoryBeatSheet.WriteAsync(
            lm, settings.LlmModel, film.Story, film.ClipCount, StoryRecipe.ClipSeconds,
            StoryRecipe.CastBrief(_descriptions), perBeatCast: false, imagePath: null,
            log: m => Debug.WriteLine("[FlipPix I] " + m), token: ct, continuity: true);
        if (beats.Count == 0) throw new InvalidOperationException("The story couldn't be divided into shots.");

        var plan = StoryContinuity.Plan(beats.Select(b => b.Env).ToList(), setting, beats.Select(b => b.Text).ToList());
        var system = VideoRecipe.SystemPrompt();
        var cast = _descriptions.Count;

        await ClipChainWriter.WriteAsync(
            lm, settings.LlmModel, system, beats.Count,
            buildRequest: (i, why) => StoryRecipe.ClipRequest(_descriptions, setting, beats, plan, i, why),
            normalize: VideoRecipe.CleanScript,
            validate: (i, body) => StoryRecipe.Validate(body, StoryRecipe.EnvFor(plan, i)),
            onProgress: (n, total) =>
            {
                film.Status = $"Writing shot {n} of {total}";
                film.Progress = 0.1 * (n - 1) / total;
            },
            log: m => Debug.WriteLine("[FlipPix I] " + m),
            token: ct,
            // Each clip lands on the strip the moment it is written.
            onWritten: (i, body) => film.Clips.Add(new StoryClip
            {
                Number = film.Clips.Count + 1,
                Beat = StoryRecipe.DisplayBeat(beats[Math.Min(i, beats.Count - 1)].Text),
                Script = StoryRecipe.Retag(StoryRecipe.StampScene(body, StoryRecipe.EnvFor(plan, i)), cast),
            }));

        if (film.Clips.Count == 0) throw new InvalidOperationException("No shot came back usable from the writer.");
    }

    // ── 3. The film ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders the clips one at a time. One clip failing is not the film failing: it is marked, the
    /// next one starts, and it can be filmed again afterwards from the same scene.
    /// </summary>
    private async Task FilmAsync(StoryFilm film, IReadOnlyList<StoryClip> clips, CancellationToken ct)
    {
        film.Stage = FilmStage.Filming;
        var names = film.Cast.Select(c => c.UploadedName!).ToList();
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        for (var k = 0; k < clips.Count; k++)
        {
            var clip = clips[k];
            BusyText = $"Filming clip {clip.Number} of {film.Clips.Count}";
            film.Status = BusyText;
            clip.State = ClipState.Filming;
            clip.Progress = 0;
            clip.Status = "Waiting for the server";
            try
            {
                var graph = VideoRecipe.Build(names, clip.Script, StoryRecipe.ClipSeconds, film.Aspect,
                    Workflows.RandomSeed(), $"FlipPixMobile/story_{stamp}_clip{clip.Number:00}");
                var stage = 0;
                var (lastValue, lastMax) = (0, 0);
                var outputs = await AppServices.Comfy.RunAsync(graph, (v, max) => Dispatcher.UIThread.Post(() =>
                {
                    if (lastMax != 0 && (v < lastValue || max != lastMax)) stage++;
                    (lastValue, lastMax) = (v, max);
                    clip.Progress = Math.Min(1, (stage + (double)v / max) / 3);
                    film.Progress = 0.1 + 0.9 * (k + clip.Progress) / clips.Count;
                }), ct);
                var video = outputs.FirstOrDefault(o => o.IsVideo)
                    ?? throw new InvalidOperationException("The server finished but saved no video.");
                clip.Url = AppServices.Comfy.ViewUrl(video);
                clip.State = ClipState.Done;
                clip.Status = "Done";
            }
            catch (OperationCanceledException)
            {
                clip.State = ClipState.Waiting;
                clip.Status = "Waiting";
                throw;
            }
            catch (Exception ex)
            {
                clip.State = ClipState.Failed;
                clip.Status = ImageViewModel.FirstLine(ex.Message);
            }
            film.Refresh();
        }

        var done = film.DoneCount;
        film.Progress = 1;
        film.Stage = done == film.Clips.Count ? FilmStage.Done : FilmStage.Failed;
        film.Status = done == film.Clips.Count
            ? $"Made in {Clock(_watch.Elapsed)}"
            : $"{done} of {film.Clips.Count} clips made. Try the rest again below.";
    }

    [RelayCommand] private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private void PlayFilm(StoryFilm film)
    {
        var urls = film.Clips.Where(c => c.IsDone && c.Url != null).Select(c => c.Url!).ToList();
        if (urls.Count == 0) return;
        PlayingTitle = film.Title;
        Playing = urls;
    }

    [RelayCommand]
    private void SelectClip(StoryClip clip)
    {
        var film = Films.FirstOrDefault(f => f.Clips.Contains(clip));
        if (film == null) return;
        if (film.Selected != null) film.Selected.IsSelected = false;
        film.Selected = film.Selected == clip ? null : clip;
        if (film.Selected != null) film.Selected.IsSelected = true;
    }

    [RelayCommand]
    private void PlayClip(StoryClip clip)
    {
        if (clip.Url == null) return;
        PlayingTitle = $"Clip {clip.Number}";
        Playing = new[] { clip.Url };
    }

    [RelayCommand] private void ClosePlayer() => Playing = null;

    [RelayCommand]
    private void RemoveFilm(StoryFilm film)
    {
        if (film.IsWorking) return;
        Films.Remove(film);
    }

    private static string TitleOf(string story)
    {
        var first = story.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? story;
        var sentence = first.Split(new[] { ". ", "! ", "? " }, 2, StringSplitOptions.None)[0].TrimEnd('.');
        return sentence.Length <= 60 ? sentence : sentence[..57].TrimEnd() + "…";
    }

    private static string Clock(TimeSpan t) =>
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}:{t.Seconds:00}" : $"{t.Seconds} s";
}
