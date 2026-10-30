using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FlipPix.UI.Services
{
    /// <summary>
    /// The H3 Video Editor's <b>✍ H3 rewriter</b> prompt writer for Story mode: each story beat is written into a
    /// full-reference H3 prompt by a language model, instead of the code template
    /// (<c>H3VideoEditorViewModel.BuildClipPrompt</c>).
    ///
    /// <para><b>Who writes what.</b> The same split as <see cref="H3SpecPrompt"/>: the model writes the four
    /// prose sections — <c>summary:</c>, <c>detailed_description:</c>, <c>overall_soundscape:</c>,
    /// <c>non_diegetic_music:</c> — to <c>prompts/h3-rewriter/flippix_beat_writer.md</c>, the official MiniMax H3
    /// prompt guides' rules. <c>subject_definitions:</c> and <c>retention_analysis:</c> are written here, from the
    /// cast, the wardrobe and the location plate, because they are fully determined by what the clip uploads and a
    /// model writing clips one at a time would renumber them.</para>
    ///
    /// <para><b>The model.</b> Any OpenAI-compatible server works. The intended one is llama.cpp's
    /// <c>llama-server</c> running Qwen3.6-27B with the MiniMax-H3 prompt-rewriter LoRA
    /// (<c>solphor/MiniMax-H3-Prompt-Rewriter-LoRA-Q8-GGUF</c>), which the editor switches on only for this pass
    /// (<see cref="LMStudioService.SetLoraScalesAsync"/>). That LoRA was trained on the three-field base layout,
    /// so a reply in <c>integrated_multimodal_description:</c> is accepted and read as the description, and a
    /// missing <c>summary:</c> is written from the beat.</para>
    ///
    /// <para>WPF-free, so the tests can reach all of it.</para>
    /// </summary>
    public static class H3RewriterBeatWriter
    {
        /// <summary>The system prompt, relative to the app's <c>prompts</c> folder.</summary>
        public const string SystemPromptFile = "h3-rewriter/flippix_beat_writer.md";

        /// <summary>The values of the editor's prompt-writer switch.</summary>
        public const string TemplateWriter = "template";
        public const string RewriterWriter = "h3-rewriter";

        /// <summary>H3's ceiling on reference pictures in one clip.</summary>
        public const int MaxPictures = 9;

        /// <summary>The style sentence written ahead of <c>[Shot 1]</c> when the model wrote none.</summary>
        public const string DefaultStyle = "The target video is live-action and cinematic, with natural light.";

        /// <summary>One character as the request describes them.</summary>
        /// <param name="Voice">How their voice is described every time they speak, or empty.</param>
        public sealed record CastVoice(int Index, string Noun, string Outfit, string Voice);

        /// <summary>Everything one clip's request needs.</summary>
        /// <param name="PlateSubject">The location plate's subject number, or 0 when the clip has no plate.</param>
        /// <param name="ContinuousTake">The clip continues the previous take through Motion Context, so it must
        /// be one shot: a cut inside a chained clip lets H3 wander and snap back to the pinned frames.</param>
        public sealed record BeatContext(
            int ClipIndex,
            int ClipCount,
            double Seconds,
            string Aspect,
            IReadOnlyList<CastVoice> Cast,
            int PlateSubject,
            string SceneSentence,
            string ContinuityBlock,
            string Setting,
            string Beat,
            string? PreviousBeat,
            string? NextBeat,
            bool ContinuousTake);

        // ── The request ─────────────────────────────────────────────────────────────────────────────

        /// <summary>One clip's user message. <paramref name="rejection"/> is the previous attempt's complaint,
        /// or empty on the first attempt.</summary>
        public static string BuildRequest(BeatContext c, string rejection)
        {
            var s = c.Seconds.ToString("0.##", CultureInfo.InvariantCulture);
            var sb = new StringBuilder();

            sb.AppendLine($"CLIP {c.ClipIndex + 1} OF {c.ClipCount} — {s} seconds" +
                          (string.IsNullOrWhiteSpace(c.Aspect) ? "." : $", aspect {c.Aspect}."));
            sb.AppendLine(c.ContinuousTake
                ? "ONE CONTINUOUS TAKE: write only [Shot 1], with no cuts. The first second is the previous " +
                  "clip's real last moment, so open on exactly how that clip ended and carry it on."
                : $"Cuts are allowed, at most one per 3 seconds; every cut time must be under {s} seconds.");
            sb.AppendLine();

            sb.AppendLine("TAGS — these are the only tags you may write:");
            foreach (var m in c.Cast)
            {
                var line = new StringBuilder($"- <Subject {m.Index}> is Character {m.Index}, {Article(m.Noun)}");
                if (!string.IsNullOrWhiteSpace(m.Outfit))
                    line.Append(", wearing ").Append(m.Outfit.Trim().TrimEnd('.'));
                line.Append('.');
                if (!string.IsNullOrWhiteSpace(m.Voice))
                    line.Append(" VOICE: ").Append(m.Voice.Trim().TrimEnd('.')).Append('.');
                sb.AppendLine(line.ToString());
            }
            if (c.PlateSubject > 0)
                sb.AppendLine($"- <Subject {c.PlateSubject}> is the location plate: " +
                              (c.SceneSentence.Length > 0 ? c.SceneSentence.TrimEnd('.') + ". " : string.Empty) +
                              "It contains nobody. Stage the whole take inside it.");
            sb.AppendLine();

            if (c.Setting.Length > 0)
                sb.AppendLine("STORY WORLD — the same in every clip:").AppendLine(c.Setting.Trim()).AppendLine();
            if (c.ContinuityBlock.Length > 0)
                sb.AppendLine(c.ContinuityBlock.Trim()).AppendLine();

            sb.AppendLine(string.IsNullOrWhiteSpace(c.PreviousBeat)
                ? "This is the FIRST clip: it opens the video, already in motion."
                : "THE CLIP BEFORE THIS ONE already showed this — do NOT show it again:\n" + c.PreviousBeat!.Trim());
            sb.AppendLine();
            sb.AppendLine("THIS CLIP'S BEAT — show exactly this, in order, filling the whole duration:");
            sb.AppendLine(c.Beat.Trim());
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(c.NextBeat))
            {
                sb.AppendLine("THE NEXT CLIP will show this — do NOT show it; end mid-motion, heading into it:");
                sb.AppendLine(c.NextBeat!.Trim());
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("This is the LAST clip: in its final seconds the motion settles as the action concludes.");
                sb.AppendLine();
            }

            sb.AppendLine(H3SpecPrompt.BeatHasLines(c.Beat)
                ? "DIALOGUE — the beat's quoted lines are spoken in this clip, word for word, inside <d>…</d>, " +
                  "each by its speaker's tag and speaker ID."
                : "DIALOGUE — the beat has no quoted lines: nobody speaks.");
            sb.AppendLine("SCORE — there is no score: non_diegetic_music is N/A.");
            sb.AppendLine();
            sb.Append("Write the four sections now: summary:, detailed_description:, overall_soundscape:, non_diegetic_music:.");

            if (!string.IsNullOrWhiteSpace(rejection))
                sb.Append("\n\nYOUR PREVIOUS ATTEMPT WAS REJECTED. Fix exactly this and write the whole clip again:\n")
                  .Append(rejection.Trim());

            return sb.ToString();
        }

        // ── Reading the reply ───────────────────────────────────────────────────────────────────────

        private static readonly Regex ThinkRegex = new(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex FenceRegex = new(@"^\s*```[a-zA-Z]*\s*$", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex BaseLabelRegex =
            new(@"integrated[ _\-]multimodal[ _\-]description[ \t]*:", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>The reply as the four canonical writer sections. Reasoning, fences and bold are dropped; the
        /// base layout's <c>integrated_multimodal_description:</c> is read as <c>detailed_description:</c>; any
        /// subject_definitions or retention_analysis the model wrote anyway are thrown away.</summary>
        public static string Normalize(string? raw)
        {
            var text = raw ?? string.Empty;
            text = ThinkRegex.Replace(text, string.Empty);
            var close = text.IndexOf("</think>", StringComparison.Ordinal);
            if (close >= 0) text = text[(close + "</think>".Length)..];
            text = FenceRegex.Replace(text, string.Empty).Replace("**", string.Empty);
            text = BaseLabelRegex.Replace(text, H3SpecPrompt.DetailedDescription);
            return H3SpecPrompt.RenderWriterSections(text).Trim();
        }

        // ── Checking it ─────────────────────────────────────────────────────────────────────────────

        private static readonly Regex ShotRegex = new(@"\[Shot (\d+)\]", RegexOptions.Compiled);
        private static readonly Regex CutTimeRegex = new(@"^\s*At (\d{2}):(\d{2})\.(\d{3}),", RegexOptions.Compiled);
        private static readonly Regex RangeRegex = new(
            @"\[\s*\d+(\.\d+)?\s*s?\s*[-–]\s*\d+(\.\d+)?\s*s?\s*\]|\(\s*\d+(\.\d+)?\s*s\s*[-–]\s*\d+(\.\d+)?\s*s\s*\)",
            RegexOptions.Compiled);
        private static readonly Regex BracketCameraRegex = new(
            @"\[(Pan|Truck|Push|Pull|Tilt|Zoom|Pedestal|Tracking|Static|Shake)\b[^\]]*\]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SubjectTagRegex = new(@"<Subject (\d+)>", RegexOptions.Compiled);
        private static readonly Regex DialogueRegex = new(@"<d>(.*?)</d>", RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex LanguageTagRegex = new(@"^\s*\[[^\]\n]{2,30}\]\s+\S", RegexOptions.Compiled);
        private static readonly Regex SpeakerIdRegex = new(@"\(S\d+(?:,\s*S\d+)*\)", RegexOptions.Compiled);

        /// <summary>
        /// Why a written clip cannot be rendered as it is, or null when it can — the official format's hard rules,
        /// handed back to the model as the rejection reason. Several problems come back together, one per line.
        /// </summary>
        public static string? Validate(string? body, BeatContext c)
        {
            var s = H3SpecPrompt.Parse(body);
            var dd = s[H3SpecPrompt.DetailedDescription];
            if (dd.Length == 0) return "There is no detailed_description: section.";

            var problems = new List<string>();

            var shots = ShotRegex.Matches(dd);
            if (shots.Count == 0)
            {
                problems.Add("detailed_description has no [Shot 1] marker.");
            }
            else
            {
                var numbers = shots.Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
                if (!numbers.SequenceEqual(Enumerable.Range(1, numbers.Count)))
                    problems.Add($"Shot numbers must run [Shot 1], [Shot 2], … in order; got {string.Join(", ", numbers)}.");
                if (c.ContinuousTake && shots.Count > 1)
                    problems.Add("This clip is ONE CONTINUOUS TAKE: write only [Shot 1], with no cuts.");

                var previous = 0.0;
                foreach (Match m in shots)
                {
                    var n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    var after = dd[(m.Index + m.Length)..];
                    var cut = CutTimeRegex.Match(after);
                    if (n == 1)
                    {
                        if (Regex.IsMatch(after, @"^\s*At \d")) problems.Add("[Shot 1] must not have a timestamp.");
                        continue;
                    }
                    if (!cut.Success)
                    {
                        problems.Add($"[Shot {n}] must open with a single cut time, 'At MM:SS.mmm,' (e.g. 'At 00:04.500,').");
                        continue;
                    }
                    var t = int.Parse(cut.Groups[1].Value, CultureInfo.InvariantCulture) * 60
                            + int.Parse(cut.Groups[2].Value, CultureInfo.InvariantCulture)
                            + int.Parse(cut.Groups[3].Value, CultureInfo.InvariantCulture) / 1000.0;
                    if (t <= previous)
                        problems.Add($"[Shot {n}]'s cut time {t:0.000}s is not after the previous cut.");
                    if (t >= c.Seconds)
                        problems.Add($"[Shot {n}]'s cut time {t:0.000}s is outside the {c.Seconds:0.##}s clip.");
                    previous = t;
                }
            }

            if (RangeRegex.IsMatch(dd))
                problems.Add("Time ranges like [0-3s] or (0s-4s) are not H3 syntax; mark cuts with [Shot N] At MM:SS.mmm.");
            if (BracketCameraRegex.IsMatch(dd))
                problems.Add("Bracketed camera commands ([Pan left] and the like) are not H3 syntax; write the camera move as a sentence.");
            if (dd.Contains("<Picture", StringComparison.Ordinal))
                problems.Add("Never write <Picture N> in detailed_description; name characters only by their <Subject N> tags.");

            var allowed = new HashSet<int>(c.Cast.Select(m => m.Index));
            if (c.PlateSubject > 0) allowed.Add(c.PlateSubject);
            var unknown = SubjectTagRegex.Matches(dd)
                .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                .Where(n => !allowed.Contains(n)).Distinct().ToList();
            if (unknown.Count > 0)
                problems.Add($"Unknown tag(s) {string.Join(", ", unknown.Select(n => $"<Subject {n}>"))}; use only " +
                             string.Join(", ", allowed.OrderBy(n => n).Select(n => $"<Subject {n}>")) + ".");

            var opens = Regex.Matches(dd, "<d>").Count;
            var closes = Regex.Matches(dd, "</d>").Count;
            if (opens != closes) problems.Add($"Unbalanced dialogue tags: {opens} <d> but {closes} </d>.");
            foreach (Match m in DialogueRegex.Matches(dd))
            {
                var inner = m.Groups[1].Value.Trim();
                var head = inner.Length > 30 ? inner[..30] + "…" : inner;
                if (!LanguageTagRegex.IsMatch(inner))
                    problems.Add($"Dialogue must open with a language tag, e.g. <d>[English] …</d>: <d>{head}");
                var before = dd[Math.Max(0, m.Index - 240)..m.Index];
                if (!SpeakerIdRegex.IsMatch(before))
                    problems.Add($"No speaker ID (S1)/(S2) before the line <d>{head}");
            }
            if (H3SpecPrompt.BeatHasLines(c.Beat) && opens == 0)
                problems.Add("The beat has quoted lines: speak them, word for word, inside <d>[Language] …</d>.");

            if (s[H3SpecPrompt.OverallSoundscape].Contains("<d>", StringComparison.Ordinal))
                problems.Add("overall_soundscape must not contain dialogue; keep <d> lines in detailed_description.");

            return problems.Count == 0 ? null : string.Join("\n", problems.Distinct());
        }

        // ── The finished clip ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The six sections H3 reads: the code-written definitions and retention around the model's prose. The
        /// summary gets its <c>[reference generation]</c> marker (or is written from the beat when the model left
        /// it out), the description gets a style sentence when it opens straight on <c>[Shot 1]</c>, the scene is
        /// stamped word for word ahead of <c>[Shot 1]</c>, and the score is <c>N/A</c>.
        /// </summary>
        public static string AssembleClip(
            string writerBody, string subjectDefinitions, string retentionAnalysis,
            string sceneSentence, string beatForSummary)
        {
            var s = H3SpecPrompt.Parse(writerBody);

            var summary = s[H3SpecPrompt.Summary];
            if (summary.Length == 0) summary = beatForSummary.Trim();

            var description = s[H3SpecPrompt.DetailedDescription];
            var shot = description.IndexOf("[Shot", StringComparison.OrdinalIgnoreCase);
            if (shot == 0) description = DefaultStyle + "\n" + description;
            description = StoryContinuity.StampSceneText(description, sceneSentence);

            return H3SpecPrompt.Assemble(
                subjectDefinitions,
                H3SpecPrompt.WithSummaryMarker(summary),
                retentionAnalysis,
                description,
                s[H3SpecPrompt.OverallSoundscape],
                "N/A");
        }

        /// <summary>
        /// <c>subject_definitions:</c> — the cast lines exactly as the template build writes them, then the
        /// location plate as a subject of its own when the clip has one (the official guide's form for an image
        /// that supplies an environment), or the plain <c>&lt;Environment&gt;</c> line when it has none.
        /// </summary>
        public static string BuildSubjectDefinitions(
            IReadOnlyList<H3SpecPrompt.CastMember> cast, int plateSubject, int platePicture,
            StoryContinuity.Environment environment, string? setting)
        {
            var lines = H3SpecPrompt.BuildSubjectDefinitions(cast, StoryContinuity.Environment.None, null)
                .Split('\n')
                .Where(l => !l.StartsWith("<Screen map>", StringComparison.Ordinal))
                .ToList();

            var place = StoryContinuity.SceneSentence(environment);
            if (place.Length == 0 && !string.IsNullOrWhiteSpace(setting)) place = setting.Trim();

            if (plateSubject > 0)
                lines.Add($"<Subject {plateSubject}> is the location in <Picture {platePicture}>" +
                          (place.Length > 0 ? $" — {place.TrimEnd('.')}." : ".") +
                          " The whole take is staged inside it; its architecture, landmarks and their sides of the " +
                          "frame, materials and light direction stay as the picture shows them. It contains nobody.");
            else if (place.Length > 0)
                lines.Add($"<Environment>: {place}");

            if (cast.Count > 1)
                lines.Add("<Screen map>: fixed for the whole film — when both are in frame, <Subject 1> keeps to " +
                          "screen-left and <Subject 2> to screen-right, and the camera stays on one side of the " +
                          "line between them, so the place keeps its layout from shot to shot and clip to clip.");

            return string.Join("\n", lines);
        }

        /// <summary><c>retention_analysis:</c> — the template build's cast lines, with the plate's own line in
        /// place of the "newly generated environment" line when the clip has one.</summary>
        public static string BuildRetentionAnalysis(
            IReadOnlyList<H3SpecPrompt.CastMember> cast, int plateSubject, int platePicture)
        {
            var lines = H3SpecPrompt.BuildRetentionAnalysis(cast).Split('\n').ToList();
            if (plateSubject <= 0) return string.Join("\n", lines);

            var plateLine =
                $"<Picture {platePicture}> → <Subject {plateSubject}>: partially_preserved, throughout the clip — the " +
                "place's layout, architecture, landmarks, materials and light direction are kept; the camera moves " +
                "freely inside it, and the picture is never a frame of the video.";
            var env = lines.FindIndex(l => l.StartsWith("<Environment>", StringComparison.Ordinal));
            if (env >= 0)
            {
                lines[env] = "Composition, camera, action, physical effects and lighting changes: newly_generated " +
                             "from detailed_description.";
                lines.Insert(env, plateLine);
            }
            else
            {
                lines.Add(plateLine);
            }
            return string.Join("\n", lines);
        }

        private static string Article(string noun)
        {
            var n = (noun ?? string.Empty).Trim();
            if (n.Length == 0) return "a person";
            return "aeiouAEIOU".IndexOf(n[0]) >= 0 ? $"an {n}" : $"a {n}";
        }
    }
}
