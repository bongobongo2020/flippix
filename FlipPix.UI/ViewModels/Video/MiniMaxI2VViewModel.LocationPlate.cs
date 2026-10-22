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
    /// <para><b>When it renders.</b> ✨ Analyze &amp; Queue writes the plate's prompt with the take's and hands the
    /// writer that description in place of the picture; the Qwen render waits for the row's own turn on the GPU
    /// (see <c>GenerateItemAsync</c>), so a new prompt never waits for the video in progress. ✎ Write prompt renders
    /// it at once and puts it in the first free slot, where later Analyzes reuse it.</para>
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
        /// Asks the LLM for the plate's prompt — the place the draft happens in, with its landmarks placed. Needs no
        /// GPU, so it runs straight away even while a video renders. Null when nothing usable came back; never
        /// throws for that, since the plate is an improvement to the take rather than a requirement of it.
        /// </summary>
        private async Task<string?> WriteLocationPlatePromptAsync(string model, AnalysisJob job, CancellationToken token)
        {
            try
            {
                var systemPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prompts", "prompt2json",
                                              LocationPlatePromptFile);
                var system = await File.ReadAllTextAsync(systemPath, token);
                var pictures = job.Pictures.Where(p => !p.IsLocationPlate).Select(p => p.Path).ToList();
                var request = new StringBuilder()
                    .AppendLine("Draft idea for the video:")
                    .AppendLine(string.IsNullOrWhiteSpace(job.Draft) ? "(none — choose a place that suits the pictures)" : job.Draft)
                    .AppendLine()
                    .Append(pictures.Count > 0
                        ? "The attached pictures are the characters. Take only what they wear and their era from them; the place is yours to write."
                        : "There are no character pictures.")
                    .ToString();

                AddLog("Location plate: writing the plate prompt...");
                var reply = pictures.Count > 0
                    ? await Task.Run(() => _lmStudioService.AnalyzeMultipleImagesWithSystemPromptAsync(
                        model, pictures, request, system, maxTokens: 600, cancellationToken: token), token)
                    : await _lmStudioService.SendTextChatAsync(model, system, request, cancellationToken: token);
                var platePrompt = CleanLLMOutput(reply).Trim().Trim('"');
                if (platePrompt.Length < 20)
                {
                    AddLog("Location plate: the writer returned nothing usable — Analyze carries on without one.");
                    return null;
                }
                AddLog($"Location plate prompt: {platePrompt}");
                return platePrompt;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AddLog($"Location plate: failed ({ex.Message}) — Analyze carries on without one.");
                return null;
            }
        }

        /// <summary>
        /// Renders a plate from its prompt with Qwen Image and returns the local path, or null when it could not be
        /// made (the reason is logged). <paramref name="holdsLease"/> is true when the caller already has the GPU —
        /// a queued take renders its plate at the start of its own turn — and false for ✎ Write prompt, which
        /// waits its turn like any other job.
        /// </summary>
        private async Task<string?> RenderLocationPlateAsync(
            string platePrompt, string aspect, bool holdsLease, CancellationToken token)
        {
            WorkflowQueueCoordinator.WorkflowLease? lease = null;
            try
            {
                var (w, h) = H3Canvas.Resolve(aspect, LocationPlateMegapixels, 16);
                var runToken = $"plate_{DateTime.Now:yyyyMMddHHmmssfff}";
                var seed = Random.Shared.NextInt64(0, 1_000_000_000_000_000L);
                var (json, saveNode) = await CastPhotoWorkflows.BuildLocationPlateAsync(
                    $"{OutputSubfolder}/{runToken}", seed, platePrompt, w, h);

                if (!holdsLease)
                {
                    AddLog("Location plate: waiting for the GPU...");
                    lease = await _workflowCoordinator.AcquireAsync("MiniMaxI2V", token);
                }
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

                AddLog($"Location plate: {Path.GetFileName(local)}.");
                return local;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AddLog($"Location plate: failed ({ex.Message}).");
                return null;
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
