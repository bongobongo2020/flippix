using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FlipPix.UI.Services
{
    /// <summary>
    /// Writes — and unwrites — the preamble the 🪪👥 H3 Cast tab puts in front of every prompt it sends to
    /// MiniMax H3: the sheet reference line and the wardrobe lock.
    ///
    /// <para><b>What the preamble is for.</b> The cast reaches H3 as reference photographs, one per view of
    /// each character's sheet. Several photographs of one person read as several <i>people</i> unless the
    /// prompt says otherwise, and the sheet's studio clothing has nothing to do with the video, so both facts
    /// have to be stated in words. They are stated <i>here</i>, in code, rather than left to the model that
    /// writes the body — a story's clips are written as independent blocks by a model that cannot see its own
    /// earlier output, so this preamble is the only text in a chain that is identical in every clip by
    /// construction, and therefore the only place an outfit can be held still.</para>
    ///
    /// <para><b>Three forms of the same prompt.</b> The model writes bodies in the <i>canonical</i> form,
    /// naming two characters as <c>&lt;Picture 1&gt;</c> and <c>&lt;Picture 2&gt;</c>. Stamping produces the
    /// <i>tagged</i> form, where each panel has a stable alias (<c>@char1_face</c>) —
    /// <c>MiniMaxH3TaggedReferenceToVideo</c> resolves those per clip and assigns the real picture numbers
    /// itself. <see cref="Detag"/> produces the <i>numbered</i> form for a ComfyUI whose
    /// MiniMaxH3-Contex-Loop predates tagged references. All three are the same words; only the reference
    /// tokens differ, which is why the fallback is a token substitution and not a second code path.</para>
    ///
    /// <para><b>The invariant everything here rests on:</b> <see cref="Strip"/> ∘ <see cref="Apply"/> is the
    /// identity on canonical bodies, and <see cref="Apply"/> ∘ <see cref="Strip"/> is idempotent. Analyze
    /// stamps a chain, the user edits the wardrobe box, Add to Queue re-stamps the same chain — none of that
    /// may accumulate preambles or lose the cast. It is why <see cref="Strip"/> collapses <i>any</i> picture
    /// number above 1 to character 2: once a character occupies several reference slots the old numbering
    /// cannot be recovered, so it is discarded rather than remembered.</para>
    /// </summary>
    public static class CastPromptStamp
    {
        /// <summary>Every reference line starts with this, so an existing one can be found and rewritten.</summary>
        public const string ReferenceLinePrefix = "For the target video,";

        /// <summary>
        /// Opens the code-written wardrobe block. Also how an existing block is found and replaced — see
        /// <see cref="StripWardrobeBlock"/>.
        /// </summary>
        public const string WardrobeLockPrefix = "WARDROBE LOCK";

        /// <summary>
        /// The sentence the wardrobe block opens with. It has to outrank the prompt body, because the body is
        /// written per clip and will drift: whatever the model says three paragraphs down, this line is the
        /// same in clip 1 and clip 8, so it is the only description in the prompt that can hold an outfit
        /// still.
        /// </summary>
        public const string WardrobeLockHeader =
            WardrobeLockPrefix + " — this is the authoritative wardrobe for the whole video and it is identical " +
            "in every clip. Dress the cast in exactly these clothes, unchanged from the first frame to the last, " +
            "and ignore any other clothing wording anywhere below; nobody changes, adds, removes or restyles a " +
            "garment unless this block says so:";

        /// <summary>The H3 field the body proper begins at, used to find where the preamble ends.</summary>
        private const string BodyAnchor = "integrated_multimodal_description:";

        #region Cast aliases

        /// <summary>Matches a picture reference in a prompt body, whatever number it carries.</summary>
        private static readonly Regex PictureTagRegex =
            new(@"<\s*Picture\s+(\d+)\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Matches one of this tab's cast aliases, whatever view it names: <c>@char2_face</c>, <c>@char1</c>.
        /// Anchored on a non-word boundary at the front so an e-mail address or a stray <c>@</c> in the prose
        /// cannot be mistaken for one.
        /// </summary>
        public static readonly Regex CastTagRegex =
            new(@"(?<![\w@])@char(\d+)(?:_(front|back|face|v\d+))?\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// The aliases character <paramref name="character"/>'s panels are registered under, in panel order.
        ///
        /// <para>Three panels are named for the views the sheet builder is asked for, because a name a
        /// language model can read is a name it can keep straight; any other count falls back to
        /// <c>_v1…_vN</c>. Index 0 is the character's <i>primary</i> alias — the one the prompt body uses
        /// wherever it just means "this person".</para>
        /// </summary>
        public static IReadOnlyList<string> Aliases(int character, int panels)
        {
            if (panels <= 1) return new[] { $"char{character}" };
            if (panels == 3)
                return new[] { $"char{character}_front", $"char{character}_back", $"char{character}_face" };
            return Enumerable.Range(1, panels).Select(v => $"char{character}_v{v}").ToArray();
        }

        /// <summary>Every alias of a cast, character 1's first — the order the references are wired in.</summary>
        public static IReadOnlyList<string> AllAliases(int panels1, int panels2) =>
            Aliases(1, Math.Max(1, panels1))
                .Concat(panels2 > 0 ? Aliases(2, panels2) : Enumerable.Empty<string>())
                .ToList();

        /// <summary>An alias as it appears in a prompt.</summary>
        public static string Token(string alias) => "@" + alias;

        /// <summary>One character's aliases as a readable list, for the log and the reference line.</summary>
        public static string DescribeAliases(int character, int panels)
        {
            var tags = Aliases(character, Math.Max(1, panels)).Select(Token).ToList();
            return tags.Count == 1
                ? tags[0]
                : string.Join(", ", tags.Take(tags.Count - 1)) + " and " + tags[^1];
        }

        /// <summary>
        /// Whether a stamped prompt actually casts the second character — read back at submit time so a clip
        /// is only ever sent the references it uses. Character 1 is not asked about: <see cref="Apply"/> names
        /// them in every reference line it writes, and a job with no character 1 does not exist.
        ///
        /// <para>A prompt stamped before this tab used aliases carries none, and reports its cast through
        /// plain picture numbers instead — where <c>&lt;Picture 2&gt;</c> may mean either character 2 or
        /// character 1's second panel. That ambiguity is why such a prompt is answered <paramref
        /// name="hasCharacter2"/>: it is the reading that can only ever send one reference too many, never one
        /// too few.</para>
        /// </summary>
        public static bool IncludesCharacter2(string? prompt, bool hasCharacter2)
        {
            var tags = CastTagRegex.Matches(prompt ?? string.Empty);
            if (tags.Count == 0) return hasCharacter2;
            return hasCharacter2 && tags.Any(m => m.Groups[1].Value != "1");
        }

        /// <summary>True when a prompt is in the tagged form and can drive the tagged reference nodes.</summary>
        public static bool IsTagged(string? prompt) => CastTagRegex.IsMatch(prompt ?? string.Empty);

        /// <summary>
        /// Turns a stamped, tagged prompt back into fixed picture numbers, for a ComfyUI whose
        /// MiniMaxH3-Contex-Loop is too old to have the tagged reference nodes. Slots are counted over the
        /// characters actually present, matching the order the references are wired in.
        /// </summary>
        public static string Detag(string prompt, int panels1, int panels2)
        {
            var slot = 1;
            foreach (var alias in AllAliases(panels1, panels2))
                prompt = prompt.Replace(Token(alias), $"<Picture {slot++}>", StringComparison.OrdinalIgnoreCase);
            return prompt;
        }

        /// <summary>
        /// Rewrites a canonical body so each character is named by their primary alias. Only the body is
        /// touched — the reference line names every panel alias, and it is that mention which activates the
        /// panels for the clip.
        /// </summary>
        private static string Retag(string body, int panels1, int panels2)
        {
            body = body.Replace("<Picture 1>", Token(Aliases(1, Math.Max(1, panels1))[0]), StringComparison.Ordinal);
            return panels2 > 0
                ? body.Replace("<Picture 2>", Token(Aliases(2, panels2)[0]), StringComparison.Ordinal)
                : body;
        }

        /// <summary>
        /// Puts a body into the one form everything here is written against: two characters, named
        /// <c>&lt;Picture 1&gt;</c> and <c>&lt;Picture 2&gt;</c>. Both the numbered slots of an already-stamped
        /// prompt and the aliases of a tagged one collapse to it.
        /// </summary>
        private static string Canonicalize(string body)
        {
            body = CastTagRegex.Replace(body, m => m.Groups[1].Value == "1" ? "<Picture 1>" : "<Picture 2>");
            return PictureTagRegex.Replace(body, m => m.Groups[1].Value == "1" ? "<Picture 1>" : "<Picture 2>");
        }

        #endregion

        #region Stamping

        /// <summary>
        /// Removes the code-written preamble — reference line and wardrobe block — and returns the body in
        /// canonical form, ready to be stamped again for a different cast, wardrobe or panel split.
        /// </summary>
        public static string Strip(string? prompt)
        {
            var t = (prompt ?? string.Empty).Trim();
            if (!t.StartsWith(ReferenceLinePrefix, StringComparison.OrdinalIgnoreCase))
                return Canonicalize(StripWardrobeBlock(t).Trim());

            var idx = t.IndexOf(BodyAnchor, StringComparison.OrdinalIgnoreCase);
            if (idx > 0) return Canonicalize(t[idx..].Trim());

            // No H3 field to anchor on (a hand-written prompt, or a model that dropped the labels): fall back
            // to dropping the reference line's own paragraph, then whatever wardrobe block follows it.
            var nl = t.IndexOf('\n');
            return nl > 0 ? Canonicalize(StripWardrobeBlock(t[(nl + 1)..].Trim()).Trim()) : string.Empty;
        }

        /// <summary>
        /// Stamps one clip: the reference line for the cast wired into it, the wardrobe lock, then the body.
        ///
        /// <para>The cast and the wardrobe are passed in rather than read off the tab, because a queued item's
        /// preamble must describe the cast and wardrobe it was queued with, not whichever happen to be loaded
        /// when the item eventually runs.</para>
        ///
        /// <para><paramref name="selectiveCast"/> drops a character the clip never names — references,
        /// wardrobe line and all. Pass it only for a story chain, where clips genuinely take turns; a lone clip
        /// keeps whoever is loaded, because loading a character you did not want in your one clip is not a
        /// thing anyone does, whereas a model forgetting to name someone in beat 6 of 12 certainly is.</para>
        /// </summary>
        public static string Apply(
            string? prompt, int panels1, int panels2, string? wardrobe, bool selectiveCast = false)
        {
            var body = Strip(prompt);
            if (body.Length == 0) return string.Empty;

            // Read off the canonical body, so it does not matter which form the prompt arrived in.
            if (selectiveCast && !body.Contains("<Picture 2>", StringComparison.Ordinal)) panels2 = 0;

            var sb = new StringBuilder(BuildReferenceLine(panels1, panels2));
            // The wardrobe box is kept in the canonical form the user typed and the derive pass wrote, so its
            // "Character 2 (<Picture 2>)" lines are re-tagged here alongside the body — otherwise the block
            // would dress a reference that belongs to character 1.
            var wardrobeLock = Retag(
                DropAbsentWardrobeLines(BuildWardrobeLock(wardrobe), panels2 > 0), panels1, panels2);
            if (wardrobeLock.Length > 0) sb.Append("\n\n").Append(wardrobeLock);
            return sb.Append("\n\n").Append(Retag(body, panels1, panels2)).ToString();
        }

        /// <summary>
        /// Writes the reference line for a cast cut into <paramref name="panels1"/> and
        /// <paramref name="panels2"/> separate references (<paramref name="panels2"/> is 0 when character 2 is
        /// not in this clip).
        ///
        /// <para>Its job is to undo, in words, the one thing splitting the sheet cannot: several pictures of
        /// one person read as several <i>people</i> unless they are explicitly grouped. So it says which
        /// pictures belong to whom, that they are the same individual from different angles, and — the part
        /// this whole mechanism exists for — that these are reference photographs rather than frames, so their
        /// backdrops, their standing poses and any side-by-side arrangement of them must not appear in the
        /// video. It also disowns the clothing, because the wardrobe is decided separately and stamped in
        /// below.</para>
        ///
        /// <para>Naming every panel alias here is not merely description: with the tagged reference nodes, a
        /// registered picture is sent to H3 <i>only</i> when its alias occurs in the clip's prompt. This line
        /// is the mention that activates the cast, which is why a clip character 2 is not in does not name
        /// them.</para>
        /// </summary>
        private static string BuildReferenceLine(int panels1, int panels2)
        {
            var total = Math.Max(1, panels1) + Math.Max(0, panels2);

            var sb = new StringBuilder(ReferenceLinePrefix).Append(' ');
            sb.Append(total == 1
                ? "the attached picture is a studio reference photograph of the cast, not a frame of the video. "
                : $"the {total} attached pictures are separate studio reference photographs of the cast, not frames of the video. ");

            sb.Append(DescribeCharacter(1, Math.Max(1, panels1)));
            if (panels2 > 0)
                sb.Append(' ').Append(DescribeCharacter(2, panels2));

            var them = panels2 > 0 ? "each of them" : "them";
            var each = panels2 > 0 ? "each person's" : "that person's";
            var they = total > 1 ? "them" : "it";
            var own = total > 1 ? "their own pictures" : "that picture";

            sb.Append($" Take ONLY {each} identity from {own} — face, facial features, hair, skin " +
                      $"and build — and keep {them} identical and unchanged from the first frame to the last. ");
            sb.Append("These references are NOT the scene: never show the same person more than once in a frame, " +
                      "never line the cast up side by side against a plain backdrop, and do not copy the " +
                      "references' plain background, their neutral standing pose, or any panel, grid or " +
                      "split-screen layout into the video. ");
            sb.Append($"Do NOT dress the cast from {they} either: place {them} in the scene described below and " +
                      $"dress {them} strictly in the outfit written there, unchanged throughout.");
            return sb.ToString();
        }

        /// <summary>Names the pictures one character occupies, and insists they are one person.</summary>
        private static string DescribeCharacter(int character, int panels)
        {
            var list = DescribeAliases(character, panels);
            if (panels <= 1)
                return $"{list} is Character {character} — a reference sheet showing one and " +
                       "the same person from several angles, not several different people.";

            return $"{list} are {panels} separate photographs of one and the same person — Character " +
                   $"{character} — shot from different angles (full-body front, full-body back, face close-up); " +
                   $"they are one individual, not {panels} different people.";
        }

        #endregion

        #region Wardrobe block

        /// <summary>
        /// The wardrobe as it appears inside a prompt — the header plus the block, or nothing at all when
        /// there is no wardrobe to lock.
        /// </summary>
        public static string BuildWardrobeLock(string? wardrobe)
        {
            // Blank lines are squeezed out because a blank line is what ends the block: a hand-typed wardrobe
            // with a paragraph break would otherwise leave its tail behind when the block is replaced.
            var body = Regex.Replace((wardrobe ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim(),
                                     @"\n[ \t]*\n+", "\n");
            return body.Length == 0 ? string.Empty : $"{WardrobeLockHeader}\n{body}";
        }

        /// <summary>
        /// Removes a leading wardrobe block so a new one can replace it — the block runs from its header to
        /// the first blank line, which is exactly how <see cref="BuildWardrobeLock"/> writes it. Without this,
        /// re-queueing a chain that Analyze already stamped would stack a second block on top of the first.
        /// </summary>
        public static string StripWardrobeBlock(string? text)
        {
            var t = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').TrimStart();
            if (!t.StartsWith(WardrobeLockPrefix, StringComparison.OrdinalIgnoreCase)) return t;

            var end = t.IndexOf("\n\n", StringComparison.Ordinal);
            return end < 0 ? string.Empty : t[(end + 2)..].TrimStart();
        }

        /// <summary>
        /// Removes character 2's line from a wardrobe block for a clip they are not in. Dressing someone who
        /// is not there is an invitation to put them there.
        /// </summary>
        private static string DropAbsentWardrobeLines(string block, bool keepCharacter2)
        {
            if (keepCharacter2 || block.Length == 0) return block;

            var kept = block.Split('\n')
                            .Where(line => !Regex.IsMatch(line, @"^\s*Character\s+(?!1\b)\d+\b",
                                                          RegexOptions.IgnoreCase))
                            .ToList();
            // A block that is one unparseable paragraph has no per-character lines to drop; keep it whole
            // rather than returning a header with nothing under it.
            return kept.Count > 1 ? string.Join("\n", kept) : block;
        }

        #endregion
    }
}
