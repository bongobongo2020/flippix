using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FlipPix.UI.Services
{
    /// <summary>One story turned into everything the ⛓ FFLF page needs.</summary>
    public sealed record FflfStoryScript(
        string MasterPrompt,
        string Location,
        string Wardrobe,
        IReadOnlyList<string> Beats,
        IReadOnlyList<string> Dialogue)
    {
        public bool HasDialogue => Dialogue.Any(d => d.Length > 0);
    }

    /// <summary>
    /// The ✨ story engine of the ⛓ FFLF tab: any story, idea or script goes to the writer model (llama-server or
    /// DeepSeek) with <see cref="SystemPromptFile"/>, and the reply — a MASTER PROMPT, LOCATION, WARDROBE, BEATS and
    /// DIALOGUE sheet laid out like the Schopenhauer explainer in that file — is read back into the five parts the
    /// page has boxes for: style / direction, setting, wardrobe, the beats box, and each beat's spoken line.
    ///
    /// <para>The parser is forgiving about how the model dressed the headings (<c>[BEATS]</c>, <c>## BEATS</c>,
    /// <c>BEATS:</c>, ===== rules) and how it numbered beats and dialogue (<c>BEAT 3 —</c>, <c>3.</c>,
    /// <c>--- DIALOGUE 3 (Beat 3) ---</c>, <c>DIALOGUE 3: "…"</c>).</para>
    /// </summary>
    public static class FflfStoryEngine
    {
        /// <summary>Under prompts/prompt2json; editable without a rebuild.</summary>
        public const string SystemPromptFile = "fflf-story-engine.md";

        public const int MinBeats = 1;
        public const int MaxBeats = 20;

        /// <summary>Stories longer than this are cut before they are sent — local servers have small contexts.</summary>
        public const int MaxStoryChars = 24000;

        /// <summary>Speaking pace the dialogue is sized to: words per second of beat.</summary>
        public const double WordsPerSecond = 2.6;

        public static (int Target, int Max) DialogueWords(double beatSeconds) =>
            ((int)Math.Round(beatSeconds * WordsPerSecond), (int)Math.Round(beatSeconds * (WordsPerSecond + 0.3)));

        /// <summary>
        /// What the vision model is asked about the opening image before the writer runs. The writer never sees the
        /// picture itself, so this description is all it has to keep the cast, wardrobe and place on the frame the
        /// video actually starts on.
        /// </summary>
        public const string OpeningImagePrompt =
            "This picture is the exact first frame of a video. Describe it precisely so a writer who cannot see it can " +
            "continue the video from it without changing anything. Plain text, these five labelled parts, nothing else:\n" +
            "PEOPLE: one line per visible person, from left to right, called A, B, C, written exactly as\n" +
            "A | where they are in the frame (e.g. left, centre, right, foreground) | apparent gender and age range, build, " +
            "skin tone, face, hair (colour, length, style), expression\n" +
            "If nobody is visible, write: none\n" +
            "WARDROBE: one line per person, written exactly as\n" +
            "A | every visible garment top to bottom with colour, material and fit, footwear if visible, jewellery and accessories\n" +
            "POSE: for each person by letter, the exact pose right now: standing/sitting, which way the body and head " +
            "face, where the eyes look, where each hand and arm is, anything held.\n" +
            "PLACE: the setting, backdrop, surfaces, props, time of day, light (direction, quality, colour) and colour palette.\n" +
            "CAMERA: shot size (close-up, chest-up, waist-up, full body), angle, lens feel, depth of field, and the visual " +
            "style (photo-real, film still, illustration, anime, 3D render…).\n" +
            "Describe only what is visible. Do not guess names. Do not invent a story.";

        /// <summary>The user turn: the story, and how many beats of how long to cut it into.</summary>
        /// <param name="openingImage">The vision model's description of the opening image, when there is one; the
        /// sheet is then written around that exact picture.</param>
        public static string BuildRequest(string story, int beats, double beatSeconds, bool withDialogue, bool hasOpeningImage,
                                          string? openingImage = null)
        {
            openingImage = openingImage?.Trim();
            if (string.IsNullOrEmpty(openingImage)) openingImage = null;
            beats = Math.Clamp(beats, MinBeats, MaxBeats);
            var text = (story ?? string.Empty).Trim();
            if (text.Length > MaxStoryChars) text = text[..MaxStoryChars] + "\n[…story cut here for length…]";
            var (target, max) = DialogueWords(beatSeconds);
            var total = beats * beatSeconds;

            var sb = new StringBuilder();
            sb.AppendLine($"Turn the story below into a {beats}-beat video sheet.");
            sb.AppendLine($"- Exactly {beats} beats of {beatSeconds:0.#} seconds each — total runtime {total:0.#} seconds. " +
                          "Say that runtime in the MASTER PROMPT.");
            if (withDialogue)
                sb.AppendLine($"- Exactly {beats} dialogue blocks, one per beat, each about {target} words and never more than {max}, " +
                              "so it can be spoken at a natural pace inside its beat.");
            else
                sb.AppendLine("- No spoken lines: write the [DIALOGUE] section with every block as just \"\" (an empty quote).");
            if (openingImage != null)
            {
                sb.AppendLine("- The video starts EXACTLY on the opening picture described below — it is frame one of beat 1, and every beat continues from it. It is the ground truth for how everything looks:");
                sb.AppendLine("  • The cast is the people in the picture, with the letters given there. Cast the story's characters onto them (the story's main character is A unless the story clearly fits another person better). Add nobody who is not in the picture, unless the story needs them to walk in.");
                sb.AppendLine("  • [WARDROBE] describes what each person is wearing in the picture — the same garments, colours and hair. Do not restyle them, even if the story describes other clothes.");
                sb.AppendLine("  • [LOCATION] describes the place and light in the picture. The whole video stays there, even if the story is set somewhere else — tell the story from this place.");
                sb.AppendLine("  • [MASTER PROMPT] keeps the picture's framing, camera angle and visual style, and describes the cast as they look in it.");
                sb.AppendLine("  • BEAT 1 starts from the exact pose in the picture and moves on from it — no jump to a different pose, position or framing.");
                sb.AppendLine("  • Where the story's own descriptions of people or places agree with the picture, use them; where they disagree on looks, the picture wins. Story facts, names, events and lines still come from the story.");
                sb.AppendLine();
                sb.AppendLine("THE OPENING PICTURE (frame one of the video):");
                sb.AppendLine("<<<");
                sb.AppendLine(openingImage);
                sb.AppendLine(">>>");
            }
            else if (hasOpeningImage)
            {
                sb.AppendLine("- The video starts on an existing opening picture of the main character (no description of it is available): keep the cast and place simple and consistent with a single continuous shot that could start on it.");
            }
            else
            {
                sb.AppendLine("- No opening picture exists yet: describe the main character and place precisely enough to create one. Where the story describes how its characters or places look, use those descriptions.");
            }
            sb.AppendLine();
            sb.AppendLine("THE STORY:");
            sb.AppendLine("<<<");
            sb.AppendLine(text);
            sb.AppendLine(">>>");
            sb.AppendLine();
            if (openingImage != null)
                sb.AppendLine("Remember: the people, clothes, place, light and framing are the opening picture's, and beat 1 starts from its pose.");
            sb.Append("Reply with the sheet only: the five sections [MASTER PROMPT], [LOCATION], [WARDROBE], [BEATS] and [DIALOGUE], in that order, nothing before or after.");
            return sb.ToString();
        }

        /// <summary>A generous token budget for the reply.</summary>
        public static int MaxTokensFor(int beats) => 1400 + Math.Clamp(beats, MinBeats, MaxBeats) * 320;

        // ── Reading the reply ────────────────────────────────────────────────────────────────────

        private enum Section { None, Master, Location, Wardrobe, Beats, Dialogue }

        // A heading line: the section name, alone on its line, in brackets, after #'s, or followed by a colon.
        private static readonly Regex HeadingLine = new(
            @"^[ \t]*(?:#{1,6}[ \t]*)?[\[*_]*[ \t]*(?<name>MASTER(?:[ \t]+(?:VIDEO[ \t]+)?PROMPT)?|LOCATION(?:[ \t]+PROMPT)?|SETTING(?:[ \t]+PROMPT)?|WARDROBE(?:[ \t]+PROMPT)?|BEATS|DIALOGUE|DIALOGUES)[ \t]*[\]*_]*[ \t]*:?[ \t]*$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly Regex RuleLine = new(@"^[ \t]*[=\-_*~#]{3,}[ \t]*$", RegexOptions.Multiline | RegexOptions.Compiled);

        // A note in brackets the model copies from the example at the top of a section, e.g. "(Separate prompt — use
        // independently of beats)" or "(15 seconds each. Actions refer to "A". …)", which can wrap over lines.
        private static readonly Regex LeadingNote = new(@"^\s*\([^()]{0,400}\)[ \t]*(?:\n|$)", RegexOptions.Compiled);

        // "BEAT 3 —", "Beat 3:", "3.", "3)", "- BEAT 3 -"
        private static readonly Regex BeatStart = new(
            @"^[ \t]*[-*#]*[ \t]*(?:BEAT[ \t]*(?<n>\d{1,2})|(?<n>\d{1,2})[.)])[ \t]*(?:\([^)\n]*\))?[ \t]*[—–:\-.)]*[ \t]*",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

        // "--- DIALOGUE 3 (Beat 3) ---", "DIALOGUE 3:", "Dialogue 3 —"
        private static readonly Regex DialogueStart = new(
            @"^[ \t]*[-*#=]*[ \t]*DIALOGUE[ \t]*(?<n>\d{1,2})[ \t]*(?:\([^)\n]*\))?[ \t]*[-—–:=*]*[ \t]*",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

        /// <summary>Reads a story sheet. Throws when it has no beats.</summary>
        public static FflfStoryScript Parse(string? reply)
        {
            var text = Regex.Replace(reply ?? string.Empty, @"<think>.*?</think>", string.Empty, RegexOptions.Singleline | RegexOptions.IgnoreCase);
            text = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("```", string.Empty);

            var sections = new Dictionary<Section, string>();
            var headings = HeadingLine.Matches(text).Cast<Match>().ToList();
            for (var i = 0; i < headings.Count; i++)
            {
                var kind = KindOf(headings[i].Groups["name"].Value);
                var start = headings[i].Index + headings[i].Length;
                var end = i + 1 < headings.Count ? headings[i + 1].Index : text.Length;
                var body = Tidy(text[start..end]);
                if (kind == Section.None || body.Length == 0) continue;
                sections[kind] = sections.TryGetValue(kind, out var had) ? had + "\n\n" + body : body;
            }

            // No headings at all: the whole reply is read as beats.
            if (headings.Count == 0) sections[Section.Beats] = Tidy(text);

            var beats = Numbered(sections.GetValueOrDefault(Section.Beats) ?? string.Empty, BeatStart)
                .Select(b => Collapse(StripBeatLabel(b))).Where(b => b.Length > 0).ToList();
            if (beats.Count == 0)
                throw new InvalidOperationException("the writer's reply had no beats in it.");

            var dialogueRaw = Numbered(sections.GetValueOrDefault(Section.Dialogue) ?? string.Empty, DialogueStart);
            var dialogue = beats.Select((_, i) => i < dialogueRaw.Count ? CleanLine(dialogueRaw[i]) : string.Empty).ToList();

            return new FflfStoryScript(
                sections.GetValueOrDefault(Section.Master) ?? string.Empty,
                sections.GetValueOrDefault(Section.Location) ?? string.Empty,
                sections.GetValueOrDefault(Section.Wardrobe) ?? string.Empty,
                beats, dialogue);
        }

        /// <summary>The beats as the beats box reads them: one numbered paragraph each. A beat the box would read as
        /// "Title: text" ("A visibly relaxes: shoulders drop…") has that colon turned into a dash, so no words are lost
        /// to a beat name.</summary>
        public static string ToBeatsText(IEnumerable<string> beats) =>
            string.Join("\n\n", beats.Select((b, i) => $"{i + 1}. {KeepWhole(b)}"));

        private static string KeepWhole(string beat)
        {
            var parsed = FflfGraph.ParseBeats(beat);
            if (parsed.Count != 1 || parsed[0].Title.Length == 0) return beat;
            var colon = beat.IndexOf(':');
            return colon < 0 ? beat : beat[..colon].TrimEnd() + " —" + beat[(colon + 1)..];
        }

        private static Section KindOf(string name)
        {
            var n = name.Trim().ToUpperInvariant();
            if (n.StartsWith("MASTER")) return Section.Master;
            if (n.StartsWith("LOCATION") || n.StartsWith("SETTING")) return Section.Location;
            if (n.StartsWith("WARDROBE")) return Section.Wardrobe;
            if (n.StartsWith("BEATS")) return Section.Beats;
            if (n.StartsWith("DIALOGUE")) return Section.Dialogue;
            return Section.None;
        }

        /// <summary>Drops rule lines and copied notes, joins wrapped lines into paragraphs.</summary>
        private static string Tidy(string body)
        {
            body = RuleLine.Replace(body, string.Empty);
            body = LeadingNote.Replace(body.TrimStart(), string.Empty);
            var paragraphs = Regex.Split(body.Trim(), @"\n[ \t]*\n")
                .Select(p => p.Trim())
                .Where(p => p.Length > 0);
            return string.Join("\n\n", paragraphs.Select(p =>
                // Keep beat / dialogue starts on their own line so they can still be split.
                Regex.Replace(p, @"[ \t]*\n[ \t]*(?![-*#]*[ \t]*(?:BEAT|DIALOGUE)[ \t]*\d|\d{1,2}[.)][ \t])", " ", RegexOptions.IgnoreCase)));
        }

        /// <summary>Splits a section at its numbered starts, in the numbers' order; a section without numbers is read
        /// one paragraph per item.</summary>
        private static List<string> Numbered(string body, Regex start)
        {
            if (body.Trim().Length == 0) return new List<string>();
            var starts = start.Matches(body).Cast<Match>().ToList();
            if (starts.Count == 0)
                return Regex.Split(body.Trim(), @"\n[ \t]*\n").Select(p => p.Trim()).Where(p => p.Length > 0).ToList();

            var items = new List<(int N, string Text)>();
            for (var i = 0; i < starts.Count; i++)
            {
                var from = starts[i].Index + starts[i].Length;
                var to = i + 1 < starts.Count ? starts[i + 1].Index : body.Length;
                int.TryParse(starts[i].Groups["n"].Value, out var n);
                items.Add((n, body[from..to].Trim()));
            }
            // The model numbered them in order nearly always; when it did not, the numbers win. Duplicates keep the first.
            return items
                .Select((it, i) => (N: it.N > 0 ? it.N : i + 1, it.Text, Order: i))
                .GroupBy(it => it.N).Select(g => g.First())
                .OrderBy(it => it.N).ThenBy(it => it.Order)
                .Select(it => it.Text).ToList();
        }

        private static string StripBeatLabel(string beat) =>
            Regex.Replace(beat, @"^\s*BEAT\s*\d+\s*[—–:\-.)]*\s*", string.Empty, RegexOptions.IgnoreCase);

        private static string Collapse(string s) => Regex.Replace(s, @"\s+", " ").Trim();

        /// <summary>A spoken line as the voice reads it: one paragraph, no wrapping quotes, no speaker tag.</summary>
        private static string CleanLine(string raw)
        {
            var line = Collapse(raw);
            // "A:" / "NARRATOR —" in front of the quote.
            line = Regex.Replace(line, @"^(?:[A-Z][A-Za-z0-9 .'-]{0,24}?)\s*[:—–-]\s*(?=[""“'‘])", string.Empty);
            line = line.Trim();
            if (line.Length >= 2 && IsOpenQuote(line[0]) && IsCloseQuote(line[^1])) line = line[1..^1].Trim();
            else if (line.Length >= 1 && IsOpenQuote(line[0]) && line.IndexOfAny(new[] { '"', '”' }, 1) < 0) line = line[1..].Trim();
            return line is "\"\"" or "“”" or "-" or "—" or "(none)" or "none" ? string.Empty : line;

            static bool IsOpenQuote(char c) => c is '"' or '“' or '\'' or '‘';
            static bool IsCloseQuote(char c) => c is '"' or '”' or '\'' or '’';
        }
    }
}
