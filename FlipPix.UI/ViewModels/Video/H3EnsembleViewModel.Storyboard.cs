using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FlipPix.ComfyUI.Models;
using FlipPix.UI.Services;
using Application = System.Windows.Application;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// H3 Ensemble, part four: the clip chain — splitting it, stamping it, catching a model that loops —
    /// and the storyboard pass in which H3 renders each clip's opening frame before any clip is committed.
    /// </summary>
    public partial class H3EnsembleViewModel
    {
        #region Clip chain

        private const string ClipHeaderFormat = "=== CLIP {0} of {1} ===";

        private static readonly Regex ClipHeaderRegex = new(
            @"^[ \t]*[=#*\-–—\[]{0,6}[ \t]*CLIP[ \t]+(\d+)\b[^\r\n]{0,60}$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Splits a prompt chain into its individual clip prompts, headers removed. Text with no
        /// headers is one clip, so every caller can treat the single-clip case as a chain of length 1.</summary>
        private static List<string> SplitClips(string? text)
        {
            var t = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            if (t.Length == 0) return new List<string>();

            var headers = ClipHeaderRegex.Matches(t);
            if (headers.Count == 0) return new List<string> { t };

            var clips = new List<string>();
            var preamble = t[..headers[0].Index].Trim();

            for (var i = 0; i < headers.Count; i++)
            {
                var start = headers[i].Index + headers[i].Length;
                var end = i + 1 < headers.Count ? headers[i + 1].Index : t.Length;
                var body = t[start..end].Trim();

                if (i == 0 && preamble.Length > 0)
                    body = body.Length > 0 ? $"{preamble}\n\n{body}" : preamble;

                if (body.Length > 0) clips.Add(body);
            }

            return clips.Count > 0 ? clips : new List<string> { t };
        }

        private static string JoinClips(IReadOnlyList<string> clips)
        {
            if (clips.Count == 0) return string.Empty;
            if (clips.Count == 1) return clips[0].Trim();

            var sb = new StringBuilder();
            for (var i = 0; i < clips.Count; i++)
            {
                if (i > 0) sb.Append("\n\n");
                sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                    ClipHeaderFormat, i + 1, clips.Count);
                sb.Append("\n\n").Append(clips[i].Trim());
            }
            return sb.ToString();
        }

        /// <summary>
        /// Assembles every clip of a chain against the current keyframes, cast, location and wardrobe.
        ///
        /// <para><b>Per clip, not per chain.</b> A hand-placed timeline lives inside one 15-second pass and
        /// belongs to clip 1 alone; a storyboard still is rendered for each clip and is that clip's own
        /// opening frame. And on this tab the <i>cast</i> is per clip too: with more than one clip the
        /// selective-cast rule drops every character that clip's body never names, so a five-hander's clip 3
        /// is numbered, wardrobed and wired as the two-hander it actually is.</para>
        /// </summary>
        private string AssembleChain(string chain)
        {
            var clips = SplitClips(chain);
            if (clips.Count == 0) return string.Empty;

            var cast = CastMembers;
            if (cast.Count == 0) return chain.Trim();

            var len = ClampLength(LengthSeconds);
            var selective = clips.Count > 1;

            var assembled = clips.Select((clip, i) => HybridCastPrompt.Assemble(
                    clip,
                    PromptKeyframesForClip(i + 1),
                    cast, CastWardrobe, len, SelectedMedium, SheetsShowWardrobe, selective,
                    environment: WiresEnvironment))
                .Where(c => c.Length > 0)
                .ToList();

            return JoinClips(assembled);
        }

        /// <summary>
        /// The same clip written for one character's face-refine pass: <b>no keyframes and no location</b>,
        /// so that character's panels are numbered from <c>&lt;Picture 1&gt;</c> and the prompt says in as
        /// many words that no attached picture aligns with a timestamp.
        ///
        /// <para>The shot list has to be rewritten as well as re-assembled: the model writes its own locks
        /// into it ("the frame is exactly &lt;Picture 2&gt; without reinterpretation"), and those numbers
        /// would land on a studio photograph — aimed at a 768px face crop.</para>
        /// </summary>
        private string RefinePromptFor(string clip, int subject)
        {
            var cast = CastMembers;
            if (cast.All(c => c.Index != subject)) return string.Empty;

            return HybridCastPrompt.Assemble(
                HybridCastPrompt.DropPictureLocks(HybridCastPrompt.Strip(clip)),
                Array.Empty<HybridCastPrompt.Keyframe>(), cast, CastWardrobe,
                ClampLength(LengthSeconds), SelectedMedium, SheetsShowWardrobe, focusSubject: subject);
        }

        private static readonly string[] AppearanceTerms =
        {
            "dress", "gown", "skirt", "blouse", "shirt", "t-shirt", "tee", "jacket", "coat", "trenchcoat",
            "hoodie", "sweater", "cardigan", "jumper", "vest", "waistcoat", "blazer", "suit", "uniform",
            "robe", "cloak", "cape", "armor", "armour", "kimono", "leggings", "jeans", "trousers", "pants",
            "shorts", "boots", "heels", "sneakers", "shoes", "sandals", "gloves", "scarf", "hat", "cap",
            "helmet", "mask", "goggles", "glasses", "necklace", "earrings", "bracelet", "belt", "apron",
            "ponytail", "braid", "braids", "bun", "bangs", "fringe", "dreadlocks", "blonde", "brunette",
            "redhead", "bearded", "freckles",
        };

        /// <summary>
        /// Flags wardrobe drift across a chain: an appearance word that appears in some clips but not all.
        ///
        /// <para>On an ensemble this is <b>advisory only</b> and deliberately quiet — clips genuinely cast
        /// different people, so a garment that appears in four clips out of nine may simply be the character
        /// who is in those four. It is reported when nearly every clip agrees, which is the shape a real
        /// costume change has.</para>
        /// </summary>
        private static string? DescribeWardrobeDrift(IReadOnlyList<string> clips)
        {
            if (clips.Count < 3) return null;

            var perClip = clips
                .Select(c => new HashSet<string>(
                    AppearanceTerms.Where(t => Regex.IsMatch(c, $@"\b{Regex.Escape(t)}\b", RegexOptions.IgnoreCase)),
                    StringComparer.OrdinalIgnoreCase))
                .ToList();

            // "In all but one or two" rather than "not in all": with a rotating cast, a term missing from
            // half the clips is a character who is not in them, not an outfit that changed.
            var inconsistent = perClip
                .SelectMany(s => s)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(t =>
                {
                    var seen = perClip.Count(s => s.Contains(t));
                    return seen < clips.Count && seen >= clips.Count - 2;
                })
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (inconsistent.Count == 0) return null;

            var shown = string.Join(", ", inconsistent.Take(8));
            var more = inconsistent.Count > 8 ? $" (+{inconsistent.Count - 8} more)" : string.Empty;
            return $"almost every clip names {shown}{more}, but not all of them";
        }

        /// <summary>
        /// Notes said once per Add to Queue, about the settings that decide whether a face survives the run.
        /// All of these are settings rather than bugs, and none is wrong for every clip — which is why they
        /// are advisories rather than changed defaults. They exist because each way a face comes back warped
        /// is invisible until the rendering has finished, and each is one checkbox away at this point.
        /// </summary>
        private IEnumerable<string> IdentityAdvisories()
        {
            if (OrderedKeyframes.Count == 0 && UsedStoryboard.Count == 0)
                yield return "Note: no keyframe stills, so the hybrid checkpoint's first-frame half has " +
                             "nothing to lock onto and this runs as plain reference-to-video. Press " +
                             "🎬 Preview Keyframes to have H3 render one opening frame per clip, or add a " +
                             "keyframe by hand.";

            // The ensemble-specific one. Nine slots divided five ways is one photograph per person, and one
            // photograph is the floor rather than a setting.
            var crowded = SplitClips(Prompt)
                .Select((c, i) => (Clip: i + 1,
                                   Cast: LoadedCharacters.Count(s => HybridCastPrompt.IncludesSubject(c, s.Index))))
                .Where(x => x.Cast >= 4)
                .ToList();
            if (crowded.Count > 0)
                yield return $"Note: clip {string.Join(", ", crowded.Select(x => x.Clip))} " +
                             $"cast{(crowded.Count == 1 ? "s" : "")} four or more characters at once. They " +
                             "share the nine reference slots, so each gets one photograph and each face is " +
                             "smaller in frame — this is where an ensemble re-casts somebody. Rewrite those " +
                             "beats around two or three people, or accept the softer likeness.";

            // The Part field is what tells the model, the costume designer and the sheet builder what a
            // non-person character actually is. Nothing else knows, and an unnamed one becomes "a creature".
            var unnamed = NonPersonCast.Where(c => !c.HasRole).Select(c => c.Index).ToList();
            if (unnamed.Count > 0)
                yield return $"WARNING: character {string.Join(", ", unnamed)} is not a person and has no " +
                             "Part filled in, so the prompt can only call it \"a character that is not a " +
                             "person\". Write what it is (\"Nimbus, a fluffy little cloud\") and re-stamp.";

            if (Interpolate)
                yield return $"Note: FILM ×{InterpolationFactor} is on. It invents every second frame from " +
                             "optical flow, so on fast action — spins, kicks, whip pans — it is the first " +
                             "thing to turn off if faces come back smeared. Interpolate afterwards in " +
                             "✨ Enhance Video instead.";

            if (FaceRefine && RefineDenoise > 0.4)
                yield return $"Note: face refine is at {RefineDenoise:0.00}. Above ~0.40 the pass re-invents " +
                             "a cropped face per frame rather than cleaning it, which on fast motion looks " +
                             "like boiling; 0.30–0.35 holds the likeness better in action clips.";
        }

        #endregion

        #region Loop guard

        /// <summary>
        /// Which clips of a chain repeat an earlier one verbatim, as <c>{ 1-based duplicate → 1-based
        /// original }</c>. A local model asked for many clips in one reply writes a few real beats and then
        /// alternates them to the end; nothing in the reply's shape says it has, and every duplicate is a
        /// full render of a file that already exists.
        /// </summary>
        private static Dictionary<int, int> FindRepeatedClips(IReadOnlyList<string> clips)
        {
            var firstSeen = new Dictionary<string, int>(StringComparer.Ordinal);
            var repeats = new Dictionary<int, int>();

            for (var i = 0; i < clips.Count; i++)
            {
                var key = HybridCastPrompt.Fingerprint(clips[i]);
                // Too little model-written text to call it a copy of anything — a stub clip, or a body that
                // is nothing but the code-written sections. Left alone rather than deleted.
                if (key.Length < 60) continue;

                if (firstSeen.TryGetValue(key, out var first)) repeats[i + 1] = first;
                else firstSeen[key] = i + 1;
            }

            return repeats;
        }

        private static string DescribeRepeats(IReadOnlyDictionary<int, int> repeats) =>
            string.Join(", ", repeats.OrderBy(r => r.Key).Select(r => $"clip {r.Key} = clip {r.Value}"));

        #endregion

        #region Storyboard — the keyframes H3 renders for itself

        /// <summary>
        /// One still per clip in the prompt box, rendered by H3 <i>before</i> the clips are: on screen to
        /// look at, re-roll or drop, and then handed straight back to the same model as that clip's opening
        /// frame lock.
        ///
        /// <para>It matters more here than on a two-hander. A chain written from a story starts with no
        /// stills at all, so the hybrid checkpoint's first-frame half is conditioned on nothing — and with a
        /// cast that changes from clip to clip, a locked opening frame is also the cheapest way to see
        /// <i>which people</i> H3 has actually put in a beat before spending twenty minutes rendering it.</para>
        /// </summary>
        public ObservableCollection<StoryboardShot> Storyboard => _storyboard;

        public bool HasStoryboard => _storyboard.Count > 0;

        /// <summary>
        /// Re-checks every still against the clip it was rendered for, and marks the ones whose beat has
        /// moved on. Compared on the model-written body alone: re-stamping rewrites the code-written half of
        /// every clip on purpose, and comparing whole prompts would call every still stale the moment one
        /// was locked in.
        /// </summary>
        private void RefreshStoryboardStaleness()
        {
            if (_storyboard.Count == 0) return;
            var clips = SplitClips(Prompt);
            foreach (var shot in _storyboard)
                shot.SetStale(shot.ClipIndex > clips.Count ||
                              !string.Equals(shot.SourceFingerprint,
                                             HybridCastPrompt.Fingerprint(clips[shot.ClipIndex - 1]),
                                             StringComparison.Ordinal));
        }

        /// <summary>The stills that are actually going to be locked, by 1-based clip index.</summary>
        private Dictionary<int, StoryboardShot> UsedStoryboard =>
            _storyboard.Where(s => s.Use && s.Exists)
                       .GroupBy(s => s.ClipIndex)
                       .ToDictionary(g => g.Key, g => g.First());

        public bool IsStoryboarding
        {
            get => _isStoryboarding;
            private set
            {
                if (_isStoryboarding == value) return;
                _isStoryboarding = value;
                OnPropertyChanged();
                OnCanExecuteChanged();
            }
        }

        public string StoryboardPhase
        {
            get => _storyboardPhase;
            private set { if (_storyboardPhase != value) { _storyboardPhase = value; OnPropertyChanged(); } }
        }

        /// <summary><c>MiniMaxH3ReferenceToVideo.length</c> accepts 5 and steps in 17s; anything below 124 is
        /// outside the range the model was trained on, which is a quality dial rather than an error.</summary>
        public IReadOnlyList<StoryboardLengthOption> StoryboardFrameOptions { get; } = new[]
        {
            new StoryboardLengthOption(5, "5 frames · cheapest, roughest"),
            new StoryboardLengthOption(22, "22 frames · fast"),
            new StoryboardLengthOption(39, "39 frames · balanced"),
            new StoryboardLengthOption(124, "124 frames · the trained minimum"),
        };

        public int StoryboardFrames
        {
            get => _storyboardFrames;
            set
            {
                var snapped = ClampStoryboardFrames(value);
                if (_storyboardFrames == snapped) return;
                _storyboardFrames = snapped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StoryboardSummary));
            }
        }

        public string PreviewKeyframesButtonText =>
            PromptClipCount > 1 ? $"🎬 Preview {PromptClipCount} Keyframes" : "🎬 Preview Keyframe";

        /// <summary>Deliberately not gated on a render being in flight — it takes the same GPU lease as
        /// everything else and simply waits its turn.</summary>
        public bool CanPreviewKeyframes =>
            HasPrompt && AllSheetsReady && HasAnyCharacter && !IsStoryboarding && !IsAnalyzing;

        public string StoryboardSummary
        {
            get
            {
                if (!HasPrompt)
                    return "Analyze first — the stills are rendered from the clips' own prompts.";
                if (_storyboard.Count == 0)
                {
                    var n = Math.Max(1, PromptClipCount);
                    return $"Not rendered yet. One {StoryboardFrames}-frame H3 pass per clip ({n} in total), " +
                           "on this graph, this canvas and this cast — frame 0 of each becomes that clip's " +
                           "opening lock. Face refine, interpolation, RTX and the audio branch are all pruned " +
                           "out, so a shot costs roughly a hundredth of the clip it previews.";
                }

                var used = UsedStoryboard.Count;
                var stale = _storyboard.Count(s => s.IsStale);
                var sb = new StringBuilder(
                    $"{used} of {_storyboard.Count} still(s) ticked — those clips open on exactly that frame " +
                    "at 0.00s, and their cast pictures move up one number. Untick one and that clip goes back " +
                    "to a reference-driven take with no lock.");
                if (stale > 0)
                    sb.Append($" ⚠ {stale} of them were rendered for a beat that has been rewritten since — " +
                              "re-roll or untick those.");
                return sb.ToString();
            }
        }

        private async Task BuildStoryboardAsync()
        {
            if (!CanPreviewKeyframes) return;
            var clips = SplitClips(Prompt);
            if (clips.Count == 0) return;
            await RunStoryboardAsync(Enumerable.Range(1, clips.Count).ToList(), reroll: false);
        }

        /// <summary>Re-renders one shot on a fresh seed — the cheap answer to a still that came back wrong,
        /// and the reason a shot carries a seed of its own at all.</summary>
        private async Task RerollShotAsync(StoryboardShot? shot)
        {
            if (shot == null || IsStoryboarding) return;
            await RunStoryboardAsync(new[] { shot.ClipIndex }, reroll: true);
        }

        private async Task RunStoryboardAsync(IReadOnlyList<int> clipIndices, bool reroll)
        {
            var chain = SplitClips(Prompt);
            var wanted = clipIndices.Where(i => i >= 1 && i <= chain.Count).ToList();
            if (wanted.Count == 0) return;

            IsStoryboarding = true;
            StoryboardPhase = "Preparing…";

            _storyboardCts?.Dispose();
            _storyboardCts = new CancellationTokenSource();
            var token = _storyboardCts.Token;

            WorkflowQueueCoordinator.WorkflowLease? lease = null;
            try
            {
                var frames = ClampStoryboardFrames(StoryboardFrames);
                AddLog($"=== {TabLogName}: storyboarding {wanted.Count} clip(s) — a {frames}-frame H3 pass " +
                       "each, frame 0 kept as that clip's opening lock ===");
                if (frames < 124)
                    AddLog($"Note: {frames} frames is below MiniMaxH3ReferenceToVideo's trained range " +
                           "(124–362 per its own tooltip). It is the cheap end of the dial, and a still can " +
                           "come back softer than the clip it stands for — raise the storyboard length if " +
                           "one looks unlike the model's own work.");

                StoryboardPhase = IsProcessing || IsProcessingQueue
                    ? "Waiting for the current render to finish…"
                    : "Waiting for the GPU…";
                lease = await _workflowCoordinator.AcquireAsync("H3Ensemble", token);

                StoryboardPhase = "Checking ComfyUI…";
                var comfyOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(s => AddLog($"[Auto-Restart] {s}"));
                if (!comfyOk) throw new Exception("ComfyUI is not running.");
                if (!_comfyUIService.IsConnected)
                {
                    StoryboardPhase = "Connecting to ComfyUI…";
                    await _comfyUIService.ConnectAsync();
                }

                for (var n = 0; n < wanted.Count; n++)
                {
                    token.ThrowIfCancellationRequested();
                    var clipIndex = wanted[n];
                    var clip = chain[clipIndex - 1];
                    var progress = wanted.Count > 1 ? $" ({n + 1}/{wanted.Count})" : string.Empty;

                    var existing = _storyboard.FirstOrDefault(s => s.ClipIndex == clipIndex);
                    var seed = reroll || existing == null || existing.Seed <= 0
                        ? System.Random.Shared.NextInt64(0, long.MaxValue)
                        : existing.Seed;

                    StoryboardPhase = $"Rendering clip {clipIndex}'s opening frame…{progress}";
                    var beat = HybridCastPrompt.ActionSummary(clip, 110);
                    AddLog($"Storyboard clip {clipIndex}: {beat}");

                    var candidates = await RenderClipStillsAsync(clipIndex, chain.Count, clip, seed, frames, token);
                    if (candidates.Count == 0)
                    {
                        AddLog($"WARNING: clip {clipIndex}'s still was not produced — that clip keeps whatever " +
                               "lock it already had.");
                        continue;
                    }

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        var target = _storyboard.FirstOrDefault(s => s.ClipIndex == clipIndex);
                        if (target == null)
                        {
                            target = new StoryboardShot(clipIndex, LoadImagePreview, OnStoryboardChanged);
                            _storyboard.Insert(_storyboard.Count(s => s.ClipIndex < clipIndex), target);
                        }
                        target.SetShot(beat, candidates, seed, HybridCastPrompt.Fingerprint(clip));
                    });
                    AddLog($"Storyboard clip {clipIndex}: {Path.GetFileName(candidates[0])} (seed {seed})");
                }

                // The picture numbering just changed under the prompt: <Picture 1> is now a frame lock and
                // the cast moved up one. Nothing else re-stamps on its own, and a prompt left un-stamped is
                // one that describes the still as a studio photograph.
                if (HasPrompt) Prompt = AssembleChain(Prompt);
                StoryboardPhase = $"{UsedStoryboard.Count} still(s) ready — untick any that are wrong.";
                AddLog(StoryboardSummary);
            }
            catch (OperationCanceledException)
            {
                AddLog("Storyboard cancelled");
                StoryboardPhase = "Cancelled";
            }
            catch (Exception ex)
            {
                AddLog($"ERROR (storyboard): {ex.Message}");
                StoryboardPhase = $"Error: {ex.Message}";
            }
            finally
            {
                lease?.Dispose();
                IsStoryboarding = false;
                _storyboardCts?.Dispose();
                _storyboardCts = null;
                OnStoryboardChanged();
            }
        }

        /// <summary>
        /// One clip → the stills H3 makes of its opening. The prompt is the clip's own, re-assembled as pure
        /// reference generation: this pass has no keyframes, so a <c>&lt;Picture n&gt;</c> left in it would
        /// point at a cast photograph. The location <i>is</i> kept — this still is about to be frame 0 of a
        /// render in that place, and a preview shot somewhere else is a preview of nothing.
        ///
        /// <para>Virtual so the H3 Multi tab can render its stills on the turbo graph its clips run on —
        /// the node ids the wiring below writes belong to the hybrid graph.</para>
        /// </summary>
        protected virtual async Task<IReadOnlyList<string>> RenderClipStillsAsync(
            int clipIndex, int clipCount, string clip, long seed, int frames, CancellationToken token)
        {
            var cast = CastMembers;
            if (cast.Count == 0) throw new Exception("No cast is loaded.");

            var stillPrompt = HybridCastPrompt.Assemble(
                HybridCastPrompt.DropPictureLocks(HybridCastPrompt.Strip(clip)),
                Array.Empty<HybridCastPrompt.Keyframe>(), cast, CastWardrobe,
                ClampLength(LengthSeconds), SelectedMedium, SheetsShowWardrobe,
                selectiveCast: clipCount > 1, environment: WiresEnvironment);
            if (stillPrompt.Length == 0)
                throw new Exception($"Clip {clipIndex} has no body to render a still from.");

            // Mirrors the submit path: a clip that never names a character is not shown their photographs,
            // and the prompt above was numbered for exactly that cast.
            var panels = CurrentCastPanels(stillPrompt);
            if (panels.Count == 0)
                throw new Exception("The cast has no reference panels to render from — build the sheets first.");

            var pictures = new List<string>(panels);
            if (WiresEnvironment) pictures.Add(EnvironmentPath);

            var uploaded = new List<string>();
            foreach (var picture in pictures) uploaded.Add(await EnsureUploadedAsync(picture));

            var json = await LoadFileAsync(WorkflowFileName, token);
            json = EnsureInputPrimitives(json);
            json = WireReferenceImages(json, uploaded, out _);

            var runToken = $"storyboard_{DateTime.Now:yyyyMMdd_HHmmss}_c{clipIndex:00}";
            SetInput(ref json, NodePrompt, "value", stillPrompt);
            SetInput(ref json, NodeResolution, "aspect_ratio", ResolvedAspectRatio);
            SetInput(ref json, NodeResolution, "megapixels", Megapixels);
            SetInput(ref json, NodeSeed, "noise_seed", seed);
            // The whole saving, and the only thing that differs from the clip this previews: the frame count.
            // Written as a literal on the reference node *and* as the duration the rest of the graph derives
            // its frame count from — node 6's sampler preview reads node 13 too. The canvas is deliberately
            // NOT reduced: this still is about to be frame 0 of a render at exactly this size.
            SetInput(ref json, NodeDuration, "value", frames / (double)OutputFrameRate);
            SetInput(ref json, NodeReference, "length", frames);

            var saves = WireStillOutputs(ref json, frames, runToken);
            json = PruneToOutputs(json, saves.Select(s => s.Key), out var pruned);
            if (pruned > 0)
                AddLog($"Storyboard graph pruned to the stills: {pruned} node(s) removed (the audio branch, " +
                       "face refine, interpolation, RTX and the video mux).");

            var promptId = await SubmitStoryboardAsync(json, token);
            var byNode = await _comfyUIService.HttpClient.GetOutputsByNodeAsync(promptId, token);

            var stills = new List<string>();
            foreach (var save in saves)
            {
                if (!byNode.TryGetValue(save.Key, out var outs) || outs.Count == 0) continue;
                var local = await ResolveImageToLocalAsync(outs[0]);
                if (local == null || !File.Exists(local)) continue;
                stills.Add(KeepStill(local, clipIndex, save.Value, seed));
            }
            return stills;
        }

        /// <summary>The panels of everyone an assembled prompt actually casts, in wiring order — the same
        /// selection Add to Queue freezes onto a queue item, resolved live because the storyboard renders
        /// from the form rather than from a queued job.</summary>
        protected IReadOnlyList<string> CurrentCastPanels(string assembledPrompt)
        {
            var paths = new List<string>();
            foreach (var slot in LoadedCharacters)
            {
                if (!HybridCastPrompt.IncludesSubject(assembledPrompt, slot.Index)) continue;
                var plan = ReferencePlanFor(slot);
                var panels = ResolvePanels(slot.PanelPaths.ToList(), slot.SheetPath, slot.Index);
                paths.AddRange(SelectPanels(panels, plan.Indices, plan.Views, slot.Index,
                                            slot.IsPerson, slot.IsGroup).Paths);
            }
            return paths;
        }

        /// <summary>
        /// Hangs one <c>ImageFromBatch</c> + <c>SaveImage</c> pair off the decoded frames per still worth
        /// keeping. Frame 0 is the one that becomes the lock; on a longer preview the midpoint and the last
        /// frame are saved too, because by then the model has moved the camera and one of them is often the
        /// composition actually wanted.
        /// </summary>
        /// <returns>SaveImage node id → the batch index it holds, in candidate order.</returns>
        private IReadOnlyList<KeyValuePair<string, int>> WireStillOutputs(
            ref string json, int frames, string runToken)
        {
            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new Exception("Workflow JSON could not be parsed.");
            RequireClass(root, NodeBaseFrames, "VAEDecode");

            var indices = new List<int> { 0 };
            if (frames >= 22) { indices.Add(frames / 2); indices.Add(frames - 1); }

            var map = new List<KeyValuePair<string, int>>();
            for (var i = 0; i < indices.Count; i++)
            {
                var pick = (StillPickIdBase + i).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var save = (StillSaveIdBase + i).ToString(System.Globalization.CultureInfo.InvariantCulture);
                root[pick] = new JsonObject
                {
                    ["inputs"] = new JsonObject
                    {
                        ["image"] = new JsonArray(NodeBaseFrames, 0),
                        ["batch_index"] = indices[i],
                        ["length"] = 1,
                    },
                    ["class_type"] = "ImageFromBatch",
                    ["_meta"] = new JsonObject { ["title"] = $"Storyboard frame {indices[i]}" }
                };
                root[save] = new JsonObject
                {
                    ["inputs"] = new JsonObject
                    {
                        ["images"] = new JsonArray(pick, 0),
                        ["filename_prefix"] = $"{OutputSubfolder}/{runToken}_f{indices[i]:000}",
                    },
                    ["class_type"] = "SaveImage",
                    ["_meta"] = new JsonObject { ["title"] = $"Storyboard save {indices[i]}" }
                };
                map.Add(new KeyValuePair<string, int>(save, indices[i]));
            }

            json = root.ToJsonString();
            return map;
        }

        /// <summary>Copies a still out of ComfyUI's output folder into the tab's own. A lock living in a temp
        /// file is a lock that goes missing between queueing a chain and rendering clip 8 of it.</summary>
        protected string KeepStill(string local, int clipIndex, int frameIndex, long seed)
        {
            try
            {
                var dir = Path.Combine(
                    _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(),
                    OutputFolderName, "storyboard");
                Directory.CreateDirectory(dir);
                var name = $"clip{clipIndex:00}_f{frameIndex:000}_{seed % 100000:00000}_" +
                           $"{DateTime.Now:yyyyMMdd_HHmmss}{Path.GetExtension(local)}";
                var kept = Path.Combine(dir, name);
                File.Copy(local, kept, true);
                return kept;
            }
            catch (Exception ex)
            {
                AddLog($"Storyboard still kept where ComfyUI wrote it ({ex.Message}).");
                return local;
            }
        }

        /// <summary>Submits a storyboard render. Reports through its own phase line and never touches the
        /// progress bar or the status line — a queued clip may well be rendering underneath it.</summary>
        protected async Task<string> SubmitStoryboardAsync(string json, CancellationToken token)
        {
            var workflow = JsonSerializer.Deserialize<JsonElement>(json);
            var phase = StoryboardPhase;
            var progress = new Progress<ProgressMessage>(msg =>
            {
                if (msg.Data?.Value != null && msg.Data?.Max > 0)
                    Application.Current.Dispatcher.Invoke(() =>
                        StoryboardPhase = $"{phase} {msg.Data.Value}/{msg.Data.Max}");
            });

            var promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, progress, token);
            AddLog($"Storyboard submitted, ID: {promptId}");
            return promptId;
        }

        private void ClearStoryboard()
        {
            if (_storyboard.Count == 0) return;
            _storyboard.Clear();   // the collection change re-stamps the prompt — see OnStoryboardChanged
            AddLog("Storyboard cleared — the clips go back to reference-driven takes with no frame lock.");
        }

        private void OnStoryboardChanged()
        {
            // Unticking a still, or stepping to another frame of one, changes what <Picture 1> is — so the
            // prompt is re-stamped there and then rather than waiting for Add to Queue to do it silently.
            // Assemble ∘ Strip is idempotent, which is what makes this safe to fire on every click.
            if (HasPrompt && !IsStoryboarding) Prompt = AssembleChain(Prompt);

            OnPropertyChanged(nameof(HasStoryboard));
            OnPropertyChanged(nameof(StoryboardSummary));
            OnPropertyChanged(nameof(PicturePlanSummary));
            OnPropertyChanged(nameof(ReferenceBudgetSummary));
            OnPropertyChanged(nameof(PromptHealthSummary));
            OnPropertyChanged(nameof(CanPreviewKeyframes));
            OnPropertyChanged(nameof(PreviewKeyframesButtonText));
            OnPropertyChanged(nameof(CanAddKeyframe));
            ClearStoryboardCommand.NotifyCanExecuteChanged();
            PreviewKeyframesCommand.NotifyCanExecuteChanged();
        }

        /// <summary>The reference node takes 5 and steps in 17s — 5, 22, 39, 56, … — and the server rejects
        /// anything off that grid rather than rounding it.</summary>
        protected static int ClampStoryboardFrames(int frames)
        {
            var clamped = Math.Clamp(frames, 5, 362);
            return clamped - (clamped - 5) % 17;
        }

        #endregion
    }
}
