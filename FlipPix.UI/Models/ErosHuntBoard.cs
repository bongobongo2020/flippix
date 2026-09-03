using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FlipPix.UI.Models
{
    /// <summary>
    /// What the 🌹 H3 Eros tab does when a tile on the hunt board is clicked. The board's buttons are on the
    /// board's own objects rather than on the tab, and this is how they reach back into it.
    ///
    /// <para><b>Why not bind to the tab's commands.</b> A <c>DataTemplate</c> has its own XAML namescope, so
    /// <c>{Binding DataContext.SomeCommand, ElementName=H3ErosRoot}</c> from inside one resolves to nothing —
    /// the button renders, the click is swallowed, and there is no error to see. That is exactly what the
    /// first version of this strip did, and why clicking a seed did nothing at all.</para>
    /// </summary>
    public interface IErosBoardHost
    {
        void PickDraft(ErosSeedDraft draft);
        void RerollDraft(ErosSeedDraft draft);
        void DeleteDraft(ErosSeedDraft draft);
        void RerollClip(ErosHuntClip clip);
        void PlayClipResult(ErosHuntClip clip);

        /// <summary>The clip's description box was edited. The host splices the new text back into the
        /// queue item's full prompt and decides what that does to the takes already on the board.</summary>
        void DescriptionEdited(ErosHuntClip clip, string description);

        /// <summary>False while the queue or another re-roll owns the GPU — the board's two re-roll buttons
        /// are the only things here that submit anything.</summary>
        bool CanStartBoardJob { get; }
    }

    /// <summary>
    /// One draft of one clip on the hunt board: a single low-resolution take, sampled on its own noise seed,
    /// that can be played, picked, re-rolled or thrown away.
    ///
    /// <para>Deliberately not <see cref="SeedHuntSample"/>: that is a fixed strip of three shared with the LTX
    /// seed-hunt tabs and has no notion of which clip it belongs to. Here the whole story is on the board at
    /// once, so a draft has to know its own clip to be actionable from a click.</para>
    /// </summary>
    public partial class ErosSeedDraft : ObservableObject
    {
        private readonly IErosBoardHost _host;

        public ErosSeedDraft(ErosHuntClip clip, int slot, IErosBoardHost host)
        {
            Clip = clip;
            Slot = slot;
            _host = host;

            PickCommand = new RelayCommand(() => _host.PickDraft(this),
                                           () => HasVideo && Clip.CanAct && !Clip.IsStale);
            RerollCommand = new RelayCommand(() => _host.RerollDraft(this),
                                             () => Clip.CanAct && _host.CanStartBoardJob);
            DeleteCommand = new RelayCommand(() => _host.DeleteDraft(this), () => HasVideo && Clip.CanAct);
        }

        /// <summary>The clip this draft is a take of.</summary>
        public ErosHuntClip Clip { get; }

        /// <summary>1-based preview slot — which of the graph's three sampler branches produced it.</summary>
        public int Slot { get; }

        public string Label => $"Take {Slot}";

        /// <summary>Watches this take and makes it the one the clip is finished from. One click, both
        /// things: the tile that plays is the tile that is chosen.</summary>
        public RelayCommand PickCommand { get; }

        /// <summary>Hunts this one slot again on a fresh seed, leaving the clip's other takes alone.</summary>
        public RelayCommand RerollCommand { get; }

        /// <summary>Throws this take away — tile and file.</summary>
        public RelayCommand DeleteCommand { get; }

        /// <summary>Local path of the draft video, or null when the slot is unfilled.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasVideo))]
        private string? _videoPath;

        /// <summary>The noise seed this take was sampled on, or -1 when the slot is unfilled. What the finish
        /// pass writes back into the graph — never re-derived from the clip's base seed, because a single slot
        /// can be re-rolled on a seed of its own.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SeedText))]
        private long _seed = -1;

        /// <summary>First frame of the draft. Several simultaneous WPF <c>MediaElement</c>s render as solid
        /// black — with a whole story on the board it would be dozens — so the tiles are still frames and the
        /// one being watched plays in the single shared player.</summary>
        [ObservableProperty]
        private BitmapImage? _thumbnail;

        /// <summary>True when this is the take the clip will be finished from.</summary>
        [ObservableProperty]
        private bool _isPicked;

        /// <summary>True while this one slot is being sampled — either as part of its clip's hunt or on its
        /// own after a re-roll.</summary>
        [ObservableProperty]
        private bool _isRendering;

        /// <summary>Short line on and under the tile: the seed, "rendering…", or why the slot is empty.</summary>
        [ObservableProperty]
        private string _status = string.Empty;

        public bool HasVideo => !string.IsNullOrEmpty(VideoPath) && File.Exists(VideoPath);

        public string SeedText => Seed >= 0 ? $"seed {Seed}" : string.Empty;

        public void Clear()
        {
            VideoPath = null;
            Seed = -1;
            Thumbnail = null;
            IsPicked = false;
            IsRendering = false;
            Status = string.Empty;
        }

        public void RaiseState()
        {
            OnPropertyChanged(nameof(HasVideo));
            PickCommand.NotifyCanExecuteChanged();
            RerollCommand.NotifyCanExecuteChanged();
            DeleteCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// One clip's row on the hunt board: its queue item and the drafts hunted for it.
    ///
    /// <para>The board is the shape of the tab. The hunt sweep runs down the whole queue filling these rows
    /// without ever stopping to ask a question, so a twelve-clip story arrives as thirty-six drafts to choose
    /// between rather than twelve interruptions to sit through.</para>
    /// </summary>
    public partial class ErosHuntClip : ObservableObject
    {
        private readonly IErosBoardHost _host;

        public ErosHuntClip(H3CastQueueItem item, int draftsPerClip, IErosBoardHost host)
        {
            Item = item;
            _host = host;
            Drafts = new ObservableCollection<ErosSeedDraft>(
                Enumerable.Range(1, draftsPerClip).Select(slot => new ErosSeedDraft(this, slot, host)));

            RerollAllCommand = new RelayCommand(() => _host.RerollClip(this),
                                                () => CanAct && _host.CanStartBoardJob);
            PlayResultCommand = new RelayCommand(() => _host.PlayClipResult(this), () => OutputPath != null);
        }

        /// <summary>The queue item this row renders. The board is a view of the queue, not a copy of it —
        /// picks and stage changes are written straight through to the item, which is what gets saved.</summary>
        public H3CastQueueItem Item { get; }

        public ObservableCollection<ErosSeedDraft> Drafts { get; }

        /// <summary>Throws this clip's takes away and hunts three more on a fresh base seed.</summary>
        public RelayCommand RerollAllCommand { get; }

        /// <summary>Plays this clip's finished video in the shared player.</summary>
        public RelayCommand PlayResultCommand { get; }

        /// <summary>"Clip 3 / 12", or "Single clip" for a standalone job.</summary>
        [ObservableProperty]
        private string _title = string.Empty;

        /// <summary>The clip's prompt, trimmed for the row header.</summary>
        [ObservableProperty]
        private string _summary = string.Empty;

        /// <summary>What this row is doing or waiting for, in one line.</summary>
        [ObservableProperty]
        private string _status = string.Empty;

        /// <summary>
        /// The clip's <c>integrated_multimodal_description</c> — the field H3 renders motion from — on its
        /// own and editable, so a beat that came out wrong can be reworded right here and re-rolled.
        ///
        /// <para>Only that field: the reference preamble and the wardrobe lock above it are code-written,
        /// identical in every clip of a chain, and hand-editing them is how a cast stops being wired
        /// correctly. The host writes the edit back into the full prompt.</para>
        /// </summary>
        [ObservableProperty]
        private string _description = string.Empty;

        /// <summary>
        /// True when the description has been edited since these takes were hunted. The takes are still worth
        /// watching, but they are no longer of this prompt — and because the finish re-samples the picked
        /// branch rather than reading a cached latent, finishing one would deliver a video nobody has seen.
        /// So a stale row cannot be picked until it is re-rolled.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanAct))]
        private bool _isStale;

        /// <summary>True while any of this clip's drafts, or its finish, is on the GPU.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanAct))]
        private bool _isBusy;

        /// <summary>Slot of the picked draft, 0 = nothing picked yet.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasPick))]
        private int _pickedSlot;

        /// <summary>Set once the picked take has been upscaled and the clip file exists.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanAct))]
        private bool _isFinished;

        /// <summary>
        /// Whether this row's prompt box is open. Shut by default: a dozen open boxes are taller than the
        /// takes they belong to, and the box is for the clip you have decided to rework, not for all of them.
        /// </summary>
        [ObservableProperty]
        private bool _isDescriptionOpen;

        /// <summary>The finished clip, once there is one.</summary>
        [ObservableProperty]
        private string? _outputPath;

        public bool HasPick => PickedSlot > 0;

        /// <summary>Any draft at all — a row with none is either unhunted or has had them all deleted.</summary>
        public bool HasDrafts => Drafts.Any(d => d.HasVideo);

        /// <summary>Whether the row's buttons do anything: a finished clip is done, a busy one is on the GPU.</summary>
        public bool CanAct => !IsBusy && !IsFinished;

        /// <summary>Mirrors the picked slot onto the drafts so exactly one tile is badged.</summary>
        public void ApplyPick(int slot)
        {
            PickedSlot = slot;
            foreach (var d in Drafts) d.IsPicked = d.Slot == slot;
        }

        public void RaiseState()
        {
            OnPropertyChanged(nameof(HasDrafts));
            OnPropertyChanged(nameof(HasPick));
            OnPropertyChanged(nameof(CanAct));
            RerollAllCommand.NotifyCanExecuteChanged();
            PlayResultCommand.NotifyCanExecuteChanged();
            foreach (var d in Drafts) d.RaiseState();
        }

        partial void OnOutputPathChanged(string? value) => PlayResultCommand.NotifyCanExecuteChanged();

        /// <summary>Every keystroke does not land here — the box commits on losing focus — so this fires once
        /// per edit, which is what the host wants: it clears the pick and stales the takes.</summary>
        partial void OnDescriptionChanged(string value) => _host.DescriptionEdited(this, value);

        partial void OnIsStaleChanged(bool value)
        {
            foreach (var d in Drafts) d.RaiseState();
        }
    }
}
