using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FlipPix.Core.Models;
using FlipPix.UI.Models;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// ⛓ Long Form (<see cref="LongFormGraph"/>): the whole story as one Context-Loop chain on the server instead of one
    /// prompt per beat. The chain saves every beat with a checkpoint under <c>output/h3_chains/&lt;run&gt;</c>, so a story
    /// that stopped, or a beat that is redone, carries on from the beat before it; the beats land in the strip as the
    /// chain reports them, and the chain's own assembly is the joined film.
    /// </summary>
    public partial class H3FflfViewModel
    {
        private async Task RenderLongFormAsync(int start, List<FflfBeat> beats, string initial, string aspect, CancellationToken ct)
        {
            var http = _comfyUIService.HttpClient;
            var runName = _runFolder;

            // A beat after the first carries on from the server's checkpoint of the beat before it — only a Long Form
            // run of this story has those. Without them the chain starts where its saved beats end.
            if (start > 0)
            {
                var (next, _) = CharacterSwapGraph.SavedClips(await http.GetTextAsync(
                    $"{CharacterSwapGraph.ChainRoutes}/checkpoints?run_name={Uri.EscapeDataString(runName)}", TimeSpan.FromMinutes(1), ct));
                if (next - 1 < start)
                {
                    AddLog(next == 1
                        ? $"⚠ Long Form: no saved Long Form beats of this story on the server ({runName}) — it renders from beat 1."
                        : $"⚠ Long Form: the server has beats 1–{next - 1} of this chain saved — it carries on from beat {next}.");
                    start = next - 1;
                    ResetFrom(start);
                }
            }

            if (beats.Any(b => b.HasDialogue))
                AddLog("⚠ Long Form: per-beat dialogue is not lip-synced yet — the beats render with generated sound.");
            if (FaceRefine || RepairHandoffFace)
                AddLog("Long Form: face restore and ✨ face refine are hand-off tools and are skipped; the opening image rides along on every beat instead.");

            var shots = new List<LongFormShot>();
            for (var i = 0; i < beats.Count; i++)
            {
                var beat = beats[i];
                if (beat.Seed <= 0) beat.Seed = i == 0 ? BaseSeed : NewSeed();
                shots.Add(new LongFormShot($"beat_{i + 1:D2}", ComposeBeatPrompt(beat, i, beats.Count, hasIdentityImage: false),
                                           SecondsFor(beat), beat.Seed));
            }

            var loras = new List<MiniMaxI2VLora>();
            if (!string.IsNullOrWhiteSpace(Lora) && LoraStrength > 0) loras.Add(new MiniMaxI2VLora { Name = Lora, Strength = LoraStrength });
            loras.AddRange(ActiveExtraLoras());
            var request = new LongFormRender
            {
                Shots = shots,
                OpeningImage = initial,
                CastImages = _runSheets.Select(r => r.Uploaded).ToList(),
                RunName = runName,
                StartClip = start + 1,
                AspectRatio = aspect,
                Megapixels = PreviewOnly ? LongFormGraph.DraftMegapixels : LongFormMegapixels,
                Steps = PreviewOnly ? LongFormGraph.DraftSteps : LongFormGraph.Steps,
                DiffusionModel = DiffusionModel,
                TextEncoder = TextEncoder,
                Loras = loras,
                MaxFidelityReferences = MaxFidelityReferences,
                OutputSubfolder = $"video/{Profile.Key}/{runName}",
                Filename = $"{runName}_longform{(PreviewOnly ? "_draft" : string.Empty)}",
            };
            var graph = LongFormGraph.Build(request);
            AddLog($"Long Form chain {runName}: beats {start + 1}–{beats.Count}" +
                   (start > 0 ? $", carrying on from beat {start}'s checkpoint" : string.Empty) +
                   (_runSheets.Count > 0 ? $", opening image + {_runSheets.Count} character sheet(s) on every beat" : ", opening image on every beat") + ".");

            for (var i = start; i < beats.Count; i++)
            {
                beats[i].State = FflfBeatState.Pending;
                beats[i].Status = i == start ? "queued — renders first" : "queued";
            }

            var total = beats.Count - start;
            var current = start;              // 0-based beat the chain is on
            var landed = new List<DateTime>();
            var runStart = DateTime.Now;
            var beatStart = DateTime.Now;
            var fetches = new List<Task>();

            void OnExecuted(object? sender, FlipPix.ComfyUI.Models.NodeExecutedEventArgs e)
            {
                if (CharacterSwapGraph.ActiveClip(e.OutputJson) is { } scene && scene.Index - 1 < beats.Count)
                {
                    current = scene.Index - 1;
                    beatStart = DateTime.Now;
                    var beat = beats[current];
                    OnUi(() =>
                    {
                        beat.State = FflfBeatState.Rendering;
                        beat.Status = "rendering…";
                    });
                    SetLiveBeat($"Beat {current + 1}/{beats.Count}");
                    ProcessingStatus = $"Beat {current + 1}/{beats.Count}: {Shorten(beat.Text, 70)}" +
                                       (landed.Count == 0 && current == start ? " — the first beat also loads the models." : string.Empty);
                    return;
                }
                // VHS reports the clip with its sound as "…-audio.mp4"; take that one when it is there.
                var clips = e.Files.Where(f => f.Filename.StartsWith(LongFormGraph.ClipPreviewPrefix, StringComparison.Ordinal)
                                               && f.Filename.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)).ToList();
                var file = clips.FirstOrDefault(f => f.Filename.EndsWith("-audio.mp4", StringComparison.OrdinalIgnoreCase)) ?? clips.FirstOrDefault();
                if (file != null) lock (fetches) fetches.Add(LandBeatAsync(beats, current, file, DateTime.Now - beatStart, ct));
            }

            async Task LandBeatAsync(List<FflfBeat> all, int index, FlipPix.ComfyUI.Models.OutputFileRef file, TimeSpan took, CancellationToken token)
            {
                var beat = all[index];
                try
                {
                    var bytes = await http.DownloadViewFileAsync(file.Filename, file.Subfolder, file.Type, token);
                    if (bytes is not { Length: > 0 }) throw new InvalidOperationException("the clip came back empty");
                    Directory.CreateDirectory(WorkRoot);
                    var path = Path.Combine(WorkRoot, $"{runName}_beat{index + 1:D2}{(PreviewOnly ? "_draft" : string.Empty)}.mp4");
                    await File.WriteAllBytesAsync(path, bytes, token);
                    landed.Add(DateTime.Now);
                    OnUi(() =>
                    {
                        beat.VideoPath = path;
                        beat.OriginalVideoPath = string.Empty;
                        beat.LastFramePath = string.Empty;   // Long Form hands on the latent, not a frame
                        beat.RenderedText = beat.Text;
                        beat.RenderedDialogue = beat.DialoguePath;
                        beat.State = FflfBeatState.Done;
                        beat.Status = $"seed {beat.Seed} · {took:m\\:ss}";
                        ResultVideoPath = path;
                        ResultVideoInfo = $"{beat.Label} · Long Form";
                        HasResult = true;
                        if (WatchingLive) Show(path);
                        OnPropertyChanged(nameof(BeatSummary));
                    });
                    ScheduleStorySave();
                    var left = beats.Count - (index + 1);
                    AddLog($"Beat {index + 1}/{beats.Count} done in {took:m\\:ss}{(left > 0 && Eta(landed, left) is { } eta ? $" · about {eta} left" : string.Empty)}.");
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    AddLog($"⚠ Beat {index + 1}: its clip could not be fetched ({ex.Message}). The chain goes on; the story film still has it.");
                }
            }

            var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(msg =>
            {
                if (msg.Data == null || msg.Data.Max <= 0 || msg.Data.Max > 60) return;
                var frac = (double)msg.Data.Value / msg.Data.Max;
                ProcessingProgress = Math.Min(99, (current - start + frac) / Math.Max(1, total) * 100);
                ProcessingStatus = $"Beat {current + 1}/{beats.Count} — step {msg.Data.Value}/{msg.Data.Max} · {(DateTime.Now - beatStart):m\\:ss}";
                OnPropertyChanged(nameof(ReadyHint));
            });

            _comfyUIService.NodeExecuted += OnExecuted;
            string promptId;
            try
            {
                // The whole story is one prompt: hours, not minutes.
                promptId = await _comfyUIService.ExecuteWorkflowAsync(graph, progress, ct, TimeSpan.FromHours(12));
            }
            catch
            {
                OnUi(() =>
                {
                    foreach (var b in beats.Skip(start).Where(b => b.State == FflfBeatState.Rendering))
                    {
                        b.State = FflfBeatState.Failed;
                        b.Status = "failed — ↻ or Generate carries on from the beat before";
                    }
                });
                throw;
            }
            finally
            {
                _comfyUIService.NodeExecuted -= OnExecuted;
                SetLiveBeat(null);
                Task[] pending;
                lock (fetches) pending = fetches.ToArray();
                try { await Task.WhenAll(pending); } catch { /* each fetch logs its own failure */ }
            }
            if (string.IsNullOrEmpty(promptId)) throw new InvalidOperationException("ComfyUI returned no prompt id — the Long Form graph may have errors.");

            ProcessingStatus = "Fetching the story film…";
            var byNode = await http.GetOutputsByNodeAsync(promptId, ct);
            var film = byNode.TryGetValue(LongFormGraph.AssembleNode, out var files)
                ? files.FirstOrDefault(f => f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                : null;
            var local = film == null ? null : await FetchOutputFileAsync(film, ct);
            if (local == null)
            {
                AddLog("⚠ The chain finished but its assembled film was not found — 🎞 Join builds one from the beats.");
                return;
            }

            JoinedVideoPath = Keep(local, $"{runName}_longform{(PreviewOnly ? "_draft" : string.Empty)}");
            ResultVideoPath = JoinedVideoPath;
            ResultVideoInfo = $"Long Form · {beats.Count} beats · {Path.GetFileName(JoinedVideoPath)}";
            HasResult = true;
            Show(JoinedVideoPath);
            ScheduleStorySave();
            try { await LocalCopyService.CopyVideoAsync(JoinedVideoPath); } catch { /* the film is kept either way */ }
            AddLog($"Long Form story film ({beats.Count} beats, colour anchored to beat 1) in {(DateTime.Now - runStart):h\\:mm\\:ss}: {JoinedVideoPath}");
            var missing = beats.Select((b, i) => (b, i)).Where(x => !x.b.HasVideo).Select(x => x.i + 1).ToList();
            if (missing.Count > 0)
                AddLog($"⚠ Beat(s) {string.Join(", ", missing)} did not land in the strip; the film above has them.");
        }

        /// <summary>Time left from the gaps between landed beats (the first gap also loaded the models).</summary>
        private static string? Eta(List<DateTime> landed, int left)
        {
            if (landed.Count < 2) return null;
            var gaps = landed.Zip(landed.Skip(1), (a, b) => (b - a).TotalSeconds).ToList();
            var t = TimeSpan.FromSeconds(gaps.Average() * left);
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" : $"{Math.Max(1, (int)Math.Round(t.TotalMinutes))} min";
        }
    }
}
