using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
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
    /// H3 Ensemble, part two: the character sheets, the prompt box, the wardrobe that is decided once for
    /// the whole cast, and the render settings.
    /// </summary>
    public partial class H3EnsembleViewModel
    {
        #region Character sheets (Qwen-Image-Edit-2511 ConvRot)

        /// <summary>
        /// Deliberately <i>not</i> gated on a render being in flight: the workflow coordinator already
        /// serializes GPU access, so a build started mid-render simply waits for the lease. Gating it would
        /// make it impossible to prepare the next job while the current one runs.
        /// </summary>
        public bool CanBuildSheets => HasAnyCharacter && !IsBuildingSheets &&
                                      LoadedCharacters.Any(c => !c.UseSourceAsSheet);

        public bool IsBuildingSheets
        {
            get => _isBuildingSheets;
            private set
            {
                if (_isBuildingSheets == value) return;
                _isBuildingSheets = value;
                OnPropertyChanged();
                OnCanExecuteChanged();
            }
        }

        /// <summary>What the sheet builder is doing right now, shown beside its button — it cannot use the
        /// tab's status line, which belongs to whatever is rendering.</summary>
        public string SheetPhase
        {
            get => _sheetPhase;
            private set { if (_sheetPhase != value) { _sheetPhase = value; OnPropertyChanged(); } }
        }

        public string BuildSheetsButtonText
        {
            get
            {
                var n = LoadedCharacters.Count(c => !c.UseSourceAsSheet);
                return n > 1 ? $"🪪 Build {n} Character Sheets" : "🪪 Build Character Sheet";
            }
        }

        /// <summary>
        /// Runs Qwen-Image-Edit-2511 once per loaded character, turning each photo into the three-panel
        /// reference sheet H3 is handed — wearing the locked wardrobe rather than whatever the photo showed.
        /// </summary>
        private async Task BuildSheetsAsync()
        {
            if (!CanBuildSheets) return;

            IsBuildingSheets = true;
            SheetPhase = "Preparing…";

            _sheetCts?.Dispose();
            _sheetCts = new CancellationTokenSource();
            var token = _sheetCts.Token;

            WorkflowQueueCoordinator.WorkflowLease? lease = null;
            try
            {
                var todo = LoadedCharacters.Where(c => !c.UseSourceAsSheet).ToList();
                AddLog($"=== H3 Ensemble: building {todo.Count} character sheet(s) with Qwen-Image-Edit-2511 ===");

                SheetPhase = "Deciding the wardrobe…";
                if (!await EnsureWardrobeAsync(token) && (HasStoryText || HasEnvironment))
                    AddLog("WARNING: no wardrobe could be derived, so the sheets keep the clothes in the source " +
                           "photos and each clip will describe an outfit of its own.");

                SheetPhase = IsProcessing || IsProcessingQueue
                    ? "Waiting for the current render to finish…"
                    : "Waiting for the GPU…";
                AddLog("Waiting for other workflows to finish...");
                lease = await _workflowCoordinator.AcquireAsync("H3Ensemble", token);

                SheetPhase = "Checking ComfyUI…";
                var comfyOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(s => AddLog($"[Auto-Restart] {s}"));
                if (!comfyOk) throw new Exception("ComfyUI is not running.");
                if (!_comfyUIService.IsConnected)
                {
                    SheetPhase = "Connecting to ComfyUI…";
                    await _comfyUIService.ConnectAsync();
                }

                var instruction = (await LoadFileAsync(Path.Combine("prompts", "prompt2json", SheetPromptFile), token)).Trim();

                for (var i = 0; i < todo.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var slot = todo[i];
                    var progress = todo.Count > 1 ? $" ({i + 1}/{todo.Count})" : string.Empty;
                    var outfit = CastPromptStamp.OutfitFor(CastWardrobe, slot.Index);

                    SheetPhase = $"Uploading character {slot.Index}…{progress}";
                    var uploaded = await EnsureUploadedAsync(slot.SourcePath);

                    var ts = DateTime.Now.ToString("yyyyMMddHHmmss");
                    var runToken = $"sheet_{slot.Index}_{ts}";

                    var json = await LoadFileAsync(SheetWorkflowFileName, token);
                    json = UseSheetCanvas(json);
                    SetInput(ref json, SheetLoadImage, "image", uploaded);
                    SetInput(ref json, SheetPositive, "prompt", BuildSheetInstruction(instruction, slot, outfit));
                    SetInput(ref json, SheetSampler, "seed", System.Random.Shared.NextInt64(0, 1_000_000_000_000_000L));
                    SetInput(ref json, SheetLatent, "width", SheetWidth);
                    SetInput(ref json, SheetLatent, "height", SheetHeight);
                    SetInput(ref json, SheetSave, "filename_prefix", $"{OutputSubfolder}/{runToken}");
                    // The sheet graph's SaveImage reads from an RTX upscale, and that node's widgets changed
                    // with the Nvidia pack — without this the sheet reaches the GPU and dies there.
                    json = RtxSuperResolutionCompat.Normalize(json, AddLog);

                    SheetPhase = $"Generating character {slot.Index}'s sheet…{progress}";
                    AddLog($"Character {slot.Index} ({slot.Description}): generating a {SheetWidth}×{SheetHeight} " +
                           $"sheet from {Path.GetFileName(slot.SourcePath)}...");
                    if (outfit.Length > 0)
                        AddLog($"Character {slot.Index} is being dressed in the locked wardrobe: {outfit}");
                    var promptId = await SubmitSheetAsync(json, token);

                    string? local = null;
                    var byNode = await _comfyUIService.HttpClient.GetOutputsByNodeAsync(promptId, token);
                    if (byNode.TryGetValue(SheetSave, out var outs) && outs.Count > 0)
                        local = await ResolveImageToLocalAsync(outs[0]);
                    local ??= FindTokenImageOnDisk(runToken);
                    if (local == null || !File.Exists(local))
                        throw new Exception($"Character {slot.Index}'s sheet was not produced.");

                    await EnsureUploadedAsync(local);
                    var applied = local;
                    var wornInSheet = outfit;
                    Application.Current.Dispatcher.Invoke(() => slot.SetSheet(applied, wornInSheet));
                    AddLog($"Character {slot.Index}: sheet ready — {Path.GetFileName(local)}");
                }

                SheetPhase = AllSheetsReady ? "Sheets ready." : "Sheets built.";
            }
            catch (OperationCanceledException)
            {
                AddLog("Sheet building cancelled");
                SheetPhase = "Cancelled";
            }
            catch (Exception ex)
            {
                AddLog($"ERROR (character sheets): {ex.Message}");
                SheetPhase = $"Error: {ex.Message}";
                MessageBox.Show($"Building the character sheets failed:\n{ex.Message}",
                    "H3 Ensemble", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                lease?.Dispose();
                IsBuildingSheets = false;
                _sheetCts?.Dispose();
                _sheetCts = null;
                OnCanExecuteChanged();
            }
        }

        /// <summary>
        /// The sheet instruction for one character: the shipped three-panel brief, who they are, and — when a
        /// wardrobe is locked — the look the sheet must show them in. Photographing the cast in the locked
        /// wardrobe removes the picture-versus-prose disagreement rather than arbitrating it.
        ///
        /// <para><b>A non-person gets a different brief.</b> The shipped one opens "Turn the person in the
        /// input image into a character reference sheet…" and asks for a full-body front, a full-body back
        /// and a <i>face close-up</i> — three instructions that, aimed at a photograph of a cloud, produce a
        /// person. So for those the brief is rewritten rather than appended to: same three-panel studio
        /// layout, same purpose, but three views of the <i>subject</i> and a detail panel instead of a
        /// face.</para>
        /// </summary>
        private static string BuildSheetInstruction(string baseInstruction, CharacterSlot slot, string outfit)
        {
            var sb = slot.IsGroup
                ? new StringBuilder(GroupSheetInstruction(slot))
                : slot.IsPerson
                    ? new StringBuilder(baseInstruction).Append($" The person is a {slot.Noun}.")
                    : new StringBuilder(NonPersonSheetInstruction(slot));

            if (outfit.Length == 0) return sb.ToString();

            if (slot.IsGroup)
            {
                sb.Append(" Every one of them looks like this, in all three panels: ")
                  .Append(outfit.TrimEnd('.', ' '))
                  .Append(". Keep the same number of them, and the same look, in every panel.");
            }
            else if (slot.IsPerson)
            {
                sb.Append(" Dress them in exactly this outfit, replacing whatever clothing the input image " +
                          "shows: ")
                  .Append(outfit.TrimEnd('.', ' '))
                  .Append(". Every one of the three panels must show that same outfit, complete and clearly " +
                          "visible, with the same garments, colours and materials — the front view from the " +
                          "front, the back view from behind, and whatever of it reaches the shoulders in the " +
                          "close-up. Change only the clothing: the face, hair, skin tone, build and age stay " +
                          "exactly as they are in the input image.");
            }
            else
            {
                sb.Append(" It must look exactly like this in all three panels: ")
                  .Append(outfit.TrimEnd('.', ' '))
                  .Append(". Keep that same form, the same colours and the same materials in every panel. Do " +
                          "not turn it into a person or into a person in a costume while doing so.");
            }
            return sb.ToString();
        }

        /// <summary>
        /// The three-panel sheet brief for a <b>group</b> — a herd, a village, a flock — whether or not they
        /// are people. The shipped brief asks for one person three times and ends "do not add … any other
        /// person", which is the opposite of what a crowd sheet needs.
        /// </summary>
        private static string GroupSheetInstruction(CharacterSlot slot)
        {
            var what = slot.HasRole ? slot.Role : $"the {slot.Noun} in the input image";
            var people = slot.IsPerson;
            return
                $"Turn the subjects of the input image into a group character reference sheet for {what}, on " +
                "a plain light grey seamless studio background, laid out as exactly three panels side by " +
                "side, left to right: (1) the whole group together, seen from the front, every member fully " +
                "in frame; (2) the same group from behind, in the same arrangement and lighting; (3) a " +
                "closer view of two or three of them, sharply focused and evenly lit. " +
                "THIS IS A GROUP, not one individual: keep the same number of them, the same arrangement " +
                "and the same appearance in all three panels, and do not reduce them to a single figure. " +
                (people
                    ? "They are people: give each of them their own clear face rather than a blur, and do " +
                      "not duplicate one of them across the group. "
                    : "They are NOT people: do not give any of them a human face, human eyes, human hair, " +
                      "human hands or human clothing, and do not replace them with people in costumes. ") +
                "Use even spacing between the panels, consistent soft even studio lighting and no cast " +
                "shadows on the background. Do not add text, labels, numbers, borders, frames, watermarks " +
                "or props.";
        }

        /// <summary>
        /// The three-panel character sheet brief for a subject that is not a human being. Deliberately a
        /// rewrite rather than a suffix on <c>h3-charsheet-2511.md</c>: that file says "the person", "the
        /// same person", "the back of the hair" and "a head-and-shoulders close-up of the face", and every
        /// one of those is an instruction to draw a person.
        /// </summary>
        private static string NonPersonSheetInstruction(CharacterSlot slot)
        {
            var what = slot.HasRole ? slot.Role : $"the {slot.Noun} in the input image";
            return
                $"Turn the subject of the input image into a character reference sheet for {what}, on a plain " +
                "light grey seamless studio background, laid out as exactly three panels side by side, left " +
                "to right: (1) the whole subject seen from the front, complete and fully in frame; (2) the " +
                "whole subject seen from the opposite side or the back, in the same lighting; (3) a close-up " +
                "of its most recognisable part, sharply focused and evenly lit. " +
                "THE SUBJECT IS NOT A PERSON. Do not give it a human face, human eyes, human hair, human " +
                "hands, arms, legs or clothing; do not replace it with a person, a person in a costume, a " +
                "mascot or a humanoid figure. Keep it exactly the thing the input image shows, with identical " +
                "shape, proportions, colours, materials, surface and markings in all three panels. Use even " +
                "spacing between the panels, consistent soft even studio lighting and no cast shadows on the " +
                "background. Do not add text, labels, numbers, borders, frames, watermarks, props, or any " +
                "person.";
        }

        /// <summary>
        /// Points the sheet workflow's sampler at its empty latent instead of the VAE-encoded source photo,
        /// so the sheet is composed on a canvas of our choosing rather than inheriting the photo's framing.
        /// Idempotent.
        /// </summary>
        private static string UseSheetCanvas(string json)
        {
            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new Exception("Sheet workflow JSON could not be parsed.");

            RequireClass(root, SheetSampler, "KSampler");
            RequireClass(root, SheetLatent, "EmptySD3LatentImage");
            RequireClass(root, SheetPositive, "TextEncodeQwenImageEditPlus");
            RequireClass(root, SheetLoadImage, "LoadImage");
            RequireClass(root, SheetSave, "SaveImage");

            json = root.ToJsonString();
            SetInput(ref json, SheetSampler, "latent_image", new JsonArray(SheetLatent, 0));
            return json;
        }

        #endregion

        #region Prompt

        /// <summary>
        /// The assembled six-section hybrid prompt. Past one clip's worth of duration it holds the whole
        /// chain — one prompt per clip, separated by <c>=== CLIP n of N ===</c> headers — and stays editable,
        /// because it is what Add to Queue splits.
        /// </summary>
        public string Prompt
        {
            get => _prompt;
            set
            {
                if (_prompt == value) return;
                _prompt = value;
                _promptClipCount = SplitClips(_prompt).Count;
                RefreshStoryboardStaleness();
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasPrompt));
                OnPropertyChanged(nameof(PromptClipCount));
                OnPropertyChanged(nameof(HasPromptSequence));
                OnPropertyChanged(nameof(PromptClipSummary));
                OnPropertyChanged(nameof(PromptHealthSummary));
                OnPropertyChanged(nameof(CastCoverageSummary));
                RestampCommand.NotifyCanExecuteChanged();
                OnCanExecuteChanged();
            }
        }

        public bool HasPrompt => !string.IsNullOrWhiteSpace(_prompt);

        /// <summary>
        /// Re-assembles what is in the box against the keyframes, cast, location and wardrobe as they stand
        /// right now. The four model-written sections survive; the four code-written ones are rebuilt.
        /// </summary>
        private void Restamp()
        {
            if (!HasPrompt) return;
            var before = PromptClipCount;
            Prompt = AssembleChain(Prompt);
            AddLog(before > 1
                ? $"Re-stamped {before} clips against {LoadedCharacterCount} character(s), " +
                  $"{OrderedKeyframes.Count} hand-placed keyframe(s) and the current wardrobe."
                : $"Re-stamped against {LoadedCharacterCount} character(s) and " +
                  $"{OrderedKeyframes.Count} keyframe(s).");
            AddLog(CastCoverageSummary);
        }

        /// <summary>Reports a prompt whose picture numbers no longer match the keyframe list — the one way
        /// this tab can silently point a lock at a studio photograph.</summary>
        public string PromptHealthSummary
        {
            get
            {
                if (!HasPrompt) return string.Empty;
                var missing = HybridCastPrompt.MissingSections(SplitClips(Prompt).FirstOrDefault());
                if (missing.Count > 0)
                    return $"Missing section(s): {string.Join(", ", missing)}. Press ✎ Re-stamp, or Analyze again.";

                // Only the model-written body is checked. The code-written sections name the cast's and the
                // location's pictures by number on purpose.
                var clips = SplitClips(Prompt);
                for (var i = 0; i < clips.Count; i++)
                {
                    var keys = KeyframesForClip(i + 1).Count;
                    var highest = HybridCastPrompt.HighestPictureReference(HybridCastPrompt.Strip(clips[i]));
                    if (highest <= keys) continue;

                    var where = clips.Count > 1 ? $"Clip {i + 1}'s shot list" : "The shot list";
                    return $"⚠ {where} names <Picture {highest}> but that clip carries only {keys} " +
                           "keyframe(s) — that number is a cast photograph or the location, not a frame. " +
                           "Press 🎬 Preview Keyframes or fix the keyframe list, then ✎ Re-stamp.";
                }
                return string.Empty;
            }
        }

        /// <summary>
        /// Who the chain actually casts, per character — the line that makes an ensemble legible.
        ///
        /// <para>The whole economy of this tab is that a clip only carries the characters it names, so a
        /// character loaded, sheeted and then never written into a single beat is nine slots' worth of
        /// preparation that reaches no render at all. That is invisible in a 30-line prompt box and obvious
        /// here.</para>
        /// </summary>
        public string CastCoverageSummary
        {
            get
            {
                var loaded = LoadedCharacters;
                if (!HasPrompt || loaded.Count == 0) return string.Empty;

                var clips = SplitClips(Prompt);
                var lines = new List<string>();
                var absent = new List<int>();

                foreach (var slot in loaded)
                {
                    var inClips = Enumerable.Range(1, clips.Count)
                        .Where(n => HybridCastPrompt.IncludesSubject(clips[n - 1], slot.Index))
                        .ToList();
                    if (inClips.Count == 0) { absent.Add(slot.Index); continue; }
                    lines.Add(clips.Count == 1
                        ? $"<Subject {slot.Index}>"
                        : inClips.Count == clips.Count
                            ? $"<Subject {slot.Index}> in every clip"
                            : $"<Subject {slot.Index}> in clip {string.Join(", ", inClips)}");
                }

                var sb = new StringBuilder();
                if (lines.Count > 0) sb.Append("Cast per clip: ").Append(string.Join("; ", lines)).Append('.');
                if (absent.Count > 0)
                    sb.Append($" ⚠ <Subject {string.Join(">, <Subject ", absent)}> {(absent.Count == 1 ? "is" : "are")} " +
                              "in no clip at all — their sheets were built for nothing. Name them in the story " +
                              "and Analyze again, or take them out of the cast.");
                return sb.ToString();
            }
        }

        public int PromptClipCount => _promptClipCount;
        public bool HasPromptSequence => PromptClipCount > 1;

        public string PromptClipSummary =>
            PromptClipCount > 1
                ? $"This prompt holds {PromptClipCount} clips — Add to Queue enqueues {PromptClipCount} jobs, in order."
                : string.Empty;

        #endregion

        #region Wardrobe (decided once for the whole cast, stamped into every clip)

        /// <summary>
        /// The cast's outfits, decided once and stamped into every clip verbatim. It is the one block of text
        /// in a chain that is byte-identical everywhere by construction, which is the only reason a
        /// five-hander can keep its clothes on across a dozen independently written clips.
        /// </summary>
        public string CastWardrobe
        {
            get => _castWardrobe;
            set
            {
                if (_castWardrobe == value) return;
                _castWardrobe = value;
                PushWardrobeToCast();
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasCastWardrobe));
                OnPropertyChanged(nameof(WardrobeSummary));
                OnPropertyChanged(nameof(SheetsShowWardrobe));
                OnPropertyChanged(nameof(CastSummary));
                ClearWardrobeCommand.NotifyCanExecuteChanged();
            }
        }

        public bool HasCastWardrobe => !string.IsNullOrWhiteSpace(CastWardrobe);

        public bool IsWardrobeLocked
        {
            get => _isWardrobeLocked;
            set
            {
                if (_isWardrobeLocked == value) return;
                _isWardrobeLocked = value;
                _wardrobeIsManual = !value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(WardrobeLockButtonText));
                OnPropertyChanged(nameof(WardrobeSummary));
            }
        }

        public string WardrobeLockButtonText => IsWardrobeLocked ? "🔒 Locked" : "🔓 Editing";

        /// <summary>True when every loaded character's sheet was generated wearing the wardrobe locked right
        /// now — what lets the prompt tell H3 to copy the clothing out of the references instead of
        /// disowning it.</summary>
        public bool SheetsShowWardrobe =>
            HasCastWardrobe && HasAnyCharacter && LoadedCharacters.All(c => c.SheetMatchesWardrobe);

        private void PushWardrobeToCast()
        {
            foreach (var slot in _cast)
                slot.ExpectedWardrobe = CastPromptStamp.OutfitFor(_castWardrobe, slot.Index);
        }

        public string WardrobeSummary
        {
            get
            {
                if (!HasCastWardrobe)
                    return CastToDress.Count == 0
                        // The tab knows nothing about the cast yet, and it will not guess: guessing is how
                        // a story about a cloud and a mountain got outfits for a man and a woman.
                        ? "Empty, and waiting for a cast. Set Kind and Part on a cast card below — or load a " +
                          "photo — and this fills itself in from the story a moment later, one line per " +
                          "character. Nothing here can guess whether your characters are people."
                        : $"Empty — being written from the story for {CastToDress.Count} character(s) a " +
                          "moment after you stop typing, one line each. Unlock to write your own.";

                var stale = LoadedCharacters.Where(c => !c.SheetMatchesWardrobe).ToList();
                var sheets = !HasAnyCharacter
                    ? " Load each character's photo below and build their sheets to have them pictured in it."
                    : stale.Count == 0
                        ? " The character sheets show these outfits, so the references and the prompt agree."
                        : $" Character {string.Join(", ", stale.Select(c => c.Index))}'s sheet does not show " +
                          "this outfit yet — rebuild the sheets, or the references and the prompt will be " +
                          "dressing them differently.";

                var keys = HasKeyframes
                    ? " Where a keyframe still shows the cast, that still wins at its own timestamp."
                    : string.Empty;

                return (IsWardrobeLocked
                    ? "Locked. This exact text is written into every clip's prompt ahead of the sections, and " +
                      "each clip keeps only the lines for the characters it casts."
                    : "Unlocked — your edits stand and the story no longer rewrites it. Re-lock when you are done.")
                    + sheets + keys;
            }
        }

        #endregion

        #region Story and length

        /// <summary>
        /// Length of the <i>finished</i> video, 5–180 s in 5 s steps. Anything longer than
        /// <see cref="LengthSeconds"/> is written as a chain of <see cref="PlannedClipCount"/> clips.
        /// </summary>
        public double StoryDurationSeconds
        {
            get => _storyDurationSeconds;
            set
            {
                var snapped = Math.Clamp(Math.Round(value / 5.0) * 5.0, 5, 180);
                if (Math.Abs(_storyDurationSeconds - snapped) < 0.0001) return;
                _storyDurationSeconds = snapped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PlannedClipCount));
                OnPropertyChanged(nameof(IsStorySequence));
                OnPropertyChanged(nameof(ClipPlanSummary));
            }
        }

        public int PlannedClipCount =>
            Math.Max(1, (int)Math.Ceiling(StoryDurationSeconds / ClampLength(LengthSeconds) - 0.0001));

        public bool IsStorySequence => PlannedClipCount > 1;

        public string ClipPlanSummary
        {
            get
            {
                var clip = ClampLength(LengthSeconds);
                var n = PlannedClipCount;
                if (n <= 1) return $"One clip of {clip:0.#}s — a single hybrid H3 pass.";

                var warn = n > 8
                    ? " ⚠ A local model plans about 6–8 distinct beats in one reply; past that it starts " +
                      "repeating itself. The loop guard drops the duplicates, so a longer setting here " +
                      "often produces a shorter chain — run Analyze twice instead."
                    : string.Empty;
                return $"{n} clips × {clip:0.#}s → {n * clip:0.#}s of video. Each clip casts only the " +
                       "characters its beat actually needs, so the reference slots go to the people on " +
                       $"screen. All of them are joined into one file when the last lands.{warn}";
            }
        }

        public string StoryText
        {
            get => _storyText;
            set
            {
                if (_storyText == value) return;
                _storyText = value;
                if (!string.IsNullOrEmpty(_storyFileName)) _storyFileName = string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasStoryText));
                OnPropertyChanged(nameof(StorySourceSummary));
                ClearStoryCommand.NotifyCanExecuteChanged();
                OnCanExecuteChanged();
                ScheduleWardrobeDerive();
            }
        }

        public bool HasStoryText => !string.IsNullOrWhiteSpace(StoryText);

        public string StorySourceSummary
        {
            get
            {
                var words = HasStoryText
                    ? StoryText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length
                    : 0;
                var loaded = string.IsNullOrEmpty(_storyFileName) ? string.Empty : $" from {_storyFileName}";
                var cast = HasAnyCharacter
                    ? $" The cast are written in as <Subject {string.Join(">, <Subject ", LoadedCharacters.Select(c => c.Index))}>" +
                      (LoadedCharacters.Any(c => c.HasRole)
                          ? " — give the rest of them a part below and the model will cast them by role rather than by order of appearance."
                          : " — the Part field on each card is what tells the model who is who.")
                    : string.Empty;

                if (HasEnvironment && HasStoryText)
                    return $"Analyze will use both: the location image for the setting, lighting and " +
                           $"wardrobe, and these {words:N0} words{loaded} for what happens.{cast}";
                if (HasEnvironment)
                    return $"Analyze will read the location image alone and invent a story that suits it.{cast}";
                if (HasStoryText)
                    return $"No location image — Analyze will work from these {words:N0} words{loaded} alone, " +
                           $"writing the setting, lighting and wardrobe out of the story itself.{cast}";
                return "Load a location image, write a story, or both — Analyze needs at least one of them.";
            }
        }

        private async Task LoadStoryFileAsync()
        {
            var initialDir = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDir) || !Directory.Exists(initialDir))
                initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select a story (.txt)",
                "Text Files|*.txt;*.md;*.text|All Files|*.*",
                initialDir,
                persistKey: "h3ensemble.story");
            if (path == null) return;

            try
            {
                var text = (await File.ReadAllTextAsync(path)).Trim();
                if (text.Length == 0)
                {
                    AddLog($"Story file is empty: {Path.GetFileName(path)}");
                    return;
                }

                StoryText = text;
                _storyFileName = Path.GetFileName(path);
                OnPropertyChanged(nameof(StorySourceSummary));
                AddLog($"Story loaded: {_storyFileName} ({text.Length:N0} chars)");
            }
            catch (Exception ex)
            {
                AddLog($"Could not read the story file: {ex.Message}");
                MessageBox.Show($"Could not read that file:\n{ex.Message}",
                    "H3 Ensemble", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Render settings

        public IReadOnlyList<string> AspectRatioOptions { get; } =
            new[] { H3Canvas.AutoAspect }
                .Concat(H3Canvas.AspectRatios.Select(a => a.Option)).ToList();

        public string SelectedAspectRatio
        {
            get => _selectedAspectRatio;
            set
            {
                if (_selectedAspectRatio == value) return;
                _selectedAspectRatio = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ResolvedAspectRatio));
                OnPropertyChanged(nameof(UpscaleSummary));
                OnPropertyChanged(nameof(LoadSummary));
                OnPropertyChanged(nameof(HasLoadWarning));
            }
        }

        /// <summary>The aspect actually sent to ComfyUI — the picked one, the opening keyframe's, or the
        /// location image's closest match. The keyframe wins because it is literally frame 0.</summary>
        public string ResolvedAspectRatio =>
            SelectedAspectRatio == H3Canvas.AutoAspect
                ? ClosestAspectRatio(OrderedKeyframes.FirstOrDefault()?.Path ?? EnvironmentPath)
                : SelectedAspectRatio;

        /// <summary>How the prompt's global rules open. It is stated once, in code, because a chain's clips
        /// are written independently and a style word that drifts is a style that changes mid-story.</summary>
        public IReadOnlyList<string> MediumOptions { get; } = new[]
        {
            "live-action and cinematic",
            "anime, cinematic, high-production",
            "3D CG, cinematic",
            "stop-motion, cinematic",
        };

        public string SelectedMedium
        {
            get => _selectedMedium;
            set { if (_selectedMedium != value) { _selectedMedium = value; OnPropertyChanged(); } }
        }

        public IReadOnlyList<MegapixelOption> MegapixelOptions { get; } = new[]
        {
            new MegapixelOption(0.4, "0.4 MP — fast draft (≈864×480)"),
            new MegapixelOption(0.7, "0.7 MP — balanced (≈1120×640)"),
            new MegapixelOption(1.0, "1.0 MP — full quality (≈1344×768)"),
        };

        public double Megapixels
        {
            get => _megapixels;
            set
            {
                if (Math.Abs(_megapixels - value) <= 0.0001) return;
                _megapixels = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(UpscaleSummary));
                OnPropertyChanged(nameof(LoadSummary));
                OnPropertyChanged(nameof(HasLoadWarning));
            }
        }

        public double LengthSeconds
        {
            get => _lengthSeconds;
            set
            {
                if (Math.Abs(_lengthSeconds - value) <= 0.0001) return;
                _lengthSeconds = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LengthSummary));
                OnPropertyChanged(nameof(LoadSummary));
                OnPropertyChanged(nameof(HasLoadWarning));
                OnPropertyChanged(nameof(RefineSummary));
                OnPropertyChanged(nameof(PlannedClipCount));
                OnPropertyChanged(nameof(IsStorySequence));
                OnPropertyChanged(nameof(ClipPlanSummary));
                ClampKeyframesToLength();
            }
        }

        /// <summary>Pulls any keyframe now past the end of the clip back inside it.</summary>
        private void ClampKeyframesToLength()
        {
            var last = ClampLength(LengthSeconds) * 2.0 / 3.0;
            foreach (var slot in _keyframes.Where(k => k.Seconds > last).ToList())
                slot.Seconds = Math.Round(last, 2);
        }

        public string LengthSummary
        {
            get
            {
                var len = ClampLength(LengthSeconds);
                var muxed = Interpolate ? OutputFrameRate * InterpolationFactor : OutputFrameRate;
                return $"{len:0.#}s → {FramesForSeconds(len)} frames rendered @ {OutputFrameRate} fps, " +
                       $"muxed at {muxed} fps";
            }
        }

        public long Seed
        {
            get => _seed;
            set { if (_seed != value) { _seed = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// Whether the second H3 pass runs — one pass per character in the clip, each tracked by that
        /// character's own face close-up. On an ensemble this is the setting with the largest cost: a
        /// three-hander runs three refine passes, and each is roughly as long as the base render's tail.
        /// </summary>
        public bool FaceRefine
        {
            get => _faceRefine;
            set
            {
                if (_faceRefine == value) return;
                _faceRefine = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RefineSummary));
            }
        }

        /// <summary>
        /// Denoise of the refine passes — how far a cropped face may move away from what the base pass
        /// rendered. Low cleans it up and keeps the performance H3 rendered; above ~0.40 it re-imagines the
        /// face per frame, which on fast motion reads as boiling.
        /// </summary>
        public double RefineDenoise
        {
            get => _refineDenoise;
            set
            {
                var snapped = Math.Clamp(Math.Round(value * 20) / 20.0, 0.15, 0.75);
                if (Math.Abs(_refineDenoise - snapped) <= 0.0001) return;
                _refineDenoise = snapped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RefineSummary));
            }
        }

        public string RefineSummary
        {
            get
            {
                if (!FaceRefine)
                    return "Off — the base H3 frames go straight to the finishing passes. Faces stay as H3 " +
                           "rendered them.";

                // Only the people can be refined: H3FaceTrackCrop tracks human faces, so a cloud gets no
                // pass and a herd gets none either (the tracker holds one subject, and a herd is not one).
                var faces = FaceCast.Count;
                var skipped = NonPersonCast.Count;
                if (LoadedCharacterCount > 0 && faces == 0)
                    return "On, but nothing in this cast has a face to refine — the pass tracks human faces " +
                           "and none of these characters is a person, so it will not run. The base H3 frames " +
                           "go straight to the finishing passes.";

                var cast = Math.Max(1, faces);
                var crops = FrameStackGb(FramesForSeconds(ClampLength(LengthSeconds)), 768, 768);
                var cost = cast == 1
                    ? "A second H3 pass on the tracked face crops"
                    : $"Up to {cast} H3 passes — one per person the clip casts, stacked one on top of the next";
                var time = cast > 2
                    ? $" That is roughly {cast}× the refine time of a single-character clip; a clip that only " +
                      "casts two of them only runs two passes."
                    : string.Empty;
                var left = skipped == 0
                    ? string.Empty
                    : $" The {skipped} non-human character(s) are left alone — there is no face there to track.";
                return $"{cost} at denoise {RefineDenoise:0.00}, each conditioned on that character's own " +
                       "panels and tracked by their face close-up, with the stage-1 audio locked so lip-sync " +
                       $"survives — ≈{crops:0.#} GB of crops on top of the frames.{time}{left} " +
                       "Needs the H3-FaceRefine + NativeAudioLock nodes.";
            }
        }

        /// <summary>FILM ×2 frame interpolation. On by default — the 8-step turbo stack renders 24 fps and
        /// the interpolation costs a fraction of what the diffusion did.</summary>
        public bool Interpolate
        {
            get => _interpolate;
            set
            {
                if (_interpolate == value) return;
                _interpolate = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LengthSummary));
                OnPropertyChanged(nameof(UpscaleSummary));
                OnPropertyChanged(nameof(LoadSummary));
                OnPropertyChanged(nameof(HasLoadWarning));
            }
        }

        /// <summary>The RTX Video Super Resolution ×2 finish. <b>Off by default</b>: it is the graph's single
        /// largest allocation, and with <see cref="Interpolate"/> on it runs over twice as many frames.</summary>
        public bool RtxUpscale
        {
            get => _rtxUpscale;
            set
            {
                if (_rtxUpscale == value) return;
                _rtxUpscale = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(UpscaleSummary));
                OnPropertyChanged(nameof(LoadSummary));
                OnPropertyChanged(nameof(HasLoadWarning));
            }
        }

        public string UpscaleSummary
        {
            get
            {
                var (cw, ch) = CanvasSize(ResolvedAspectRatio, Megapixels);
                var fps = Interpolate ? OutputFrameRate * InterpolationFactor : OutputFrameRate;
                if (!RtxUpscale) return $"Output: ≈{cw}×{ch} @ {fps} fps. No upscale pass.";
                var (w, h) = UpscaleSize(ResolvedAspectRatio, Megapixels);
                return $"Output: RTX ×2 super-resolution → ≈{w}×{h} @ {fps} fps.";
            }
        }

        /// <summary>
        /// The frame stack the run will have to hold in one piece, and a warning when that is the size that
        /// kills ComfyUI mid-render.
        /// </summary>
        public string LoadSummary
        {
            get
            {
                var frames = FinishedFrameCount();
                var (cw, ch) = CanvasSize(ResolvedAspectRatio, Megapixels);
                var baseGb = FrameStackGb(frames, cw, ch);
                var interp = Interpolate ? $" (FILM ×{InterpolationFactor})" : string.Empty;

                if (!RtxUpscale)
                    return $"{frames} frames{interp} × {cw}×{ch} ≈ {baseGb:0.#} GB of frames held at once.";

                var (uw, uh) = UpscaleSize(ResolvedAspectRatio, Megapixels);
                var upGb = FrameStackGb(frames, uw, uh);
                var text = $"{frames} frames{interp}: ≈{baseGb:0.#} GB at the H3 canvas, ≈{upGb:0.#} GB after " +
                           "RTX ×2, both live at the same time during the upscale.";
                return upGb >= HeavyFrameStackGb
                    ? text + " ⚠ That is the size that takes ComfyUI down mid-render — shorten the clip, drop " +
                             "to 0.7 MP, turn interpolation off, or turn RTX off and upscale afterwards in " +
                             "✨ Enhance Video."
                    : text;
            }
        }

        public bool HasLoadWarning
        {
            get
            {
                if (!RtxUpscale) return false;
                var (uw, uh) = UpscaleSize(ResolvedAspectRatio, Megapixels);
                return FrameStackGb(FinishedFrameCount(), uw, uh) >= HeavyFrameStackGb;
            }
        }

        /// <summary>Frames that reach the file — the render's own count, doubled when FILM runs.</summary>
        private int FinishedFrameCount() =>
            FramesForSeconds(ClampLength(LengthSeconds)) * (Interpolate ? InterpolationFactor : 1);

        private static double FrameStackGb(int frames, int width, int height) =>
            (double)frames * width * height * 3 * 4 / (1024.0 * 1024.0 * 1024.0);

        private string ClosestAspectRatio(string path)
        {
            int w = 0, h = 0;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try
                {
                    using var fs = File.OpenRead(path);
                    var frame = BitmapFrame.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    w = frame.PixelWidth; h = frame.PixelHeight;
                }
                catch { /* fall through to the 16:9 default */ }
            }
            return H3Canvas.ClosestAspectRatio(w, h);
        }

        /// <summary>Mirrors the ResolutionSelector's maths, for display only.</summary>
        private static (int Width, int Height) CanvasSize(string aspectOption, double megapixels)
        {
            var ratio = H3Canvas.AspectRatios
                .FirstOrDefault(a => a.Option == aspectOption).Ratio;
            if (ratio <= 0) ratio = 16.0 / 9.0;

            var area = Math.Max(0.1, megapixels) * 1_000_000.0;
            var w = RoundTo32(Math.Sqrt(area * ratio));
            return (w, RoundTo32(w / ratio));

            static int RoundTo32(double v) => Math.Max(32, (int)Math.Round(v / 32.0) * 32);
        }

        private static (int Width, int Height) UpscaleSize(string aspectOption, double megapixels)
        {
            var (w, h) = CanvasSize(aspectOption, megapixels);
            return ((int)(w * RtxScale), (int)(h * RtxScale));
        }

        /// <summary>H3's supported clip length is 4–15 seconds at 24 fps.</summary>
        private static double ClampLength(double seconds) =>
            Math.Clamp(seconds <= 0 ? 8 : seconds, 4, 15);

        /// <summary>Mirrors node 13's expression: 24 fps snapped onto the model's 17k+5 frame grid.</summary>
        private static int FramesForSeconds(double seconds)
        {
            var frames = Math.Max(5, (int)Math.Round(seconds * 24));
            return frames + (5 - frames % 17 + 17) % 17;
        }

        #endregion

        #region Analyze state

        public bool IsAnalyzing
        {
            get => _isAnalyzing;
            set
            {
                if (_isAnalyzing == value) return;
                _isAnalyzing = value;
                if (value)
                {
                    _analyzeStarted = DateTime.UtcNow;
                    AnalyzePhase = _isDerivingWardrobe ? "Writing the wardrobe…" : "Preparing…";
                    _analyzeClock.Start();
                }
                else
                {
                    _analyzeClock.Stop();
                    _analyzeStarted = default;
                    AnalyzePhase = string.Empty;
                }
                OnPropertyChanged();
                OnPropertyChanged(nameof(AnalyzeBusyText));
                OnPropertyChanged(nameof(AnalyzeButtonText));
                OnCanExecuteChanged();
            }
        }

        public string AnalyzeButtonText => IsAnalyzing ? "⏳ Analyzing…" : "🔍 Analyze → H3 Prompt";

        /// <summary>What the analysis is doing right now. A chain is one llama-server turn that reports
        /// nothing at all until it lands — minutes of it, on a local model.</summary>
        public string AnalyzePhase
        {
            get => _analyzePhase;
            private set
            {
                if (_analyzePhase == value) return;
                _analyzePhase = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AnalyzeBusyText));
            }
        }

        /// <summary>The phase with a clock running behind it — what tells a stalled server apart from a slow
        /// one, which a phase on its own cannot.</summary>
        public string AnalyzeBusyText
        {
            get
            {
                var phase = string.IsNullOrEmpty(_analyzePhase) ? "Analyzing…" : _analyzePhase;
                if (_analyzeStarted == default) return phase;
                var elapsed = DateTime.UtcNow - _analyzeStarted;
                return $"{phase}  {elapsed.ToString(@"m\:ss")}";
            }
        }

        /// <summary>Analyze needs something to work from — the location image, a story, or a keyframe.
        /// Deliberately not gated on a render being in flight: it talks to the llama-server.</summary>
        public bool CanAnalyze => (HasEnvironment || HasStoryText || HasKeyframes) && !IsAnalyzing;

        public bool CanGenerate =>
            HasPrompt && HasAnyCharacter && AllSheetsReady && !IsAnalyzing &&
            KeyframesForClip(1).Count + CastPanelCount + (WiresEnvironment ? 1 : 0) <= MaxReferenceImages;

        #endregion

        #region File helpers

        /// <summary>Uploads a file to ComfyUI once, caching the returned input-folder name by path.</summary>
        private async Task<string> EnsureUploadedAsync(string path)
        {
            if (_uploadCache.TryGetValue(path, out var cached) && !string.IsNullOrEmpty(cached))
                return cached;
            if (!File.Exists(path))
                throw new FileNotFoundException($"Image is gone: {path}");

            var name = await _comfyUIService.UploadImageAsync(path);
            if (string.IsNullOrEmpty(name)) throw new Exception($"Failed to upload {Path.GetFileName(path)}.");
            _uploadCache[path] = name;
            AddLog($"Uploaded: {name}");
            return name;
        }

        /// <summary>Reads a file shipped next to the exe (workflow JSON or prompt), relative to BaseDirectory.</summary>
        private static async Task<string> LoadFileAsync(string relativePath, CancellationToken token)
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relativePath);
            if (!File.Exists(path))
                throw new FileNotFoundException($"File not found: {path}");
            return await File.ReadAllTextAsync(path, token);
        }

        private async Task<string?> ResolveImageToLocalAsync(string imageFile)
        {
            try
            {
                var settings = _settingsService.Settings;
                if (settings != null)
                {
                    var baseUrl = GetComfyUIBaseUrl();
                    var isRemote = IsComfyUIRemote(new Uri(baseUrl).Host);
                    var outputFolder = settings.ResolveOutputFolder(isRemote);
                    if (!string.IsNullOrEmpty(outputFolder))
                    {
                        var srcPath = Path.Combine(outputFolder, imageFile.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(srcPath))
                        {
                            await WaitForFileStableAsync(srcPath);
                            return srcPath;
                        }
                    }
                }

                var parts = imageFile.Split('/');
                var filename = parts.Last();
                var subfolder = parts.Length > 1 ? string.Join("/", parts.Take(parts.Length - 1)) : string.Empty;
                var bytes = await _comfyUIService.HttpClient.DownloadViewFileAsync(filename, subfolder, "output");
                if (bytes is { Length: > 0 })
                {
                    var tmp = Path.Combine(Path.GetTempPath(), $"h3ensemble_{Guid.NewGuid():N}_{filename}");
                    await File.WriteAllBytesAsync(tmp, bytes);
                    return tmp;
                }
            }
            catch (Exception ex)
            {
                AddLog($"Resolve image failed: {ex.Message}");
            }
            return null;
        }

        private string? FindTokenImageOnDisk(string runToken)
        {
            try
            {
                var settings = _settingsService.Settings;
                if (settings == null) return null;
                var baseUrl = GetComfyUIBaseUrl();
                var isRemote = IsComfyUIRemote(new Uri(baseUrl).Host);
                var outputFolder = settings.ResolveOutputFolder(isRemote);
                if (string.IsNullOrEmpty(outputFolder)) return null;

                var candidates = new List<string>();
                foreach (var folder in new[] { outputFolder, Path.Combine(outputFolder, OutputSubfolder) })
                {
                    if (Directory.Exists(folder))
                        candidates.AddRange(Directory.GetFiles(folder, "*.png")
                            .Where(f => Path.GetFileName(f).IndexOf(runToken, StringComparison.OrdinalIgnoreCase) >= 0));
                }
                return candidates.OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
            }
            catch (Exception ex)
            {
                AddLog($"Disk scan failed: {ex.Message}");
                return null;
            }
        }

        private async Task<string> SubmitSheetAsync(string json, CancellationToken token)
        {
            var workflow = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);
            var phase = SheetPhase;
            var progress = new Progress<ProgressMessage>(msg =>
            {
                if (msg.Data?.Value != null && msg.Data?.Max > 0)
                    Application.Current.Dispatcher.Invoke(() =>
                        SheetPhase = $"{phase} {msg.Data.Value}/{msg.Data.Max}");
            });

            var promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, progress, token);
            AddLog($"Workflow submitted, ID: {promptId}");
            return promptId;
        }

        #endregion
    }
}
