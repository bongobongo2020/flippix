using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace FlipPix.UI.Linux.Services
{
    /// <summary>
    /// One reported step-bar inside a run, in the order ComfyUI will reach it.
    ///
    /// <para>ComfyUI reports progress <em>per node</em>: every sampler counts its own steps from 1 to
    /// its own maximum. Fed straight to a progress bar, a four-pass job therefore sweeps the bar to the
    /// end four times over. A stage says what one of those sweeps is worth against the whole run.</para>
    /// </summary>
    /// <param name="Label">Shown while this stage runs, e.g. "Pass 2/4".</param>
    /// <param name="Steps">Steps this stage is expected to report. 0 = adopt whatever it reports.</param>
    /// <param name="UnitsPerStep">
    /// Cost of one of its steps relative to the cheapest step in the run. The detail pass re-samples at
    /// 1.5× linear, so its steps cost roughly 3× a base-pass step and have to weigh that much more.
    /// </param>
    /// <param name="UnitsAfter">
    /// Work that runs after this stage and reports nothing — VAE encode/decode, the latent upscaler,
    /// stitching. Without it the bar would sit dead through every gap between samplers.
    /// </param>
    public sealed record ProgressStage(string Label, int Steps, double UnitsPerStep, double UnitsAfter = 0);

    /// <summary>
    /// Turns ComfyUI's per-node step reports into a single monotonic percentage, a wall clock and an ETA.
    ///
    /// <para>The estimate is one number: <c>T</c>, the run's predicted total seconds. The bar is simply
    /// <c>elapsed / T</c>, so it keeps moving every tick even while the server is busy with work it does
    /// not report. Every step report re-derives <c>T</c> from where the run demonstrably is
    /// (<c>T = elapsed / fractionDone</c>), so a wrong seed estimate corrects itself inside the first
    /// minute instead of lying for the whole run.</para>
    ///
    /// <para>Two guards keep that honest: the bar never goes backwards (an estimate corrected downwards
    /// makes it pause rather than rewind), and clock-driven movement may never run past the end of the
    /// work the current stage is known to be followed by — so an over-optimistic estimate stalls short
    /// of the truth instead of parking at 99% for ten minutes.</para>
    /// </summary>
    public sealed class GenerationProgressTracker : IDisposable
    {
        private readonly Action<double> _setProgress;
        private readonly Action<string> _setStatus;
        private readonly Action<string> _setTimer;

        private readonly object _gate = new();
        private readonly Stopwatch _clock = new();
        private DispatcherTimer? _timer;

        private List<ProgressStage> _stages = new();
        private double _leadUnits;          // warm-up before the first stage reports anything
        private double _totalUnits = 1;
        private double _unitsBefore;        // units credited by stages that have finished
        private int _stageIndex = -1;
        private int _stageValue;
        private int _stageMax;

        private double _from, _to;
        private double _estimateSeconds;
        private double _displayed;          // 0..1, monotonic
        private string _phase = string.Empty;
        private bool _running;
        private bool _sawFirstStep;
        private TimeSpan _lastReportAt;

        /// <summary>Fraction of the mapped span held back until the run actually reports completion.</summary>
        private const double Ceiling = 0.995;

        public GenerationProgressTracker(Action<double> setProgress, Action<string> setStatus, Action<string> setTimer)
        {
            _setProgress = setProgress;
            _setStatus = setStatus;
            _setTimer = setTimer;
        }

        /// <summary>Wall-clock time since <see cref="Begin"/>.</summary>
        public TimeSpan Elapsed => _clock.Elapsed;

        /// <summary>True between <see cref="Begin"/> and <see cref="Finish"/>.</summary>
        public bool IsRunning { get { lock (_gate) { return _running; } } }

        /// <summary>Starts the clock and the 1 Hz repaint.</summary>
        /// <param name="stages">Every step-bar the run is expected to report, in order.</param>
        /// <param name="leadUnits">Work before the first stage — loading the model, encoding references.</param>
        /// <param name="from">Percentage the bar sits at now.</param>
        /// <param name="to">Percentage that means "the run has finished".</param>
        /// <param name="estimatedSeconds">Seed guess for the whole run; corrected from the first step on.</param>
        /// <param name="phase">What is happening before the first step arrives.</param>
        public void Begin(IReadOnlyList<ProgressStage> stages, double leadUnits,
                          double from, double to, double estimatedSeconds, string phase)
        {
            lock (_gate)
            {
                _stages = stages.ToList();
                _leadUnits = Math.Max(0, leadUnits);
                _unitsBefore = 0;
                _stageIndex = -1;
                _stageValue = 0;
                _stageMax = 0;
                _from = from;
                _to = to;
                _displayed = 0;
                _phase = phase;
                _sawFirstStep = false;
                _estimateSeconds = Math.Max(30, estimatedSeconds);
                _totalUnits = Math.Max(1, RecomputeTotal());
                _lastReportAt = TimeSpan.Zero;
                _running = true;
                _clock.Restart();
            }

            OnUi(() =>
            {
                _timer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _timer.Tick -= OnTick;
                _timer.Tick += OnTick;
                _timer.Start();
            });
            Repaint();
        }

        /// <summary>
        /// Feeds one ComfyUI progress message in. Reports without a real step count (a node that only
        /// says "1 of 1") carry no information and are dropped — passing them on would drag the bar back
        /// to the start of the run every time a loader or a decoder ran.
        /// </summary>
        public void Report(int value, int max)
        {
            if (max <= 1) return;

            lock (_gate)
            {
                if (!_running) return;

                // A new stage announces itself either by counting a different total, or by starting
                // over. Both happen at every pass boundary: the base sampler's 8 steps, the detail
                // sampler's 4, then the next pass's 8 again.
                if (_stageIndex < 0 || max != _stageMax || value < _stageValue)
                    AdvanceStage(max);

                _stageValue = Math.Min(value, _stageMax);
                _sawFirstStep = true;
                _phase = string.Empty;
                _lastReportAt = _clock.Elapsed;

                // Re-derive the whole-run estimate from where the run demonstrably is.
                var fraction = UnitsDone() / _totalUnits;
                if (fraction > 0.015 && _clock.Elapsed.TotalSeconds > 10)
                    _estimateSeconds = Math.Max(_clock.Elapsed.TotalSeconds / fraction, _clock.Elapsed.TotalSeconds + 5);
            }

            Repaint();
        }

        /// <summary>
        /// Names work that reports no steps of its own — waiting for the server, fetching the file. Shown
        /// until the next step report replaces it.
        /// </summary>
        public void SetPhase(string phase)
        {
            lock (_gate) { _phase = phase; }
            Repaint();
        }

        /// <summary>Stops the clock. On success the bar is driven to <c>to</c>.</summary>
        public void Finish(bool success)
        {
            TimeSpan elapsed;
            lock (_gate)
            {
                _running = false;
                _clock.Stop();
                elapsed = _clock.Elapsed;
                if (success) _displayed = 1;
            }
            OnUi(() => _timer?.Stop());

            if (success)
            {
                _setProgress(_to);
                _setTimer($"✓ {FormatSpan(elapsed)}");
            }
            else
            {
                _setTimer($"✗ stopped at {FormatSpan(elapsed)}");
            }
        }

        public void Dispose()
        {
            OnUi(() =>
            {
                if (_timer != null) { _timer.Stop(); _timer.Tick -= OnTick; _timer = null; }
            });
        }

        // ── Internals ─────────────────────────────────────────────────────────────────────

        private void OnTick(object? sender, EventArgs e) => Repaint();

        private void AdvanceStage(int max)
        {
            if (_stageIndex < 0)
            {
                // The lead-in is only credited once the first step proves it is over. Crediting it up
                // front would have the bar jump forward the instant the job was submitted.
                _unitsBefore += _leadUnits;
            }
            else if (_stageIndex < _stages.Count)
            {
                var done = _stages[_stageIndex];
                _unitsBefore += done.Steps * done.UnitsPerStep + done.UnitsAfter;
            }

            _stageIndex++;
            if (_stageIndex >= _stages.Count)
            {
                // The graph reported a bar the plan did not predict. Better to widen the run than to
                // pin the bar at the end while work is still going on.
                _stages.Add(new ProgressStage("Working", max, 1.0));
            }
            else if (_stages[_stageIndex].Steps != max)
            {
                // Trust what the server actually counts over what the workflow was read to say.
                _stages[_stageIndex] = _stages[_stageIndex] with { Steps = max };
            }

            _stageMax = max;
            _stageValue = 0;
            _totalUnits = Math.Max(1, RecomputeTotal());
        }

        private double RecomputeTotal() =>
            _leadUnits + _stages.Sum(s => Math.Max(s.Steps, 1) * s.UnitsPerStep + s.UnitsAfter);

        private double UnitsDone()
        {
            var current = _stageIndex >= 0 && _stageIndex < _stages.Count
                ? _stageValue * _stages[_stageIndex].UnitsPerStep
                : 0;
            return Math.Min(_unitsBefore + current, _totalUnits);
        }

        /// <summary>
        /// How far the clock alone may carry the bar: to the end of the current stage plus the unreported
        /// work known to follow it. Past that, the run has to say something before the bar moves again.
        /// </summary>
        private double UnitsCeiling()
        {
            if (_stageIndex < 0 || _stageIndex >= _stages.Count)
            {
                var first = _stages.Count > 0 ? _stages[0].Steps * _stages[0].UnitsPerStep : 0;
                return Math.Min(_leadUnits + first, _totalUnits);
            }

            var stage = _stages[_stageIndex];
            return Math.Min(_unitsBefore + stage.Steps * stage.UnitsPerStep + stage.UnitsAfter, _totalUnits);
        }

        private void Repaint()
        {
            double progress;
            string status, timer;

            lock (_gate)
            {
                var elapsed = _clock.Elapsed.TotalSeconds;
                var byUnits = UnitsDone() / _totalUnits;
                var byTime = _estimateSeconds > 0 ? elapsed / _estimateSeconds : 0;
                var ceiling = UnitsCeiling() / _totalUnits;

                var fraction = Math.Clamp(Math.Max(byUnits, Math.Min(byTime, ceiling)), 0, Ceiling);
                if (fraction > _displayed) _displayed = fraction;
                progress = _from + (_to - _from) * _displayed;

                status = BuildStatus();
                timer = BuildTimer(elapsed);
            }

            _setProgress(progress);
            _setStatus(status);
            _setTimer(timer);
        }

        private string BuildStatus()
        {
            if (!string.IsNullOrEmpty(_phase)) return _phase;
            if (!_sawFirstStep) return "Starting on the server — loading the model...";

            var label = _stageIndex >= 0 && _stageIndex < _stages.Count ? _stages[_stageIndex].Label : "Working";
            var text = _stageMax > 0 ? $"{label} — step {_stageValue}/{_stageMax}" : label;

            // A sampler step on this graph lands every 10-40s; a gap far past that is the unreported
            // work between passes, and saying so beats a status line that merely looks frozen.
            var quiet = _clock.Elapsed - _lastReportAt;
            if (quiet > TimeSpan.FromSeconds(75))
                text += $" · decoding/encoding for {FormatSpan(quiet)}";
            return text;
        }

        private string BuildTimer(double elapsedSeconds)
        {
            var elapsed = FormatSpan(TimeSpan.FromSeconds(elapsedSeconds));
            if (!_running) return elapsed;

            var remaining = _estimateSeconds - elapsedSeconds;
            if (_displayed >= Ceiling || remaining < 5) return $"⏱ {elapsed} · finishing";
            return $"⏱ {elapsed} · ~{FormatSpan(TimeSpan.FromSeconds(remaining))} left";
        }

        private static string FormatSpan(TimeSpan t)
        {
            if (t.TotalSeconds < 0) t = TimeSpan.Zero;
            return t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
                : $"{t.Minutes:D2}:{t.Seconds:D2}";
        }

        private static void OnUi(Action action)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) action();
            else dispatcher.Invoke(action);
        }
    }
}
