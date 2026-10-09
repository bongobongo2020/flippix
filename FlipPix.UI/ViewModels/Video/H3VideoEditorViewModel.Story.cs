using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlipPix.UI.Models;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// Story to Video functionality for H3 Video Editor.
    /// Import stories, analyze cast, generate reference images, and populate timeline with seamless clips.
    /// </summary>
    public partial class H3VideoEditorViewModel
    {
        // ── Story Mode State ──────────────────────────────────────────────────────────────────────

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanAnalyzeStory))]
        [NotifyPropertyChangedFor(nameof(CanPopulateTimeline))]
        private bool _storyModeEnabled;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasStory))]
        [NotifyPropertyChangedFor(nameof(CanAnalyzeStory))]
        private string _storyText = string.Empty;

        [ObservableProperty]
        private string _storyFilePath = string.Empty;

        [ObservableProperty]
        private string _storyFileName = string.Empty;

        [ObservableProperty]
        private double _targetDurationSeconds = 30.0;

        [ObservableProperty]
        private string _storySetting = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanPopulateTimeline))]
        [NotifyPropertyChangedFor(nameof(CanAnalyzeStory))]
        private bool _isStoryAnalyzed;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanAnalyzeStory))]
        [NotifyPropertyChangedFor(nameof(CanPopulateTimeline))]
        private bool _isAnalyzing;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanAnalyzeStory))]
        [NotifyPropertyChangedFor(nameof(CanPopulateTimeline))]
        private bool _isPopulating;

        [ObservableProperty]
        private string _storyStatus = string.Empty;

        [ObservableProperty]
        private string _castPhotoEngine = "qwen21";

        [ObservableProperty]
        private bool _autoGenerateCast = true;

        public bool HasStory => !string.IsNullOrWhiteSpace(StoryText);
        public bool CanAnalyzeStory => StoryModeEnabled && HasStory && !IsAnalyzing && !IsPopulating;
        public bool CanPopulateTimeline => StoryModeEnabled && IsStoryAnalyzed && !IsAnalyzing && !IsPopulating;

        // Beat sheet results
        private List<StoryBeatSheet.StoryBeat> _storyBeats = new();
        private List<StoryContinuity.Environment> _continuityPlan = new();
        private List<(string Kind, string Role)> _detectedCast = new();

        // ── Commands ──────────────────────────────────────────────────────────────────────────────

        public IRelayCommand ImportStoryCommand { get; private set; } = null!;
        public IRelayCommand ImportStoryFolderCommand { get; private set; } = null!;
        public IAsyncRelayCommand AnalyzeStoryCommand { get; private set; } = null!;
        public IAsyncRelayCommand PopulateTimelineCommand { get; private set; } = null!;
        public IRelayCommand ClearStoryCommand { get; private set; } = null!;

        private void InitializeStoryCommands()
        {
            ImportStoryCommand = new RelayCommand(async () => await ImportStoryAsync());
            ImportStoryFolderCommand = new RelayCommand(async () => await ImportStoryFolderAsync());
            AnalyzeStoryCommand = new AsyncRelayCommand(AnalyzeStoryAsync, () => CanAnalyzeStory);
            PopulateTimelineCommand = new AsyncRelayCommand(PopulateTimelineAsync, () => CanPopulateTimeline);
            ClearStoryCommand = new RelayCommand(ClearStory);
        }

        // ── Story Import ──────────────────────────────────────────────────────────────────────────

        private async Task ImportStoryAsync()
        {
            try
            {
                var filter = "Text Files|*.txt|All Files|*.*";
                var paths = await _fileDialogService.OpenFilesDialogAsync("Select story file", filter);
                if (paths == null || paths.Length == 0) return;

                var path = paths[0];
                if (!File.Exists(path))
                {
                    AddLog($"File not found: {path}");
                    return;
                }

                StoryText = await File.ReadAllTextAsync(path);
                StoryFilePath = path;
                StoryFileName = Path.GetFileName(path);
                IsStoryAnalyzed = false;
                _storyBeats.Clear();
                _detectedCast.Clear();

                AddLog($"Imported story: {StoryFileName} ({StoryText.Length} chars)");
                StoryStatus = $"Loaded: {StoryFileName}";
            }
            catch (Exception ex)
            {
                AddLog($"Error importing story: {ex.Message}");
            }
        }

        private async Task ImportStoryFolderAsync()
        {
            try
            {
                var folder = await _fileDialogService.OpenFolderDialogAsync("Select folder with story files");
                if (string.IsNullOrEmpty(folder)) return;

                var files = Directory.GetFiles(folder, "*.txt", SearchOption.TopDirectoryOnly)
                    .OrderBy(f => f)
                    .ToList();

                if (files.Count == 0)
                {
                    AddLog($"No .txt files found in: {folder}");
                    return;
                }

                // Combine all stories or use first one
                var combinedStory = new StringBuilder();
                foreach (var file in files)
                {
                    var content = await File.ReadAllTextAsync(file);
                    if (combinedStory.Length > 0) combinedStory.AppendLine().AppendLine("---").AppendLine();
                    combinedStory.Append(content);
                }

                StoryText = combinedStory.ToString();
                StoryFilePath = folder;
                StoryFileName = $"{files.Count} stories from {Path.GetFileName(folder)}";
                IsStoryAnalyzed = false;
                _storyBeats.Clear();
                _detectedCast.Clear();

                AddLog($"Imported {files.Count} stories from folder: {folder}");
                StoryStatus = $"Loaded: {files.Count} stories";
            }
            catch (Exception ex)
            {
                AddLog($"Error importing story folder: {ex.Message}");
            }
        }

        private void ClearStory()
        {
            StoryText = string.Empty;
            StoryFilePath = string.Empty;
            StoryFileName = string.Empty;
            StorySetting = string.Empty;
            IsStoryAnalyzed = false;
            StoryStatus = string.Empty;
            _storyBeats.Clear();
            _detectedCast.Clear();
            _continuityPlan.Clear();
            AddLog("Story cleared");
        }

        // ── Story Analysis ────────────────────────────────────────────────────────────────────────

        private async Task AnalyzeStoryAsync()
        {
            if (!CanAnalyzeStory) return;

            IsAnalyzing = true;
            StoryStatus = "Analyzing story...";

            try
            {
                var token = CancellationToken.None;

                // Get available models from LM Studio
                var models = await _lmStudioService.GetAvailableModelsAsync(token);
                if (models == null || models.Count == 0)
                {
                    AddLog("ERROR: No LLM models available. Please ensure LM Studio is running.");
                    StoryStatus = "No LLM models available";
                    return;
                }
                var model = models.First().Name.Length > 0 ? models.First().Name : models.First().Id;

                // Calculate clip count from target duration
                var clipDuration = DefaultClipDuration > 0 ? DefaultClipDuration : 5.0;
                var clipCount = (int)Math.Ceiling(TargetDurationSeconds / clipDuration);
                clipCount = Math.Max(2, Math.Min(60, clipCount)); // Clamp to 2-60 clips

                AddLog($"Analyzing story for {clipCount} clips ({TargetDurationSeconds}s / {clipDuration}s per clip)");
                StoryStatus = "Detecting characters in story...";

                // Step 1: Detect cast from story
                // Note: maxCharacters is a ceiling, not a target - the LLM should only return
                // characters that actually exist in the story
                var castReply = await CastPhotoWorkflows.AskCastAsync(
                    _lmStudioService, model, StoryText,
                    maxCharacters: 4,
                    personKindsOnly: true, // Story mode typically features people
                    token);

                // Log raw reply for debugging character detection issues
                AddLog($"Cast detection raw reply:\n{castReply}");

                _detectedCast = CastPhotoWorkflows.ParseCastLines(castReply, 4).ToList();

                if (_detectedCast.Count == 0)
                {
                    AddLog("WARNING: No characters detected in story. Using generic placeholders.");
                    _detectedCast.Add(("man", "Character 1 - the protagonist"));
                }
                else if (_detectedCast.Count > 2)
                {
                    AddLog($"NOTE: Detected {_detectedCast.Count} characters. If this seems too many, " +
                           "verify they all actually appear in your story. The LLM sometimes invents " +
                           "extra characters. You can manually adjust after populating the timeline.");
                }

                AddLog($"Detected {_detectedCast.Count} character(s):");
                for (int i = 0; i < _detectedCast.Count; i++)
                {
                    var (kind, role) = _detectedCast[i];
                    AddLog($"  Character {i + 1}: {kind} - {role}");
                }

                StoryStatus = $"Breaking story into {clipCount} beats...";

                // Step 2: Build cast brief for beat sheet
                var castBrief = BuildCastBrief(_detectedCast);

                // Step 3: Generate beat sheet
                var beatResult = await StoryBeatSheet.WriteAsync(
                    _lmStudioService,
                    model,
                    StoryText,
                    clipCount,
                    clipDuration,
                    castBrief,
                    perBeatCast: false,
                    imagePath: null,
                    log: AddLog,
                    token,
                    continuity: true);

                StorySetting = beatResult.Setting;
                _storyBeats = beatResult.Beats;

                AddLog($"Generated {_storyBeats.Count} beats, setting: {StorySetting}");

                // Step 4: Plan continuity
                var envStrings = _storyBeats.Select(b => b.Env).ToList();
                var beatTexts = _storyBeats.Select(b => b.Text).ToList();
                _continuityPlan = StoryContinuity.Plan(envStrings, StorySetting, beatTexts).ToList();

                foreach (var line in StoryContinuity.Describe(_continuityPlan))
                    AddLog(line);

                IsStoryAnalyzed = true;
                StoryStatus = $"Analyzed: {_detectedCast.Count} characters, {_storyBeats.Count} beats";
                AddLog("Story analysis complete. Ready to populate timeline.");
            }
            catch (Exception ex)
            {
                AddLog($"Error analyzing story: {ex.Message}");
                StoryStatus = $"Analysis failed: {ex.Message}";
            }
            finally
            {
                IsAnalyzing = false;
            }
        }

        private string BuildCastBrief(List<(string Kind, string Role)> cast)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"This story features EXACTLY {cast.Count} character(s) — no more, no less:");
            for (int i = 0; i < cast.Count; i++)
            {
                var (kind, role) = cast[i];
                sb.AppendLine($"  CHARACTER {i + 1}: {role}");
            }
            sb.AppendLine();
            sb.AppendLine($"IMPORTANT: Only use {string.Join(" and ", Enumerable.Range(1, cast.Count).Select(n => $"CHARACTER {n}"))}. " +
                         "Do NOT invent or reference any other characters. These are the ONLY characters in the story.");
            return sb.ToString();
        }

        // ── Cast Photo Generation ─────────────────────────────────────────────────────────────────

        private async Task PrepareCastPhotosAsync()
        {
            if (!AutoGenerateCast) return;

            var emptySlots = new List<int>();
            for (int i = 0; i < Math.Min(4, _detectedCast.Count); i++)
            {
                if (!ReferenceSlots[i].HasImages)
                    emptySlots.Add(i);
            }

            if (emptySlots.Count == 0)
            {
                AddLog("All cast slots have photos - skipping auto-generation");
                return;
            }

            AddLog($"Auto-generating {emptySlots.Count} cast photo(s) using {CastPhotoWorkflows.LabelFor(CastPhotoEngine)}...");

            foreach (var slotIndex in emptySlots)
            {
                if (slotIndex >= _detectedCast.Count) continue;

                var (kind, role) = _detectedCast[slotIndex];
                var prompt = BuildCastPhotoPrompt(kind, role, slotIndex);

                try
                {
                    var photoPath = await GenerateCastPhotoAsync(slotIndex, prompt, CastPhotoEngine, CancellationToken.None);
                    if (!string.IsNullOrEmpty(photoPath) && File.Exists(photoPath))
                    {
                        ReferenceSlots[slotIndex].AddImage(photoPath);
                        AddLog($"Generated Picture {slotIndex + 1}: {Path.GetFileName(photoPath)}");
                    }
                    else
                    {
                        AddLog($"WARNING: Failed to generate Picture {slotIndex + 1}");
                    }
                }
                catch (Exception ex)
                {
                    AddLog($"ERROR generating Picture {slotIndex + 1}: {ex.Message}");

                    // Try fallback engine if primary failed and isn't already the fallback
                    if (CastPhotoEngine != CastPhotoWorkflows.SafetyFallbackEngine)
                    {
                        AddLog($"Retrying Picture {slotIndex + 1} with fallback engine ({CastPhotoWorkflows.LabelFor(CastPhotoWorkflows.SafetyFallbackEngine)})...");
                        try
                        {
                            var fallbackPath = await GenerateCastPhotoAsync(slotIndex, prompt, CastPhotoWorkflows.SafetyFallbackEngine, CancellationToken.None);
                            if (!string.IsNullOrEmpty(fallbackPath) && File.Exists(fallbackPath))
                            {
                                ReferenceSlots[slotIndex].AddImage(fallbackPath);
                                AddLog($"Fallback succeeded for Picture {slotIndex + 1}: {Path.GetFileName(fallbackPath)}");
                            }
                            else
                            {
                                AddLog($"Fallback also failed for Picture {slotIndex + 1}");
                            }
                        }
                        catch (Exception fallbackEx)
                        {
                            AddLog($"Fallback engine also failed: {fallbackEx.Message}");
                        }
                    }
                }
            }
        }

        private string BuildCastPhotoPrompt(string kind, string role, int slotIndex)
        {
            // Build a portrait prompt for the character
            var sb = new StringBuilder();
            sb.Append("A full-length character portrait photograph of ");

            // Add gender/type
            if (kind.Contains("female", StringComparison.OrdinalIgnoreCase) ||
                kind.Contains("woman", StringComparison.OrdinalIgnoreCase) ||
                kind.Contains("girl", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append("a woman");
            }
            else if (kind.Contains("male", StringComparison.OrdinalIgnoreCase) ||
                     kind.Contains("man", StringComparison.OrdinalIgnoreCase) ||
                     kind.Contains("boy", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append("a man");
            }
            else
            {
                sb.Append($"a {kind}");
            }

            // Add role description if it contains useful details
            if (!string.IsNullOrEmpty(role))
            {
                // Extract appearance/clothing from role
                var lower = role.ToLowerInvariant();
                var hasAppearance = lower.Contains("wear") || lower.Contains("dress") ||
                                    lower.Contains("hair") || lower.Contains("eyes") ||
                                    lower.Contains("tall") || lower.Contains("short") ||
                                    lower.Contains("young") || lower.Contains("old");
                if (hasAppearance)
                {
                    sb.Append($", {role}");
                }
            }

            sb.Append(". Professional studio lighting, neutral background, standing pose, looking at camera.");
            return sb.ToString();
        }

        private async Task<string?> GenerateCastPhotoAsync(int slotIndex, string prompt, string engine, CancellationToken ct)
        {
            var ts = DateTime.Now.ToString("yyyyMMddHHmmss");
            var runToken = $"h3_story_cast_{slotIndex + 1}_{ts}";
            var seed = System.Random.Shared.NextInt64(0, 1_000_000_000_000_000L);

            var (json, saveNode) = await CastPhotoWorkflows.BuildAsync(
                engine, $"h3_story/{runToken}", seed, prompt, AddLog, null);

            // Parse JSON string to avoid double-encoding when submitting to ComfyUI
            var workflow = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);

            AddLog($"Generating cast photo for Character {slotIndex + 1} using {CastPhotoWorkflows.LabelFor(engine)}...");

            // Submit and wait for completion
            var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(msg =>
            {
                if (msg.Data?.Value != null && msg.Data?.Max != null && msg.Data.Max > 0)
                {
                    var pct = (double)msg.Data.Value / msg.Data.Max * 100;
                    StoryStatus = $"Generating cast {slotIndex + 1}/{_detectedCast.Count}: {pct:F0}%";
                }
            });

            var promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, progress, ct);

            // Retrieve the output image
            var byNode = await _comfyUIService.HttpClient.GetOutputsByNodeAsync(promptId, ct);
            if (byNode.TryGetValue(saveNode, out var outs) && outs.Count > 0)
            {
                // Resolve to local path - outs[0] is the filename like "h3_story/h3_story_cast_1_20261008.png"
                var imageFile = outs[0];
                var localPath = await ResolveImageToLocalAsync(imageFile);
                return localPath;
            }

            // Fallback: search for the file on disk
            return FindTokenImageOnDisk(runToken);
        }

        private Task<string?> ResolveImageToLocalAsync(string imageFile)
        {
            try
            {
                var settings = _settingsService.Settings;
                if (settings != null)
                {
                    // Try local output folder first, then remote
                    var outputFolder = settings.OutputFolderPath;
                    if (!string.IsNullOrEmpty(outputFolder))
                    {
                        var srcPath = Path.Combine(outputFolder, imageFile.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(srcPath))
                        {
                            return Task.FromResult<string?>(srcPath);
                        }
                    }

                    // Try remote folder as fallback
                    var remoteFolder = settings.RemoteOutputFolderPath;
                    if (!string.IsNullOrEmpty(remoteFolder))
                    {
                        var srcPath = Path.Combine(remoteFolder, imageFile.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(srcPath))
                        {
                            return Task.FromResult<string?>(srcPath);
                        }
                    }
                }
                return Task.FromResult<string?>(null);
            }
            catch
            {
                return Task.FromResult<string?>(null);
            }
        }

        private string? FindTokenImageOnDisk(string runToken)
        {
            // Check ComfyUI output folder (try local first, then remote)
            var settings = _settingsService.Settings;
            if (settings == null) return null;

            var folders = new[] { settings.OutputFolderPath, settings.RemoteOutputFolderPath }
                .Where(f => !string.IsNullOrEmpty(f) && Directory.Exists(f));

            foreach (var folder in folders)
            {
                var files = Directory.GetFiles(folder, $"*{runToken}*", SearchOption.AllDirectories);
                var match = files.FirstOrDefault(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                                       f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }
            return null;
        }

        // ── Timeline Population ───────────────────────────────────────────────────────────────────

        private async Task PopulateTimelineAsync()
        {
            if (!CanPopulateTimeline) return;

            IsPopulating = true;
            StoryStatus = "Preparing cast photos...";

            try
            {
                // Generate missing cast photos
                await PrepareCastPhotosAsync();

                StoryStatus = "Populating timeline...";
                AddLog($"Populating timeline with {_storyBeats.Count} clips...");

                // Clear existing timeline
                TimelineClips.Clear();

                // Build cast members for prompt generation
                var cast = BuildCastMembers();

                // Calculate clip duration
                var clipDuration = TargetDurationSeconds / _storyBeats.Count;
                clipDuration = Math.Max(2.5, Math.Min(15.0, clipDuration)); // Clamp 2.5-15s

                for (int i = 0; i < _storyBeats.Count; i++)
                {
                    var beat = _storyBeats[i];
                    var env = i < _continuityPlan.Count ? _continuityPlan[i] : StoryContinuity.Environment.None;
                    var isLastClip = i == _storyBeats.Count - 1;

                    // Get previous beat for continuity handoff (null for first clip)
                    StoryBeatSheet.StoryBeat? previousBeat = i > 0 ? _storyBeats[i - 1] : null;

                    // Build the prompt for this clip with beat-to-beat continuity
                    var prompt = BuildClipPrompt(beat, i, cast, env, previousBeat, clipDuration, isLastClip);

                    // Create the timeline clip
                    var clip = new H3TimelineClip
                    {
                        Index = i,
                        DurationSeconds = clipDuration,
                        Prompt = prompt,
                        UseMotionContext = i > 0 && ChainClips,
                    };

                    TimelineClips.Add(clip);

                    AddLog($"Added clip {i + 1}: {beat.Text.Substring(0, Math.Min(50, beat.Text.Length))}...");
                }

                RecalculateTimeline();

                // Set the first clip's prompt as the current prompt text
                if (TimelineClips.Count > 0)
                {
                    SelectedClip = TimelineClips[0];
                    PromptText = TimelineClips[0].Prompt;
                }

                StoryStatus = $"Timeline ready: {TimelineClips.Count} clips, {TotalDuration:0.0}s total";
                AddLog($"Timeline populated with {TimelineClips.Count} clips. Ready to generate!");
            }
            catch (Exception ex)
            {
                AddLog($"Error populating timeline: {ex.Message}");
                StoryStatus = $"Population failed: {ex.Message}";
            }
            finally
            {
                IsPopulating = false;
            }
        }

        private List<H3SpecPrompt.CastMember> BuildCastMembers()
        {
            var members = new List<H3SpecPrompt.CastMember>();
            for (int i = 0; i < Math.Min(4, _detectedCast.Count); i++)
            {
                var (kind, role) = _detectedCast[i];
                // Extract outfit from role if mentioned
                var outfit = ExtractOutfitFromRole(role);
                members.Add(new H3SpecPrompt.CastMember(i + 1, kind, outfit));
            }
            return members;
        }

        private string ExtractOutfitFromRole(string role)
        {
            // Look for clothing-related words in the role description
            var lower = role.ToLowerInvariant();
            var clothingKeywords = new[] { "wearing", "dressed in", "in a", "with a", "wears", "dressed" };

            foreach (var keyword in clothingKeywords)
            {
                var idx = lower.IndexOf(keyword);
                if (idx >= 0)
                {
                    var afterKeyword = role.Substring(idx);
                    // Take until end of sentence or role
                    var endIdx = afterKeyword.IndexOfAny(new[] { '.', ',', ';' });
                    return endIdx > 0 ? afterKeyword.Substring(0, endIdx).Trim() : afterKeyword.Trim();
                }
            }
            return string.Empty;
        }

        private string BuildClipPrompt(
            StoryBeatSheet.StoryBeat beat,
            int clipIndex,
            List<H3SpecPrompt.CastMember> cast,
            StoryContinuity.Environment env,
            StoryBeatSheet.StoryBeat? previousBeat,
            double clipDuration,
            bool isLastClip)
        {
            var sb = new StringBuilder();

            // subject_definitions
            var subjectDefs = H3SpecPrompt.BuildSubjectDefinitions(cast, env, StorySetting);
            sb.AppendLine("subject_definitions:");
            sb.AppendLine(subjectDefs);
            sb.AppendLine();

            // summary with mode marker
            sb.AppendLine("summary:");
            sb.Append(H3SpecPrompt.SummaryMarker);
            sb.Append(" ");
            sb.AppendLine(beat.Text);
            if (beat.PartCount > 1)
            {
                sb.AppendLine(StoryBeatSheet.DescribePart(beat));
            }
            sb.AppendLine();

            // retention_analysis
            sb.AppendLine("retention_analysis:");
            sb.AppendLine(H3SpecPrompt.BuildRetentionAnalysis(cast));
            sb.AppendLine();

            // detailed_description with shot breakdown
            sb.AppendLine("detailed_description:");
            var shots = H3SpecPrompt.ShotCount(clipDuration);
            var cutTimes = H3ResearchPrompt.CutTimes(clipDuration, shots);

            // Add continuity sentence
            var sceneSentence = StoryContinuity.SceneSentence(env);
            if (!string.IsNullOrEmpty(sceneSentence))
            {
                sb.AppendLine($"Continuity of place and time: {sceneSentence}");
            }

            // Add beat-to-beat narrative continuity handoff
            if (previousBeat.HasValue && !string.IsNullOrEmpty(previousBeat.Value.Text))
            {
                sb.AppendLine();
                sb.AppendLine(BuildBeatContinuityHandoff(previousBeat.Value, beat, cast.Count, clipIndex));
                sb.AppendLine();
            }

            // Generate shot descriptions
            for (int s = 0; s < shots; s++)
            {
                if (s == 0)
                {
                    if (previousBeat.HasValue)
                    {
                        // First shot picks up directly from previous beat's ending
                        sb.Append($"[Shot 1] Picking up immediately from the previous clip: ");
                    }
                    else
                    {
                        sb.Append($"[Shot 1] ");
                    }
                }
                else
                {
                    var cutTime = cutTimes[s - 1];
                    sb.Append($"[Shot {s + 1}] At {cutTime}, ");
                }

                // Distribute beat action across shots
                var shotFraction = (double)s / shots;
                var actionPart = GetShotAction(beat.Text, s, shots, isLastClip);

                // Add subject tags
                for (int c = 0; c < cast.Count; c++)
                {
                    if (actionPart.Contains($"CHARACTER {c + 1}", StringComparison.OrdinalIgnoreCase) ||
                        (c == 0 && !actionPart.Contains("<Subject", StringComparison.OrdinalIgnoreCase)))
                    {
                        actionPart = actionPart.Replace($"CHARACTER {c + 1}", $"<Subject {c + 1}>", StringComparison.OrdinalIgnoreCase);
                    }
                }

                // Ensure at least one subject is tagged
                if (!actionPart.Contains("<Subject"))
                {
                    actionPart = $"<Subject 1> {actionPart}";
                }

                sb.AppendLine(actionPart);
            }

            // Add ending momentum for non-last clips
            if (!isLastClip)
            {
                sb.AppendLine();
                sb.AppendLine("CLIP ENDING: This clip ends mid-action, with characters in motion and the scene unresolved. The final frame shows momentum carrying forward into the next clip — no pauses, no held poses, no resolution.");
            }

            sb.AppendLine();

            // overall_soundscape
            sb.AppendLine("overall_soundscape:");
            sb.AppendLine(BuildSoundscape(beat.Text, env));
            sb.AppendLine();

            // non_diegetic_music
            sb.AppendLine("non_diegetic_music:");
            sb.AppendLine(BuildMusicCue(clipIndex, isLastClip, _storyBeats.Count));

            return sb.ToString();
        }

        /// <summary>
        /// Builds the beat-to-beat narrative continuity handoff for seamless clip transitions.
        /// This is used at populate time when we don't have the previous clip's generated content yet.
        /// </summary>
        private string BuildBeatContinuityHandoff(StoryBeatSheet.StoryBeat previousBeat, StoryBeatSheet.StoryBeat currentBeat, int castCount, int clipIndex)
        {
            var sb = new StringBuilder();

            sb.AppendLine($"NARRATIVE CONTINUITY — This is clip {clipIndex + 1} of a continuous sequence. The viewer just watched:");
            sb.AppendLine($"  PREVIOUS BEAT: {previousBeat.Text}");
            sb.AppendLine();
            sb.AppendLine("This clip picks up IMMEDIATELY from there:");
            sb.AppendLine("- Same positions, same states, same ongoing actions as the previous beat's ending");
            sb.AppendLine("- Any motion started in the previous beat continues naturally into this one");
            sb.AppendLine("- No jump cuts, no time skips, no position resets — this is ONE continuous film");

            // Extract ending state from previous beat for specific handoff
            var prevEnding = ExtractBeatEnding(previousBeat.Text);
            if (!string.IsNullOrEmpty(prevEnding))
            {
                sb.AppendLine();
                sb.AppendLine($"The previous clip ended with: {prevEnding}");
                sb.AppendLine("[Shot 1] must show this state continuing, not restarting.");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Extracts the likely ending state from a beat's text — the last sentence or action phrase.
        /// </summary>
        private string ExtractBeatEnding(string beatText)
        {
            if (string.IsNullOrWhiteSpace(beatText)) return string.Empty;

            // Split into sentences and take the last one
            var sentences = beatText.Split(new[] { '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 5)
                .ToList();

            return sentences.Count > 0 ? sentences.Last() : beatText.Trim();
        }

        private string GetShotAction(string beatText, int shotIndex, int totalShots, bool isLastClip)
        {
            // Split the beat into rough parts for each shot
            var sentences = beatText.Split(new[] { '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 5)
                .ToList();

            if (sentences.Count == 0)
            {
                return beatText;
            }

            // Distribute sentences across shots
            var sentenceIndex = (int)((double)shotIndex / totalShots * sentences.Count);
            sentenceIndex = Math.Min(sentenceIndex, sentences.Count - 1);

            var action = sentences[sentenceIndex];

            // Add camera direction based on shot position
            var cameraMove = shotIndex switch
            {
                0 => "Medium shot, the camera pushes in slowly as",
                1 => "Wide shot, the camera tracks laterally following the action as",
                _ when shotIndex == totalShots - 1 && isLastClip => "Close-up, the camera holds steady on",
                _ when shotIndex == totalShots - 1 => "Medium close-up, the camera continues moving as",
                _ => "Medium shot, the camera adjusts to follow"
            };

            return $"{cameraMove} {action}.";
        }

        private string BuildSoundscape(string beatText, StoryContinuity.Environment env)
        {
            var sb = new StringBuilder();

            // Environment sounds
            if (!env.IsEmpty)
            {
                if (!env.Interior)
                {
                    sb.Append("Exterior ambience: ");
                    if (env.Lighting.Contains("rain", StringComparison.OrdinalIgnoreCase))
                        sb.Append("rain falling, wet surfaces, ");
                    else if (env.Hour.Contains("night", StringComparison.OrdinalIgnoreCase))
                        sb.Append("distant city sounds, quiet night ambience, ");
                    else
                        sb.Append("outdoor ambience, ");
                }
                else
                {
                    sb.Append("Interior room tone, ");
                }
            }

            // Action sounds
            var lower = beatText.ToLowerInvariant();
            if (lower.Contains("fight") || lower.Contains("punch") || lower.Contains("kick"))
                sb.Append("impact sounds, grunts of exertion, ");
            if (lower.Contains("run") || lower.Contains("chase"))
                sb.Append("rapid footsteps, heavy breathing, ");
            if (lower.Contains("speak") || lower.Contains("say") || lower.Contains("tell"))
                sb.Append("clear dialogue, ");

            sb.Append("natural movement foley.");
            return sb.ToString();
        }

        private string BuildMusicCue(int clipIndex, bool isLastClip, int totalClips)
        {
            // Build escalating score based on position in story
            var intensity = (double)clipIndex / totalClips;

            if (clipIndex == 0)
            {
                return "Tense orchestral underscore begins, low strings and subtle percussion, building anticipation.";
            }
            else if (isLastClip)
            {
                return "Score reaches climax with full orchestration, then resolves as the action concludes.";
            }
            else if (intensity > 0.7)
            {
                return "Intense action score, driving percussion, brass accents, building toward the climax.";
            }
            else if (intensity > 0.4)
            {
                return "Score intensifies, strings become more urgent, percussion drives the rhythm forward.";
            }
            else
            {
                return "Underscore continues, tension building steadily, strings and subtle percussion.";
            }
        }

        // ── Settings Persistence & Command Notifications ──────────────────────────────────────────

        partial void OnStoryModeEnabledChanged(bool value)
        {
            SaveSettings();
            NotifyStoryCommandsCanExecuteChanged();
        }

        partial void OnStoryTextChanged(string value)
        {
            NotifyStoryCommandsCanExecuteChanged();
        }

        partial void OnIsStoryAnalyzedChanged(bool value)
        {
            NotifyStoryCommandsCanExecuteChanged();
        }

        partial void OnIsAnalyzingChanged(bool value)
        {
            NotifyStoryCommandsCanExecuteChanged();
        }

        partial void OnIsPopulatingChanged(bool value)
        {
            NotifyStoryCommandsCanExecuteChanged();
        }

        partial void OnTargetDurationSecondsChanged(double value) => SaveSettings();
        partial void OnCastPhotoEngineChanged(string value) => SaveSettings();
        partial void OnAutoGenerateCastChanged(bool value) => SaveSettings();

        private void NotifyStoryCommandsCanExecuteChanged()
        {
            AnalyzeStoryCommand?.NotifyCanExecuteChanged();
            PopulateTimelineCommand?.NotifyCanExecuteChanged();
        }
    }
}
