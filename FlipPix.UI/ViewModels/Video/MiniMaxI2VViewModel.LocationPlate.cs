using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FlipPix.UI.Models;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// 🌀 MiniMax I2V's <b>location plate</b>: an empty set, rendered by the Image Generator's Qwen Image 2512
    /// graph into a free reference slot when Analyze runs, so the take is staged in a real place.
    ///
    /// <para><b>Why.</b> H3 given only character references has nothing to build the set from but the words,
    /// and those clips come out flat. A picture of the place is what the references cannot supply. So Analyze
    /// first asks the LLM for a plate prompt — the place the draft idea happens in, two or three landmarks placed
    /// left, right and far, and nobody in it (<c>prompts/prompt2json/h3-location-plate.md</c>) — renders it at the
    /// clip's aspect, and then writes the Ref2VA prompt with that picture declared as the setting.</para>
    ///
    /// <para><b>When it does nothing.</b> The switch is off; a plate is already in a slot (it is reused — clear the
    /// slot to get a new one); all four slots are full; or <c>&lt;Picture 1&gt;</c> is a stereo pair, which is
    /// already the setting. A plate that fails to render is logged and Analyze carries on without one.</para>
    /// </summary>
    public partial class MiniMaxI2VViewModel
    {
        private const string LocationPlatePromptFile = "h3-location-plate.md";

        /// <summary>The plate's size budget — a little over the 1.5 MP finish, so the reference is never the
        /// weakest picture in the set.</summary>
        private const double LocationPlateMegapixels = 1.6;

        private bool? _autoLocationPlate;

        /// <summary>Whether Analyze renders a location plate into a free slot first. Persisted, on by default.</summary>
        public bool AutoLocationPlate
        {
            get => _autoLocationPlate ??= _settingsService.Settings?.MiniMaxI2VAutoLocationPlate ?? true;
            set
            {
                if (AutoLocationPlate == value) return;
                _autoLocationPlate = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LocationPlateSummary));

                var settings = _settingsService.Settings;
                if (settings != null)
                {
                    settings.MiniMaxI2VAutoLocationPlate = value;
                    _settingsService.SaveSettings(settings);
                }
            }
        }

        /// <summary>The line under the switch.</summary>
        public string LocationPlateSummary
        {
            get
            {
                if (!AutoLocationPlate)
                    return "Off — add a picture of the place yourself; without one the take tends to come out flat.";
                var plate = References.FirstOrDefault(r => r.IsLocationPlate && r.HasImage);
                if (plate != null)
                    return $"Picture {plate.Slot} is the plate, and Analyze reuses it. Clear that slot for a new one.";
                return References.Skip(1).Any(r => !r.HasImage)
                    ? "Analyze renders an empty set for the draft idea (Qwen Image) into the first free slot, and " +
                      "stages the take in it."
                    : "All four slots are full — no plate will be rendered.";
            }
        }

        /// <summary>The slot the plate would go in: the first free one after <c>&lt;Picture 1&gt;</c>.</summary>
        private MiniMaxI2VReference? FreePlateSlot() =>
            References.Skip(1).FirstOrDefault(r => !r.HasImage);

        /// <summary>
        /// Renders the plate if one is wanted and there is room. Never throws for a render failure — the plate is
        /// an improvement to the take, not a requirement of it — but does honour cancellation.
        /// </summary>
        private async Task EnsureLocationPlateAsync(string model, CancellationToken token)
        {
            if (!AutoLocationPlate) return;
            if (References.Any(r => r.IsLocationPlate && r.HasImage))
            {
                AddLog("Location plate: reusing the one already in the references.");
                return;
            }
            if (PrimaryStereo != StereoLayout.None)
            {
                AddLog("Location plate: skipped — Picture 1 is a stereo pair and already sets the scene.");
                return;
            }
            var slot = FreePlateSlot();
            if (slot == null)
            {
                AddLog("Location plate: skipped — all four reference slots are full.");
                return;
            }

            WorkflowQueueCoordinator.WorkflowLease? lease = null;
            try
            {
                // ── The plate prompt ──────────────────────────────────────────
                var systemPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prompts", "prompt2json",
                                              LocationPlatePromptFile);
                var system = await File.ReadAllTextAsync(systemPath, token);
                var pictures = FilledReferences.Select(p => p.Path).ToList();
                var request = new StringBuilder()
                    .AppendLine("Draft idea for the video:")
                    .AppendLine(string.IsNullOrWhiteSpace(Prompt) ? "(none — choose a place that suits the pictures)" : Prompt.Trim())
                    .AppendLine()
                    .Append(pictures.Count > 0
                        ? "The attached pictures are the characters. Take only what they wear and their era from them; the place is yours to write."
                        : "There are no character pictures.")
                    .ToString();

                AddLog("Location plate: writing the plate prompt...");
                var reply = pictures.Count > 0
                    ? await _lmStudioService.AnalyzeMultipleImagesWithSystemPromptAsync(
                        model, pictures, request, system, maxTokens: 600, cancellationToken: token)
                    : await _lmStudioService.SendTextChatAsync(model, system, request, cancellationToken: token);
                var platePrompt = CleanLLMOutput(reply).Trim().Trim('"');
                if (platePrompt.Length < 20)
                {
                    AddLog("Location plate: the writer returned nothing usable — Analyze carries on without one.");
                    return;
                }
                AddLog($"Location plate prompt: {platePrompt}");

                // ── The render ────────────────────────────────────────────────
                var (w, h) = H3Canvas.Resolve(ResolvedAspectRatio, LocationPlateMegapixels, 16);
                var runToken = $"plate_{DateTime.Now:yyyyMMddHHmmss}";
                var seed = Random.Shared.NextInt64(0, 1_000_000_000_000_000L);
                var (json, saveNode) = await CastPhotoWorkflows.BuildLocationPlateAsync(
                    $"{OutputSubfolder}/{runToken}", seed, platePrompt, w, h);

                AddLog("Location plate: waiting for the GPU...");
                lease = await _workflowCoordinator.AcquireAsync("MiniMaxI2V", token);
                if (!await _comfyUIService.DetectAndRestartIfCrashedAsync(s => AddLog($"[Auto-Restart] {s}")))
                    throw new Exception("ComfyUI is not running.");
                if (!_comfyUIService.IsConnected) await _comfyUIService.ConnectAsync();

                AddLog($"Location plate: rendering {w}×{h} with Qwen Image (seed {seed})...");
                var workflow = JsonSerializer.Deserialize<JsonElement>(json);
                var promptId = await _comfyUIService.ExecuteWorkflowAsync(
                    workflow, new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(_ => { }), token);

                var byNode = await _comfyUIService.HttpClient.GetOutputsByNodeAsync(promptId, token);
                string? local = null;
                if (byNode.TryGetValue(saveNode, out var outs) && outs.Count > 0)
                    local = await ResolveOutputToLocalAsync(outs[0]);
                if (local == null || !File.Exists(local))
                    throw new Exception("the plate was not produced.");

                slot.SetLocationPlate(local);
                AddLog($"Location plate: Picture {slot.Slot} — {Path.GetFileName(local)}.");
                OnPropertyChanged(nameof(LocationPlateSummary));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AddLog($"Location plate: failed ({ex.Message}) — Analyze carries on without one.");
            }
            finally
            {
                lease?.Dispose();
            }
        }

        /// <summary>What the prompt writer is told about the plate, beside its picture number.</summary>
        private static string LocationPlateNote =>
            "LOCATION PLATE — the setting only, and it contains nobody. Bind it in subject_definitions as the " +
            "environment subject, stage the whole take inside it, keep its landmarks on the side of the frame " +
            "where the picture shows them, and never put a person from it on screen.";
    }
}
