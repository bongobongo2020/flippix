using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FlipPix.UI.Services
{
    /// <summary>
    /// Utility for parsing prompts from text input
    /// </summary>
    public static class PromptParser
    {
        /// <summary>
        /// Extract individual prompts from a block of text using multiple parsing strategies
        /// </summary>
        /// <param name="text">Input text containing prompts</param>
        /// <returns>List of extracted prompts</returns>
        public static List<string> ExtractPrompts(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new List<string>();
            }

            var prompts = new List<string>();

            // Strategy 1: Try JSON array format first
            var jsonPrompts = TryParseJsonArray(text);
            if (jsonPrompts.Count > 0)
            {
                return jsonPrompts;
            }

            // Strategy 2: Try numbered list format (1. First prompt, 2. Second prompt)
            var numberedPrompts = TryParseNumberedList(text);
            if (numberedPrompts.Count > 0)
            {
                prompts.AddRange(numberedPrompts);
                return prompts;
            }

            // Strategy 3: Try dash/asterisk list format (- First prompt, - Second prompt)
            var bulletPrompts = TryParseBulletList(text);
            if (bulletPrompts.Count > 0)
            {
                prompts.AddRange(bulletPrompts);
                return prompts;
            }

            // Strategy 4: Try comma-separated values
            var commaPrompts = TryParseCommaSeparated(text);
            if (commaPrompts.Count > 1)
            {
                prompts.AddRange(commaPrompts);
                return prompts;
            }

            // Strategy 5: Try newline-separated (each line is a prompt)
            var linePrompts = TryParseNewlineSeparated(text);
            if (linePrompts.Count > 1)
            {
                prompts.AddRange(linePrompts);
                return prompts;
            }

            // Strategy 6: Split by sentence boundaries (period, exclamation, question mark)
            var sentencePrompts = TryParseSentences(text);
            if (sentencePrompts.Count > 1)
            {
                prompts.AddRange(sentencePrompts);
                return prompts;
            }

            // Fallback: Treat entire text as a single prompt
            prompts.Add(CleanPrompt(text.Trim()));

            return prompts;
        }

        /// <summary>
        /// Strips chain-of-thought / thinking preamble from a model response, returning only the
        /// final output paragraph.
        /// </summary>
        public static string StripThinking(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            // 1. XML thinking tags: <think>...</think> or <thinking>...</thinking>
            //    Qwen3/DeepSeek reasoning models emit </think> before their actual response.
            var afterTag = Regex.Match(text, @"</think(?:ing)?>\s*([\s\S]+)$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            if (afterTag.Success)
            {
                var r = afterTag.Groups[1].Value.Trim();
                // Even if the content after </think> is short/truncated, it is the real output
                if (r.Length > 0) return r;
            }

            // 1.5. Qwen3/gemma4 plain-markdown thinking: numbered bold section headers
            //      (**1. Analyse:** / **2. Draft:** / **3. Refine:**).
            var numberedExtract = ExtractFromNumberedMarkdownThinking(text);
            if (numberedExtract != null) return numberedExtract;

            // 1.7. Bullet list of individually quoted sentences — Qwen3 /no_think output format.
            //      Joins all COMPLETE "- "sentence."" bullets into one paragraph.
            //      Truncated last items (no closing quote) are naturally excluded.
            var quotedBullets = Regex.Matches(text,
                "^[ \\t]*[-*\u2022][ \\t]+\"([^\"]{20,})\"",
                RegexOptions.Multiline, TimeSpan.FromSeconds(1));
            if (quotedBullets.Count >= 2)
            {
                var sentences = quotedBullets.Cast<Match>()
                    .Select(m => m.Groups[1].Value.Trim())
                    .Where(s => s.Length > 10)
                    .ToList();
                if (sentences.Count > 0)
                    return string.Join(" ", sentences);
            }

            // 2. Quoted draft paragraph — curly "..." or straight "..." wrapping.
            //    Require an uppercase start AND sentence-ending punctuation before the closing
            //    quote so truncated/unfinished blocks (no closing quote) are ignored.
            //    U+201C/U+201D = curly left/right double quotation marks.
            var quotedDraft = Regex.Matches(text,
                "(?:\u201C|\")([A-Z][^\u201C\u201D\"]{80,}[.!?])(?:\u201D|\")",
                RegexOptions.None, TimeSpan.FromSeconds(1));
            if (quotedDraft.Count > 0)
                return quotedDraft[quotedDraft.Count - 1].Groups[1].Value.Trim();

            // 3. Intro phrase "Let's draft it carefully:" (or similar) followed by paragraph.
            // OPTIMIZED: Replaced lazy quantifier [\s\S]{80,}? with explicit line matching to prevent catastrophic backtracking.
            // Matches first line (80+ chars), then continuation lines that don't start with blank line + digit.
            var draftIntro = Regex.Matches(
                text,
                @"(?:let'?s?\s+draft[^:\n]*|here'?s?\s+(?:the\s+)?(?:final\s+)?draft[^:\n]*)\s*:?\s*\n+([^\n]{80,}(?:\n(?!\s*\n\d)[^\n]*)*)",
                RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            if (draftIntro.Count > 0)
            {
                var candidate = draftIntro[draftIntro.Count - 1].Groups[1].Value.Trim();
                // Strip surrounding straight/curly quotes if present
                candidate = Regex.Replace(candidate, @"^[\u201C\u201D""\u2018\u2019']+|[\u201C\u201D""\u2018\u2019']+$", "",
                    RegexOptions.None, TimeSpan.FromSeconds(1)).Trim();
                candidate = StripPostPromptMeta(candidate);
                if (candidate.Length > 50) return candidate;
            }

            // 4. Italic/bold section labels: *Final Draft:*, *Final Check:*, *Revised Draft:*, etc.
            //    followed immediately by a newline + paragraph content.
            // OPTIMIZED: Limited nested quantifier (?:\n...)* to max 100 iterations to prevent catastrophic backtracking.
            // Original pattern caused exponential time complexity with large responses.
            var labelMatches = Regex.Matches(
                text,
                @"\*{1,2}[^\*\n]{0,80}(?:Draft|Output|Prompt|Check|Answer)[^\*\n]{0,80}\*{1,2}[^\n]*\n([ \t]*[^\-\*\d\n][^\n]{60,}(?:\n(?![ \t]*[\*\-•\d])[^\n]*){0,100})",
                RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            if (labelMatches.Count > 0)
            {
                var candidate = labelMatches[labelMatches.Count - 1].Groups[1].Value.Trim();
                candidate = Regex.Replace(candidate, @"^[\u201C\u201D""\u2018\u2019']+|[\u201C\u201D""\u2018\u2019']+$", "",
                    RegexOptions.None, TimeSpan.FromSeconds(1)).Trim();
                candidate = StripPostPromptMeta(candidate);
                if (candidate.Length > 50) return candidate;
            }

            // 5. Last substantial paragraph that does not look like analysis/reasoning.
            //    Split on single blank lines (with optional spaces/tabs — but NOT more newlines).
            var paragraphs = Regex.Split(text, @"\n[ \t]*\n", RegexOptions.None, TimeSpan.FromSeconds(1))
                .Select(p => p.Trim())
                .Where(p => p.Length > 80)
                .ToList();

            var analysisLine = new Regex(
                @"^(?:\d+[\.\)]\s|[ \t]*[\*\-•]\s|\*\*|Here'?s?\s+(?:a\s+)?think|Let me\s|I'?ll\s|I will\s|Step \d|Check:|Note:|Draft:|Refine|Deconstruct|Self-Correct|All constraint|Ready\.?|Proceed|This matches)",
                RegexOptions.IgnoreCase);

            for (int i = paragraphs.Count - 1; i >= 0; i--)
            {
                if (!analysisLine.IsMatch(paragraphs[i]))
                {
                    var result = paragraphs[i];
                    // Strip surrounding quotes the model may have left
                    result = Regex.Replace(result, @"^[\u201C\u201D""\u2018\u2019']+|[\u201C\u201D""\u2018\u2019']+$", "",
                        RegexOptions.None, TimeSpan.FromSeconds(1)).Trim();
                    return result;
                }
            }

            return text.Trim();
        }

        /// <summary>
        /// Extracts the final image generation prompt from Qwen3/gemma4-style multi-section
        /// markdown chain-of-thought output.  Returns null if not applicable.
        /// </summary>
        public static string? ExtractFromNumberedMarkdownThinking(string text)
        {
            // Detect multi-section bold-header markdown thinking.
            // Handles all observed formats:
            //   "**1. Section:**"  — number inside bold
            //   "1.  **Section:**" — number OUTSIDE bold (current bug: was not matched)
            //   "**Section:**"     — non-numbered
            var sectionHeaders = Regex.Matches(text,
                @"^\s*(?:\d+[\.\)]\s+)?\*\*[^*\n]{5,60}\*\*",
                RegexOptions.Multiline, TimeSpan.FromSeconds(1));
            if (sectionHeaders.Count < 2)
                return null;

            // Re-usable boundary: "start of next section header line or end of string"
            // Matches both "  **Bold" and "1.  **Bold" at line start.
            const string NEXT = @"\n\s*(?:\d+[\.\)]\s+)?\*\*[^*\n]";

            // Priority 1: LAST inline draft/refining bullet — returns the most refined version.
            //   Label can contain extra text around the keyword, e.g.:
            //     "* *Draft 1:* TEXT"              — label has a number suffix
            //     "* *Refining (Adding Detail):*"  — label has extra words around keyword
            //     "- *Draft:* TEXT"                — dash bullet
            //     "* *Final Prompt:* TEXT"         — explicit "Final"
            var draftMatches = Regex.Matches(
                text,
                @"[-*•][ \t]+\*{1,2}(?:[^:\n*]*(?:Draft|Refin|Final|Enhanc|Polished|Output|Prompt|Combin)[^:\n*]*):?\*{0,2}[ \t]+([^\n]{50,})",
                RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            if (draftMatches.Count > 0)
                return draftMatches[draftMatches.Count - 1].Groups[1].Value.Trim();

            // Priority 2: last "Draft/Refin/Final/Combin" section — take its paragraph block.
            //   Captures even truncated text (model writes most-refined content first within
            //   the section). Lookahead updated to handle "1.  **Next Section" boundaries.
            // OPTIMIZED: Replaced lazy quantifier [\s\S]+? with line-based matching to prevent catastrophic backtracking.
            // Matches lines until hitting the NEXT section boundary or end of string.
            var finalSections = Regex.Matches(
                text,
                @"\*\*(?:[^*\n]*(?:Draft|Refin|Final|Combin)[^*\n]*)\*\*[:\s]*\n([^\n]+(?:\n(?!\s*(?:\d+[\.\)]\s+)?\*\*[^*\n])[^\n]*)*)",
                RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            if (finalSections.Count > 0)
            {
                var content = finalSections[finalSections.Count - 1].Groups[1].Value.Trim();
                // Strip any opening curly/straight quote wrapping
                content = Regex.Replace(content, @"^[""'\u201C\u201D]+", "", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim();
                if (content.Length > 50) return content;
            }

            // Priority 3: last non-Analyse section with labeled bullets → join their values.
            //   Fallback when truncated before any Refine/Draft section is written.
            //   Split pattern updated to handle "1.  **Section" boundaries.
            var sectionParts = Regex.Split(text, @"(?=" + NEXT + ")", RegexOptions.None, TimeSpan.FromSeconds(1));
            for (int i = sectionParts.Length - 1; i >= 0; i--)
            {
                var section = sectionParts[i].Trim();
                if (section.Length < 50) continue;

                var firstLine = section.Split('\n')[0];
                if (Regex.IsMatch(firstLine, @"\bAnalyz", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))) continue;

                // Handles both "* **Label:** VALUE" and "- *Label:* VALUE"
                var bulletValues = Regex.Matches(
                    section,
                    @"^[ \t]*[-*•][ \t]+\*{1,2}[^:\*\n]+\*{0,2}:[ \t]+(.{40,})$",
                    RegexOptions.Multiline, TimeSpan.FromSeconds(1));
                if (bulletValues.Count > 0)
                {
                    var parts = bulletValues.Cast<Match>()
                        .Select(m => m.Groups[1].Value.Trim().TrimEnd('.', ','))
                        .Where(s => s.Length > 20)
                        .ToList();
                    if (parts.Count > 0)
                        return string.Join(". ", parts);
                }
            }

            return null;
        }

        private static string StripPostPromptMeta(string text)
        {
            // Remove trailing meta-commentary that sometimes follows the final paragraph
            return Regex.Replace(
                text,
                @"\s*(?:✅|☑️?|Proceeds\.?|Ready\.?|Note:[\s\S]*|All constraints[\s\S]*|Self-Correction[\s\S]*)$",
                string.Empty,
                RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)).Trim();
        }

        /// <summary>
        /// Clean and normalize a prompt string
        /// </summary>
        /// <param name="prompt">Raw prompt string</param>
        /// <returns>Cleaned prompt string</returns>
        public static string CleanPrompt(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt))
            {
                return string.Empty;
            }

            // Remove leading/trailing whitespace
            var cleaned = prompt.Trim();

            // Remove common list markers (-, *, •, etc.)
            cleaned = Regex.Replace(cleaned, @"^[\s\*\•\-]+[\s\)]*", "", RegexOptions.None, TimeSpan.FromSeconds(1));

            // Remove leading numbers (1., 2., etc.)
            cleaned = Regex.Replace(cleaned, @"^\d+[\.\)]+\s*", "", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

            // Remove quotes surrounding the entire prompt
            if (cleaned.Length >= 2 && ((cleaned.StartsWith("\"") && cleaned.EndsWith("\"")) ||
                                       (cleaned.StartsWith("'") && cleaned.EndsWith("'")) ||
                                       (cleaned.StartsWith("\"") && cleaned.EndsWith("\""))))
            {
                cleaned = cleaned.Substring(1, cleaned.Length - 2);
            }

            // Remove leading/trailing punctuation
            cleaned = cleaned.Trim(' ', '\t', '\n', '\r', '.', ',', '!', '?', ';', ':', '-', '_', '(', ')', '[', ']');

            // Collapse multiple whitespace into single space
            cleaned = Regex.Replace(cleaned, @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1));

            return cleaned.Trim();
        }

        #region Private Parsing Methods

        private static List<string> TryParseJsonArray(string text)
        {
            var prompts = new List<string>();
            try
            {
                text = text.Trim();
                if ((text.StartsWith("[") && text.EndsWith("]")) ||
                    (text.StartsWith("[") && text.Contains("\"")))
                {
                    // Simple JSON array parsing
                    var matches = Regex.Matches(text, "\"([^\"]*)\"", RegexOptions.None, TimeSpan.FromSeconds(1));
                    foreach (Match match in matches)
                    {
                        if (match.Groups.Count > 1)
                        {
                            var prompt = CleanPrompt(match.Groups[1].Value);
                            if (!string.IsNullOrEmpty(prompt))
                            {
                                prompts.Add(prompt);
                            }
                        }
                    }

                    if (prompts.Count > 0)
                    {
                        return prompts;
                    }
                }
            }
            catch
            {
                // Ignore JSON parsing errors
            }
            return prompts;
        }

        private static List<string> TryParseNumberedList(string text)
        {
            var prompts = new List<string>();
            try
            {
                // Match patterns like "1. prompt", "1) prompt", "1 - prompt"
                var pattern = @"^(\d+[\.\)\-]+)\s*(.+)$";
                var lines = text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

                foreach (var line in lines)
                {
                    var match = Regex.Match(line.Trim(), pattern, RegexOptions.Multiline, TimeSpan.FromSeconds(1));
                    if (match.Success && match.Groups.Count > 2)
                    {
                        var prompt = CleanPrompt(match.Groups[2].Value);
                        if (!string.IsNullOrEmpty(prompt))
                        {
                            prompts.Add(prompt);
                        }
                    }
                    else if (prompts.Count > 0)
                    {
                        // Continuation of a previous item
                        prompts.Add(CleanPrompt(line));
                    }
                }
            }
            catch
            {
                // Ignore parsing errors
            }
            return prompts;
        }

        private static List<string> TryParseBulletList(string text)
        {
            var prompts = new List<string>();
            try
            {
                // Match patterns like "- prompt", "* prompt", "• prompt"
                var pattern = @"^[\s\*\•\-]+(.+)$";
                var lines = text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

                foreach (var line in lines)
                {
                    var match = Regex.Match(line.Trim(), pattern, RegexOptions.Multiline, TimeSpan.FromSeconds(1));
                    if (match.Success && match.Groups.Count > 1)
                    {
                        var prompt = CleanPrompt(match.Groups[1].Value);
                        if (!string.IsNullOrEmpty(prompt))
                        {
                            prompts.Add(prompt);
                        }
                    }
                }
            }
            catch
            {
                // Ignore parsing errors
            }
            return prompts;
        }

        private static List<string> TryParseCommaSeparated(string text)
        {
            var prompts = new List<string>();
            try
            {
                // Split by comma, but only if there are multiple commas
                var parts = text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1)
                {
                    foreach (var part in parts)
                    {
                        var prompt = CleanPrompt(part);
                        if (!string.IsNullOrEmpty(prompt) && prompt.Length > 10) // Only non-trivial prompts
                        {
                            prompts.Add(prompt);
                        }
                    }
                }
            }
            catch
            {
                // Ignore parsing errors
            }
            return prompts;
        }

        private static List<string> TryParseNewlineSeparated(string text)
        {
            var prompts = new List<string>();
            try
            {
                var lines = text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var prompt = CleanPrompt(line);
                    if (!string.IsNullOrEmpty(prompt) && prompt.Length > 10) // Only non-trivial prompts
                    {
                        prompts.Add(prompt);
                    }
                }
            }
            catch
            {
                // Ignore parsing errors
            }
            return prompts;
        }

        private static List<string> TryParseSentences(string text)
        {
            var prompts = new List<string>();
            try
            {
                // Split by sentence endings
                var sentences = Regex.Split(text, @"(?<=[.!?])\s+", RegexOptions.None, TimeSpan.FromSeconds(1));
                foreach (var sentence in sentences)
                {
                    var prompt = CleanPrompt(sentence);
                    if (!string.IsNullOrEmpty(prompt) && prompt.Length > 10) // Only non-trivial prompts
                    {
                        prompts.Add(prompt);
                    }
                }
            }
            catch
            {
                // Ignore parsing errors
            }
            return prompts;
        }

        #endregion
    }
}
