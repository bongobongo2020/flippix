using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// Story mode: turns each generated cast photo into an H3 <b>character sheet</b> (front, three-quarter, side,
    /// back, face close-up) rendered by H3 itself as a single-frame still via the Fizgig H3 Still nodes. The sheet
    /// becomes the slot's first image — the one uploaded as that character's <c>&lt;Picture N&gt;</c> — so every
    /// clip is conditioned on the character from all sides instead of one front-on pose. The photo stays in the
    /// slot behind it.
    /// </summary>
    public partial class H3VideoEditorViewModel
    {
        /// <summary>Build a character sheet from every auto-generated cast photo.</summary>
        [ObservableProperty]
        private bool _generateCharacterSheets = true;

        partial void OnGenerateCharacterSheetsChanged(bool value) => SaveSettings();

        /// <summary>Slots whose first image is a sheet this run made, so a re-populate does not sheet a sheet.</summary>
        private readonly HashSet<int> _sheetSlots = new();

        private const string SheetToken = "h3_story_sheet_";

        private async Task PrepareCharacterSheetsAsync(CancellationToken ct)
        {
            if (!GenerateCharacterSheets) return;

            var targets = Enumerable.Range(0, Math.Min(4, _detectedCast.Count))
                .Where(i => _autoCastSlots.Contains(i) && !_sheetSlots.Contains(i) && ReferenceSlots[i].HasImages)
                .Where(i => !(ReferenceSlots[i].Images[0].FileName ?? string.Empty).Contains(SheetToken, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (targets.Count == 0) return;

            var http = _comfyUIService.HttpClient;
            if (await http.NodeClassExistsAsync(H3StillSheetGraph.NodeLatent, ct) == false)
            {
                AddLog("⚠ Character sheets skipped: the ComfyUI server has no FizgigH3StillLatent node. Install " +
                       "ComfyUI-Fizgig-H3-Still (github.com/shootthesound/ComfyUI-Fizgig-H3-Still) and restart ComfyUI.");
                return;
            }

            var models = H3StillSheetGraph.Resolve(
                await http.GetModelFilenamesAsync("UNETLoader", "unet_name", ct),
                await http.GetModelFilenamesAsync("LoraLoaderModelOnly", "lora_name", ct),
                await http.GetModelFilenamesAsync("CLIPLoader", "clip_name", ct),
                await http.GetModelFilenamesAsync("VAELoader", "vae_name", ct));
            AddLog($"🧾 Character sheets: {targets.Count} via H3 Still ({Path.GetFileName(models.Model)}" +
                   (models.Lora != null ? $" + {Path.GetFileName(models.Lora)} @ {models.LoraStrength:0.##}" : ", no turbo") + ")");

            foreach (var i in targets)
            {
                if (ct.IsCancellationRequested || _storyBatchCancelled) return;
                var photo = ReferenceSlots[i].Images[0].Path;
                if (string.IsNullOrEmpty(photo) || !File.Exists(photo)) continue;

                try
                {
                    StoryStatus = $"Character sheet {i + 1}/{targets.Count}...";
                    var sheet = await GenerateCharacterSheetAsync(i, photo, models, ct);
                    if (string.IsNullOrEmpty(sheet) || !File.Exists(sheet))
                    {
                        AddLog($"⚠ Character sheet for Picture {i + 1} produced no image — the clips use the photo.");
                        continue;
                    }
                    ReferenceSlots[i].InsertFirst(sheet);
                    _sheetSlots.Add(i);
                    AddLog($"Character sheet for Picture {i + 1}: {Path.GetFileName(sheet)}");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AddLog($"⚠ Character sheet for Picture {i + 1} failed ({ex.Message}) — the clips use the photo.");
                }
            }
            SaveSettings();
        }

        private async Task<string?> GenerateCharacterSheetAsync(int slotIndex, string photoPath,
                                                               H3StillSheetGraph.Models models, CancellationToken ct)
        {
            var runToken = $"{SheetToken}{slotIndex + 1}_{DateTime.Now:yyyyMMddHHmmss}";
            var uploaded = await _comfyUIService.UploadImageAsync(photoPath, ct);

            var (kind, _) = _detectedCast[slotIndex];
            var prompt = H3StillSheetGraph.BuildPrompt(SheetNoun(kind), StoryLookFor(slotIndex), StoryOutfitFor(slotIndex));
            var graph = H3StillSheetGraph.Build(models, uploaded, prompt,
                Random.Shared.NextInt64(0, 999_999_999_999_999L), $"h3_story/{runToken}");

            var progress = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(msg =>
            {
                if (msg.Data?.Value != null && msg.Data?.Max != null && msg.Data.Max > 0)
                    StoryStatus = $"Character sheet {slotIndex + 1}: {(double)msg.Data.Value / msg.Data.Max * 100:F0}%";
            });

            var promptId = await _comfyUIService.ExecuteWorkflowAsync(graph, progress, ct);
            var byNode = await _comfyUIService.HttpClient.GetOutputsByNodeAsync(promptId, ct);
            if (byNode.TryGetValue(H3StillSheetGraph.SaveNode, out var outs) && outs.Count > 0)
            {
                var local = await ResolveImageToLocalAsync(outs[0]);
                if (local != null) return local;

                // Remote server with no shared output folder: pull the file into the project folder.
                var rel = outs[0].Replace('\\', '/');
                var cut = rel.LastIndexOf('/');
                var bytes = await _comfyUIService.HttpClient.DownloadOutputImageAsync(
                    cut >= 0 ? rel[(cut + 1)..] : rel, cut >= 0 ? rel[..cut] : string.Empty, ct);
                if (bytes is { Length: > 0 })
                {
                    var dir = Path.GetDirectoryName(photoPath) ?? Path.GetTempPath();
                    var dest = Path.Combine(dir, Path.GetFileName(rel));
                    await File.WriteAllBytesAsync(dest, bytes, ct);
                    return dest;
                }
            }
            return FindTokenImageOnDisk(runToken);
        }

        private static string SheetNoun(string kind)
        {
            var k = kind ?? string.Empty;
            if (k.Contains("woman", StringComparison.OrdinalIgnoreCase) || k.Contains("female", StringComparison.OrdinalIgnoreCase) ||
                k.Contains("girl", StringComparison.OrdinalIgnoreCase)) return "a woman";
            if (k.Contains("man", StringComparison.OrdinalIgnoreCase) || k.Contains("male", StringComparison.OrdinalIgnoreCase) ||
                k.Contains("boy", StringComparison.OrdinalIgnoreCase)) return "a man";
            return k.Length > 0 ? $"a {k}" : string.Empty;
        }
    }
}
