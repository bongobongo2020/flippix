using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using FlipPix.Core.Models;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// The FFLF story library, as 🌀 MiniMax I2V keeps its drafts: whatever is on the page — the opening image,
    /// the beats, the dials and every beat's take — lands on disk a second after it changes, comes back on the
    /// next launch, and stays in a searchable list of stories (📚 Stories) that any one of can be reopened from.
    ///
    /// <para>A story keeps its takes: reopened, its finished beats are finished again (as long as their videos and
    /// last frames are still there), so Generate carries on from the first beat without one.</para>
    ///
    /// <para>One story is one row. Editing the beats keeps updating the story they belong to; pasting a different
    /// story over them (see <see cref="MiniMaxI2VViewModel.IsSameDraft"/>) or ＋ New story starts another, and the
    /// one replaced stays in the list.</para>
    /// </summary>
    public partial class H3FflfViewModel : IDisposable
    {
        private const int StorySaveDelayMs = 1000;

        private readonly ScenePromptLibrary _storyLibrary;
        private readonly Task<List<ScenePrompt>> _storiesLoad;
        private ScenePrompt? _activeStory;
        private CancellationTokenSource? _storySaveCts;
        private bool _storyDirty;
        private int _storyCount;

        public RelayCommand OpenStoriesCommand { get; private set; } = null!;
        public RelayCommand NewStoryCommand { get; private set; } = null!;

        public string StoriesLabel => _storyCount > 0 ? $"📚 Stories ({_storyCount})" : "📚 Stories";

        /// <summary>The open story's name, for the header.</summary>
        public string StoryName => _activeStory?.Name ?? (string.IsNullOrWhiteSpace(BeatsText) ? "New story" : "Unsaved story");

        private void InitStories()
        {
            OpenStoriesCommand = new RelayCommand(() => _ = OpenStoriesAsync(), () => !IsProcessing);
            NewStoryCommand = new RelayCommand(NewStory, () => !IsProcessing);
            _ = RestoreStoryAsync();
        }

        // ── Saving ───────────────────────────────────────────────────────────────────────────────

        /// <summary>Marks the page changed; it is written a second after the last change.</summary>
        private void ScheduleStorySave()
        {
            if (_loading) return;
            _storyDirty = true;
            _storySaveCts?.Cancel();
            _storySaveCts = new CancellationTokenSource();
            _ = SaveStoryAfterDelayAsync(_storySaveCts.Token);
        }

        private async Task SaveStoryAfterDelayAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(StorySaveDelayMs, token);
                var stories = await _storiesLoad;
                if (token.IsCancellationRequested) return;
                var thumbnailFor = CommitStory(stories);
                if (thumbnailFor == null) return;
                await _storyLibrary.SaveAsync(stories);

                // The opening image as the list's thumbnail, made once per image.
                var story = thumbnailFor;
                var source = story.ReferenceImagePaths.FirstOrDefault(File.Exists);
                if (source == null || story.SceneImagePath == source) return;
                var thumbnail = await Task.Run(() => _storyLibrary.CreateThumbnail(source, story.Id));
                if (thumbnail.Length == 0) return;
                story.ThumbnailFile = thumbnail;
                story.SceneImagePath = source;
                await _storyLibrary.SaveAsync(stories);
            }
            catch (OperationCanceledException)
            {
                // Changed again; the newer call carries the save.
            }
            catch (Exception ex)
            {
                AddLog($"Could not autosave the story: {ex.Message}");
            }
        }

        /// <summary>Writes a save still waiting out its delay. Blocks: the caller is about to rely on it.</summary>
        private void FlushStory()
        {
            _storySaveCts?.Cancel();
            var stories = _storiesLoad.GetAwaiter().GetResult();
            if (CommitStory(stories) != null) _storyLibrary.Save(stories);
        }

        /// <summary>Folds the page into the story list. Returns the story written, or null when nothing changed.</summary>
        private ScenePrompt? CommitStory(List<ScenePrompt> stories)
        {
            if (!_storyDirty) return null;
            _storyDirty = false;

            var body = (BeatsText ?? string.Empty).Trim();
            if (body.Length == 0)
            {
                // A cleared page stays cleared on the next launch; the story it held stays in the list.
                SetActiveStory(null);
                return null;
            }

            var story = _activeStory;
            if (story != null && (!stories.Contains(story) || !MiniMaxI2VViewModel.IsSameDraft(story.Prompt, body))) story = null;

            if (story == null)
            {
                story = new ScenePrompt { Name = SuggestStoryName(body, stories) };
                stories.Insert(0, story);
            }
            else
            {
                // The name follows the opening words until the user gives it one of their own.
                var others = stories.Where(s => !ReferenceEquals(s, story)).ToList();
                if (story.Name == SuggestStoryName(story.Prompt, others)) story.Name = SuggestStoryName(body, others);
            }

            story.Prompt = body;
            story.StoryText = body;
            story.ReferenceImagePaths = HasInitialImage ? new List<string> { InitialImagePath } : new List<string>();
            story.AspectRatio = SelectedAspectRatio;
            story.LengthSeconds = BeatSeconds;
            story.Shots = Beats.Count;
            story.StoryDurationSeconds = Beats.Count * BeatSeconds;
            story.LastUsed = DateTime.Now;
            story.Fflf = Snapshot();

            SetActiveStory(story);
            SetStoryCount(stories.Count);
            return story;
        }

        /// <summary>The first beat's title when it has one, otherwise its opening words.</summary>
        private static string SuggestStoryName(string body, IEnumerable<ScenePrompt> others)
        {
            var first = FflfGraph.ParseBeats(body).FirstOrDefault();
            var seed = first == null ? body : first.Title.Length > 0 ? first.Title : first.Text;
            return ScenePromptLibrary.SuggestName(string.Empty, seed, others);
        }

        private FflfStory Snapshot() => new()
        {
            InitialImagePath = InitialImagePath,
            BeatsText = BeatsText,
            StyleText = StyleText,
            BeatSeconds = BeatSeconds,
            AspectRatio = SelectedAspectRatio,
            FirstPassMegapixels = FirstPassMegapixels,
            FinalMegapixels = FinalMegapixels,
            PreviewOnly = PreviewOnly,
            DiffusionModel = DiffusionModel,
            Lora = Lora,
            LoraStrength = LoraStrength,
            FirstPassSteps = FirstPassSteps,
            LockFirstFrame = LockFirstFrame,
            KeepInitialReference = KeepInitialReference,
            MaxFidelityReferences = MaxFidelityReferences,
            RepairHandoffFace = RepairHandoffFace,
            FaceRepairStrength = FaceRepairStrength,
            JoinWhenDone = JoinWhenDone,
            BaseSeed = BaseSeed,
            RunFolder = _runFolder,
            JoinedVideoPath = JoinedVideoPath,
            Beats = Beats.Select(b => new FflfStoryBeat
            {
                Text = b.Text,
                Seed = b.Seed,
                Done = b.IsDone,
                VideoPath = b.VideoPath,
                LastFramePath = b.LastFramePath,
                Status = b.IsDone ? b.Status : string.Empty,
            }).ToList(),
        };

        private void SetActiveStory(ScenePrompt? story)
        {
            _activeStory = story;
            OnPropertyChanged(nameof(StoryName));
            var settings = _settingsService.Settings;
            var id = story?.Id ?? string.Empty;
            if (settings == null || (settings.H3Fflf ??= new H3FflfSettings()).ActiveStoryId == id) return;
            settings.H3Fflf.ActiveStoryId = id;
            try { _settingsService.SaveSettings(settings); }
            catch (Exception ex) { _logger.LogWarning($"FFLF: could not save settings: {ex.Message}"); }
        }

        private void SetStoryCount(int count)
        {
            if (_storyCount == count) return;
            _storyCount = count;
            OnPropertyChanged(nameof(StoriesLabel));
        }

        // ── Restoring ────────────────────────────────────────────────────────────────────────────

        private async Task RestoreStoryAsync()
        {
            var stories = await _storiesLoad;
            OnUi(() =>
            {
                SetStoryCount(stories.Count);
                var id = _settingsService.Settings?.H3Fflf?.ActiveStoryId;
                if (!string.IsNullOrEmpty(id) && stories.FirstOrDefault(s => s.Id == id) is { Fflf: not null } story)
                {
                    ApplyStory(story);
                    var done = Beats.Count(b => b.IsDone);
                    AddLog($"Restored the story \"{story.Name}\"" + (done > 0 ? $" — {done} of {Beats.Count} beats already rendered." : "."));
                }
                else if (!string.IsNullOrWhiteSpace(BeatsText))
                {
                    // Beats typed before the library existed become its first story.
                    ScheduleStorySave();
                }
            });
        }

        /// <summary>Puts a saved story on the page, takes and all.</summary>
        private void ApplyStory(ScenePrompt entry)
        {
            if (IsProcessing || entry.Fflf is not { } s) return;
            _storySaveCts?.Cancel();
            _storyDirty = false;

            _loading = true;
            try
            {
                InitialImagePath = File.Exists(s.InitialImagePath) ? s.InitialImagePath : string.Empty;
                BeatsText = s.BeatsText;
                StyleText = s.StyleText;
                BeatSeconds = Math.Clamp(s.BeatSeconds, 1, 20);
                SelectedAspectRatio = AspectRatioOptions.Contains(s.AspectRatio) ? s.AspectRatio : H3Canvas.AutoAspect;
                FirstPassMegapixels = FirstPassSizes.Any(o => o.Value == s.FirstPassMegapixels) ? s.FirstPassMegapixels : 0.3;
                FinalMegapixels = FinalSizes.Any(o => o.Value == s.FinalMegapixels) ? s.FinalMegapixels : 1.0;
                PreviewOnly = s.PreviewOnly;
                DiffusionModel = string.IsNullOrWhiteSpace(s.DiffusionModel) ? H3FflfSettings.DefaultModel : s.DiffusionModel;
                Lora = s.Lora ?? string.Empty;
                OfferModel(DiffusionModel);
                OfferLora(Lora);
                LoraStrength = Math.Clamp(s.LoraStrength, 0, 2);
                FirstPassSteps = Math.Clamp(s.FirstPassSteps, 1, 60);
                LockFirstFrame = s.LockFirstFrame;
                KeepInitialReference = s.KeepInitialReference;
                MaxFidelityReferences = s.MaxFidelityReferences;
                RepairHandoffFace = s.RepairHandoffFace;
                FaceRepairStrength = ClampRepairStrength(s.FaceRepairStrength);
                JoinWhenDone = s.JoinWhenDone;
                BaseSeed = s.BaseSeed > 0 ? s.BaseSeed : NewSeed();
                _runFolder = s.RunFolder ?? string.Empty;
                _uploadedInitial = null;

                // The beats, then each one's take for as long as the chain is unbroken: a beat started on the
                // last frame of the one before, so one missing take ends what can be carried on from.
                var parsed = FflfGraph.ParseBeats(BeatsText);
                Beats.Clear();
                var chainIntact = true;
                for (var i = 0; i < parsed.Count; i++)
                {
                    var beat = new FflfBeat(i + 1, parsed[i].Title, parsed[i].Text);
                    if (i < s.Beats.Count && s.Beats[i].Text == beat.Text)
                    {
                        var saved = s.Beats[i];
                        beat.Seed = saved.Seed;
                        chainIntact &= saved.Done && File.Exists(saved.VideoPath) && File.Exists(saved.LastFramePath);
                        if (chainIntact)
                        {
                            beat.VideoPath = saved.VideoPath;
                            beat.LastFramePath = saved.LastFramePath;
                            beat.RenderedText = beat.Text;
                            beat.Status = saved.Status;
                            beat.State = FflfBeatState.Done;
                        }
                    }
                    else chainIntact = false;
                    Beats.Add(beat);
                }
                JoinedVideoPath = File.Exists(s.JoinedVideoPath) && Beats.All(b => b.IsDone) ? s.JoinedVideoPath : string.Empty;
            }
            finally
            {
                _loading = false;
            }

            _activeStory = entry;
            SetActiveStory(entry);
            Save();
            OnPropertyChanged(nameof(BeatSummary));
            OnPropertyChanged(nameof(CanvasSummary));
            RefreshCommands();

            var last = HasJoined ? JoinedVideoPath : Beats.LastOrDefault(b => b.HasVideo)?.VideoPath;
            if (last != null) Show(last);
            else ActivePreview = string.Empty;
        }

        // ── Commands ─────────────────────────────────────────────────────────────────────────────

        private void NewStory()
        {
            if (IsProcessing) return;
            FlushStory();
            _loading = true;
            try
            {
                BeatsText = string.Empty;
                StyleText = string.Empty;
                InitialImagePath = string.Empty;
                Beats.Clear();
                _runFolder = string.Empty;
                _uploadedInitial = null;
                JoinedVideoPath = string.Empty;
                BaseSeed = NewSeed();
            }
            finally { _loading = false; }
            SetActiveStory(null);
            Save();
            ActivePreview = string.Empty;
            OnPropertyChanged(nameof(BeatSummary));
            RefreshCommands();
            AddLog("New story — the last one is in 📚 Stories.");
        }

        /// <summary>The beats edited in the Stories list: the story's own copy follows, so opening it (or the
        /// page, if it is the open story) shows the edit. Takes stay with beats whose text is unchanged.</summary>
        private static void StoryEditedInList(ScenePrompt entry)
        {
            entry.StoryText = entry.Prompt;
            if (entry.Fflf is not { } story) return;
            story.BeatsText = entry.Prompt;
            var beats = FflfGraph.ParseBeats(entry.Prompt);
            entry.Shots = beats.Count;
            entry.StoryDurationSeconds = beats.Count * story.BeatSeconds;
            story.JoinedVideoPath = string.Empty;
        }

        private async Task OpenStoriesAsync()
        {
            if (IsProcessing) return;
            try
            {
                var stories = await _storiesLoad;
                // The list should already show what was typed a moment ago.
                FlushStory();

                var window = new ScenePromptLibraryWindow(_storyLibrary, stories, new ScenePromptLibraryChrome
                {
                    WindowTitle = "Stories — FFLF",
                    Heading = "Stories",
                    Subtitle = "Every story on the FFLF page is saved here as you go — the beats, the opening image, the settings "
                             + "and each beat's take — and survives a restart. Pick one to carry on with it.",
                    PromptNote = "Edit the beats right here — saved as soon as you click away. Finished beats come back finished "
                               + "as long as their text, videos and last frames are unchanged; Generate carries on from the first beat without a take.",
                    PromptEditable = true,
                    PromptEdited = StoryEditedInList,
                    EmptyText = "No stories yet.\nPaste beats on the FFLF page and they land here.",
                    NoMatchText = "No story matches that search.",
                    UseButtonText = "✓ Open This Story",
                    LibraryNoun = "stories",
                    AccentColorKey = "AccentIndigoColor",
                });

                window.Owner = Application.Current?.Windows
                    .OfType<System.Windows.Window>()
                    .FirstOrDefault(w => w.IsActive);
                if (window.Owner == null)
                    window.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;

                var picked = window.ShowDialog() == true ? window.SelectedScene : null;
                SetStoryCount(stories.Count);

                if (picked?.Fflf != null)
                {
                    ApplyStory(picked);
                    picked.LastUsed = DateTime.Now;
                    picked.UseCount++;
                    await _storyLibrary.SaveAsync(stories);
                    AddLog($"Opened the story \"{picked.Name}\" — {Beats.Count(b => b.IsDone)} of {Beats.Count} beats rendered.");
                }
                else if (_activeStory != null && !stories.Contains(_activeStory))
                {
                    // The open story was deleted in the list: the page stays, as an unsaved story.
                    SetActiveStory(null);
                }
                else if (_activeStory?.Fflf is { } open && open.BeatsText != BeatsText)
                {
                    // The open story was edited in the list: show the edit, or the next autosave would undo it.
                    ApplyStory(_activeStory);
                    AddLog($"\"{_activeStory.Name}\" was edited in Stories — the page now shows the new beats.");
                }
            }
            catch (Exception ex)
            {
                AddLog($"Stories failed to open: {ex.Message}");
                MessageBox.Show($"Could not open the stories:\n{ex.Message}", "Stories", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void Dispose()
        {
            _comfyUIService.LivePreviewReceived -= OnLivePreviewReceived;
            try { FlushStory(); }
            catch (Exception ex) { AddLog($"Could not autosave the story: {ex.Message}"); }
        }
    }
}
