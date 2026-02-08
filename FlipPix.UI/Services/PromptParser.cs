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
            cleaned = Regex.Replace(cleaned, @"^[\s\*\•\-]+[\s\)]*", "");

            // Remove leading numbers (1., 2., etc.)
            cleaned = Regex.Replace(cleaned, @"^\d+[\.\)]+\s*", "", RegexOptions.IgnoreCase);

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
            cleaned = Regex.Replace(cleaned, @"\s+", " ");

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
                    var matches = Regex.Matches(text, "\"([^\"]*)\"");
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
                    var match = Regex.Match(line.Trim(), pattern, RegexOptions.Multiline);
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
                    var match = Regex.Match(line.Trim(), pattern, RegexOptions.Multiline);
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
                var sentences = Regex.Split(text, @"(?<=[.!?])\s+");
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
