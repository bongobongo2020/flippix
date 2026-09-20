using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// The 📐 Singularity spec build's half of the clip writer: its request, its check, and the passes that turn
    /// a reply into a six-section body. What the build is, and why, is on <see cref="H3SpecPrompt"/>.
    ///
    /// <para>Everything before the clip calls — the wardrobe, the beat sheet, the continuity plan — and
    /// everything after the bodies — the stamp, the join, the library — is the ordinary flow in
    /// <see cref="AnalyzeAsync"/>. Only the three-field passes are swapped: those match
    /// <c>integrated_multimodal_description:</c>, which a spec clip does not have.</para>
    /// </summary>
    public partial class H3ExperimentalViewModel
    {
        /// <summary>Whether the clips are written to the Singularity spec. Off on every tab but ⚡ H3 Express,
        /// whose checkbox this is.</summary>
        protected virtual bool SpecPromptBuild => false;

        private int SpecCastCount => HasCharacter2 ? 2 : 1;

        /// <summary>The cast as the code-written sections name them, each with their line of the locked
        /// wardrobe.</summary>
        private List<H3SpecPrompt.CastMember> SpecCast()
        {
            var cast = new List<H3SpecPrompt.CastMember>
            {
                new(1, CastDescriptor.SexOf(1) ?? "person", CastPromptStamp.OutfitFor(CastWardrobe, 1)),
            };
            if (HasCharacter2)
                cast.Add(new(2, CastDescriptor.SexOf(2) ?? "person", CastPromptStamp.OutfitFor(CastWardrobe, 2)));
            return cast;
        }

        /// <summary>Raw reply → the writer's four sections, labels canonical and on lines of their own, and every
        /// character tag a <c>&lt;Subject N&gt;</c>.</summary>
        private string NormalizeSpecClipBody(string raw) =>
            H3SpecPrompt.RenderWriterSections(H3SpecPrompt.ToSubjectTags(NormalizeClipBody(raw)));

        /// <summary>Whether this clip must speak its beat's quoted lines. A beat split across clips speaks them in
        /// its last part only, or every part would say them again.</summary>
        private static bool SpeaksBeatLines(StoryBeatSheet.StoryBeat beat) =>
            H3SpecPrompt.BeatHasLines(beat.Text) && beat.Part == beat.PartCount;

        /// <summary>The spec's own structure, cast and dialogue checks, then the same hour-and-light check the
        /// other builds get.</summary>
        private string? ValidateSpecClip(
            string body, StoryContinuity.Environment environment, IReadOnlyList<StoryBeatSheet.StoryBeat> beats, int index) =>
            H3SpecPrompt.Validate(body, SpecCastCount, index < beats.Count && SpeaksBeatLines(beats[index]))
            ?? StoryContinuity.Contradiction(body, environment);

        /// <summary>
        /// One clip's user message for the spec build. The same context as the other builds' request — style,
        /// setting, continuity, cast, wardrobe, the neighbouring beats — with the cast named as subjects, the
        /// spec's per-clip rules in place of the researched block, the shot the previous clip actually ended on,
        /// and the reason for a retry said out loud.
        /// </summary>
        /// <param name="previousBody">The previous clip as its writer returned it, or null for the first clip and
        /// for a clip whose predecessor came back empty.</param>
        private string BuildSpecClipRequest(
            string setting, IReadOnlyList<StoryBeatSheet.StoryBeat> beats,
            IReadOnlyList<StoryContinuity.Environment> environments, int index, int clipCount,
            double seconds, int shots, string rejection, string? previousBody)
        {
            var beat = beats[index];
            var environment = EnvironmentFor(environments, index);
            var previousEnvironment = index > 0 ? EnvironmentFor(environments, index - 1)
                                                : (StoryContinuity.Environment?)null;
            var lastClip = index + 1 >= beats.Count;

            var wardrobe = HasCastWardrobe
                ? "WARDROBE — already decided, not yours to choose, and already written into subject_definitions:. " +
                  "Each line opens 'Character N (<Subject N>, …) wears …'; attach the garments after that prefix to " +
                  "that subject's tag the first time they appear in detailed_description: — '<Subject N>, wearing " +
                  "<those garments>,' — in exactly these words. This is the only clothing wording you may use; where " +
                  "the beat describes clothing differently, this wins:\n" +
                  H3SpecPrompt.ToSubjectTags(CastWardrobe.Trim())
                : "WARDROBE — none was decided. Write each subject's outfit out once in full at their first " +
                  "appearance, and keep that wording for the rest of the clip.";

            var location = setting.Length > 0
                ? $"SETTING — the same story world in every clip of this chain, restated inside [Shot 1]:\n{setting}"
                : "SETTING — read it off the beat below, and restate it inside [Shot 1].";
            var continuity = StoryContinuity.WriterBlock(environment, previousEnvironment);
            if (continuity.Length > 0) location += "\n\n" + continuity;

            var handoff = index > 0 ? H3SpecPrompt.Handoff(previousBody, SpecCastCount) : string.Empty;
            var previous = index > 0
                ? "THE CLIP BEFORE THIS ONE played this beat — do NOT play it again:\n" + beats[index - 1].Text +
                  "\n\n" + (handoff.Length > 0
                      ? handoff
                      : "Its shots are not available, so open [Shot 1] on the state that beat leaves the subjects in.")
                : "This is the chain's FIRST clip: it opens the film, already in motion.";

            var next = !lastClip
                ? "THE CLIP AFTER THIS ONE will play this — do NOT reach into it; end this clip mid-action, " +
                  $"on its way there:\n{beats[index + 1].Text}"
                : "This is the chain's LAST clip: the story's final moment lands inside it.";

            var lines = !H3SpecPrompt.BeatHasLines(beat.Text)
                ? string.Empty
                : SpeaksBeatLines(beat)
                    ? " Speak every quoted line of it in this clip, word for word."
                    : $" Its quoted lines are spoken in part {beat.PartCount} of {beat.PartCount}, not in this one.";

            var s = seconds.ToString("0.##", CultureInfo.InvariantCulture);
            var retry = rejection.Length > 0
                ? $"YOUR PREVIOUS REPLY WAS REJECTED: {rejection}\nWrite the clip again from the beat, fixing that.\n\n"
                : string.Empty;

            return
                "Mode: reference generation. The cast reaches H3 as studio reference photographs, never as frames " +
                "of this video. subject_definitions: and retention_analysis: for this clip are written by code and " +
                "bind each subject to their photographs; you write the other four sections. Write no alignment or " +
                "anchor line, and begin with summary:.\n\n" +
                H3VisualStyles.Rule(VisualStyle) + "\n" +
                $"{location}\n\n" +
                H3SpecPrompt.CastBlock(SpecCast()) + "\n\n" +
                $"{wardrobe}\n\n" +
                H3SpecPrompt.RulesFor(SpecCastCount, seconds, shots, setting,
                                      hasContinuityPlan: !environment.IsEmpty, lastClip: lastClip) + "\n\n" +
                $"THIS IS CLIP {index + 1} OF {clipCount}. It is {s} seconds long.\n\n" +
                $"{previous}\n\n" +
                ChainedOpening(index) +
                $"THIS CLIP'S BEAT — direct it and fill the whole {s} seconds with it. Invent the blow-by-blow " +
                "choreography inside it, but add no character, place or outcome it does not have:\n" +
                $"{beat.Text}{StoryBeatSheet.DescribePart(beat)}{lines}\n\n" +
                $"{next}\n\n" +
                retry +
                "Reply with summary:, detailed_description:, overall_soundscape: and non_diegetic_music:, each label " +
                "on a line of its own, and nothing else.";
        }

        /// <summary>
        /// The written replies → six-section clip bodies. Per section: digits folded and a runaway cut back to its
        /// last sentence, as the other builds get per field. The shots get the same timestamp repair and the same
        /// fold of timestamp-less markers. Then the code sections go around them.
        ///
        /// <para>A clip with no shots left to render is dropped, as before; one missing a sound section is kept
        /// and said.</para>
        /// </summary>
        private List<string> FinishSpecClips(
            IReadOnlyList<string> replies, IReadOnlyList<StoryContinuity.Environment> environments,
            string setting, double seconds)
        {
            var cast = SpecCast();
            var retention = H3SpecPrompt.BuildRetentionAnalysis(cast);
            var bodies = new List<string>();
            var dropped = new List<int>();
            var quiet = new List<string>();

            for (var i = 0; i < replies.Count; i++)
            {
                var clipNumber = i + 1;
                var sections = H3SpecPrompt.Parse(replies[i]);
                var removed = 0;

                string Field(string label)
                {
                    var text = FoldDigits(sections[label]);
                    var cut = DegenerateCutIndex(text);
                    if (cut < 0) return text;
                    removed += text.Length - cut;
                    return text[..cut].TrimEnd();
                }

                var description = Field(H3SpecPrompt.DetailedDescription);
                var summary = Field(H3SpecPrompt.Summary);
                var soundscape = Field(H3SpecPrompt.OverallSoundscape);
                var music = Field(H3SpecPrompt.NonDiegeticMusic);

                if (removed > 0)
                    AddLog($"WARNING: clip {clipNumber} degenerated — {removed:N0} characters of unpunctuated " +
                           "word-salad cut back to the last complete sentence in the section(s) that ran away.");

                if (description.Length == 0)
                {
                    dropped.Add(clipNumber);
                    continue;
                }

                description = NormalizeTimestamps(description, seconds);
                description = FoldShots(description, clipNumber, "\n\n") ?? description;

                var missing = new List<string>();
                if (soundscape.Length == 0) missing.Add("overall_soundscape");
                if (music.Length == 0) missing.Add("non_diegetic_music");
                if (missing.Count > 0) quiet.Add($"{clipNumber} (no {string.Join(", no ", missing)})");

                bodies.Add(H3SpecPrompt.Assemble(
                    H3SpecPrompt.BuildSubjectDefinitions(cast, EnvironmentFor(environments, i), setting),
                    H3SpecPrompt.WithSummaryMarker(summary),
                    retention,
                    description,
                    soundscape,
                    music));
            }

            if (quiet.Count > 0)
                AddLog($"Note: clip(s) {string.Join("; ", quiet)} came back without a sound section. They are kept " +
                       "and queued — the shots are written in full; a missing score is sent as N/A.");

            if (dropped.Count > 0)
                AddLog($"WARNING: clip(s) {string.Join(", ", dropped)} came back with no detailed_description to " +
                       "render and were dropped. Re-run Analyze, or write those clips into the prompt box by hand.");

            return bodies;
        }
    }
}
