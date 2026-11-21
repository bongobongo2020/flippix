using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlipPix.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// The look of the story, split into three boxes that are all written in front of every beat
    /// (<see cref="FflfGraph.ComposeLook"/>):
    /// <list type="bullet">
    /// <item><b>Style / direction</b> (<see cref="H3FflfViewModel.StyleText"/>) — the master prompt: format, camera,
    /// fight direction, performance rules.</item>
    /// <item><b>Setting</b> — the space and the light.</item>
    /// <item><b>Wardrobe</b> — how the character looks. 📂 picks a picture of the character and 🔍 Analyze has
    /// llama-server describe the hair and outfit in it, filling the box.</item>
    /// </list>
    /// A master prompt pasted whole into the style box, with "SETTING PROMPT" / "WARDROBE PROMPT" headings, is
    /// split into the three boxes on its own.
    /// </summary>
    public partial class H3FflfViewModel
    {
        private const string WardrobeSystemPrompt =
            "You look at one photograph of a character and write the WARDROBE prompt a video model uses to keep that " +
            "character looking exactly the same in every shot.\n" +
            "Write one paragraph of 60–120 words in plain, present-tense description, with no heading or preamble. Cover, in order:\n" +
            "1. Hair: colour, length, how it is worn (ponytail, loose, braided…), any face-framing pieces. Eye colour if clearly visible.\n" +
            "2. Every visible garment from top to bottom: colour, material, fit and notable details (knit, print, logo placement, how it is worn).\n" +
            "3. Footwear, or barefoot. Accessories: jewellery, glasses, hats, bags, watches, wraps.\n" +
            "4. Any visible skin finish such as sweat sheen or makeup.\n" +
            "Describe only what is visible. Never name or identify the person, and do not guess age, ethnicity or body measurements. " +
            "Do not describe the background, the pose, the expression or the lighting. " +
            "If more than one person is in the picture, describe only the most prominent one. Output only the paragraph.";

        // Headings of a pasted master prompt: the line must start with one of these words and be short.
        private static readonly Regex LookHeading = new(
            @"(?im)^[ \t#*]*(?<kind>MASTER(?:[ \t]+VIDEO)?|STYLE|SETTING|WARDROBE)[ \t]+PROMPT\b[^\n]{0,80}$",
            RegexOptions.Compiled);

        private bool _splittingLook;
        private CancellationTokenSource? _wardrobeCts;

        /// <summary>The space and light, written after the style in front of every beat. Optional.</summary>
        [ObservableProperty] private string _settingText = string.Empty;

        /// <summary>The character's look, written after the setting in front of every beat. Optional.</summary>
        [ObservableProperty] private string _wardrobeText = string.Empty;

        /// <summary>The picture of the character 🔍 Analyze reads the wardrobe from.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasWardrobeImage), nameof(WardrobeImageName))]
        private string _wardrobeImagePath = string.Empty;

        [ObservableProperty] private bool _isAnalyzingWardrobe;
        [ObservableProperty] private string _wardrobeStatus = string.Empty;

        public bool HasWardrobeImage => !string.IsNullOrEmpty(WardrobeImagePath) && File.Exists(WardrobeImagePath);
        public string WardrobeImageName => HasWardrobeImage ? Path.GetFileName(WardrobeImagePath) : "No character picture — 📂 to pick one";

        private System.Windows.Media.Imaging.BitmapImage? _wardrobeThumbnail;
        public System.Windows.Media.Imaging.BitmapImage? WardrobeThumbnail => _wardrobeThumbnail;

        /// <summary>Style, setting and wardrobe as the one block in front of every beat.</summary>
        public string ComposedStyle => FflfGraph.ComposeLook(StyleText, SettingText, WardrobeText);

        public RelayCommand BrowseWardrobeImageCommand { get; private set; } = null!;
        public RelayCommand ClearWardrobeImageCommand { get; private set; } = null!;
        public RelayCommand AnalyzeWardrobeCommand { get; private set; } = null!;
        public RelayCommand SplitLookCommand { get; private set; } = null!;

        private void InitLook()
        {
            BrowseWardrobeImageCommand = new RelayCommand(() => _ = BrowseWardrobeImageAsync(), () => !IsAnalyzingWardrobe);
            ClearWardrobeImageCommand = new RelayCommand(() => WardrobeImagePath = string.Empty, () => HasWardrobeImage && !IsAnalyzingWardrobe);
            AnalyzeWardrobeCommand = new RelayCommand(() => _ = AnalyzeWardrobeAsync(), () => HasWardrobeImage && !IsAnalyzingWardrobe);
            SplitLookCommand = new RelayCommand(() => { if (!TrySplitLook(StyleText)) AddLog("No SETTING PROMPT / WARDROBE PROMPT headings in the style box to split on."); });
        }

        private void RefreshLookCommands()
        {
            BrowseWardrobeImageCommand?.NotifyCanExecuteChanged();
            ClearWardrobeImageCommand?.NotifyCanExecuteChanged();
            AnalyzeWardrobeCommand?.NotifyCanExecuteChanged();
        }

        partial void OnStyleTextChanged(string value)
        {
            OnPropertyChanged(nameof(ComposedStyle));
            if (!_loading && !_splittingLook) TrySplitLook(value);
        }

        partial void OnSettingTextChanged(string value) => OnPropertyChanged(nameof(ComposedStyle));
        partial void OnWardrobeTextChanged(string value) => OnPropertyChanged(nameof(ComposedStyle));
        partial void OnIsAnalyzingWardrobeChanged(bool value) => OnUi(RefreshLookCommands);

        partial void OnWardrobeImagePathChanged(string value)
        {
            _wardrobeThumbnail = null;
            OnPropertyChanged(nameof(WardrobeThumbnail));
            AsyncThumbnail.Load(value, 160, 0, bitmap =>
            {
                if (WardrobeImagePath != value) return;
                _wardrobeThumbnail = bitmap;
                OnPropertyChanged(nameof(WardrobeThumbnail));
            });
            OnUi(RefreshLookCommands);
        }

        /// <summary>
        /// A whole master prompt pasted into the style box: what sits under "SETTING PROMPT" goes to the setting
        /// box, what sits under "WARDROBE PROMPT" to the wardrobe box, and the rest (the master direction) stays.
        /// Nothing happens unless a setting or wardrobe heading is there.
        /// </summary>
        private bool TrySplitLook(string text)
        {
            var parts = SplitLook(text);
            if (parts == null) return false;
            _splittingLook = true;
            try
            {
                StyleText = parts.Value.Style;
                if (parts.Value.Setting != null) SettingText = parts.Value.Setting;
                if (parts.Value.Wardrobe != null) WardrobeText = parts.Value.Wardrobe;
            }
            finally { _splittingLook = false; }
            AddLog("✂ Split the pasted prompt into " + string.Join(", ",
                new[] { "style", parts.Value.Setting != null ? "setting" : null, parts.Value.Wardrobe != null ? "wardrobe" : null }
                    .Where(p => p != null)) + ".");
            return true;
        }

        /// <summary>The style box's text cut at its headings; null when it has no setting or wardrobe heading.</summary>
        public static (string Style, string? Setting, string? Wardrobe)? SplitLook(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var headings = LookHeading.Matches(text).Cast<Match>().ToList();
            if (!headings.Any(h => IsKind(h, "SETTING") || IsKind(h, "WARDROBE"))) return null;

            var style = new List<string>();
            string? setting = null, wardrobe = null;
            var before = text[..headings[0].Index].Trim();
            if (before.Length > 0) style.Add(before);
            for (var i = 0; i < headings.Count; i++)
            {
                var h = headings[i];
                var start = h.Index + h.Length;
                var end = i + 1 < headings.Count ? headings[i + 1].Index : text.Length;
                var body = text[start..end].Trim();
                if (body.Length == 0) continue;
                if (IsKind(h, "SETTING")) setting = Join(setting, body);
                else if (IsKind(h, "WARDROBE")) wardrobe = Join(wardrobe, body);
                else style.Add(body);
            }
            return (string.Join("\n\n", style), setting, wardrobe);

            static bool IsKind(Match m, string kind) => m.Groups["kind"].Value.StartsWith(kind, StringComparison.OrdinalIgnoreCase);
            static string Join(string? a, string b) => a == null ? b : a + "\n\n" + b;
        }

        private async Task BrowseWardrobeImageAsync()
        {
            var start = _settingsService.Settings is { } all ? Profile.Settings(all).LastPhotoFolder : null;
            if (string.IsNullOrEmpty(start) || !Directory.Exists(start))
                start = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            var path = await _fileDialogService.OpenFileDialogAsync("A picture of the character (for the wardrobe)",
                "Image Files|*.png;*.jpg;*.jpeg;*.webp;*.bmp|All Files|*.*", start, persistKey: $"{Profile.Key}.wardrobe");
            if (path == null) return;
            WardrobeImagePath = path;
            WardrobeStatus = "Press 🔍 Analyze to describe the outfit in it.";
            AddLog($"Wardrobe picture: {Path.GetFileName(path)}");
        }

        /// <summary>Sends the character picture to llama-server and puts the description in the wardrobe box.</summary>
        private async Task AnalyzeWardrobeAsync()
        {
            if (!HasWardrobeImage || IsAnalyzingWardrobe) return;
            var llm = _serviceProvider?.GetService<LMStudioService>();
            if (llm == null) { WardrobeStatus = "The LLM service is not available."; return; }

            _wardrobeCts?.Cancel();
            var cts = new CancellationTokenSource();
            _wardrobeCts = cts;
            var path = WardrobeImagePath;
            IsAnalyzingWardrobe = true;
            WardrobeStatus = "Finding the vision model…";
            try
            {
                var lm = _settingsService.Settings?.LMStudioSettings;
                await llm.SetBaseUrlAsync(lm?.BaseUrl ?? "http://localhost:8080");
                var models = await llm.GetAvailableModelsAsync(cts.Token);
                var model = lm?.SelectedModel ?? string.Empty;
                if (string.IsNullOrEmpty(model) && models.Count > 0) model = models[0].Id ?? models[0].Name ?? string.Empty;
                if (string.IsNullOrEmpty(model))
                    throw new InvalidOperationException("no LM Studio / llama-server model is available. Make sure the server is running and a model is loaded.");

                WardrobeStatus = "Reading the outfit from the picture…";
                AddLog($"👕 Analyzing the wardrobe in {Path.GetFileName(path)} → {llm.DescribeTarget(model)}");
                var reply = await Task.Run(() => llm.AnalyzeMultipleImagesWithSystemPromptAsync(
                    model, new List<string> { path },
                    "Write the wardrobe prompt for the character in this picture.",
                    WardrobeSystemPrompt, maxTokens: 1500, cancellationToken: cts.Token), cts.Token);

                var text = CleanLLMOutput(reply ?? string.Empty);
                text = Regex.Replace(text, @"^(?i:wardrobe(?:\s+prompt)?\s*[:\-—]\s*)", string.Empty).Trim();
                OnUi(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    if (text.Length == 0) { WardrobeStatus = "The model sent nothing back — try again."; return; }
                    WardrobeText = text;
                    WardrobeStatus = "Filled from the picture — edit anything that is off.";
                    AddLog("👕 Wardrobe filled from the picture.");
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                OnUi(() => WardrobeStatus = $"Could not analyze: {ex.Message}");
                AddLog($"👕 Wardrobe analysis failed: {ex.Message}");
            }
            finally
            {
                OnUi(() => { if (ReferenceEquals(_wardrobeCts, cts)) IsAnalyzingWardrobe = false; });
            }
        }
    }
}
