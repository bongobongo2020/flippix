using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlipPix.Core.Models;
using MessageBox = System.Windows.MessageBox;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// 📒 Recipes: everything a clip is rendered with — diffusion model, text encoder, the LoRA and its strength,
    /// the extra LoRAs, steps, canvas, beat length, the likeness options and the seed — saved under a name, so a
    /// combination that worked can be put back later and rendered again. The story itself (beats, images) is not
    /// part of a recipe; 📚 Stories keeps that. A tab built on this one adds its own dials
    /// (<see cref="SnapshotTabRecipe"/>). Kept in the tab's settings.
    /// </summary>
    public partial class H3FflfViewModel
    {
        public ObservableCollection<FflfRecipe> Recipes { get; } = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(RecipeSummary), nameof(HasSelectedRecipe))]
        private FflfRecipe? _selectedRecipe;

        /// <summary>The name 💾 Save writes under — the picked recipe's, until it is changed.</summary>
        [ObservableProperty] private string _recipeName = string.Empty;

        /// <summary>Loading a recipe also puts its seed back, so the same story renders the same clip.</summary>
        [ObservableProperty] private bool _recipeRestoresSeed = true;

        public bool HasSelectedRecipe => SelectedRecipe != null;

        public RelayCommand SaveRecipeCommand { get; private set; } = null!;
        public RelayCommand LoadRecipeCommand { get; private set; } = null!;
        public RelayCommand DeleteRecipeCommand { get; private set; } = null!;

        /// <summary>What the picked recipe renders with, in one line.</summary>
        public string RecipeSummary => SelectedRecipe is { } r ? Describe(r) : Recipes.Count == 0
            ? "No recipes yet — set the model, text encoder, LoRAs and steps, type a name and 💾 Save."
            : "Pick a recipe to see what it renders with.";

        private void InitRecipes(H3FflfSettings s)
        {
            SaveRecipeCommand = new RelayCommand(SaveRecipe, () => !IsProcessing && RecipeName.Trim().Length > 0);
            LoadRecipeCommand = new RelayCommand(() => { if (SelectedRecipe != null) ApplyRecipe(SelectedRecipe); },
                                                 () => !IsProcessing && SelectedRecipe != null);
            DeleteRecipeCommand = new RelayCommand(DeleteRecipe, () => SelectedRecipe != null);
            foreach (var r in (s.Recipes ?? new List<FflfRecipe>()).Where(r => r != null && !string.IsNullOrWhiteSpace(r.Name))
                                                                   .OrderByDescending(r => r.Saved))
                Recipes.Add(r);
        }

        partial void OnSelectedRecipeChanged(FflfRecipe? value)
        {
            if (value != null) RecipeName = value.Name;
            RefreshRecipeCommands();
        }

        partial void OnRecipeNameChanged(string value) => SaveRecipeCommand?.NotifyCanExecuteChanged();

        private void RefreshRecipeCommands()
        {
            SaveRecipeCommand?.NotifyCanExecuteChanged();
            LoadRecipeCommand?.NotifyCanExecuteChanged();
            DeleteRecipeCommand?.NotifyCanExecuteChanged();
        }

        /// <summary>A tab built on this one: its own dials, as JSON, and putting them back.</summary>
        protected virtual string SnapshotTabRecipe() => string.Empty;
        protected virtual void ApplyTabRecipe(string json) { }

        // ── Save, load, delete ───────────────────────────────────────────────────────────────────

        private FflfRecipe CaptureRecipe(string name)
        {
            var recipe = CaptureCommonRecipe(name);
            if (OffersBeatWorkflows) CaptureBeatWorkflow(recipe);
            return recipe;
        }

        private FflfRecipe CaptureCommonRecipe(string name) => new()
        {
            Name = name,
            Saved = DateTime.Now,
            DiffusionModel = DiffusionModel,
            TextEncoder = TextEncoder,
            Lora = Lora,
            LoraStrength = LoraStrength,
            ExtraLoras = ExtraLoraRows(),
            FirstPassSteps = FirstPassSteps,
            BeatSeconds = BeatSeconds,
            AspectRatio = SelectedAspectRatio,
            FirstPassMegapixels = FirstPassMegapixels,
            FinalMegapixels = FinalMegapixels,
            PreviewOnly = PreviewOnly,
            LockFirstFrame = LockFirstFrame,
            KeepInitialReference = KeepInitialReference,
            MaxFidelityReferences = MaxFidelityReferences,
            RepairHandoffFace = RepairHandoffFace,
            FaceRepairStrength = FaceRepairStrength,
            FaceRefine = FaceRefine,
            FaceRefineStrength = FaceRefineStrength,
            Seed = BaseSeed,
            TabJson = SnapshotTabRecipe(),
        };

        private void SaveRecipe()
        {
            var name = RecipeName.Trim();
            if (name.Length == 0) return;
            var existing = Recipes.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing != null && MessageBox.Show($"Replace the recipe \"{existing.Name}\" with the settings on the page now?",
                    "Recipes", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            var recipe = CaptureRecipe(name);
            if (existing != null) Recipes.Remove(existing);
            Recipes.Insert(0, recipe);
            SelectedRecipe = recipe;
            Save();
            OnPropertyChanged(nameof(RecipeSummary));
            AddLog($"📒 Recipe \"{name}\" {(existing != null ? "updated" : "saved")}: {Describe(recipe)}");
        }

        private void DeleteRecipe()
        {
            if (SelectedRecipe is not { } recipe) return;
            if (MessageBox.Show($"Delete the recipe \"{recipe.Name}\"?", "Recipes", MessageBoxButton.YesNo, MessageBoxImage.Question)
                != MessageBoxResult.Yes) return;
            Recipes.Remove(recipe);
            SelectedRecipe = null;
            RecipeName = string.Empty;
            Save();
            OnPropertyChanged(nameof(RecipeSummary));
            AddLog($"📒 Recipe \"{recipe.Name}\" deleted.");
        }

        /// <summary>Puts a recipe's settings on the page; the story stays as it is.</summary>
        private void ApplyRecipe(FflfRecipe r)
        {
            if (IsProcessing) return;
            _loading = true;
            try
            {
                DiffusionModel = string.IsNullOrWhiteSpace(r.DiffusionModel) ? DiffusionModel : r.DiffusionModel;
                if (!string.IsNullOrWhiteSpace(r.TextEncoder)) TextEncoder = r.TextEncoder;
                Lora = r.Lora ?? string.Empty;
                OfferModel(DiffusionModel);
                OfferTextEncoder(TextEncoder);
                OfferLora(Lora);
                LoraStrength = Math.Clamp(r.LoraStrength, 0, 2);
                ReadExtraLoras(r.ExtraLoras);
                FirstPassSteps = Math.Clamp(r.FirstPassSteps, 1, 60);
                BeatSeconds = Math.Clamp(r.BeatSeconds, 1, 20);
                if (AspectRatioOptions.Contains(r.AspectRatio)) SelectedAspectRatio = r.AspectRatio;
                if (FirstPassSizes.Any(o => o.Value == r.FirstPassMegapixels)) FirstPassMegapixels = r.FirstPassMegapixels;
                if (FinalSizes.Any(o => o.Value == r.FinalMegapixels)) FinalMegapixels = r.FinalMegapixels;
                PreviewOnly = r.PreviewOnly;
                LockFirstFrame = r.LockFirstFrame;
                KeepInitialReference = r.KeepInitialReference;
                MaxFidelityReferences = r.MaxFidelityReferences;
                RepairHandoffFace = r.RepairHandoffFace;
                FaceRepairStrength = ClampRepairStrength(r.FaceRepairStrength);
                FaceRefine = r.FaceRefine;
                FaceRefineStrength = ClampRefineStrength(r.FaceRefineStrength);
                if (RecipeRestoresSeed && r.Seed > 0) BaseSeed = r.Seed;
                ApplyBeatWorkflow(r);
                if (!string.IsNullOrWhiteSpace(r.TabJson))
                {
                    try { ApplyTabRecipe(r.TabJson); }
                    catch (Exception ex) { AddLog($"⚠ Recipe \"{r.Name}\": the {Profile.Name} tab's own settings could not be read ({ex.Message})."); }
                }
            }
            finally { _loading = false; }

            Save();
            ScheduleStorySave();
            OnPropertyChanged(nameof(BeatSummary));
            OnPropertyChanged(nameof(CanvasSummary));
            OnPropertyChanged(nameof(ExtraLoraSummary));
            RefreshCommands();
            WarnMissingExtraLoras();
            AddLog($"📒 Loaded recipe \"{r.Name}\": {Describe(r)}" +
                   (Beats.Any(b => b.IsDone) ? " — beats already rendered keep their takes; ↻ or Start over to render them with it." : string.Empty));
        }

        private static string Describe(FflfRecipe r) => r.BeatWorkflow == FlipPix.UI.Services.PikaFlexGraph.Key
            ? $"PikaFlex + ReDiverse · {Path.GetFileNameWithoutExtension(r.PikaModel)} · " +
              $"{Path.GetFileNameWithoutExtension(r.PikaLora)} @ {r.PikaLoraStrength:0.##} · ReDiverse {Path.GetFileNameWithoutExtension(r.RediverseLora)} @ {r.RediverseStrength:0.##}" +
              string.Concat((r.ExtraLoras ?? new List<H3FflfLoraRow>()).Where(l => l.On && !string.IsNullOrWhiteSpace(l.Name) && l.Strength > 0)
                  .Select(l => $" + {Path.GetFileNameWithoutExtension(l.Name)} @ {l.Strength:0.##}")) +
              $" · TE {Path.GetFileNameWithoutExtension(r.TextEncoder)} · {r.BeatSeconds:0.#} s beats · {r.PikaMegapixels:0.##} MP" +
              (r.PreviewOnly ? " (draft)" : string.Empty) + (r.Seed > 0 ? $" · seed {r.Seed}" : string.Empty) + $" · saved {r.Saved:yyyy-MM-dd HH:mm}"
            : DescribeErosMax(r);

        private static string DescribeErosMax(FflfRecipe r)
        {
            var loras = new List<string>();
            if (!string.IsNullOrWhiteSpace(r.Lora) && r.LoraStrength > 0)
                loras.Add($"{Path.GetFileNameWithoutExtension(r.Lora)} @ {r.LoraStrength:0.##}");
            loras.AddRange((r.ExtraLoras ?? new List<H3FflfLoraRow>())
                .Where(l => l.On && !string.IsNullOrWhiteSpace(l.Name) && l.Strength > 0)
                .Select(l => $"{Path.GetFileNameWithoutExtension(l.Name)} @ {l.Strength:0.##}"));
            return $"{Path.GetFileNameWithoutExtension(r.DiffusionModel)} · TE {Path.GetFileNameWithoutExtension(r.TextEncoder)} · " +
                   (loras.Count == 0 ? "no LoRA" : string.Join(" + ", loras)) +
                   $" · {r.FirstPassSteps} steps · {r.BeatSeconds:0.#} s beats · {r.FirstPassMegapixels:0.##}→{r.FinalMegapixels:0.##} MP" +
                   (r.PreviewOnly ? " (draft)" : string.Empty) +
                   (r.FaceRefine ? $" · face refine {r.FaceRefineStrength:0.##}" : string.Empty) +
                   (r.Seed > 0 ? $" · seed {r.Seed}" : string.Empty) +
                   $" · saved {r.Saved:yyyy-MM-dd HH:mm}";
        }
    }
}
