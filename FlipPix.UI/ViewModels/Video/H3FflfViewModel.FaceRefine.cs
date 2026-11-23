using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlipPix.UI.Models;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// ✨ Face refine — H3 FaceRefine Plus on a finished take (<see cref="FaceRefinePlusGraph"/>). Two ways in:
    /// <list type="bullet">
    /// <item>the <see cref="FaceRefine"/> setting refines every beat as it lands, before the next beat starts on
    /// its last frame, so the refined face is what the story carries on with;</item>
    /// <item>✨ on a beat refines that one take, there and then — no new seed, no other beat rendered again. The
    /// take as rendered is kept, so refining again (say at the other strength) starts from it, not from a
    /// refine.</item>
    /// </list>
    /// A refine that fails leaves the take as it was. Shared by ⛓ FFLF, 🥋 Action and 🥷 Martial.
    /// </summary>
    public partial class H3FflfViewModel
    {
        /// <summary>Refine every beat's face as it renders (not drafts).</summary>
        [ObservableProperty] private bool _faceRefine;

        [ObservableProperty] private double _faceRefineStrength = FaceRefinePlusGraph.DefaultStrength;

        public IReadOnlyList<MegapixelOption> FaceRefineStrengths { get; } = new[]
        {
            new MegapixelOption(FaceRefinePlusGraph.DefaultStrength, "0.30 — redraws toward the opening face"),
            new MegapixelOption(FaceRefinePlusGraph.LightStrength, "0.15 — light, keeps more of the take"),
        };

        private static double ClampRefineStrength(double v) =>
            v > 0 && v < 0.225 ? FaceRefinePlusGraph.LightStrength : FaceRefinePlusGraph.DefaultStrength;

        /// <summary>✨ on a beat: refine that take's face now.</summary>
        public RelayCommand<FflfBeat> RefineBeatFaceCommand { get; }

        private bool CanRefineBeatFace(FflfBeat? beat) =>
            beat != null && !IsProcessing && beat.IsDone && (beat.HasVideo || File.Exists(beat.OriginalVideoPath));

        /// <summary>The models the refine loads. The 2-Pass tabs (FFLF, Action): the page's diffusion model and
        /// LoRA, with the text encoder and VAEs the beat template loads.</summary>
        protected virtual FaceRefineModels FaceRefineModelsFor(string template)
        {
            var loras = Lora.Length > 0 && LoraStrength > 0
                ? new[] { new FaceRefineLora(Lora, LoraStrength) }
                : Array.Empty<FaceRefineLora>();
            return FaceRefinePlusGraph.FromTwoPassTemplate(template, DiffusionModel, loras);
        }

        /// <summary>✨ on one beat, outside a run: holds the queue lease for the one job.</summary>
        private async Task RefineBeatFaceNowAsync(FflfBeat beat)
        {
            if (IsProcessing) return;
            var index = Beats.IndexOf(beat);
            if (index < 0) return;
            IsProcessing = true;
            _runCts = new CancellationTokenSource();
            var ct = _runCts.Token;
            RefreshCommands();
            try
            {
                ProcessingStatus = "Waiting for the ComfyUI queue…";
                using var lease = await _workflowCoordinator.AcquireAsync($"h3_{Profile.Key}", ct);
                _holdingLease = true;
                var template = string.Empty;
                if (BeatWorkflowFile is { } workflowFile)
                {
                    var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, workflowFile);
                    if (File.Exists(workflowPath)) template = await File.ReadAllTextAsync(workflowPath, ct);
                }
                if (await RefineBeatFaceAsync(beat, index, template, ct))
                {
                    // The joined file still has the old take in it.
                    if (HasJoined)
                    {
                        JoinedVideoPath = string.Empty;
                        AddLog("The joined video had the unrefined take in it — 🎞 Join again to include the refined one.");
                    }
                    ProcessingStatus = $"✨ {beat.Label}: face refined.";
                }
                else ProcessingStatus = $"✨ {beat.Label}: face refine failed — see the log.";
            }
            catch (OperationCanceledException) { ProcessingStatus = "Stopped."; }
            catch (Exception ex)
            {
                ProcessingStatus = $"Face refine failed: {ex.Message}";
                AddLog($"✨ Face refine failed: {ex.Message}");
            }
            finally
            {
                _holdingLease = false;
                IsProcessing = false;
                _runCts?.Dispose();
                _runCts = null;
                ScheduleStorySave();
                RefreshCommands();
            }
        }

        /// <summary>
        /// Refines one take's face and puts the result in its place: the video and the last frame the next beat
        /// starts on. Returns false (the take untouched) when it fails; a stop is thrown.
        /// </summary>
        private async Task<bool> RefineBeatFaceAsync(FflfBeat beat, int index, string template, CancellationToken ct)
        {
            var label = $"✨ {beat.Label}";
            var source = File.Exists(beat.OriginalVideoPath) ? beat.OriginalVideoPath : beat.VideoPath;
            if (!File.Exists(source)) { AddLog($"{label}: the take is missing on disk — nothing to refine."); return false; }

            var previousStatus = beat.Status;
            beat.IsRefiningFace = true;
            beat.Status = "✨ refining the face…";
            var started = DateTime.Now;
            try
            {
                ProcessingStatus = $"{label}: sending the take to ComfyUI…";
                var video = await _comfyUIService.UploadVideoAsync(source, ct);
                if (string.IsNullOrEmpty(video)) throw new InvalidOperationException("the take could not be uploaded");
                var identity = await UploadInitialAsync(ct);

                var probe = Mp4Probe.TryRead(source);
                var folder = _runFolder.Length > 0 ? _runFolder : $"{Profile.Key}_refine";
                var request = new FaceRefineRequest
                {
                    Video = video,
                    HasAudio = probe?.HasAudio ?? true,
                    FrameCap = FaceRefinePlusGraph.FrameCapFor(probe?.DurationSeconds ?? 0),
                    IdentityImage = identity,
                    Prompt = FaceRefinePlusGraph.ComposePrompt(beat.Text, ComposedStyle),
                    Models = FaceRefineModelsFor(template),
                    Strength = FaceRefineStrength,
                    Seed = beat.Seed > 0 ? beat.Seed : NewSeed(),
                    OutputPrefix = $"video/{Profile.Key}/{folder}/beat_{index + 1:D2}_face",
                };
                var graph = FaceRefinePlusGraph.Build(request);
                AddLog($"{label}: H3 FaceRefine Plus at {request.Strength:0.00}, {Path.GetFileName(request.Models.DiffusionModel)}" +
                       (request.FrameCap > 0 ? $", {request.FrameCap} frames" : string.Empty) +
                       (request.HasAudio ? ", lips locked to the take's audio." : ", no soundtrack."));

                var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(msg =>
                {
                    if (msg.Data == null || msg.Data.Max <= 0 || msg.Data.Max > 60) return;
                    ProcessingStatus = $"{label} — step {msg.Data.Value}/{msg.Data.Max} · {(DateTime.Now - started):m\\:ss}";
                });
                ProcessingStatus = $"{label}: tracking and redrawing the face…";
                var promptId = await _comfyUIService.ExecuteWorkflowAsync(graph, progress, ct, TimeSpan.FromHours(1));
                if (string.IsNullOrEmpty(promptId)) throw new InvalidOperationException("ComfyUI returned no prompt id");

                var byNode = await _comfyUIService.HttpClient.GetOutputsByNodeAsync(promptId, ct);
                var videoFile = byNode.TryGetValue(FaceRefinePlusGraph.VideoSaveNode, out var vids)
                    ? vids.FirstOrDefault(f => f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) ?? vids.FirstOrDefault()
                    : null;
                var frameFile = byNode.TryGetValue(FaceRefinePlusGraph.LastFrameSaveNode, out var frames) ? frames.FirstOrDefault() : null;
                var refined = videoFile == null ? null : await FetchOutputFileAsync(videoFile, ct);
                if (refined == null) throw new InvalidOperationException("ComfyUI reported no refined video");
                var frame = frameFile == null ? null : await FetchOutputFileAsync(frameFile, ct);

                // The take as rendered is kept the first time, in the work folder (the download cache is pruned).
                if (!beat.IsFaceRefined)
                {
                    beat.OriginalVideoPath = Keep(beat.VideoPath, $"{folder}_beat{index + 1:D2}_take");
                    beat.OriginalLastFramePath = beat.LastFramePath;
                }
                beat.VideoPath = refined;
                if (frame != null)
                {
                    beat.LastFramePath = Keep(frame, $"{folder}_beat{index + 1:D2}_last_refined");
                    beat.UploadedLastFrame = string.Empty;
                }
                beat.Status = $"{(previousStatus.Length > 0 ? previousStatus + " · " : string.Empty)}✨ {request.Strength:0.00}";
                ResultVideoPath = refined;
                ResultVideoInfo = $"{beat.Label} · face refined · {Path.GetFileName(refined)}";
                HasResult = true;
                WatchingLive = false;
                Show(refined);
                ScheduleStorySave();
                AddLog($"{label}: done in {(DateTime.Now - started):m\\:ss}: {refined}");
                return true;
            }
            catch (OperationCanceledException) { beat.Status = previousStatus; throw; }
            catch (Exception ex)
            {
                beat.Status = previousStatus;
                AddLog($"⚠ {label}: face refine failed ({ex.Message}) — the take is kept as rendered. " +
                       $"It needs the ComfyUI-H3-FaceRefine ({FaceRefinePlusGraph.BaseRepo}) and ComfyUI-H3-FaceRefine-Plus " +
                       $"({FaceRefinePlusGraph.PlusRepo}) node packs and face_yolov8m.pt in models/ultralytics/bbox.");
                _logger.LogWarning($"{Profile.Name} face refine failed: {ex}");
                return false;
            }
            finally { beat.IsRefiningFace = false; }
        }
    }
}
