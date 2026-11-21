using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FlipPix.UI.Services
{
    /// <summary>A model file the 🥷 Martial tab knows where to get.</summary>
    public sealed record MartialModelFile(string FileName, string Folder, string Url, string Purpose);

    /// <summary>
    /// The files the martial workflow (02_MiniMax_H3martial) uses, under the names they are published as. The
    /// authored workflow names a few files that were never published; the closest published file stands in:
    /// <list type="bullet">
    /// <item>MiniMax-H3-int8 → minimax_h3_fl2va_pruned_int8_convrot (the wushu LoRAs were trained on fl2va).</item>
    /// <item>qwen3vl_32b_minimax_h3_int8_convrot → the w4a8 encoder the other H3 tabs use (the int8 one is 27 GB).</item>
    /// <item>minimax_h3_fl2v_turbo_8step_v1.0_768p…rank_64 → minimax_h3_fl2v_turbo_8step_v1.0_comfyui_bf16 (unresized).</item>
    /// <item>wushu_spatial_physics_clean_2500 → wushu_spatial_physics_clean_3000_pruned.</item>
    /// <item>wushu_action_fl2va_v7_2000step_no_adaln → wushu_action_v7_fl2va_…_2000step (H3PowerLoraStack ports its adaLN).</item>
    /// <item>The bypassed "真实电影质感" film-look LoRA is not published; it is left out of the stack.</item>
    /// </list>
    /// </summary>
    public static class MartialCatalog
    {
        private const string Wushu = "https://huggingface.co/Jojocodex/wushu-action-v7-minimax-h3-fl2va-ref2va-lora/resolve/main/";
        private const string Physics = "https://huggingface.co/Jojocodex/minimax-h3-spatial-physics-lora/resolve/main/";
        private const string Camera = "https://huggingface.co/Jojocodex/minimax-h3-Camera-Motion-lora/resolve/main/";
        private const string Comfy = "https://huggingface.co/Comfy-Org/MiniMax-H3/resolve/main/";

        public const string TurboLora = "minimax_h3_fl2v_turbo_8step_v1.0_comfyui_bf16.safetensors";
        public const string PhysicsLora = "wushu_spatial_physics_clean_3000_pruned.safetensors";
        public const string WushuLora = "wushu_action_v7_fl2va_aitoolkit_adaln_full-int8convrot_bf16te_2000step.safetensors";
        public const string WushuR2vLora = "wushu_h3_r2v_v7_2000.safetensors";
        public const string CameraLora = "camera_motion_h3_lora_v1_3000_pruned.safetensors";

        public static IReadOnlyList<MartialModelFile> Files { get; } = new[]
        {
            new MartialModelFile(TurboLora, "loras/H3", Comfy + "loras/" + TurboLora, "8-step turbo (as authored, 0.8)"),
            new MartialModelFile(PhysicsLora, "loras/H3", Physics + PhysicsLora, "wushu spatial physics — weight, contact, footing (0.3)"),
            new MartialModelFile(WushuLora, "loras/H3", Wushu + WushuLora, "wushu_action v7 fl2va — kung-fu exchanges, trigger wushu_action (0.4)"),
            new MartialModelFile(WushuR2vLora, "loras/H3", Wushu + WushuR2vLora, "wushu_action v7 for ref2va models — swap in on a ref2va model"),
            new MartialModelFile("wushu_h3_v8_2000step.safetensors", "loras/H3", Wushu + "wushu_h3_v8_2000step.safetensors", "wushu_action v8 (newer, 2000 steps)"),
            new MartialModelFile(CameraLora, "loras/H3", Camera + CameraLora, "camera motion — trigger 'camera motion' (0.3)"),
            new MartialModelFile("minimax_h3_fl2va_pruned_int8_convrot.safetensors", "diffusion_models/h3-minimax",
                Comfy + "diffusion_models/minimax_h3_fl2va_pruned_int8_convrot.safetensors", "H3 fl2va base, pruned int8 (21 GB)"),
            new MartialModelFile("minimax_h3_fl2va_int8_convrot.safetensors", "diffusion_models/h3-minimax",
                Comfy + "diffusion_models/minimax_h3_fl2va_int8_convrot.safetensors", "H3 fl2va base, full int8 — what the LoRAs were trained on (34 GB)"),
            new MartialModelFile("minimax_h3_latent_upscaler_3d_bf16.safetensors", "latent_upscale_models",
                "https://huggingface.co/Comfy-Org/MiniMax-H3", "3D latent upscaler for the refine"),
        };

        public static MartialModelFile? Find(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var file = Path.GetFileName(name.Replace('\\', '/'));
            return Files.FirstOrDefault(f => f.FileName.Equals(file, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The authored stack, in order, with its strengths. The film-look LoRA (bypassed in the
        /// workflow, unpublished) is not in it.</summary>
        public static IReadOnlyList<(string Name, double Strength)> AuthoredStack { get; } = new[]
        {
            ($"H3/{TurboLora}", 0.8),
            ($"H3/{PhysicsLora}", 0.3),
            ($"H3/{WushuLora}", 0.4),
            ($"H3/{CameraLora}", 0.3),
        };
    }
}
