using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// Story mode's three additions on top of analyze → populate → generate:
    /// <list type="number">
    /// <item><b>👤 Looks and voices.</b> One LLM pass decides each character's face, hair, age and build, and the
    /// voice they speak in. The look goes into the auto-generated cast photo next to the locked outfit, so the
    /// reference matches the story; the voice goes into every clip that character speaks in, so it stays the
    /// same voice from clip to clip.</item>
    /// <item><b>🏞️ Location plates.</b> One empty picture per place in the continuity plan, written by the LLM
    /// (<c>prompts/prompt2json/h3-location-plate.md</c>, the MiniMax I2V tab's plate brief) and rendered with Qwen
    /// Image. Each clip sends its place's plate after the cast pictures, declared as a subject of its own, so the
    /// clips in one place are staged in the same set instead of one H3 re-imagines every five seconds.</item>
    /// <item><b>✍ H3 rewriter.</b> Each beat is written by the LLM to the official MiniMax H3 prompt format
    /// (<see cref="H3RewriterBeatWriter"/>), checked, and sent back once with the reasons when it breaks the
    /// format. On a llama-server with the MiniMax-H3 prompt-rewriter LoRA loaded, the LoRA is switched on only
    /// for this pass and off for the analysis around it.</item>
    /// </list>
    /// Each one falls back to what Story mode did before when it cannot run, and says so in the log.
    /// </summary>
    public partial class H3VideoEditorViewModel
    {
        private const string LocationPlatePromptFile = "h3-location-plate.md";

        /// <summary>A little over the clip's finish, so the plate is never the weakest picture in the set.</summary>
        private const double LocationPlateMegapixels = 1.6;

        /// <summary>More places than this share the last plate's slot budget badly; later places go without.</summary>
        private const int MaxLocationPlates = 6;

        /// <summary>"h3-rewriter" or "template" — who writes each clip's prompt.</summary>
        [ObservableProperty]
        private string _storyPromptWriter = H3RewriterBeatWriter.RewriterWriter;

        /// <summary>Whether Story mode renders a location plate per place.</summary>
        [ObservableProperty]
        private bool _storyLocationPlates = true;

        /// <summary>The choices for the prompt-writer box.</summary>
        public IReadOnlyList<string> StoryPromptWriters { get; } =
            new[] { H3RewriterBeatWriter.RewriterWriter, H3RewriterBeatWriter.TemplateWriter };

        private bool UseRewriter => StoryPromptWriter == H3RewriterBeatWriter.RewriterWriter;

        // Character number (1-based) → (look, voice), from the looks pass. Empty when it could not be derived.
        private Dictionary<int, (string Look, string Voice)> _storyLooks = new();

        /// <summary>One place in the story, its plate prompt and, once rendered, its picture.</summary>
        private sealed class PlateGroup
        {
            public StoryContinuity.Environment Env;
            public string Prompt = string.Empty;
            public string? Path;
        }

        private List<PlateGroup> _plateGroups = new();

        // Beat index → index into _plateGroups, or -1 for a beat that has no plate.
        private List<int> _beatPlateGroup = new();

        partial void OnStoryPromptWriterChanged(string value) => SaveSettings();
        partial void OnStoryLocationPlatesChanged(bool value) => SaveSettings();

        /// <summary>Drops this story's looks and plates; called wherever the story's analysis is dropped.</summary>
        private void ClearStoryExtras()
        {
            _storyLooks = new Dictionary<int, (string Look, string Voice)>();
            _plateGroups = new List<PlateGroup>();
            _beatPlateGroup = new List<int>();
        }

        // ── The model ─────────────────────────────────────────────────────────────────────────────────

        /// <summary>The LLM the story passes use: the server's first model, as analysis picks it. Null (logged)
        /// when the server has none.</summary>
        private async Task<string?> ResolveStoryModelAsync(CancellationToken token)
        {
            var models = await _lmStudioService.GetAvailableModelsAsync(token);
            if (models == null || models.Count == 0)
            {
                AddLog("ERROR: No LLM models available. Please ensure the LLM server is running.");
                return null;
            }
            return models.First().Name.Length > 0 ? models.First().Name : models.First().Id;
        }

        private static async Task<string> ReadPromptFileAsync(string relativePath, CancellationToken token)
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prompts",
                                    relativePath.Replace('/', Path.DirectorySeparatorChar));
            return await File.ReadAllTextAsync(path, token);
        }

        // ── llama-server LoRA ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Sets every LoRA on a llama-server to <paramref name="on"/> (scale 1, or the scale it was loaded at) or
        /// off, and returns the scales to put back afterwards — null when the server is not a llama-server with
        /// adapters, which leaves everything as it was.
        /// </summary>
        private async Task<List<(int Id, double Scale)>?> SwitchLoraAsync(bool on, CancellationToken token)
        {
            var original = await _lmStudioService.GetLoraScalesAsync(token);
            if (original == null) return null;

            var target = original.Select(a => (a.Id, on ? (a.Scale > 0 ? a.Scale : 1.0) : 0.0)).ToList();
            if (target.SequenceEqual(original)) return original;

            if (await _lmStudioService.SetLoraScalesAsync(target, token))
                AddLog(on ? "llama-server: H3 rewriter LoRA on for clip writing."
                          : "llama-server: LoRA off for story analysis.");
            else
                AddLog("⚠ llama-server did not accept the LoRA switch — carrying on with it as it was.");
            return original;
        }

        private async Task RestoreLoraAsync(List<(int Id, double Scale)>? original)
        {
            if (original == null) return;
            try { await _lmStudioService.SetLoraScalesAsync(original, CancellationToken.None); }
            catch { /* best effort: the next pass sets it again */ }
        }

        // ── 👤 Looks and voices ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// One line per character: what they look like and how they sound. Empty on failure — never throws, since
        /// a story with no looks still renders, as it did before this pass existed.
        /// </summary>
        private async Task DeriveStoryLooksAsync(string model, CancellationToken token)
        {
            _storyLooks = new Dictionary<int, (string Look, string Voice)>();
            if (_detectedCast.Count == 0) return;

            const string systemPrompt =
                "You are a casting director writing the look and voice bible for a short film. You reply with " +
                "nothing but the lines you were asked for — no preamble, no headings, no markdown, no notes.";

            var cast = _detectedCast.Take(4).ToList();
            var who = string.Join("; ", cast.Select((c, i) => $"Character {i + 1} is {c.Role}"));
            var shape = string.Join("\n", cast.Select((c, i) => $"CHARACTER {i + 1} ({c.Kind}): <look> | VOICE: <voice>"));

            var userMessage =
                "The story below is the only source: take each character's appearance from it where it describes " +
                "them, and where it does not, decide one that suits their age, origin and role.\n" +
                $"The cast: {who}.\n" +
                $"The story:\n{StoryText.Trim()}\n\n" +
                "Reply with exactly these lines and nothing else:\n" + shape + "\n\n" +
                "<look> is ONE sentence of at most 35 words: apparent age, build, skin tone, face shape and features, " +
                "eye colour, hair colour, length and style, facial hair, and one distinguishing mark if any. No " +
                "clothing, pose, expression, action or background. " +
                "<voice> is at most 15 words: pitch, timbre, pace and accent, e.g. 'a low, gravelly baritone, slow, " +
                "with a soft Scottish accent'.";

            try
            {
                var reply = await _lmStudioService.SendTextChatAsync(
                    model, systemPrompt, userMessage, maxTokens: 600, cancellationToken: token);
                var text = (reply ?? string.Empty).Replace("**", string.Empty);
                text = Regex.Replace(text, @"<think>.*?</think>", string.Empty, RegexOptions.Singleline);

                foreach (Match m in Regex.Matches(text,
                             @"^\s*CHARACTER\s+(\d)\s*(?:\([^)]*\))?\s*:\s*(.+?)\s*(?:\|\s*VOICE\s*:\s*(.+?))?\s*$",
                             RegexOptions.IgnoreCase | RegexOptions.Multiline))
                {
                    var n = int.Parse(m.Groups[1].Value);
                    if (n < 1 || n > cast.Count || _storyLooks.ContainsKey(n)) continue;
                    _storyLooks[n] = (m.Groups[2].Value.Trim().TrimEnd('.'), m.Groups[3].Value.Trim().TrimEnd('.'));
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AddLog($"Looks pass failed: {ex.Message}");
            }

            if (_storyLooks.Count == 0)
            {
                AddLog("WARNING: Could not derive looks and voices — cast photos use the role only, and voices may vary.");
                return;
            }
            AddLog("Looks and voices (locked for every clip):");
            foreach (var (n, lv) in _storyLooks.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value)))
                AddLog($"  Character {n}: {lv.Look}" + (lv.Voice.Length > 0 ? $" — voice: {lv.Voice}" : string.Empty));
        }

        /// <summary>Character <paramref name="slotIndex"/>'s (0-based) look, or "".</summary>
        private string StoryLookFor(int slotIndex) =>
            _storyLooks.TryGetValue(slotIndex + 1, out var lv) ? lv.Look : string.Empty;

        /// <summary>Character <paramref name="slotIndex"/>'s (0-based) voice, or "".</summary>
        private string StoryVoiceFor(int slotIndex) =>
            _storyLooks.TryGetValue(slotIndex + 1, out var lv) ? lv.Voice : string.Empty;

        // ── 🏞️ Location plates ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Groups the beats by place (same place, hour and light — <see cref="StoryContinuity.Environment.SameAs"/>)
        /// and has the LLM write one plate prompt per place. Needs no GPU, so it runs during analysis. A place
        /// whose prompt fails simply has no plate.
        /// </summary>
        private async Task WriteLocationPlatePromptsAsync(string model, CancellationToken token)
        {
            _plateGroups = new List<PlateGroup>();
            _beatPlateGroup = Enumerable.Repeat(-1, _storyBeats.Count).ToList();
            if (!StoryLocationPlates || _storyBeats.Count == 0) return;

            string system;
            try
            {
                system = await ReadPromptFileAsync($"prompt2json/{LocationPlatePromptFile}", token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AddLog($"Location plates: the plate brief could not be read ({ex.Message}) — no plates.");
                return;
            }

            var overflowLogged = false;
            for (var i = 0; i < _storyBeats.Count; i++)
            {
                var env = i < _continuityPlan.Count ? _continuityPlan[i] : StoryContinuity.Environment.None;
                var group = _plateGroups.FindIndex(g => g.Env.SameAs(env));
                if (group < 0)
                {
                    if (_plateGroups.Count >= MaxLocationPlates)
                    {
                        if (!overflowLogged)
                            AddLog($"Location plates: more than {MaxLocationPlates} places — the rest have no plate.");
                        overflowLogged = true;
                        continue;
                    }
                    _plateGroups.Add(new PlateGroup { Env = env });
                    group = _plateGroups.Count - 1;
                }
                _beatPlateGroup[i] = group;
            }

            for (var g = 0; g < _plateGroups.Count; g++)
            {
                token.ThrowIfCancellationRequested();
                var first = _beatPlateGroup.IndexOf(g);
                var beat = first >= 0 ? _storyBeats[first].Text : string.Empty;
                var place = StoryContinuity.SceneSentence(_plateGroups[g].Env);

                var request = new StringBuilder()
                    .AppendLine("Draft idea for the video:")
                    .AppendLine(Regex.Replace(beat, @"\bCHARACTER\s+\d\b", "a character", RegexOptions.IgnoreCase))
                    .AppendLine()
                    .AppendLine(place.Length > 0 ? $"The place, exactly: {place}" : "The place: choose it from the draft idea.")
                    .AppendLine(StorySetting.Length > 0 ? $"The story world: {StorySetting}" : string.Empty)
                    .Append("There are no character pictures.")
                    .ToString();

                try
                {
                    StoryStatus = $"Writing location plate {g + 1}/{_plateGroups.Count}...";
                    var reply = await _lmStudioService.SendTextChatAsync(model, system, request, maxTokens: 400, cancellationToken: token);
                    var prompt = Regex.Replace(reply ?? string.Empty, @"<think>.*?</think>", string.Empty, RegexOptions.Singleline)
                        .Replace("**", string.Empty).Trim().Trim('"');
                    if (prompt.Length < 20)
                    {
                        AddLog($"Location plate {g + 1}: the writer returned nothing usable — that place has no plate.");
                        continue;
                    }
                    _plateGroups[g].Prompt = prompt;
                    AddLog($"Location plate {g + 1} ({(place.Length > 0 ? place : "story setting")}): {prompt}");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AddLog($"Location plate {g + 1}: prompt failed ({ex.Message}) — that place has no plate.");
                }
            }
        }

        /// <summary>Renders every plate that has a prompt and no picture yet, with Qwen Image at the clip aspect.
        /// Runs at populate time, beside the cast photos.</summary>
        private async Task RenderLocationPlatesAsync(CancellationToken token)
        {
            var todo = _plateGroups.Where(g => g.Prompt.Length > 0 && (g.Path == null || !File.Exists(g.Path))).ToList();
            if (todo.Count == 0) return;

            var (w, h) = H3Canvas.Resolve(SelectedAspectRatio, LocationPlateMegapixels, 16);
            AddLog($"Rendering {todo.Count} location plate(s) at {w}×{h} with Qwen Image...");

            for (var i = 0; i < todo.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var group = todo[i];
                var runToken = $"h3_story_plate_{_plateGroups.IndexOf(group) + 1}_{DateTime.Now:yyyyMMddHHmmssfff}";
                try
                {
                    StoryStatus = $"Rendering location plate {i + 1}/{todo.Count}...";
                    var seed = Random.Shared.NextInt64(0, 1_000_000_000_000_000L);
                    var (json, saveNode) = await CastPhotoWorkflows.BuildLocationPlateAsync(
                        $"h3_story/{runToken}", seed, group.Prompt, w, h);
                    var workflow = JsonSerializer.Deserialize<JsonElement>(json);
                    var promptId = await _comfyUIService.ExecuteWorkflowAsync(
                        workflow, new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(_ => { }), token);

                    string? local = null;
                    var byNode = await _comfyUIService.HttpClient.GetOutputsByNodeAsync(promptId, token);
                    if (byNode.TryGetValue(saveNode, out var outs) && outs.Count > 0)
                        local = await ResolveImageToLocalAsync(outs[0]);
                    local ??= FindTokenImageOnDisk(runToken);

                    if (local == null || !File.Exists(local))
                    {
                        AddLog($"WARNING: location plate {_plateGroups.IndexOf(group) + 1} was not produced — that place has no plate.");
                        continue;
                    }
                    group.Path = local;
                    AddLog($"Location plate {_plateGroups.IndexOf(group) + 1}: {Path.GetFileName(local)}");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AddLog($"ERROR rendering location plate {_plateGroups.IndexOf(group) + 1}: {ex.Message} — that place has no plate.");
                }
            }
        }

        /// <summary>Beat <paramref name="beatIndex"/>'s rendered plate, or null.</summary>
        private string? PlateFor(int beatIndex)
        {
            if (beatIndex < 0 || beatIndex >= _beatPlateGroup.Count) return null;
            var g = _beatPlateGroup[beatIndex];
            if (g < 0 || g >= _plateGroups.Count) return null;
            var path = _plateGroups[g].Path;
            return !string.IsNullOrEmpty(path) && File.Exists(path) ? path : null;
        }

        /// <summary>The <c>&lt;Picture N&gt;</c> a plate gets: the render path sends it right after the filled
        /// Picture slots.</summary>
        private int PlatePictureNumber() =>
            Math.Min(H3RewriterBeatWriter.MaxPictures, ReferenceSlots.Take(4).Count(s => s.HasImages) + 1);

        // ── ✍ The H3 rewriter ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Writes every beat's prompt with the LLM. Returns one prompt per beat; an entry is empty where the model
        /// gave nothing renderable even after a retry, and the caller writes that clip from the template instead.
        /// Null when the pass could not run at all (no model, no system prompt).
        /// </summary>
        private async Task<List<string>?> WriteRewriterPromptsAsync(
            List<H3SpecPrompt.CastMember> cast, double clipDuration, CancellationToken token)
        {
            var model = await ResolveStoryModelAsync(token);
            if (model == null) return null;

            string system;
            try
            {
                system = await ReadPromptFileAsync(H3RewriterBeatWriter.SystemPromptFile, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AddLog($"H3 rewriter: prompts/{H3RewriterBeatWriter.SystemPromptFile} could not be read ({ex.Message}) " +
                       "— writing the clips from the template.");
                return null;
            }

            var voices = cast.Select(m => new H3RewriterBeatWriter.CastVoice(
                m.Index, m.Noun, m.Outfit, StoryVoiceFor(m.Index - 1))).ToList();
            var platePicture = PlatePictureNumber();
            var plateSubject = cast.Count + 1;

            H3RewriterBeatWriter.BeatContext Context(int i)
            {
                var env = i < _continuityPlan.Count ? _continuityPlan[i] : StoryContinuity.Environment.None;
                var prevEnv = i > 0 && i - 1 < _continuityPlan.Count ? _continuityPlan[i - 1] : (StoryContinuity.Environment?)null;
                return new H3RewriterBeatWriter.BeatContext(
                    ClipIndex: i,
                    ClipCount: _storyBeats.Count,
                    Seconds: clipDuration,
                    Aspect: SelectedAspectRatio,
                    Cast: voices,
                    PlateSubject: PlateFor(i) != null ? plateSubject : 0,
                    SceneSentence: StoryContinuity.SceneSentence(env),
                    ContinuityBlock: StoryContinuity.WriterBlock(env, prevEnv),
                    Setting: StorySetting,
                    Beat: TagSubjects(_storyBeats[i].Text, cast.Count),
                    PreviousBeat: i > 0 ? TagSubjects(_storyBeats[i - 1].Text, cast.Count) : null,
                    NextBeat: i + 1 < _storyBeats.Count ? TagSubjects(_storyBeats[i + 1].Text, cast.Count) : null,
                    ContinuousTake: i > 0 && ChainClips);
            }

            var bodies = new string[_storyBeats.Count];
            var lora = await SwitchLoraAsync(on: true, token);
            try
            {
                AddLog($"H3 rewriter: writing {_storyBeats.Count} clip prompt(s) with {model}...");
                await ClipChainWriter.WriteAsync(
                    _lmStudioService, model, system, _storyBeats.Count,
                    buildRequest: (i, reason) => H3RewriterBeatWriter.BuildRequest(Context(i), reason),
                    normalize: H3RewriterBeatWriter.Normalize,
                    validate: (i, body) =>
                    {
                        var env = i < _continuityPlan.Count ? _continuityPlan[i] : StoryContinuity.Environment.None;
                        return H3RewriterBeatWriter.Validate(body, Context(i)) ?? StoryContinuity.Contradiction(body, env);
                    },
                    onProgress: (n, total) => StoryStatus = $"Writing clip prompt {n} of {total}...",
                    log: AddLog,
                    describe: b => $"{b.Length:N0} chars",
                    token: token,
                    onWritten: (i, body) => bodies[i] = body);
            }
            finally
            {
                await RestoreLoraAsync(lora);
            }

            var prompts = new List<string>(_storyBeats.Count);
            for (var i = 0; i < _storyBeats.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(bodies[i]))
                {
                    prompts.Add(string.Empty);
                    continue;
                }
                var env = i < _continuityPlan.Count ? _continuityPlan[i] : StoryContinuity.Environment.None;
                var hasPlate = PlateFor(i) != null;
                var defs = H3RewriterBeatWriter.BuildSubjectDefinitions(
                    cast, hasPlate ? plateSubject : 0, platePicture, env, StorySetting);
                var retention = H3RewriterBeatWriter.BuildRetentionAnalysis(
                    cast, hasPlate ? plateSubject : 0, platePicture);
                prompts.Add(H3RewriterBeatWriter.AssembleClip(
                    bodies[i], defs, retention, StoryContinuity.SceneSentence(env),
                    TagSubjects(_storyBeats[i].Text, cast.Count)));
            }
            return prompts;
        }
    }
}
