using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using FlipPix.UI.Models;
using Application = System.Windows.Application;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// ⚡ H3 Express's <b>job queue</b>: more than one folder, each with its own cast and its own render
    /// settings, lined up to run one after another — and a second job can be added while the first is
    /// still rendering.
    ///
    /// <para><b>The problem it solves.</b> Everything on the rail is the <i>running</i> job. The stack, the
    /// step count and the LoRA are read live as each clip's graph is built, and the cast cards are read at
    /// the top of every story — which is why the whole rail freezes the moment ▶ is pressed. So "queue
    /// another folder with a different workflow" cannot be done by editing the rail: it needs a place to
    /// compose a job where the render is not looking, and a moment to hand that job over where nothing is
    /// half-applied. Those are the ➕ New job sheet (<see cref="ExpressJobViewModel"/>) and
    /// <see cref="AdvanceToNextJobAsync"/>, which runs between two stories and never inside one.</para>
    ///
    /// <para><b>What a job is.</b> An <see cref="ExpressJob"/> — a frozen copy of every dial, plus the story
    /// rows themselves. Pressing ▶ takes the rail down as job ①; each queued job is applied onto the rail
    /// (<see cref="ApplyJob"/>) when its turn comes, so from the render path's point of view nothing has
    /// changed: it is still reading the same live properties it always did, and they say what this job
    /// wants. The queue is therefore additive — with nothing queued, one press of ▶ renders one folder
    /// exactly as it did before this existed.</para>
    ///
    /// <para><b>The queue is not persisted.</b> A job holds photo paths, a folder and a stack; what it does
    /// not hold is the run it was queued during. Restoring half a pipeline into a tab whose rail has since
    /// moved would be a film rendered with settings nobody chose, so a restart starts empty.</para>
    /// </summary>
    public partial class H3ExpressViewModel
    {
        private readonly ObservableCollection<ExpressJob> _jobs = new();

        private void InitJobs()
        {
            NewJobCommand = new RelayCommand(() => _ = ComposeJobAsync(null));
            EditJobCommand = new RelayCommand<ExpressJob>(job => { if (job is { IsQueued: true }) _ = ComposeJobAsync(job); },
                                                          job => job is { IsQueued: true });
            RemoveJobCommand = new RelayCommand<ExpressJob>(RemoveJob, job => job is { IsRunning: false });
            MoveJobUpCommand = new RelayCommand<ExpressJob>(job => MoveJob(job, -1), job => CanMoveJob(job, -1));
            MoveJobDownCommand = new RelayCommand<ExpressJob>(job => MoveJob(job, +1), job => CanMoveJob(job, +1));
            ClearFinishedJobsCommand = new RelayCommand(ClearFinishedJobs, () => _jobs.Any(j => j.IsFinished));
            RequeueJobCommand = new RelayCommand<ExpressJob>(RequeueJob, job => job is { IsFinished: true });
            QueuePageCommand = new RelayCommand(QueuePage, () => CanQueuePage);

            _jobs.CollectionChanged += (_, _) => RenumberJobs();
        }

        // ── What the card binds to ──────────────────────────────────────────────────────────────────

        /// <summary>The pipeline, in the order it will run: the job being rendered first (while one is),
        /// then everything queued behind it, then whatever has finished.</summary>
        public ObservableCollection<ExpressJob> Jobs => _jobs;

        public bool HasJobs => _jobs.Count > 0;

        /// <summary>Whether anything on the list has been through — what the Clear finished button appears for.</summary>
        public bool HasFinishedJobs => _jobs.Any(j => j.IsFinished);

        /// <summary>How many jobs are still to come, not counting the one rendering.</summary>
        public int QueuedJobCount => _jobs.Count(j => j.IsQueued);

        /// <summary>Whether ▶ has a queue to run rather than only the page.</summary>
        public bool HasQueuedJobs => QueuedJobCount > 0;

        public RelayCommand NewJobCommand { get; private set; } = null!;
        public RelayCommand<ExpressJob> EditJobCommand { get; private set; } = null!;
        public RelayCommand<ExpressJob> RemoveJobCommand { get; private set; } = null!;
        public RelayCommand<ExpressJob> MoveJobUpCommand { get; private set; } = null!;
        public RelayCommand<ExpressJob> MoveJobDownCommand { get; private set; } = null!;
        public RelayCommand<ExpressJob> RequeueJobCommand { get; private set; } = null!;
        public RelayCommand ClearFinishedJobsCommand { get; private set; } = null!;
        public RelayCommand QueuePageCommand { get; private set; } = null!;

        /// <summary>
        /// The page's own folder is left out of a queued run, so it needs a way onto the list. Offered only
        /// when there is a queue to be left out of and the page actually has something waiting.
        /// </summary>
        public bool CanQueuePage => QueuedJobCount > 0 && Stories.Any(st => st.IsWaiting);

        /// <summary>Puts the page — its folder, its cast, its settings and its waiting stories — on the end
        /// of the queue, as a job of its own.</summary>
        private void QueuePage()
        {
            if (!CanQueuePage) return;
            var job = CaptureJob();
            job.State = ExpressJobState.Queued;

            var at = _jobs.Count;
            for (var i = 0; i < _jobs.Count; i++)
                if (_jobs[i].IsFinished) { at = i; break; }
            _jobs.Insert(at, job);
            _ = job.LoadPreviewsAsync();

            AddLog($"=== Queue: the page is now job {job.Position} — \"{job.Title}\", {job.Stories.Count} stor" +
                   $"{(job.Stories.Count == 1 ? "y" : "ies")}, {job.StackLabel} at {job.Steps} steps, " +
                   $"{job.CastLine}. ===");
            RaiseJobState();
        }

        /// <summary>What ▶ in the JOBS card says it will do.</summary>
        public string StartQueueText =>
            QueuedJobCount == 1 ? "▶ Start the queue — 1 job"
                                : $"▶ Start the queue — {QueuedJobCount} jobs";

        /// <summary>The line under the ➕ button: what the queue is going to do, in plain words.</summary>
        public string JobQueueSummary
        {
            get
            {
                var queued = QueuedJobCount;
                if (queued == 0)
                    return IsBatchRunning
                        ? "Nothing queued behind this run. ➕ adds a second folder with a cast and a workflow of " +
                          "its own — it starts as soon as this one is done, and changes nothing about it."
                        : "The page above is the job ⚡ Render will run. ➕ queues another one behind it — its own " +
                          "folder, its own cast, its own stack — and they run in order, unattended.";

                var stories = _jobs.Where(j => j.IsQueued).Sum(j => j.Stories.Count(s => s.IsWaiting));
                var plan = $"{queued} job{(queued == 1 ? string.Empty : "s")} waiting · {stories} " +
                           $"stor{(stories == 1 ? "y" : "ies")} between them, run in this order, each bringing " +
                           "its own cast and settings with it when its turn comes.";
                // The page's own folder is not in this list, and a run started now will not touch it.
                return CanQueuePage
                    ? plan + $"  The page's own {Stories.Count(s => s.IsWaiting)} waiting stor" +
                             $"{(Stories.Count(s => s.IsWaiting) == 1 ? "y is" : "ies are")} not part of it — " +
                             "➕ Add this page as a job to put them on the end."
                    : plan;
            }
        }

        /// <summary>
        /// ⚡ Render is also on when the page's own list is empty but a job is queued behind it — the
        /// queued job brings its stories with it, so there is work to do even with nothing on screen.
        /// </summary>
        public override bool CanStartBatch => base.CanStartBatch ||
            (!IsBatchRunning && !IsFeelingLucky && !IsProcessingQueue && !IsBuildingSheets &&
             !IsWritingPrompt && _jobs.Any(j => j.IsQueued && j.Stories.Any(s => s.IsWaiting)));

        /// <summary>"Job ② of ③ · " in front of the story line, and nothing at all when there is no queue —
        /// a single-folder run should not start calling itself job 1 of 1.</summary>
        protected override string BatchStatusPrefix
        {
            get
            {
                if (_jobs.Count < 2) return string.Empty;
                var running = _jobs.FirstOrDefault(j => j.IsRunning);
                return running == null ? string.Empty : $"Job {running.Position} of {_jobs.Count} · ";
            }
        }

        // ── The card's buttons ──────────────────────────────────────────────────────────────────────

        private void RemoveJob(ExpressJob? job)
        {
            if (job == null || job.IsRunning) return;
            _jobs.Remove(job);
            AddLog(job.IsFinished
                ? $"Queue: {job.Title} taken off the list."
                : $"Queue: job {job.Title} removed — {QueuedJobCount} still waiting.");
            RaiseJobState();
        }

        private bool CanMoveJob(ExpressJob? job, int delta)
        {
            if (job is not { IsQueued: true }) return false;
            var i = _jobs.IndexOf(job);
            var to = i + delta;
            // A queued job may move among the other queued ones only: never in front of the one rendering,
            // and never down among the finished rows, which are history rather than plan.
            return to >= 0 && to < _jobs.Count && _jobs[to].IsQueued;
        }

        private void MoveJob(ExpressJob? job, int delta)
        {
            if (!CanMoveJob(job, delta) || job == null) return;
            var i = _jobs.IndexOf(job);
            _jobs.Move(i, i + delta);
            RaiseJobState();
        }

        private void ClearFinishedJobs()
        {
            for (var i = _jobs.Count - 1; i >= 0; i--)
                if (_jobs[i].IsFinished) _jobs.RemoveAt(i);
            RaiseJobState();
        }

        /// <summary>Puts a finished job back on the end of the queue with every story waiting again — the
        /// quick way to re-render a folder that has already been through once.</summary>
        private void RequeueJob(ExpressJob? job)
        {
            if (job is not { IsFinished: true }) return;
            job.ResetStories();
            job.State = ExpressJobState.Queued;
            _jobs.Remove(job);
            _jobs.Add(job);
            AddLog($"Queue: {job.Title} is waiting again — {job.Stories.Count} stor" +
                   $"{(job.Stories.Count == 1 ? "y" : "ies")} back to waiting." +
                   (IsBatchRunning ? string.Empty : "  Press ⚡ Render to start."));
            RaiseJobState();
        }

        // ── Composing one ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Opens the ➕ sheet — on a copy of <paramref name="existing"/> when a queued job is being changed,
        /// and otherwise on a copy of the rail's own settings, since "the same again with a different
        /// folder" is the common case and should be one click away.
        /// </summary>
        private async Task ComposeJobAsync(ExpressJob? existing)
        {
            var draft = (existing ?? CaptureJob()).CloneSettings();
            if (existing != null)
                foreach (var s in existing.Stories) draft.Stories.Add(s);
            else
                draft.Folder = string.Empty;   // a new job is a new folder; the rail's would list it twice

            try
            {
                var vm = new ExpressJobViewModel(
                    draft,
                    isNew: existing == null,
                    DiffusionModelOptions.ToList(),
                    LoraOptions.ToList(),
                    AspectRatioOptions,
                    MegapixelOptions,
                    PreviewMegapixelOptions,
                    UpscaleStepOptions,
                    CastPhotoEngineOptions,
                    VisualStyleOptions,
                    MaxStoryDurationSeconds,
                    StackDefaultsFor,
                    _fileDialogService,
                    AddLog,
                    _settingsService.Settings?.VideoGeneratorImageFolder ?? string.Empty);

                var window = new ExpressJobWindow(vm)
                {
                    Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                };
                if (window.Owner == null) window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

                if (window.ShowDialog() != true || !vm.Confirmed) return;

                if (existing == null)
                {
                    draft.State = ExpressJobState.Queued;
                    // Behind everything still to run, and in front of the rows that have already finished.
                    var at = _jobs.Count;
                    for (var i = 0; i < _jobs.Count; i++)
                        if (_jobs[i].IsFinished) { at = i; break; }
                    _jobs.Insert(at, draft);
                    await draft.LoadPreviewsAsync();
                    AddLog($"=== Queue: job \"{draft.Title}\" added — {draft.Stories.Count} stor" +
                           $"{(draft.Stories.Count == 1 ? "y" : "ies")}, {draft.StackLabel} at {draft.Steps} steps, " +
                           $"{draft.CastLine}. " +
                           (IsBatchRunning
                                ? "It starts when the run in progress is finished."
                                : "Press ⚡ Render to start the queue.") + " ===");
                }
                else
                {
                    existing.TakeSettingsFrom(draft);
                    existing.Stories.Clear();
                    foreach (var s in draft.Stories) existing.Stories.Add(s);
                    await existing.LoadPreviewsAsync();
                    existing.RefreshStoriesLine();
                    AddLog($"Queue: job \"{existing.Title}\" updated — {existing.Stories.Count} stor" +
                           $"{(existing.Stories.Count == 1 ? "y" : "ies")}, {existing.StackLabel} at {existing.Steps} steps.");
                }
                RaiseJobState();
            }
            catch (Exception ex)
            {
                AddLog($"The job sheet could not be opened: {ex.Message}");
            }
        }

        /// <summary>What each stack loads and samples at, for the sheet's stack radio — this checkpoint's own
        /// saved step count when it has one, and its stack's authored count otherwise.</summary>
        private ExpressStackDefaults StackDefaultsFor(ExpressStack stack)
        {
            var model = stack switch
            {
                ExpressStack.TaoMate => TaoMateModel,
                ExpressStack.Bunny => BunnyModel,
                ExpressStack.Singularity => SingularityModel,
                _ => "h3-minimax/10Eros_Max_h3_TURBO-hybrid_beta4_int8_convrot.safetensors"
            };
            var authored = stack switch
            {
                ExpressStack.TaoMate => 10,
                ExpressStack.Bunny => BunnySteps,
                ExpressStack.Singularity when !SingularityErSde => 10,
                _ => 12
            };
            var min = stack == ExpressStack.TaoMate ? 7 : MinSteps;
            return new ExpressStackDefaults(model, RecallSteps(StepsKey(model)) ?? authored, min);
        }

        // ── The rail ⟷ a job ────────────────────────────────────────────────────────────────────────

        /// <summary>Everything the rail currently says, as a job — what ▶ takes down as job ①, and what a
        /// fresh ➕ sheet opens on.</summary>
        private ExpressJob CaptureJob()
        {
            var job = new ExpressJob
            {
                Folder = BatchFolder,
                Cast1Photo = CastMember1.PhotoPath,
                Cast1Sex = CastMember1.Sex,
                Cast1Outfit = CastMember1.Outfit,
                Cast1OutfitSource = CastMember1.OutfitSource,
                Cast2Photo = CastMember2.PhotoPath,
                Cast2Sex = CastMember2.Sex,
                Cast2Outfit = CastMember2.Outfit,
                Cast2OutfitSource = CastMember2.OutfitSource,
                CastOwnClothes = CastOwnClothes,
                CastPhotoEngine = CastPhotoEngine,
                Stack = Stack,
                SingularityErSde = SingularityErSde,
                ChainClips = ChainClips,
                ChainPinFinish = ChainPinFinish,
                DiffusionModel = SelectedDiffusionModel,
                Steps = FirstPassStepCount,
                Lora = SelectedLora,
                LoraStrength = LoraStrength,
                AspectRatio = SelectedAspectRatio,
                Megapixels = Megapixels,
                PreviewMegapixels = PreviewMegapixels,
                UpscaleSteps = UpscaleSteps,
                UseRife = UseRife,
                StoryDurationSeconds = StoryDurationSeconds,
                ClipLengthSeconds = LengthSeconds,
                ResearchPrompts = ResearchPrompts,
                SpecPrompts = SingularitySpecPrompts,
                ReuseSavedPrompts = ReuseSavedPrompts,
                VisualStyle = VisualStyle?.Name ?? string.Empty,
            };
            foreach (var s in Stories) job.Stories.Add(s);
            return job;
        }

        /// <summary>
        /// Writes a job onto the rail — every dial through its own property, so the page, the settings file
        /// and everything computed off them follow exactly as if the values had been typed in.
        ///
        /// <para><b>Order is not arbitrary.</b> The stack moves the model dropdown, and the model dropdown
        /// brings that checkpoint's own step count with it — so the stack goes first, then the model, then
        /// the steps over the top of both. The cast cards go on last, because the run puts them onto the
        /// character slots at the top of each story.</para>
        ///
        /// <para>Called between two stories, never inside one. There is no clip in flight at that moment,
        /// so nothing reads a half-applied job.</para>
        /// </summary>
        private void ApplyJob(ExpressJob job)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                {
                    // The stack, and the checkpoint and steps that belong to it.
                    Stack = job.Stack;
                    SingularityErSde = job.SingularityErSde;

                    // Chaining, before the clips are planned: it changes the length each clip is asked for.
                    ChainClips = job.ChainClips;
                    ChainPinFinish = job.ChainPinFinish;

                    if (job.DiffusionModel.Length > 0)
                    {
                        OfferOption(DiffusionModelOptions, job.DiffusionModel);
                        SelectedDiffusionModel = job.DiffusionModel;
                    }
                    FirstPassStepCount = job.Steps;

                    OfferOption(LoraOptions, job.Lora);
                    SelectedLora = job.Lora;
                    LoraStrength = job.LoraStrength;

                    // The canvas and the lengths.
                    if (AspectRatioOptions.Contains(job.AspectRatio)) SelectedAspectRatio = job.AspectRatio;
                    Megapixels = job.Megapixels;
                    PreviewMegapixels = job.PreviewMegapixels;
                    UpscaleSteps = job.UpscaleSteps;
                    UseRife = job.UseRife;
                    StoryDurationSeconds = job.StoryDurationSeconds;
                    LengthSeconds = job.ClipLengthSeconds;

                    // How the clips are written.
                    ResearchPrompts = job.ResearchPrompts;
                    SingularitySpecPrompts = job.SpecPrompts;
                    ReuseSavedPrompts = job.ReuseSavedPrompts;
                    if (VisualStyleOptions.FirstOrDefault(s => s.Name == job.VisualStyle) is { } style)
                        VisualStyle = style;

                    // The cast.
                    ApplyCastMember(CastMember1, job.Cast1Photo, job.Cast1Sex, job.Cast1Outfit, job.Cast1OutfitSource);
                    ApplyCastMember(CastMember2, job.Cast2Photo, job.Cast2Sex, job.Cast2Outfit, job.Cast2OutfitSource);
                    CastOwnClothes = job.CastOwnClothes;
                    CastPhotoEngine = job.CastPhotoEngine;
                }

                // The folder and the list last, so the page reads as this job only once it is fully on.
                SetBatchFolder(job.Folder);
            });

            ReplaceStories(job.Stories);
        }

        private static void ApplyCastMember(ExpressCastMember m, string photo, string sex, string outfit, string source)
        {
            m.Sex = string.Equals(sex, ExpressCastMember.Female, StringComparison.OrdinalIgnoreCase)
                ? ExpressCastMember.Female
                : ExpressCastMember.Male;
            // The outfit before the photo: setting the photo is what decides whether an outfit belongs to it.
            m.Outfit = outfit ?? string.Empty;
            m.OutfitSource = source ?? string.Empty;
            m.PhotoPath = photo ?? string.Empty;
        }

        /// <summary>Puts a name in a dropdown's list when the server's answer does not have it, so applying a
        /// job never blanks a ComboBox while the render uses a value nothing on screen names.</summary>
        private static void OfferOption(ObservableCollection<DiffusionModelOption> options, string name)
        {
            if (name.Length == 0) return;
            if (options.Any(o => string.Equals(o.Value, name, StringComparison.OrdinalIgnoreCase))) return;
            options.Add(new DiffusionModelOption(name, LabelFor(name)));
        }

        // ── The seams the batch loop calls ──────────────────────────────────────────────────────────

        /// <summary>
        /// ▶ has been accepted. The rail's settings become job ①, in front of anything queued, and the
        /// finished rows of a previous run are dropped — the card should read as this run's plan.
        /// </summary>
        protected override void OnBatchStarting()
        {
            var startedQueue = false;
            ExpressJob? first = null;

            Application.Current.Dispatcher.Invoke(() =>
            {
                for (var i = _jobs.Count - 1; i >= 0; i--)
                    if (_jobs[i].IsFinished) _jobs.RemoveAt(i);

                first = _jobs.FirstOrDefault(j => j.IsQueued);
                if (first != null)
                {
                    // There is a queue, so the queue is the plan: ▶ starts at ① and works down, in the order
                    // the card shows. The page is not quietly inserted in front of it — a job the user did
                    // not queue, jumping the ones they did, is the one thing the card must never do.
                    startedQueue = true;
                    first.State = ExpressJobState.Running;
                }
                else
                {
                    // Nothing queued: the page is the job, exactly as one press of ▶ has always worked.
                    first = CaptureJob();
                    first.State = ExpressJobState.Running;
                    _jobs.Insert(0, first);
                    _ = first.LoadPreviewsAsync();
                }
                RaiseJobState();
            });

            if (!startedQueue || first == null) return;

            AddLog($"=== ⚡ H3 Express: {_jobs.Count} job(s) in the queue — each with its own folder, cast and " +
                   "render settings, run one after another. ===");
            if (Stories.Any(s => s.IsWaiting))
                AddLog($"  The page's own {Stories.Count(s => s.IsWaiting)} waiting stor" +
                       $"{(Stories.Count(s => s.IsWaiting) == 1 ? "y is" : "ies are")} NOT part of this run — the " +
                       "queue is. Press ➕ Add this page as a job to put them on the end of it.");

            AddLog($"=== ⚡ Job {first.Position}: \"{first.Title}\" — {first.Stories.Count} stor" +
                   $"{(first.Stories.Count == 1 ? "y" : "ies")}, {first.StackLabel} at {first.Steps} steps, " +
                   $"{first.Megapixels:0.##} MP, {first.CastLine} ===");

            // Nothing is rendering yet, so the whole page can move at once — the same handover
            // AdvanceToNextJobAsync makes between two stories.
            ApplyJob(first);
            _ = RecognizeStoriesAsync();
        }

        /// <summary>
        /// The current job's stories are all walked. Mark it done, and if another is waiting, put it on the
        /// rail and say so — the loop then runs its stories exactly as it ran these.
        /// </summary>
        protected override async Task<bool> AdvanceToNextJobAsync(CancellationToken token)
        {
            ExpressJob? next = null;
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (_jobs.FirstOrDefault(j => j.IsRunning) is { } done)
                {
                    done.State = ExpressJobState.Done;
                    done.RefreshStoriesLine();
                }
                next = _jobs.FirstOrDefault(j => j.IsQueued);
                if (next != null) next.State = ExpressJobState.Running;
                RaiseJobState();
            });

            if (next == null || token.IsCancellationRequested) return false;

            AddLog($"=== ⚡ Job {next.Position}: \"{next.Title}\" — {next.Stories.Count} stor" +
                   $"{(next.Stories.Count == 1 ? "y" : "ies")}, {next.StackLabel} at {next.Steps} steps, " +
                   $"{next.Megapixels:0.##} MP, {next.CastLine} ===");

            // Between two stories: nothing is in flight, so the whole rail can move at once.
            ApplyJob(next);

            // The new list has not been read yet, so its rows carry no 📚 badge and the button's count is
            // the last job's. Off the UI thread, and not waited on: nothing downstream needs it.
            _ = RecognizeStoriesAsync();

            // One turn of the dispatcher, so the bindings and the settings writes the apply kicked off have
            // landed before the next story reads the cards.
            await Application.Current.Dispatcher.InvokeAsync(() => { },
                System.Windows.Threading.DispatcherPriority.Background);
            return true;
        }

        /// <summary>The run is over. Whatever was rendering stops being "now rendering": finished if it got
        /// through its list, stopped if ✕ ended it, and anything still queued stays queued for the next ▶.</summary>
        protected override void OnBatchFinished(bool stopped)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (_jobs.FirstOrDefault(j => j.IsRunning) is { } running)
                {
                    running.State = stopped ? ExpressJobState.Stopped : ExpressJobState.Done;
                    running.RefreshStoriesLine();
                }
                foreach (var j in _jobs) j.RefreshStoriesLine();
                RaiseJobState();
            });

            if (stopped && QueuedJobCount > 0)
                AddLog($"Queue: {QueuedJobCount} job(s) are still waiting. Press ⚡ Render to carry on with them.");
        }

        // ── Housekeeping ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The queue's own state follows the run and the story list, not only the list of jobs: ▶'s label,
        /// whether the page can still be added, and what the summary says all move when a story is ticked
        /// off. Called from the tab's <c>OnCanExecuteChanged</c>, and null-guarded because the base
        /// constructor reaches that before <see cref="InitJobs"/> has made the commands.
        /// </summary>
        private void RaiseQueueState()
        {
            OnPropertyChanged(nameof(CanQueuePage));
            OnPropertyChanged(nameof(JobQueueSummary));
            OnPropertyChanged(nameof(RunButtonText));
            QueuePageCommand?.NotifyCanExecuteChanged();
        }

        private void RenumberJobs()
        {
            for (var i = 0; i < _jobs.Count; i++) _jobs[i].Position = i + 1;
            OnPropertyChanged(nameof(HasJobs));
            OnPropertyChanged(nameof(HasFinishedJobs));
            OnPropertyChanged(nameof(QueuedJobCount));
            OnPropertyChanged(nameof(HasQueuedJobs));
            OnPropertyChanged(nameof(JobQueueSummary));
            OnPropertyChanged(nameof(StartQueueText));
            OnPropertyChanged(nameof(CanQueuePage));
            OnPropertyChanged(nameof(RunButtonText));
            QueuePageCommand.NotifyCanExecuteChanged();
        }

        private void RaiseJobState()
        {
            RenumberJobs();
            OnPropertyChanged(nameof(CanStartBatch));
            ClearFinishedJobsCommand.NotifyCanExecuteChanged();
            EditJobCommand.NotifyCanExecuteChanged();
            RemoveJobCommand.NotifyCanExecuteChanged();
            MoveJobUpCommand.NotifyCanExecuteChanged();
            MoveJobDownCommand.NotifyCanExecuteChanged();
            RequeueJobCommand.NotifyCanExecuteChanged();
        }
    }
}
