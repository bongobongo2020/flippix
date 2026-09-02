using System;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using MessageBox = System.Windows.MessageBox;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// "H3 Experimental" tab — the 🪪🌀 H3 Duo flow with the prompt writing routed through the
    /// <b>ComfyUI-MiniMaxH3-Prompt-Writer</b> (duckyshell), reached as an MCP-style tool call against the
    /// llama-server the app is already configured to talk to (10.0.0.138 in the usual setup).
    ///
    /// <para><b>How the tool loop works.</b> llama-server exposes tool calling client-side: the model emits
    /// a <c>tool_calls</c> reply and the <i>client</i> is handed the call to execute. So the flow here runs
    /// in two turns — exactly the shape the ComfyUI extension's own backend uses:</para>
    /// <list type="number">
    /// <item><b>The model submits a Creative Brief.</b> One chat turn that offers the model a single tool,
    /// <c>h3_prompt_writer</c>, with <c>tool_choice</c> forced to it. The model's whole job in that turn is
    /// to read the story and call the tool with the brief: the story's events in order, the setting, the
    /// cast in their <c>&lt;Picture N&gt;</c> tags, and the fight budget — how the chain's N clips are
    /// shared across the story's own action.</item>
    /// <item><b>FlipPix executes the tool</b> — it plays the prompt-writer server. The brief, the locked
    /// wardrobe, the cast tags and the N × S-second clip plan are sent back to the same llama-server
    /// wrapped in the H3 Prompt Writer's own system wrapper plus the official MiniMax prompt-writing guide
    /// it bundles (both copied verbatim from the repo into <c>prompts/prompt2json/h3pw_*.md</c>), under
    /// the chain layer (<c>h3pw_chain.md</c>) that carries this tab's rules: headers, self-contained
    /// clips, tag-only identity, the wardrobe lock, and the action-expansion rule — the story's fight is
    /// dissected wind-up → strike → contact → recoil → fall → recovery, shot count scaled to each clip's
    /// seconds, and <b>nothing outside the story's own action is invented</b>. The tool's result is the
    /// clip chain, which lands in the prompt box and flows through the stock Duo queue/render/FFmpeg-join
    /// machinery unchanged.</item>
    /// </list>
    ///
    /// <para>Everything else is inherited: the story/scene inputs, the wardrobe derived once and locked
    /// (populated automatically the moment a story lands, as always), the two character cards and their
    /// panel-split sheets, the queue, and the turbo render (draft → 2× latent upscale → finish) on this
    /// tab's own copy of the Duo graph.</para>
    ///
    /// <para><b>When the run starts.</b> Loading a story .txt derives the wardrobe and nothing else —
    /// the chain is written against the clip plan, and the plan is wrong until the video time says what
    /// it should be. Setting the per-clip length (debounced, so stepping through values is one run) is
    /// what starts the writer; the Analyze button re-runs it by hand at any time.</para>
    /// </summary>
    public partial class H3ExperimentalViewModel : H3DuoViewModel
    {
        // ── The MCP-style tool the model is offered in turn 1. The name matches what the user's
        //    llama-server MCP config would expose; since llama-server hands tool calls back to the
        //    client, FlipPix executes it either way. ────────────────────────────────────────────
        private const string PromptWriterTool = "h3_prompt_writer";

        private const string PromptWriterToolDescription =
            "The MiniMax H3 Prompt Writer. Give it a creative brief for a story video chain and it returns " +
            "complete H3 prompts, one per clip, in the official MiniMax format. Call it once per chain.";

        /// <summary>The JSON Schema of the tool's arguments — the grammar llama-server constrains the call to.</summary>
        private const string PromptWriterToolSchema = """
            {
              "type": "object",
              "properties": {
                "creative_brief": {
                  "type": "string",
                  "description": "The complete creative brief for the whole chain, in plain language: the story's events in their order, the setting (period, place, time of day, weather, mood), the visual style, how each character is cast onto their <Picture N> tag, and the fight budget - how the N clips are shared across the story's own action, which exchange each clip dissects and from what angles. Quote the wardrobe wording given to you. Expand ONLY the story's own action; invent no new events."
                },
                "clip_count": {
                  "type": "integer",
                  "description": "How many clips the chain has (the number given in the request)."
                },
                "seconds_per_clip": {
                  "type": "number",
                  "description": "Duration of each clip in seconds (the number given in the request)."
                }
              },
              "required": ["creative_brief", "clip_count", "seconds_per_clip"]
            }
            """;

        // ── The H3 Prompt Writer's own files, copied verbatim from duckyshell/ComfyUI-MiniMaxH3-Prompt-Writer ──
        private const string WrapperFile = "h3pw_system_wrapper.md";   // backend/system_prompts.py SYSTEM_WRAPPER
        private const string GuideFile = "h3pw_guide_base.md";         // guides/VIDEO_PROMPT_WRITING_GUIDE_base_en.md
        private const string ChainFile = "h3pw_chain.md";              // this tab's chain/action-expansion layer

        public H3ExperimentalViewModel(
            ComfyUIService comfyUIService,
            LMStudioService lmStudioService,
            IAppLogger logger,
            FlipPix.Core.Services.SettingsService settingsService,
            IServiceProvider? serviceProvider,
            WorkflowQueueCoordinator workflowCoordinator,
            IFileDialogService fileDialogService)
            : base(comfyUIService, lmStudioService, logger, settingsService, serviceProvider,
                   workflowCoordinator, fileDialogService)
        {
            // The tab is for long stories — a two-minute chain is what it is for, and the H3 Cast
            // family's 15s default meant every run started by dragging the slider the whole way
            // across. Set here rather than in the base so the other cast tabs keep theirs.
            StoryDurationSeconds = 120;

            // The tab's own store, so its story chains and the I2V tab's takes never share a picker.
            _chainLibrary = new ScenePromptLibrary(AddLog, ScenePromptLibrary.FolderFor("h3experimental"));
            OpenChainLibraryCommand = new RelayCommand(async () => await OpenChainLibraryAsync());
            SaveChainCommand = new RelayCommand(async () => await SaveCurrentChainAsync(manual: true));

            AddLog("H3 Experimental initialized — story chains are written through the H3 Prompt Writer " +
                   "tool (brief → official MiniMax guide → clips); a story derives the wardrobe, and " +
                   "setting the video time runs the writer");

            // Off the constructor's thread: the index is read from disk and this tab is on the Video
            // Generator's startup path.
            _ = PrimeChainLibraryAsync();
        }

        /// <summary>The Duo graph's copy under this tab's name — experiments cannot break the Duo tab's file.</summary>
        protected override string WorkflowFileName => "workflow/video/h3-minimax/h3-experimental.json";

        protected override string OutputSubfolder => "h3_experimental";

        protected override string OutputFileStem => "H3Experimental";

        protected override string OutputFolderName => "H3Experimental";

        protected override string TabDisplayName => "H3 Experimental";

        protected override string QueueFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlipPix", "queue", "h3experimental_queue.json");

        // ── The trigger: the video time is what starts the pipeline ────────────────────────────────

        // Debounce for the auto-run below — cancelled and re-armed on every length change so stepping
        // through values fires one run, not one per click.
        private CancellationTokenSource? _autoAnalyzeCts;

        /// <summary>
        /// A story .txt landing derives the wardrobe (the stock debounce on the story text already does
        /// that) but does <b>not</b> start the chain — the clip plan is wrong until the video time says
        /// what it should be, and a chain written against the wrong plan is a chain written again. The
        /// run starts here instead, once the per-clip length is set: debounced two seconds so arrowing
        /// through values fires a single run against the value you settle on.
        /// </summary>
        protected override void OnLengthSecondsChanged()
        {
            if (!HasStoryText) return; // the prompt-writer flow is story-driven; nothing to auto-run
            // A recall is restoring the length a saved chain was written at — the chain is already written,
            // and running the writer again would overwrite it two seconds later.
            if (_restoringChain) return;

            _autoAnalyzeCts?.Cancel();
            _autoAnalyzeCts?.Dispose();
            var cts = new CancellationTokenSource();
            _autoAnalyzeCts = cts;
            _ = AutoAnalyzeAfterLengthChangeAsync(cts);
        }

        private async Task AutoAnalyzeAfterLengthChangeAsync(CancellationTokenSource cts)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
                if (!CanAnalyze || IsAnalyzing) return;

                var len = ClampLength(LengthSeconds);
                AddLog($"Video time set — {len:0.#}s per clip → {PlannedClipCount} clips. " +
                       "Running the H3 Prompt Writer...");
                await AnalyzeAsync();
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (ReferenceEquals(_autoAnalyzeCts, cts))
                {
                    _autoAnalyzeCts = null;
                    cts.Dispose();
                }
            }
        }

        /// <summary>
        /// The stock file-load behaviour plus one log line saying where the go button moved to — the
        /// wardrobe derives on its own as always, and the chain waits for the video time.
        /// </summary>
        protected override async Task LoadStoryFileAsync()
        {
            await base.LoadStoryFileAsync();
            if (HasStoryText)
                AddLog("Story loaded — the wardrobe is being derived. Set the video time (clip length) " +
                       "and the H3 Prompt Writer will run on its own.");
        }

        // ── The flow ────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Story runs go through the two-turn prompt-writer tool loop; a scene image with no story keeps
        /// the stock flow — the prompt writer's whole premise is a story brief to expand.
        /// </summary>
        protected override async Task AnalyzeAsync()
        {
            if (!HasStoryText)
            {
                await base.AnalyzeAsync();
                return;
            }

            IsAnalyzing = true;
            _analyzeCts?.Dispose();
            _analyzeCts = new CancellationTokenSource();
            var token = _analyzeCts.Token;
            // What the status line said before the writer borrowed it — a render in flight owns that line
            // and gets it back untouched when the writer is done.
            var statusBeforeWriting = ProcessingStatus;

            try
            {
                var model = await ResolveLlmModelAsync(token);
                if (model == null) return;

                var len = ClampLength(LengthSeconds);
                var clipCount = PlannedClipCount;

                AddLog($"H3 Prompt Writer: writing a {clipCount}-clip chain ({clipCount} × {len:0.#}s = " +
                       $"{clipCount * len:0.#}s continuous) from the story — via {_lmStudioService.DescribeTarget(model)}");

                if (StoryText.Length > 20000)
                    AddLog($"WARNING: the story is {StoryText.Length:N0} characters — a local model will very " +
                           "likely truncate it. Cut it down to the beats you want on screen.");

                // The wardrobe is still decided here, ahead of the chain, exactly as on the Duo tab — the
                // tool's result is stamped with it and the sheets are built from it.
                if (!await EnsureWardrobeAsync(token, model))
                    AddLog("WARNING: the wardrobe could not be written — the clips will each describe the " +
                           "outfits themselves, which is where between-clip costume changes come from.");

                // ── Turn 1 — the model submits the Creative Brief through the tool call ─────────────
                ProcessingStatus = "H3 Prompt Writer: preparing the creative brief...";

                var briefSystem =
                    "You are the director of a MiniMax H3 story chain. You never write video prompts yourself — " +
                    "that is what the h3_prompt_writer tool is for. Your one job this turn is to call that tool " +
                    "with the best possible creative brief: the story's events in their order, the setting read " +
                    "out of the prose (period, place, time of day, weather, mood), the visual style the material " +
                    "calls for, each character cast onto their <Picture N> tag, the quoted wardrobe, and above " +
                    "all the FIGHT BUDGET — how the chain's clips are shared across the story's own action: " +
                    "which exchange each clip dissects (wind-up, strike, contact, recoil, fall, recovery) and " +
                    "from what angles, so the story's final event lands in the final clip. The story's own " +
                    "action is expanded and NOTHING is invented: no new events, journeys, locations, " +
                    "conversations or outcomes the prose does not contain. Call the tool exactly once.";

                var briefUser = BuildBriefRequest(len, clipCount);

                var call = await _lmStudioService.CallToolAsync(
                    model,
                    briefSystem,
                    briefUser,
                    PromptWriterTool,
                    PromptWriterToolDescription,
                    PromptWriterToolSchema,
                    maxTokens: Math.Min(32000, 2500 + 1200 * clipCount),
                    cancellationToken: token,
                    sampling: LlmSampling.StoryChainBrief);

                if (call == null)
                    throw new Exception("the model did not call the prompt writer tool — the server may not " +
                                        "support tool calling. Re-run, or use the 🪪🌀 H3 Duo tab.");

                // Grammar-constrained as the call is, a local model occasionally emits an arguments string
                // that is not clean JSON (an unescaped quote or newline inside the brief). One retry before
                // giving up on the shape; the last attempt's raw payload stands as the brief if even the
                // retry will not parse — a degraded brief beats a dead run.
                var (brief, briefClips, briefSeconds) = ParseBriefCall(call.Function.Arguments);
                if (string.IsNullOrWhiteSpace(brief))
                {
                    AddLog("The brief came back malformed — asking the model to submit it again...");
                    var retry = await _lmStudioService.CallToolAsync(
                        model,
                        briefSystem,
                        briefUser,
                        PromptWriterTool,
                        PromptWriterToolDescription,
                        PromptWriterToolSchema,
                        maxTokens: Math.Min(32000, 2500 + 1200 * clipCount),
                        cancellationToken: token,
                        sampling: LlmSampling.StoryChainBrief);
                    if (retry != null)
                    {
                        call = retry;
                        (brief, briefClips, briefSeconds) = ParseBriefCall(call.Function.Arguments,
                            fallbackToRaw: true);
                    }
                }

                if (string.IsNullOrWhiteSpace(brief))
                    throw new Exception("the prompt writer was called with an empty creative brief.");

                // The brief runs away into word-salad too, and that is the worse of the two failures: the
                // writer reads the budget line for a clip and writes what it says, so one degenerate line
                // in the brief becomes a degenerate clip — and the brief's tail is where the chain's
                // ending lives. One clean re-submission before falling back to the cut.
                (brief, briefClips, briefSeconds) = await StabilizeBriefAsync(
                    model, briefSystem, briefUser, clipCount,
                    (brief, briefClips, briefSeconds), token);

                if (briefClips != clipCount)
                    AddLog($"Note: the brief budgets {briefClips} clips; the chain will be written as " +
                           $"{clipCount} (the tab's plan).");

                AddLog($"Brief submitted through the {call.Function.Name} tool ({brief.Length:N0} chars) — " +
                       "executing the prompt writer: official MiniMax guide + the chain rules...");

                // ── Turn 2 — FlipPix executes the tool: wrapper + guide + chain layer ───────────────
                ProcessingStatus = "H3 Prompt Writer: writing the clip chain...";

                var system = string.Join("\n\n",
                    await ReadSystemPromptAsync(WrapperFile, token),
                    await ReadSystemPromptAsync(GuideFile, token),
                    await ReadSystemPromptAsync(ChainFile, token));

                var user = BuildWriterRequest(brief, len, clipCount, briefSeconds);

                // Headroom as in the stock flow, tightened: the guide budgets 350–500 words per clip, so
                // 2,000 tokens per extra clip is ~2.5× what an honest clip needs — and a reply that
                // degenerates cannot burn an afternoon running to an 11k-token ceiling.
                var maxTokens = Math.Min(48000, 6000 + 2000 * (Math.Max(1, clipCount) - 1));

                var result = HasSceneImage
                    ? await _lmStudioService.AnalyzeImageWithSystemPromptAsync(
                        model,
                        SceneImagePath,
                        user,
                        system,
                        maxTokens: maxTokens,
                        cancellationToken: token,
                        sampling: LlmSampling.StoryChainFormatted)
                    : await _lmStudioService.SendTextChatAsync(
                        model,
                        system,
                        user,
                        maxTokens: maxTokens,
                        cancellationToken: token,
                        sampling: LlmSampling.StoryChainFormatted);

                // ── Post-writing passes, all deterministic on the clip bodies ──────────────────
                // 1. The runaway guard: a reply that collapsed into unpunctuated word-salad (observed:
                //    one clip's description turning into 141,000 characters of noun-associations that ran
                //    to the token ceiling) is truncated at its last complete sentence, and clip bodies
                //    left structurally incomplete by the cut are dropped — a shorter honest chain beats
                //    a full-length one with a clip that cannot render.
                // 2. Every clip of a two-person fight must name BOTH fighters — an opponent left as an
                //    untagged pronoun has no identity in that clip, and renders as the tagged fighter
                //    fighting a duplicate of themselves (the failure this pass exists to prevent).
                //    Offending bodies get one focused rewrite turn each; whatever survives is warned about.
                // 3. Timestamps are padded to the guide's MM:SS.mmm shape — models write "00:9.3".
                // 4. Stamping is NON-selective on this tab: both cast members' references and wardrobe
                //    line land in every clip even if a body still forgot the tag, because a two-hander
                //    fight has both fighters on screen throughout — clipping either one's references is
                //    how the duplicate-of-self render happens.
                var bodies = SplitClips(TruncateDegenerateTail(CleanOutput(result)))
                    .Select((b, i) => SanitizeClipFields(b, i + 1))
                    .ToList();
                var dropped = bodies.Where(b => !KeepsClipStructure(b)).ToList();
                if (dropped.Count > 0)
                    AddLog($"WARNING: {dropped.Count} clip(s) came back structurally incomplete (missing " +
                           "fields) and were dropped. Re-run Analyze, or write the missing clips into the " +
                           "prompt box by hand.");
                bodies = bodies.Where(KeepsClipStructure).ToList();
                if (HasCharacter2 && bodies.Count > 0)
                    bodies = await RepairUntaggedOpponentAsync(model, bodies, token);
                var perClipSeconds = briefSeconds > 0 ? briefSeconds : len;
                bodies = bodies.Select(b => NormalizeTimestamps(b, perClipSeconds)).ToList();

                var cleaned = JoinClips(bodies
                    .Select(b => CastPromptStamp.Apply(b, Panels1, Panels2, CastWardrobe,
                                                       selectiveCast: false, CastDescriptor))
                    .Where(c => c.Length > 0)
                    .ToList());
                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    Prompt = cleaned;
                    var written = PromptClipCount;
                    AddLog(written > 1
                        ? $"Chain written by the H3 Prompt Writer ({written} clips, {cleaned.Length} chars, " +
                          $"{CountShots(cleaned)} shots total)"
                        : $"Prompt written by the H3 Prompt Writer ({cleaned.Length} chars, {CountShots(cleaned)} shots)");

                    if (written != clipCount)
                        AddLog($"WARNING: asked for {clipCount} clip(s) but the writer returned {written}. " +
                               "Add to Queue enqueues what is in the prompt box — re-run Analyze, or edit the " +
                               "headers by hand.");

                    // The both-fighters check, said out loud on the bodies: the stamped reference line names
                    // both characters in every clip by construction now, so this warning is about the prose —
                    // a body still naming only one fighter leaves the opponent's actions tied to the stamped
                    // line alone. Worth re-running Analyze when it appears.
                    if (HasCharacter2)
                    {
                        var untagged = SplitClips(cleaned)
                            .Select((body, i) => (Index: i + 1,
                                                 Tagged: body.Contains("<Picture 1>", StringComparison.Ordinal) &&
                                                        body.Contains("<Picture 2>", StringComparison.Ordinal)))
                            .Where(c => !c.Tagged)
                            .Select(c => c.Index)
                            .ToList();
                        if (untagged.Count > 0)
                            AddLog($"WARNING: clip(s) {string.Join(", ", untagged)} still name only one fighter — " +
                                   "the opponent in those clips rides on the stamped reference line instead of " +
                                   "their own tag. Re-run Analyze, or tag <Picture 2> in those clips by hand.");
                        else
                            AddLog("Cast check: every clip names both fighters — no duplicate-of-self renders.");
                    }

                    // Filed as soon as it exists: a chain costs a long llama-server turn and is not
                    // reproducible run to run, so it is worth keeping before anything else can go wrong.
                    await SaveCurrentChainAsync(manual: false);

                    var drift = DescribeWardrobeDrift(SplitClips(cleaned).Select(CastPromptStamp.Strip).ToList());
                    if (drift != null)
                        AddLog(HasCastWardrobe
                            ? $"Note: the clip bodies describe the cast's appearance and {drift}. Every clip " +
                              "carries the same wardrobe block ahead of its description and that block outranks " +
                              "them, so the outfits should still hold — but if a clip comes out wrong, " +
                              "harmonise those words too."
                            : $"WARNING: the clips describe the cast's appearance and {drift}, and there is no " +
                              "wardrobe locked to override them — they will change outfits between clips.");
                }
                else
                {
                    AddLog("WARNING: the H3 Prompt Writer returned an empty result");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AddLog($"ERROR during analysis: {ex.Message}");
                MessageBox.Show($"H3 Prompt Writer failed:\n{ex.Message}", "H3 Experimental",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsAnalyzing = false;
                _analyzeCts?.Dispose();
                _analyzeCts = null;
                // The writer's own status line is not left standing: it said "writing the clip chain..."
                // for as long as the tab stayed open, which reads as a run that never finished — and it
                // was the only thing on screen explaining why Add to Queue was still greyed out. It now
                // reports what the queue is actually waiting for.
                if (IsProcessing || IsProcessingQueue) ProcessingStatus = statusBeforeWriting;
                else RefreshQueueReadinessStatus(force: true);
            }
        }

        // The last readiness line this tab wrote. The status line is shared with the render pipeline, so it
        // is only ever rewritten while it still says what we last put there — a render's own progress is
        // never stomped.
        private string _readinessStatus = string.Empty;

        /// <summary>
        /// Puts the reason Add to Queue is greyed out on the status line, and keeps it current: the usual
        /// answer is the character sheets, and they finish building long after the writer has stopped.
        /// </summary>
        private void RefreshQueueReadinessStatus(bool force = false)
        {
            if (IsAnalyzing || IsBuildingSheets || IsProcessing || IsProcessingQueue) return;
            // The writer's own line is claimed on the way out (force); after that the line is only ever
            // updated while it still says what we last wrote.
            if (!force && !string.IsNullOrEmpty(ProcessingStatus) &&
                !string.Equals(ProcessingStatus, _readinessStatus, StringComparison.Ordinal)) return;

            _readinessStatus = DescribeQueueReadiness();
            ProcessingStatus = _readinessStatus;
        }

        protected override void OnCanExecuteChanged()
        {
            base.OnCanExecuteChanged();
            RefreshQueueReadinessStatus();
        }

        /// <summary>
        /// What the status line says once the writer has stopped — the reason
        /// <see cref="H3CastViewModel.CanGenerate"/> is false, said out loud. Building the sheets is the
        /// usual answer: the chain writes fine without them, but a job cannot be queued until every loaded
        /// character has one.
        /// </summary>
        private string DescribeQueueReadiness()
        {
            if (string.IsNullOrWhiteSpace(Prompt))
                return "No prompt written — press Analyze to run the H3 Prompt Writer.";

            var clips = PromptClipCount > 1 ? $"{PromptClipCount}-clip chain written" : "Prompt written";

            if (!HasCharacter1)
                return $"{clips} — load character 1 and build the sheets before queueing.";
            if (!AllSheetsReady)
                return $"{clips} — press 🃏 Build Sheets; Add to Queue stays off until every " +
                       "character has a sheet.";

            return $"{clips} — ready to queue.";
        }

        // ── Request builders ───────────────────────────────────────────────────────────────────────

        /// <summary>Turn 1's user message: the story, the cast in their tags, the locked wardrobe, and the
        /// N × S-second plan the brief has to budget the fight across.</summary>
        private string BuildBriefRequest(double len, int clipCount)
        {
            var wardrobe = HasCastWardrobe
                ? "The wardrobe, already decided and locked — quote it word for word inside the brief:\n" +
                  CastWardrobe.Trim()
                : "No wardrobe has been decided — the brief must dress the cast itself (read the outfits off " +
                  "the story and the setting it calls for) and word them identically every time they appear.";

            var cast = HasCharacter2
                ? $"Two character reference images are attached to every clip: <Picture 1> (Character 1) and " +
                  "<Picture 2> (Character 2). Cast the story's people onto those tags — and budget BOTH fighters " +
                  "into every clip: in a two-person fight both are on screen throughout, so the brief's per-clip " +
                  "plan names what EACH of them is doing in that clip."
                : "One character reference image is attached to every clip: <Picture 1> (Character 1). Cast " +
                  "the story's protagonist onto that tag.";

            return
                $"{cast}\n" +
                $"{wardrobe}\n" +
                $"The chain: {clipCount} clips, each {len:0.##} seconds, that together run the whole story " +
                $"continuously ({clipCount} × {len:0.##}s ≈ {clipCount * len:0.##}s total).\n\n" +
                "THE ACTION IS THE PLOT: the runtime is longer than the prose, and every extra second comes " +
                "from slowing the story's OWN action down — its fights above all, each exchange dissected " +
                "into wind-up, strike, contact, recoil, fall and recovery, each movement with its own shots " +
                "and angles — never from new events, journeys, locations or outcomes the story does not " +
                "narrate. Budget the clips across the story's events so its FINAL event is what the LAST " +
                "clip shows, and say that budget in the brief, clip by clip.\n\n" +
                $"The story:\n{StoryText.Trim()}";
        }

        /// <summary>Turn 2's user message: the brief the tool was called with, plus everything the writer
        /// needs that is not the model's to decide — the wardrobe, the tag map, the clip plan and the
        /// per-clip shot target scaled to the clip's seconds.</summary>
        private string BuildWriterRequest(string brief, double len, int clipCount, double briefSeconds)
        {
            var seconds = briefSeconds > 0 ? briefSeconds : len;
            var shots = Math.Clamp((int)Math.Round(seconds * 0.8, MidpointRounding.AwayFromZero), 6, 14);

            var wardrobe = HasCastWardrobe
                ? "The wardrobe — ALREADY DECIDED, not yours to choose. Each line below opens 'Character N " +
                  "wears …'; the garments after that prefix are the outfit. Attach them to the character's tag the " +
                  "first time they appear in a clip — '<Picture N>, wearing <those garments>,' — in exactly those " +
                  "words, and keep that wording identical everywhere else it is mentioned. This quote is the ONLY " +
                  "clothing wording you may use: never re-dress the cast from the story's own prose — where the " +
                  "story describes clothing differently or more floridly than the quote, the quote wins, word for " +
                  "word:\n" +
                  CastWardrobe.Trim()
                : "No wardrobe was decided ahead of this run: read the outfits off the brief's setting, write " +
                  "them out in full once, and then use the identical wording in every clip.";

            var cast = HasCharacter2
                ? "Two character reference images are attached to every clip: <Picture 1> (Character 1) and " +
                  "<Picture 2> (Character 2). Refer to the characters ONLY by those tags — never describe " +
                  "their hair, faces, skin, build or age; the tags carry all of it. Write what they DO. " +
                  "BOTH FIGHTERS ARE TAGGED IN EVERY CLIP — this is a hard rule: name <Picture 1> AND " +
                  "<Picture 2> at each fighter's first appearance in every clip, and wherever either is " +
                  "named, struck, grabbed or reacted to after that. A fighter must NEVER appear only as an " +
                  "untagged pronoun or label — no 'he', 'his chest', 'the man', 'her opponent' standing in " +
                  "for a character the clip has not tagged; the tag replaces the name everywhere ('drives " +
                  "her knee into <Picture 2>’s nose'). A clip that names only one fighter renders that " +
                  "fighter fighting a duplicate of themselves — the failure this rule exists to prevent. " +
                  "Never mention 'the story', 'this clip' or the viewer; write only what is seen and heard."
                : "One character reference image is attached to every clip: <Picture 1> (Character 1). Refer " +
                  "to the character ONLY by that tag — never describe their hair, face, skin, build or age; " +
                  "the tag carries all of it. Write what they DO. Never mention 'the story', 'this clip' " +
                  "or the viewer; write only what is seen and heard.";

            return
                "Mode: T2VA body with character references — there is NO first-frame image, so every clip " +
                "begins directly with the three core fields; never write the I2VA/FL2VA/L2VA anchor line. The " +
                "multi-shot structure is explicitly required by this brief — a fight chain cut like a music " +
                "video is the user's intent, not cinematic embellishment.\n" +
                "Creative brief from the director:\n" +
                $"{brief.Trim()}\n\n" +
                $"{cast}\n" +
                $"{wardrobe}\n\n" +
                $"The chain: write {clipCount} clips, each {seconds.ToString("0.##", CultureInfo.InvariantCulture)} " +
                "seconds long, that together play the story continuously from its first action to its last. " +
                $"Separate them with the \"=== CLIP n of {clipCount} ===\" headers. Each clip carries roughly " +
                $"{shots} shots scaled to its {seconds.ToString("0.##", CultureInfo.InvariantCulture)} seconds, " +
                "every timestamp inside the clip's own duration; each clip opens already in motion and ends " +
                "mid-action so the cuts read as one continuous take.\n" +
                "Hard limits, enforced after the reply: every sentence is a complete sentence that ends with " +
                "its own punctuation — never write an unbroken chain of words; each clip's three fields are " +
                "complete before the next clip's header; and after the last clip the reply stops.";
        }

        /// <summary>Pulls <c>creative_brief</c> / <c>clip_count</c> / <c>seconds_per_clip</c> out of the tool
        /// call's arguments. Defensive on every field: a local model's arguments string can arrive wrapped,
        /// partial, or with the numbers as text — the plan the tab already holds is the fallback. A payload
        /// that is not clean JSON returns empty (the caller retries), unless <paramref name="fallbackToRaw"/>
        /// is set — the last attempt's raw text then stands as the brief.</summary>
        private static (string Brief, int Clips, double Seconds) ParseBriefCall(string? arguments, bool fallbackToRaw = false)
        {
            var empty = (string.Empty, 0, 0.0);
            if (string.IsNullOrWhiteSpace(arguments)) return empty;

            try
            {
                var node = JsonNode.Parse(arguments);
                if (node is not JsonObject obj) return empty;

                var brief = obj["creative_brief"]?.GetValue<string>() ?? string.Empty;
                var clips = obj["clip_count"]?.GetValue<int>() ?? 0;
                var seconds = obj["seconds_per_clip"] is JsonValue sv && sv.TryGetValue<double>(out var d) ? d : 0.0;

                return string.IsNullOrWhiteSpace(brief) ? empty : (brief.Trim(), clips, seconds);
            }
            catch
            {
                // An arguments string that is not clean JSON: empty, so the caller retries — and on the
                // final attempt the raw payload itself is the brief.
                return fallbackToRaw ? (arguments.Trim(), 0, 0.0) : empty;
            }
        }

        // ── Post-writing passes ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Keeps a degenerate creative brief out of the writer turn. The brief is one long string, and the
        /// runaway that hits the clip writer hits it the same way — the observed one walked the thesaurus
        /// from the middle of clip 8's budget line to the token ceiling, and the writer then copied that
        /// salad, verbatim, into the description of every clip it fed.
        ///
        /// <para>A clean brief passes through untouched. A degenerate one is re-submitted once — a fresh
        /// sample usually lands fine — and whichever of the two carries more healthy prose is cut back to
        /// its last complete sentence. Truncation is the fallback, not the goal: the tail of a brief is
        /// where the story's ending is budgeted, so it is worth one more call to keep it.</para>
        /// </summary>
        private async Task<(string Brief, int Clips, double Seconds)> StabilizeBriefAsync(
            string model, string briefSystem, string briefUser, int clipCount,
            (string Brief, int Clips, double Seconds) submitted, CancellationToken token)
        {
            if (DegenerateCutIndex(submitted.Brief) < 0) return submitted;

            AddLog("WARNING: the creative brief degenerated into unpunctuated word-salad — the writer would " +
                   "copy that into every clip it budgets. Asking for the brief once more...");

            var retry = await _lmStudioService.CallToolAsync(
                model,
                briefSystem,
                briefUser,
                PromptWriterTool,
                PromptWriterToolDescription,
                PromptWriterToolSchema,
                maxTokens: Math.Min(32000, 2500 + 1200 * clipCount),
                cancellationToken: token,
                sampling: LlmSampling.StoryChainBrief);

            if (retry != null)
            {
                var second = ParseBriefCall(retry.Function.Arguments);
                if (!string.IsNullOrWhiteSpace(second.Brief) &&
                    HealthyLength(second.Brief) > HealthyLength(submitted.Brief))
                {
                    submitted = second;
                    if (DegenerateCutIndex(submitted.Brief) < 0)
                    {
                        AddLog("The re-submitted brief is clean.");
                        return submitted;
                    }
                }
            }

            var cut = DegenerateCutIndex(submitted.Brief);
            if (cut < 0) return submitted;

            AddLog($"WARNING: {submitted.Brief.Length - cut:N0} characters of word-salad cut off the end of " +
                   "the brief — the last clips are budgeted from a brief that stops early. Check how the " +
                   "chain ends, or re-run Analyze.");
            return (submitted.Brief[..cut].TrimEnd(), submitted.Clips, submitted.Seconds);
        }

        /// <summary>How much of a text is healthy prose — its whole length, or the index the runaway
        /// starts at. The measure the two brief submissions are compared on.</summary>
        private static int HealthyLength(string text)
        {
            var cut = DegenerateCutIndex(text);
            return cut < 0 ? (text?.Length ?? 0) : cut;
        }

        /// <summary>The three H3 field labels, in the order the guide writes them.</summary>
        private static readonly string[] ClipFieldLabels =
        {
            "integrated_multimodal_description:",
            "overall_soundscape:",
            "non_diegetic_music:",
        };

        /// <summary>
        /// The runaway guard applied field by field <i>inside</i> one clip.
        /// <see cref="TruncateDegenerateTail"/> only ever sees the end of the whole reply, so a clip that
        /// degenerated in the middle of the chain — the common case, because the model recovers at the next
        /// field label and writes the following clips normally — carried its word-salad through untouched
        /// and rendered it. Cutting each field back to its own last complete sentence removes the salad and
        /// keeps the clip: the structure survives, so the clip is not dropped for the sake of one bad field.
        /// A field that is salad end to end comes back empty and the clip fails
        /// <see cref="KeepsClipStructure"/> instead.
        /// </summary>
        private string SanitizeClipFields(string body, int clipNumber)
        {
            var marks = new List<(string Label, int Index)>();
            foreach (var label in ClipFieldLabels)
            {
                var index = body.IndexOf(label, StringComparison.Ordinal);
                if (index >= 0) marks.Add((label, index));
            }
            if (marks.Count == 0) return body;
            marks.Sort((a, b) => a.Index.CompareTo(b.Index));

            var parts = new List<string>();
            var preamble = body[..marks[0].Index].Trim();
            if (preamble.Length > 0) parts.Add(preamble);

            var removed = 0;
            for (var i = 0; i < marks.Count; i++)
            {
                var start = marks[i].Index + marks[i].Label.Length;
                var end = i + 1 < marks.Count ? marks[i + 1].Index : body.Length;
                var text = body[start..end].Trim();

                var cut = DegenerateCutIndex(text);
                if (cut >= 0)
                {
                    removed += text.Length - cut;
                    text = text[..cut].TrimEnd();
                }

                parts.Add($"{marks[i].Label} {text}".TrimEnd());
            }

            if (removed > 0)
                AddLog($"WARNING: clip {clipNumber} degenerated — {removed:N0} characters of unpunctuated " +
                       "word-salad cut back to the last complete sentence in the field(s) that ran away.");

            return string.Join("\n\n", parts);
        }

        /// <summary>Whether one field label is present AND followed by something to render. A label the
        /// runaway guard emptied is not a field the generator can use, so it does not count as present.</summary>
        private static bool HasFieldContent(string body, string label)
        {
            var index = body.IndexOf(label, StringComparison.Ordinal);
            if (index < 0) return false;

            var rest = body[(index + label.Length)..];
            foreach (var other in ClipFieldLabels)
            {
                if (string.Equals(other, label, StringComparison.Ordinal)) continue;
                var next = rest.IndexOf(other, StringComparison.Ordinal);
                if (next >= 0) rest = rest[..next];
            }
            return rest.Trim().Length > 0;
        }


        // ── The runaway guard ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The longest run of characters without sentence-ending punctuation a healthy reply can contain.
        /// Honest H3 prose punctuates every 100–200 characters (even a bare timestamp list carries the
        /// decimal points of <c>MM:SS.mmm</c>); the runaway this guard exists for produced 141,000
        /// characters with no terminator at all. 1,200 is a handful of sentences of headroom — far enough
        /// from anything an honest clip writes that the guard cannot fire on one, near enough that the
        /// guard trips within a paragraph of the model leaving the rails.
        /// </summary>
        private const int RunawaySuffixLimit = 1200;

        /// <summary>
        /// Where a reply's unpunctuated word-salad begins, as an index to cut at — the character after the
        /// last sentence terminator (<c>.</c> <c>!</c> <c>?</c>) before the runaway — or -1 when the text is
        /// healthy. A <c>.</c> between two digits is a decimal point, not a terminator: the guide's
        /// <c>MM:SS.mmm</c> timestamps are full of them, and counting one would cut the clip at the last
        /// timestamp instead of the last sentence — leaving a dangling <c>[Shot 5] At 00:10.</c> fragment
        /// at the end of the truncated body. The detector is otherwise deliberately dumb: a suffix with no
        /// sentence punctuation at all is not a property any honest H3 clip has, however creative the
        /// model gets.
        /// </summary>
        private static int DegenerateCutIndex(string text)
        {
            if (string.IsNullOrEmpty(text)) return -1;

            for (var i = text.Length - 1; i >= 0; i--)
            {
                var c = text[i];
                if (c != '.' && c != '!' && c != '?') continue;
                if (c == '.' && i > 0 && i + 1 < text.Length &&
                    char.IsDigit(text[i - 1]) && char.IsDigit(text[i + 1]))
                    continue; // decimal point — keep scanning for the real last sentence

                return text.Length - (i + 1) > RunawaySuffixLimit ? i + 1 : -1;
            }

            // No sentence terminator anywhere: degenerate iff the whole text is one long runway.
            return text.Length > RunawaySuffixLimit ? 0 : -1;
        }

        /// <summary>
        /// Cuts a degenerate reply at its last complete sentence (see <see cref="DegenerateCutIndex"/>),
        /// logging what was removed. Healthy text passes through untouched.
        /// </summary>
        private string TruncateDegenerateTail(string text)
        {
            var cut = DegenerateCutIndex(text);
            if (cut < 0) return text;

            AddLog($"WARNING: the writer degenerated — {text.Length - cut:N0} characters of unpunctuated " +
                   "word-salad truncated to the last complete sentence.");
            return text[..cut].TrimEnd();
        }

        /// <summary>Whether a clip body (canonical form) names BOTH fighters by tag. The hard rule this
        /// tab enforces: a two-person fight has both fighters on screen in every clip, and a fighter who
        /// appears only as an untagged pronoun renders as the other fighter fighting a duplicate of
        /// themselves — H3 casts every unnamed body from whichever references the clip was sent.</summary>
        private static bool NamesBothFighters(string body) =>
            body.Contains("<Picture 1>", StringComparison.Ordinal) &&
            body.Contains("<Picture 2>", StringComparison.Ordinal);

        /// <summary>Whether a clip body still carries the three H3 field labels <i>with something under
        /// each of them</i> — the structure a repair turn must preserve to be worth keeping. A retagged
        /// clip that lost its labels is worse than an untagged clip that kept them: the stamped reference
        /// line already carries both fighters, so the structure is the part that cannot be sacrificed. The
        /// content half of the test is what drops a clip <see cref="SanitizeClipFields"/> emptied — a field
        /// that was word-salad end to end leaves a bare label, and a bare label renders nothing.</summary>
        private static bool KeepsClipStructure(string body) =>
            ClipFieldLabels.All(label => HasFieldContent(body, label));

        /// <summary>
        /// One focused rewrite turn per offending clip: the body comes back with the opponent named by their
        /// tag at every point the prose acted on them, and nothing else changed. A repair that fails its own
        /// goal is discarded — the original body stands and the warning after stamping names the clip.
        /// </summary>
        private async Task<List<string>> RepairUntaggedOpponentAsync(
            string model, List<string> bodies, CancellationToken token)
        {
            const string repairSystem =
                "You repair one clip of a MiniMax H3 two-person fight. The clip you are given names at most one " +
                "fighter by their reference tag; one or both fighters appear only as an untagged pronoun or " +
                "label ('he', 'his chest', 'the man', 'her opponent'). Rewrite the clip so BOTH fighters are " +
                "named by their tags — <Picture 1> and <Picture 2> — at each fighter's first appearance, and " +
                "the tag replaces the name or pronoun at every point after that where either is named, " +
                "struck, grabbed or reacted to ('drives her knee into <Picture 2>'s nose', '<Picture 2> " +
                "roars'). Close-ups of a body part belong to the fighter whose part it is — say the tag. " +
                "Change NOTHING else. The clip's structure is sacred: the three field labels " +
                "'integrated_multimodal_description:', 'overall_soundscape:' and 'non_diegetic_music:' appear " +
                "exactly as they do in the input, in the same order, with the same shots, timestamps and " +
                "wording everywhere the pronoun was not standing in for the untagged fighter. Return only " +
                "the rewritten clip — no headers, no commentary.";

            for (var i = 0; i < bodies.Count; i++)
            {
                if (NamesBothFighters(bodies[i])) continue;

                token.ThrowIfCancellationRequested();
                AddLog($"Cast repair: clip {i + 1} names only one fighter — retagging the opponent...");
                try
                {
                    var rewritten = CleanOutput(await _lmStudioService.SendTextChatAsync(
                        model,
                        repairSystem,
                        bodies[i],
                        maxTokens: 4000,
                        cancellationToken: token));

                    // Both gates or nothing: the rewrite must name both fighters AND still be a structurally
                    // intact H3 clip. A repair that retags but drops the field labels is discarded — the
                    // stamped reference line carries both fighters anyway, so structure is not for sale.
                    if (!string.IsNullOrWhiteSpace(rewritten) && NamesBothFighters(rewritten) &&
                        KeepsClipStructure(rewritten))
                        bodies[i] = rewritten;
                    else
                        AddLog($"Cast repair: clip {i + 1} could not be retagged without losing the clip's " +
                               "structure — keeping the original body (the stamped reference line still " +
                               "carries both fighters).");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AddLog($"Cast repair: clip {i + 1} failed ({ex.Message}) — keeping the original body " +
                           "(the stamped reference line still carries both fighters).");
                }
            }

            return bodies;
        }

        /// <summary>Pads cut timestamps into the guide's <c>MM:SS.mmm</c> shape — and rescues the
        /// near-misses local models write: <c>At 00:9.3</c>, <c>at 00:05.80</c>, and the recurring
        /// seconds<b>:</b>sub-second family — <c>At 01:5000</c> (1.5s), <c>At 14:20</c> (14.20s),
        /// <c>At 01:00.400</c> (1.4s). Each match is read first as the guide's own MM:SS(.mmm); when that
        /// lands outside the clip's duration it is re-read as seconds plus a sub-second part (two digits =
        /// centiseconds, three or four = milliseconds). Whatever still does not fit is clamped just inside
        /// the clip's end, so no timestamp can point past the last frame.</summary>
        private static string NormalizeTimestamps(string text, double clipSeconds)
        {
            var limit = Math.Max(1.0, clipSeconds);

            // Plain decimal seconds — "At 01.35", "At 13.9" — converted to the guide's shape too. Run
            // after the colon pass, whose output ("At 00:01.350") cannot match this pattern.
            text = Regex.Replace(text, @"\b[Aa]t\s+(\d{1,3})\.(\d{1,3})\b", m =>
            {
                var seconds = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var millis = int.Parse(m.Groups[2].Value.PadRight(3, '0'), CultureInfo.InvariantCulture);
                if (seconds + millis / 1000.0 > limit)
                {
                    var clamped = Math.Max(0.0, limit - 0.75);
                    return Format(0, (int)clamped, (int)Math.Round((clamped - (int)clamped) * 1000));
                }
                return Format(seconds / 60, seconds % 60, millis);
            });

            return Regex.Replace(text, @"\b[Aa]t\s+(\d{1,2}):(\d{1,4})(?:\.(\d{1,3}))?\b", m =>
            {
                var first = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var secondRaw = m.Groups[2].Value;
                var second = int.Parse(secondRaw, CultureInfo.InvariantCulture);
                var fracMillis = m.Groups[3].Success
                    ? int.Parse(m.Groups[3].Value.PadRight(3, '0'), CultureInfo.InvariantCulture)
                    : 0;

                // The guide's own reading — only when the colon really is a minute separator and the
                // result fits inside the clip.
                if (secondRaw.Length <= 2 && second <= 59 && first * 60 + second + fracMillis / 1000.0 <= limit)
                    return Format(first, second, fracMillis);

                // The seconds:sub-second reading — two digits are centiseconds, three or four are millis.
                var subMillis = secondRaw.Length <= 2
                    ? second * 10
                    : int.Parse(secondRaw.PadRight(3, '0')[..3], CultureInfo.InvariantCulture);
                if (first + (subMillis + fracMillis) / 1000.0 <= limit)
                    return Format(0, first, subMillis + fracMillis);

                // Nothing honest fits — park it just inside the end and let the prose carry the beat.
                var clamped = Math.Max(0.0, limit - 0.75);
                return Format(0, (int)clamped, (int)Math.Round((clamped - (int)clamped) * 1000));
            });

            static string Format(int minutes, int seconds, int millis) =>
                $"At {minutes:00}:{seconds:00}.{millis:000}";
        }
    }
}
