using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using MessageBox = System.Windows.MessageBox;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// 🌀 MiniMax I2V's Analyze: the reference pictures plus a draft idea turned into the six-field Ref2VA
    /// prompt by the llama-server.
    ///
    /// <para><b>Analyze &amp; Queue</b> is the main path. It freezes the form into a queue item on the spot —
    /// pictures, draft, length, every render setting — and writes that item's prompt in the background. The
    /// form is free again the moment the button is pressed, so the next picture can be loaded and analyzed
    /// while the first one is still being written or rendered. When the prompt lands the item turns Pending
    /// and the queue starts if nothing is rendering.</para>
    ///
    /// <para>Everything an analysis reads is captured in an <see cref="AnalysisJob"/> first, and nothing in the
    /// pipeline touches the live form. That is what makes several of them safe at once: the old Analyze read
    /// <c>Prompt</c>, the slots and the continuations while it ran, so it had to lock the tab until it was done
    /// — and with the location plate on, "done" meant waiting for whatever was rendering to give up the GPU.</para>
    ///
    /// <para><b>Write prompt</b> keeps the old behaviour for hand-editing: the same pipeline, with the result
    /// written into the boxes instead of a queue item.</para>
    /// </summary>
    public partial class MiniMaxI2VViewModel
    {
        /// <summary>One reference picture as an analysis sees it. A queued take's location plate has no
        /// <see cref="Path"/> yet — it is rendered when the take reaches the GPU — so the writer is given its
        /// <see cref="PlatePrompt"/> in words instead of the picture.</summary>
        private sealed record AnalysisPicture(string Path, bool IsLocationPlate, string? PlatePrompt = null)
        {
            public bool IsDeferredPlate => IsLocationPlate && string.IsNullOrEmpty(Path);
        }

        /// <summary>Everything an analysis reads, frozen when its button was pressed.</summary>
        private sealed class AnalysisJob
        {
            public List<AnalysisPicture> Pictures { get; init; } = new();
            public string Draft { get; init; } = string.Empty;
            public bool IsUltra { get; init; }
            public double UltraSeconds { get; init; }
            public int LengthSeconds { get; init; }
            public List<int> ContinuationSeconds { get; init; } = new();
            public List<string> MediaInstructions { get; init; } = new();
            public List<string> StereoInstructions { get; init; } = new();
            public string Aspect { get; init; } = string.Empty;

            /// <summary>Whether to render a location plate first, and where in <see cref="Pictures"/> it goes —
            /// the first free slot, so the &lt;Picture N&gt; numbering matches where Write prompt puts it.</summary>
            public bool WantPlate { get; init; }
            public int PlateIndex { get; init; }

            /// <summary>
            /// Analyze &amp; Queue: write the plate's prompt now but leave the render to the take's own turn on
            /// the GPU, so a new row's prompt never waits for the video that is rendering. ✎ Write prompt renders
            /// it straight away, because it puts the picture in a slot for the user to see.
            /// </summary>
            public bool DeferPlateRender { get; init; }

            public CancellationTokenSource Cts { get; } = new();
        }

        private sealed record AnalysisResult(List<string> Segments, List<AnalysisPicture> Pictures, string? PlatePath);
        /// <summary>Background analyses that belong to a queue row, so removing the row can cancel its job
        /// and Retry can run a failed one again.</summary>
        private readonly Dictionary<MiniMaxI2VQueueItem, AnalysisJob> _analysisJobs = new();

        public RelayCommand AnalyzeAndQueueCommand { get; private set; } = null!;

        private bool _showSettings;
        private bool _showMoreReferences;

        /// <summary>Whether the rail's ⚙ Settings block is open. Held here rather than on the toggle so it
        /// survives switching tabs, which rebuilds the page.</summary>
        public bool ShowSettings
        {
            get => _showSettings;
            set { if (_showSettings != value) { _showSettings = value; OnPropertyChanged(); } }
        }

        /// <summary>Whether 💎 Ultra's pictures 5–9 and audio/video slots are open.</summary>
        public bool ShowMoreReferences
        {
            get => _showMoreReferences;
            set { if (_showMoreReferences != value) { _showMoreReferences = value; OnPropertyChanged(); } }
        }

        /// <summary>Deliberately not gated on another analysis or a render — that is the point of it.</summary>
        public bool CanAnalyzeAndQueue => IsUltra ? HasUltraVisualReference : HasReference;
        private void InitAnalysis()
        {
            AnalyzeAndQueueCommand = new RelayCommand(AnalyzeAndQueue, () => CanAnalyzeAndQueue);
        }

        private AnalysisJob SnapshotAnalysis(bool deferPlateRender)
        {
            var pictures = (IsUltra ? UltraPictures : FilledReferences)
                .Select(r => new AnalysisPicture(r.Path, r.IsLocationPlate))
                .ToList();

            var hasPlate = References.Any(r => r.IsLocationPlate && r.HasImage);
            var freeSlot = FreePlateSlot();
            if (AutoLocationPlate && hasPlate)
                AddLog("Location plate: reusing the one already in the references.");
            else if (AutoLocationPlate && PrimaryStereo != StereoLayout.None)
                AddLog("Location plate: skipped — Picture 1 is a stereo pair and already sets the scene.");
            else if (AutoLocationPlate && freeSlot == null)
                AddLog("Location plate: skipped — all four reference slots are full.");

            return new AnalysisJob
            {
                Pictures = pictures,
                Draft = Prompt.Trim(),
                IsUltra = IsUltra,
                UltraSeconds = Ultra.DurationSeconds,
                LengthSeconds = LengthSeconds,
                ContinuationSeconds = IsUltra ? new List<int>() : Continuations.Select(c => c.Seconds).ToList(),
                MediaInstructions = IsUltra ? UltraMediaInstructions() : new List<string>(),
                StereoInstructions = StereoInstructions(),
                Aspect = IsUltra ? UltraResolvedAspect : ResolvedAspectRatio,
                WantPlate = AutoLocationPlate && !hasPlate && PrimaryStereo == StereoLayout.None && freeSlot != null,
                PlateIndex = freeSlot == null ? pictures.Count : freeSlot.Slot - 1,
                DeferPlateRender = deferPlateRender,
            };
        }

        #region Analyze & Queue

        /// <summary>
        /// Freezes the form into a queue row now and writes its prompt in the background. Returns at once:
        /// the row shows "Writing prompt" until the reply lands, then turns Pending and starts the queue if
        /// nothing is rendering.
        /// </summary>
        private void AnalyzeAndQueue()
        {
            if (!CanAnalyzeAndQueue) return;
            if (!IsUltra && !ConfirmMemoryRisk()) return;

            var job = SnapshotAnalysis(deferPlateRender: true);
            var item = IsUltra ? BuildUltraQueueItem() : BuildRef2VAQueueItem();
            item.Prompt = job.Draft;
            item.ContinuationPrompts = job.ContinuationSeconds.Select(_ => string.Empty).ToList();
            item.NeedsAnalysis = true;
            item.Status = MiniMaxI2VQueueItem.AnalyzingStatus;

            _analysisJobs[item] = job;
            _queue.Add(item);
            AddLog($"Writing the prompt in the background — {item.DisplayText}");
            UpdateQueueStatus();

            _ = RunQueuedAnalysisAsync(item, job);
        }

        private async Task RunQueuedAnalysisAsync(MiniMaxI2VQueueItem item, AnalysisJob job)
        {
            try
            {
                var result = await RunAnalysisAsync(job, job.Cts.Token);
                if (!_queue.Contains(item)) return;

                var continuations = item.IsUltra ? new List<string>() : result.Segments.Skip(1).ToList();
                var empty = continuations
                    .Select((p, i) => (p, i))
                    .Where(t => string.IsNullOrWhiteSpace(t.p))
                    .Select(t => (t.i + 1).ToString())
                    .ToList();
                if (empty.Count > 0)
                    throw new Exception($"continuation {string.Join(", ", empty)} came back without a prompt.");

                item.Prompt = result.Segments[0];
                item.ContinuationPrompts = continuations;
                item.ReferencePaths = result.Pictures.Where(p => !p.IsDeferredPlate).Select(p => p.Path).ToList();

                // The plate the prompt was written against, rendered when this row reaches the GPU.
                var plateAt = result.Pictures.FindIndex(p => p.IsDeferredPlate);
                item.LocationPlatePrompt = plateAt < 0 ? null : result.Pictures[plateAt].PlatePrompt;
                item.LocationPlateIndex = Math.Max(plateAt, 0);

                item.NeedsAnalysis = false;
                item.ErrorMessage = null;
                item.ItemStatus = QueueItemStatus.Pending;
                item.NotifyPromptChanged();
                _analysisJobs.Remove(item);
                job.Cts.Dispose();

                AddLog($"Prompt written — queued: {item.DisplayText}");
                UpdateQueueStatus();
                SaveQueueToFile();

                // Start it now if the GPU is idle; a running queue picks it up on its own.
                if (!IsProcessingQueue) _ = ProcessQueueAsync();

                // Filed as soon as it exists, not only when it renders.
                await SavePromptAsync(item.Prompt, item.ContinuationPrompts, item.ContinuationSeconds,
                    item.ReferencePaths, item.LengthSeconds, item.AspectRatio, manual: false);
            }
            catch (OperationCanceledException)
            {
                // The row was removed or the queue cleared.
            }
            catch (Exception ex)
            {
                if (!_queue.Contains(item)) return;
                item.ItemStatus = QueueItemStatus.Failed;
                item.ErrorMessage = $"Analysis failed: {ex.Message}";
                AddLog($"ERROR during analysis: {ex.Message} — press 🔄 Retry to try again.");
                UpdateQueueStatus();
            }
        }

        /// <summary>Puts a row whose analysis failed back through the writer.</summary>
        private bool RetryAnalysis(MiniMaxI2VQueueItem item)
        {
            if (!item.NeedsAnalysis || !_analysisJobs.TryGetValue(item, out var job)) return false;
            item.ErrorMessage = null;
            item.Status = MiniMaxI2VQueueItem.AnalyzingStatus;
            _ = RunQueuedAnalysisAsync(item, job);
            return true;
        }

        private void CancelAnalysis(MiniMaxI2VQueueItem item)
        {
            if (!_analysisJobs.Remove(item, out var job)) return;
            job.Cts.Cancel();
        }

        private void CancelAllAnalyses()
        {
            foreach (var job in _analysisJobs.Values) job.Cts.Cancel();
            _analysisJobs.Clear();
        }

        #endregion

        #region Write prompt (into the boxes)

        private async Task AnalyzeAsync()
        {
            if (!CanAnalyze) return;

            IsAnalyzing = true;
            var job = SnapshotAnalysis(deferPlateRender: false);

            try
            {
                var result = await RunAnalysisAsync(job, job.Cts.Token);

                if (result.PlatePath != null && FreePlateSlot() is { } slot)
                {
                    slot.SetLocationPlate(result.PlatePath);
                    AddLog($"Location plate: Picture {slot.Slot} — {Path.GetFileName(result.PlatePath)}.");
                    OnPropertyChanged(nameof(LocationPlateSummary));
                }

                Prompt = result.Segments[0];
                for (var i = 0; i < Continuations.Count && i + 1 < result.Segments.Count; i++)
                    if (!string.IsNullOrWhiteSpace(result.Segments[i + 1]))
                        Continuations[i].Prompt = result.Segments[i + 1];
                OnCanExecuteChanged();

                await SaveCurrentPromptAsync(manual: false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AddLog($"ERROR during analysis: {ex.Message}");
                MessageBox.Show($"Analysis failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsAnalyzing = false;
                job.Cts.Dispose();
            }
        }

        #endregion

        #region The pipeline

        /// <summary>
        /// Model, system prompt, optional location plate, the reply, and a repair pass for any continuation the
        /// reply left out. Reads only <paramref name="job"/>, never the form.
        /// </summary>
        private async Task<AnalysisResult> RunAnalysisAsync(AnalysisJob job, CancellationToken token)
        {
            var baseUrl = _settingsService.Settings?.LMStudioSettings?.BaseUrl ?? "http://alien:8080";
            await _lmStudioService.SetBaseUrlAsync(baseUrl);

            var models = await _lmStudioService.GetAvailableModelsAsync(token);
            var model = _settingsService.Settings?.LMStudioSettings?.SelectedModel ?? string.Empty;
            if (string.IsNullOrEmpty(model) && models.Count > 0)
                model = models[0].Id ?? models[0].Name ?? string.Empty;
            if (string.IsNullOrEmpty(model))
                throw new Exception("no LM Studio / llama-server model is available. Make sure the server is running and a model is loaded.");

            var promptFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "prompts", "prompt2json", SystemPromptFile);
            if (!File.Exists(promptFilePath))
                throw new FileNotFoundException($"System prompt not found: {promptFilePath}");
            var systemPrompt = await File.ReadAllTextAsync(promptFilePath, token);

            var pictures = job.Pictures.ToList();
            string? plate = null;
            if (job.WantPlate && await WriteLocationPlatePromptAsync(model, job, token) is { } platePrompt)
            {
                var at = Math.Clamp(job.PlateIndex, 0, pictures.Count);
                if (job.DeferPlateRender)
                {
                    pictures.Insert(at, new AnalysisPicture(string.Empty, true, platePrompt));
                }
                else
                {
                    plate = await RenderLocationPlateAsync(platePrompt, job.Aspect, holdsLease: false, token);
                    if (plate != null) pictures.Insert(at, new AnalysisPicture(plate, true));
                }
            }

            AddLog($"Writing the Ref2VA prompt — {pictures.Count} picture(s) → {_lmStudioService.DescribeTarget(model)}");

            var reply = await AskWriterAsync(model, pictures, BuildAnalyzeRequest(job, pictures), systemPrompt, 4000, token);
            var clean = CleanLLMOutput(reply);
            if (string.IsNullOrWhiteSpace(clean))
                throw new Exception("the prompt writer returned an empty reply.");

            // "No music" in words gets music; the field is written N/A instead.
            var blocks = SplitSegments(clean).Select(H3SpecPrompt.NormalizeMusicSections).ToList();
            var segments = new List<string> { blocks[0] };
            AddLog($"Segment 1 prompt written ({blocks[0].Length} chars)");

            // Ultra renders one pass; continuations belong to the Ref2VA workflow.
            for (var i = 0; i < job.ContinuationSeconds.Count; i++)
                segments.Add(i + 1 < blocks.Count ? blocks[i + 1] : string.Empty);

            await RepairMissingSegmentsAsync(model, systemPrompt, job, pictures, segments, token);
            return new AnalysisResult(segments, pictures, plate);
        }

        /// <summary>
        /// One call to the writer, run on the thread pool: the service resizes and base64-encodes every picture
        /// before it sends anything, and on the UI thread that alone was enough to stall the window.
        /// </summary>
        private Task<string> AskWriterAsync(
            string model, IReadOnlyList<AnalysisPicture> pictures, string request, string systemPrompt,
            int maxTokens, CancellationToken token)
        {
            var paths = pictures.Where(p => !p.IsDeferredPlate).Select(p => p.Path).ToList();
            return Task.Run(() => _lmStudioService.AnalyzeMultipleImagesWithSystemPromptAsync(
                model, paths, request, systemPrompt, maxTokens: maxTokens, cancellationToken: token), token);
        }

        /// <summary>
        /// The user message: what each picture is, the draft idea, and — when the take is continued — the
        /// per-segment durations the model has to pace its beats against.
        /// </summary>
        private static string BuildAnalyzeRequest(AnalysisJob job, IReadOnlyList<AnalysisPicture> pictures)
        {
            var lines = DescribePictures(pictures);

            lines.Add(string.Empty);
            if (job.IsUltra)
            {
                lines.AddRange(job.MediaInstructions);
                lines.Add($"Write ONE segment. Target duration: {job.UltraSeconds:0.#} seconds.");
            }
            else if (job.ContinuationSeconds.Count == 0)
            {
                lines.Add($"Write ONE segment. Target duration: {job.LengthSeconds} seconds.");
            }
            else
            {
                lines.Add($"Write {job.ContinuationSeconds.Count + 1} segments, in order, separated by the segment markers:");
                lines.Add($"  Segment 1 — {job.LengthSeconds} seconds.");
                for (var i = 0; i < job.ContinuationSeconds.Count; i++)
                    lines.Add($"  Segment {i + 2} — {job.ContinuationSeconds[i]} seconds, continuing directly out of segment {i + 1}.");
            }

            lines.AddRange(job.StereoInstructions);

            lines.Add(string.Empty);
            lines.Add("Draft idea from the user:");
            lines.Add(string.IsNullOrWhiteSpace(job.Draft)
                ? "(none — build a single natural beat out of what the pictures show, and add nothing beyond it)"
                : job.Draft);

            return string.Join("\n", lines);
        }

        private static List<string> DescribePictures(IReadOnlyList<AnalysisPicture> pictures)
        {
            var lines = new List<string> { $"You are given {pictures.Count} reference picture(s), in order:" };
            for (var i = 0; i < pictures.Count; i++)
            {
                var picture = pictures[i];
                if (picture.IsDeferredPlate)
                    lines.Add($"  <Picture {i + 1}> — {LocationPlateNote} It is not attached here (it is rendered "
                              + $"just before the video); it shows: {picture.PlatePrompt}");
                else if (picture.IsLocationPlate)
                    lines.Add($"  <Picture {i + 1}> — {LocationPlateNote}");
                else
                    lines.Add($"  <Picture {i + 1}> — {Path.GetFileNameWithoutExtension(picture.Path)}");
            }
            if (pictures.Any(p => p.IsDeferredPlate))
                lines.Add("The attached images are the other pictures, in the order above.");
            return lines;
        }

        /// <summary>
        /// Re-asks for any continuation the first reply left empty, one at a time.
        ///
        /// <para>A single reply carrying every segment is cheaper and keeps the take coherent, so it stays
        /// the first attempt — but whether the model emits the <c>=== SEGMENT n ===</c> markers is sampling
        /// luck. Each missing segment is requested on its own, with the segment before it supplied as context
        /// so the continuation still picks up where its predecessor ended.</para>
        ///
        /// <para>Deliberately sequential: segment 3's context is segment 2, which may itself have just been
        /// repaired.</para>
        /// </summary>
        private async Task RepairMissingSegmentsAsync(
            string model, string systemPrompt, AnalysisJob job, IReadOnlyList<AnalysisPicture> pictures,
            List<string> segments, CancellationToken token)
        {
            var missing = segments.Skip(1).Count(string.IsNullOrWhiteSpace);
            if (missing == 0) return;
            AddLog($"{missing} continuation(s) came back without a segment marker — asking for them one at a time");

            for (var i = 1; i < segments.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(segments[i])) continue;
                token.ThrowIfCancellationRequested();

                var previous = segments[i - 1];
                if (string.IsNullOrWhiteSpace(previous))
                {
                    AddLog($"WARNING: continuation {i} has no preceding segment to continue from — skipped");
                    continue;
                }

                var reply = await AskWriterAsync(model, pictures, BuildRepairRequest(job, pictures, i, previous),
                    systemPrompt, 2500, token);

                // The reply should be one segment, but a model that ignored "no marker" would wrap it —
                // take the first block either way.
                var text = H3SpecPrompt.NormalizeMusicSections(
                    SplitSegments(CleanLLMOutput(reply)).FirstOrDefault() ?? string.Empty);
                if (string.IsNullOrWhiteSpace(text))
                {
                    AddLog($"WARNING: continuation {i} still came back empty");
                    continue;
                }

                segments[i] = text;
                AddLog($"Continuation {i} written on retry ({text.Length} chars)");
            }
        }

        /// <summary>
        /// Asks for exactly one continuation (<paramref name="continuation"/> is 1-based).
        ///
        /// <para>The user's draft is supplied alongside the preceding segment, and that is not optional.
        /// The Ref2VA system prompt forbids inventing action, so a request carrying only the previous
        /// segment leaves the model nothing it is permitted to write — and it answers by restating that
        /// segment almost verbatim. The draft is what makes advancing the action a compliant answer.</para>
        /// </summary>
        private static string BuildRepairRequest(
            AnalysisJob job, IReadOnlyList<AnalysisPicture> pictures, int continuation, string previous)
        {
            var lines = DescribePictures(pictures);

            lines.Add(string.Empty);
            lines.Add("The user's idea for the WHOLE take:");
            lines.Add(string.IsNullOrWhiteSpace(job.Draft)
                ? "(none given — carry the action forward naturally from the segment below, one new beat only)"
                : job.Draft);

            lines.Add(string.Empty);
            lines.Add($"The take runs as {job.ContinuationSeconds.Count + 1} segments of "
                      + $"{job.LengthSeconds}s + {string.Join("s + ", job.ContinuationSeconds)}s.");
            lines.Add($"Write ONLY segment {continuation + 1}, lasting {job.ContinuationSeconds[continuation - 1]} seconds — the part of "
                      + "the user's idea that falls in that stretch, and nothing from any other segment.");
            lines.Add("Output the six Ref2VA fields exactly once. Do NOT write a segment marker line.");
            lines.Add("Its first interval restarts at 0.0s — timestamps are local to this segment, not cumulative.");
            lines.Add("Re-establish the subjects by their <Subject N> bindings, and keep wardrobe, setting and "
                      + "lighting identical so nothing drifts across the join.");

            lines.AddRange(job.StereoInstructions);

            lines.Add(string.Empty);
            lines.Add("The segment immediately before yours is below. It is CONTEXT ONLY — your segment starts "
                      + "where it ends and must carry the action forward. Do not repeat its events, do not "
                      + "re-describe its beat, and do not copy its detailed_description:");
            lines.Add(previous.Trim());

            return string.Join("\n", lines);
        }

        #endregion
    }
}
