using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        [ObservableProperty] private string _engineWriter = WriterSettings;

        /// <summary>Off: the engine writes the beats without spoken lines.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(EnginePlan))]
        private bool _engineWithDialogue = true;

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
                var plan = $"{beats} beat{(beats == 1 ? "" : "s")} × {BeatSeconds:0.#} s = {beats * BeatSeconds:0.#} s";
                return EngineWithDialogue ? $"{plan} · ≈{FflfStoryEngine.DialogueWords(BeatSeconds).Target} spoken words a beat" : plan;
            }
        }

        public RelayCommand GenerateFromStoryCommand { get; private set; } = null!;
        public RelayCommand BrowseStoryFileCommand { get; private set; } = null!;
        public RelayCommand CancelEngineCommand { get; private set; } = null!;

        /// <summary>Runs in the constructor, while the page is still loading.</summary>
        private void InitStoryEngine(H3FflfSettings s)
        {
            GenerateFromStoryCommand = new RelayCommand(() => _ = GenerateFromStoryAsync(),
                () => !IsProcessing && !IsEngineWriting && !string.IsNullOrWhiteSpace(EngineStoryText));
            BrowseStoryFileCommand = new RelayCommand(() => _ = BrowseStoryFileAsync(), () => !IsEngineWriting);
            CancelEngineCommand = new RelayCommand(() => _engineCts?.Cancel(), () => IsEngineWriting);

#pragma warning disable MVVMTK0034
            _engineStoryText = s.StoryEngineText ?? string.Empty;
            _engineBeatCount = Math.Clamp(s.StoryEngineBeats, FflfStoryEngine.MinBeats, FflfStoryEngine.MaxBeats);
            _engineWriter = EngineWriters.Any(w => w.Id == s.StoryEngineWriter) ? s.StoryEngineWriter : WriterSettings;
            _engineWithDialogue = s.StoryEngineDialogue;
#pragma warning restore MVVMTK0034
        }

        private void SaveStoryEngine(H3FflfSettings s)
        {
            s.StoryEngineText = EngineStoryText;
            s.StoryEngineBeats = EngineBeatCount;
            s.StoryEngineWriter = EngineWriter;
            s.StoryEngineDialogue = EngineWithDialogue;
        }

        private void RefreshEngineCommands()
        {
            GenerateFromStoryCommand?.NotifyCanExecuteChanged();
            BrowseStoryFileCommand?.NotifyCanExecuteChanged();
            CancelEngineCommand?.NotifyCanExecuteChanged();
        }

        partial void OnEngineStoryTextChanged(string value) => RefreshEngineCommands();
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
                var request = FflfStoryEngine.BuildRequest(EngineStoryText, beats, BeatSeconds, EngineWithDialogue, HasInitialImage);
                var target = toDeepSeek ? lm?.DescribeDeepSeekTarget() ?? "DeepSeek" : llm.DescribeTarget(model);
                AddLog($"✨ Writing {beats} beat(s) of {BeatSeconds:0.#} s from the story ({EngineStoryText.Length:N0} chars) → {target}");
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
                EngineStatus = $"✔ {script.Beats.Count} beats{note}, {lines} spoken line{(lines == 1 ? "" : "s")} — read them through" +
                               (HasInitialImage ? ", then ▶ Generate." : ", pick the opening image, then ▶ Generate.") +
                               (missing.Count > 0 ? $" ⚠ No {string.Join(", ", missing)} came back." : string.Empty);
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
                BeatsText = FflfStoryEngine.ToBeatsText(script.Beats);

                var parsed = FflfGraph.ParseBeats(BeatsText);
                Beats.Clear();
                for (var i = 0; i < parsed.Count; i++)
                {
                    var beat = new FflfBeat(i + 1, parsed[i].Title, parsed[i].Text)
                    {
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
