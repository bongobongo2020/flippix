using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlipPix.Core.Models;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// ✨ Story engine: a story pasted into the story box (or a .txt / .md loaded with 📂) goes to the writer —
    /// llama-server or the DeepSeek API — and comes back as the whole page in one click: the master prompt in the
    /// style box, the location in the setting box, the wardrobe, the beats, and every beat's spoken line ready for
    /// 🗣 Yakkinator (<see cref="FflfStoryEngine"/>, prompts/prompt2json/fflf-story-engine.md).
    ///
    /// <para>The story on the page before is kept in 📚 Stories; the generated one becomes a new story. The opening
    /// image and the character picture stay, so a story can be rewritten for the same character.</para>
    /// </summary>
    public partial class H3FflfViewModel
    {
        public const string WriterSettings = "settings";
        public const string WriterLocal = "local";
        public const string WriterDeepSeek = "deepseek";

        public sealed record EngineWriterOption(string Id, string Label);

        public IReadOnlyList<EngineWriterOption> EngineWriters { get; } = new[]
        {
            new EngineWriterOption(WriterSettings, "As in Settings"),
            new EngineWriterOption(WriterLocal, "llama-server (local)"),
            new EngineWriterOption(WriterDeepSeek, "DeepSeek API"),
        };

        private CancellationTokenSource? _engineCts;

        /// <summary>The story, idea, article or script the engine turns into the page.</summary>
        [ObservableProperty] private string _engineStoryText = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(EnginePlan))]
        private int _engineBeatCount = 8;

        /// <summary>✨ Extra direction (optional): how the writer should treat the story — sent with it, and kept in
        /// <see cref="SavedDirections"/> once a story is generated with it.</summary>
        [ObservableProperty] private string _engineDirection = string.Empty;

        /// <summary>Every extra direction used, newest first — one list shared by every FFLF-style tab and saved in
        /// <see cref="ComfyUISettings.StoryDirections"/>.</summary>
        public ObservableCollection<StoryDirectionPreset> SavedDirections => _savedDirections ??= LoadSavedDirections();
        private static ObservableCollection<StoryDirectionPreset>? _savedDirections;

        /// <summary>The direction picked in the drop-down: picking one puts its text in the box.</summary>
        [ObservableProperty] private StoryDirectionPreset? _selectedDirection;

        public const int MaxSavedDirections = 40;

        /// <summary>Offered the first time, so the drop-down shows what a direction looks like.</summary>
        private static readonly StoryDirectionPreset[] StarterDirections =
        {
            new()
            {
                Name = "More action",
                Text = "Rewrite this scene as a brutal cinematic fight and focus on continuous physical action, not backstory. " +
                       "Write the combat like a choreographed action sequence: every strike creates the next movement, every dodge " +
                       "uses momentum, and every counter grows from the previous position.",
            },
        };

        [ObservableProperty] private string _engineWriter = WriterSettings;

        /// <summary>Off: the engine writes the beats without spoken lines.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(EnginePlan))]
        private bool _engineWithDialogue = true;

        /// <summary>⏱ On: the writer gives each beat its own length (a <c>[7 s]</c> tag in the beats box), aiming for
        /// beats × <see cref="H3FflfViewModel.BeatSeconds"/> in all; off: every beat is BeatSeconds long.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(EnginePlan), nameof(BeatSecondsLabel))]
        private bool _engineDynamicBeatTime;

        public string BeatSecondsLabel => EngineDynamicBeatTime ? "Avg seconds / beat" : "Seconds / beat";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(EngineLabel))]
        private bool _isEngineWriting;

        [ObservableProperty] private string _engineStatus = string.Empty;

        public string EngineLabel => IsEngineWriting ? "✨ Writing…" : "✨ Generate all from story";

        public string EnginePlan
        {
            get
            {
                var beats = Math.Clamp(EngineBeatCount, FflfStoryEngine.MinBeats, FflfStoryEngine.MaxBeats);
                if (EngineDynamicBeatTime)
                {
                    var dynamicPlan = $"{beats} beat{(beats == 1 ? "" : "s")}, each {FflfGraph.MinTaggedSeconds:0}–{FflfGraph.MaxTaggedSeconds:0} s as the writer sees fit · ≈{beats * BeatSeconds:0.#} s in all";
                    return EngineWithDialogue ? $"{dynamicPlan} · lines sized to each beat" : dynamicPlan;
                }
                var plan = $"{beats} beat{(beats == 1 ? "" : "s")} × {BeatSeconds:0.#} s = {beats * BeatSeconds:0.#} s";
                return EngineWithDialogue ? $"{plan} · ≈{FflfStoryEngine.DialogueWords(BeatSeconds).Target} spoken words a beat" : plan;
            }
        }

        public RelayCommand GenerateFromStoryCommand { get; private set; } = null!;
        public RelayCommand BrowseStoryFileCommand { get; private set; } = null!;
        public RelayCommand CancelEngineCommand { get; private set; } = null!;
        public RelayCommand DeleteDirectionCommand { get; private set; } = null!;

        /// <summary>Runs in the constructor, while the page is still loading.</summary>
        private void InitStoryEngine(H3FflfSettings s)
        {
            GenerateFromStoryCommand = new RelayCommand(() => _ = GenerateFromStoryAsync(),
                () => !IsProcessing && !IsEngineWriting && !string.IsNullOrWhiteSpace(EngineStoryText));
            BrowseStoryFileCommand = new RelayCommand(() => _ = BrowseStoryFileAsync(), () => !IsEngineWriting);
            CancelEngineCommand = new RelayCommand(() => _engineCts?.Cancel(), () => IsEngineWriting);
            DeleteDirectionCommand = new RelayCommand(DeleteSelectedDirection, () => SelectedDirection != null);

#pragma warning disable MVVMTK0034
            _engineStoryText = s.StoryEngineText ?? string.Empty;
            _engineDirection = s.StoryEngineDirection ?? string.Empty;
            _engineBeatCount = Math.Clamp(s.StoryEngineBeats, FflfStoryEngine.MinBeats, FflfStoryEngine.MaxBeats);
            _engineWriter = EngineWriters.Any(w => w.Id == s.StoryEngineWriter) ? s.StoryEngineWriter : WriterSettings;
            _engineWithDialogue = s.StoryEngineDialogue;
            _engineDynamicBeatTime = s.StoryEngineDynamicBeats;
            _openingDescription = s.OpeningImageDescription;
            _openingDescriptionFor = s.OpeningImageDescriptionFor;
#pragma warning restore MVVMTK0034
        }

        private void SaveStoryEngine(H3FflfSettings s)
        {
            s.StoryEngineText = EngineStoryText;
            s.StoryEngineDirection = EngineDirection;
            s.StoryEngineBeats = EngineBeatCount;
            s.StoryEngineWriter = EngineWriter;
            s.StoryEngineDialogue = EngineWithDialogue;
            s.StoryEngineDynamicBeats = EngineDynamicBeatTime;
            s.OpeningImageDescription = _openingDescription ?? string.Empty;
            s.OpeningImageDescriptionFor = _openingDescriptionFor ?? string.Empty;
        }

        private void RefreshEngineCommands()
        {
            GenerateFromStoryCommand?.NotifyCanExecuteChanged();
            BrowseStoryFileCommand?.NotifyCanExecuteChanged();
            CancelEngineCommand?.NotifyCanExecuteChanged();
        }

        partial void OnEngineStoryTextChanged(string value) => RefreshEngineCommands();

        partial void OnSelectedDirectionChanged(StoryDirectionPreset? value)
        {
            if (value != null && !_loading) EngineDirection = value.Text;
            DeleteDirectionCommand?.NotifyCanExecuteChanged();
        }

        // ── ✨ Saved directions ─────────────────────────────────────────────────────────────────

        private ObservableCollection<StoryDirectionPreset> LoadSavedDirections()
        {
            var all = _settingsService.Settings;
            var list = all?.StoryDirections;
            if (list == null)
            {
                // First run: seed the starters (null means never seeded; an emptied list stays empty).
                list = StarterDirections.Select(d => new StoryDirectionPreset { Name = d.Name, Text = d.Text }).ToList();
                if (all != null) all.StoryDirections = list;
            }
            return new ObservableCollection<StoryDirectionPreset>(list.Where(d => !string.IsNullOrWhiteSpace(d.Text)));
        }

        private void PersistSavedDirections()
        {
            var all = _settingsService.Settings;
            if (all == null) return;
            all.StoryDirections = SavedDirections.ToList();
            try { _settingsService.SaveSettings(all); }
            catch (Exception ex) { _logger.LogWarning($"{Profile.Name}: could not save the story directions: {ex.Message}"); }
        }

        /// <summary>Puts a direction at the top of the drop-down — moved there if the same text is already saved.</summary>
        private void RememberDirection(string text)
        {
            text = (text ?? string.Empty).Trim();
            if (text.Length == 0) return;
            var list = SavedDirections;
            var existing = list.FirstOrDefault(d => string.Equals(d.Text.Trim(), text, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                var at = list.IndexOf(existing);
                if (at > 0) list.Move(at, 0);
            }
            else
            {
                list.Insert(0, new StoryDirectionPreset { Name = UniqueDirectionName(DirectionName(text)), Text = text });
                while (list.Count > MaxSavedDirections) list.RemoveAt(list.Count - 1);
            }
            PersistSavedDirections();
        }

        // "More action: Rewrite this scene…" → "More action"; otherwise the first few words.
        private static readonly Regex DirectionLabel = new(@"^\s*(?<name>[^:\r\n]{1,40}?)\s*:\s*\S", RegexOptions.Compiled);

        /// <summary>The drop-down name of a direction: its "Name:" prefix when it has one, else its first words.</summary>
        public static string DirectionName(string text)
        {
            text = (text ?? string.Empty).Trim();
            var m = DirectionLabel.Match(text);
            if (m.Success && m.Groups["name"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 5)
                return m.Groups["name"].Value.Trim();
            var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var full = string.Join(' ', words.Take(7));
            var name = full.Length > 48 ? full[..48].TrimEnd() : full;
            return words.Length > 7 || name.Length < full.Length ? name + "…" : name;
        }

        private string UniqueDirectionName(string name)
        {
            var taken = SavedDirections.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!taken.Contains(name)) return name;
            for (var n = 2; ; n++)
                if (!taken.Contains($"{name} ({n})")) return $"{name} ({n})";
        }

        /// <summary>✕ by the drop-down: forgets the picked direction (the text in the box stays).</summary>
        private void DeleteSelectedDirection()
        {
            var picked = SelectedDirection;
            if (picked == null) return;
            SelectedDirection = null;
            SavedDirections.Remove(picked);
            PersistSavedDirections();
        }
        partial void OnIsEngineWritingChanged(bool value) => RefreshCommands();
        partial void OnBeatSecondsChanged(double value) => OnPropertyChanged(nameof(EnginePlan));

        /// <summary>📂: a story file read into the story box.</summary>
        private async Task BrowseStoryFileAsync()
        {
            var path = await _fileDialogService.OpenFileDialogAsync("A story to turn into the video",
                "Text Files|*.txt;*.md;*.markdown;*.text|All Files|*.*",
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), persistKey: $"{Profile.Key}.story");
            if (path == null || !File.Exists(path)) return;
            LoadStoryFile(path);
        }

        /// <summary>Reads a story file into the story box — from 📂 or a file dropped on the box.</summary>
        public void LoadStoryFile(string path)
        {
            if (IsEngineWriting || !File.Exists(path)) return;
            try
            {
                var info = new FileInfo(path);
                if (info.Length > 2_000_000) { EngineStatus = $"{info.Name} is too big for a story (over 2 MB)."; return; }
                EngineStoryText = File.ReadAllText(path).Trim();
                var words = EngineStoryText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
                EngineStatus = $"Loaded {info.Name} — {words:N0} words." +
                               (EngineStoryText.Length > FflfStoryEngine.MaxStoryChars ? $" Only the first {FflfStoryEngine.MaxStoryChars:N0} characters are sent." : string.Empty);
                AddLog($"✨ Story loaded from {info.Name} ({words:N0} words).");
            }
            catch (Exception ex)
            {
                EngineStatus = $"Could not read the file: {ex.Message}";
            }
        }

        /// <summary>The one-click Generate: story → master prompt, location, wardrobe, beats and lines on the page.</summary>
        private async Task GenerateFromStoryAsync()
        {
            if (IsEngineWriting || IsProcessing || string.IsNullOrWhiteSpace(EngineStoryText)) return;
            var llm = _serviceProvider?.GetService<LMStudioService>();
            if (llm == null) { EngineStatus = "The LLM service is not available."; return; }

            _engineCts?.Cancel();
            var cts = new CancellationTokenSource();
            _engineCts = cts;
            var token = cts.Token;
            IsEngineWriting = true;
            RefreshEngineCommands();
            var started = DateTime.Now;
            try
            {
                var lm = _settingsService.Settings?.LMStudioSettings;
                var writer = EngineWriter;
                if (writer == WriterDeepSeek && !llm.HasDeepSeekKey)
                    throw new InvalidOperationException("no DeepSeek API key is saved — add one in Settings → LM Studio, or pick llama-server.");
                var toDeepSeek = writer == WriterDeepSeek || (writer == WriterSettings && lm?.DeepSeekActive == true);

                // The writer cannot see pictures: the opening image is described first (on the local vision model),
                // so the sheet is written around the frame the video really starts on.
                var openingImage = HasInitialImage ? await DescribeOpeningImageAsync(llm, token, status => EngineStatus = status) : null;

                using var route = writer switch
                {
                    WriterDeepSeek => llm.PreferDeepSeek(),
                    WriterLocal => llm.LocalOnly(),
                    _ => (IDisposable?)null,
                };

                var model = string.Empty;
                if (!toDeepSeek)
                {
                    EngineStatus = "Finding the writer model…";
                    await llm.SetBaseUrlAsync(lm?.BaseUrl ?? "http://localhost:8080");
                    var models = await llm.GetAvailableModelsAsync(token);
                    model = lm?.SelectedModel ?? string.Empty;
                    if (string.IsNullOrEmpty(model) && models.Count > 0) model = models[0].Id ?? models[0].Name ?? string.Empty;
                    if (string.IsNullOrEmpty(model))
                        throw new InvalidOperationException("no llama-server / LM Studio model is available. Make sure the server is running and a model is loaded, or pick DeepSeek.");
                }

                var promptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prompts", "prompt2json", FflfStoryEngine.SystemPromptFile);
                if (!File.Exists(promptPath)) throw new FileNotFoundException($"System prompt not found: {promptPath}");
                var system = await File.ReadAllTextAsync(promptPath, token);

                var beats = Math.Clamp(EngineBeatCount, FflfStoryEngine.MinBeats, FflfStoryEngine.MaxBeats);
                var dynamicTime = EngineDynamicBeatTime;
                var direction = EngineDirection.Trim();
                var request = FflfStoryEngine.BuildRequest(EngineStoryText, beats, BeatSeconds, EngineWithDialogue, HasInitialImage, openingImage,
                                                           dynamicBeatTime: dynamicTime, direction: direction);
                if (direction.Length > 0)
                {
                    OnUi(() => RememberDirection(direction));
                    AddLog($"✨ Extra direction: {(direction.Length > 160 ? direction[..160] + "…" : direction)}");
                }
                var target = toDeepSeek ? lm?.DescribeDeepSeekTarget() ?? "DeepSeek" : llm.DescribeTarget(model);
                AddLog(dynamicTime
                    ? $"✨ Writing {beats} beat(s), the writer picking each one's length (≈{beats * BeatSeconds:0.#} s in all), from the story ({EngineStoryText.Length:N0} chars) → {target}"
                    : $"✨ Writing {beats} beat(s) of {BeatSeconds:0.#} s from the story ({EngineStoryText.Length:N0} chars) → {target}");
                if (EngineStoryText.Length > FflfStoryEngine.MaxStoryChars)
                    AddLog($"✨ ⚠ The story is long — only its first {FflfStoryEngine.MaxStoryChars:N0} characters are sent.");
                EngineStatus = $"The writer is turning the story into {beats} beats…";

                var reply = await llm.SendTextChatAsync(model, system, request, FflfStoryEngine.MaxTokensFor(beats), token);
                var script = FflfStoryEngine.Parse(CleanLLMOutput(reply ?? string.Empty));
                token.ThrowIfCancellationRequested();

                OnUi(() => ApplyEngineScript(script));

                var missing = new[]
                {
                    script.MasterPrompt.Length == 0 ? "master prompt" : null,
                    script.Location.Length == 0 ? "location" : null,
                    script.Wardrobe.Length == 0 ? "wardrobe" : null,
                    EngineWithDialogue && !script.HasDialogue ? "dialogue" : null,
                }.Where(m => m != null).ToList();
                var note = script.Beats.Count == beats ? string.Empty : $" (asked for {beats})";
                var lines = script.Dialogue.Count(d => d.Length > 0);
                if (dynamicTime)
                {
                    var timed = script.BeatSeconds.Count(s => s > 0);
                    AddLog(timed > 0
                        ? $"⏱ Beat lengths from the writer: {string.Join(" / ", script.BeatSeconds.Select(s => s > 0 ? $"{s:0.#}" : $"{BeatSeconds:0.#}"))} s " +
                          $"= {script.BeatSeconds.Sum(s => s > 0 ? s : BeatSeconds):0.#} s." +
                          (timed < script.Beats.Count ? $" {script.Beats.Count - timed} beat(s) came back without one and use {BeatSeconds:0.#} s." : string.Empty)
                        : $"⏱ ⚠ The writer gave no beat lengths — every beat uses {BeatSeconds:0.#} s.");
                }
                EngineStatus = $"✔ {script.Beats.Count} beats{note}, {lines} spoken line{(lines == 1 ? "" : "s")} — read them through" +
                               (HasInitialImage ? ", then ▶ Generate." : ", pick the opening image, then ▶ Generate.") +
                               (missing.Count > 0 ? $" ⚠ No {string.Join(", ", missing)} came back." : string.Empty) +
                               (HasInitialImage && openingImage == null ? " ⚠ The opening image could not be described, so the sheet may not match it — see the log." : string.Empty);
                AddLog($"✨ Page filled in {(DateTime.Now - started):m\\:ss}: master prompt, location, wardrobe, {script.Beats.Count} beat(s){note}, {lines} line(s)." +
                       (missing.Count > 0 ? $" ⚠ Missing: {string.Join(", ", missing)}." : string.Empty) +
                       (lines > 0 ? " 🗣 Send all voices the lines." : string.Empty));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                EngineStatus = "Cancelled.";
            }
            catch (Exception ex)
            {
                EngineStatus = $"Failed: {ex.Message}";
                AddLog($"✨ Story engine failed: {ex.Message}");
                _logger.LogError(ex, $"{Profile.Name} story engine failed");
            }
            finally
            {
                OnUi(() =>
                {
                    if (ReferenceEquals(_engineCts, cts))
                    {
                        _engineCts = null;
                        IsEngineWriting = false;
                        RefreshEngineCommands();
                    }
                    cts.Dispose();
                });
            }
        }

        // The last description, kept (and saved) while the same image file — same path and timestamp — is the opening
        // image. Its A, B, C are the letters the beats and the character sheets use, so it is made once per image.
        private string? _openingDescription, _openingDescriptionFor;

        /// <summary>"path|last-write ticks": what the description and the character sheets were made from.</summary>
        private string OpeningImageKey => HasInitialImage ? $"{InitialImagePath}|{File.GetLastWriteTimeUtc(InitialImagePath).Ticks}" : string.Empty;

        /// <summary>
        /// The opening image in words, from the local vision model — null when it cannot be described, in which case
        /// the writer is only told that an opening image exists (logged and shown as a warning).
        /// </summary>
        private async Task<string?> DescribeOpeningImageAsync(LMStudioService llm, CancellationToken token, Action<string> status)
        {
            var path = InitialImagePath;
            var key = OpeningImageKey;
            if (key.Length == 0) return null;
            if (_openingDescriptionFor == key && !string.IsNullOrWhiteSpace(_openingDescription)) return _openingDescription;

            var lm = _settingsService.Settings?.LMStudioSettings;
            status("Looking at the opening image…");
            var started = DateTime.Now;
            try
            {
                using var local = llm.LocalOnly();
                await llm.SetBaseUrlAsync(lm?.BaseUrl ?? "http://localhost:8080");
                var model = lm?.SelectedModel ?? string.Empty;
                if (string.IsNullOrEmpty(model))
                {
                    var models = await llm.GetAvailableModelsAsync(token);
                    if (models.Count > 0) model = models[0].Id ?? models[0].Name ?? string.Empty;
                }
                var reply = await llm.AnalyzeImageAsync(model, path, FflfStoryEngine.OpeningImagePrompt, maxTokens: 1200, token);
                var description = CleanLLMOutput(reply ?? string.Empty).Trim();
                if (description.Length < 40) throw new InvalidOperationException("the vision model returned no description.");

                _openingDescription = description;
                _openingDescriptionFor = key;
                OnUi(Save);
                AddLog($"✨ Opening image described in {(DateTime.Now - started):m\\:ss} — the sheet will be written around it:\n{description}");
                return description;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                AddLog($"✨ ⚠ Could not describe the opening image ({ex.Message}). The writer will not know what it shows — " +
                       "load a vision model in llama-server (or set the vision switch model in Settings) and generate again.");
                _logger.LogWarning($"{Profile.Name} opening image description failed: {ex}");
                return null;
            }
        }

        /// <summary>The writer's sheet on the page, as a new story: the one there before stays in 📚 Stories.</summary>
        private void ApplyEngineScript(FflfStoryScript script)
        {
            if (IsProcessing) return;
            FlushStory();
            _loading = true;
            try
            {
                if (script.MasterPrompt.Length > 0) StyleText = script.MasterPrompt;
                if (script.Location.Length > 0) SettingText = script.Location;
                if (script.Wardrobe.Length > 0) WardrobeText = script.Wardrobe;
                BeatsText = FflfStoryEngine.ToBeatsText(script.Beats, script.BeatSeconds);

                var parsed = FflfGraph.ParseBeats(BeatsText);
                Beats.Clear();
                for (var i = 0; i < parsed.Count; i++)
                {
                    var beat = new FflfBeat(i + 1, parsed[i].Title, parsed[i].Text)
                    {
                        Seconds = parsed[i].Seconds,
                        LineText = i < script.Dialogue.Count ? script.Dialogue[i] : string.Empty,
                    };
                    Beats.Add(beat);
                }
                _runFolder = string.Empty;
                _uploadedInitial = null;
                JoinedVideoPath = string.Empty;
                BaseSeed = NewSeed();
            }
            finally { _loading = false; }

            SetActiveStory(null);
            Save();
            ScheduleStorySave();
            ActivePreview = string.Empty;
            OnPropertyChanged(nameof(ComposedStyle));
            OnPropertyChanged(nameof(BeatSummary));
            RefreshCommands();
        }
    }
}
