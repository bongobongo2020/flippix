using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using FlipPix.UI.Models;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// 🌀 MiniMax I2V's SUBJECTS box: who is in &lt;Picture 1&gt; and what they wear, pinned so it stops
    /// drifting between takes.
    ///
    /// <para>Every Analyze describes the subjects' clothing afresh from the picture, so two drafts run against the
    /// same picture came back with two different outfits. Here the picture is read once, when it goes into slot 1:
    /// one line per person — man or woman, where they stand, what they wear — which the user can correct. <b>Apply to
    /// idea</b> writes those lines into the idea box as a wardrobe block (replacing any earlier one), and the
    /// writer is told to use that wording verbatim. Load another draft, press Apply again, and the outfits match.</para>
    ///
    /// <para>The result is remembered per picture file, edits included, so the same picture next week comes back
    /// with the same wardrobe without asking the model again.</para>
    /// </summary>
    public partial class MiniMaxI2VViewModel
    {
        /// <summary>First line of the block Apply writes. Also how the block is found again to replace it, and how
        /// the writer request knows the draft carries one.</summary>
        public const string WardrobeHeading = "Wardrobe (keep identical in every shot):";

        private const int MaxSubjects = 4;
        private const int WardrobeSaveDelayMs = 800;

        private static readonly Regex WardrobeBlock = new(
            @"(?m)^[ \t]*" + Regex.Escape(WardrobeHeading) + @"[^\n]*(?:\r?\n[ \t]*-[^\n]*)*",
            RegexOptions.Compiled);

        private const string SubjectDetectorSystemPrompt =
            "You look at one photograph and list every person in it, from left to right.\n" +
            "For each person write exactly one line in this form:\n" +
            "<number> | <man or woman — boy or girl only for a clear child> | <left, centre or right> | <clothing>\n" +
            "Clothing is everything the person visibly wears, head to toe: each garment with its colour, material, fit " +
            "and notable details, plus shoes and accessories (jewellery, glasses, hats, bags) when visible. " +
            "One sentence of at most 45 words, e.g. \"fitted black leather jacket over a white crew-neck T-shirt, " +
            "dark blue slim jeans, white low-top sneakers, silver wristwatch\". If part of the outfit is out of frame, " +
            "describe only what is shown. If the person wears nothing on top or at all, say so plainly.\n" +
            "Describe clothing only — not the face, hair, body, pose or setting. Never name or identify anyone.\n" +
            "Output only those lines, nothing before or after. If there are no people, output NONE.";

        private sealed record WardrobeEntry(string Sex, string Position, string Clothing);

        private readonly Dictionary<string, List<WardrobeEntry>> _wardrobeCache = new(StringComparer.OrdinalIgnoreCase);
        private string _wardrobeCacheFile = string.Empty;
        private CancellationTokenSource? _wardrobeSaveCts;

        /// <summary>The picture <see cref="Subjects"/> describes; empty when slot 1 is empty.</summary>
        private string _subjectsPicture = string.Empty;
        private CancellationTokenSource? _subjectsCts;
        private bool _isDetectingSubjects;
        private string _subjectsStatus = string.Empty;
        private bool _wardrobeReady;

        public ObservableCollection<MiniMaxI2VSubject> Subjects { get; } = new();

        public RelayCommand DetectSubjectsCommand { get; private set; } = null!;
        public RelayCommand ApplyWardrobeCommand { get; private set; } = null!;

        public bool HasSubjects => Subjects.Count > 0;

        /// <summary>The box shows whenever there is a picture to describe.</summary>
        public bool ShowSubjectsBox => _subjectsPicture.Length > 0;

        public bool IsDetectingSubjects
        {
            get => _isDetectingSubjects;
            private set
            {
                if (_isDetectingSubjects == value) return;
                _isDetectingSubjects = value;
                OnPropertyChanged();
                DetectSubjectsCommand.NotifyCanExecuteChanged();
                ApplyWardrobeCommand.NotifyCanExecuteChanged();
            }
        }

        public string SubjectsStatus
        {
            get => _subjectsStatus;
            private set { if (_subjectsStatus == value) return; _subjectsStatus = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasSubjectsStatus)); }
        }

        public bool HasSubjectsStatus => _subjectsStatus.Length > 0;

        private void InitWardrobe()
        {
            DetectSubjectsCommand = new RelayCommand(
                () => { if (_subjectsPicture.Length > 0) _ = DetectSubjectsAsync(_subjectsPicture); },
                () => _subjectsPicture.Length > 0 && !IsDetectingSubjects);
            ApplyWardrobeCommand = new RelayCommand(ApplyWardrobe,
                () => Subjects.Any(s => !string.IsNullOrWhiteSpace(s.Clothing)) && !IsDetectingSubjects);

            Subjects.CollectionChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(HasSubjects));
                ApplyWardrobeCommand.NotifyCanExecuteChanged();
            };

            _wardrobeCacheFile = Path.Combine(ScenePromptLibrary.FolderFor("minimax-i2v-wardrobe"), "wardrobe.json");
            try
            {
                if (File.Exists(_wardrobeCacheFile))
                {
                    var saved = JsonSerializer.Deserialize<Dictionary<string, List<WardrobeEntry>>>(File.ReadAllText(_wardrobeCacheFile));
                    if (saved != null)
                        foreach (var (k, v) in saved) _wardrobeCache[k] = v;
                }
            }
            catch (Exception ex)
            {
                AddLog($"Could not read the remembered wardrobes: {ex.Message}");
            }

            _wardrobeReady = true;
            OnPrimaryPictureChanged();
        }

        /// <summary>Called whenever a reference slot changes; only a new &lt;Picture 1&gt; does anything.</summary>
        private void OnPrimaryPictureChanged()
        {
            if (!_wardrobeReady) return;

            var first = References.Count > 0 ? References[0] : null;
            var path = first is { HasImage: true, IsLocationPlate: false } ? first.Path : string.Empty;
            if (string.Equals(path, _subjectsPicture, StringComparison.OrdinalIgnoreCase)) return;

            _subjectsCts?.Cancel();
            _subjectsPicture = path;
            SetSubjects(Array.Empty<WardrobeEntry>());
            SubjectsStatus = string.Empty;
            IsDetectingSubjects = false;
            OnPropertyChanged(nameof(ShowSubjectsBox));
            DetectSubjectsCommand.NotifyCanExecuteChanged();
            if (path.Length == 0) return;

            if (_wardrobeCache.TryGetValue(path, out var remembered) && remembered.Count > 0)
            {
                SetSubjects(remembered);
                SubjectsStatus = "Remembered for this picture — press ↻ to look again.";
                return;
            }

            _ = DetectSubjectsAsync(path);
        }

        private async Task DetectSubjectsAsync(string path)
        {
            _subjectsCts?.Cancel();
            var cts = new CancellationTokenSource();
            _subjectsCts = cts;
            var token = cts.Token;

            IsDetectingSubjects = true;
            SubjectsStatus = "Looking at who is in Picture 1…";
            try
            {
                var model = await ResolveWriterModelAsync(token);
                var reply = await Task.Run(() => _lmStudioService.AnalyzeMultipleImagesWithSystemPromptAsync(
                    model, new List<string> { path },
                    "List the people in this picture and what each one is wearing.",
                    SubjectDetectorSystemPrompt, maxTokens: 1200, cancellationToken: token), token);

                var found = ParseSubjects(CleanLLMOutput(reply));
                OnUiThread(() =>
                {
                    if (token.IsCancellationRequested || !string.Equals(path, _subjectsPicture, StringComparison.OrdinalIgnoreCase)) return;
                    SetSubjects(found);
                    IsDetectingSubjects = false;
                    if (found.Count == 0)
                    {
                        SubjectsStatus = "No people found in Picture 1.";
                        return;
                    }
                    SubjectsStatus = "Edit if anything is off, then press 👕 Apply to idea.";
                    RememberWardrobe();
                    AddLog($"Subjects in Picture 1: {string.Join("; ", found.Select((s, i) => $"{i + 1} {s.Sex}"))}.");
                });
            }
            catch (OperationCanceledException)
            {
                // Picture 1 changed, or ↻ was pressed again.
            }
            catch (Exception ex)
            {
                OnUiThread(() =>
                {
                    if (!ReferenceEquals(_subjectsCts, cts)) return;
                    IsDetectingSubjects = false;
                    SubjectsStatus = $"Could not read the subjects: {ex.Message}";
                });
                AddLog($"Subject detection failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Reads the detector's <c>n | sex | position | clothing</c> lines. Tolerant of bullets, numbering
        /// styles and a missing position column; anything else is ignored.
        /// </summary>
        public static List<(string Sex, string Position, string Clothing)> ParseSubjectLines(string reply)
        {
            var result = new List<(string, string, string)>();
            foreach (var raw in (reply ?? string.Empty).Split('\n'))
            {
                var line = raw.Trim().TrimStart('-', '*', '•', ' ').Trim();
                if (line.Length == 0 || !line.Contains('|')) continue;

                var parts = line.Split('|').Select(p => p.Trim().Trim('<', '>', '"', ' ')).ToList();
                if (parts.Count > 0 && Regex.IsMatch(parts[0], @"^\(?#?\d+[.)]?$")) parts.RemoveAt(0);
                if (parts.Count < 2) continue;

                var sex = NormalizeSex(parts[0]);
                string position = string.Empty, clothing;
                if (parts.Count >= 3)
                {
                    position = parts[1].ToLowerInvariant();
                    clothing = string.Join(", ", parts.Skip(2));
                }
                else
                {
                    clothing = parts[1];
                }
                clothing = clothing.Trim().TrimEnd('.').Trim();
                if (clothing.Length == 0) continue;

                result.Add((sex, position, clothing));
                if (result.Count == MaxSubjects) break;
            }
            return result;
        }

        private static List<WardrobeEntry> ParseSubjects(string reply) =>
            ParseSubjectLines(reply).Select(t => new WardrobeEntry(t.Sex, t.Position, t.Clothing)).ToList();

        private static string NormalizeSex(string text)
        {
            var t = text.ToLowerInvariant();
            if (Regex.IsMatch(t, @"\b(woman|female|lady|girl)\b")) return t.Contains("girl") ? "girl" : "woman";
            if (Regex.IsMatch(t, @"\b(man|male|gentleman|boy)\b")) return t.Contains("boy") ? "boy" : "man";
            return "person";
        }

        private void SetSubjects(IEnumerable<WardrobeEntry> entries)
        {
            foreach (var s in Subjects) s.Changed -= OnSubjectEdited;
            Subjects.Clear();
            var n = 1;
            foreach (var e in entries)
            {
                var subject = new MiniMaxI2VSubject(n++) { Sex = e.Sex, Position = e.Position, Clothing = e.Clothing };
                subject.Changed += OnSubjectEdited;
                Subjects.Add(subject);
            }
        }

        private void OnSubjectEdited(object? sender, EventArgs e)
        {
            ApplyWardrobeCommand.NotifyCanExecuteChanged();
            RememberWardrobe();
        }

        /// <summary>Remembers the rows for the current picture, a moment after the last edit.</summary>
        private void RememberWardrobe()
        {
            if (_subjectsPicture.Length == 0) return;
            _wardrobeCache[_subjectsPicture] = Subjects
                .Select(s => new WardrobeEntry(s.Sex, s.Position, s.Clothing.Trim()))
                .ToList();

            _wardrobeSaveCts?.Cancel();
            var cts = new CancellationTokenSource();
            _wardrobeSaveCts = cts;
            var snapshot = JsonSerializer.Serialize(_wardrobeCache, new JsonSerializerOptions { WriteIndented = true });
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(WardrobeSaveDelayMs, cts.Token);
                    Directory.CreateDirectory(Path.GetDirectoryName(_wardrobeCacheFile)!);
                    await File.WriteAllTextAsync(_wardrobeCacheFile, snapshot, cts.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { AddLog($"Could not save the wardrobe: {ex.Message}"); }
            });
        }

        /// <summary>The wardrobe block for the current rows, or empty when there is nothing to say.</summary>
        public static string BuildWardrobeBlock(IEnumerable<MiniMaxI2VSubject> subjects)
        {
            var lines = subjects
                .Where(s => !string.IsNullOrWhiteSpace(s.Clothing))
                .Select(s =>
                {
                    var who = string.IsNullOrWhiteSpace(s.Sex) ? "person" : s.Sex.Trim();
                    var where = string.IsNullOrWhiteSpace(s.Position) ? string.Empty : $", {s.Position.Trim()}";
                    return $"- Subject {s.Number} ({who}{where}): {s.Clothing.Trim().TrimEnd('.')}.";
                })
                .ToList();
            return lines.Count == 0 ? string.Empty : WardrobeHeading + "\n" + string.Join("\n", lines);
        }

        /// <summary>
        /// Puts the wardrobe block at the end of <paramref name="idea"/>, replacing a block an earlier Apply left
        /// there, so pressing it again after an edit never stacks two different outfits.
        /// </summary>
        public static string ApplyWardrobeBlock(string idea, string block)
        {
            var body = WardrobeBlock.Replace(idea ?? string.Empty, string.Empty);
            body = Regex.Replace(body, @"(\r?\n){3,}", "\n\n").Trim();
            if (block.Length == 0) return body;
            return body.Length == 0 ? block : body + "\n\n" + block;
        }

        private void ApplyWardrobe()
        {
            var block = BuildWardrobeBlock(Subjects);
            if (block.Length == 0) return;
            Prompt = ApplyWardrobeBlock(Prompt, block);
            AddLog("Wardrobe written into the idea.");
        }

        /// <summary>What the writer is told when the draft carries a wardrobe block.</summary>
        private static IEnumerable<string> WardrobeInstructions(string draft)
        {
            if (string.IsNullOrEmpty(draft) || !draft.Contains(WardrobeHeading)) yield break;
            yield return string.Empty;
            yield return "The draft below ends with a fixed wardrobe for the people in <Picture 1>. It is locked: "
                       + "describe each person's clothing with exactly that wording in subject_definitions and anywhere "
                       + "clothing is mentioned, even where the picture seems to show something different. Match each "
                       + "line to the person by sex and position. Add no other garments, change no colours, and do not "
                       + "have anyone change clothes. Do not copy the wardrobe lines out as a section of their own.";
        }

        /// <summary>The llama-server model every writer call on this tab uses.</summary>
        private async Task<string> ResolveWriterModelAsync(CancellationToken token)
        {
            var baseUrl = _settingsService.Settings?.LMStudioSettings?.BaseUrl ?? "http://alien:8080";
            await _lmStudioService.SetBaseUrlAsync(baseUrl);

            var models = await _lmStudioService.GetAvailableModelsAsync(token);
            var model = _settingsService.Settings?.LMStudioSettings?.SelectedModel ?? string.Empty;
            if (string.IsNullOrEmpty(model) && models.Count > 0)
                model = models[0].Id ?? models[0].Name ?? string.Empty;
            if (string.IsNullOrEmpty(model))
                throw new Exception("no LM Studio / llama-server model is available. Make sure the server is running and a model is loaded.");
            return model;
        }
    }
}
