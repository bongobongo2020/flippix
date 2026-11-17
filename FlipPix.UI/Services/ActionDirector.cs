using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FlipPix.Core.Models;

namespace FlipPix.UI.Services
{
    /// <summary>A fight written by the director: the setting every beat shares, and the beats.</summary>
    public sealed record ActionScript(string Setting, IReadOnlyList<FflfBeatText> Beats);

    /// <summary>
    /// The words of the 🥋 Action tab. Two jobs:
    ///
    /// <list type="bullet">
    /// <item><b>Directing.</b> An opening image and a story become a fight in beats: <see cref="BuildRequest"/> is
    /// what the writer model is asked (against <c>prompts/prompt2json/h3-action-director.md</c>), and
    /// <see cref="ParseScript"/> reads its reply back into a setting and beats the ⛓ FFLF beat box understands.</item>
    /// <item><b>Prompting.</b> <see cref="ComposeBeatPrompt"/> wraps one beat in what the Action Director
    /// workflow's own example prompts carry: the combat preset's lead phrase, the one-continuous-shot rules,
    /// the contact rules and an audio line.</item>
    /// </list>
    /// </summary>
    public static class ActionDirector
    {
        public const string SystemPromptFile = "h3-action-director.md";

        public const int MinBeats = 1;
        public const int MaxBeats = 12;

        // ── The request to the writer ───────────────────────────────────────────────────────────────

        /// <summary>
        /// The user message. Pictures are numbered in the order they are attached: the opening image first
        /// (when there is one), then the cast photos in <paramref name="castRoles"/> order.
        /// </summary>
        public static string BuildRequest(H3CombatPreset preset, int beats, double seconds, string? story,
                                          bool hasOpeningImage, IReadOnlyList<string> castRoles)
        {
            beats = Math.Clamp(beats, MinBeats, MaxBeats);
            var sb = new StringBuilder();
            var picture = 1;
            if (hasOpeningImage)
                sb.AppendLine($"Picture {picture++} is the opening frame: the fight starts exactly on it. Take the people, their clothes, their positions, the place and the light from it.");
            foreach (var role in castRoles)
                sb.AppendLine($"Picture {picture++} is a photo of {role}; use it to describe that fighter's look.");
            if (picture == 1)
                sb.AppendLine("No picture is attached: invent two clearly different-looking adult fighters and a place that suit the story.");

            sb.AppendLine();
            sb.AppendLine($"Action style: {preset.Code} — {preset.Name}: {preset.Brief}.");
            sb.AppendLine($"Write exactly {beats} beat{(beats == 1 ? "" : "s")}, {seconds:0.#} seconds each ({beats * seconds:0.#} seconds in all).");
            sb.AppendLine($"Timecodes in each beat run from 0.0 to {seconds:0.#}s.");
            sb.AppendLine();
            sb.AppendLine("Story from the user:");
            sb.Append(string.IsNullOrWhiteSpace(story)
                ? "(none — build a complete fight out of what the picture shows: a reason to start, an escalation, a reversal and a clear ending)"
                : story.Trim());
            return sb.ToString();
        }

        // ── Reading the reply ───────────────────────────────────────────────────────────────────────

