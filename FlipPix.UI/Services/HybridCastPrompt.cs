using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FlipPix.UI.Services
{
    /// <summary>
    /// Builds the six-section <b>hybrid FL + reference</b> prompt the 🪪👥⚡ H3 Cast Hybrid tab sends to
    /// MiniMax H3 — the documented exception to "do not mix keyframe alignment with the R2V six-section
    /// form", in which the alignment lives <i>inside</i> the text and the extra pictures exist only on the
    /// reference node (<c>prompts/h3-hybrid-prompting-guide.md</c>).
    ///
    /// <para><b>Why half of this prompt is written in code.</b> Four of the six sections are pure bookkeeping:
    /// which picture is a frame lock and at what timestamp, which picture is only an identity reference and
    /// must never become a frame, and which pictures are the same person. All of it is fully determined by
    /// what the tab is about to upload — and none of it survives being asked for. A language model writing a
    /// chain of clips as independent blocks re-invents the retention lines every few clips, and a single
    /// wrong <c>fully_preserved</c> is a studio photograph rendered as a video frame. So
    /// <see cref="Assemble"/> writes <c>subject_definitions</c>, <c>retention_analysis</c>, the alignment
    /// paragraph and the global negatives from the reference list itself, and the model is left with the
    /// half it is actually good at: <c>summary</c>, the shots, the soundscape and the score.</para>
    ///
    /// <para><b>Picture numbering.</b> <c>--ref-image</c> order is <c>&lt;Picture 1&gt;</c>,
    /// <c>&lt;Picture 2&gt;</c>, … 1-based, in connection order. This tab wires the <i>keyframe stills first</i>
    /// (so a timeline lock is always <c>&lt;Picture 1&gt;</c>…<c>&lt;Picture K&gt;</c>, exactly as the guide's
    /// templates read) and the cast's sheet panels after them. The people themselves are
    /// <c>&lt;Subject 1&gt;</c> / <c>&lt;Subject 2&gt;</c> in the body, which is what keeps the model's prose
    /// independent of how many pictures each of them happens to occupy.</para>
    ///
    /// <para><b>The invariant.</b> <see cref="Strip"/> ∘ <see cref="Assemble"/> is the identity on model
    /// bodies and <see cref="Assemble"/> ∘ <see cref="Strip"/> is idempotent: Analyze assembles, the user
    /// edits the wardrobe or the keyframe list, Add to Queue re-assembles the same body — and no preamble
    /// accumulates.</para>
    /// </summary>
    public static class HybridCastPrompt
    {
        // ── The six section labels, in the order H3 expects them ───────────────
        public const string SubjectDefinitions = "subject_definitions";
        public const string Summary = "summary";
        public const string RetentionAnalysis = "retention_analysis";
        public const string DetailedDescription = "detailed_description";
        public const string OverallSoundscape = "overall_soundscape";
        public const string NonDiegeticMusic = "non_diegetic_music";

        /// <summary>The four sections the language model writes. The other two are written here.</summary>
        public static readonly string[] ModelSections =
            { Summary, DetailedDescription, OverallSoundscape, NonDiegeticMusic };

        private static readonly string[] AllSections =
        {
            SubjectDefinitions, Summary, RetentionAnalysis, DetailedDescription,
            OverallSoundscape, NonDiegeticMusic,
        };

        /// <summary>Opens <c>summary</c> in hybrid mode — the mode marker H3 reads to mean "complete these
        /// keyframes <i>and</i> generate from these references".</summary>
        public const string SummaryMarker = "[keyframe completion + reference generation]";

        /// <summary>Opens the code-written first paragraph of <c>detailed_description</c>.</summary>
        public const string AlignmentLead = "How the reference pictures align with the target video —";

        /// <summary>Opens the code-written second paragraph of <c>detailed_description</c>.</summary>
        public const string GlobalRulesLead = "The target video is ";

        /// <summary>Opens the wardrobe block — shared verbatim with the plain H3 Cast tab.</summary>
        public const string WardrobeLockPrefix = CastPromptStamp.WardrobeLockPrefix;

        #region Inputs

        /// <summary>
        /// One timeline still: a picture the video must land on exactly, at <paramref name="Seconds"/>.
        /// A cut replaces pose, wardrobe <i>and</i> background together, which is why a keyframe carries its
        /// own shot rather than continuing the one before it.
        /// </summary>
        /// <param name="Seconds">Where in the clip this frame is locked. The first keyframe is normally 0.</param>
        /// <param name="Label">What to call the still in the log and the UI — never sent to H3.</param>
        public sealed record Keyframe(double Seconds, string Label);

        /// <summary>The three views <c>h3-charsheet-2511.md</c> builds a sheet from, in panel order.</summary>
        public const string ViewFront = "full-body front";
        public const string ViewBack = "full-body back";
        public const string ViewFace = "face close-up";

        /// <summary>
        /// One member of the cast as the prompt needs them: which subject they are, the word to call them
        /// by, and what each of their reference pictures shows.
        ///
        /// <para>The views are carried rather than derived from the count, because the tab does not
        /// necessarily send every panel it cut. Dropping the back view leaves two pictures that are
        /// <i>front and face</i>, not "view 1 and view 2" — and a face picture the prompt fails to call a
        /// face is a picture H3 has no reason to weigh when the camera is close.</para>
        /// </summary>
        public sealed record CastMember(int Index, string Noun, IReadOnlyList<string> Views)
        {
            /// <summary>How many reference slots this character occupies.</summary>
            public int Panels => Math.Max(1, Views.Count);

            /// <summary>Whichever picture is the face close-up, 0-based — the one the likeness comes from
            /// when the camera is close, and the identity reference the face-refine pass tracks by.</summary>
            public int FacePanel
            {
                get
                {
                    var i = Views.ToList().FindIndex(v => string.Equals(v, ViewFace, StringComparison.OrdinalIgnoreCase));
                    return i >= 0 ? i : Math.Max(0, Views.Count - 1);
                }
            }
        }

        /// <summary>
        /// What a sheet cut into <paramref name="panels"/> pieces shows, when nothing better is known — a
        /// three-panel sheet is the one this tab builds, and everything else is described positionally
        /// rather than guessed at.
        /// </summary>
        public static IReadOnlyList<string> DefaultViews(int panels) => panels switch
        {
            <= 1 => new[] { "full character sheet" },
            3 => new[] { ViewFront, ViewBack, ViewFace },
            _ => Enumerable.Range(1, panels).Select(i => $"view {i}").ToList(),
        };

        #endregion

        #region Numbering

        /// <summary>Total pictures a run wires: the keyframes, then every cast panel.</summary>
        public static int PictureCount(int keyframes, IReadOnlyList<CastMember> cast) =>
            keyframes + cast.Sum(c => Math.Max(1, c.Panels));

        /// <summary>
        /// The 1-based picture number of one cast panel — keyframes occupy the numbers before it. Cast order
        /// is the wiring order, so this and <c>ref_image_N</c> cannot drift apart.
        /// </summary>
        public static int CastPicture(int keyframes, IReadOnlyList<CastMember> cast, int character, int panel)
        {
            var n = keyframes + 1;
            foreach (var member in cast)
            {
                if (member.Index == character) return n + panel;
                n += Math.Max(1, member.Panels);
            }
            throw new ArgumentOutOfRangeException(nameof(character), $"Character {character} is not in this cast.");
        }

        /// <summary>Every picture number one character occupies, in panel order.</summary>
        public static IReadOnlyList<int> CastPictures(int keyframes, IReadOnlyList<CastMember> cast, int character)
        {
            var member = cast.First(c => c.Index == character);
            return Enumerable.Range(0, Math.Max(1, member.Panels))
                             .Select(p => CastPicture(keyframes, cast, character, p))
                             .ToList();
        }

        private static string Picture(int n) => $"<Picture {n}>";

        private static string Subject(int n) => $"<Subject {n}>";

        /// <summary>"&lt;Picture 3&gt;, &lt;Picture 4&gt; and &lt;Picture 5&gt;" — or the single one.</summary>
        private static string PictureList(IEnumerable<int> numbers)
        {
            var list = numbers.Select(Picture).ToList();
            if (list.Count == 0) return string.Empty;
            if (list.Count == 1) return list[0];
            return string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1];
        }

        private static string Seconds(double s) => s.ToString("0.00", CultureInfo.InvariantCulture);

        #endregion

        #region Assembly

        /// <summary>
        /// Puts a model-written body into the finished six-section hybrid prompt for a given set of
        /// keyframes and cast.
        /// </summary>
        /// <param name="body">Whatever the model returned, or an already-assembled prompt — it is
        /// <see cref="Strip"/>ped first, so re-assembling is safe and idempotent.</param>
        /// <param name="keyframes">Timeline locks in timestamp order. May be empty: with no keyframe the
        /// prompt is a plain reference-generation run and says so, rather than pretending picture 1 is a
        /// frame.</param>
        /// <param name="cast">The characters, in wiring order. Character 1 is always present.</param>
        /// <param name="wardrobe">The locked outfits, stamped in ahead of the sections exactly as the plain
        /// H3 Cast tab does — it is the one block that is identical in every clip of a chain.</param>
        /// <param name="clipSeconds">The clip's own duration, for the "no end-frame lock at N.00" sentence.</param>
        /// <param name="medium">"live-action and cinematic", "anime, cinematic", … — opens the global rules.</param>
        /// <param name="sheetsShowWardrobe">True when the sheets were built wearing the locked outfits, so
        /// the pictures and the wardrobe block agree and the cast pictures carry clothing as well as identity.</param>
        /// <param name="selectiveCast">Drops a character the body never names. Pass it for a story chain,
        /// where clips genuinely take turns; a lone clip keeps whoever is loaded.</param>
        /// <param name="focusSubject">1 or 2 to write the prompt for <b>one</b> member of the cast — the
        /// face-refine pass, which regenerates one tracked face at a time and must not be shown the other
        /// character's photographs while it does. 0 (the default) keeps the whole cast.</param>
        public static string Assemble(
            string? body,
            IReadOnlyList<Keyframe> keyframes,
            IReadOnlyList<CastMember> cast,
            string? wardrobe,
            double clipSeconds,
            string medium,
            bool sheetsShowWardrobe = false,
            bool selectiveCast = false,
            int focusSubject = 0)
        {
            var stripped = Strip(body);
            if (stripped.Length == 0) return string.Empty;

            var sections = SplitSections(stripped);
            if (focusSubject > 0)
                cast = cast.Where(c => c.Index == focusSubject).ToList();
            else if (selectiveCast && cast.Count > 1 && !MentionsSubject(stripped, 2))
                cast = cast.Where(c => c.Index == 1).ToList();
            if (cast.Count == 0) return string.Empty;

            var keys = keyframes.OrderBy(k => k.Seconds).ToList();
            var sb = new StringBuilder();

            var wardrobeLock = CastPromptStamp.BuildWardrobeLock(wardrobe, sheetsShowWardrobe);
            if (wardrobeLock.Length > 0) sb.Append(DropAbsentCast(wardrobeLock, cast)).Append('\n');

            Section(sb, SubjectDefinitions, BuildSubjectDefinitions(keys, cast, sheetsShowWardrobe, focusSubject));
            Section(sb, Summary, BuildSummary(keys, cast, clipSeconds, medium, Get(sections, Summary)));
            Section(sb, RetentionAnalysis, BuildRetentionAnalysis(keys, cast, clipSeconds, sheetsShowWardrobe));
            Section(sb, DetailedDescription,
                BuildAlignment(keys, cast, clipSeconds) + "\n" +
                BuildGlobalRules(keys, cast, medium) +
                Paragraph(Get(sections, DetailedDescription)));
            Section(sb, OverallSoundscape, Fallback(Get(sections, OverallSoundscape),
                "Room tone matching the scene, quiet breath, light fabric rustle, and the incidental sounds " +
                "of the movement described above."));
            Section(sb, NonDiegeticMusic, Fallback(Get(sections, NonDiegeticMusic), "N/A"));

            return sb.ToString().TrimEnd();
        }

        private static void Section(StringBuilder sb, string label, string content)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(label).Append(":\n").Append(content.Trim()).Append('\n');
        }

        private static string Paragraph(string text) =>
            string.IsNullOrWhiteSpace(text) ? string.Empty : "\n" + text.Trim();

        private static string Fallback(string text, string standIn) =>
            string.IsNullOrWhiteSpace(text) ? standIn : text.Trim();

        /// <summary>
        /// Section 1 — every keyframe's job (one line each, because each carries its own timestamp), then
        /// <b>one block per character</b>: what their pictures are, and who they are.
        ///
        /// <para>The cast lines are the ones that matter most: a studio photograph handed to H3 without a job
        /// is a picture H3 is free to treat as a frame, which is how a reference sheet ends up rendered as a
        /// shot of someone standing on a plain backdrop.</para>
        ///
        /// <para><b>Why per character rather than per picture.</b> Six panels used to mean six near-identical
        /// paragraphs here and six more in <c>retention_analysis</c> — some seven hundred words of boilerplate
        /// ahead of the shot list that actually describes the video, all of it repeated in every clip of a
        /// chain. The pictures of one person share a job, so they are given it together, and what is left is
        /// the part that differs: which views they are, and which of them carries the face.</para>
        /// </summary>
        private static string BuildSubjectDefinitions(
            IReadOnlyList<Keyframe> keys, IReadOnlyList<CastMember> cast, bool sheetsShowWardrobe,
            int focusSubject = 0)
        {
            var lines = new List<string>();

            for (var i = 0; i < keys.Count; i++)
            {
                var p = Picture(i + 1);
                var shot = $"[Shot {i + 1}]";
                if (i == 0 && keys[i].Seconds <= 0.001)
                    lines.Add($"{p} is the opening keyframe for {shot} at {Seconds(keys[i].Seconds)} seconds. " +
                              "Job: exact first frame only — pose, wardrobe, and background as shown, without " +
                              "reinterpretation. Not a last-frame lock.");
                else
                    lines.Add($"{p} is the cut-in keyframe for {shot} at {Seconds(keys[i].Seconds)} seconds. " +
                              $"Job: exact {shot.Trim('[', ']')} frame only — pose, wardrobe, and background as " +
                              $"shown, without reinterpretation. Not a continuation of {Picture(i)}.");
            }

            var clothing = sheetsShowWardrobe
                ? "identity and wardrobe appearance only — face, facial features, hair, skin, build and the " +
                  "exact garments shown"
                : "identity only — face, facial features, hair, skin and build; the studio clothing they show " +
                  "is irrelevant to this video";

            foreach (var member in cast)
            {
                var pictures = CastPictures(keys.Count, cast, member.Index);
                var views = string.Join(", ", member.Views.Take(pictures.Count));

                lines.Add($"{PictureList(pictures)} {(pictures.Count == 1 ? "is a" : "are")} studio reference " +
                          $"photograph{(pictures.Count == 1 ? "" : "s")} of {Subject(member.Index)} — " +
                          $"{views}, in that order. Job: {clothing}. Not a person-as-scene. Not a pose. Not a " +
                          $"background. Not a timeline keyframe. Never insert " +
                          $"{(pictures.Count == 1 ? "it" : "any of them")} as a video frame.");

                var shown = pictures.Count == 1
                    ? $"shown in {PictureList(pictures)}"
                    : $"shown in {PictureList(pictures)} — those are one and the same person from several " +
                      $"angles, not {pictures.Count} different people";
                var face = Picture(pictures[Math.Min(member.FacePanel, pictures.Count - 1)]);
                lines.Add($"{Subject(member.Index)} is the same adult, a {member.Noun}, {shown}. Face and " +
                          "identity stay consistent from the first frame to the last, and come only from " +
                          $"their own pictures: the likeness in {face} is the one to match at every distance " +
                          "and through every camera move, including wide shots and fast motion where the face " +
                          "is small." +
                          (keys.Count > 0
                              ? " Where a keyframe shows them, that frame's pose, wardrobe and background win " +
                                "at its timestamp — their face does not."
                              : string.Empty));
            }

            if (focusSubject > 0)
                lines.Add($"This pass regenerates only {Subject(focusSubject)}'s face, cropped out of a frame " +
                          "that is already rendered. Anyone else the shots name is outside the crop: do not " +
                          "draw another person into it, and do not change the framing.");

            return string.Join("\n", lines);
        }

        /// <summary>
        /// Section 2 — the mode marker, the cut plan, and then the model's own one-paragraph pitch. The
        /// marker has to be the first thing in the section, so the code lead is always paragraph one.
        /// </summary>
        private static string BuildSummary(
            IReadOnlyList<Keyframe> keys, IReadOnlyList<CastMember> cast,
            double clipSeconds, string medium, string modelSummary)
        {
            var who = string.Join(" and ", cast.Select(c => Subject(c.Index)));
            var sb = new StringBuilder(SummaryMarker).Append(' ');
            sb.Append($"Generate a {Seconds(clipSeconds)}-second {medium} clip of {who}. ");

            if (keys.Count == 0)
            {
                sb.Append("One continuous take, no hard cuts, no opening frame lock and no end-frame lock — " +
                          "every attached picture is a studio identity reference, not a frame. ");
            }
            else
            {
                for (var i = 0; i < keys.Count; i++)
                    sb.Append(i == 0
                        ? $"At {Seconds(keys[i].Seconds)} seconds the frame is exactly {Picture(1)}. "
                        : $"At {Seconds(keys[i].Seconds)} seconds cut to exactly {Picture(i + 1)}. ");

                sb.Append($"Then continue from {Picture(keys.Count)} through {Seconds(clipSeconds)} seconds " +
                          "with no end-frame lock. ");
                if (keys.Count > 1)
                    sb.Append("Each cut replaces pose, outfit and background together. ");
            }

            var castPictures = cast.SelectMany(c => CastPictures(keys.Count, cast, c.Index)).ToList();
            if (castPictures.Count > 0)
                sb.Append($"{PictureList(castPictures)} {(castPictures.Count == 1 ? "is a" : "are")} studio " +
                          "identity reference" + (castPictures.Count == 1 ? "" : "s") +
                          " only and must never appear as cut-in stills.");

            var model = modelSummary.Trim();
            // The marker paragraph is what Strip cuts back off, so the model's own summary has to stay a
            // paragraph of its own rather than being folded into it.
            return model.Length == 0 ? sb.ToString().TrimEnd() : sb.ToString().TrimEnd() + "\n" + model;
        }

        /// <summary>
        /// Section 3 — what each picture and subject keeps. This is the section that decides whether a
        /// picture becomes a frame: <c>fully_preserved</c> at a timestamp is a lock,
        /// <c>partially_preserved</c> with "never a keyframe" is a reference.
        /// </summary>
        private static string BuildRetentionAnalysis(
            IReadOnlyList<Keyframe> keys, IReadOnlyList<CastMember> cast,
            double clipSeconds, bool sheetsShowWardrobe)
        {
            var lines = new List<string>();

            for (var i = 0; i < keys.Count; i++)
            {
                var role = i == 0 && keys[i].Seconds <= 0.001 ? "opening frame lock" : "cut-in lock";
                var extra = i == 0
                    ? $"; not an end-frame lock at {Seconds(clipSeconds)} seconds"
                    : $"; not a style transfer onto {Picture(i)}";
                lines.Add($"{Picture(i + 1)} (appears in [Shot {i + 1}] at {Seconds(keys[i].Seconds)}s): " +
                          $"fully_preserved - {role}; Shot {i + 1} set, pose and wardrobe only{extra}.");
            }

            var what = sheetsShowWardrobe
                ? "retain identity and the garment appearance shown"
                : "retain facial identity, hair, skin and build only — not the studio clothing";

            foreach (var member in cast)
            {
                var pictures = CastPictures(keys.Count, cast, member.Index);
                lines.Add($"{PictureList(pictures)} (never keyframes): partially_preserved - {what}; do not " +
                          "reproduce a studio photograph as a video frame; do not copy its plain background, " +
                          "its neutral standing pose, or any panel, grid or split-screen layout.");
            }

            foreach (var member in cast)
            {
                var shots = keys.Count > 0
                    ? string.Join(", ", Enumerable.Range(1, keys.Count).Select(i => $"[Shot {i}]"))
                    : "[Shot 1]";
                lines.Add($"{Subject(member.Index)} (appears in {shots}): fully_preserved - the same person " +
                          "throughout, at every shot size; identity taken only from their own pictures, and " +
                          "not weakened when the framing is wide, the face is small or the camera is moving" +
                          (keys.Count > 0 ? "; each shot matches that shot's keyframe exactly." : "."));
            }

            return string.Join("\n", lines);
        }

        /// <summary>
        /// The first paragraph of section 4 — the alignment statement. This is where hybrid mode differs
        /// from plain R2V: the timestamps are named in prose rather than wired into a first/last-frame node.
        /// </summary>
        private static string BuildAlignment(
            IReadOnlyList<Keyframe> keys, IReadOnlyList<CastMember> cast, double clipSeconds)
        {
            var sb = new StringBuilder(AlignmentLead).Append(' ');

            if (keys.Count == 0)
            {
                sb.Append("no attached picture aligns with any timestamp as a frame. ");
            }
            else
            {
                sb.Append(string.Join("; ", keys.Select((k, i) =>
                    $"Picture {i + 1} (from [Shot {i + 1}]) aligns with the {Seconds(k.Seconds)}-second mark " +
                    "of the target video")));
                sb.Append(". ");
            }

            var castPictures = cast.SelectMany(c => CastPictures(keys.Count, cast, c.Index)).ToList();
            if (castPictures.Count > 0)
            {
                var names = string.Join(", ", castPictures.Select(n => $"Picture {n}"));
                sb.Append($"{names} {(castPictures.Count == 1 ? "does" : "do")} not align with any timestamp " +
                          $"as {(castPictures.Count == 1 ? "a frame" : "frames")}; " +
                          $"{(castPictures.Count == 1 ? "it is a" : "they are")} studio identity " +
                          $"reference{(castPictures.Count == 1 ? "" : "s")} of the cast, used throughout. ");
            }

            sb.Append($"There is no last-frame lock at {Seconds(clipSeconds)} seconds.");
            return sb.ToString();
        }

        /// <summary>The second paragraph of section 4 — the global negatives, restated per clip because a
        /// clip is rendered with no memory of the one before it.</summary>
        private static string BuildGlobalRules(
            IReadOnlyList<Keyframe> keys, IReadOnlyList<CastMember> cast, string medium)
        {
            var sb = new StringBuilder($"{GlobalRulesLead}{medium}. No on-screen text. No extra people. ");
            sb.Append("Do not invent a new outfit. ");
            // The failure this sentence is aimed at: identity holds in close-ups and slips in the wide, fast
            // shots, where the face is a handful of pixels and the model has the least to hold on to.
            sb.Append("Keep every face on model at every shot size — a face that is far away, small in frame " +
                      "or moving fast is still the same person, and must not be re-cast, re-aged or " +
                      "generalised into a stock face. ");
            if (keys.Count > 1) sb.Append("Do not blend rooms or wardrobes across cuts. ");

            var castPictures = cast.SelectMany(c => CastPictures(keys.Count, cast, c.Index)).ToList();
            if (castPictures.Count > 0)
                sb.Append($"Do not cut to {PictureList(castPictures)}, and never show the same person more " +
                          "than once in a frame or line the cast up side by side against a plain backdrop.");
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Prepares the shared wardrobe block for a hybrid prompt: drops the characters this clip does not
        /// cast, and <b>re-tags its people from pictures to subjects</b>.
        ///
        /// <para><see cref="CastPromptStamp.NormalizeWardrobe"/> writes
        /// <c>Character 2 (&lt;Picture 2&gt;, a woman) wears: …</c> — the plain 🪪👥 H3 Cast tab's canonical
        /// numbering, where one character is one picture. It renumbers those lines when it stamps a prompt;
        /// this tab never could, because here <c>&lt;Picture 2&gt;</c> is character 1's <i>back view</i>.
        /// Left alone, the block tells H3 that the man's back is the woman — a cast mix-up written into the
        /// authoritative wardrobe of every clip. Subject tags carry the same meaning and cannot collide.</para>
        /// </summary>
        private static string DropAbsentCast(string wardrobeLock, IReadOnlyList<CastMember> cast)
        {
            var lines = wardrobeLock.Split('\n').ToList();
            var kept = lines.Where(l => KeepsWardrobeLine(l, cast)).Select(RetagWardrobeLine).ToList();
            // Nothing but the header left means a wardrobe box that does not describe this cast at all — keep
            // it whole rather than reducing the authoritative wardrobe to a heading.
            if (kept.Count <= 1) kept = lines.Select(RetagWardrobeLine).ToList();
            return string.Join("\n", kept);
        }

        private static readonly Regex WardrobeCharacterRegex =
            new(@"^\s*Character\s+(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static bool KeepsWardrobeLine(string line, IReadOnlyList<CastMember> cast)
        {
            var m = WardrobeCharacterRegex.Match(line);
            if (!m.Success) return true;
            return int.TryParse(m.Groups[1].Value, out var index) && cast.Any(c => c.Index == index);
        }

        /// <summary>Turns the <c>(&lt;Picture n&gt;, a woman)</c> of a wardrobe line into
        /// <c>(&lt;Subject n&gt;, a woman)</c>. Only touches lines that name a character, so a hand-typed
        /// wardrobe that mentions nothing of the sort passes through untouched.</summary>
        private static string RetagWardrobeLine(string line) =>
            WardrobeCharacterRegex.IsMatch(line)
                ? PictureRegex.Replace(line, m => Subject(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)))
                : line;

        #endregion

        #region Stripping

        /// <summary>
        /// Cuts an assembled prompt back to the model's own four sections — the inverse of
        /// <see cref="Assemble"/>, and the reason a wardrobe edit or a keyframe change can be applied to a
        /// prompt already on screen without re-asking the language model.
        ///
        /// <para>Text that was never assembled (a raw model reply, a hand-typed prompt) passes through
        /// unchanged apart from having its sections normalised, which is what makes
        /// <c>Assemble(Strip(x))</c> safe to call on anything.</para>
        /// </summary>
        public static string Strip(string? prompt)
        {
            var t = Normalize(prompt);
            if (t.Length == 0) return string.Empty;

            t = StripWardrobeBlock(t);
            var sections = SplitSections(t);
            if (sections.Count == 0) return t;

            var kept = new StringBuilder();
            foreach (var label in ModelSections)
            {
                if (!sections.TryGetValue(label, out var content)) continue;
                content = label switch
                {
                    Summary => DropLineStartingWith(content, SummaryMarker),
                    DetailedDescription => DropLineStartingWith(
                        DropLineStartingWith(content, AlignmentLead), GlobalRulesLead),
                    _ => content,
                };
                if (content.Trim().Length == 0) continue;
                if (kept.Length > 0) kept.Append('\n');
                kept.Append(label).Append(":\n").Append(content.Trim()).Append('\n');
            }

            return kept.ToString().TrimEnd();
        }

        /// <summary>Removes a leading wardrobe block — from its header to the first blank line, which is how
        /// <see cref="CastPromptStamp.BuildWardrobeLock"/> writes it.</summary>
        public static string StripWardrobeBlock(string text) =>
            CastPromptStamp.StripWardrobeBlock(text);

        /// <summary>
        /// Rewrites a model body for a pass that receives <b>no keyframe stills</b> — the face-refine pass,
        /// which is conditioned on the cast's panels alone.
        ///
        /// <para><b>Why the body cannot simply be re-assembled.</b> <see cref="Assemble"/> renumbers the
        /// sections it writes, but the model's own shot list carries the locks too: the system prompt has it
        /// write "the frame is exactly &lt;Picture 2&gt; without reinterpretation" at each keyframe timestamp.
        /// Numbered for a picture list that starts with the cast instead, that sentence points at a studio
        /// photograph and asks for it as a frame — the one failure this tab exists to prevent, aimed at a
        /// 768px face crop.</para>
        ///
        /// <para>So every sentence naming a picture goes. Sentence granularity, because that is the unit the
        /// lock is written in: a shot line reads "[Shot 2] At 00:03.000, a hard cut. The frame is exactly
        /// &lt;Picture 2&gt; without reinterpretation. The camera then pushes in." and only the middle
        /// sentence is about the still. A <c>[Shot n]</c> marker that went with it is put back, so the shot
        /// list keeps its shape.</para>
        /// </summary>
        public static string DropPictureLocks(string? body)
        {
            var t = Normalize(body);
            if (t.Length == 0 || !PictureRegex.IsMatch(t)) return t;

            var lines = new List<string>();
            foreach (var line in t.Split('\n'))
            {
                if (!PictureRegex.IsMatch(line)) { lines.Add(line); continue; }

                var marker = Regex.Match(line, @"^\s*\[Shot\s+\d+\]", RegexOptions.IgnoreCase);
                var kept = Regex.Split(line, @"(?<=[.!?])\s+")
                                .Where(s => !PictureRegex.IsMatch(s))
                                .Select(s => s.Trim())
                                .Where(s => s.Length > 0)
                                .ToList();

                var rebuilt = string.Join(" ", kept);
                if (marker.Success && !rebuilt.StartsWith("[Shot", StringComparison.OrdinalIgnoreCase))
                    rebuilt = $"{marker.Value.Trim()} {rebuilt}".Trim();

                // A tag that survived sat in a fragment with no sentence end. It still must not name a
                // picture, so it loses the tag rather than the fragment.
                rebuilt = PictureRegex.Replace(rebuilt, "that shot's own framing");
                if (rebuilt.Length > 0) lines.Add(rebuilt);
            }

            return string.Join("\n", lines).Trim();
        }

        /// <summary>
        /// Drops the one line a code-written lead opens, wherever in the section it sits.
        ///
        /// <para>Line granularity, not paragraph: each of the three leads writes exactly one line, and
        /// these sections separate their parts with single newlines rather than blank ones — the shot list
        /// is a run of consecutive <c>[Shot n]</c> lines, and <see cref="Assemble"/> joins the summary's
        /// marker to the model's own paragraph the same way. Splitting on blank lines here would take the
        /// model's text out with the lead, which is exactly what it used to do.</para>
        /// </summary>
        private static string DropLineStartingWith(string section, string lead) =>
            string.Join("\n", Normalize(section).Split('\n')
                .Where(l => !l.TrimStart().StartsWith(lead, StringComparison.OrdinalIgnoreCase)))
                .Trim();

        #endregion

        #region Reading

        /// <summary>Matches a section label sitting on a line of its own, however the model decorated it.</summary>
        private static readonly Regex SectionLabelRegex = new(
            @"^[ \t]*[#*\-]*[ \t]*(subject_definitions|summary|retention_analysis|detailed_description|overall_soundscape|non_diegetic_music)[ \t]*:",
            RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Splits a prompt into its labelled sections. Anything ahead of the first label is dropped — with a
        /// model that reliably means a "here is your prompt:" preamble.
        /// </summary>
        public static IReadOnlyDictionary<string, string> SplitSections(string? prompt)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var t = Normalize(prompt);
            if (t.Length == 0) return result;

            var matches = SectionLabelRegex.Matches(t);
            for (var i = 0; i < matches.Count; i++)
            {
                var label = matches[i].Groups[1].Value.ToLowerInvariant();
                var start = matches[i].Index + matches[i].Length;
                var end = i + 1 < matches.Count ? matches[i + 1].Index : t.Length;
                var content = t[start..end].Trim();
                // A model that emits a label twice gets its blocks concatenated rather than one silently lost.
                result[label] = result.TryGetValue(label, out var existing) && existing.Length > 0
                    ? existing + "\n" + content
                    : content;
            }
            return result;
        }

        private static string Get(IReadOnlyDictionary<string, string> sections, string label) =>
            sections.TryGetValue(label, out var v) ? v : string.Empty;

        /// <summary>The sections a prompt is missing, for the tab's own warning line.</summary>
        public static IReadOnlyList<string> MissingSections(string? prompt)
        {
            var sections = SplitSections(prompt);
            return AllSections.Where(s => !sections.ContainsKey(s) || sections[s].Trim().Length == 0).ToList();
        }

        private static readonly Regex SubjectRegex =
            new(@"<\s*Subject\s+(\d+)\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex PictureRegex =
            new(@"<\s*Picture\s+(\d+)\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Whether the body casts a given subject — what decides, per clip of a chain, whose
        /// reference photographs are uploaded at all.</summary>
        private static bool MentionsSubject(string prompt, int subject) =>
            SubjectRegex.Matches(prompt).Any(m => m.Groups[1].Value == subject.ToString(CultureInfo.InvariantCulture));

        /// <summary>
        /// Whether an assembled prompt casts character 2. Read back at submit time so a clip is only ever
        /// sent the references it uses — a face H3 is told to keep is a face it will find a place for.
        /// </summary>
        public static bool IncludesCharacter2(string? prompt, bool hasCharacter2) =>
            hasCharacter2 && MentionsSubject(prompt ?? string.Empty, 2);

        /// <summary>
        /// The highest <c>&lt;Picture n&gt;</c> the body names. Compared against the keyframe count so a body
        /// written for three stills and then re-stamped with two can be reported rather than silently
        /// pointing at a cast photograph.
        /// </summary>
        public static int HighestPictureReference(string? prompt) =>
            PictureRegex.Matches(prompt ?? string.Empty)
                        .Select(m => int.TryParse(m.Groups[1].Value, out var n) ? n : 0)
                        .DefaultIfEmpty(0)
                        .Max();

        private static string Normalize(string? text) =>
            (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();

        #endregion

        #region Reading a prompt back

        private static readonly Regex ShotMarkerRegex =
            new(@"^\[\s*Shot\s*\d+\s*\]\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex LeadingTimestampRegex =
            new(@"^(?:At\s+)?\d{1,2}:\d{2}(?:[.:]\d{1,3})?\s*,?\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

        private static readonly Regex NonWordRegex = new(@"[^a-z0-9]+", RegexOptions.Compiled);

        /// <summary>
        /// One plain line saying what this clip actually does — the model's own <c>summary</c> sentence with
        /// the code-written lead dropped, the cast read back as "Character n", and the whole thing collapsed
        /// onto a single line.
        ///
        /// <para><b>Why the queue needs this.</b> The row used to bind straight to <c>Prompt</c>, which by
        /// then is an assembled six-section prompt: a wardrobe lock, four subject definitions, four retention
        /// lines and an alignment paragraph, all of it identical in every clip of a chain, ahead of the one
        /// sentence that differs. Thirty lines of boilerplate per row, and the beat — the only thing that
        /// distinguishes clip 9 from clip 3 — buried in the middle of it. Worse, it made the 2026-08-18
        /// duplicate-clip loop invisible: the wall of text looked the same for every row because most of it
        /// genuinely was.</para>
        /// </summary>
        /// <param name="maxLength">Trimmed at the last word boundary before this, with an ellipsis.</param>
        public static string ActionSummary(string? prompt, int maxLength = 150)
        {
            var sections = SplitSections(Strip(prompt));

            // summary first: it is the beat in one sentence, which is exactly the question the row asks.
            // The shot list is the fallback, and its first line still opens on the action.
            var text = FirstMeaningfulLine(Get(sections, Summary));
            if (text.Length == 0) text = FirstMeaningfulLine(Get(sections, DetailedDescription));
            if (text.Length == 0)
            {
                // Not an assembled prompt at all — a hand-typed body, or a legacy queue item. Show its
                // opening rather than nothing.
                text = FirstMeaningfulLine(Normalize(prompt));
                if (text.Length == 0) return string.Empty;
            }

            text = ShotMarkerRegex.Replace(text, string.Empty);
            text = LeadingTimestampRegex.Replace(text, string.Empty);
            text = SubjectRegex.Replace(text, m => "Character " + m.Groups[1].Value);
            text = PictureRegex.Replace(text, m => "Picture " + m.Groups[1].Value);
            text = WhitespaceRegex.Replace(text, " ").Trim();

            return Ellipsize(text, maxLength);
        }

        /// <summary>First line that carries content, skipping labels and the code-written leads.</summary>
        private static string FirstMeaningfulLine(string section)
        {
            foreach (var raw in Normalize(section).Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith(SummaryMarker, StringComparison.OrdinalIgnoreCase)) continue;
                if (line.StartsWith(AlignmentLead, StringComparison.OrdinalIgnoreCase)) continue;
                if (line.StartsWith(GlobalRulesLead, StringComparison.OrdinalIgnoreCase)) continue;
                if (line.StartsWith(WardrobeLockPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                return line;
            }
            return string.Empty;
        }

        private static string Ellipsize(string text, int maxLength)
        {
            if (maxLength <= 1 || text.Length <= maxLength) return text;
            var cut = text.LastIndexOf(' ', Math.Min(maxLength, text.Length - 1));
            if (cut < maxLength / 2) cut = maxLength;
            return text[..cut].TrimEnd(' ', ',', ';', '.', '—', '-') + "…";
        }

        /// <summary>
        /// A clip reduced to a comparison key: the model's own four sections, lowercased with everything
        /// that is not a letter or a digit removed.
        ///
        /// <para><b>Why <see cref="Strip"/> first.</b> The code-written sections — wardrobe lock, subject
        /// definitions, retention analysis, alignment paragraph — are *designed* to be byte-identical in
        /// every clip of a chain, and on a 15-clip chain they are the bulk of the text. Comparing assembled
        /// prompts would put two entirely different beats at 90% similar and find nothing. What is left
        /// after stripping is only what the model wrote, so an exact match there means the model really did
        /// hand back the same clip twice.</para>
        /// </summary>
        public static string Fingerprint(string? prompt) =>
            NonWordRegex.Replace(Strip(prompt).ToLowerInvariant(), string.Empty);

        #endregion
    }
}
