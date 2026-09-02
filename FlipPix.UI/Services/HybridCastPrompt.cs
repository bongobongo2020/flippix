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

        /// <summary>The three views <c>h3-charsheet-2511.md</c> builds a person's sheet from, in panel
        /// order.</summary>
        public const string ViewFront = "full-body front";
        public const string ViewBack = "full-body back";
        public const string ViewFace = "face close-up";

        /// <summary>The same three panels for a subject that is not a person. A cloud's sheet has a front,
        /// a reverse and a close-up of whatever makes it recognisable — calling that third panel a "face
        /// close-up" in the prompt is an instruction to find a face in it.</summary>
        public const string ViewWholeFront = "whole subject from the front";
        public const string ViewWholeBack = "whole subject from the reverse side";
        public const string ViewDetail = "close-up of its most recognisable part";

        /// <summary>And the same three for a group, where the close-up is of some of them rather than of a
        /// face — a herd has no single face, and asking for one gets a single animal.</summary>
        public const string ViewGroupFront = "the whole group from the front";
        public const string ViewGroupBack = "the whole group from behind";
        public const string ViewGroupDetail = "closer view of two or three of them";

        /// <summary>
        /// One member of the cast as the prompt needs them: which subject they are, the word to call them
        /// by, and what each of their reference pictures shows.
        ///
        /// <para>The views are carried rather than derived from the count, because the tab does not
        /// necessarily send every panel it cut. Dropping the back view leaves two pictures that are
        /// <i>front and face</i>, not "view 1 and view 2" — and a face picture the prompt fails to call a
        /// face is a picture H3 has no reason to weigh when the camera is close.</para>
        /// </summary>
        /// <param name="IsPerson">False for a cast member who is not a human being — a cloud, a mountain,
        /// a herd. Every sentence this file would otherwise write about their face, hair, skin, build and
        /// age is wrong for them, and "the same adult" is the wrongest of all: it is an instruction to put a
        /// person on screen. Defaults true, so the two-hander tabs are untouched.</param>
        /// <param name="Descriptor">What to call them when the tag is not enough — "a man", or, for a
        /// non-person, what they actually are ("Nimbus, a fluffy little cloud"). Falls back to
        /// <c>a {Noun}</c>.</param>
        /// <param name="IsGroup">Several of them acting as one character — a herd, a village, a flock.
        /// Orthogonal to <paramref name="IsPerson"/>: a village of travellers is both.</param>
        public sealed record CastMember(int Index, string Noun, IReadOnlyList<string> Views,
                                        bool IsPerson = true, string? Descriptor = null,
                                        bool IsGroup = false)
        {
            /// <summary>How many reference slots this character occupies.</summary>
            public int Panels => Math.Max(1, Views.Count);

            /// <summary>The words the prose uses for them: their own description, or "a man".</summary>
            public string Describe =>
                string.IsNullOrWhiteSpace(Descriptor) ? $"a {Noun}" : Descriptor!.Trim();

            /// <summary>"it" / "they" — how the code-written prose refers back to a non-person.</summary>
            public string It => IsGroup ? "they" : "it";

            /// <summary>"its" / "their".</summary>
            public string Its => IsGroup ? "their" : "its";

            /// <summary>What kind of thing the retention lines call them.</summary>
            public string Kindword => IsGroup ? "group" : IsPerson ? "person" : "character";

            /// <summary>Whichever picture is the closest view, 0-based — the face close-up for a person (the
            /// one the likeness comes from when the camera is close, and the identity reference the
            /// face-refine pass tracks by), or the detail panel for anything else.</summary>
            public int FacePanel
            {
                get
                {
                    var i = Views.ToList().FindIndex(
                        v => string.Equals(v, ViewFace, StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(v, ViewDetail, StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(v, ViewGroupDetail, StringComparison.OrdinalIgnoreCase));
                    return i >= 0 ? i : Math.Max(0, Views.Count - 1);
                }
            }
        }

        /// <summary>
        /// What a sheet cut into <paramref name="panels"/> pieces shows, when nothing better is known — a
        /// three-panel sheet is the one the tabs build, and everything else is described positionally rather
        /// than guessed at.
        /// </summary>
        /// <param name="isPerson">False swaps the person vocabulary for the one a cloud's sheet actually
        /// shows. It matters: these words go into the prompt verbatim, and a panel the prompt calls a face
        /// close-up is a panel H3 will look for a face in.</param>
        public static IReadOnlyList<string> DefaultViews(int panels, bool isPerson = true,
                                                         bool isGroup = false) => panels switch
        {
            <= 1 => new[] { "full character sheet" },
            3 => isGroup
                ? new[] { ViewGroupFront, ViewGroupBack, ViewGroupDetail }
                : isPerson
                    ? new[] { ViewFront, ViewBack, ViewFace }
                    : new[] { ViewWholeFront, ViewWholeBack, ViewDetail },
            _ => Enumerable.Range(1, panels).Select(i => $"view {i}").ToList(),
        };

        #endregion

        #region Numbering

        /// <summary>Total pictures a run wires: the keyframes, then every cast panel.</summary>
        public static int PictureCount(int keyframes, IReadOnlyList<CastMember> cast) =>
            keyframes + cast.Sum(c => Math.Max(1, c.Panels));

        /// <summary>
        /// The 1-based number of the <b>environment</b> reference — the photograph of the location the video
        /// is set in. It is wired <i>last</i>, after every cast panel, so that adding or dropping one is the
        /// only renumbering it can cause: a clip that drops a character it never names would otherwise move
        /// the location's number as well, and the location is named in three of the code-written sections.
        /// </summary>
        public static int EnvironmentPicture(int keyframes, IReadOnlyList<CastMember> cast) =>
            PictureCount(keyframes, cast) + 1;

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
        private static string PictureList(IEnumerable<int> numbers) =>
            JoinWords(numbers.Select(Picture));

        /// <summary>"A", "A and B", "A, B and C" — the list form the prose sections read in. Written out
        /// because a five-hander joined with " and " everywhere reads as one run-on name.</summary>
        private static string JoinWords(IEnumerable<string> parts)
        {
            var list = parts.ToList();
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
        /// <param name="focusSubject">A subject index to write the prompt for <b>one</b> member of the cast —
        /// the face-refine pass, which regenerates one tracked face at a time and must not be shown the other
        /// characters' photographs while it does. 0 (the default) keeps the whole cast.</param>
        /// <param name="environment">True when a photograph of the <b>location</b> is attached as the last
        /// picture. It is a reference like the cast's, not a frame: it carries the set, its materials, its
        /// palette and its light, and anybody visible in it is scenery rather than casting. Forced off when
        /// <paramref name="focusSubject"/> is set — a 768px face crop has no location in it to keep.</param>
        public static string Assemble(
            string? body,
            IReadOnlyList<Keyframe> keyframes,
            IReadOnlyList<CastMember> cast,
            string? wardrobe,
            double clipSeconds,
            string medium,
            bool sheetsShowWardrobe = false,
            bool selectiveCast = false,
            int focusSubject = 0,
            bool environment = false)
        {
            var stripped = Strip(body);
            if (stripped.Length == 0) return string.Empty;

            var sections = SplitSections(stripped);
            if (focusSubject > 0)
            {
                cast = cast.Where(c => c.Index == focusSubject).ToList();
                // This pass renders a 768px crop of one face. There is no set inside it to hold.
                environment = false;
            }
            else if (selectiveCast && cast.Count > 1)
            {
                // Everybody the body does not name is dropped. On an ensemble that is the point — nine
                // reference slots shared by whoever a clip casts — and on a two-hander it is what it always
                // did. The fallback matters: a body naming nobody at all is a model slip, and a clip with no
                // cast is a clip with no references, which is a clip with nothing holding a face still.
                var named = cast.Where(c => MentionsSubject(stripped, c.Index)).ToList();
                cast = named.Count > 0 ? named : new List<CastMember> { cast[0] };
            }
            if (cast.Count == 0) return string.Empty;

            var keys = keyframes.OrderBy(k => k.Seconds).ToList();
            var sb = new StringBuilder();

            var wardrobeLock = CastPromptStamp.BuildWardrobeLock(wardrobe, sheetsShowWardrobe);
            if (wardrobeLock.Length > 0) sb.Append(DropAbsentCast(wardrobeLock, cast)).Append('\n');

            Section(sb, SubjectDefinitions,
                BuildSubjectDefinitions(keys, cast, sheetsShowWardrobe, focusSubject, environment));
            Section(sb, Summary, BuildSummary(keys, cast, clipSeconds, medium, Get(sections, Summary), environment));
            Section(sb, RetentionAnalysis,
                BuildRetentionAnalysis(keys, cast, clipSeconds, sheetsShowWardrobe, environment));
            Section(sb, DetailedDescription,
                BuildAlignment(keys, cast, clipSeconds, environment) + "\n" +
                BuildGlobalRules(keys, cast, medium, environment) +
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
            int focusSubject = 0, bool environment = false)
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

            foreach (var member in cast)
            {
                var pictures = CastPictures(keys.Count, cast, member.Index);
                var views = string.Join(", ", member.Views.Take(pictures.Count));

                // What the pictures are for. A non-person has no face, hair, skin or build to keep, and
                // saying otherwise is an instruction to grow them one.
                var keep = member.IsPerson
                    ? sheetsShowWardrobe
                        ? "identity and wardrobe appearance only — face, facial features, hair, skin, build " +
                          "and the exact garments shown"
                        : "identity only — face, facial features, hair, skin and build; the studio clothing " +
                          "they show is irrelevant to this video"
                    : sheetsShowWardrobe
                        ? "identity and appearance only — its shape, silhouette, proportions, colours, " +
                          "materials, surface and markings, and the exact items shown on it"
                        : "identity only — its shape, silhouette, proportions, colours, materials and " +
                          "markings; the studio background it sits on is irrelevant to this video";

                lines.Add($"{PictureList(pictures)} {(pictures.Count == 1 ? "is a" : "are")} studio reference " +
                          $"photograph{(pictures.Count == 1 ? "" : "s")} of {Subject(member.Index)} — " +
                          $"{views}, in that order. Job: {keep}. Not a person-as-scene. Not a pose. Not a " +
                          $"background. Not a timeline keyframe. Never insert " +
                          $"{(pictures.Count == 1 ? "it" : "any of them")} as a video frame.");

                var same = member.Kindword;
                var shown = pictures.Count == 1
                    ? $"shown in {PictureList(pictures)}"
                    : $"shown in {PictureList(pictures)} — those are one and the same {same} from several " +
                      $"angles, not {pictures.Count} different " +
                      $"{(member.IsGroup ? "groups" : member.IsPerson ? "people" : "characters")}";
                var face = Picture(pictures[Math.Min(member.FacePanel, pictures.Count - 1)]);

                if (member.IsGroup)
                {
                    // A crowd is neither "the same adult" nor a thing. What has to hold is that it is the
                    // same several — the same number, the same look — and, when they are people, that their
                    // faces are still faces rather than a smear of extras.
                    lines.Add($"{Subject(member.Index)} is {member.Describe} — several individuals acting " +
                              $"together as one character, {shown}. {char.ToUpperInvariant(member.It[0])}" +
                              $"{member.It[1..]} keep the same number, the same look and the same " +
                              $"{(member.IsPerson ? "clothing" : "form, colours and materials")} from the " +
                              $"first frame to the last, taken only from {member.Its} own pictures. Do not " +
                              "swap them for a different set, do not reduce them to a single individual, and " +
                              "do not let them become a faceless crowd in the background." +
                              (member.IsPerson
                                  ? " They are people: give each of them a real, legible face rather than a " +
                                    "blur, and never repeat one of them twice in a frame."
                                  : " They are not people: do not give any of them a human face, human hair " +
                                    "or human hands, and do not replace them with people in costumes.") +
                              (keys.Count > 0
                                  ? " Where a keyframe shows them, that frame wins at its timestamp."
                                  : string.Empty));
                }
                else if (member.IsPerson)
                {
                    lines.Add($"{Subject(member.Index)} is the same adult, {member.Describe}, {shown}. Face " +
                              "and identity stay consistent from the first frame to the last, and come only " +
                              $"from their own pictures: the likeness in {face} is the one to match at every " +
                              "distance and through every camera move, including wide shots and fast motion " +
                              "where the face is small." +
                              (keys.Count > 0
                                  ? " Where a keyframe shows them, that frame's pose, wardrobe and background " +
                                    "win at its timestamp — their face does not."
                                  : string.Empty));
                }
                else
                {
                    // The three things a non-human character needs said and a person does not: it is a
                    // character rather than set dressing, what stays constant is its form rather than a
                    // face, and it does not turn into a person — which is the direction it drifts, because
                    // a person is what the model has most of.
                    lines.Add($"{Subject(member.Index)} is {member.Describe} — a character in this video, " +
                              $"not a person, {shown}. It acts: it moves, reacts and carries the beat it is " +
                              "in, and it is never reduced to scenery, a backdrop or a prop. Its shape, " +
                              "proportions, colours, materials and markings stay identical from the first " +
                              $"frame to the last and come only from its own pictures, {face} above all. Do " +
                              "not turn it into a person, do not give it a human face, human hair or human " +
                              "hands, and do not replace it with a person in a costume or a person-shaped " +
                              "mascot." +
                              (keys.Count > 0
                                  ? " Where a keyframe shows it, that frame wins at its timestamp."
                                  : string.Empty));
                }
            }

            if (environment)
            {
                var set = Picture(EnvironmentPicture(keys.Count, cast));
                lines.Add($"{set} is a photograph of the LOCATION this video is set in. Job: the set only — " +
                          "its layout, architecture, surfaces, materials, props, colour palette and the " +
                          "quality of its light. It is not a person and not a character sheet. Anybody " +
                          "visible in it is scenery and is not part of the cast: do not carry them into the " +
                          "video, and never take a face from it. Never insert it as a video frame.");
                lines.Add($"The scene is that place. Every shot happens inside it, or in a part of it the " +
                          $"camera has not shown yet, lit the way {set} is lit — the cast are put into that " +
                          "space, not photographed against it.");
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
            double clipSeconds, string medium, string modelSummary, bool environment = false)
        {
            var who = JoinWords(cast.Select(c => Subject(c.Index)));
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

            if (environment)
                sb.Append($" {Picture(EnvironmentPicture(keys.Count, cast))} is the location the whole clip " +
                          "is set in — a place reference, never a frame and never a person.");

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
            double clipSeconds, bool sheetsShowWardrobe, bool environment = false)
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

            foreach (var member in cast)
            {
                var what = member.IsPerson
                    ? sheetsShowWardrobe
                        ? "retain identity and the garment appearance shown"
                        : "retain facial identity, hair, skin and build only — not the studio clothing"
                    : sheetsShowWardrobe
                        ? "retain its identity, form and the appearance shown — shape, proportions, colours, " +
                          "materials and markings"
                        : "retain its shape, proportions, colours, materials and markings only — not the " +
                          "studio background";

                var pictures = CastPictures(keys.Count, cast, member.Index);
                lines.Add($"{PictureList(pictures)} (never keyframes): partially_preserved - {what}; do not " +
                          "reproduce a studio photograph as a video frame; do not copy its plain background, " +
                          "its neutral standing pose, or any panel, grid or split-screen layout.");
            }

            if (environment)
                lines.Add($"{Picture(EnvironmentPicture(keys.Count, cast))} (never a keyframe): " +
                          "partially_preserved - retain the location: its layout, architecture, surfaces, " +
                          "materials, props, colour palette and light, in every shot. Do not reproduce it as " +
                          "a still frame, do not read it as a character sheet, and do not carry any person " +
                          "visible in it into the video.");

            foreach (var member in cast)
            {
                var shots = keys.Count > 0
                    ? string.Join(", ", Enumerable.Range(1, keys.Count).Select(i => $"[Shot {i}]"))
                    : "[Shot 1]";
                lines.Add($"{Subject(member.Index)} (appears in {shots}): fully_preserved - the same " +
                          $"{member.Kindword} throughout, at every shot size; identity taken only from " +
                          "their own pictures, and not weakened when the framing " +
                          (member.IsGroup
                              ? "is wide or the camera is moving; same number of them, never a generic crowd"
                              : member.IsPerson
                                  ? "is wide, the face is small or the camera is moving"
                                  : "is wide, it is small in frame or the camera is moving; never redrawn " +
                                    "as a person and never demoted to background scenery") +
                          (keys.Count > 0 ? "; each shot matches that shot's keyframe exactly." : "."));
            }

            return string.Join("\n", lines);
        }

        /// <summary>
        /// The first paragraph of section 4 — the alignment statement. This is where hybrid mode differs
        /// from plain R2V: the timestamps are named in prose rather than wired into a first/last-frame node.
        /// </summary>
        private static string BuildAlignment(
            IReadOnlyList<Keyframe> keys, IReadOnlyList<CastMember> cast, double clipSeconds,
            bool environment = false)
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

            if (environment)
            {
                var n = EnvironmentPicture(keys.Count, cast);
                sb.Append($"Picture {n} does not align with any timestamp as a frame either; it is the " +
                          "location the clip is set in, present in every shot as the set rather than as an " +
                          "image. ");
            }

            sb.Append($"There is no last-frame lock at {Seconds(clipSeconds)} seconds.");
            return sb.ToString();
        }

        /// <summary>The second paragraph of section 4 — the global negatives, restated per clip because a
        /// clip is rendered with no memory of the one before it.</summary>
        private static string BuildGlobalRules(
            IReadOnlyList<Keyframe> keys, IReadOnlyList<CastMember> cast, string medium,
            bool environment = false)
        {
            var sb = new StringBuilder($"{GlobalRulesLead}{medium}. No on-screen text. No extra people. ");
            sb.Append("Do not invent a new outfit. ");
            // The failure this sentence is aimed at: identity holds in close-ups and slips in the wide, fast
            // shots, where the face is a handful of pixels and the model has the least to hold on to.
            if (cast.Any(c => c.IsPerson))
                sb.Append("Keep every face on model at every shot size — a face that is far away, small in " +
                          "frame or moving fast is still the same person, and must not be re-cast, re-aged " +
                          "or generalised into a stock face. ");
            // The same failure for a cast member who has no face to lose: what drifts instead is their
            // form, and it drifts towards a person, because a person is what the model has most of.
            if (cast.Any(c => !c.IsPerson))
                sb.Append("The non-human characters keep their own form at every shot size — same shape, " +
                          "same proportions, same colours and materials, however small or fast they are in " +
                          "frame. None of them turns into a person, wears a human face, or is replaced by " +
                          "someone in a costume. ");
            if (keys.Count > 1) sb.Append("Do not blend rooms or wardrobes across cuts. ");

            var castPictures = cast.SelectMany(c => CastPictures(keys.Count, cast, c.Index)).ToList();
            if (castPictures.Count > 0)
                sb.Append($"Do not cut to {PictureList(castPictures)}, and never show the same character " +
                          "more than once in a frame or line the cast up side by side against a plain " +
                          "backdrop.");
            if (environment)
                sb.Append($" Do not cut to {Picture(EnvironmentPicture(keys.Count, cast))} as a still, and " +
                          "do not move the scene to another place partway through.");
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

            // Before anything reads the tags: a slipped bracket here costs a character their reference
            // photographs for the whole clip. See RepairTags.
            t = RepairTags(t);
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

        /// <summary>
        /// A subject or picture tag a model <i>meant</i> to write and did not quite: the opening bracket,
        /// an optional space, the word, the number, and then anything or nothing where the closing bracket
        /// belongs.
        ///
        /// <para>Observed on the sibling H3 Duo tab, whose tags fail the same way: from clip 4 of a 12-clip
        /// chain every tag arrived as <c>&lt;Picture 1</c> with no closing bracket, and by clip 10 as
        /// <c>&lt; Picture 2,</c> with a space after the bracket too. The consequence here is worse than
        /// cosmetic, because <see cref="MentionsSubject"/> uses <see cref="SubjectRegex"/> to decide whose
        /// photographs are uploaded: a clip whose only mention of a character was malformed is submitted
        /// <b>without that character's references at all</b>, and the generator invents somebody.</para>
        ///
        /// <para>The closing bracket and the space before it are consumed together or not at all, so an
        /// unclosed tag is not welded to the word after it. The opening <c>&lt;</c> is what makes the
        /// repair safe: prose does not contain one.</para>
        /// </summary>
        private static readonly Regex BrokenTagRegex =
            new(@"<\s*(Subject|Picture|Pictuer|Pic|Subj|S|P)\s*(\d{1,2})(?:\s*>)?",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Mends near-miss subject and picture tags so they resolve to references instead of being silently
        /// dropped. Idempotent, and a no-op on a clean body. Called from <see cref="Strip"/>, so every path
        /// into <see cref="Assemble"/> — Analyze, a re-stamp, Add to Queue, a prompt edited by hand — gets it.
        /// </summary>
        public static string RepairTags(string? body)
        {
            if (string.IsNullOrEmpty(body)) return body ?? string.Empty;
            return BrokenTagRegex.Replace(body, m =>
            {
                var word = m.Groups[1].Value;
                var subject = word.Equals("Subject", StringComparison.OrdinalIgnoreCase) ||
                              word.Equals("Subj", StringComparison.OrdinalIgnoreCase) ||
                              word.Equals("S", StringComparison.OrdinalIgnoreCase);
                return $"<{(subject ? "Subject" : "Picture")} {m.Groups[2].Value}>";
            });
        }

        /// <summary>How many tags <see cref="RepairTags"/> would mend — what a tab reports after writing.</summary>
        public static int CountBrokenTags(string? body)
        {
            if (string.IsNullOrEmpty(body)) return 0;
            var broken = 0;
            foreach (Match m in BrokenTagRegex.Matches(body))
            {
                var word = m.Groups[1].Value;
                var canonical = word.Equals("Subject", StringComparison.Ordinal) ? "Subject"
                              : word.Equals("Picture", StringComparison.Ordinal) ? "Picture"
                              : null;
                if (canonical == null || m.Value != $"<{canonical} {m.Groups[2].Value}>") broken++;
            }
            return broken;
        }

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
        /// Whether an assembled prompt casts a given subject. The N-character generalisation of
        /// <see cref="IncludesCharacter2"/>: with five slots a clip typically names two or three of them, and
        /// a character a clip never mentions is a character whose photographs it should not be shown — a face
        /// H3 is told to keep is a face it will find somewhere to put.
        /// </summary>
        public static bool IncludesSubject(string? prompt, int subject) =>
            MentionsSubject(prompt ?? string.Empty, subject);

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