        private static readonly Regex Think = new(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        private static readonly Regex BeatHead = new(
            @"^[ \t]*(?:#+[ \t]*)?(?:\*\*)?BEAT[ \t]*\d{1,2}(?:[ \t]*(?:of|/)[ \t]*\d{1,2})?[ \t]*[—–\-:.]*[ \t]*(?<title>[^\n]*)$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);
        private static readonly Regex SettingHead = new(
            @"^[ \t]*(?:#+[ \t]*)?(?:\*\*)?(?:SETTING|STYLE)(?:[ \t]*/[ \t]*\w+)?(?:\*\*)?[ \t]*[:—–\-][ \t]*", RegexOptions.Multiline | RegexOptions.IgnoreCase);

        /// <summary>
        /// Reads "SETTING: …" and the "BEAT n — Title" blocks out of the writer's reply. Each beat comes back as
        /// one line of text (its timecoded exchanges in order). A reply without BEAT headings is read as plain
        /// beats, the way the beat box itself would.
        /// </summary>
        public static ActionScript ParseScript(string? reply)
        {
            var text = Think.Replace(reply ?? string.Empty, string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            text = text.Replace("```", string.Empty).Replace("**", string.Empty).Trim();
            if (text.Length == 0) return new ActionScript(string.Empty, Array.Empty<FflfBeatText>());

            var heads = BeatHead.Matches(text);
            if (heads.Count == 0)
                return new ActionScript(string.Empty, FflfGraph.ParseBeats(text));

            var before = text[..heads[0].Index];
            var setting = SettingHead.Match(before) is { Success: true } m ? before[(m.Index + m.Length)..] : before;

            var beats = new List<FflfBeatText>();
            for (var i = 0; i < heads.Count; i++)
            {
                var start = heads[i].Index + heads[i].Length;
                var end = i + 1 < heads.Count ? heads[i + 1].Index : text.Length;
                var body = Flatten(text[start..end]);
                var title = CleanTitle(heads[i].Groups["title"].Value);
                // "BEAT 2: he ducks…" — the heading line is the beat itself.
                if (body.Length == 0) { body = Flatten(heads[i].Groups["title"].Value); title = string.Empty; }
                if (body.Length > 0) beats.Add(new FflfBeatText(title, body));
            }
            return new ActionScript(Flatten(setting), beats);
        }

        private static string Flatten(string text) => Regex.Replace(text, @"\s+", " ").Trim();

        /// <summary>A title the beat box reads back as a title: no sentence punctuation, eight words at most.</summary>
        private static string CleanTitle(string raw)
        {
            var title = Regex.Replace(raw, @"[:.!?""“”#*]", " ");
            title = Regex.Replace(title, @"\s+", " ").Trim(' ', '—', '–', '-');
            var words = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 8) title = string.Join(' ', words.Take(8));
            if (title.Length > 60) title = title[..60].TrimEnd();
            return title.Length < 2 ? string.Empty : title;
        }

        /// <summary>The beats as the beat box holds them: "Title: text" paragraphs.</summary>
        public static string ToBeatsText(IEnumerable<FflfBeatText> beats) =>
            string.Join("\n\n", beats.Select(b => b.Title.Length > 0 ? $"{b.Title}: {b.Text}" : b.Text));

        // ── One beat's prompt ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The prompt for one beat. Pictures: 1 is the frame the beat starts on, 2 the opening image when it is
        /// sent as the character reference, then the cast photos — at most four pictures reach the graph, so a
        /// cast photo past that is not mentioned.
        /// </summary>
        public static string ComposeBeatPrompt(string beat, string? style, H3CombatPreset preset, H3CombatMix mix,
                                               double seconds, bool hasIdentityImage, bool isFirstBeat, bool isLastBeat,
                                               IReadOnlyList<string> castRoles, int maxPictures = 4)
        {
            var setting = new StringBuilder();
            var picture = hasIdentityImage ? 3 : 2;
            foreach (var role in castRoles)
            {
                if (picture > maxPictures) break;
                setting.Append($"<Picture {picture++}> is {role}: keep that face, hair, build and clothing on {role} throughout. ");
            }
            if (!string.IsNullOrWhiteSpace(style))
            {
                if (setting.Length > 0) setting.Append("\n\n");
                setting.Append(style.Trim());
            }

            var body = new StringBuilder();
            body.Append(preset.Lead).Append(' ')
                .Append($"One continuous {seconds:0.#}-second shot at 24 fps, the whole bodies in frame; ")
                .Append("no cuts, no teleporting, nobody new enters, left and right stay as they are, no duplicated limbs.\n");
            body.Append(beat.Trim()).Append('\n');
            body.Append(preset.Rules);
            body.Append(isLastBeat
                ? " The shot ends on a clear, held final moment."
                : " The shot ends with everyone fully visible and in focus, mid-fight, ready for what comes next.");
            if (beat.IndexOf("Audio:", StringComparison.OrdinalIgnoreCase) < 0)
                body.Append('\n').Append(AudioLine(mix));

            return FflfGraph.ComposePrompt(body.ToString(), setting.ToString(), hasIdentityImage, isFirstBeat);
        }

        /// <summary>The sounds the mix calls for — H3 writes its own soundtrack from the prompt.</summary>
        public static string AudioLine(H3CombatMix mix)
        {
            var sounds = new List<string> { "body impacts", "footwork", "cloth movement", "hard breathing" };
            if (mix.Weapon > 0) sounds.Insert(0, "steel ringing on steel");
            if (mix.GunFu > 0) sounds.Insert(0, "gunshots and falling shell casings");
            return $"Audio: synchronised {string.Join(", ", sounds)}; no music, no narration. No subtitles, no on-screen text.";
        }
    }
}
