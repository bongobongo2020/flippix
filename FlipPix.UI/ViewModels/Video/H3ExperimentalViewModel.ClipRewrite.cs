using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// One clip of an already-written story, written again to a line of direction — what the 🔄 button under
    /// each clip in 📚 Story Prompts calls.
    ///
    /// <para><b>Why it is not the clip writer.</b> <see cref="AnalyzeAsync"/> writes a chain from a beat sheet
    /// and a continuity plan, and neither survives the run that produced them: a saved story is its clips and
    /// nothing else. Re-deriving the beats to rewrite clip 7 would rewrite what clips 1–6 and 8–N were written
    /// against, which is the opposite of what the button is for. So the neighbours stand in for the plan — the
    /// clip before it says where this clip opens, the clip after it says where it must not reach, and the clip
    /// itself carries the place, the hour and the light that may not move.</para>
    ///
    /// <para><b>The build is the story's, not the tab's.</b> A set written to the 📐 Singularity spec is
    /// rewritten to it whatever the checkbox says today, because the rewritten clip has to sit between its
    /// neighbours in the same shape. A spec clip's <c>subject_definitions:</c> and <c>retention_analysis:</c>
    /// are written by code and are kept verbatim: they bind the subjects to their photographs and carry the
    /// place, and the writer is asked for the other four sections only.</para>
    /// </summary>
    public partial class H3ExperimentalViewModel
    {
        /// <summary>A rewrite is one clip, so it gets the per-clip ceiling the chain writer uses.</summary>
        private const int RewriteMaxTokens = 3000;

        /// <summary>
        /// Writes one clip again so that it plays <see cref="ClipRewriteRequest.Direction"/>, and returns the
        /// new body in the shape the library stores — no reference line, no wardrobe lock. Two attempts, the
        /// second told what was wrong with the first, as in the chain writer.
        /// </summary>
        /// <exception cref="InvalidOperationException">No llama-server model, or nothing renderable came back
        /// from either attempt. The message is meant to be shown to the user as it is.</exception>
        protected async Task<string> RewriteClipAsync(ClipRewriteRequest request, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(request.Direction))
                throw new InvalidOperationException("There is no direction to write this clip to.");

            var spec = IsSpecRewrite(request);
            var researched = !spec && !string.Equals(request.PromptBuild, "shipped", StringComparison.Ordinal);
            var castCount = RewriteCastCount(request);
            var seconds = request.Seconds > 0 ? request.Seconds : ClampLength(LengthSeconds);
            // The clip's own cut plan, so the rewrite drops into the chain at the same length and rhythm; the
            // build's own pacing only when the body carries no shots to count.
            var shots = Math.Max(1, CountShots(request.Body));
            if (shots <= 1)
                shots = spec ? H3SpecPrompt.ShotCount(seconds)
                      : researched ? H3ResearchPrompt.ShotCount(seconds) : ShippedShotCount(seconds);

            var model = await ResolveLlmModelAsync(token)
                        ?? throw new InvalidOperationException(
                            "No llama-server model is available — start the server and try again.");

            var system = await ReadSystemPromptAsync(
                spec ? H3SpecPrompt.ClipSystemPromptFile
                     : researched ? H3ResearchPrompt.ClipSystemPromptFile : ClipSystemPromptFile, token);

            AddLog($"📚 Rewriting clip {request.Index + 1} of \"{request.Title}\" to your direction " +
                   $"({(spec ? "Singularity spec" : researched ? "researched" : "shipped")} build, {seconds:0.#}s, " +
                   $"{shots} shots) — via {_lmStudioService.DescribeTarget(model)}");

            var best = string.Empty;
            var rejection = string.Empty;

            for (var attempt = 1; attempt <= 2; attempt++)
            {
                token.ThrowIfCancellationRequested();

                var raw = await _lmStudioService.SendTextChatAsync(
                    model, system,
                    BuildRewriteRequest(request, spec, researched, castCount, seconds, shots, rejection),
                    maxTokens: RewriteMaxTokens,
                    cancellationToken: token,
                    sampling: LlmSampling.StoryChainFormatted);

                var body = spec
                    ? FinishSpecRewrite(request, NormalizeSpecClipBody(raw ?? string.Empty))
                    : FinishRewrite(request, raw ?? string.Empty, seconds);

                var complaint = spec
                    ? H3SpecPrompt.Validate(H3SpecPrompt.RenderWriterSections(body), castCount)
                    : ValidateRewrite(body, castCount);

                if (complaint == null)
                {
                    AddLog($"📚 Clip {request.Index + 1} rewritten ({body.Length:N0} chars, {CountShots(body)} shots)." +
                           " It is in the editor — nothing is saved until 💾 Save.");
                    return body;
                }

                if (body.Length > 0) best = body;
                rejection = complaint;
                AddLog(attempt == 1
                    ? $"📚 Clip {request.Index + 1} came back wrong ({complaint}) — writing it once more."
                    : $"📚 Clip {request.Index + 1}: the second attempt was wrong too ({complaint}).");
            }

            if (best.Length == 0)
                throw new InvalidOperationException(
                    "The writer returned nothing renderable for this clip. The clip in the editor is unchanged.");

            AddLog($"📚 Clip {request.Index + 1}: the rewrite is kept despite the warning above — read it before saving.");
            return best;
        }

        /// <summary>Whether this clip is rewritten in the 📐 spec build: what the set was written in, and — for a
        /// set that recorded no build — what the clip itself looks like.</summary>
        private static bool IsSpecRewrite(ClipRewriteRequest request) =>
            request.PromptBuild == H3SpecPrompt.BuildTag ||
            request.PromptBuild == H3SpecPrompt.EarlierBuildTag ||
            (request.PromptBuild.Length == 0 && H3SpecPrompt.IsSpecBody(request.Body));

        /// <summary>How many characters this story's clips are written for — what the set recorded, and failing
        /// that whichever tags the clips actually carry.</summary>
        private static int RewriteCastCount(ClipRewriteRequest request)
        {
            var recorded = request.CastNouns?.Count ?? 0;
            var tagged = request.Body.Contains("<Picture 2>", StringComparison.Ordinal) ||
                         request.Body.Contains("<Subject 2>", StringComparison.Ordinal) ||
                         (request.Previous ?? string.Empty).Contains("<Picture 2>", StringComparison.Ordinal) ||
                         (request.Previous ?? string.Empty).Contains("<Subject 2>", StringComparison.Ordinal)
                ? 2 : 1;
            return Math.Clamp(Math.Max(recorded, tagged), 1, 2);
        }

        private static string NounFor(ClipRewriteRequest request, int character)
        {
            var nouns = request.CastNouns;
            return nouns != null && nouns.Count >= character && !string.IsNullOrWhiteSpace(nouns[character - 1])
                ? nouns[character - 1].Trim()
                : "person";
        }

        // ── The request ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The user message for one rewrite: the same fixed context a clip call gets — cast, wardrobe, the
        /// build's rules, the shot plan — with the neighbours in place of the beats, the clip as it stands, and
        /// the direction it is being rewritten to.
        /// </summary>
        private string BuildRewriteRequest(
            ClipRewriteRequest request, bool spec, bool researched, int castCount, double seconds, int shots,
            string rejection)
        {
            var lastClip = request.Index + 1 >= request.ClipCount;
            var s = seconds.ToString("0.##", CultureInfo.InvariantCulture);

            var cast = spec
                ? H3SpecPrompt.CastBlock(Enumerable.Range(1, castCount)
                    .Select(n => new H3SpecPrompt.CastMember(
                        n, NounFor(request, n), CastPromptStamp.OutfitFor(request.Wardrobe, n)))
                    .ToList())
                : castCount > 1
                    ? "CAST — two reference photographs are attached to this clip. <Picture 1> is CHARACTER 1 " +
                      $"(a {NounFor(request, 1)}); <Picture 2> is CHARACTER 2 (a {NounFor(request, 2)}). Keep the " +
                      "numbers exactly as the clip below uses them, and name BOTH by their tags."
                    : "CAST — one reference photograph is attached to this clip. <Picture 1> is CHARACTER 1 " +
                      $"(a {NounFor(request, 1)}).";

            var tag = spec ? "<Subject N>" : "<Picture N>";
            var wardrobe = request.Wardrobe.Trim().Length > 0
                ? "WARDROBE — already decided, unchanged by this rewrite, and worn by the same people in every " +
                  "other clip of this film. Each line opens 'Character N … wears …'; attach the garments after " +
                  $"that prefix to that character's {tag} tag the first time they appear, in exactly these words:\n" +
                  (spec ? H3SpecPrompt.ToSubjectTags(request.Wardrobe.Trim()) : request.Wardrobe.Trim())
                : "WARDROBE — none was decided for this film. Keep the clothing wording the clip below already " +
                  "uses, word for word.";

            // The place, the hour and the light are the clip's, not the direction's: the clips around it were
            // written in them and H3 renders each clip having never seen the others.
            var scene = SceneOf(request.Body, spec);
            var place = scene.Length > 0
                ? "WHERE AND WHEN — this clip's place, hour and light, unchanged by the rewrite and identical in " +
                  $"the clips around it:\n{scene}"
                : "WHERE AND WHEN — keep the place, the hour and the light exactly as the clip below has them. " +
                  "The clips around it were written in them.";

            var rules = spec
                ? H3SpecPrompt.RulesFor(castCount, seconds, shots, setting: null, hasContinuityPlan: true, lastClip: lastClip)
                : researched
                    ? H3ResearchPrompt.RulesFor(request.Index, castCount > 1, seconds, shots, setting: null,
                                                hasContinuityPlan: true)
                    : $"THIS CLIP CARRIES ABOUT {shots} SHOTS. Every timestamp after [Shot 1] falls inside " +
                      $"00:00.000–{Timecode(seconds)}.";

            // The spec build's own handoff where it can be read; otherwise the clip's closing prose, which says
            // the same thing in the shape the other builds write.
            var handoff = spec ? H3SpecPrompt.Handoff(request.Previous, castCount) : string.Empty;
            var ending = Tail(EndOf(request.Previous, spec), 700);
            var previous =
                request.Index == 0 ? "This is the film's FIRST clip: it opens the video, already in motion."
                : handoff.Length > 0 ? handoff
                : ending.Length > 0
                    ? "THE CLIP BEFORE THIS ONE ENDS LIKE THIS — the film cuts straight from it into this clip, " +
                      "which opens on that same moment from a new angle and never restarts the action:\n" + ending
                    : "The clip before this one is not available: open [Shot 1] mid-action, on the state the clip " +
                      "below opens in.";

            var opening = Head(OpeningOf(request.Next, spec), 700);
            var next =
                lastClip ? "This is the film's LAST clip: the story's final moment lands inside it."
                : opening.Length > 0
                    ? "THE CLIP AFTER THIS ONE PLAYS THIS — do NOT reach into it; end this clip mid-action, on " +
                      "its way there:\n" + opening
                    : "End this clip mid-action: another clip follows it.";

            var current = spec ? H3SpecPrompt.RenderWriterSections(request.Body) : request.Body;

            var retry = rejection.Length > 0
                ? $"YOUR PREVIOUS REPLY WAS REJECTED: {rejection}\nWrite the clip again, fixing that.\n\n"
                : string.Empty;

            var reply = spec
                ? "Reply with summary:, detailed_description:, overall_soundscape: and non_diegetic_music:, each " +
                  "label on a line of its own, and nothing else."
                : "Reply with the three fields and nothing else.";

            return
                "Mode: " + (spec ? "reference generation" : "character-reference video") + ". The attached pictures " +
                "are studio reference photographs of the cast — plain backdrop, neutral pose, shot for identity " +
                "alone. They are NOT frames of this video and the viewer never sees them. Write no alignment or " +
                "anchor line.\n\n" +
                "YOU ARE REWRITING ONE CLIP of a film whose other clips are already written and are not changing. " +
                "It has to drop into their place: the same cast, the same clothes, the same location, the same " +
                $"length, the same shape.\n\n" +
                $"{place}\n\n" +
                $"{cast}\n\n" +
                $"{wardrobe}\n\n" +
                $"{rules}\n\n" +
                $"THIS IS CLIP {request.Index + 1} OF {request.ClipCount}. It is {s} seconds long.\n\n" +
                $"{previous}\n\n" +
                "THE CLIP AS IT STANDS — this is what it plays now, and everything in it that the direction below " +
                $"does not change stays as it is:\n{current}\n\n" +
                "THE DIRECTION — rewrite the clip so that it plays this, in the user's own words. Follow it " +
                "closely: it is the action this clip is now for. Fill the whole " + s + " seconds with it, " +
                "choreographed blow by blow, and add no character, place or outcome it does not ask for:\n" +
                request.Direction.Trim() + "\n\n" +
                $"{next}\n\n" +
                retry +
                reply;
        }

        // ── The reply → a body the library can store ────────────────────────────────────────────────

        /// <summary>The 📐 spec rewrite: the writer's four sections, with the clip's own code-written
        /// <c>subject_definitions:</c> and <c>retention_analysis:</c> put back around them untouched.</summary>
        private string FinishSpecRewrite(ClipRewriteRequest request, string reply)
        {
            var written = H3SpecPrompt.Parse(reply);
            var original = H3SpecPrompt.Parse(request.Body);

            string Field(string label)
            {
                var text = FoldDigits(written[label]);
                var cut = DegenerateCutIndex(text);
                return cut < 0 ? text : text[..cut].TrimEnd();
            }

            var description = Field(H3SpecPrompt.DetailedDescription);
            if (description.Length == 0) return string.Empty;

            description = NormalizeTimestamps(description, request.Seconds > 0 ? request.Seconds : ClampLength(LengthSeconds));
            description = FoldShots(description, request.Index + 1, "\n\n") ?? description;

            return H3SpecPrompt.Assemble(
                original[H3SpecPrompt.SubjectDefinitions],
                H3SpecPrompt.WithSummaryMarker(Field(H3SpecPrompt.Summary)),
                original[H3SpecPrompt.RetentionAnalysis],
                description,
                Field(H3SpecPrompt.OverallSoundscape),
                Field(H3SpecPrompt.NonDiegeticMusic));
        }

        /// <summary>The three-field rewrite: the chain writer's own repair passes, then the scene sentence the
        /// clip was carrying written back in — a clip rewritten on its own has no continuity plan to derive one
        /// from, and the clips around it all say these exact words.</summary>
        private string FinishRewrite(ClipRewriteRequest request, string reply, double seconds)
        {
            var body = SanitizeClipFields(CanonicalizeFieldLabels(NormalizeClipBody(reply)), request.Index + 1);
            if (!HasFieldContent(body, ClipFieldLabels[0])) return string.Empty;

            body = NormalizeShots(NormalizeTimestamps(FoldDigits(body), seconds), request.Index + 1);
            return StoryContinuity.StampSceneText(body, StoryContinuity.SceneIn(request.Body));
        }

        /// <summary>What makes a three-field rewrite renderable — the same two checks the chain writer makes,
        /// minus the hour-and-light one: the scene sentence is written back in from the clip it replaces, so
        /// there is no plan here for the prose to contradict.</summary>
        private static string? ValidateRewrite(string body, int castCount)
        {
            if (body.Length == 0)
                return "it carried no integrated_multimodal_description to render. Reply with the three fields " +
                       "and nothing else, starting with that label.";

            if (castCount > 1 && !NamesBothFighters(body))
                return "it did not name both characters by their tags. Every character in the clip appears as " +
                       "<Picture 1> or <Picture 2> — at their first appearance and wherever they are struck, " +
                       "grabbed, named or reacted to. Write it again.";

            return null;
        }

        // ── Reading one clip for the next one's request ─────────────────────────────────────────────

        /// <summary>The place, hour and light a clip carries: the spec build's <c>subject_definitions:</c>
        /// scene line, or the sentence <see cref="StoryContinuity.StampScene"/> wrote into the others.</summary>
        private static string SceneOf(string body, bool spec)
        {
            if (!spec) return StoryContinuity.SceneIn(body);

            var definitions = H3SpecPrompt.Parse(body)[H3SpecPrompt.SubjectDefinitions];
            if (definitions.Length == 0) return string.Empty;

            // The place is the definitions' <Environment> line — the one line there that is not a subject.
            var lines = definitions.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            var place = lines.LastOrDefault(l => l.StartsWith("<Environment>", StringComparison.OrdinalIgnoreCase));
            if (place != null)
            {
                var colon = place.IndexOf(':');
                return colon >= 0 ? place[(colon + 1)..].Trim() : place;
            }
            return lines.Count > 0 && !lines[^1].StartsWith("<Subject", StringComparison.OrdinalIgnoreCase)
                ? lines[^1]
                : string.Empty;
        }

        /// <summary>A clip's closing moment, for the clip that cuts out of it.</summary>
        private static string EndOf(string? body, bool spec) =>
            spec ? H3SpecPrompt.LastShot(body) : DescriptionOf(body);

        /// <summary>A clip's opening, for the clip that must not reach into it.</summary>
        private static string OpeningOf(string? body, bool spec)
        {
            if (!spec) return DescriptionOf(body);
            var sections = H3SpecPrompt.Parse(body);
            var summary = sections[H3SpecPrompt.Summary];
            // The mode marker is for H3, not for a writer reading what the next clip plays.
            if (summary.StartsWith(H3SpecPrompt.SummaryMarker, StringComparison.OrdinalIgnoreCase))
                summary = summary[H3SpecPrompt.SummaryMarker.Length..].Trim();
            return summary.Length > 0 ? summary : sections[H3SpecPrompt.DetailedDescription];
        }

        /// <summary>The rendered prose of a three-field clip, with the scene stamp taken off — it is said
        /// separately, and saying it twice reads as a second place.</summary>
        private static string DescriptionOf(string? body)
        {
            var text = CastPromptStamp.ExtractDescription(body ?? string.Empty);
            if (text.Length == 0) text = (body ?? string.Empty).Trim();
            return Regex.Replace(StoryContinuity.StripScene(text), @"\s+", " ").Trim();
        }

        private static string Head(string text, int chars) =>
            text.Length <= chars ? text : text[..chars].TrimEnd() + "…";

        private static string Tail(string text, int chars) =>
            text.Length <= chars ? text : "…" + text[^chars..].TrimStart();

        private static string Timecode(double seconds)
        {
            var whole = (int)Math.Floor(seconds);
            var millis = (int)Math.Round((seconds - whole) * 1000);
            return $"{whole / 60:00}:{whole % 60:00}.{millis:000}";
        }
    }
}
