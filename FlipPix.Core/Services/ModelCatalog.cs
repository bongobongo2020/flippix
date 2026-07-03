using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FlipPix.Core.Services;

/// <summary>
/// Maps a model filename to a known download URL so the missing-model resolver can fetch it
/// automatically. Seeded from the shipped FlipPix model manifests (scripts/flippix-models*.txt)
/// plus the text/diffusion encoders the default image workflows use. Any extra
/// "flippix-models*.txt" found next to the app (path | size | url, same format) is merged in at
/// runtime, so the catalog can be extended without a rebuild. Models not in the catalog can still
/// be installed via "Locate folder".
/// </summary>
public static class ModelCatalog
{
    // leaf filename (case-insensitive) -> download URL
    private static readonly Dictionary<string, string> _urls =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // --- Z-Image Turbo (default image workflow) ---
        ["z_image_turbo_bf16.safetensors"] = "https://huggingface.co/Comfy-Org/ZhipuAI_Z-Image-Turbo_models/resolve/main/z_image_turbo_bf16.safetensors",
        ["qwen_3_4b.safetensors"] = "https://huggingface.co/Comfy-Org/ZhipuAI_Z-Image-Turbo_models/resolve/main/qwen_3_4b.safetensors",
        ["ae.safetensors"] = "https://huggingface.co/Comfy-Org/ZhipuAI_Z-Image-Turbo_models/resolve/main/ae.safetensors",
        // Gemma text encoder used by the Z-Image 4K and Flux2-Klein image workflows.
        ["gemma_2_2b_it_elm_bf16.safetensors"] = "https://huggingface.co/Comfy-Org/PixelDiT/resolve/main/text_encoders/gemma_2_2b_it_elm_bf16.safetensors",

        // --- Qwen Image / Edit ---
        ["qwen_2.5_vl_7b_fp8_scaled.safetensors"] = "https://huggingface.co/Comfy-Org/Qwen-Image_ComfyUI/resolve/main/split_files/text_encoders/qwen_2.5_vl_7b_fp8_scaled.safetensors",
        ["qwen_image_vae.safetensors"] = "https://huggingface.co/QuantStack/Qwen-Image-GGUF/resolve/main/VAE/Qwen_Image-VAE.safetensors",
        ["Qwen-Image-Edit-2509_fp8_e4m3fn.safetensors"] = "https://huggingface.co/Kijai/Qwen-Edit-2509_safetensors/resolve/main/Qwen-Image-Edit-2509_fp8_e4m3fn.safetensors",
        ["Qwen-Image-Lightning-8steps-V2.0.safetensors"] = "https://huggingface.co/lightx2v/Qwen-Image-Lightning/resolve/main/Qwen-Image-Lightning-8steps-V2.0.safetensors",
        ["mult-angles.safetensors"] = "https://huggingface.co/dx8152/Qwen-Edit-2509-Multiple-angles/resolve/main/%E9%95%9C%E5%A4%B4%E8%BD%AC%E6%8D%A2.safetensors",

        // --- WAN 2.x video (full install) ---
        ["umt5_xxl_fp8_e4m3fn_scaled.safetensors"] = "https://huggingface.co/Kijai/WanVideoGenerator/resolve/main/text_encoders/umt5_xxl_fp8_e4m3fn_scaled.safetensors",
        ["wan_2.1_vae.safetensors"] = "https://huggingface.co/Kijai/WanVideoGenerator/resolve/main/vae/wan_2.1_vae.safetensors",
        ["wan2.2_i2v_A14b_high_noise_scaled_fp8_e4m3_lightx2v_4step_comfyui_1030.safetensors"] = "https://huggingface.co/Kijai/WanVideoGenerator/resolve/main/diffusion_models/wan2.2_i2v_A14b_high_noise_scaled_fp8_e4m3_lightx2v_4step_comfyui_1030.safetensors",
        ["wan2.2_i2v_A14b_low_noise_scaled_fp8_e4m3_lightx2v_4step_comfyui.safetensors"] = "https://huggingface.co/Kijai/WanVideoGenerator/resolve/main/diffusion_models/wan2.2_i2v_A14b_low_noise_scaled_fp8_e4m3_lightx2v_4step_comfyui.safetensors",

        // --- LTX 2.3 (16gb video tier) ---
        ["LTX-2.3-22B-distilled-1.1-Q3_K_S.gguf"] = "https://huggingface.co/QuantStack/LTX-2.3-GGUF/resolve/main/LTX-2.3-distilled-1.1/LTX-2.3-22B-distilled-1.1-Q3_K_S.gguf",
    };

    private static bool _mergedFromDisk;
    private static readonly object _lock = new();

    /// <summary>
    /// Returns the download URL for <paramref name="modelNameOrPath"/> (filename or full relative
    /// path — only the leaf filename is matched), or null if the model isn't in the catalog.
    /// </summary>
    public static string? GetUrl(string modelNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(modelNameOrPath)) return null;
        EnsureMergedFromDisk();
        var leaf = Path.GetFileName(modelNameOrPath.Replace('\\', '/'));
        return _urls.TryGetValue(leaf, out var url) ? url : null;
    }

    /// <summary>True if a download URL is known for this model.</summary>
    public static bool HasUrl(string modelNameOrPath) => GetUrl(modelNameOrPath) != null;

    // Best-effort one-time merge of any flippix-models*.txt sitting next to the executable (or in a
    // "scripts" subfolder). Lets a deployment ship/extend the manifest without code changes.
    private static void EnsureMergedFromDisk()
    {
        if (_mergedFromDisk) return;
        lock (_lock)
        {
            if (_mergedFromDisk) return;
            _mergedFromDisk = true;
            try
            {
                var baseDir = AppContext.BaseDirectory;
                var dirs = new[] { baseDir, Path.Combine(baseDir, "scripts") };
                foreach (var dir in dirs)
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var file in Directory.EnumerateFiles(dir, "flippix-models*.txt"))
                        MergeManifest(file);
                }
            }
            catch
            {
                // Catalog is best-effort; ignore disk/parse problems.
            }
        }
    }

    private static void MergeManifest(string path)
    {
        try
        {
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var parts = line.Split('|');
                if (parts.Length < 3) continue;
                var relPath = parts[0].Trim();
                var url = parts[2].Trim();
                if (relPath.Length == 0 || url.Length == 0) continue;
                var leaf = Path.GetFileName(relPath.Replace('\\', '/'));
                if (leaf.Length == 0) continue;
                // Don't overwrite a curated built-in entry with a manifest one.
                if (!_urls.ContainsKey(leaf)) _urls[leaf] = url;
            }
        }
        catch
        {
            // Ignore a single bad manifest file.
        }
    }
}
