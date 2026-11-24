using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using FlipPix.Core.Models;
using FlipPix.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// 🧾 Character sheets: before the first beat, every person in the opening image (up to
    /// <see cref="FflfCast.MaxSheets"/>, found by the vision model's description — the same A, B, C the story engine
    /// writes the beats with) gets a character sheet — front, three-quarter, side, back and face — made from the
    /// opening image by an image-edit model: Qwen Image 2.1 Edit (default), Flux2 Klein 9B or FIBO 2 (<see cref="FflfSheetGraph"/>). The sheets go with
    /// every beat after the start frame, each named in the prompt, so a character who turns away, walks or is half
    /// hidden in one beat comes back as the same person in the next, and two people never trade faces or clothes.
    ///
    /// <para>The sheets are made once per opening image and kept; a new image makes new ones on the next ▶.
    /// Anything that fails (no vision model, a missing model or node, an empty render) is logged and the story renders as
    /// before, without sheets.</para>
    /// </summary>
    public partial class H3FflfViewModel
    {
        /// <summary>🧾 Make a character sheet per person in the opening image and send them with every beat.</summary>
        [ObservableProperty] private bool _characterSheets = true;

        /// <summary>Which image-edit model draws the sheets — an <see cref="FflfSheetGraph"/> engine id.</summary>
        [ObservableProperty] private string _sheetEngine = FflfSheetGraph.EngineQwen21;

        public IReadOnlyList<FflfSheetGraph.EngineOption> SheetEngines => FflfSheetGraph.Engines;

        /// <summary>Off on tabs that bring their own cast pictures (🥋 Action, Martial).</summary>
        protected virtual bool UsesCharacterSheets => true;

        private List<FflfCharacterSheetEntry> _sheets = new();
        private string _sheetsFor = string.Empty;

        /// <summary>This run's sheets on the server, in picture order: who each shows, and the uploaded name.</summary>
        private List<(string Label, string Uploaded)> _runSheets = new();

        private void InitCharacterSheets(H3FflfSettings s)
        {
#pragma warning disable MVVMTK0034
            _characterSheets = s.CharacterSheets;
            _sheetEngine = FflfSheetGraph.Normalize(s.CharacterSheetEngine);
#pragma warning restore MVVMTK0034
            _sheets = (s.CharacterSheetList ?? new()).Where(e => e != null).ToList();
            _sheetsFor = s.CharacterSheetsFor ?? string.Empty;
        }

        private void SaveCharacterSheets(H3FflfSettings s)
        {
            s.CharacterSheets = CharacterSheets;
            s.CharacterSheetEngine = SheetEngine;
            s.CharacterSheetList = _sheets.ToList();
            s.CharacterSheetsFor = _sheetsFor;
        }

        /// <summary>
        /// Runs inside the queue lease, after the opening image is uploaded: makes the sheets this image does not
        /// have yet and uploads them for the run. Never throws except for cancellation.
        /// </summary>
        private async Task PrepareCharacterSheetsAsync(CancellationToken ct)
        {
            _runSheets = new();
            if (!UsesCharacterSheets || !CharacterSheets || !HasInitialImage) return;

            try
            {
                var key = $"{OpeningImageKey}|{SheetEngine}";
                var have = _sheetsFor == key && _sheets.Count > 0 && _sheets.All(e => File.Exists(e.Path));
                if (!have)
                {
                    var made = await MakeCharacterSheetsAsync(key, ct);
                    if (made == null) return;
                    _sheets = made;
                    _sheetsFor = key;
                    OnUi(Save);
                }
                else
                {
                    AddLog($"🧾 Character sheets: reusing {_sheets.Count} for this opening image ({string.Join(", ", _sheets.Select(e => e.Label))}).");
                }

                var uploaded = new List<(string, string)>();
                foreach (var e in _sheets.Take(FflfCast.MaxSheets))
                {
                    ProcessingStatus = $"Sending {e.Letter}'s character sheet to ComfyUI…";
                    var name = await _comfyUIService.UploadImageAsync(e.Path, ct);
                    if (!string.IsNullOrEmpty(name)) uploaded.Add((e.Label, name));
                }
                _runSheets = uploaded;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _runSheets = new();
                AddLog($"🧾 ⚠ Character sheets skipped ({ex.Message}) — the beats render without them.");
                _logger.LogWarning($"{Profile.Name} character sheets failed: {ex}");
            }
        }

        /// <summary>The sheets for the opening image, or null when there is nobody to sheet or they cannot be made.</summary>
        private async Task<List<FflfCharacterSheetEntry>?> MakeCharacterSheetsAsync(string key, CancellationToken ct)
        {
            var llm = _serviceProvider?.GetService<LMStudioService>();
            if (llm == null) { AddLog("🧾 ⚠ Character sheets skipped: the LLM service is not available to look at the opening image."); return null; }

            var description = await DescribeOpeningImageAsync(llm, ct, status => ProcessingStatus = status);
            if (description == null) { AddLog("🧾 ⚠ Character sheets skipped: the opening image could not be described."); return null; }

            var cast = FflfCast.Parse(description);
            if (cast.Count == 0)
            {
                AddLog("🧾 Character sheets: nobody was found in the opening image — none made.");
                return null;
            }
            if (cast.Count > FflfCast.MaxSheets)
                AddLog($"🧾 {cast.Count} people in the opening image — sheets for the first {FflfCast.MaxSheets} only (H3 takes four pictures).");
            cast = cast.Take(FflfCast.MaxSheets).ToList();

            var engine = SheetEngine;
            var engineName = FflfSheetGraph.Engines.First(e => e.Id == engine).Label;
            string? workflow = null;
            if (FflfSheetGraph.WorkflowFile(engine) is { } workflowFile)
            {
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, workflowFile);
                if (!File.Exists(path)) { AddLog($"🧾 ⚠ Character sheets skipped: workflow file not found ({workflowFile})."); return null; }
                workflow = await File.ReadAllTextAsync(path, ct);
            }
            AddLog($"🧾 Character sheets for {string.Join(", ", cast.Select(c => c.Label))} with {engineName}");

            var initial = await UploadInitialAsync(ct);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var sheets = new List<FflfCharacterSheetEntry>();
            foreach (var member in cast)
            {
                ct.ThrowIfCancellationRequested();
                var started = DateTime.Now;
                try
                {
                    ProcessingStatus = $"🧾 Drawing {member.Letter}'s character sheet ({sheets.Count + 1}/{cast.Count})…";
                    OnPropertyChanged(nameof(ReadyHint));
                    var prompt = FflfSheetGraph.Prompt(member, cast.Count);
                    var graph = FflfSheetGraph.Build(engine, workflow, initial, prompt, NewSeed(),
                        $"video/{Profile.Key}/sheets/{stamp}_{member.Letter}");
                    var promptId = await _comfyUIService.ExecuteWorkflowAsync(graph, null, ct, TimeSpan.FromMinutes(15));
                    if (string.IsNullOrEmpty(promptId)) throw new InvalidOperationException("ComfyUI returned no prompt id.");
                    var byNode = await _comfyUIService.HttpClient.GetOutputsByNodeAsync(promptId, ct);
                    var file = byNode.TryGetValue(FflfSheetGraph.SaveNode(engine), out var outs) ? outs.FirstOrDefault() : null;
                    var local = file == null ? null : await FetchOutputFileAsync(file, ct);
                    if (local == null) throw new InvalidOperationException("the sheet was not saved.");

                    var path = Keep(local, $"sheet_{stamp}_{member.Letter}");
                    sheets.Add(new FflfCharacterSheetEntry { Letter = member.Letter, Label = member.Label, Path = path });
                    AddLog($"🧾 {member.Label}: character sheet in {(DateTime.Now - started):m\\:ss} — {Path.GetFileName(path)}");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    AddLog($"🧾 ⚠ {member.Label}: no character sheet ({ex.Message}).");
                }
            }
            return sheets.Count > 0 ? sheets : null;
        }

        partial void OnCharacterSheetsChanged(bool value) => OnPropertyChanged(nameof(CharacterSheetsHint));
        partial void OnSheetEngineChanged(string value) => OnPropertyChanged(nameof(CharacterSheetsHint));

        /// <summary>Under the 🧾 box: what the sheets are for this opening image.</summary>
        public string CharacterSheetsHint =>
            !CharacterSheets ? "Off — the beats are sent the start frame and the opening image only."
            : _sheetsFor == $"{OpeningImageKey}|{SheetEngine}" && _sheets.Count > 0 ? $"Made for this image: {string.Join(", ", _sheets.Select(e => e.Label))}."
            : "Made on the next ▶ Generate, one per person in the opening image (under a minute each).";
    }
}
