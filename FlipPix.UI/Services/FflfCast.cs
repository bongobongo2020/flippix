using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FlipPix.UI.Services
{
    /// <summary>One person in the ⛓ FFLF opening image, as the vision model described them.</summary>
    /// <param name="Letter">A, B, C — the letter the beats call them by.</param>
    /// <param name="Position">Where they are in the frame ("left", "right", "centre").</param>
    public sealed record FflfCastMember(string Letter, string Position, string Look, string Outfit)
    {
        /// <summary>"A (the person on the left)" — how the log and the beat prompt name them.</summary>
        public string Label => Position.Length > 0 ? $"{Letter} (the person {FflfCast.Where(Position)})" : Letter;
    }

    /// <summary>
    /// The cast of the ⛓ FFLF opening image, read from the vision model's description
    /// (<see cref="FflfStoryEngine.OpeningImagePrompt"/>): its PEOPLE and WARDROBE parts list one person per line as
    /// <c>A | position | look</c> and <c>A | outfit</c>. Each person gets an H3 character sheet drawn from the
    /// opening image (<see cref="FflfSheetGraph"/>), and the sheets go with every beat as that person's reference.
    /// </summary>
    public static class FflfCast
    {
        /// <summary>The most people that get a sheet: the start frame plus three sheets fill H3's four pictures.</summary>
        public const int MaxSheets = 3;

        private static readonly Regex Heading = new(
            @"^[ \t]*[#*_\[]*[ \t]*(?<name>PEOPLE|WARDROBE|POSE|PLACE|CAMERA)\b[ \t]*[\]*_]*[ \t]*:?(?<rest>.*)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // "A | left | woman…", "- **A** | …", "A: left | …"
        private static readonly Regex PersonLine = new(
            @"^[ \t]*(?:[-*•][ \t]*)?\**(?<letter>[A-F])\**[ \t]*[|:][ \t]*(?<rest>.+)$",
            RegexOptions.Compiled);

        public static IReadOnlyList<FflfCastMember> Parse(string? description)
        {
            var people = new List<(string Letter, string Position, string Look)>();
            var outfits = new Dictionary<string, string>();
            var section = string.Empty;

            foreach (var raw in (description ?? string.Empty).Replace("\r", string.Empty).Split('\n'))
            {
                var line = raw;
                var heading = Heading.Match(line);
                if (heading.Success)
                {
                    section = heading.Groups["name"].Value.ToUpperInvariant();
                    line = heading.Groups["rest"].Value;
                    if (line.Trim().Length == 0) continue;
                }
                if (section is not ("PEOPLE" or "WARDROBE")) continue;

                var m = PersonLine.Match(line);
                if (!m.Success) continue;
                var letter = m.Groups["letter"].Value;
                var parts = m.Groups["rest"].Value.Split('|').Select(Clean).Where(p => p.Length > 0).ToList();
                if (parts.Count == 0) continue;

                if (section == "PEOPLE")
                {
                    if (people.Any(p => p.Letter == letter)) continue;
                    // "A | left | look" — or, when the model left the position out, "A | look".
                    people.Add(parts.Count >= 2 ? (letter, parts[0], string.Join(", ", parts.Skip(1))) : (letter, string.Empty, parts[0]));
                }
                else if (!outfits.ContainsKey(letter))
                {
                    outfits[letter] = string.Join(", ", parts);
                }
            }

            return people
                .OrderBy(p => p.Letter, StringComparer.Ordinal)
                .Select(p => new FflfCastMember(p.Letter, p.Position, p.Look, outfits.GetValueOrDefault(p.Letter) ?? string.Empty))
                .ToList();
        }

        /// <summary>"left" → "on the left", "centre" → "in the centre"; "behind the desk" stays as it is.</summary>
        internal static string Where(string position)
        {
            var p = position.Trim().TrimEnd('.');
            if (Regex.IsMatch(p, @"^(on|in|at|to|near|behind|beside|by)\b", RegexOptions.IgnoreCase)) return p;
            p = Regex.Replace(p, @"^the\s+", string.Empty, RegexOptions.IgnoreCase);
            return Regex.IsMatch(p, @"^(far\s+)?(left|right)\b", RegexOptions.IgnoreCase) ? $"on the {p}" : $"in the {p}";
        }

        private static string Clean(string s) => Regex.Replace(s.Replace("*", string.Empty), @"\s+", " ").Trim().Trim('.', ';', ',').Trim();
    }
}
