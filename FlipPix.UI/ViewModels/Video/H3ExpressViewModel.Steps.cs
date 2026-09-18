using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Input;
using FlipPix.Core.Models;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// ⚡ H3 Express's sampling-steps dial — the first pass's step count, on a slider, remembered
    /// <b>per checkpoint</b>.
    ///
    /// <para><b>Why per checkpoint and not per tab.</b> The number belongs to the weights, not to the tab:
    /// the Singularity ref2va build is what the author measured at ten steps, the 10Eros hybrid at twelve,
    /// and the TaoMate relay splits a ten-step schedule across two samplers. One slider shared by all three
    /// would mean every stack change silently re-tuned the other two. So a count is stored against the model
    /// it was set for — <see cref="ComfyUISettings.H3ExpressStepsByModel"/> — and moving the Model dropdown
    /// (or picking a stack, which moves it) brings that model's own count back.</para>
    ///
    /// <para><b>What "not set" means.</b> A checkpoint with nothing stored for it is not in the dictionary at
    /// all and renders at <see cref="AuthoredFirstPassSteps"/> — whatever its stack is authored at. That is
    /// what the slider shows, and what ↺ puts back. The dial is an override you opt into: an install where
    /// nobody has touched it renders exactly as it did before this existed.</para>
    ///
    /// <para><b>Frozen mid-run.</b> Locked by <see cref="H3BatchViewModel.CanChangeWorkflow"/>, like the
    /// stack above it: <see cref="FirstPassSteps"/> is read live as each clip's graph is built, so a folder
    /// whose step count moved halfway through is a folder of films that do not match each other.</para>
    /// </summary>
    public partial class H3ExpressViewModel
    {
        /// <summary>The fewest steps the slider offers. Below about four these checkpoints do not resolve a
        /// frame at all, so there is nothing under it worth reaching for.</summary>
        public const int MinSteps = 4;

        /// <summary>The most it offers. These are turbo and 3-step-LoRA builds sampled at cfg 1, where past
        /// the twenties a clip costs proportionally more for nothing visible.</summary>
        public const int MaxSteps = 30;

        /// <summary>🍥 TaoMate's floor. Its first leg runs <c>steps_to_run: 6</c> of the schedule and the
        /// second resamples the rest, so a total of six or fewer leaves the TaoMate LoRA nothing to do —
        /// the one setting on that stack that would quietly not be the render it says it is.</summary>
        private const int TaoMateMinSteps = 7;

        /// <summary>The count stored for <see cref="_stepsModel"/>, or null when that checkpoint has none
        /// and its stack's authored count stands.</summary>
        private int? _stepsOverride;

        /// <summary>Which model <see cref="_stepsOverride"/> was read for, spelled as <see cref="StepsKey"/>
        /// spells it. Kept so the dropdown moving is noticed once rather than on every read.</summary>
        private string _stepsModel = string.Empty;

        private void InitSteps()
        {
            _stepsModel = StepsKey(SelectedDiffusionModel);
            _stepsOverride = RecallSteps(_stepsModel);

            ResetStepsCommand = new RelayCommand(ResetSteps, () => HasStepsOverride && CanChangeWorkflow);

            if (_stepsOverride.HasValue)
                AddLog($"  Steps: {FirstPassStepCount} on {LabelFor(SelectedDiffusionModel)} — the setting " +
                       $"saved for that checkpoint (authored: {AuthoredFirstPassSteps}).");
        }

        // ── The dial ────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Steps on the first pass, as the slider reads and writes it: this checkpoint's stored count, or its
        /// stack's authored one while it has none. Setting it stores it against the current model and saves
        /// at once, so it is still there after a restart; ↺ is the way back out.
        /// </summary>
        public int FirstPassStepCount
        {
            get => ClampSteps(_stepsOverride ?? AuthoredFirstPassSteps);
            set
            {
                var clamped = ClampSteps(value);

                // Landing on the authored count clears the entry rather than writing one: "nothing stored"
                // already means exactly that, and a stored copy of it would outlive the graph it came from.
                // It also makes the one write a Slider can make on its own — coercing a value against bounds
                // it has not been given yet — a no-op instead of an override nobody asked for.
                if (clamped == AuthoredFirstPassSteps)
                {
                    if (_stepsOverride != null) ResetSteps();
                    return;
                }

                if (_stepsOverride == clamped) return;
                _stepsOverride = clamped;
                StoreSteps(_stepsModel, clamped);
                RaiseStepsState();

                AddLog($"Steps: {clamped} on {LabelFor(SelectedDiffusionModel)} " +
                       $"(authored: {AuthoredFirstPassSteps}) — every clip rendered from now on, and " +
                       "remembered for that checkpoint alone.");
            }
        }

        /// <summary>The slider's floor: seven on 🍥 TaoMate, which spends six of the schedule before the
        /// LoRA leg starts, and <see cref="MinSteps"/> everywhere else.</summary>
        public int MinStepsForStack => UseTaoMate ? TaoMateMinSteps : MinSteps;

        /// <summary>Whether this checkpoint is rendering at a count chosen here rather than the one its stack
        /// ships with. What ↺ is enabled by.</summary>
        public bool HasStepsOverride =>
            _stepsOverride.HasValue && _stepsOverride.Value != AuthoredFirstPassSteps;

        /// <summary>↺ — forget the stored count for this checkpoint and go back to its authored one. Also
        /// what the slider does when it is dragged back onto the authored count.</summary>
        public RelayCommand ResetStepsCommand { get; private set; } = new(() => { });

        private void ResetSteps()
        {
            if (_stepsOverride == null) return;
            _stepsOverride = null;
            ForgetSteps(_stepsModel);
            RaiseStepsState();
            AddLog($"Steps: back to {FirstPassStepCount} on {LabelFor(SelectedDiffusionModel)} — the count its " +
                   "stack is authored at.");
        }

        /// <summary>The line under the slider: what the count is, where it came from, and what moving it
        /// costs.</summary>
        public string StepsSummary
        {
            get
            {
                var authored = AuthoredFirstPassSteps;
                var steps = FirstPassStepCount;
                var stack = UseTaoMate
                    ? $"the relay's whole schedule — {TaoMateMinSteps - 1} steps on the base weights, the rest " +
                      "resampled on the TaoMate LoRA"
                    : UseSingularity
                        ? SingularityErSde
                            ? "er_sde/beta over the Singularity graph"
                            : "euler/simple, as the Singularity graph is authored"
                        : "er_sde/beta on the 10Eros hybrid";

                var where = steps == authored
                    ? $"the authored count for this stack ({stack})."
                    : $"your count for this checkpoint — authored is {authored} ({stack}). " +
                      (steps > authored
                          ? "More steps cost proportionally more per clip; on a turbo build they mostly firm up motion."
                          : "Fewer is quicker and tends to come out softer.");

                return $"{steps} steps — {where} Saved against {LabelFor(SelectedDiffusionModel)} alone, so each " +
                       "checkpoint keeps its own.";
            }
        }

        // ── Plumbing ────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// What the render samples at: the stored count for the loaded checkpoint, else the authored one.
        /// Read live while a clip's graph is built, which is why the slider is frozen mid-run.
        /// </summary>
        protected override int FirstPassSteps => ClampSteps(_stepsOverride ?? AuthoredFirstPassSteps);

        private int ClampSteps(int steps) => Math.Clamp(steps, MinStepsForStack, MaxSteps);

        /// <summary>One spelling for a model name, so a count written on one run is found by the next
        /// whatever the server's casing or slashes were.</summary>
        private static string StepsKey(string? model) =>
            (model ?? string.Empty).Trim().Replace('\\', '/').ToLowerInvariant();

        private int? RecallSteps(string key)
        {
            if (key.Length == 0) return null;
            var map = _settingsService.Settings?.H3ExpressStepsByModel;
            return map != null && map.TryGetValue(key, out var steps) && steps > 0 ? ClampSteps(steps) : null;
        }

        private void StoreSteps(string key, int steps)
        {
            var settings = _settingsService.Settings;
            if (settings == null || key.Length == 0) return;

            var map = settings.H3ExpressStepsByModel;
            if (map == null)
            {
                map = new Dictionary<string, int>();
                settings.H3ExpressStepsByModel = map;
            }
            map[key] = steps;
            _settingsService.SaveSettings(settings);
        }

        private void ForgetSteps(string key)
        {
            var settings = _settingsService.Settings;
            var map = settings?.H3ExpressStepsByModel;
            if (settings == null || map == null || key.Length == 0) return;
            if (map.Remove(key)) _settingsService.SaveSettings(settings);
        }

        /// <summary>
        /// Called when the model dropdown moves — including when a stack change moves it, and when the
        /// server's list is rebuilt under it — to bring that checkpoint's own count back.
        /// </summary>
        private void OnStepsModelChanged()
        {
            var key = StepsKey(SelectedDiffusionModel);
            if (key == _stepsModel)
            {
                // The same checkpoint, but the stack around it may have changed what "authored" means.
                RaiseStepsState();
                return;
            }

            _stepsModel = key;
            var had = _stepsOverride;
            _stepsOverride = RecallSteps(key);
            RaiseStepsState();

            if (_stepsOverride.HasValue)
                AddLog($"Steps: {FirstPassStepCount} — the count saved for {LabelFor(SelectedDiffusionModel)} " +
                       $"(authored: {AuthoredFirstPassSteps}).");
            else if (had.HasValue)
                AddLog($"Steps: {FirstPassStepCount} — {LabelFor(SelectedDiffusionModel)} has no count of its " +
                       "own, so its stack's authored one stands.");
        }

        private void RaiseStepsState()
        {
            OnPropertyChanged(nameof(FirstPassStepCount));
            OnPropertyChanged(nameof(MinStepsForStack));
            OnPropertyChanged(nameof(HasStepsOverride));
            OnPropertyChanged(nameof(StepsSummary));
            OnPropertyChanged(nameof(StackSummary));
            OnPropertyChanged(nameof(HuntSummary));
            ResetStepsCommand.NotifyCanExecuteChanged();
        }
    }
}
