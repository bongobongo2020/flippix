using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using FlipPix.Core.Models;
using FlipPix.UI.Models;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>One entry of the ⛓ FFLF tab's Workflow dropdown.</summary>
    public sealed record FflfWorkflowOption(string Key, string Label, string Description);

    /// <summary>
    /// The ⛓ FFLF tab's beat workflow: the ErosMax 2-Pass graph (<see cref="FflfGraph"/>) or PikaFlex + ReDiverse
    /// (<see cref="PikaFlexGraph"/>). Only the FFLF tab itself offers the choice; the tabs built on it keep their own graph.
    /// </summary>
    public partial class H3FflfViewModel
    {
        public const string ErosMaxWorkflow = "erosmax";

        public IReadOnlyList<FflfWorkflowOption> BeatWorkflowOptions { get; } = new[]
        {
            new FflfWorkflowOption(ErosMaxWorkflow, "ErosMax 2-Pass (default)",
                "Low-res first pass → latent upscale → refine → RTX. Takes the opening image and character sheets as references."),
            new FflfWorkflowOption(PikaFlexGraph.Key, "PikaFlex + ReDiverse — fast 6-step LCM",
                "fl2va model + PikaFlex LoRA, ReDiverse on the first step. One pass at a single canvas; starts on the first frame, no reference pictures."),
        };

        public IReadOnlyList<MegapixelOption> PikaSizes { get; } = new[]
        {
            new MegapixelOption(0.4, "0.4 MP"),
            new MegapixelOption(0.6, "0.6 MP (workflow)"),
            new MegapixelOption(0.8, "0.8 MP"),
            new MegapixelOption(1.0, "1.0 MP"),
        };

        /// <summary>The size a PikaFlex ⚡ draft renders at.</summary>
        public const double PikaDraftMegapixels = 0.3;

        /// <summary>Whether this tab shows the Workflow dropdown (the tabs built on it render their own graph).</summary>
        protected virtual bool OffersBeatWorkflows => GetType() == typeof(H3FflfViewModel);

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(UsePikaFlex), nameof(UseErosMax), nameof(CanvasSummary), nameof(BeatWorkflowDescription), nameof(CharacterSheetsHint))]
        private string _beatWorkflow = ErosMaxWorkflow;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanvasSummary))]
        private double _pikaMegapixels = 0.6;

        // PikaFlex's own files, shown in ⚙ Model in place of the ErosMax model and LoRA. Empty = the authored file,
        // under whatever name the server keeps it (filled in once the server lists are read).
        [ObservableProperty] private string _pikaModel = string.Empty;
        [ObservableProperty] private string _pikaLora = string.Empty;
        [ObservableProperty] private double _pikaLoraStrength = 1.0;
        [ObservableProperty] private string _rediverseLora = string.Empty;
        [ObservableProperty] private double _rediverseStrength = 1.0;

        public bool UsePikaFlex => OffersBeatWorkflows && BeatWorkflow == PikaFlexGraph.Key;
        public bool UseErosMax => !UsePikaFlex;

        public string BeatWorkflowDescription =>
            BeatWorkflowOptions.FirstOrDefault(o => o.Key == BeatWorkflow)?.Description ?? string.Empty;

        private IReadOnlyList<string> _serverModels = Array.Empty<string>();

        private void InitBeatWorkflow(H3FflfSettings s)
        {
            // The fields, not the properties: nothing is saved or logged while the constructor reads the settings.
#pragma warning disable MVVMTK0034
            _beatWorkflow = BeatWorkflowOptions.Any(o => o.Key == s.BeatWorkflow) ? s.BeatWorkflow : ErosMaxWorkflow;
            _pikaMegapixels = PikaSizes.Any(o => o.Value == s.PikaMegapixels) ? s.PikaMegapixels : 0.6;
            _pikaModel = s.PikaModel ?? string.Empty;
            _pikaLora = s.PikaLora ?? string.Empty;
            _pikaLoraStrength = Math.Clamp(s.PikaLoraStrength, 0, 2);
            _rediverseLora = s.RediverseLora ?? string.Empty;
            _rediverseStrength = Math.Clamp(s.RediverseStrength, 0, 2);
#pragma warning restore MVVMTK0034
            OfferPikaFiles();
        }

        private void OfferPikaFiles()
        {
            OfferModel(PikaModel);
            OfferLora(PikaLora);
            OfferLora(RediverseLora);
        }

        private void SaveBeatWorkflow(H3FflfSettings s)
        {
            s.BeatWorkflow = BeatWorkflow;
            s.PikaMegapixels = PikaMegapixels;
            s.PikaModel = PikaModel;
            s.PikaLora = PikaLora;
            s.PikaLoraStrength = PikaLoraStrength;
            s.RediverseLora = RediverseLora;
            s.RediverseStrength = RediverseStrength;
        }

        private void CaptureBeatWorkflow(FflfRecipe r)
        {
            r.BeatWorkflow = BeatWorkflow;
            r.PikaMegapixels = PikaMegapixels;
            r.PikaModel = PikaFiles().DiffusionModel;
            r.PikaLora = PikaFiles().PikaLora;
            r.PikaLoraStrength = PikaLoraStrength;
            r.RediverseLora = PikaFiles().RediverseLora;
            r.RediverseStrength = RediverseStrength;
        }

        /// <summary>A recipe's workflow and PikaFlex files; one saved before the choice existed is an ErosMax recipe.</summary>
        private void ApplyBeatWorkflow(FflfRecipe r)
        {
            if (!OffersBeatWorkflows) return;
            BeatWorkflow = BeatWorkflowOptions.Any(o => o.Key == r.BeatWorkflow) ? r.BeatWorkflow : ErosMaxWorkflow;
            if (string.IsNullOrWhiteSpace(r.BeatWorkflow)) return;
            if (PikaSizes.Any(o => o.Value == r.PikaMegapixels)) PikaMegapixels = r.PikaMegapixels;
            if (!string.IsNullOrWhiteSpace(r.PikaModel)) PikaModel = r.PikaModel;
            if (!string.IsNullOrWhiteSpace(r.PikaLora)) PikaLora = r.PikaLora;
            if (!string.IsNullOrWhiteSpace(r.RediverseLora)) RediverseLora = r.RediverseLora;
            PikaLoraStrength = Math.Clamp(r.PikaLoraStrength, 0, 2);
            RediverseStrength = Math.Clamp(r.RediverseStrength, 0, 2);
            OfferPikaFiles();
        }

        partial void OnBeatWorkflowChanged(string value)
        {
            if (_loading) return;
            AddLog(UsePikaFlex
                ? $"Workflow: PikaFlex + ReDiverse — {Path.GetFileName(PikaFiles().DiffusionModel)}, 6-step LCM at {PikaMegapixels:0.#} MP. " +
                  "⚙ Model now shows the PikaFlex model and LoRAs (extra LoRAs still apply); the opening image and character sheets are not sent as references."
                : "Workflow: ErosMax 2-Pass.");
            if (Beats.Any(b => b.State != FflfBeatState.Pending))
                AddLog("Takes already rendered stay; ↻ Redo or Start over to render them with this workflow.");
        }

        /// <summary>The PikaFlex files: the ones picked in ⚙ Model, or the authored ones under the names the server keeps them.</summary>
        private PikaFlexFiles PikaFiles() => new()
        {
            DiffusionModel = Pick(PikaModel, _serverModels, PikaFlexGraph.AuthoredModel),
            PikaLora = Pick(PikaLora, ServerLoras, PikaFlexGraph.AuthoredPikaLora),
            RediverseLora = Pick(RediverseLora, ServerLoras, PikaFlexGraph.AuthoredRediverseLora),
            PikaStrength = PikaLoraStrength,
            RediverseStrength = RediverseStrength,
        };

        private static string Pick(string chosen, IEnumerable<string> server, string authored) =>
            string.IsNullOrWhiteSpace(chosen) ? PikaFlexGraph.Resolve(server, authored) : chosen.Trim().Replace('\\', '/');

        /// <summary>After the server lists are read: a PikaFlex picker left empty shows the file it will use.</summary>
        private void FillPikaPickers()
        {
            var wasLoading = _loading;
            _loading = true;
            try
            {
                if (string.IsNullOrWhiteSpace(PikaModel) && _serverModels.Count > 0) PikaModel = PikaFlexGraph.Resolve(_serverModels, PikaFlexGraph.AuthoredModel);
                if (string.IsNullOrWhiteSpace(PikaLora) && ServerLoras.Count > 0) PikaLora = PikaFlexGraph.Resolve(ServerLoras, PikaFlexGraph.AuthoredPikaLora);
                if (string.IsNullOrWhiteSpace(RediverseLora) && ServerLoras.Count > 0) RediverseLora = PikaFlexGraph.Resolve(ServerLoras, PikaFlexGraph.AuthoredRediverseLora);
            }
            finally { _loading = wasLoading; }
            OfferPikaFiles();
        }

        /// <summary>After the server lists are read: says which PikaFlex files it does not have.</summary>
        private void WarnMissingPikaFiles()
        {
            if (OffersBeatWorkflows) FillPikaPickers();
            if (!UsePikaFlex || (_serverModels.Count == 0 && ServerLoras.Count == 0)) return;
            var files = PikaFiles();
            var missing = new List<string>();
            if (_serverModels.Count > 0 && !_serverModels.Contains(files.DiffusionModel, StringComparer.OrdinalIgnoreCase)) missing.Add(files.DiffusionModel);
            if (ServerLoras.Count > 0 && !ServerLoras.Contains(files.PikaLora, StringComparer.OrdinalIgnoreCase)) missing.Add(files.PikaLora);
            if (ServerLoras.Count > 0 && !ServerLoras.Contains(files.RediverseLora, StringComparer.OrdinalIgnoreCase)) missing.Add(files.RediverseLora);
            if (missing.Count > 0)
                AddLog($"⚠ PikaFlex: {string.Join(", ", missing.Select(Path.GetFileName))} not on the server — put them in diffusion_models / loras.");
        }

        private System.Text.Json.Nodes.JsonObject BuildPikaFlexGraph(FflfBeatRender request) =>
            PikaFlexGraph.Build(request, PikaFiles(), request.PreviewOnly ? PikaDraftMegapixels : PikaMegapixels);

        private string PikaCanvasSummary()
        {
            var mp = PreviewOnly ? PikaDraftMegapixels : PikaMegapixels;
            var (w, h) = H3Canvas.Resolve(ResolvedAspectRatio, mp, PikaFlexGraph.ResolutionMultiple);
            return $"{ResolvedAspectRatio} · PikaFlex one pass at {w}×{h}{(PreviewOnly ? " (draft)" : string.Empty)} · 6-step LCM";
        }
    }
}
