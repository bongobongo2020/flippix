using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FlipPix.ComfyUI.Models;
using FlipPix.ComfyUI.Services;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using Application = System.Windows.Application;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// H3 Ensemble, part five: the queue, the render, and the submit-time patches that turn
    /// <c>h3-cast-hybrid.json</c> into an N-character graph — the reference wiring, one cloned face-refine
    /// block per character in the clip, and the reachability prune that deletes whatever is left over.
    /// </summary>
    public partial class H3EnsembleViewModel
    {
        #region Queue

        public ObservableCollection<H3EnsembleQueueItem> Queue => _queue;

        public bool HasQueueItems => _queue.Count > 0;
        public bool HasPendingItems => _queue.Any(x => x.ItemStatus == QueueItemStatus.Pending);
        public bool HasFailedItems => _queue.Any(x => x.ItemStatus == QueueItemStatus.Failed);

        public bool IsProcessingQueue
        {
            get => _isProcessingQueue;
            private set
            {
                if (_isProcessingQueue == value) return;
                _isProcessingQueue = value;
                OnPropertyChanged();
                OnCanExecuteChanged();
            }
        }

        public string QueueStatus
        {
            get => _queueStatus;
            private set { if (_queueStatus != value) { _queueStatus = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// Freezes the form into queue items and starts the drain loop if it is not already running. The
        /// prompt box — not the duration slider — decides how many items are queued: it is split on its
        /// <c>=== CLIP n of N ===</c> headers and each clip becomes one job, so a hand-edited chain queues
        /// exactly what is on screen.
        /// </summary>
        private void AddToQueue()
        {
            if (!CanGenerate) return;

            // Re-assembled rather than trusted: editing the wardrobe box, adding a keyframe or switching a
            // character's sex and pressing Add to Queue is enough to bring the prompt back into line.
            var chain = AssembleChain(Prompt);
            if (chain != Prompt) Prompt = chain;

            var clips = SplitClips(chain);
            if (clips.Count == 0) return;

            // Last line of defence, and the cheapest one in the app. Analyze drops duplicates already, but
            // the prompt box is editable, a chain can be pasted in or restored from a saved queue, and every
            // clip of a story shares one seed — so an identical prompt is an identical file, found out at the
            // cost of a full render.
            var repeats = FindRepeatedClips(clips);
            if (repeats.Count > 0)
            {
                AddLog($"WARNING: {DescribeRepeats(repeats)} — the same prompt on the chain's shared seed " +
                       "renders the same file, so the duplicates are not queued. Re-run Analyze for more " +
                       "beats, or edit them by hand.");
                clips = clips.Where((_, i) => !repeats.ContainsKey(i + 1)).ToList();
                if (clips.Count == 0) return;
                chain = JoinClips(clips);
                Prompt = chain;
            }

            var storyId = clips.Count > 1
                ? $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}"[..20]
                : string.Empty;

            // One seed for the whole chain: the prompts differ so the clips still differ, but the per-clip
            // re-roll of the noise is one more thing that made the cast look subtly re-cast between beats.
            var storySeed = Seed >= 0 || clips.Count == 1
                ? Seed
                : System.Random.Shared.NextInt64(0, long.MaxValue);

            var keys = OrderedKeyframes;
            var environment = WiresEnvironment ? EnvironmentPath : string.Empty;

            for (var i = 0; i < clips.Count; i++)
            {
                var clip = clips[i];
                // A hand-placed timeline lives inside one pass, so it belongs to clip 1 alone; a storyboard
                // still is rendered per clip and is that clip's own opening frame. See KeyframesForClip.
                var clipKeys = KeyframesForClip(i + 1);

                // The whole loaded cast is frozen onto the item, not just this clip's — the submit path
                // re-reads the prompt to decide who is actually sent, and re-queueing an edited chain has to
                // be able to put somebody back in.
                var cast = LoadedCharacters.Select(slot =>
                {
                    var plan = ReferencePlanFor(slot);
                    return new EnsembleCastMember
                    {
                        Index = slot.Index,
                        Noun = slot.Noun,
                        Role = slot.Role,
                        IsPerson = slot.IsPerson,
                        IsGroup = slot.IsGroup,
                        SourcePath = slot.SourcePath,
                        SheetPath = slot.SheetPath,
                        PanelPaths = slot.PanelPaths.ToList(),
                        // Frozen with the prompt, because the prompt's picture numbering was written from
                        // exactly this list.
                        PanelIndices = plan.Indices.ToList(),
                        PanelViews = plan.Views.ToList(),
                    };
                }).ToList();

                var inClip = cast.Where(m => HybridCastPrompt.IncludesSubject(clip, m.Index)).ToList();
                var pictures = clipKeys.Count +
                               inClip.Sum(m => Math.Max(1, m.PanelIndices.Count)) +
                               (environment.Length > 0 ? 1 : 0);
                if (pictures > MaxReferenceImages)
                    AddLog($"WARNING: clip {i + 1} carries {clipKeys.Count} lock(s), " +
                           $"{inClip.Count} character(s) and " +
                           $"{(environment.Length > 0 ? "the location" : "no location")} — {pictures} " +
                           $"pictures, more than the {MaxReferenceImages} slots MiniMaxH3ReferenceToVideo " +
                           "has, and it will fail at submit. Set References to Auto, drop a keyframe, or " +
                           "rewrite that beat around fewer people.");

                var item = new H3EnsembleQueueItem
                {
                    KeyframePaths = clipKeys.Select(k => k.Path).ToList(),
                    KeyframeSeconds = clipKeys.Select(k => k.Seconds).ToList(),
                    Cast = cast,
                    EnvironmentPath = environment,
                    Prompt = clip,
                    // Frozen here, not derived at submit time: they need the keyframes, the cast and the
                    // wardrobe box, and by then the form may have moved on.
                    // Only the people. H3FaceTrackCrop tracks human faces, so a pass aimed at a cloud
                    // either finds nothing or finds somebody else's face and redraws that.
                    RefinePrompts = FaceRefine
                        ? inClip.Where(m => m.IsPerson)
                                .ToDictionary(m => m.Index, m => RefinePromptFor(clip, m.Index))
                        : new Dictionary<int, string>(),
                    AspectRatio = ResolvedAspectRatio,
                    Megapixels = Megapixels,
                    LengthSeconds = ClampLength(LengthSeconds),
                    Medium = SelectedMedium,
                    Seed = storySeed,
                    FaceRefine = FaceRefine,
                    RefineDenoise = RefineDenoise,
                    Interpolate = Interpolate,
                    RtxUpscale = RtxUpscale,
                    StoryId = storyId,
                    ClipIndex = i + 1,
                    ClipCount = clips.Count,
                    ItemStatus = QueueItemStatus.Pending,
                };

                // The pipeline switches the render graph itself cares about — the turbo pipeline's on the
                // H3 Multi tab, nothing extra on this one.
                ConfigureQueuedItem(item);

                _queue.Add(item);
                AddLog($"Queued: {item.DisplayText}");
            }

            AddLog(PicturePlanSummary);
            AddLog(CastCoverageSummary);

            var staleShots = _storyboard.Where(sb => sb.IsStale && sb.Use).Select(sb => sb.ClipIndex).ToList();
            if (staleShots.Count > 0)
                AddLog($"WARNING: the storyboard still(s) for clip(s) {string.Join(", ", staleShots)} were " +
                       "rendered for a beat that has been rewritten since. They are being locked in as those " +
                       "clips' opening frames anyway — re-roll them, or untick them, and re-queue.");
            foreach (var note in IdentityAdvisories()) AddLog(note);

            if (clips.Count > 1)
            {
                var storyboarded = Enumerable.Range(1, clips.Count).Where(n => UsedStoryboard.ContainsKey(n)).ToList();
                if (storyboarded.Count > 0)
                    AddLog($"Clip(s) {string.Join(", ", storyboarded)} open on the storyboard still H3 rendered " +
                           "for them, locked at 0.00s. The rest are continuous takes carried by the cast " +
                           "references.");
                if (keys.Count > 0)
                    AddLog($"The {keys.Count} hand-placed keyframe lock(s) are attached to clip 1 only — a " +
                           "timeline lives inside a single pass.");

                AddLog($"Story queued: {clips.Count} clips × {ClampLength(LengthSeconds):0.#}s " +
                       $"→ {clips.Count * ClampLength(LengthSeconds):0.#}s of video, rendered one at a time " +
                       $"and joined when the last one lands. All {clips.Count} share seed {storySeed}.");
            }

            SaveQueueToFile();

            if (HasCastWardrobe)
            {
                var stale = LoadedCharacters.Where(c => !c.SheetMatchesWardrobe).ToList();
                AddLog(stale.Count == 0
                    ? "Wardrobe: the character sheets show the locked outfits, so the references and the prompts " +
                      "agree on the clothes."
                    : $"WARNING: character {string.Join(", ", stale.Select(c => c.Index))}'s sheet does not " +
                      "show the locked wardrobe (it was built earlier, or loaded as-is). The prompt says one " +
                      "thing and the reference photograph shows another — rebuild the sheets and re-queue.");
            }
            else
            {
                AddLog("WARNING: no wardrobe is locked, so each clip dresses the cast from its own description " +
                       "— that is what makes them change clothes between clips. Press 🎽 Derive and re-queue.");
            }

            UpdateQueueStatus();

            if (!IsProcessingQueue) _ = ProcessQueueAsync();
        }

        private void RemoveQueueItem(H3EnsembleQueueItem? item)
        {
            if (item == null || item.ItemStatus == QueueItemStatus.Processing) return;
            _queue.Remove(item);
            SaveQueueToFile();
            UpdateQueueStatus();
        }

        private void ClearQueue()
        {
            _queueCts?.Cancel();
            _queue.Clear();
            SaveQueueToFile();
            UpdateQueueStatus();
            AddLog("Queue cleared");
        }

        private void StopQueue() => _queueCts?.Cancel();

        private void CancelEverything()
        {
            _sheetCts?.Cancel();
            _storyboardCts?.Cancel();
            _queueCts?.Cancel();
            _wardrobeCts?.Cancel();
        }

        private void ReprocessAllFailed()
        {
            var failed = _queue.Where(x => x.ItemStatus == QueueItemStatus.Failed).ToList();
            if (failed.Count == 0) return;
            foreach (var item in failed)
            {
                item.ItemStatus = QueueItemStatus.Pending;
                item.ErrorMessage = null;
            }
            UpdateQueueStatus();
            SaveQueueToFile();
            if (!IsProcessingQueue) _ = ProcessQueueAsync();
        }

        private void UpdateQueueStatus()
        {
            var pending = _queue.Count(x => x.ItemStatus == QueueItemStatus.Pending);
            var running = _queue.Count(x => x.ItemStatus == QueueItemStatus.Processing);
            var done = _queue.Count(x => x.ItemStatus == QueueItemStatus.Completed);
            var failed = _queue.Count(x => x.ItemStatus == QueueItemStatus.Failed);
            QueueStatus = _queue.Count == 0
                ? string.Empty
                : $"{pending} pending • {running} running • {done} done • {failed} failed";

            OnPropertyChanged(nameof(HasPendingItems));
            OnPropertyChanged(nameof(HasFailedItems));
            OnCanExecuteChanged();
        }

        /// <summary>
        /// Drains pending items one at a time. The coordinator lease is taken <b>per item</b> rather than
        /// around the loop, so a long queue does not lock every other tab out of ComfyUI for its whole run.
        /// </summary>
        private async Task ProcessQueueAsync()
        {
            if (IsProcessingQueue) return;

            IsProcessingQueue = true;
            _queueCts?.Dispose();
            _queueCts = new CancellationTokenSource();
            var token = _queueCts.Token;

            AddLog($"Starting {TabLogName} queue...");
            try
            {
                H3EnsembleQueueItem? item;
                while (!token.IsCancellationRequested &&
                       (item = _queue.FirstOrDefault(x => x.ItemStatus == QueueItemStatus.Pending)) != null)
                {
                    item.ItemStatus = QueueItemStatus.Processing;
                    item.StartedAt = DateTime.Now;
                    UpdateQueueStatus();
                    SaveQueueToFile();

                    try
                    {
                        await GenerateItemAsync(item, token);
                        item.ItemStatus = QueueItemStatus.Completed;
                        item.CompletedAt = DateTime.Now;
                        AddLog($"Completed: {item.DisplayText}");
                        await CompleteStoryAsync(item, token);
                    }
                    catch (OperationCanceledException)
                    {
                        item.ItemStatus = QueueItemStatus.Pending;
                        AddLog("Queue stopped — the current item is back to Pending.");
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (await TryHandleCrashAndRetryAsync(item, ex))
                        {
                            item.ItemStatus = QueueItemStatus.Pending;
                            AddLog("Item reset to Pending — will retry after ComfyUI restart");
                        }
                        else
                        {
                            item.ItemStatus = QueueItemStatus.Failed;
                            item.ErrorMessage = ex.Message;
                            AddLog($"FAILED: {ex.Message}");
                        }
                    }

                    UpdateQueueStatus();
                    SaveQueueToFile();
                }
            }
            finally
            {
                IsProcessingQueue = false;
                ProcessingStatus = token.IsCancellationRequested ? "Queue stopped" : "Queue finished";
                AddLog("Queue processing finished.");
                OnCanExecuteChanged();
            }
        }

        /// <summary>
        /// Runs once the <i>last</i> clip of a chain lands: announces the finished story, then FFmpeg-joins
        /// its clips into one continuous video. Deliberately exception-free — the drain loop's catch would
        /// otherwise read a join failure as a render failure.
        /// </summary>
        private async Task CompleteStoryAsync(H3EnsembleQueueItem finished, CancellationToken token)
        {
            try
            {
                if (!finished.IsStoryClip || string.IsNullOrEmpty(finished.StoryId)) return;

                var siblings = _queue.Where(x => x.StoryId == finished.StoryId)
                                     .OrderBy(x => x.ClipIndex)
                                     .ToList();

                if (siblings.Any(x => x.ItemStatus != QueueItemStatus.Completed))
                {
                    var stalled = siblings.Count(x => x.ItemStatus == QueueItemStatus.Failed);
                    if (stalled > 0 && !siblings.Any(x => x.ItemStatus is QueueItemStatus.Pending or QueueItemStatus.Processing))
                        AddLog($"Story not joined: {stalled} of {siblings.Count} clips failed. " +
                               "Retry them and the join runs when the last one lands.");
                    return;
                }

                var total = siblings.Sum(x => ClampLength(x.LengthSeconds));
                AddLog($"=== Story complete: {siblings.Count} clips, {total:0.#}s total ===");
                foreach (var clip in siblings)
                    AddLog($"  clip {clip.ClipIndex}/{clip.ClipCount}: {clip.OutputVideoPath}");

                await JoinStoryAsync(finished.StoryId, siblings, token);
            }
            catch (Exception ex)
            {
                AddLog($"Story join failed: {ex.Message}");
            }
        }

        private async Task JoinStoryAsync(string storyId, IReadOnlyList<H3EnsembleQueueItem> clips,
            CancellationToken token)
        {
            var paths = clips.Select(c => c.OutputVideoPath)
                             .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
                             .Select(p => p!)
                             .ToList();

            if (paths.Count < clips.Count)
                AddLog($"Join: {clips.Count - paths.Count} clip file(s) are missing from disk and are left out.");
            if (paths.Count < 2)
            {
                AddLog("Join skipped: fewer than two clip files are available.");
                return;
            }

            var ffmpeg = FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpeg))
            {
                AddLog("Join skipped: FFmpeg not found. The clips are separate files, in playback order.");
                return;
            }

            var outputDir = Path.GetDirectoryName(paths[0])
                            ?? Path.Combine(_settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), OutputFolderName);
            Directory.CreateDirectory(outputDir);

            var joinedPath = Path.Combine(outputDir, $"H3Ensemble_{storyId}_joined.mp4");
            var total = clips.Sum(c => ClampLength(c.LengthSeconds));

            ProcessingStatus = $"Joining {paths.Count} clips...";
            AddLog($"Joining {paths.Count} clips with FFmpeg → {Path.GetFileName(joinedPath)}");
            await ConcatClipsAsync(ffmpeg, paths, joinedPath, token);

            if (!File.Exists(joinedPath) || new FileInfo(joinedPath).Length == 0)
            {
                AddLog("Join produced no file — the individual clips are unaffected.");
                return;
            }

            await LocalCopyService.CopyVideoAsync(joinedPath);

            var fi = new FileInfo(joinedPath);
            Application.Current.Dispatcher.Invoke(() =>
            {
                ResultVideoPath = joinedPath;
                ResultVideoInfo = $"H3 Ensemble • joined story • {paths.Count} clips • {total:0.#}s • " +
                                  $"{fi.Length / 1024 / 1024.0:F1}MB";
                HasResult = true;
                OnCanExecuteChanged();
            });
            ProcessingStatus = "Clips joined!";
            AddLog($"=== Joined video complete: {joinedPath} ===");
        }

        /// <summary>
        /// FFmpeg concat-demuxer join, re-encoded rather than stream-copied: H3 writes an audio track per
        /// clip, and a copy-mode concat of separately encoded H3 outputs is where the timestamp and
        /// codec-parameter edge cases live.
        /// </summary>
        private async Task ConcatClipsAsync(string ffmpeg, IReadOnlyList<string> clips, string outPath,
            CancellationToken token)
        {
            var listPath = Path.Combine(Path.GetTempPath(), $"h3ensemble_concat_{Guid.NewGuid():N}.txt");
            var sb = new StringBuilder();
            foreach (var clip in clips)
                sb.AppendLine($"file '{clip.Replace("\\", "/").Replace("'", @"'\''")}'");
            await File.WriteAllTextAsync(listPath, sb.ToString(), token);

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpeg,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                foreach (var a in new[]
                {
                    "-y", "-f", "concat", "-safe", "0", "-i", listPath,
                    "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
                    "-c:a", "aac", "-b:a", "192k", "-pix_fmt", "yuv420p", outPath
                }) psi.ArgumentList.Add(a);

                using var p = System.Diagnostics.Process.Start(psi)
                              ?? throw new Exception("Failed to start FFmpeg.");
                var stderr = await p.StandardError.ReadToEndAsync(token);
                await p.WaitForExitAsync(token);
                if (p.ExitCode != 0)
                {
                    var tail = stderr.Length <= 600 ? stderr : stderr[^600..];
                    throw new Exception($"FFmpeg exited {p.ExitCode}: {tail}");
                }
            }
            finally
            {
                try { File.Delete(listPath); } catch { /* temp file: best effort */ }
            }
        }

        #endregion

        #region Queue persistence

        private void SaveQueueToFile()
        {
            try
            {
                var dir = Path.GetDirectoryName(QueueFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var pending = _queue.Where(q => q.ItemStatus != QueueItemStatus.Completed).ToList();
                File.WriteAllText(QueueFilePath,
                    JsonSerializer.Serialize(pending, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { AddLog($"Error saving queue: {ex.Message}"); }
        }

        /// <summary>Defers the persisted queue read off the constructor — this view model is built during app
        /// startup and must not do disk work there.</summary>
        private void ScheduleQueueLoad()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                _ = LoadQueueFromFileAsync();
                return;
            }

            dispatcher.InvokeAsync(async () => await LoadQueueFromFileAsync(),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        private async Task LoadQueueFromFileAsync()
        {
            try
            {
                if (!File.Exists(QueueFilePath)) return;

                var items = await Task.Run(() =>
                    JsonSerializer.Deserialize<List<H3EnsembleQueueItem>>(File.ReadAllText(QueueFilePath)));
                if (items == null || items.Count == 0) return;

                _queue.Clear();
                foreach (var item in items)
                {
                    if (item.ItemStatus == QueueItemStatus.Completed) continue;
                    if (item.ItemStatus == QueueItemStatus.Processing) item.ItemStatus = QueueItemStatus.Pending;
                    _queue.Add(item);
                }

                UpdateQueueStatus();
                if (HasPendingItems)
                    AddLog($"Queue restored: {_queue.Count} items ({_queue.Count(x => x.ItemStatus == QueueItemStatus.Pending)} pending) — press ▶ Start to resume.");
                else if (_queue.Count > 0)
                    AddLog($"Queue restored: {_queue.Count} items");
            }
            catch (Exception ex) { AddLog($"Error loading queue: {ex.Message}"); }
        }

        #endregion

        #region Generation

        /// <summary>
        /// Renders one queued clip. Virtual so the 🪪🎬 H3 Multi tab — the same cast, keyframes, wardrobe
        /// and chain, on the MiniMax I2V turbo graph — can substitute its own submit path while inheriting
        /// this tab's queue, storyboard, analysis and everything else.
        /// </summary>
        protected virtual async Task GenerateItemAsync(H3EnsembleQueueItem item, CancellationToken token)
        {
            IsProcessing = true;
            HasResult = false;
            ResultVideoPath = string.Empty;
            ResultVideoInfo = string.Empty;
            ProcessingProgress = 0;
            ProcessingStatus = "Preparing H3 Ensemble workflow...";

            WorkflowQueueCoordinator.WorkflowLease? lease = null;
            try
            {
                var clipLabel = item.IsStoryClip ? $", clip {item.ClipIndex}/{item.ClipCount}" : string.Empty;
                AddLog($"=== H3 Ensemble ({item.KeyframeCount} keyframe(s), {item.Cast.Count} sheet(s)" +
                       $"{(item.HasEnvironment ? " + location" : string.Empty)}{clipLabel}) ===");
                AddLog("Waiting for other workflows to finish...");
                lease = await _workflowCoordinator.AcquireAsync("H3Ensemble", token);

                ProcessingStatus = "Checking ComfyUI...";
                var comfyOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(s => AddLog($"[Auto-Restart] {s}"));
                if (!comfyOk) throw new Exception("ComfyUI is not running.");
                if (!_comfyUIService.IsConnected)
                {
                    ProcessingStatus = "Connecting to ComfyUI...";
                    await _comfyUIService.ConnectAsync();
                }

                var json = await LoadFileAsync(WorkflowFileName, token);
                json = EnsureInputPrimitives(json);

                ProcessingStatus = "Uploading keyframes, cast and location...";
                ProcessingProgress = 5;

                // Keyframes first, then the cast in cast order, then the location — this is the order
                // <Picture 1>… was numbered in when the prompt was assembled, and it is the only thing
                // standing between a frame lock and a studio photograph being rendered as the opening shot.
                var keyframes = item.KeyframePaths
                    .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
                    .ToList();
                if (keyframes.Count != item.KeyframePaths.Count)
                    throw new FileNotFoundException(
                        $"{item.KeyframePaths.Count - keyframes.Count} keyframe still(s) are gone from disk. " +
                        "The prompt is numbered for all of them, so this item cannot be renumbered now — " +
                        "restore the files or re-queue the job.");

                // Who this clip actually casts. The prompt was assembled with the selective cast already
                // applied, so a character it never names is not uploaded, not wired and not encoded.
                var castInClip = item.Cast
                    .Where(m => HybridCastPrompt.IncludesSubject(item.Prompt, m.Index))
                    .ToList();
                if (castInClip.Count == 0 && item.Cast.Count > 0)
                {
                    // A hand-typed or legacy prompt that names no subject at all. Sending nobody would mean
                    // sending no references, so the whole cast goes rather than none of it.
                    castInClip = item.Cast.ToList();
                    AddLog("This prompt names no <Subject n> — sending the whole queued cast rather than none.");
                }
                var left = item.Cast.Where(m => castInClip.All(c => c.Index != m.Index)).Select(m => m.Index).ToList();
                if (left.Count > 0)
                    AddLog($"Character {string.Join(", ", left)} {(left.Count == 1 ? "is" : "are")} not named " +
                           "in this clip — their references are left out of it entirely.");

                // Panels, per character, in cast order.
                var selected = new List<(EnsembleCastMember Member, SelectedPanels Panels)>();
                foreach (var member in castInClip)
                {
                    var sheet = ResolvePanels(member.PanelPaths, member.SheetPath, member.Index);
                    selected.Add((member, SelectPanels(sheet, member.PanelIndices, member.PanelViews,
                                                      member.Index, member.IsPerson, member.IsGroup)));
                }

                var castPictures = selected.SelectMany(s => s.Panels.Paths).ToList();
                var pictures = new List<string>(keyframes);
                pictures.AddRange(castPictures);
                if (item.HasEnvironment) pictures.Add(item.EnvironmentPath);

                var uploadedRefs = new List<string>();
                foreach (var picture in pictures) uploadedRefs.Add(await EnsureUploadedAsync(picture));

                // ── The refine passes, and whether this item can actually have them ────────────
                var faceRefine = item.FaceRefine;
                if (faceRefine && !HasRefineNodes(json))
                {
                    faceRefine = false;
                    AddLog("Face refine was requested but the workflow file no longer carries the refine " +
                           $"branch (nodes {NodeFaceTrack}–{NodeFaceStitch}) — rendering the base H3 frames " +
                           "as-is.");
                }

                var passes = new List<RefinePass>();
                if (faceRefine)
                {
                    var loaderCursor = keyframes.Count;
                    foreach (var (member, panels) in selected)
                    {
                        var start = loaderCursor;
                        loaderCursor += panels.Paths.Count;

                        // The cursor still advances for them — their panels are wired, they are simply not
                        // refined. Two reasons, both fatal to the pass: H3FaceTrackCrop tracks *human*
                        // faces, so aimed at a cloud it finds nothing or finds somebody else and redraws
                        // them; and it holds *one* subject, so on a crowd it would pick whoever is largest
                        // and refine only them.
                        if (!member.IsPerson || member.IsGroup)
                        {
                            AddLog($"Character {member.Index} ({member.Describe}) is not refined: the pass " +
                                   (member.IsGroup
                                       ? "tracks one subject at a time and this character is several."
                                       : "tracks human faces and this character is not a person."));
                            continue;
                        }

                        if (!item.RefinePrompts.TryGetValue(member.Index, out var prompt) ||
                            string.IsNullOrWhiteSpace(prompt))
                        {
                            AddLog($"Character {member.Index} is not refined: this item carries no cast-only " +
                                   "prompt for them. Re-queue the job to give them a pass.");
                            continue;
                        }
                        // The refine prompt was numbered at queue time for the pictures its own pass
                        // receives; if the two have drifted apart, a <Picture n> in it points at nothing
                        // that pass was sent.
                        if (HybridCastPrompt.HighestPictureReference(prompt) > panels.Paths.Count)
                        {
                            AddLog($"Character {member.Index} is not refined: their refine prompt numbers " +
                                   $"more pictures than the {panels.Paths.Count} panel(s) that pass receives. " +
                                   "Re-queue the job to renumber it.");
                            continue;
                        }
                        if (panels.Paths.Count == 0) continue;

                        passes.Add(new RefinePass(member.Index, start, panels.Paths.Count, panels.FacePanel, prompt));
                    }

                    if (passes.Count == 0)
                    {
                        faceRefine = false;
                        AddLog(castInClip.All(m => !m.IsPerson)
                            ? "Face refine is off for this clip: nothing in it is a person, so there is no " +
                              "face to track. The base H3 frames go straight to the finishing passes."
                            : "Face refine is off for this item: no character in it carries a usable " +
                              "cast-only prompt. Re-queue the job to refine it.");
                    }
                }

                json = WireReferenceImages(json, uploadedRefs, out var refLoaders);
                if (faceRefine) json = WireRefinePasses(json, refLoaders, passes);

                var runSeed = item.Seed >= 0 ? item.Seed : System.Random.Shared.NextInt64(0, long.MaxValue);
                var len = ClampLength(item.LengthSeconds);
                var aspect = item.AspectRatio;
                var (canvasW, canvasH) = CanvasSize(aspect, item.Megapixels);
                var (upW, upH) = UpscaleSize(aspect, item.Megapixels);
                var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var clipTag = item.IsStoryClip ? $"_c{item.ClipIndex:00}" : string.Empty;
                var runToken = $"h3ensemble_{ts}{clipTag}";

                SetInput(ref json, NodePrompt, "value", item.Prompt);
                SetInput(ref json, NodeResolution, "aspect_ratio", aspect);
                SetInput(ref json, NodeResolution, "megapixels", item.Megapixels);
                SetInput(ref json, NodeDuration, "value", len);
                SetInput(ref json, NodeSeed, "noise_seed", runSeed);
                SetInput(ref json, NodeFps, "value", (double)OutputFrameRate);
                SetInput(ref json, NodeSaveVideo, "filename_prefix", $"{OutputSubfolder}/{runToken}");

                if (faceRefine)
                {
                    for (var i = 0; i < passes.Count; i++)
                    {
                        var pass = i + 1;
                        SetInput(ref json, PassNode(pass, RefinePromptId), "value", passes[i].Prompt);
                        SetInput(ref json, PassNode(pass, 106), "denoise", item.RefineDenoise);
                        // Each pass gets its own noise, derived from the run's seed so the whole job still
                        // reproduces from one — two passes redrawing different faces would otherwise share
                        // the same noise on crops of the same size.
                        SetInput(ref json, PassNode(pass, 108), "noise_seed",
                                 (runSeed % (long.MaxValue - pass)) + pass);
                    }
                    AddLog($"Face refine: {passes.Count} pass(es) at denoise {item.RefineDenoise:0.00} — " +
                           string.Join(", ", passes.Select(p =>
                               $"<Subject {p.Subject}> on {p.LoaderCount} panel(s)")) +
                           ", each tracked by that character's own face close-up, with the stage-1 audio " +
                           "locked, stitched back one on top of the next.");
                }
                else
                {
                    AddLog("Face refine off: the base H3 frames go straight to the finishing passes.");
                }

                var rtxUpscale = item.RtxUpscale;
                json = WireOutputChain(json, faceRefine ? passes.Count : 0, item.Interpolate, ref rtxUpscale);
                if (item.RtxUpscale && !rtxUpscale)
                    AddLog($"RTX ×{RtxScale:0.#} was requested but the workflow file no longer has node " +
                           $"{NodeRtxUpscale} — rendering at the H3 canvas instead. Upscale the finished " +
                           "file in ✨ Enhance Video, or restore the node to the workflow.");

                // The Nvidia RTX pack changed this node's widgets; both sets are written so the graph runs
                // whichever version the server has. See RtxSuperResolutionCompat.
                json = RtxSuperResolutionCompat.Normalize(json, AddLog);

                var steps = ReadInt(json, NodeScheduler, "steps");
                json = PruneToOutputs(json, new[] { NodeSaveVideo }, out var prunedCount);
                if (prunedCount > 0)
                    AddLog($"Graph pruned to the video output: {prunedCount} disconnected node(s) removed.");

                var renderedFrames = FramesForSeconds(len);
                var finishedFrames = renderedFrames * (item.Interpolate ? InterpolationFactor : 1);
                var muxFps = item.Interpolate ? OutputFrameRate * InterpolationFactor : OutputFrameRate;
                var finish = (faceRefine ? $"face refine {item.RefineDenoise:0.00} ×{passes.Count}, " : string.Empty) +
                             (item.Interpolate ? $"FILM ×{InterpolationFactor} → {muxFps}fps" : $"{muxFps}fps") +
                             (rtxUpscale ? $", RTX ×{RtxScale:0.#} → ≈{upW}×{upH}" : ", no upscale");

                var castEnd = keyframes.Count + castPictures.Count;
                AddLog(keyframes.Count == 0
                    ? $"References: <Picture 1>–<Picture {castEnd}> are the cast" +
                      (item.HasEnvironment ? $", <Picture {uploadedRefs.Count}> is the location" : string.Empty) +
                      " — this clip is a continuous take with no frame lock."
                    : $"References: <Picture 1>–<Picture {keyframes.Count}> are the keyframe locks at " +
                      $"{string.Join(", ", item.KeyframeSeconds.Select(s => $"{s:0.00}s"))}; " +
                      $"<Picture {keyframes.Count + 1}>–<Picture {castEnd}> are the cast" +
                      (item.HasEnvironment ? $"; <Picture {uploadedRefs.Count}> is the location" : string.Empty) +
                      ".");

                ProcessingProgress = 10;
                ProcessingStatus = "Generating video...";
                AddLog($"Generating (seed {runSeed}, {len:0.#}s / {renderedFrames} frames @ {OutputFrameRate}fps, " +
                       $"{aspect} ≈{canvasW}×{canvasH}, {item.Megapixels:0.0} MP, {steps} steps, {finish})...");

                var peakGb = rtxUpscale
                    ? FrameStackGb(finishedFrames, upW, upH)
                    : FrameStackGb(finishedFrames, canvasW, canvasH);
                AddLog($"Peak frame stack ≈{peakGb:0.#} GB ({finishedFrames} frames held at once)" +
                       (faceRefine
                           ? $", plus ≈{FrameStackGb(renderedFrames, 768, 768):0.#} GB of face crops during " +
                             "each refine pass."
                           : "."));
                if (peakGb >= HeavyFrameStackGb)
                    AddLog("WARNING: that is large enough to take ComfyUI down mid-render — if this job dies " +
                           "with the prompt \"neither queued nor in the run history\", shorten the clip, drop " +
                           "to 0.7 MP, turn interpolation off, or turn RTX off and upscale afterwards in " +
                           "✨ Enhance Video.");

                var local = await SubmitAndRetrieveAsync(json, runToken, NodeSaveVideo, 10, 95, token);
                if (local == null || !File.Exists(local))
                    throw new Exception("No output video was generated.");

                var outputDir = Path.Combine(
                    _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), OutputFolderName);
                Directory.CreateDirectory(outputDir);
                var finalName = item.IsStoryClip
                    ? $"H3Ensemble_{(string.IsNullOrEmpty(item.StoryId) ? ts : item.StoryId)}_clip{item.ClipIndex:00}.mp4"
                    : $"H3Ensemble_{ts}.mp4";
                var finalPath = Path.Combine(outputDir, finalName);
                File.Copy(local, finalPath, true);
                await LocalCopyService.CopyVideoAsync(finalPath);

                var fi = new FileInfo(finalPath);
                var size = rtxUpscale ? $"RTX ×{RtxScale:0.#} ≈{upW}×{upH}" : $"≈{canvasW}×{canvasH}";
                item.OutputVideoPath = finalPath;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    ResultVideoPath = finalPath;
                    ResultVideoInfo = $"H3 Ensemble • {(item.IsStoryClip ? $"clip {item.ClipIndex}/{item.ClipCount} • " : string.Empty)}" +
                                      $"{castInClip.Count} character(s) • {item.KeyframeCount} keyframe(s) • " +
                                      $"{(faceRefine ? $"face refine {item.RefineDenoise:0.00} ×{passes.Count} • " : string.Empty)}" +
                                      $"turbo {steps}-step • {size} • {muxFps}fps • {aspect} • " +
                                      $"{len:0.#}s • {fi.Length / 1024 / 1024.0:F1}MB";
                    HasResult = true;
                    OnCanExecuteChanged();
                });
                ProcessingProgress = 100;
                ProcessingStatus = "Complete!";
                AddLog($"=== Complete: {finalPath} ===");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AddLog($"ERROR: {ex.Message}");
                ProcessingStatus = $"Error: {ex.Message}";
                throw;
            }
            finally
            {
                lease?.Dispose();
                IsProcessing = false;
                OnCanExecuteChanged();
            }
        }

        /// <summary>
        /// A queued item's last stop before it joins the queue: the tab stamps whichever pipeline switches
        /// its own render graph reads. The base stamps none — every switch the hybrid graph honours is
        /// already in the initializer above. The H3 Multi tab overrides this to freeze its turbo-pipeline
        /// settings (latent upscale, max-fidelity references, audio enhancement, SLA) onto the item.
        /// </summary>
        protected virtual void ConfigureQueuedItem(H3EnsembleQueueItem item) { }

        /// <summary>One character's face-refine pass: who it redraws, which of the run's reference loaders
        /// it is conditioned on, which of those is their face close-up, and the prompt it reads.</summary>
        private sealed record RefinePass(
            int Subject, int LoaderStart, int LoaderCount, int FacePanel, string Prompt);

        #endregion

        #region Graph patches

        /// <summary>
        /// Wrapper around <see cref="WorkflowNodeUpdater.UpdateNodeInput"/> that fails loudly on a node id or
        /// input that is no longer in the graph. The updater silently no-ops instead, which on these
        /// workflows would mean shipping the baked-in demo prompt and reference image to the GPU.
        /// </summary>
        private static void SetInput(ref string json, string nodeId, string input, object value)
        {
            if (WorkflowNodeUpdater.GetNodeInput(json, nodeId, input) == null)
                throw new Exception($"Workflow node '{nodeId}' has no input '{input}' — the workflow file no longer matches this tab.");
            WorkflowNodeUpdater.UpdateNodeInput(ref json, nodeId, input, value);
        }

        /// <summary>
        /// Asserts the node classes the patches below assume, and makes sure the reference node reads its
        /// prompt, canvas and frame count from the input primitives rather than from widget values baked in
        /// by an export. Idempotent.
        /// </summary>
        private static string EnsureInputPrimitives(string json)
        {
            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new Exception("Workflow JSON could not be parsed.");

            RequireClass(root, NodeReference, "MiniMaxH3ReferenceToVideo");
            RequireClass(root, NodePrompt, "PrimitiveStringMultiline");
            RequireClass(root, NodeResolution, "ResolutionSelector");
            RequireClass(root, NodeFrames, "ComfyMathExpression");
            RequireClass(root, NodeDuration, "PrimitiveFloat");
            RequireClass(root, NodeSeed, "RandomNoise");
            RequireClass(root, NodeRefImage1, "LoadImage");
            RequireClass(root, NodeBaseFrames, "VAEDecode");
            RequireClass(root, NodeInterpolate, "FrameInterpolate");
            // The RTX upscale is optional in the file itself, not merely prunable: a workflow saved without
            // it is a perfectly good graph, and demanding it would fail every submit rather than the one
            // setting it affects. See HasRtxNode.
            if (root[NodeRtxUpscale] is JsonObject)
                RequireClass(root, NodeRtxUpscale, "RTXVideoSuperResolution");
            RequireClass(root, NodeFps, "PrimitiveFloat");
            RequireClass(root, NodeFpsDoubled, "ComfyMathExpression");
            RequireClass(root, NodeCreateVideo, "CreateVideo");
            RequireClass(root, NodeSaveVideo, "SaveVideo");

            json = root.ToJsonString();
            SetInput(ref json, NodeReference, "prompt", new JsonArray(NodePrompt, 0));
            SetInput(ref json, NodeReference, "width", new JsonArray(NodeResolution, 0));
            SetInput(ref json, NodeReference, "height", new JsonArray(NodeResolution, 1));
            SetInput(ref json, NodeReference, "length", new JsonArray(NodeFrames, 1));

            // The refine pass shares only the frame count; its canvas is the face crop and its prompt is its
            // own. Skipped rather than demanded when the file has no refine branch — see HasRefineNodes.
            if (JsonNode.Parse(json)?[NodeRefineReference] is JsonObject)
            {
                SetInput(ref json, NodeRefineReference, "prompt", new JsonArray(NodeRefinePrompt, 0));
                SetInput(ref json, NodeRefineReference, "length", new JsonArray(NodeFrames, 1));
            }
            return json;
        }

        /// <summary>Whether the workflow file still carries the face-refine branch. Like the RTX node it is
        /// optional in the file itself rather than merely prunable.</summary>
        private static bool HasRefineNodes(string json)
        {
            var root = JsonNode.Parse(json)?.AsObject();
            return root != null &&
                   root[NodeRefinePrompt] is JsonObject && root[NodeFaceTrack] is JsonObject &&
                   root[NodeRefineReference] is JsonObject && root[NodeAudioLock] is JsonObject &&
                   root[NodeRefineDenoise] is JsonObject && root[NodeRefineSeed] is JsonObject &&
                   root[NodeFaceStitch] is JsonObject;
        }

        /// <summary>
        /// Resolves the panel files a queued character actually renders from, splitting the sheet again when
        /// the frozen paths are gone. Whatever this returns has to have the <i>same number</i> of entries as
        /// the item's prompt was numbered for, so the panel count is forced, never re-detected.
        /// </summary>
        protected IReadOnlyList<string> ResolvePanels(
            IReadOnlyList<string> frozen, string sheetPath, int character)
        {
            var kept = frozen.Where(p => !string.IsNullOrEmpty(p) && File.Exists(p)).ToList();
            if (kept.Count > 0 && kept.Count == frozen.Count) return kept;

            var legacy = frozen.Count == 0;
            var requested = legacy ? CharacterSheetSplitter.WholeSheet : frozen.Count;
            var panels = CharacterSheetSplitter.Split(sheetPath, requested);
            if (panels.Count == 0)
                throw new FileNotFoundException($"Character {character}'s sheet is gone: {sheetPath}");

            AddLog($"Character {character}: cached panels missing, re-split ({panels.Note}).");
            if (!legacy && panels.Count != frozen.Count)
                AddLog($"WARNING: character {character} re-split into {panels.Count} panel(s) but the prompt " +
                       $"was numbered for {frozen.Count}. Re-queue this item to renumber it.");
            return panels.Paths;
        }

        /// <summary>
        /// Narrows a character's panels to the ones this job actually sends, and says what each one shows.
        /// The selection is the reference budget frozen at queue time.
        /// </summary>
        protected SelectedPanels SelectPanels(
            IReadOnlyList<string> panels, IReadOnlyList<int> indices, IReadOnlyList<string> views,
            int character, bool isPerson = true, bool isGroup = false)
        {
            if (indices.Count == 0)
                return SelectedPanels.Of(
                    panels, HybridCastPrompt.DefaultViews(panels.Count, isPerson, isGroup));

            var usable = indices.Where(i => i >= 0 && i < panels.Count).ToList();
            if (usable.Count != indices.Count)
                AddLog($"WARNING: character {character} was queued sending panel(s) " +
                       $"{string.Join(", ", indices.Select(i => i + 1))} of a {panels.Count}-panel sheet, and " +
                       "that sheet no longer has them. The prompt is numbered for the full list — re-queue " +
                       "this item.");
            if (usable.Count == 0) usable = Enumerable.Range(0, panels.Count).ToList();

            var picked = usable.Select(i => panels[i]).ToList();
            var pickedViews = usable
                .Select((_, slot) => slot < views.Count ? views[slot] : $"view {slot + 1}")
                .ToList();
            return SelectedPanels.Of(picked, pickedViews);
        }

        /// <summary>The panels of one character that a job uploads, what they show, and which of them is the
        /// face close-up — the picture that character's refine pass tracks them by.</summary>
        protected sealed record SelectedPanels(
            IReadOnlyList<string> Paths, IReadOnlyList<string> Views, int FacePanel)
        {
            public static SelectedPanels Of(IReadOnlyList<string> paths, IReadOnlyList<string> views)
            {
                var face = views.ToList().FindIndex(
                    v => string.Equals(v, HybridCastPrompt.ViewFace, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(v, HybridCastPrompt.ViewDetail, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(v, HybridCastPrompt.ViewGroupDetail, StringComparison.OrdinalIgnoreCase));
                return new SelectedPanels(paths, views, face >= 0 ? face : Math.Max(0, paths.Count - 1));
            }
        }

        /// <summary>
        /// Wires the run's pictures into <c>ref_images.ref_image_0…N</c> in the order they were numbered:
        /// keyframe locks first, then the cast's panels, then the location.
        ///
        /// <para>They go in <b>unresized</b>. <c>MiniMaxH3ReferenceToVideo</c> sizes references itself
        /// (<c>ref_image_size: match</c> scales each one to the generation's pixel area keeping aspect), and
        /// pre-scaling every reference to the exact video canvas hands H3 a canvas-shaped, canvas-sized
        /// picture — the shape of an output frame, which is a strong invitation to render one.</para>
        /// </summary>
        /// <param name="loaders">The injected <c>LoadImage</c> node ids, in picture order — what the refine
        /// passes pick their own conditioning out of.</param>
        private static string WireReferenceImages(
            string json, IReadOnlyList<string> uploadedNames, out IReadOnlyList<string> loaders)
        {
            if (uploadedNames.Count == 0)
                throw new Exception("No reference images to wire — the run has neither keyframes nor a cast.");
            if (uploadedNames.Count > MaxReferenceImages)
                throw new Exception($"{uploadedNames.Count} reference images, but MiniMaxH3ReferenceToVideo " +
                                    $"takes at most {MaxReferenceImages}. Set References to Auto, drop a " +
                                    "keyframe, or write this beat around fewer characters.");

            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new Exception("Workflow JSON could not be parsed.");

            RequireClass(root, NodeReference, "MiniMaxH3ReferenceToVideo");
            RequireClass(root, NodeRefImage1, "LoadImage");

            // The workflow ships one LoadImage; the rest are injected beside it, ids well clear of the graph's.
            var ids = new List<string>();
            for (var i = 0; i < uploadedNames.Count; i++)
            {
                var id = i == 0
                    ? NodeRefImage1
                    : (ReferenceNodeIdBase + i).ToString(CultureInfo.InvariantCulture);
                root[id] = new JsonObject
                {
                    ["inputs"] = new JsonObject { ["image"] = uploadedNames[i] },
                    ["class_type"] = "LoadImage",
                    ["_meta"] = new JsonObject { ["title"] = $"Ref Image {i + 1}" }
                };
                ids.Add(id);
            }

            AttachReferences(root, NodeReference, ids);

            loaders = ids;
            return root.ToJsonString();
        }

        /// <summary>Replaces a reference node's whole <c>ref_image_N</c> list. Cleared rather than
        /// overwritten: a run with fewer pictures than the file was authored for must not inherit a stale
        /// slot pointing at a node that is about to be pruned.</summary>
        protected static void AttachReferences(JsonObject root, string nodeId, IReadOnlyList<string> loaders)
        {
            if (root[nodeId]?["inputs"] is not JsonObject inputs)
                throw new Exception($"Workflow node '{nodeId}' has no inputs — the workflow file no longer matches this tab.");

            foreach (var key in inputs.Select(kv => kv.Key)
                                      .Where(k => k.StartsWith(RefImagePrefix, StringComparison.Ordinal))
                                      .ToList())
                inputs.Remove(key);

            for (var i = 0; i < loaders.Count; i++)
                inputs[RefImagePrefix + i.ToString(CultureInfo.InvariantCulture)] =
                    new JsonArray(loaders[i], 0);
        }

        /// <summary>The prompt primitive of the base refine block, as an int — pass <c>k</c>'s copy is
        /// <c>100·k + 15</c>.</summary>
        private const int RefinePromptId = 15;

        /// <summary>
        /// The node id one of the refine block's nodes takes in pass <paramref name="pass"/>. Pass 1 is the
        /// block as shipped (15, 100–111); pass <c>k</c> is that block shifted into the <c>100·k</c> range,
        /// so the second pass is 215 and 200–211, the third 315 and 300–311, and so on.
        /// </summary>
        private static string PassNode(int pass, int baseId) =>
            (baseId == RefinePromptId
                ? pass <= 1 ? RefinePromptId : 100 * pass + RefinePromptId
                : pass <= 1 ? baseId : baseId + 100 * (pass - 1))
            .ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Conditions the face-refine passes — <b>one per character in the clip</b> — on that character's own
        /// panels, and tells each tracker which face it is following.
        ///
        /// <para><b>Why one pass each.</b> <c>H3FaceTrackCrop</c> holds a single subject through a clip: with
        /// no <c>identity_reference</c> it picks whoever is largest in the first frame and follows them, so
        /// in a multi-character clip every other face was never refined at all — and the pass that did run
        /// was shown the whole cast's photographs, which gave it nothing to say about which of the faces it
        /// was looking at. Each character now gets their own pass: tracked by their own face close-up,
        /// conditioned on their own panels, prompted with their own copy of the clip. Each pass runs over the
        /// frames the previous one stitched, so the edits compose rather than compete.</para>
        ///
        /// <para>The panels are wired onto the same <c>LoadImage</c> nodes as the base pass and renumbered
        /// from <c>ref_image_0</c> — the numbering those prompts were written for. The keyframe stills and
        /// the location are left off deliberately: these passes have no timeline and no set — they re-draw a
        /// 768px crop of a face the base pass already placed.</para>
        /// </summary>
        private static string WireRefinePasses(
            string json, IReadOnlyList<string> loaders, IReadOnlyList<RefinePass> passes)
        {
            if (passes.Count == 0) return json;

            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new Exception("Workflow JSON could not be parsed.");
            if (root[NodeRefineReference] is not JsonObject) return json;

            for (var i = 0; i < passes.Count; i++)
            {
                var pass = i + 1;
                var p = passes[i];
                var own = loaders.Skip(p.LoaderStart).Take(p.LoaderCount).ToList();
                if (own.Count == 0)
                    throw new Exception($"Character {p.Subject}'s refine pass has no panel to condition on — " +
                                        "it would redraw the face from the prompt text alone.");

                if (pass > 1)
                {
                    json = AddRefinePass(json, pass);
                    root = JsonNode.Parse(json)!.AsObject();
                }

                AttachReferences(root, PassNode(pass, 101), own);
                SetIdentityReference(root, PassNode(pass, 100), own, p.FacePanel);
                json = root.ToJsonString();
            }

            return json;

            // The tracker's optional identity input: with it the subject is chosen by face identity rather
            // than by size, which is the only way several people in one frame can be told apart across a clip.
            static void SetIdentityReference(
                JsonObject root, string trackNode, IReadOnlyList<string> loaders, int facePanel)
            {
                if (root[trackNode]?["inputs"] is not JsonObject inputs) return;
                var index = Math.Clamp(facePanel, 0, loaders.Count - 1);
                inputs["identity_reference"] = new JsonArray(loaders[index], 0);
            }
        }

        /// <summary>
        /// Clones the refine chain (<c>100</c>–<c>111</c> plus its prompt primitive) into pass
        /// <paramref name="pass"/>'s own <c>100·pass</c> block, reading the frames the previous pass already
        /// stitched.
        ///
        /// <para>Injected here rather than shipped in the workflow file because how many passes a clip needs
        /// is decided by how many characters its text names — and because a hand-authored copy of eleven
        /// nodes is eleven more links to keep in step with the original every time the chain changes. Every
        /// link inside the clone is remapped to the clone; every link out of it is left alone, except the two
        /// that read the base render (<c>H3FaceTrackCrop.images</c> and <c>H3FaceStitch.base_images</c>),
        /// which are moved onto the previous pass's output so this edit lands on top of that one rather than
        /// discarding it.</para>
        /// </summary>
        private static string AddRefinePass(string json, int pass)
        {
            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new Exception("Workflow JSON could not be parsed.");

            var previousStitch = PassNode(pass - 1, RefineBlockLast);
            var map = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [NodeRefinePrompt] = PassNode(pass, RefinePromptId),
            };
            foreach (var kv in root.ToList())
            {
                if (!int.TryParse(kv.Key, out var id) || id < RefineBlockFirst || id > RefineBlockLast) continue;
                map[kv.Key] = PassNode(pass, id);
            }

            foreach (var (source, clone) in map)
            {
                if (root[source] is not JsonObject node) continue;
                var copy = JsonNode.Parse(node.ToJsonString())!.AsObject();

                if (copy["inputs"] is JsonObject inputs)
                    foreach (var input in inputs.ToList())
                    {
                        if (input.Value is not JsonArray link || link.Count < 2) continue;
                        var from = link[0]?.GetValue<string>();
                        if (from == null) continue;
                        var slot = link[1]!.GetValue<int>();
                        var target = map.TryGetValue(from, out var mapped) ? mapped
                                   : from == NodeBaseFrames ? previousStitch
                                   : from;
                        inputs[input.Key] = new JsonArray(target, slot);
                    }

                var title = copy["_meta"]?["title"]?.GetValue<string>();
                copy["_meta"] = new JsonObject { ["title"] = $"{title ?? clone} (refine pass {pass})" };
                root[clone] = copy;
            }

            return root.ToJsonString();
        }

        /// <summary>
        /// Wires the tail of the graph — which frames reach the file, and at what rate — for the optional
        /// passes. The frames come from the <i>last</i> face-stitch node when the refine passes run and from
        /// the base decode when they do not; they then go through interpolation and the RTX upscale, or
        /// straight on when those are off. Whatever is left unreferenced becomes unreachable and
        /// <see cref="PruneToOutputs"/> deletes it, which is the only safe way to drop a branch: several of
        /// these nodes would otherwise still execute on their own.
        /// </summary>
        private static string WireOutputChain(
            string json, int refinePasses, bool interpolate, ref bool rtxUpscale)
        {
            var rendered = refinePasses > 0 ? PassNode(refinePasses, RefineBlockLast) : NodeBaseFrames;
            var frames = interpolate ? NodeInterpolate : rendered;
            SetInput(ref json, NodeInterpolate, "images", new JsonArray(rendered, 0));

            if (HasRtxNode(json))
                SetInput(ref json, NodeRtxUpscale, "images", new JsonArray(frames, 0));
            else
                // Reported by the caller, and turned off here so every downstream size and frame-stack figure
                // describes the file that is actually about to be written.
                rtxUpscale = false;

            SetInput(ref json, NodeCreateVideo, "images",
                new JsonArray(rtxUpscale ? NodeRtxUpscale : frames, 0));
            // The mux rate has to follow the frame count, or an interpolated clip plays at half speed.
            SetInput(ref json, NodeCreateVideo, "fps",
                new JsonArray(interpolate ? NodeFpsDoubled : NodeFps, 0));
            return json;
        }

        /// <summary>Whether the workflow file still carries the optional RTX upscale node.</summary>
        private static bool HasRtxNode(string json) =>
            JsonNode.Parse(json)?[NodeRtxUpscale] is JsonObject;

        /// <summary>Reads an integer widget out of the graph — used for the steps the workflow ships with,
        /// which the tab reports but never overrides.</summary>
        private static int ReadInt(string json, string nodeId, string input)
        {
            var node = JsonNode.Parse(json)?[nodeId]?["inputs"]?[input];
            return node is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
        }

        /// <summary>Fails loudly when a node the patches rewire is missing or is no longer the class they
        /// assume — both would otherwise produce a graph that only fails on the server, or worse, silently
        /// renders the wrong thing.</summary>
        protected static void RequireClass(JsonObject root, string nodeId, string expected)
        {
            if (root[nodeId] is not JsonObject node)
                throw new Exception($"Workflow node '{nodeId}' is not in the graph — the workflow file no longer matches this tab.");
            var actual = node["class_type"]?.GetValue<string>();
            if (actual != expected)
                throw new Exception($"Workflow node '{nodeId}' is a {actual ?? "(none)"}, expected {expected} — the workflow file no longer matches this tab.");
        }

        /// <summary>
        /// Cuts the graph down to the output nodes we want plus everything they depend on, and deletes every
        /// other node outright. Pruning by reachability is the only reliable way to drop a branch: anything
        /// ending in an OUTPUT_NODE runs whether or not something downstream consumes it, so unhooking a sink
        /// is not enough on its own.
        /// </summary>
        protected static string PruneToOutputs(string json, IEnumerable<string> keepOutputs, out int removed)
        {
            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new Exception("Workflow JSON could not be parsed.");

            var reachable = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>(keepOutputs);
            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (!reachable.Add(id)) continue;
                if (root[id]?["inputs"] is not JsonObject inputs) continue;

                foreach (var input in inputs)
                {
                    if (input.Value is JsonArray link && link.Count == 2 && LinkSource(link[0]) is { } src)
                        stack.Push(src);
                }
            }

            removed = 0;
            foreach (var id in root.Select(kv => kv.Key).ToList())
            {
                if (reachable.Contains(id)) continue;
                root.Remove(id);
                removed++;
            }

            return root.ToJsonString();

            static string? LinkSource(JsonNode? node)
            {
                if (node is not JsonValue value) return null;
                if (value.TryGetValue<string>(out var s)) return s;
                if (value.TryGetValue<long>(out var i)) return i.ToString(CultureInfo.InvariantCulture);
                return null;
            }
        }

        #endregion

        #region Submit and retrieve

        /// <summary>Submits the workflow, waits for completion, and resolves the video sink's output to a
        /// local file — first via /history node outputs, then a disk scan for the run token.</summary>
        protected async Task<string?> SubmitAndRetrieveAsync(
            string json, string runToken, string outputNode, double from, double to, CancellationToken token)
        {
            var existing = GetExistingVideoFiles("*.mp4", OutputSubfolder);
            var promptId = await SubmitAsync(json, from, to, token);

            ProcessingStatus = "Waiting for output...";
            var byNode = await _comfyUIService.HttpClient.GetOutputsByNodeAsync(promptId, token);
            if (byNode.TryGetValue(outputNode, out var outs) && outs.Count > 0)
            {
                var pick = outs.FirstOrDefault(f => f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) ?? outs[0];
                var local = await ResolveOutputToLocalAsync(pick);
                if (local != null) return local;
            }

            var found = await WaitForNewVideoAsync(existing, "*.mp4",
                TimeSpan.FromMinutes(60), TimeSpan.FromSeconds(4), OutputSubfolder);
            if (found != null && Path.GetFileName(found).IndexOf(runToken, StringComparison.OrdinalIgnoreCase) >= 0)
                return found;
            return found ?? FindTokenVideoOnDisk(runToken);
        }

        private async Task<string> SubmitAsync(string json, double progressFrom, double progressTo, CancellationToken token)
        {
            var workflow = JsonSerializer.Deserialize<JsonElement>(json);
            var span = progressTo - progressFrom;
            var progress = new Progress<ProgressMessage>(msg =>
            {
                if (msg.Data?.Value != null && msg.Data?.Max != null && msg.Data.Max > 0)
                {
                    var pct = (double)msg.Data.Value / msg.Data.Max;
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        ProcessingProgress = progressFrom + pct * span;
                        ProcessingStatus = $"Generating: {msg.Data.Value}/{msg.Data.Max}";
                    });
                }
            });

            var promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, progress, token);
            AddLog($"Workflow submitted, ID: {promptId}");
            return promptId;
        }

        protected async Task<string?> ResolveOutputToLocalAsync(string videoFile)
        {
            try
            {
                var settings = _settingsService.Settings;
                if (settings != null)
                {
                    var baseUrl = GetComfyUIBaseUrl();
                    var isRemote = IsComfyUIRemote(new Uri(baseUrl).Host);
                    var outputFolder = settings.ResolveOutputFolder(isRemote);
                    if (!string.IsNullOrEmpty(outputFolder))
                    {
                        var localPath = Path.Combine(outputFolder, videoFile.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(localPath))
                        {
                            await WaitForFileStableAsync(localPath);
                            return localPath;
                        }
                    }
                }

                var parts = videoFile.Split('/');
                var filename = parts.Last();
                var subfolder = parts.Length > 1 ? string.Join("/", parts.Take(parts.Length - 1)) : string.Empty;
                var bytes = await _comfyUIService.HttpClient.DownloadOutputVideoAsync(filename, subfolder);
                if (bytes is { Length: > 0 })
                {
                    var tempPath = Path.Combine(Path.GetTempPath(), $"h3ensemble_{Guid.NewGuid():N}_{filename}");
                    await File.WriteAllBytesAsync(tempPath, bytes);
                    return tempPath;
                }
            }
            catch (Exception ex)
            {
                AddLog($"Resolve output failed: {ex.Message}");
            }
            return null;
        }

        protected string? FindTokenVideoOnDisk(string runToken)
        {
            try
            {
                var settings = _settingsService.Settings;
                if (settings == null) return null;
                var baseUrl = GetComfyUIBaseUrl();
                var isRemote = IsComfyUIRemote(new Uri(baseUrl).Host);
                var outputFolder = settings.ResolveOutputFolder(isRemote);
                if (string.IsNullOrEmpty(outputFolder)) return null;

                var candidates = new List<string>();
                foreach (var folder in new[] { outputFolder, Path.Combine(outputFolder, OutputSubfolder) })
                {
                    if (Directory.Exists(folder))
                        candidates.AddRange(Directory.GetFiles(folder, "*.mp4", SearchOption.AllDirectories)
                            .Where(f => Path.GetFileName(f).IndexOf(runToken, StringComparison.OrdinalIgnoreCase) >= 0));
                }
                return candidates.OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
            }
            catch (Exception ex)
            {
                AddLog($"Disk scan failed: {ex.Message}");
                return null;
            }
        }

        #endregion
    }
}
