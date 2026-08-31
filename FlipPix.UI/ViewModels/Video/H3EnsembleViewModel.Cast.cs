using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FlipPix.ComfyUI.Models;
using FlipPix.ComfyUI.Services;
using FlipPix.UI.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// H3 Ensemble, part five: the cast as the story itself writes it, and the cast photos as the
    /// Image Generator's own text-to-image workflows produce them.
    ///
    /// <para><b>Reading the cast out of the story.</b> A story uploaded or pasted into the story box
    /// is already everything the tab needs to know <i>who</i> is in the film — it was only asking the
    /// user to type it back in by hand. A debounced llama-server pass (the same 2.5 s one the wardrobe
    /// uses) now lists the story's characters — their kind, their name and a one-line description — and
    /// fills the empty cast cards with them. It never touches a card the user has made their own: a
    /// photo, a hand-written Part, or a hand-picked Kind mark a slot as spoken for, and a slot this pass
    /// filled earlier is only rewritten while its Kind and Part still say exactly what this pass left
    /// there. The wardrobe pass picks the new cast up on its own — filling a card fires the same change
    /// event browsing for a photo does.</para>
    ///
    /// <para><b>Generating the photo.</b> Each card's ✨ Generate button renders that character's
    /// portrait with one of the Image Generator tab's base graphs — Z-Image, Krea2 realism, or Qwen
    /// 2.5.1.2 — from the character's Part plus the locked wardrobe, so the photo arrives already
    /// wearing the outfit the character sheet will be built in. The result is dropped into the card's
    /// photo slot as if it had been browsed for: Build Character Sheets, the reference wiring and the
    /// face-refine pass all work on it unchanged.</para>
    /// </summary>
    public partial class H3EnsembleViewModel
    {
        #region Cast detection — the story names them, the tab casts them

        private bool _isDerivingCast;
        private CancellationTokenSource? _castCts;

        /// <summary>slot index → <c>"kind|role"</c> as the automatic pass last wrote it. A slot whose
        /// live Kind/Part still matches its stamp is one the user has not touched since, so a later
        /// pass may rewrite it; anything else is the user's own work and stands.</summary>
        private readonly Dictionary<int, string> _autoCastStamp = new();

        /// <summary>
        /// Debounced, like the wardrobe: fires 2.5 s after the story stops changing (typing, pasting
        /// and 📄 Load .txt all land here through the <see cref="StoryText"/> setter).
        /// </summary>
        private void ScheduleCastDerive()
        {
            _castCts?.Cancel();
            _castCts = null;

            if (!HasStoryText) return;

            var cts = new CancellationTokenSource();
            _castCts = cts;
            _ = AutoDeriveCastAsync(cts);
        }

        private async Task AutoDeriveCastAsync(CancellationTokenSource cts)
        {
            var token = cts.Token;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2.5), token);

                // Another model pass is in flight — come back when it lands rather than queuing a
                // second llama-server turn behind it. The user typing again cancels and restarts this.
                if (_isDerivingCast || _isDerivingWardrobe || IsAnalyzing)
                {
                    ScheduleCastDerive();
                    return;
                }

                // Silent when there is nothing free to fill: every card already says something the
                // user put there, and "your cast is full" is not news.
                if (_cast.All(c => !SlotIsFree(c))) return;

                _isDerivingCast = true;
                IsAnalyzing = true;
                try
                {
                    var model = await ResolveLlmModelAsync(token, quiet: true);
                    if (model == null) return;

                    AnalyzePhase = "Reading the cast out of the story…";
                    AddLog($"Reading the cast out of the story — sending to {_lmStudioService.DescribeTarget(model)}…");
                    var reply = await AskCastAsync(model, token);
                    token.ThrowIfCancellationRequested();

                    var detected = CastPhotoWorkflows.ParseCastLines(reply, MaxCharacters);
                    if (detected.Count == 0)
                    {
                        AddLog("The cast could not be read out of the story — set Kind and Part on the " +
                               "cards by hand.");
                        return;
                    }
                    ApplyDetectedCast(detected);
                }
                finally
                {
                    _isDerivingCast = false;
                    IsAnalyzing = false;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AddLog($"Automatic cast pass failed: {ex.Message}");
            }
            finally
            {
                if (ReferenceEquals(_castCts, cts)) _castCts = null;
                cts.Dispose();
            }
        }

        /// <summary>The one llama-server turn, shared with the H3 Cast tab: list the story's
        /// characters, most important first — every kind this tab's cards offer.</summary>
        private Task<string> AskCastAsync(string model, CancellationToken token) =>
            CastPhotoWorkflows.AskCastAsync(
                _lmStudioService, model, StoryText, MaxCharacters, personKindsOnly: false, token);

        /// <summary>
        /// Whether the automatic cast pass may write this card: never one with a photo (browsed or
        /// generated — a photo is the user saying "this one is cast"), and otherwise either a pristine
        /// card (default person kind, no Part) or one this pass itself filled and the user has not
        /// touched since.
        /// </summary>
        private bool SlotIsFree(CharacterSlot c)
        {
            if (c.HasSource) return false;
            if (_autoCastStamp.TryGetValue(c.Index, out var stamp))
                return stamp == $"{c.Kind}|{c.Role}";
            // A hand-picked non-person kind with no Part yet is still an explicit act — leave it.
            return !c.HasRole && (c.Kind == CharacterSlot.Male || c.Kind == CharacterSlot.Female);
        }

        /// <summary>
        /// Writes the detected characters into the free cards, in slot order, and retires the stale
        /// auto-filled ones a shorter story no longer lists. Everything fires through the normal
        /// property setters, so the cast summaries, the wardrobe pass and the reference maths all
        /// react exactly as if the user had typed the cards in by hand.
        /// </summary>
        private void ApplyDetectedCast(IReadOnlyList<(string Kind, string Role)> detected)
        {
            var taken = detected.Take(MaxCharacters).ToList();
            var free = _cast.Where(SlotIsFree).ToList();
            var retired = 0;

            for (var i = 0; i < free.Count; i++)
            {
                if (i < taken.Count)
                {
                    free[i].Kind = taken[i].Kind;
                    free[i].Role = taken[i].Role;
                    _autoCastStamp[free[i].Index] = $"{taken[i].Kind}|{taken[i].Role}";
                }
                else if (_autoCastStamp.ContainsKey(free[i].Index))
                {
                    // Only auto-filled cards are retired — a pristine card stays pristine, and the
                    // user's own cards were never in this list.
                    free[i].Role = string.Empty;
                    free[i].Kind = free[i].Index % 2 == 0 ? CharacterSlot.Female : CharacterSlot.Male;
                    _autoCastStamp.Remove(free[i].Index);
                    retired++;
                }
            }

            var who = string.Join("; ", _cast.Where(c => c.HasRole).Select(c => $"<Subject {c.Index}> {c.Role}"));
            AddLog((retired > 0
                ? $"Cast from the story: {who} — retired {retired} character(s) the story no longer lists."
                : $"Cast from the story: {who}") +
                " Check the cards' Kind and Part, then ✨ Generate or browse a photo for each.");
        }

        #endregion

        #region Cast photos — Z-Image / Krea2 / Qwen 2.5.1.2

        /// <summary>Whose card the ✨ menu was last opened on. The menu's LoRA entries carry only the
        /// LoRA as their parameter, so the slot rides along on the tab instead — set by the window
        /// every time a card's menu opens, before any entry can be clicked.</summary>
        internal CharacterSlot? CastPhotoMenuSlot { get; set; }

        /// <summary>The ✨ menu's Z-Image entries: "(as authored …)" first, then every zimage LoRA
        /// on disk. Rebuilt each time a menu opens — see <see cref="RefreshCastPhotoLoras"/>.</summary>
        public System.Collections.ObjectModel.ObservableCollection<CastPhotoWorkflows.CastLora> CastZimageLoraMenu { get; } = new();

        /// <summary>The ✨ menu's Z-Famegrid entries — the same zimage LoRA folder, feeding that
        /// workflow's own character-LoRA slot. Same shape as the Z-Image list.</summary>
        public System.Collections.ObjectModel.ObservableCollection<CastPhotoWorkflows.CastLora> CastFamegridLoraMenu { get; } = new();

        /// <summary>The ✨ menu's Krea2 entries, same shape as the Z-Image list.</summary>
        public System.Collections.ObjectModel.ObservableCollection<CastPhotoWorkflows.CastLora> CastKrea2LoraMenu { get; } = new();

        /// <summary>Rescans the LoRA folders the Image Generator tab reads and rebuilds the ✨ menu's
        /// LoRA lists. Called on every menu open — a disk scan is cheap next to a GPU render.</summary>
        internal void RefreshCastPhotoLoras()
        {
            Refill(CastZimageLoraMenu, CastPhotoWorkflows.ListZimageLoras(_settingsService.Settings, AddLog), "zimage");
            Refill(CastFamegridLoraMenu, CastPhotoWorkflows.ListZimageLoras(_settingsService.Settings, AddLog), "famegrid");
            Refill(CastKrea2LoraMenu, CastPhotoWorkflows.ListKrea2Loras(_settingsService.Settings, AddLog), "krea2");

            void Refill(
                System.Collections.ObjectModel.ObservableCollection<CastPhotoWorkflows.CastLora> menu,
                IReadOnlyList<CastPhotoWorkflows.CastLora> loras,
                string engine)
            {
                menu.Clear();
                menu.Add(CastPhotoWorkflows.CastLora.AsAuthored);
                foreach (var lora in loras)
                    menu.Add(lora);
                if (menu.Count == 1)
                    AddLog($"No {engine} LoRAs found — the ✨ menu offers only the workflow's own. " +
                           "Check the LoRA folders in Settings.");
            }
        }

        private bool CanGenerateCastPhoto(CharacterSlot? slot) =>
            slot is { IsCast: true } && !slot.IsGeneratingPhoto && !IsGeneratingCastPhoto;

        /// <summary>One at a time: the lease serializes the GPU anyway, and the reference maths that
        /// reads the cards mid-change is the one thing this tab cannot afford to race.</summary>
        private bool IsGeneratingCastPhoto => _cast.Any(c => c.IsGeneratingPhoto);

        /// <summary>
        /// Renders one character's portrait with an Image Generator base workflow and drops it into
        /// their card's photo slot. Mirrors <see cref="BuildSheetsAsync"/>'s lifecycle — wardrobe
        /// first, then the GPU lease, then ComfyUI — because it is the same kind of job: a small
        /// generation whose output becomes part of the cast's references.
        /// </summary>
        private async Task GenerateCastPhotoAsync(CharacterSlot? slot, string engine, CastPhotoWorkflows.CastLora? lora = null)
        {
            if (slot == null || !CanGenerateCastPhoto(slot)) return;

            slot.IsGeneratingPhoto = true;
            slot.PhotoPhase = "Preparing…";

            WorkflowQueueCoordinator.WorkflowLease? lease = null;
            try
            {
                var label = CastPhotoWorkflows.LabelFor(engine) +
                            (lora is { IsDefault: false } ? $" + LoRA {lora.Name}" : string.Empty);
                AddLog($"=== Character {slot.Index}: generating the photo with {label} ===");

                // The portrait should wear what the sheets will be photographed in — deciding the
                // wardrobe first is what keeps photo, sheet and prompt agreeing on the outfit.
                slot.PhotoPhase = "Deciding the wardrobe…";
                if (!await EnsureWardrobeAsync(CancellationToken.None))
                    AddLog($"WARNING: no wardrobe could be written — Character {slot.Index}'s photo is " +
                           "generated from their description alone.");

                slot.PhotoPhase = "Waiting for the GPU…";
                lease = await _workflowCoordinator.AcquireAsync("H3Ensemble", CancellationToken.None);

                slot.PhotoPhase = "Checking ComfyUI…";
                var comfyOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(s => AddLog($"[Auto-Restart] {s}"));
                if (!comfyOk) throw new Exception("ComfyUI is not running.");
                if (!_comfyUIService.IsConnected)
                {
                    slot.PhotoPhase = "Connecting to ComfyUI…";
                    await _comfyUIService.ConnectAsync();
                }

                var prompt = BuildCastPhotoPrompt(slot);
                var ts = DateTime.Now.ToString("yyyyMMddHHmmss");
                var runToken = $"cast_{slot.Index}_{ts}";
                var seed = System.Random.Shared.NextInt64(0, 1_000_000_000_000_000L);

                var (json, saveNode) = await CastPhotoWorkflows.BuildAsync(
                    engine, $"{OutputSubfolder}/{runToken}", seed, prompt, AddLog, lora);
                AddLog($"Character {slot.Index} ({slot.Description}): {label} portrait — {prompt}");

                var promptId = await SubmitCastPhotoAsync(json, slot, CancellationToken.None);

                slot.PhotoPhase = "Retrieving the photo…";
                string? local = null;
                var byNode = await _comfyUIService.HttpClient.GetOutputsByNodeAsync(promptId, CancellationToken.None);
                if (byNode.TryGetValue(saveNode, out var outs) && outs.Count > 0)
                    local = await ResolveImageToLocalAsync(outs[0]);
                local ??= FindTokenImageOnDisk(runToken);
                if (local == null || !File.Exists(local))
                    throw new Exception($"Character {slot.Index}'s photo was not produced.");

                // Exactly what browsing for it does — and the sheet, the references and face refine
                // all take it from there. A portrait is not yet a reference, so the sheet is built
                // straight away, in the lease this run already holds.
                Application.Current.Dispatcher.Invoke(() =>
                {
                    slot.SourcePath = local;
                    slot.UseSourceAsSheet = false;   // a generated portrait is a photo, never a ready-made sheet
                });
                AddLog($"Character {slot.Index}: photo generated — {Path.GetFileName(local)}.");
                await BuildSheetAfterPhotoAsync(slot);
            }
            catch (Exception ex)
            {
                AddLog($"ERROR (cast photo): {ex.Message}");
                slot.PhotoPhase = $"Error: {ex.Message}";
                MessageBox.Show($"Generating Character {slot.Index}'s photo failed:\n{ex.Message}",
                    "H3 Ensemble", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                lease?.Dispose();
                slot.IsGeneratingPhoto = false;
                OnCanExecuteChanged();
            }
        }

        /// <summary>
        /// The half of ✨ Generate that follows the photo: cutting it into the three-panel reference
        /// sheet H3 is actually handed. Runs in the workflow lease <see cref="GenerateCastPhotoAsync"/>
        /// already holds, so there is no second GPU wait. A sheet failure does not fail the photo —
        /// the card keeps the photo and says so, and 🪪 Build Character Sheet remains the fallback.
        /// </summary>
        private async Task BuildSheetAfterPhotoAsync(CharacterSlot slot)
        {
            slot.PhotoPhase = "Building the character sheet…";
            IsBuildingSheets = true;
            try
            {
                var instruction = (await LoadFileAsync(
                    Path.Combine("prompts", "prompt2json", SheetPromptFile), CancellationToken.None)).Trim();
                await BuildOneSheetAsync(slot, instruction, string.Empty, CancellationToken.None);
                SheetPhase = "Sheets ready.";
                slot.PhotoPhase = "Done — photo and sheet ready";
            }
            catch (Exception ex)
            {
                AddLog($"ERROR (character sheet after photo): {ex.Message}");
                SheetPhase = $"Error: {ex.Message}";
                slot.PhotoPhase = "Photo ready — build the sheet with 🪪";
            }
            finally
            {
                IsBuildingSheets = false;
            }
        }

        private async Task<string> SubmitCastPhotoAsync(string json, CharacterSlot slot, CancellationToken token)
        {
            var workflow = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);
            var progress = new Progress<ProgressMessage>(msg =>
            {
                if (msg.Data?.Value != null && msg.Data?.Max > 0)
                    Application.Current.Dispatcher.Invoke(() =>
                        slot.PhotoPhase = $"Generating… {msg.Data.Value}/{msg.Data.Max}");
            });

            var promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, progress, token);
            AddLog($"Cast photo workflow submitted, ID: {promptId}");
            return promptId;
        }

        /// <summary>
        /// The portrait brief: who the character is, what they wear (the locked wardrobe, when there
        /// is one), and a clean neutral studio shot of exactly them — the sheet builder re-renders it
        /// into panels anyway, so what it needs is a readable single subject, not a performance.
        /// </summary>
        private string BuildCastPhotoPrompt(CharacterSlot slot)
        {
            var outfit = CastPromptStamp.OutfitFor(CastWardrobe, slot.Index);
            var sb = new StringBuilder();

            if (slot.IsGroup)
                sb.Append($"A photograph of {slot.Description} — the whole group together, every member " +
                          "fully visible and facing the camera.");
            else if (slot.IsPerson)
                sb.Append($"A full-length character portrait photograph of {slot.Description}, standing " +
                          "relaxed and facing the camera, head to feet fully in frame, looking at the lens.");
            else
                sb.Append($"A photograph of {slot.Description}, centered and whole, fully visible.");

            if (outfit.Length > 0)
                sb.Append(" Wearing exactly this: ").Append(outfit.TrimEnd('.', ' ')).Append('.');

            sb.Append(slot.IsPerson
                ? " Neutral expression, natural pose, no other people, no props, no text."
                : " Nothing else in frame, no people, no text.");
            sb.Append(" Plain light grey seamless studio background, soft even lighting, sharp focus — a " +
                      "clean reference photograph of this character for a film's cast list.");
            return sb.ToString();
        }

        #endregion
    }
}
