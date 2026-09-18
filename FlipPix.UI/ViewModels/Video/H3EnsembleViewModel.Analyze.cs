using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using FlipPix.UI.Services;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// H3 Ensemble, part three: the wardrobe pass and the analysis that turns a location, a story and a cast
    /// of up to five into a chain of six-section H3 prompts.
    /// </summary>
    public partial class H3EnsembleViewModel
    {
        #region Analysis

        private async Task AnalyzeAsync()
        {
            if (!CanAnalyze) return;

            IsAnalyzing = true;
            _analyzeCts?.Dispose();
            _analyzeCts = new CancellationTokenSource();
            var token = _analyzeCts.Token;

            try
            {
                AnalyzePhase = "Finding the model…";
                var model = await ResolveLlmModelAsync(token);
                if (model == null) return;

                var len = ClampLength(LengthSeconds);
                var clipCount = PlannedClipCount;
                var fromImage = HasEnvironment;
                var keys = OrderedKeyframes;
                var source = fromImage
                    ? HasStoryText ? "the location image + the story text" : "the location image"
                    : "the story text";
                AddLog(clipCount > 1
                    ? $"Writing a {clipCount}-clip ensemble chain ({clipCount} × {len:0.#}s = {clipCount * len:0.#}s) " +
                      $"for {LoadedCharacterCount} character(s) from {source} — sending to {_lmStudioService.DescribeTarget(model)}"
                    : $"Writing a {len:0.#}s ensemble H3 prompt for {LoadedCharacterCount} character(s) from " +
                      $"{source} — sending to {_lmStudioService.DescribeTarget(model)}");

                if (HasStoryText && StoryText.Length > 20000)
                    AddLog($"WARNING: the story is {StoryText.Length:N0} characters — a local model will very " +
                           "likely truncate it. Cut it down to the beats you want on screen.");

                // A character with a part but no photograph is one the generator never receives: the cast
                // brief only names the subjects whose sheets are attached, so they would be missing from
                // the story with no other sign of it.
                var photoless = CastWithoutPhotos;
                if (photoless.Count > 0)
                    AddLog($"Note: character {string.Join(", ", photoless.Select(c => c.Index))} " +
                           $"({string.Join("; ", photoless.Select(c => c.Descriptor))}) " +
                           $"{(photoless.Count == 1 ? "has" : "have")} no photo yet, so " +
                           $"{(photoless.Count == 1 ? "it is" : "they are")} not in this prompt — the story " +
                           "is written around the characters whose sheets H3 will actually receive. Load " +
                           "their picture, build the sheets and Analyze again to write them in.");

                AnalyzePhase = "Dressing the cast…";
                if (!await EnsureWardrobeAsync(token, model))
                    AddLog("WARNING: the wardrobe could not be written — the clips will each describe the " +
                           "outfits themselves, which is where between-clip costume changes come from. " +
                           "Fill the wardrobe box in by hand, or press 🎽 Derive again.");

                AnalyzePhase = "Reading the system prompt…";
                var systemPrompt = await ReadSystemPromptAsync(SystemPromptFile, token);
                if (clipCount > 1)
                {
                    systemPrompt += "\n\n" + await ReadSystemPromptAsync(StorySystemPromptFile, token);
                    if (!fromImage)
                        systemPrompt += "\n\nNOTE FOR THIS RUN: there is no location image. Wherever the rules " +
                                        "above say to read the setting or the wardrobe off the location " +
                                        "image, read them off the STORY instead — decide each of them once, " +
                                        "and then repeat that wording verbatim in every clip.";
                }

                var draft = PromptClipCount > 1
                    ? "(the prompt box holds a previous sequence — ignore it and write a fresh one)"
                    : !HasPrompt
                        ? "(none — invent a sequence that suits the material above)"
                        : HybridCastPrompt.Strip(Prompt).Trim();

                var lengthBlock = clipCount > 1
                    ? $"Story sequence: write {clipCount} clips that together tell ONE continuous story " +
                      $"running about {clipCount * len:0.##} seconds in total. Each clip is {len:0.##} " +
                      "seconds long and is rendered separately, so each one must be a complete, " +
                      "self-contained set of the four sections. Separate them with a line spelled exactly " +
                      $"\"=== CLIP n of {clipCount} ===\", numbered 1 to {clipCount} in order.\n"
                    : $"Target duration: {len:0.##} seconds.\n";

                var keyBlock = BuildKeyframeBrief(keys, len, clipCount);
                var castBlock = BuildCastBrief(clipCount);
                var setBlock = BuildLocationBrief();

                // Ahead of the story rather than after it: the writer decides the medium in its first
                // sentence, and a rule that arrives after the material has already been read is one the
                // opening of [Shot 1] has stopped listening to.
                var styleRule = H3VisualStyles.Rule(VisualStyle);

                string userMessage;
                if (fromImage)
                {
                    var story = HasStoryText
                        ? StoryText.Trim()
                        : "(none — invent a story that suits the location and carry it from beginning to end)";

                    userMessage =
                        "Image role: this image is the LOCATION the video is set in — its setting, " +
                        "architecture, lighting, art style, mood and the wardrobe the cast wears. " +
                        "Any people visible in it are NOT the cast: they are scenery, and you must not write " +
                        "them into the video.\n" +
                        setBlock +
                        keyBlock +
                        castBlock + "\n" +
                        lengthBlock +
                        styleRule +
                        $"Story the video must tell:\n{story}\n" +
                        $"Draft idea from the user:\n{draft}";
                }
                else
                {
                    var wholeStory = clipCount > 1
                        ? $"Together the {clipCount} clips must tell the whole story below, beginning to end — " +
                          "split it into that many beats before writing anything, one beat per clip.\n"
                        : $"The whole story has to be told inside {len:0.##} seconds, so pick the beats that " +
                          "carry it and compress the rest; do not stop halfway through.\n";

                    userMessage =
                        "There is NO reference image of the location. The material below is the only source: " +
                        "read the setting, period, time of day, weather, lighting and mood out of it and " +
                        "write them into the prompt explicitly, keeping them consistent from the first shot " +
                        "to the last.\n" +
                        keyBlock +
                        castBlock + "\n" +
                        lengthBlock +
                        wholeStory +
                        styleRule +
                        (HasStoryText ? $"The story:\n{StoryText.Trim()}\n" : string.Empty) +
                        $"Draft idea from the user:\n{draft}";
                }

                var maxTokens = Math.Min(32000, 5000 + 2200 * (Math.Max(1, clipCount) - 1));

                // A chain is the one call in this app that asks a model for N structurally identical blocks
                // in a single turn, so it is the one call that needs repetition controls and a scratchpad to
                // plan the beats in. A single clip keeps the request the tabs have always sent.
                LlmSampling? sampling = clipCount > 1 ? LlmSampling.StoryChain : null;

                async Task<string> AskAsync(string message, LlmSampling? how) => fromImage
                    ? await _lmStudioService.AnalyzeImageWithSystemPromptAsync(
                        model, EnvironmentPath, message, systemPrompt,
                        maxTokens: maxTokens, cancellationToken: token, sampling: how)
                    : await _lmStudioService.SendTextChatAsync(
                        model, systemPrompt, message, maxTokens: maxTokens,
                        cancellationToken: token, sampling: how);

                AnalyzePhase = clipCount > 1
                    ? $"Writing {clipCount} clips — {_lmStudioService.DescribeTarget(model)}…"
                    : $"Writing the prompt — {_lmStudioService.DescribeTarget(model)}…";
                var assembled = AssembleChain(CleanOutput(await AskAsync(userMessage, sampling)));

                if (clipCount > 1 && !string.IsNullOrWhiteSpace(assembled))
                    assembled = await BreakLoopAsync(assembled, userMessage, AskAsync, token);

                if (!string.IsNullOrWhiteSpace(assembled))
                {
                    Prompt = assembled;
                    var written = PromptClipCount;
                    AddLog(written > 1
                        ? $"Chain written ({written} clips, {assembled.Length} chars, {CountShots(assembled)} shots total)"
                        : $"Prompt written ({assembled.Length} chars, {CountShots(assembled)} shots)");

                    if (written > clipCount)
                        AddLog($"WARNING: asked for {clipCount} clip(s) but the model returned {written}. " +
                               "Add to Queue enqueues what is in the prompt box — re-run Analyze, or edit the " +
                               "headers by hand.");
                    else if (written < clipCount)
                        AddLog($"{written} of the {clipCount} clips asked for. Add to Queue enqueues what is " +
                               $"in the prompt box, so this is {written * ClampLength(LengthSeconds):0.#}s of " +
                               "video — re-run Analyze, or write the missing beats in by hand.");

                    ReportKeyframeCoverage(assembled, keys);
                    AddLog(CastCoverageSummary);

                    var drift = DescribeWardrobeDrift(SplitClips(assembled).Select(HybridCastPrompt.Strip).ToList());
                    if (drift != null)
                        AddLog(HasCastWardrobe
                            ? $"Note: the clip bodies describe the cast's appearance and {drift}. Every clip " +
                              "carries the same wardrobe block ahead of its sections and that block outranks " +
                              "them, so the outfits should still hold."
                            : $"WARNING: the clips describe the cast's appearance and {drift}, and there is no " +
                              "wardrobe locked to override them — they will change outfits between clips. Fill " +
                              "the wardrobe box in (🎽 Derive) and press ✎ Re-stamp.");
                }
                else
                {
                    AddLog("WARNING: Analysis returned empty result");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AddLog($"ERROR during analysis: {ex.Message}");
                MessageBox.Show($"Analysis failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsAnalyzing = false;
                _analyzeCts?.Dispose();
                _analyzeCts = null;
            }
        }

        /// <summary>
        /// Tells the model exactly which picture numbers are frame locks and at what timestamps. It is the
        /// single most important paragraph in the request: everything else it writes is prose, and this is
        /// the part that has to line up with the wiring.
        /// </summary>
        private static string BuildKeyframeBrief(
            IReadOnlyList<KeyframeSlot> keys, double clipSeconds, int clipCount)
        {
            if (keys.Count == 0)
                return "KEYFRAMES: there are none. No picture is a frame — write one continuous take per " +
                       "clip with no frame lock at 0.00 and no `<Picture n>` anywhere in your text.\n";

            var sb = new StringBuilder(
                $"KEYFRAMES: {keys.Count} still(s) are attached as frame locks and are numbered in timestamp " +
                "order. Each one must be a shot boundary in your shot list, opening with the lock sentence:\n");
            for (var i = 0; i < keys.Count; i++)
                sb.Append($"  <Picture {i + 1}> is locked at {keys[i].Seconds:0.00} seconds" +
                          (i == 0 && keys[i].Seconds <= 0.001 ? " — the exact opening frame.\n" : ".\n"));
            sb.Append($"After the last lock the clip continues to {clipSeconds:0.00} seconds with no " +
                      "end-frame lock. Never name a `<Picture n>` above " + keys.Count + ": those numbers are " +
                      "the cast's studio photographs and the location, which are never frames.\n");
            if (clipCount > 1)
                sb.Append("These locks exist in CLIP 1 only. Clips 2 onwards must contain no `<Picture n>` at all.\n");
            return sb.ToString();
        }

        /// <summary>The location paragraph — only written when the picture is actually wired, because
        /// otherwise the model is being told about a reference the generator will never receive.</summary>
        private string BuildLocationBrief() =>
            !WiresEnvironment
                ? string.Empty
                : "LOCATION: the image above is also attached to the generator as a reference picture of the " +
                  "SET. The generator can see the place; it cannot see any of it that you do not put in a " +
                  "shot. Write every scene inside that location or in a part of it the camera has not shown " +
                  "yet, restate its architecture, materials, palette and light in every clip, and never move " +
                  "the story to a different place. Do not refer to it by a picture number, and do not cast " +
                  "anybody who happens to be visible in it.\n";

        /// <summary>
        /// How the cast is described to the model: named as subjects, given their part in the story, and with
        /// the wardrobe quoted as settled fact when there is one.
        ///
        /// <para><b>The ensemble-specific rule is the last one.</b> A five-hander is only affordable because
        /// each clip carries just the characters it names — so the model is told, in as many words, that it
        /// is allowed to leave people out of a beat, and that naming somebody who is not in the shot costs a
        /// reference slot that the people who <i>are</i> in it needed.</para>
        /// </summary>
        private string BuildCastBrief(int clipCount)
        {
            var loaded = LoadedCharacters;
            var sb = new StringBuilder();

            if (loaded.Count == 0)
                return "CAST: no character reference sheets are attached.\n";

            sb.Append($"CAST: {loaded.Count} character reference sheet(s) are attached to the generator. " +
                      "Refer to the characters only by these tags — never by a picture number and never by a " +
                      "name from the story; wherever the story names its characters, cast these subjects in " +
                      "those roles:\n");
            foreach (var slot in loaded)
            {
                sb.Append($"  <Subject {slot.Index}> — ");
                if (slot.IsPerson)
                    sb.Append($"a {slot.Noun}")
                      .Append(slot.HasRole ? $", playing {slot.Role}" : string.Empty)
                      .Append($". Use \"{slot.Pronoun}\" for them.");
                else
                    // The bug this branch exists for: a cloud, a mountain and a herd of goats were being
                    // told to be "a man" or "a woman", and the generator duly put people on screen.
                    sb.Append($"{slot.Descriptor}. NOT a person — this character is ")
                      .Append(slot.Kind == CharacterSlot.Creature ? "an animal or creature"
                              : slot.Kind == CharacterSlot.Group ? "several of the same thing acting together"
                              : "a thing, not a human being")
                      .Append(". Do not describe it as a man, a woman, a child or a person, do not give it a " +
                              "human face, human hair, human hands or a human body, and do not turn it into " +
                              "someone in a costume. Take its pronoun from the story — a story that calls it " +
                              "\"he\" means he — and if the story never says, use \"it\" (or \"they\" for " +
                              "several). It still acts: it moves, reacts and carries its beats.");
                sb.Append('\n');
            }

            sb.Append("You have NOT seen those sheets — the generator has. ");
            if (loaded.Any(c => c.IsPerson))
                sb.Append("For the people, use the stated pronoun and write no word for their hair, face, " +
                          "skin, build or age. ");
            if (loaded.Any(c => !c.IsPerson))
                sb.Append("For the non-human characters, write no word inventing their shape, colour or " +
                          "material either — their pictures carry all of that. Write what they DO. ");

            if (HasCastWardrobe)
            {
                sb.Append("CLOTHING IS ALREADY DECIDED AND IS NOT YOURS TO CHOOSE. The cast wear exactly this, " +
                          "in every shot of every clip:\n").Append(CastWardrobe.Trim()).Append('\n');
                sb.Append("Attach that outfit to the tag the first time each character appears in each clip — " +
                          "\"<Subject 1>, wearing …\" — copying the wording above rather than rephrasing it. ");
                sb.Append(SheetsShowWardrobe
                    ? "Their reference sheets were photographed in exactly these clothes, so the pictures and " +
                      "the words agree — do not contradict either. "
                    : "Their reference sheets are studio photographs and whatever those show them wearing is " +
                      "irrelevant. ");
                sb.Append("Never put them in anything else and never invent a costume change; the only clothing " +
                          "change allowed is one the user's story explicitly asks for.");
            }
            else if (HasEnvironment)
            {
                sb.Append("CLOTHING: dress the cast as the LOCATION image's period, place and situation " +
                          "plainly call for — NOT as their reference sheets show them. Write each outfit out " +
                          "explicitly the first time that character appears — garments, colours, materials, " +
                          "footwear, headwear, worn accessories — attached to their tag, and restate it in " +
                          "the same words every later time. A character who is not a person wears whatever " +
                          "the story gives them and nothing more; where the story gives them nothing, say " +
                          "nothing about clothing for them at all.");
            }
            else
            {
                sb.Append("CLOTHING: take the wardrobe from the STORY where it describes it, and where it does " +
                          "not, dress the cast in what the period, place and situation plainly call for. Write " +
                          "it out explicitly the first time each character appears and restate it in exactly " +
                          "the same words everywhere else.");
            }

            if (HasKeyframes)
                sb.Append(" Where a keyframe still shows the cast, that still wins at its own timestamp — the " +
                          "wardrobe words describe what they wear between the locks.");

            // Said once more, at the end, because it is the failure the whole non-person branch exists to
            // stop and the model has to carry it through a long chain.
            if (loaded.Any(c => !c.IsPerson))
                sb.Append("\n\nNOT EVERY CHARACTER IS A PERSON. " +
                          string.Join(" ", loaded.Where(c => !c.IsPerson).Select(c =>
                              $"<Subject {c.Index}> is {c.Descriptor}.")) +
                          " Write them as what they are, doing what such a thing would do — a cloud drifts, " +
                          "billows, thins and rains; a mountain looms, shadows and stands. Never write a " +
                          "human face, human limbs or human clothing onto one, never replace one with a " +
                          "person, and never put a person on screen to stand in for one.");

            // The rule that makes an ensemble affordable at all. Nine reference slots are shared by everyone
            // a clip names, so a character written into a beat they are not really in takes the likeness
            // budget away from the characters on screen.
            if (loaded.Count > 2)
                sb.Append($"\n\nENSEMBLE RULE — WHO IS IN EACH SHOT. There are {loaded.Count} characters and " +
                          "the generator can hold only a handful of reference photographs per clip. A " +
                          "character is only sent to the generator for a clip whose text actually names their " +
                          "tag, so:\n" +
                          "  - Name a subject ONLY in a clip where they are genuinely on screen. Do not list " +
                          "the whole cast in a clip that is really about two of them.\n" +
                          "  - Aim for two or three named subjects in any one clip. Four or more in a single " +
                          $"{ClampLength(LengthSeconds):0.#}-second clip is a crowd scene in which nobody's " +
                          "face is legible, and every one of them comes back looking like somebody else.\n" +
                          "  - A character who is present but not the point of the beat is better left out of " +
                          "that clip entirely than written in as background.\n" +
                          (clipCount > 1
                              ? $"  - Across the {clipCount} clips, give every one of the {loaded.Count} " +
                                "characters at least one clip of their own where they are named. A character " +
                                "no clip names never reaches the screen at all.\n"
                              : string.Empty));

            // Repeated here as well as in the system prompt because it is the rule that decides whether the
            // cast survive the clip: a face a handful of pixels wide is a face H3 re-invents.
            // The framing rule is about faces, so it is written for whoever has one. A mountain does not
            // need to be framed at a tenth of frame height — asking for that would fight the story.
            sb.Append(" FRAMING IS A HARD CONSTRAINT: no shot may be wider than a full-body wide shot — no " +
                      "ultra-wide, no extreme long shot, no aerial. ");
            if (loaded.Any(c => c.IsPerson))
                sb.Append("Every shot a person appears in must frame their face legibly. ");
            if (loaded.Any(c => !c.IsPerson))
                sb.Append("A character who is not a person must be large and clearly readable in every shot " +
                          "they are in — close enough that their shape, colour and materials are obvious, " +
                          "never a speck on a horizon. Where one of them is genuinely huge, frame a part of " +
                          "it rather than backing away to fit all of it in. ");
            sb.Append("Do not combine a wide framing with a fast or large camera move. The cast may move as " +
                      "violently as the story needs; it is the camera's distance that is constrained.");
            return sb.ToString();
        }

        /// <summary>Says out loud whether the model actually put every lock in the shot list — a keyframe the
        /// shots never mention is a keyframe H3 has no reason to land on.</summary>
        private void ReportKeyframeCoverage(string chain, IReadOnlyList<KeyframeSlot> keys)
        {
            if (keys.Count == 0) return;

            var clip1 = SplitClips(chain).FirstOrDefault() ?? string.Empty;
            var shots = HybridCastPrompt.SplitSections(clip1)
                                        .TryGetValue(HybridCastPrompt.DetailedDescription, out var d)
                        ? d : string.Empty;

            var missing = Enumerable.Range(1, keys.Count)
                .Where(n => !Regex.IsMatch(shots, $@"<\s*Picture\s+{n}\s*>", RegexOptions.IgnoreCase))
                .ToList();

            AddLog(missing.Count == 0
                ? $"Keyframes: all {keys.Count} lock(s) appear in the shot list at their timestamps."
                : $"WARNING: the shot list never names <Picture {string.Join(">, <Picture ", missing)}> — " +
                  "those stills are still attached and still declared as locks in retention_analysis, but the " +
                  "shots do not cut to them. Edit the shot list, or re-run Analyze.");
        }

        private static async Task<string> ReadSystemPromptAsync(string fileName, CancellationToken token)
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prompts", "prompt2json", fileName);
            if (!File.Exists(path))
                throw new FileNotFoundException($"System prompt not found: {path}");
            return await File.ReadAllTextAsync(path, token);
        }

        /// <summary>Strips the wrappers small models like to add without touching the section structure.</summary>
        private static string CleanOutput(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            text = text.Replace("**", "").Trim();

            if (text.StartsWith("```"))
            {
                var firstBreak = text.IndexOf('\n');
                if (firstBreak > 0) text = text[(firstBreak + 1)..];
                var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
                if (lastFence >= 0) text = text[..lastFence];
                text = text.Trim();
            }

            if (text.StartsWith("prompt:", StringComparison.OrdinalIgnoreCase))
                text = text[7..].TrimStart();
            if (text.Length > 1 && text[0] == '"' && text[^1] == '"')
                text = text[1..^1].Trim();

            return text.Trim();
        }

        /// <summary>Counts `[Shot n]` markers, purely for the log line.</summary>
        private static int CountShots(string prompt)
        {
            var count = 0;
            var idx = prompt.IndexOf("[Shot ", StringComparison.OrdinalIgnoreCase);
            while (idx >= 0)
            {
                count++;
                idx = prompt.IndexOf("[Shot ", idx + 6, StringComparison.OrdinalIgnoreCase);
            }
            return count;
        }

        #endregion

        #region Wardrobe (decided once, stamped into every clip)

        private async Task<string?> ResolveLlmModelAsync(CancellationToken token, bool quiet = false)
        {
            var baseUrl = _settingsService.Settings?.LMStudioSettings?.BaseUrl ?? "http://alien:8080";
            await _lmStudioService.SetBaseUrlAsync(baseUrl);

            var models = await _lmStudioService.GetAvailableModelsAsync(token);
            var model = _settingsService.Settings?.LMStudioSettings?.SelectedModel ?? string.Empty;
            if (string.IsNullOrEmpty(model) && models.Count > 0)
                model = models[0].Id ?? models[0].Name ?? string.Empty;
            if (!string.IsNullOrEmpty(model)) return model;

            if (quiet)
                AddLog("The wardrobe could not be written: no llama-server model is available. Start the " +
                       "server, then press 🎽 Derive.");
            else
                MessageBox.Show("No LM Studio / llama-server model available. Ensure the server is running and a model is loaded.",
                    "LM Studio Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        private async Task RederiveWardrobeAsync()
        {
            if (!CanAnalyze) return;

            IsAnalyzing = true;
            _analyzeCts?.Dispose();
            _analyzeCts = new CancellationTokenSource();
            var token = _analyzeCts.Token;

            try
            {
                // Checked before the model is even resolved: a wardrobe for nobody is worse than none,
                // because it is written into the lock block of every clip and a later top-up leaves it there.
                if (WardrobeCast.Count == 0)
                {
                    AddLog(NoCastToDress);
                    return;
                }

                var model = await ResolveLlmModelAsync(token);
                if (model == null) return;

                var dress = WardrobeCast;
                AnalyzePhase = "Writing the wardrobe…";
                AddLog($"Writing the look of {dress.Count} character(s) — " +
                       string.Join(", ", dress.Select(c => $"<Subject {c.Index}> {c.Descriptor}")) +
                       $" — sending to {_lmStudioService.DescribeTarget(model)}");

                var vague = dress.Where(c => !c.IsPerson && !c.HasRole).Select(c => c.Index).ToList();
                if (vague.Count > 0)
                    AddLog($"WARNING: character {string.Join(", ", vague)} is not a person and has no Part, " +
                           "so the designer is only being told \"a character that is not a person\". Write " +
                           "what it is and derive again.");
                var derived = await DeriveWardrobeAsync(model, dress, token);
                if (string.IsNullOrWhiteSpace(derived))
                {
                    AddLog("WARNING: the wardrobe came back empty — the box is unchanged.");
                    return;
                }

                SetDerivedWardrobe(derived, dress);
                if (HasPrompt)
                    AddLog("Press ✎ Re-stamp to write this wardrobe into the prompt already in the box — no " +
                           "need to re-run Analyze.");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AddLog($"ERROR writing the wardrobe: {ex.Message}");
                MessageBox.Show($"Writing the wardrobe failed:\n{ex.Message}",
                    TabLogName, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsAnalyzing = false;
                _analyzeCts?.Dispose();
                _analyzeCts = null;
            }
        }

        private void ClearWardrobe()
        {
            CastWardrobe = string.Empty;
            _wardrobeStoryStamp = string.Empty;
            _wardrobeCastStamp = string.Empty;
            IsWardrobeLocked = true;
            _wardrobeIsManual = false;
            ScheduleWardrobeDerive();
        }

        private void SetDerivedWardrobe(string wardrobe, IReadOnlyList<CharacterSlot> dressed)
        {
            var covers = WardrobeCast.All(c => CastPromptStamp.OutfitFor(wardrobe, c.Index).Length > 0);
            var partial = HasCastWardrobe && !covers;
            CastWardrobe = partial ? CastPromptStamp.MergeWardrobe(CastWardrobe, wardrobe) : wardrobe;
            _wardrobeStoryStamp = StorySourceStamp();
            _wardrobeCastStamp = CastSexStamp();
            AddLog(partial
                ? $"Wardrobe: character {string.Join(", ", dressed.Select(c => c.Index))} dressed; the rest "
                  + $"of the cast keeps what they had:\n{CastWardrobe.Trim()}"
                : $"Wardrobe locked:\n{CastWardrobe.Trim()}");

            var undressed = WardrobeCast
                .Where(c => CastPromptStamp.OutfitFor(CastWardrobe, c.Index).Length == 0).ToList();
            if (undressed.Count > 0)
                AddLog($"WARNING: character {string.Join(", ", undressed.Select(c => c.Index))} came back " +
                       "with no outfit. The next Analyze or Build Sheets writes one; press 🎽 Derive to do it now.");

            var stale = LoadedCharacters.Where(c => c.HasSheet && !c.SheetMatchesWardrobe).ToList();
            if (stale.Count > 0)
                AddLog($"Character {string.Join(", ", stale.Select(c => c.Index))}'s sheet was built in " +
                       "other clothes — press Build Character Sheet(s) again so the references H3 gets are " +
                       "wearing this wardrobe.");
        }

        /// <summary>Separates a stamp's fields — a control character, so no story text can forge one.</summary>
        private const char StampSeparator = (char)31;   // Unit Separator

        private string StorySourceStamp() =>
            StoryText.Trim() + StampSeparator + (HasEnvironment ? EnvironmentPath : string.Empty);

        /// <summary>
        /// What the wardrobe was last written <i>for</i>, per character: their slot, their sex and their part.
        ///
        /// <para>Keyed by slot rather than positional, unlike the two-hander tabs. With five slots the cast
        /// is genuinely sparse — characters 1, 3 and 4 is an ordinary state — and a positional stamp read
        /// back with <c>stamp[index - 1]</c> would compare character 4 against character 3's noun and rewrite
        /// an outfit whose character sheet has already been built.</para>
        /// </summary>
        private string CastSexStamp() =>
            string.Join(StampSeparator, _cast.Select(c => $"{c.Index}={c.Noun}|{c.Role}"));

        private Dictionary<int, string> ParseCastStamp(string stamp)
        {
            var map = new Dictionary<int, string>();
            foreach (var part in stamp.Split(StampSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                if (eq > 0 && int.TryParse(part[..eq], out var index)) map[index] = part[(eq + 1)..];
            }
            return map;
        }

        /// <summary>
        /// The story is the source of the wardrobe, so a change to the story (or the location, or the cast)
        /// has to reach the outfits by itself. Debounced, because the story box is typed into.
        /// </summary>
        private void ScheduleWardrobeDerive()
        {
            if (_wardrobeIsManual) return;

            _wardrobeCts?.Cancel();
            _wardrobeCts = null;

            if (!HasStoryText && !HasEnvironment) return;

            var cts = new CancellationTokenSource();
            _wardrobeCts = cts;
            _ = AutoDeriveWardrobeAsync(cts);
        }

        private async Task AutoDeriveWardrobeAsync(CancellationTokenSource cts)
        {
            var token = cts.Token;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2.5), token);

                // Silent rather than noisy: this fires 2.5 s after every keystroke in the story box, and
                // "you have not cast anybody yet" is not news while somebody is still typing the story. The
                // 🎽 Derive button and Analyze both say it out loud.
                if (WardrobeCast.Count == 0) return;

                var dress = CharactersNeedingWardrobe();
                if (dress.Count == 0) return;

                if (IsAnalyzing || _isDerivingWardrobe)
                {
                    // A pass that arrives while another is running reschedules itself rather than returning:
                    // a dropped debounce used to mean a character was simply never dressed.
                    ScheduleWardrobeDerive();
                    return;
                }

                _isDerivingWardrobe = true;
                IsAnalyzing = true;
                try
                {
                    var model = await ResolveLlmModelAsync(token, quiet: true);
                    if (model == null) return;

                    AddLog(dress.Count < WardrobeCast.Count
                        ? $"Writing an outfit for character {string.Join(", ", dress.Select(c => c.Index))}..."
                        : HasCastWardrobe
                            ? "Story changed — rewriting the cast's wardrobe from it..."
                            : "Deriving the cast's wardrobe from the story...");
                    var derived = await DeriveWardrobeAsync(model, dress, token);
                    token.ThrowIfCancellationRequested();

                    if (string.IsNullOrWhiteSpace(derived))
                    {
                        AddLog("The wardrobe could not be written from this material — press 🎽 Derive to retry, " +
                               "or unlock the box and write it yourself.");
                        return;
                    }
                    SetDerivedWardrobe(derived, dress);
                }
                finally
                {
                    _isDerivingWardrobe = false;
                    IsAnalyzing = false;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AddLog($"Automatic wardrobe pass failed: {ex.Message}");
            }
            finally
            {
                if (ReferenceEquals(_wardrobeCts, cts)) _wardrobeCts = null;
                cts.Dispose();
            }
        }

        /// <summary>
        /// Makes sure the wardrobe covers the whole cast before something that depends on it runs — the sheet
        /// builder, which photographs the cast in it, and Analyze, which quotes it.
        /// </summary>
        private async Task<bool> EnsureWardrobeAsync(CancellationToken token, string? llmModel = null)
        {
            if (WardrobeCast.Count == 0)
            {
                AddLog(NoCastToDress);
                return HasCastWardrobe;
            }

            var dress = CharactersNeedingWardrobe();
            if (dress.Count == 0)
            {
                if (HasCastWardrobe) AddLog("Wardrobe: using the outfits already in the wardrobe box.");
                return HasCastWardrobe;
            }
            if (!HasStoryText && !HasEnvironment) return HasCastWardrobe;

            _wardrobeCts?.Cancel();

            var model = llmModel ?? await ResolveLlmModelAsync(token);
            if (model == null) return HasCastWardrobe;

            AddLog(dress.Count < WardrobeCast.Count
                ? $"Wardrobe: character {string.Join(", ", dress.Select(c => c.Index))} has no outfit yet — " +
                  "writing one, leaving the rest of the cast dressed as they are..."
                : "Wardrobe: deciding the cast's outfits once, so every clip — and every character sheet — " +
                  "can be dressed identically...");
            var derived = await DeriveWardrobeAsync(model, dress, token);
            if (string.IsNullOrWhiteSpace(derived)) return HasCastWardrobe;

            SetDerivedWardrobe(derived, dress);
            return true;
        }

        /// <summary>
        /// Who the wardrobe pass writes for: every slot the user has said something about — a photo, a part,
        /// or a kind that is not a person. <b>Never a guess.</b>
        ///
        /// <para>The two-hander tabs dress both their slots unconditionally, because there the answer is
        /// always "a man and a woman" and the panel is worked top-down: the story is typed before anybody
        /// browses for a photo. This tab inherited that as a fallback and it was wrong twice over. Sized by
        /// <i>loaded photos</i>, a story typed into an empty tab had no cast, so the fallback fired; and the
        /// fallback's cast was slots 1 and 2 at their default kinds, so a story about a cloud, a mountain and
        /// a herd of goats came back with outfits for a man and a woman — and those two bogus lines then
        /// survived into the wardrobe lock of every clip, because a later top-up only fills in what is
        /// missing.</para>
        ///
        /// <para>Filling in a card's Kind or Part is the explicit act of saying "this character exists", and
        /// it is exactly what somebody does after pasting a story. So that is what counts, and with none of
        /// it done the honest answer is no cast at all — see <see cref="NoCastToDress"/>.</para>
        /// </summary>
        private IReadOnlyList<CharacterSlot> WardrobeCast => CastToDress;

        /// <summary>The line said instead of deriving a wardrobe for people who are not in the film.</summary>
        private const string NoCastToDress =
            "Wardrobe: nothing to dress yet. Nothing here can guess who — or what — your story's characters " +
            "are, and guessing wrong writes outfits into every clip. On a cast card, set Kind (a person? a " +
            "creature? a character that is not a person at all?) and write the Part (\"Nimbus, a fluffy " +
            "little cloud\"), or load that character's photo. Then press 🎽 Derive.";

        private static IReadOnlyList<CastPromptStamp.CastRole> Roles(IEnumerable<CharacterSlot> cast) =>
            cast.Select(c => new CastPromptStamp.CastRole(
                c.Index, c.Noun, c.IsPerson ? null : c.Descriptor)).ToList();

        private IReadOnlyList<CharacterSlot> CharactersNeedingWardrobe()
        {
            if (!HasCastWardrobe || StorySourceStamp() != _wardrobeStoryStamp) return WardrobeCast;

            var wroteFor = ParseCastStamp(_wardrobeCastStamp);
            return WardrobeCast.Where(c =>
                CastPromptStamp.OutfitFor(CastWardrobe, c.Index).Length == 0 ||
                !wroteFor.TryGetValue(c.Index, out var was) ||
                !string.Equals(was, $"{c.Noun}|{c.Role}", StringComparison.OrdinalIgnoreCase)).ToList();
        }

        private async Task<string> DeriveWardrobeAsync(
            string model, IReadOnlyList<CharacterSlot> dress, CancellationToken token)
        {
            if (dress.Count == 0) return string.Empty;

            // A cast that is not all human needs a different brief: a costume supervisor asked to dress a
            // cloud writes a person wearing cloud-coloured clothes, which is exactly the failure.
            var anyNonPerson = dress.Any(c => !c.IsPerson);
            var systemPrompt = anyNonPerson
                ? "You are a costume and character designer writing the look bible for a short animated " +
                  "film. Some of the characters are people; others are not people at all — a cloud, a " +
                  "mountain, an animal, a group. You describe each one as what it IS, and you never turn a " +
                  "non-human character into a person or into a person wearing a costume. You reply with " +
                  "nothing but the lines you were asked for — no preamble, no headings, no markdown, no " +
                  "notes, no explanation."
                : "You are a costume supervisor writing the wardrobe bible for a short film. You reply with " +
                  "nothing but the wardrobe lines you were asked for — no preamble, no headings, no " +
                  "markdown, no notes, no explanation.";

            var shape = string.Join("\n", dress.Select(c =>
                $"CHARACTER {c.Index} ({c.Description}): <{(c.IsPerson ? "outfit" : "appearance")}>"));
            var who = dress.Count == 1
                ? $"Character {dress[0].Index}, who is {dress[0].Descriptor}"
                : "these characters — " + string.Join(", ", dress.Select(c => $"Character {c.Index} is {c.Descriptor}"));

            var settled = WardrobeCast
                .Where(c => dress.All(d => d.Index != c.Index))
                .Select(c => (c, Outfit: CastPromptStamp.OutfitFor(CastWardrobe, c.Index)))
                .Where(x => x.Outfit.Length > 0)
                .Select(x => $"Character {x.c.Index} ({x.c.Description}) is already settled and must not be " +
                             $"changed: {x.Outfit}")
                .ToList();
            var settledBlock = settled.Count == 0
                ? string.Empty
                : string.Join("\n", settled) + "\nDress the character(s) below to belong in the same production " +
                  "as that, without copying it and without writing a line for anyone already dressed.\n";

            var ensemble = dress.Count >= 2
                ? "They are on screen together, so make them tell apart at a glance: no two of them may share " +
                  "a silhouette or a dominant colour, and each should read as its own part in the story. "
                : string.Empty;

            var personRule =
                "For a character who IS a person, <outfit> is ONE sentence of at most 45 words naming every " +
                "visible garment and worn item — top, bottom or dress, outer layer, footwear, headwear, " +
                "gloves, eyewear, jewellery, bag, belt — each with its colour and its material. Write only " +
                "clothing and worn accessories: no face, hair, skin, build, age, name, pose, expression, " +
                "background, weather or action. ";

            // The non-person half. "Outfit" is still the right frame — the story may well give a cloud a
            // raincoat — but the sentence has to be allowed to describe the thing itself, because for a
            // mountain the wardrobe IS the mountain.
            var thingRule = anyNonPerson
                ? "For a character who is NOT a person, <appearance> is ONE sentence of at most 45 words " +
                  "describing what that character looks like: its form and silhouette, its colours, its " +
                  "materials and surface, and anything the story explicitly gives it to wear or carry. " +
                  "Describe it as the thing it is — a cloud is made of vapour and light, a mountain of rock " +
                  "and snow. Never describe it as a person, never give it a human face, human hair, human " +
                  "hands or ordinary human clothing, and never dress a person up as it. No pose, no " +
                  "expression, no background, no weather, no action. "
                : string.Empty;

            var rules =
                settledBlock +
                $"Decide the look of {who} in this video. " +
                "Reply with exactly these lines and nothing else:\n" + shape + "\n\n" +
                personRule + thingRule + ensemble +
                "Whatever you write must be practical for everything the character does in the story and must " +
                "hold from beginning to end, because it is what they look like in every shot of the finished " +
                "video. Write a line for every character listed above even if the story features fewer than " +
                "that — an unused line costs nothing, whereas a missing one leaves that character undescribed.";

            string userMessage;
            if (HasEnvironment)
            {
                var story = HasStoryText
                    ? $"The story they act out:\n{StoryText.Trim()}\n"
                    : string.Empty;
                userMessage =
                    "Image role: REFERENCE ONLY — this is the LOCATION the video is set in, and it is where " +
                    "the wardrobe comes from. Read the clothing off the people in it. If it shows no people, " +
                    "dress the cast in what the setting, period and situation plainly call for.\n" +
                    story + rules;
            }
            else
            {
                userMessage =
                    "There is no reference image. The story below is the only source: take the wardrobe from it " +
                    "where it describes clothing, and where it does not, dress the cast in what the period, " +
                    "place and situation plainly call for.\n" +
                    $"The story:\n{StoryText.Trim()}\n" +
                    rules;
            }

            var maxTokens = Math.Min(1600, 300 + 220 * dress.Count);
            var result = HasEnvironment
                ? await _lmStudioService.AnalyzeImageWithSystemPromptAsync(
                    model, EnvironmentPath, userMessage, systemPrompt, maxTokens: maxTokens, cancellationToken: token)
                : await _lmStudioService.SendTextChatAsync(
                    model, systemPrompt, userMessage, maxTokens: maxTokens, cancellationToken: token);

            return CastPromptStamp.NormalizeWardrobe(CleanOutput(result), Roles(_cast), Roles(dress));
        }

        #endregion
    }
}
