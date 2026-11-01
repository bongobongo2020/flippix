using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// Autosave for the idea box. The prompt library only hears about a prompt once Analyze or a render has
    /// happened, and Analyze overwrites the box — so the idea it was written from, or one that never got
    /// that far, existed nowhere. Whatever is typed here lands on disk a moment later, comes back in the box
    /// on the next launch, and stays in a searchable history of its own.
    ///
    /// <para>One line of thought is one row: typing keeps updating the row it started, and only text that
    /// has been replaced outright (see <see cref="IsSameDraft"/>) starts another. A separate store from the
    /// takes, because half-written ideas would bury them.</para>
    ///
    /// <para>Text that came from the prompt library or from Analyze is not a draft: the library already
    /// holds it, so it only becomes one once it is edited. Until then the next launch restores it from the
    /// library (see <see cref="FillBoxesWithTake"/>).</para>
    /// </summary>
    public partial class MiniMaxI2VViewModel : IDisposable
    {
        /// <summary>Quiet time after the last keystroke before the box is written out.</summary>
        private const int DraftSaveDelayMs = 1000;

        private ScenePromptLibrary _draftLibrary = null!;
        private Task<List<ScenePrompt>> _draftsLoad = null!;
        private ScenePrompt? _activeDraft;
        /// <summary>What the boxes held when a library take was put in them; null once edited.</summary>
        private string? _takeInBoxes;
        private CancellationTokenSource? _draftSaveCts;
        private bool _draftDirty;
        private int _draftCount;

        public RelayCommand OpenDraftsCommand { get; private set; } = null!;

        public string DraftsLabel => _draftCount > 0 ? $"📝 Drafts ({_draftCount})" : "📝 Drafts";

        private void InitDrafts()
        {
            _draftLibrary = new ScenePromptLibrary(AddLog, ScenePromptLibrary.FolderFor("minimax-i2v-drafts"));
            _draftsLoad = _draftLibrary.LoadAsync();
            OpenDraftsCommand = new RelayCommand(async () => await OpenDraftsAsync());
            _ = RestoreDraftAsync();
        }

        /// <summary>
        /// Whether <paramref name="updated"/> is still the same draft as <paramref name="current"/>: at least
        /// half of the shorter text survives at its start and end, which holds for typing and for editing in
        /// the middle. Pasting over the box or Analyze's reply fails it, and so keep the text they replaced.
        /// </summary>
        public static bool IsSameDraft(string current, string updated)
        {
            var shorter = Math.Min(current.Length, updated.Length);
            if (shorter == 0) return false;

            var prefix = 0;
            while (prefix < shorter && current[prefix] == updated[prefix]) prefix++;

            var suffix = 0;
            while (suffix < shorter - prefix && current[^(suffix + 1)] == updated[^(suffix + 1)]) suffix++;

            return (prefix + suffix) * 2 >= shorter;
        }

        private void ScheduleDraftSave()
        {
            _draftDirty = true;
            _draftSaveCts?.Cancel();
            _draftSaveCts = new CancellationTokenSource();
            _ = SaveDraftAfterDelayAsync(_draftSaveCts.Token);
        }

        private async Task SaveDraftAfterDelayAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(DraftSaveDelayMs, token);
                var drafts = await _draftsLoad;
                if (token.IsCancellationRequested || !CommitDraft(drafts)) return;
                await _draftLibrary.SaveAsync(drafts);

                // Encoded here rather than in CommitDraft, which also runs on the way out of the app.
                var draft = _activeDraft;
                if (draft == null || draft.ThumbnailFile.Length > 0) return;
                var source = draft.ReferenceImagePaths.Where(File.Exists).Take(2).LastOrDefault();
                if (source == null) return;

                var thumbnail = await Task.Run(() => _draftLibrary.CreateThumbnail(source, draft.Id));
                if (thumbnail.Length == 0) return;
                draft.ThumbnailFile = thumbnail;
                await _draftLibrary.SaveAsync(drafts);
            }
            catch (OperationCanceledException)
            {
                // Typed again; the newer call carries the save.
            }
            catch (Exception ex)
            {
                AddLog($"Could not autosave the draft: {ex.Message}");
            }
        }

        /// <summary>Writes a save still waiting out its delay. Blocks: the caller is about to rely on it.</summary>
        private void FlushDraft()
        {
            _draftSaveCts?.Cancel();
            var drafts = _draftsLoad.GetAwaiter().GetResult();
            if (CommitDraft(drafts)) _draftLibrary.Save(drafts);
        }

        /// <summary>Folds the boxes into the draft list. Returns whether the list needs writing.</summary>
        private bool CommitDraft(List<ScenePrompt> drafts)
        {
            if (!_draftDirty) return false;
            _draftDirty = false;

            var body = (Prompt ?? string.Empty).Trim();
            if (body.Length == 0)
            {
                // A cleared box stays cleared on the next launch; the text it held stays in the history.
                _takeInBoxes = null;
                SetActiveDraft(null);
                return false;
            }

            var segments = Continuations.Select(c => (c.Prompt ?? string.Empty).Trim()).ToList();
            var seconds = Continuations.Select(c => c.Seconds).ToList();
            while (segments.Count > 0 && segments[^1].Length == 0)
            {
                segments.RemoveAt(segments.Count - 1);
                seconds.RemoveAt(seconds.Count - 1);
            }

            if (_takeInBoxes == BoxesText()) return false;
            _takeInBoxes = null;

            var draft = _activeDraft;
            if (draft != null && (!drafts.Contains(draft) || !IsSameDraft(draft.Prompt, body))) draft = null;
            if (draft != null && draft.Prompt == body && draft.ContinuationPrompts.SequenceEqual(segments)) return false;

            if (draft == null)
            {
                draft = _draftLibrary.AddOrRefresh(drafts, new ScenePrompt
                {
                    Name = ScenePromptLibrary.SuggestName(string.Empty, body, drafts),
                    Prompt = body,
                    ContinuationPrompts = segments,
                }).Entry;
            }
            else
            {
                // The name follows the opening words until the user gives it one of their own.
                var others = drafts.Where(d => !ReferenceEquals(d, draft)).ToList();
                if (draft.Name == ScenePromptLibrary.SuggestName(string.Empty, draft.Prompt, others))
                    draft.Name = ScenePromptLibrary.SuggestName(string.Empty, body, others);
            }

            draft.Prompt = body;
            draft.ContinuationPrompts = segments;
            draft.ContinuationSeconds = seconds;
            draft.ReferenceImagePaths = FilledReferences.Select(r => r.Path).ToList();
            // The choice rather than ResolvedAspectRatio: Auto has to come back as Auto.
            draft.AspectRatio = SelectedAspectRatio;
            draft.LengthSeconds = LengthSeconds;
            draft.LastUsed = DateTime.Now;

            SetActiveDraft(draft);
            SetDraftCount(drafts.Count);
            return true;
        }

        /// <summary>Records which draft the box holds, which is what the next launch puts back.</summary>
        private void SetActiveDraft(ScenePrompt? draft)
        {
            _activeDraft = draft;
            RememberBoxEntry(draft?.Id ?? string.Empty);
        }

        private void RememberBoxEntry(string id)
        {
            var settings = _settingsService.Settings;
            if (settings == null || settings.MiniMaxI2VActiveDraftId == id) return;
            settings.MiniMaxI2VActiveDraftId = id;
            _settingsService.SaveSettings(settings);
        }

        private void SetDraftCount(int count)
        {
            if (_draftCount == count) return;
            _draftCount = count;
            OnPropertyChanged(nameof(DraftsLabel));
        }

        private async Task RestoreDraftAsync()
        {
            var drafts = await _draftsLoad;
            await EnsurePromptsLoadedAsync();
            OnUiThread(() =>
            {
                SetDraftCount(drafts.Count);

                var id = _settingsService.Settings?.MiniMaxI2VActiveDraftId;
                if (string.IsNullOrEmpty(id) || !string.IsNullOrWhiteSpace(Prompt)) return;

                if (drafts.FirstOrDefault(d => d.Id == id) is { } draft)
                {
                    ApplyDraft(draft);
                    AddLog($"Restored your draft \"{draft.Name}\".");
                }
                else if (_savedPrompts?.FirstOrDefault(t => t.Id == id) is { } take)
                {
                    // Not its aspect: the library records what Auto resolved to, and this box may be on Auto.
                    FillBoxesWithTake(() =>
                    {
                        Prompt = take.Prompt;
                        RestoreContinuations(take.ContinuationPrompts, take.ContinuationSeconds);
                        if (take.LengthSeconds > 0) LengthSeconds = (int)Math.Round(take.LengthSeconds);
                    }, take.Id);
                    AddLog($"Restored \"{take.Name}\" from the prompt library.");
                }
            });
        }

        private void ApplyDraft(ScenePrompt draft)
        {
            // Active first, so what the boxes report back below reads as no change rather than a new row.
            SetActiveDraft(draft);
            Prompt = draft.Prompt;
            RestoreContinuations(draft.ContinuationPrompts, draft.ContinuationSeconds);
            if (draft.LengthSeconds > 0) LengthSeconds = (int)Math.Round(draft.LengthSeconds);
            if (AspectRatioOptions.Contains(draft.AspectRatio)) SelectedAspectRatio = draft.AspectRatio;
        }

        /// <summary>
        /// Replaces the boxes with text the prompt library holds, without filing it as a draft. Whatever
        /// was typed there is saved first, since it may not have reached disk yet. Pass
        /// <paramref name="takeId"/> when the library entry is known; otherwise see <see cref="RememberTakeInBoxes"/>.
        /// </summary>
        private void FillBoxesWithTake(Action fill, string? takeId = null)
        {
            FlushDraft();
            fill();

            _draftSaveCts?.Cancel();
            _draftDirty = false;
            _takeInBoxes = BoxesText();
            _activeDraft = null;
            RememberBoxEntry(takeId ?? string.Empty);
        }

        /// <summary>Points the next launch at a take just filed, unless the boxes were edited meanwhile.</summary>
        private void RememberTakeInBoxes(ScenePrompt? take)
        {
            if (take != null && _takeInBoxes != null) RememberBoxEntry(take.Id);
        }

        private string BoxesText() => string.Join("\n",
            new[] { Prompt }.Concat(Continuations.Select(c => c.Prompt))
                .Select(p => (p ?? string.Empty).Trim())
                .Reverse().SkipWhile(p => p.Length == 0).Reverse());

        private async Task OpenDraftsAsync()
        {
            try
            {
                var drafts = await _draftsLoad;
                // The picker should already list what was typed a moment ago.
                FlushDraft();

                var window = new ScenePromptLibraryWindow(_draftLibrary, drafts, new ScenePromptLibraryChrome
                {
                    WindowTitle = "Drafts — MiniMax I2V",
                    Heading = "Drafts",
                    Subtitle = "Everything typed in the idea box is saved here as you go, and survives a restart. "
                             + "Pick one to put it back in the boxes and carry on editing it.",
                    PromptNote = "The reference pictures are not restored — load your own, and keep the "
                               + "<Picture N> order the text expects.",
                    EmptyText = "No drafts yet.\nType in the idea box and it lands here.",
                    NoMatchText = "No draft matches that search.",
                    UseButtonText = "✓ Use This Draft",
                    LibraryNoun = "drafts",
                    AccentColorKey = "AccentAmberColor",
                });

                window.Owner = Application.Current?.Windows
                    .OfType<System.Windows.Window>()
                    .FirstOrDefault(w => w.IsActive);
                if (window.Owner == null)
                    window.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;

                var picked = window.ShowDialog() == true ? window.SelectedScene : null;
                SetDraftCount(drafts.Count);

                if (picked != null)
                {
                    ApplyDraft(picked);
                    AddLog($"Loaded the draft \"{picked.Name}\".");
                }
                else if (_activeDraft != null && !drafts.Contains(_activeDraft))
                {
                    SetActiveDraft(null);
                }
            }
            catch (Exception ex)
            {
                AddLog($"Drafts failed to open: {ex.Message}");
                MessageBox.Show($"Could not open the drafts:\n{ex.Message}",
                    "Drafts", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void Dispose()
        {
            try
            {
                FlushDraft();
            }
            catch (Exception ex)
            {
                AddLog($"Could not autosave the draft: {ex.Message}");
            }
        }
    }
}
