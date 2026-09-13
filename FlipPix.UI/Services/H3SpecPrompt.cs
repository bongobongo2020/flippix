using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FlipPix.UI.Services
{
    /// <summary>
    /// ⚡ H3 Express's 📐 <b>Singularity spec</b> prompt build: clip prompts written to
    /// <c>prompts/MiniMax_H3_Singularity_Prompt_Writing_Specification_Enhanced_EN.md</c>. The § numbers below are
    /// that document's.
    ///
    /// <para><b>What it changes.</b> The shipped and researched builds put a clip in three fields —
    /// <c>integrated_multimodal_description:</c> and the two sound fields. The spec is written for full-reference
    /// video, which is what Express renders (cast sheets into the reference-to-video node), and its structure is
    /// six sections (§2): <c>subject_definitions:</c>, <c>summary:</c>, <c>retention_analysis:</c>,
    /// <c>detailed_description:</c>, <c>overall_soundscape:</c>, <c>non_diegetic_music:</c> — the shape the
    /// authored ref2va workflow carries too. In the shots the characters are <c>&lt;Subject N&gt;</c>; the
    /// pictures are named only where the subjects are defined (§4), because a reference photograph named in the
    /// shots is one H3 is free to treat as a frame.</para>
    ///
    /// <para><b>Who writes what.</b> The language model writes <c>summary:</c>, the shots and the two sound
    /// sections, to the spec's action, camera, physics, lighting, acting and sound rules
    /// (<c>h3pw_clip_singularity_spec.md</c>). <c>subject_definitions:</c> and <c>retention_analysis:</c> are
    /// written here from the cast, the wardrobe and the continuity plan. They are fully determined by what the
    /// tab uploads, and a local model writing clips one at a time re-invents them every few clips — the
    /// inconsistency §12 and §16 warn about.</para>
    ///
    /// <para>Both code sections are baked into the clip body, so everything that treats a clip as a body — the
    /// stamp, the story-prompt store, the clip editor — carries them. The stamp's reference line and wardrobe
    /// lock still go ahead of <c>subject_definitions:</c>, where the guides put a task header.</para>
    ///
    /// <para>WPF-free, so the tests can reach all of it.</para>
    /// </summary>
    public static class H3SpecPrompt
    {
        /// <summary>The per-clip system prompt this build writes against.</summary>
        public const string ClipSystemPromptFile = "h3pw_clip_singularity_spec.md";

        /// <summary>What a saved story's <c>PromptBuild</c> says when this build wrote it.</summary>
        public const string BuildTag = "singularity-spec";

        public const string SubjectDefinitions = "subject_definitions:";
        public const string Summary = "summary:";
        public const string RetentionAnalysis = "retention_analysis:";
        public const string DetailedDescription = "detailed_description:";
        public const string OverallSoundscape = "overall_soundscape:";
        public const string NonDiegeticMusic = "non_diegetic_music:";

        /// <summary>Opens <c>summary:</c> — the mode marker the ref2va format reads as "generate from these
        /// references".</summary>
        public const string SummaryMarker = "[reference generation]";

        /// <summary>The four sections the writer produces, in the order the prompt carries them.</summary>
        public static readonly IReadOnlyList<string> WriterSections =
            new[] { Summary, DetailedDescription, OverallSoundscape, NonDiegeticMusic };

        /// <summary>One cast member as the code sections need them.</summary>
        /// <param name="Noun">"man", "woman" — how the wardrobe block names them.</param>
        /// <param name="Outfit">Their line of the locked wardrobe, or empty.</param>
        public sealed record CastMember(int Index, string Noun, string Outfit);

        // ── Reading a reply ─────────────────────────────────────────────────────────────────────────

        /// <summary>A section label at the start of a line, however the writer decorated it: <c>## Detailed
        /// Description:</c>, <c>- summary:</c>, <c>non-diegetic music:</c>.</summary>
        private static readonly Regex LabelRegex = new(
            @"^[ \t]*(?:[-*•>][ \t]*)?(?:#{1,6}[ \t]*)?(subject[ _\-]definitions|summary|retention[ _\-]analysis|detailed[ _\-]description|overall[ _\-]soundscape|non[ _\-]diegetic[ _\-]music)[ \t]*:[ \t]*",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly Regex ShotMarkerRegex =
            new(@"\[\s*Shot\s+\d+\s*\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>A prompt split into its labelled sections. <see cref="Lead"/> is whatever came before the
        /// first label.</summary>
        public sealed record Sections(string Lead, IReadOnlyDictionary<string, string> Fields)
        {
            /// <summary>The section's text, or empty when it is missing.</summary>
            public string this[string label] => Fields.TryGetValue(label, out var v) ? v : string.Empty;
        }

        /// <summary>Splits a prompt at its section labels. A label written twice keeps its first non-empty
        /// text.</summary>
        public static Sections Parse(string? text)
        {
            var t = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            var matches = LabelRegex.Matches(t);
            if (matches.Count == 0) return new Sections(t.Trim(), fields);

            for (var i = 0; i < matches.Count; i++)
            {
                var label = matches[i].Groups[1].Value.ToLowerInvariant().Replace(' ', '_').Replace('-', '_') + ":";
                var start = matches[i].Index + matches[i].Length;
                var end = i + 1 < matches.Count ? matches[i + 1].Index : t.Length;
                var content = t[start..end].Trim();
                if (!fields.TryGetValue(label, out var had) || had.Length == 0) fields[label] = content;
            }
            return new Sections(t[..matches[0].Index].Trim(), fields);
        }

        /// <summary>True for a body in this build's six-section shape — what a saved story is checked for.</summary>
        public static bool IsSpecBody(string? text)
        {
            var s = Parse(text);
            return s.Fields.ContainsKey(SubjectDefinitions) || s.Fields.ContainsKey(DetailedDescription);
        }

        /// <summary>The writer's reply with only its four sections kept, each label canonical and on a line of its
        /// own. A reply with no labels at all comes back as it was, so the check can say so.</summary>
        public static string RenderWriterSections(string? reply)
        {
            var s = Parse(reply);
            if (s.Fields.Count == 0) return s.Lead;
            var sb = new StringBuilder();
            foreach (var label in WriterSections)
                if (s.Fields.TryGetValue(label, out var content)) Section(sb, label, content);
            return sb.ToString();
        }

        // ── Tags ────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A picture tag, or a near miss of one — what the writer slips into from habit.</summary>
        private static readonly Regex PictureTagRegex =
            new(@"<\s*(?:Picture|Pictuer|Picutre|Pic|P)\s*(\d{1,2})(?:\s*>)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>A subject tag with its bracket or spacing gone astray: <c>&lt;Subject 1</c>, <c>&lt;subject1&gt;</c>.</summary>
        private static readonly Regex SubjectTagRegex =
            new(@"<\s*(?:Subject|Subjcet|Sujbect|Subj)\s*(\d{1,2})(?:\s*>)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex CleanSubjectTagRegex = new(@"<Subject (\d{1,2})>", RegexOptions.Compiled);

        /// <summary>
        /// Every character tag in the writer's text as a well-formed <c>&lt;Subject N&gt;</c>. A picture tag or a
        /// cast alias in the shots names a reference photograph where the spec wants the entity (§4), so it is
        /// rewritten rather than rejected — the writer meant the character.
        /// </summary>
        public static string ToSubjectTags(string? text)
        {
            var t = text ?? string.Empty;
            t = CastPromptStamp.CastTagRegex.Replace(t, m => $"<Subject {m.Groups[1].Value}>");
            t = PictureTagRegex.Replace(t, m => $"<Subject {m.Groups[1].Value}>");
            return SubjectTagRegex.Replace(t, m => $"<Subject {m.Groups[1].Value}>");
        }

        /// <summary>
        /// Null when the writer's reply can be rendered; otherwise why not, phrased for the retry. Run on the
        /// reply after <see cref="ToSubjectTags"/>.
        /// </summary>
        public static string? Validate(string? reply, int castCount)
        {
            var s = Parse(reply);
            var description = s[DetailedDescription];
            if (description.Length == 0)
                return "it carried no detailed_description: section to render. Reply with the four sections — " +
                       "summary:, detailed_description:, overall_soundscape:, non_diegetic_music: — each label on " +
                       "its own line, starting with summary:.";

            if (!ShotMarkerRegex.IsMatch(description))
                return "its detailed_description: has no [Shot 1]. Write the shots in playback order, each opening " +
                       "with its [Shot n] marker.";

            if (s[Summary].Length == 0)
                return "it had no summary: section. Open with summary: — one to three sentences on what happens in " +
                       "this clip, how it develops and the state it ends in.";

            for (var n = 1; n <= castCount; n++)
                if (!description.Contains($"<Subject {n}>", StringComparison.Ordinal))
                    return $"its detailed_description: never names <Subject {n}>, so that character has no identity " +
                           "in the shots and would render as a stranger or a duplicate. Name every character by " +
                           $"their tag, written in full as <Subject {n}>.";

            var strangers = CleanSubjectTagRegex.Matches(reply ?? string.Empty)
                .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                .Where(n => n < 1 || n > castCount)
                .Distinct()
                .ToList();
            if (strangers.Count > 0)
                return $"it names <Subject {strangers[0]}>, but this clip's cast is {TagList(castCount)} only. " +
                       "Invent no character.";

            return null;
        }

        // ── The code-written sections ───────────────────────────────────────────────────────────────

        /// <summary>
        /// <c>subject_definitions:</c> — one line per character binding the subject to their photographs, their
        /// wardrobe, and the clip's environment (§3, §4). Written in the canonical <c>&lt;Picture N&gt;</c> form;
        /// the stamp turns those into the cast aliases the reference node resolves.
        ///
        /// <para>Wardrobe-neutral about the sheets on purpose: whether a sheet shows the locked outfit is a fact
        /// about this run, and a saved body is rendered again in later runs. The stamped reference line says it
        /// fresh every time.</para>
        /// </summary>
        public static string BuildSubjectDefinitions(
            IReadOnlyList<CastMember> cast, StoryContinuity.Environment environment, string? setting)
        {
            var lines = new List<string>();
            foreach (var m in cast)
            {
                var line = new StringBuilder(
                    $"<Subject {m.Index}>: Character {m.Index}, {Article(m.Noun)} — the one individual shown in the " +
                    $"Character {m.Index} reference photographs (<Picture {m.Index}>). Face, facial features, hair, " +
                    "skin and build come only from those photographs and stay identical in every shot, at every " +
                    "distance and through every camera move.");
                if (!string.IsNullOrWhiteSpace(m.Outfit))
                    line.Append(" Wears, unchanged from the first frame to the last: ")
                        .Append(m.Outfit.Trim().TrimEnd('.')).Append('.');
                lines.Add(line.ToString());
            }

            var place = StoryContinuity.SceneSentence(environment);
            if (place.Length == 0 && !string.IsNullOrWhiteSpace(setting)) place = setting.Trim();
            if (place.Length > 0) lines.Add($"<Environment>: {place}");

            return string.Join("\n", lines);
        }

        /// <summary>
        /// <c>retention_analysis:</c> — what each photograph hands its subject, in the spec's vocabulary (§6).
        /// <c>partially_preserved</c>, never <c>fully_preserved</c>: only the identity is kept, and a photograph
        /// declared fully preserved is one H3 may copy whole — backdrop, pose and all — into the video.
        /// </summary>
        public static string BuildRetentionAnalysis(IReadOnlyList<CastMember> cast)
        {
            var lines = cast.Select(m =>
                $"<Picture {m.Index}> → <Subject {m.Index}>: partially_preserved, throughout the clip — identity " +
                "(face, facial features, hair, skin, build) is kept; clothing follows subject_definitions. The " +
                "photograph's plain studio backdrop, neutral standing pose, framing and any panel layout are not " +
                "carried into the video, and it is never a frame of it.").ToList();
            lines.Add("<Environment>, composition, camera, action, physical effects and lighting: newly_generated " +
                      "from detailed_description.");
            lines.Add("Audio: newly_generated — there is no reference audio.");
            return string.Join("\n", lines);
        }

        /// <summary>A leading bracketed mode marker the writer may have typed, right or wrong.</summary>
        private static readonly Regex LeadingModeMarkerRegex =
            new(@"^\s*\[[^\]\n]*(?:generation|completion)[^\]\n]*\]\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary><c>summary:</c> opened by exactly one <see cref="SummaryMarker"/>, whatever marker the writer
        /// used — a keyframe marker here would ask H3 for a different task.</summary>
        public static string WithSummaryMarker(string? summary)
        {
            var text = LeadingModeMarkerRegex.Replace((summary ?? string.Empty).Trim(), string.Empty).Trim();
            return text.Length == 0 ? SummaryMarker : $"{SummaryMarker} {text}";
        }

        /// <summary>The six sections in the spec's order (§2, §18). An empty soundscape is left out; an empty
        /// score is written <c>N/A</c>.</summary>
        public static string Assemble(
            string subjectDefinitions, string summary, string retentionAnalysis,
            string description, string soundscape, string music)
        {
            var sb = new StringBuilder();
            Section(sb, SubjectDefinitions, subjectDefinitions);
            Section(sb, Summary, summary);
            Section(sb, RetentionAnalysis, retentionAnalysis);
            Section(sb, DetailedDescription, description);
            Section(sb, OverallSoundscape, soundscape);
            Section(sb, NonDiegeticMusic, string.IsNullOrWhiteSpace(music) ? "N/A" : music);
            return sb.ToString();
        }

        private static void Section(StringBuilder sb, string label, string? content)
        {
            var text = (content ?? string.Empty).Trim();
            if (text.Length == 0) return;
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(label).Append('\n').Append(text);
        }

        // ── The request ─────────────────────────────────────────────────────────────────────────────

        /// <summary>Three to five shots across the clip, one per ~4.5 s — the same budget as the researched build.
        /// The spec asks each shot for a full action chain, camera chain, physical feedback and sound (§7), and
        /// warns against packing actions into shots (§16); a cut every second leaves no room for either.</summary>
        public static int ShotCount(double seconds) => H3ResearchPrompt.ShotCount(seconds);

        /// <summary>Appended to the beat sheet, where a clip's workload is decided (§8, §16, §17).</summary>
        public const string BeatSheetRules =
            "Two extra rules for these beats:\n" +
            "- Each beat is ONE continuous action that can be watched from start to finish: who starts in what " +
            "state, what sets it off, what they do, what it does to someone or something, and the state it leaves " +
            "them in. Never a list of separate events.\n" +
            "- One main action per beat. A beat holding an arrival, a fight and an escape is three beats, not one.";

        /// <summary>Who the subjects are, and the tag rules the writer breaks most.</summary>
        public static string CastBlock(IReadOnlyList<CastMember> cast)
        {
            var who = string.Join("; ", cast.Select(m => $"<Subject {m.Index}> is CHARACTER {m.Index} ({Article(m.Noun)})"));
            var names = cast.Count > 1 ? "CHARACTER 1 and CHARACTER 2" : "CHARACTER 1";
            return $"CAST — {who}. subject_definitions: already binds " +
                   $"{(cast.Count > 1 ? "each subject" : "the subject")} to their reference photographs. The beat " +
                   $"calls them {names}; write them ONLY as {TagList(cast.Count)} — in full, with both angle brackets " +
                   "— at their first appearance and wherever they act, are struck, grabbed, spoken to or reacted to, " +
                   "keeping the numbers exactly as the beat uses them. Never write <Picture N>, a name, or an untagged " +
                   "\"he\", \"she\" or \"the man\" for anyone on screen, and never describe a face, hair, skin, build " +
                   "or age.";
        }

        /// <summary>The shot count and the cut times, and how the clip's last shot ends (§7, §12).</summary>
        public static string ShotPlan(double seconds, int shots, bool lastClip)
        {
            var times = H3ResearchPrompt.CutTimes(seconds, shots);
            var sb = new StringBuilder();
            sb.Append("SHOT PLAN — detailed_description: carries EXACTLY ").Append(shots)
              .Append(" shots, in playback order. [Shot 1] opens the clip and carries no timestamp. ");
            if (times.Count > 0)
                sb.Append("The later cuts land on these times, written exactly like this: ")
                  .Append(string.Join(", ", times.Select((t, i) => $"[Shot {i + 2}] At {t}"))).Append(". ");
            sb.Append("Every shot ends on a stated final state — a pose, a position, what is held, what has changed. ");
            sb.Append(lastClip
                ? "This is the story's last clip: its last shot may settle into the story's final state, with the " +
                  "camera still moving gently."
                : $"The last shot runs to {H3ResearchPrompt.Timecode(seconds)} and its final state is still in " +
                  "motion — a body moving, a camera travelling — on its way into the next clip. Never a held pose, " +
                  "never a stare into the lens.");
            return sb.ToString();
        }

        /// <summary>The seven questions of §7 and the chains of §1, restated per clip because they are the rules a
        /// local writer drops first.</summary>
        public const string ShotQuestions =
            "EVERY SHOT ANSWERS SEVEN QUESTIONS — what is visible; where each subject is in the frame; what state " +
            "they start in; what they continuously do, as a chain (initial state → trigger → primary action → " +
            "momentum → contact or reaction → final state); how the camera moves (position or shot size, movement, " +
            "direction, speed or amplitude, and the subject it follows); what physical or visual feedback the action " +
            "causes (dust, cloth, hair, sparks, debris, reflections, light); and what is heard.";

        /// <summary>§11, §13 and §16's generic-language failures, in one line.</summary>
        public const string ConcreteRule =
            "CONCRETE, NOT GENERIC — never \"cinematic\", \"epic\", \"dynamic camera\" or \"cool effects\" in place of " +
            "an instruction someone could film. Light has a source, a direction and a behaviour on the surfaces it " +
            "hits. Emotion is eyes, face, breath, posture, hands and what the subject is looking at. One main action " +
            "chain per shot.";

        /// <summary>§12's screen direction. Fixed for the whole chain: the spec keeps direction consistent unless a
        /// reversal is deliberate, and each clip here is rendered with no memory of the last.</summary>
        public static string ScreenDirection(int castCount) => castCount > 1
            ? "SCREEN DIRECTION — <Subject 1> starts on screen-left and <Subject 2> on screen-right, in every clip of " +
              "this chain. Say where each is in any shot that holds both, and keep those sides unless the beat has " +
              "one cross past the other — then describe the crossing."
            : "SCREEN DIRECTION — keep <Subject 1>'s direction of travel the same from shot to shot unless the beat " +
              "turns them around, and say so when it does.";

        /// <summary>§14: speaker IDs, sync, and the silence line H3 needs when nobody speaks.</summary>
        public static string SpeechRule(int castCount)
        {
            var silent = castCount > 1
                ? "<Subject 1> and <Subject 2> remain silent"
                : "<Subject 1> remains silent";
            return
                "SOUND AND SPEECH — write dialogue ONLY if the beat contains spoken words.\n" +
                $"- No spoken words: in [Shot 1] write \"{silent}, and no voice, narration or voiceover is heard.\" " +
                "and write no <d> tag anywhere.\n" +
                "- Spoken words: give each speaker a stable speaker ID in the order they are first heard — (S1), then " +
                "(S2) — and write the line inside its shot as <Subject N> (S1) says: <d>[English] the words</d>, about " +
                "two words per second, the mouth moving in sync. A subject visible but not speaking during a line " +
                "remains silent with the mouth closed.\n" +
                "- Every sound in overall_soundscape: is tied to something visible in the shots — footsteps with the " +
                "steps, the impact with the contact. No speech and no music there.";
        }

        /// <summary>The whole per-clip rule block.</summary>
        /// <param name="hasContinuityPlan">Whether the request already carries a per-clip CONTINUITY block; the
        /// chain-wide lighting lock is left out when it does, as in the researched build.</param>
        public static string RulesFor(
            int castCount, double seconds, int shots, string? setting, bool hasContinuityPlan, bool lastClip)
        {
            var sb = new StringBuilder();
            if (!hasContinuityPlan) sb.Append(H3ResearchPrompt.LightingLock(setting)).Append("\n\n");
            sb.Append(ShotPlan(seconds, shots, lastClip)).Append("\n\n");
            sb.Append(ShotQuestions).Append("\n\n");
            sb.Append(ConcreteRule).Append("\n\n");
            sb.Append(ScreenDirection(castCount)).Append("\n\n");
            sb.Append(SpeechRule(castCount));
            return sb.ToString();
        }

        /// <summary>The log line saying this build wrote the run.</summary>
        public static string DescribeRun(int clipCount, double seconds)
        {
            var shots = ShotCount(seconds);
            return $"📐 Singularity spec prompts ON — {ClipSystemPromptFile}: six-section full-reference prompts " +
                   "(subject_definitions and retention_analysis written by code, the rest by the writer), " +
                   $"{shots} shots per {seconds.ToString("0.#", CultureInfo.InvariantCulture)}s clip (cuts at " +
                   $"{string.Join(", ", H3ResearchPrompt.CutTimes(seconds, shots))}), <Subject N> tags, for all " +
                   $"{clipCount} clip(s).";
        }

        // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

        private static string TagList(int castCount) =>
            castCount > 1 ? "<Subject 1> and <Subject 2>" : "<Subject 1>";

        /// <summary>"a man", "an android", and "a person" for nothing at all.</summary>
        private static string Article(string? noun)
        {
            var n = (noun ?? string.Empty).Trim();
            if (n.Length == 0) return "a person";
            if (n.StartsWith("a ", StringComparison.OrdinalIgnoreCase) ||
                n.StartsWith("an ", StringComparison.OrdinalIgnoreCase)) return n;
            return ("aeiou".Contains(char.ToLowerInvariant(n[0])) ? "an " : "a ") + n;
        }
    }
}
