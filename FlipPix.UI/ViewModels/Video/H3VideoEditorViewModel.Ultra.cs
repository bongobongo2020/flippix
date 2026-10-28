using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using FlipPix.Core.Models;
using FlipPix.UI.Models;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// The PRESET dropdown's <b>💎 All-In-One Ultra (Full-Res Tiled)</b> entry: each clip renders through 🌀 MiniMax
    /// I2V's Ultra graph (h3-minimax-ultra.json — one pass at the finished canvas, sampled by SelfLift with the
    /// LMS latent upscaler and full-res tiling) instead of the editor's chain graph, with the same card of dials.
    ///
    /// <para><b>The graph is built by the I2V tab's own builder</b>
    /// (<see cref="MiniMaxI2VViewModel.BuildUltraWorkflow"/>), so every dial reaches the node it does there and
    /// the defaults are the authored, tested workflow. The editor then sets what is per clip — prompt, length,
    /// file name, aspect — and wires the same Motion Context chain as its classic graph: the H3 Motion Context
    /// conditioning replaces Ref2V's into SelfLift's positive (and the zeroed negative), the sampler's latent is
    /// saved for the next clip, and the pinned head is trimmed off picture and sound before the VHS sink. A
    /// continuing clip runs untiled: SelfLift's tiler cannot read the pinned rows.</para>
    ///
    /// <para>The card's dials are kept on <see cref="H3VideoEditorSettings.Ultra"/>, apart from the I2V tab's, and
    /// its canvas is the editor's header aspect at the card's size — the whole timeline is one canvas so the
    /// chain can carry from clip to clip.</para>
    /// </summary>
    public partial class H3VideoEditorViewModel
    {
        /// <summary>The Ultra card. The same object settings persist, so an edit is saved as it is made.</summary>
        public MiniMaxI2VUltraOptions Ultra { get; private set; } = new();

        /// <summary>Whether the picked preset is the Ultra one.</summary>
        public bool IsUltra => SelectedPreset?.IsUltra == true;

        /// <summary>The classic chain graph's dials are shown (Ultra and 2-Pass hide them).</summary>
        public bool IsClassicEngine => !IsUltra && !IsTwoPass;

        public RelayCommand ResetUltraCommand { get; private set; } = null!;
        public RelayCommand RefreshUltraModelsCommand { get; private set; } = null!;

        private bool _isLoadingUltraModels;

        private void InitializeUltra()
        {
            var settings = _settingsService.Settings?.H3VideoEditor;
            Ultra = settings?.Ultra ?? new MiniMaxI2VUltraOptions();
            if (settings != null) settings.Ultra = Ultra;
            Ultra.PropertyChanged += OnUltraChanged;

            ResetUltraCommand = new RelayCommand(ResetUltra);
            RefreshUltraModelsCommand = new RelayCommand(() => _ = LoadUltraModelsAsync(), () => !_isLoadingUltraModels);

            UltraDiffusionModelOptions.Add(new DiffusionModelOption(
                MiniMaxI2VUltraOptions.AuthoredDiffusionModel,
                MiniMaxI2VViewModel.LabelFor(MiniMaxI2VUltraOptions.AuthoredDiffusionModel) + " (workflow)"));
            foreach (var m in new[] { H3EditorPreset.VanillaModel, H3EditorPreset.FusedTurboModel })
                UltraDiffusionModelOptions.Add(new DiffusionModelOption(m, MiniMaxI2VViewModel.LabelFor(m)));
            OfferUltraModel();
        }

        // ── The card ────────────────────────────────────────────────────────────────────────────────

        public ObservableCollection<DiffusionModelOption> UltraDiffusionModelOptions { get; } = new();

        public IReadOnlyList<DiffusionModelOption> UltraTextEncoderOptions => MiniMaxI2VViewModel.UltraTextEncoders;
        public IReadOnlyList<string> UltraAttentionOptions => MiniMaxI2VViewModel.UltraAttentions;
        public IReadOnlyList<string> UltraSchedulerOptions => MiniMaxI2VViewModel.UltraSchedulers;
        public IReadOnlyList<string> UltraUpscalerOptions => MiniMaxI2VViewModel.UltraUpscalers;
        public IReadOnlyList<MegapixelOption> UltraMegapixelOptions => MiniMaxI2VViewModel.UltraMegapixels;

        public string UltraAuthoredBadge => Ultra.IsAuthored
            ? "✓ Every dial is on the workflow's tested value."
            : "Changed from the workflow's tested values — ↺ puts them back.";

        public string UltraCanvasSummary
        {
            get
            {
                var (w, h) = H3Canvas.Resolve(SelectedAspectRatio, Ultra.Megapixels, MiniMaxI2VViewModel.UltraResolutionMultiple);
                return $"→ {SelectedAspectRatio} (header) · {w}×{h} finished, sampled at " +
                       $"{(int)(w * Ultra.LowresScale)}×{(int)(h * Ultra.LowresScale)} for the first " +
                       $"{MiniMaxI2VViewModel.UltraTransitionFor(Ultra)} step(s). Clip lengths come from the timeline.";
            }
        }

        public string UltraSamplingSummary => MiniMaxI2VViewModel.UltraSamplingSummaryFor(Ultra);

        public string UltraLoraSummary => MiniMaxI2VViewModel.UltraLoraSummaryFor(Ultra);

        private void OnUltraChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Every edit also announces IsAuthored; one save per edit is enough.
            if (e.PropertyName == nameof(MiniMaxI2VUltraOptions.IsAuthored)) return;
            SaveSettings();
            RaiseUltraState();
        }

        private void RaiseUltraState()
        {
            OnPropertyChanged(nameof(UltraCanvasSummary));
            OnPropertyChanged(nameof(UltraSamplingSummary));
            OnPropertyChanged(nameof(UltraLoraSummary));
            OnPropertyChanged(nameof(UltraAuthoredBadge));
        }

        private void ResetUltra()
        {
            Ultra.Reset();
            OfferUltraModel();
            AddLog("💎 Ultra: every dial back on the authored workflow's value.");
        }

        /// <summary>Keeps the card's checkpoint in the model list, for when the server has not answered (or
        /// no longer has that file).</summary>
        private void OfferUltraModel()
        {
            var name = (Ultra.DiffusionModel ?? string.Empty).Trim().Replace('\\', '/');
            if (name.Length == 0) return;
            if (UltraDiffusionModelOptions.Any(o => string.Equals(o.Value, name, StringComparison.OrdinalIgnoreCase)))
                return;
            UltraDiffusionModelOptions.Add(new DiffusionModelOption(name, MiniMaxI2VViewModel.LabelFor(name)));
        }

        /// <summary>Fills the model list from the server's diffusion_models/h3-minimax, keeping the card's pick.</summary>
        private async Task LoadUltraModelsAsync()
        {
            if (_isLoadingUltraModels) return;
            _isLoadingUltraModels = true;
            RefreshUltraModelsCommand.NotifyCanExecuteChanged();
            try
            {
                var found = (await _comfyUIService.HttpClient.GetUnetFilenamesAsync())
                    .Select(n => n.Replace('\\', '/'))
                    .Where(n => n.StartsWith("h3-minimax/", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (found.Count == 0)
                {
                    AddLog("Ultra models: ComfyUI reported nothing under diffusion_models/h3-minimax.");
                    return;
                }

                var keep = Ultra.DiffusionModel;
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    UltraDiffusionModelOptions.Clear();
                    foreach (var n in found)
                        UltraDiffusionModelOptions.Add(new DiffusionModelOption(n, MiniMaxI2VViewModel.LabelFor(n) +
                            (n == MiniMaxI2VUltraOptions.AuthoredDiffusionModel ? " (workflow)" : string.Empty)));
                    if (!found.Any(n => string.Equals(n, keep, StringComparison.OrdinalIgnoreCase)))
                        UltraDiffusionModelOptions.Add(new DiffusionModelOption(keep, MiniMaxI2VViewModel.LabelFor(keep) + " (not on server)"));
                    // Clear() pushed a blank back through the ComboBox binding, which the options object refuses;
                    // re-announce so the selection shows again.
                    Ultra.Refresh(nameof(MiniMaxI2VUltraOptions.DiffusionModel));
                });
                AddLog($"Ultra models: {found.Count} in diffusion_models/h3-minimax.");
            }
            catch (Exception ex)
            {
                AddLog($"Ultra models could not be read ({ex.Message}).");
            }
            finally
            {
                _isLoadingUltraModels = false;
                RefreshUltraModelsCommand.NotifyCanExecuteChanged();
            }
        }

        // ── The clip graph ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// One clip through h3-minimax-ultra.json: the card's dials via the I2V tab's builder, then the editor's
        /// per-clip inputs, the Motion Context chain and the thumbnail.
        /// </summary>
        private async Task<(JsonObject workflow, string saveNodeId, string thumbnailNodeId, string? contextFolder)>
            BuildUltraClipWorkflowAsync(H3TimelineClip clip, string?[] uploadedImages)
        {
            var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, MiniMaxI2VViewModel.UltraWorkflowFileName);
            if (!File.Exists(workflowPath))
                throw new FileNotFoundException($"Workflow file not found: {MiniMaxI2VViewModel.UltraWorkflowFileName}");

            var pictures = OrderedPictures(uploadedImages);
            var options = Ultra.Clone();
            options.DiffusionModel = options.DiffusionModel.Trim().Replace('\\', '/');
            var item = new MiniMaxI2VQueueItem
            {
                Workflow = I2VWorkflow.Ultra,
                Ultra = options,
                Prompt = clip.Prompt,
                AspectRatio = SelectedAspectRatio,
                Megapixels = options.Megapixels,
            };

            var built = MiniMaxI2VViewModel.BuildUltraWorkflow(
                await File.ReadAllTextAsync(workflowPath), item, pictures, Array.Empty<string>(), Array.Empty<string>(),
                Random.Shared.NextInt64(0, 999_999_999_999_999L), "h3_editor", out _);
            var workflow = JsonNode.Parse(built)?.AsObject() ?? throw new Exception("Ultra workflow could not be parsed.");

            UltraInput(workflow, MiniMaxI2VViewModel.UltraDuration)["value"] = clip.DurationSeconds;
            UltraInput(workflow, MiniMaxI2VViewModel.UltraSink)["filename_prefix"] = $"video/{ProjectFolder}/clip_{clip.DisplayIndex:D5}";

            AddLog($"Engine: 💎 Ultra — {Path.GetFileName(options.DiffusionModel)}, {UltraSamplingSummary}");
            AddLog($"  LoRAs: {UltraLoraSummary}");
            AddLog(pictures.Count > 0 ? $"References: {pictures.Count} picture(s)" : "No reference pictures — text-to-video");

            var contextFolder = ApplyUltraChain(workflow, clip);
            AddThumbnailNodes(workflow, clip, workflow.ContainsKey("trim") ? "trim" : MiniMaxI2VViewModel.UltraDecode);
            return (workflow, MiniMaxI2VViewModel.UltraSink, ThumbnailSaveNodeId, contextFolder);
        }

        /// <summary>The filled picture slots, in order, with the same gap warning as the classic graph.</summary>
        private List<string> OrderedPictures(string?[] uploadedImages)
        {
            var lastFilled = Array.FindLastIndex(uploadedImages, u => !string.IsNullOrEmpty(u));
            if (lastFilled >= 0 && uploadedImages.Take(lastFilled).Any(string.IsNullOrEmpty))
                AddLog("⚠ A Picture slot before the last filled one is empty — the pictures after it are renumbered " +
                       "and no longer match the <Picture N> tags in the prompt. Fill the slots in order.");
            return uploadedImages.Where(u => !string.IsNullOrEmpty(u)).Select(u => u!).ToList();
        }

        /// <summary>
        /// The Motion Context chain on the Ultra graph — the same nodes and settings as the classic graph's
        /// (<see cref="ApplyChain"/>). Returns the folder this clip's latent is saved to, or null when none is.
        /// </summary>
        private string? ApplyUltraChain(JsonObject workflow, H3TimelineClip clip)
        {
            if (!ChainAvailable) return null;

            const string selfLift = MiniMaxI2VViewModel.UltraSelfLift;
            var folder = ChainFolderFor(clip);
            workflow["ctx_save"] = UltraNode("MiniMaxH3MotionContextSaveLatent", "Motion Context - save this clip", new JsonObject
            {
                ["latent"] = new JsonArray(selfLift, 0),
                ["filename_prefix"] = $"{folder}/clip",
                ["clip_index"] = 1,
            });

            var prev = ChainPredecessor(clip);
            if (prev != null)
            {
                var vae = MiniMaxI2VViewModel.UltraVideoVae;
                var audioVae = MiniMaxI2VViewModel.UltraAudioVae;
                var refNode = MiniMaxI2VViewModel.UltraRef2V;

                workflow["ctx_load"] = UltraNode("MiniMaxH3MotionContextLoadLatent", "Motion Context - previous clip", new JsonObject
                {
                    ["latent_path"] = prev.ContextLatentFolder,
                    ["clip_index"] = 1,
                });
                workflow["ctx"] = UltraNode("MiniMaxH3MotionContext", "Motion Context (pins the previous clip's tail)", new JsonObject
                {
                    ["conditioning"] = new JsonArray(refNode, 0),
                    ["vae"] = new JsonArray(vae, 0),
                    ["latent"] = new JsonArray(refNode, 1),
                    ["context_length"] = ChainContextFrames.ToString(),
                    ["audio_context_length"] = 18,
                    ["context_latent"] = new JsonArray("ctx_load", 0),
                    ["audio_vae"] = new JsonArray(audioVae, 0),
                });

                // Both of SelfLift's conditionings carry the pinned rows; the negative stays the zeroed positive.
                UltraInput(workflow, selfLift)["positive"] = new JsonArray("ctx", 0);
                UltraInput(workflow, MiniMaxI2VViewModel.UltraNegative)["conditioning"] = new JsonArray("ctx", 0);

                // SelfLift's tiler sizes its tiles from each keyframe's "latent", which the pinned rows do not
                // carry — it raises KeyError: 'latent'. Untiled, the same chain renders and joins seamlessly
                // (checked on the server), so a continuing clip runs its full-size steps in one piece.
                if (Ultra.HighresTiling)
                {
                    UltraInput(workflow, selfLift)["highres_tiling"] = false;
                    AddLog("  Full-res tiling is off for this clip: SelfLift cannot tile a Motion Context chain. " +
                           "If the full-size steps run out of VRAM, lower the Ultra size or turn on Low VRAM block swap.");
                }

                workflow["trim"] = UltraNode("MiniMaxH3MotionContextTrim", "Trim pinned head", new JsonObject
                {
                    ["images"] = new JsonArray(MiniMaxI2VViewModel.UltraDecode, 0),
                    ["trim_frames"] = new JsonArray("ctx", 1),
                    ["audio"] = new JsonArray(MiniMaxI2VViewModel.UltraDecodeAudio, 0),
                    ["fps"] = (double)ChainFrameRate,
                    ["match_tail"] = true,
                });
                UltraInput(workflow, MiniMaxI2VViewModel.UltraSink)["images"] = new JsonArray("trim", 0);
                UltraInput(workflow, MiniMaxI2VViewModel.UltraSink)["audio"] = new JsonArray("trim", 1);

                var rendered = ChainedSeconds(clip.DurationSeconds);
                UltraInput(workflow, MiniMaxI2VViewModel.UltraDuration)["value"] = rendered;
                AddLog($"🔗 Clip {clip.DisplayIndex} continues clip {prev.DisplayIndex}: {ChainContextFrames} frames pinned, " +
                       $"rendered {rendered:0.##}s → ≈{clip.DurationSeconds:0.#}s delivered after the trim");
            }
            else
            {
                var idx = TimelineClips.IndexOf(clip);
                AddLog(idx > 0 && clip.UseMotionContext
                    ? $"🔗 Clip {clip.DisplayIndex} starts a new chain — clip {idx} has no chained render to continue from."
                    : $"🔗 Clip {clip.DisplayIndex} starts the chain.");
            }

            var next = TimelineClips.IndexOf(clip) + 1;
            if (next > 0 && next < TimelineClips.Count && TimelineClips[next].IsRendered && TimelineClips[next].UseMotionContext)
                AddLog($"⚠ Clip {next + 1} was rendered continuing the take this replaces — regenerate it too or the join won't match.");

            return folder;
        }

        private static JsonObject UltraNode(string classType, string title, JsonObject inputs) => new()
        {
            ["inputs"] = inputs,
            ["class_type"] = classType,
            ["_meta"] = new JsonObject { ["title"] = title },
        };

        private static JsonObject UltraInput(JsonObject workflow, string nodeId) =>
            workflow[nodeId]?["inputs"] as JsonObject
            ?? throw new Exception($"Workflow node '{nodeId}' is missing — {MiniMaxI2VViewModel.UltraWorkflowFileName} no longer matches the editor.");
    }
}
