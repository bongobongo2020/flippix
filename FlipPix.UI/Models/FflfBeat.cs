using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FlipPix.UI.Models
{
    public enum FflfBeatState { Pending, Rendering, Done, Failed }

    /// <summary>
    /// One story beat on the ⛓ FFLF tab: its text, the take rendered for it, and the last frame of that take —
    /// the frame the next beat starts on.
    /// </summary>
    public partial class FflfBeat : ObservableObject
    {
        public FflfBeat(int number, string title, string text)
        {
            _number = number;
            _title = title;
            _text = text;
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Label))]
        private int _number;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Label))]
        private string _title;

        [ObservableProperty] private string _text;

        /// <summary>The beat's own length, from its <c>[7 s]</c> tag in the beats box (⏱ dynamic beat time);
        /// 0 = the tab's 'Seconds / beat'.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Label))]
        private double _seconds;

        public string Label => (string.IsNullOrEmpty(Title) ? $"Beat {Number}" : $"Beat {Number} · {Title}") +
                               (Seconds > 0 ? $" · {Seconds:0.#} s" : string.Empty);

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StateIcon), nameof(IsDone), nameof(IsRendering))]
        private FflfBeatState _state;

        public bool IsDone => State == FflfBeatState.Done;
        public bool IsRendering => State == FflfBeatState.Rendering;

        public string StateIcon => State switch
        {
            FflfBeatState.Rendering => "⏳",
            FflfBeatState.Done => "✅",
            FflfBeatState.Failed => "⚠",
            _ => "○",
        };

        /// <summary>The seed the take was (or will be) rendered with; 0 until it has one.</summary>
        [ObservableProperty] private long _seed;

        [ObservableProperty] private string _status = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasVideo))]
        private string _videoPath = string.Empty;

        public bool HasVideo => !string.IsNullOrEmpty(VideoPath) && File.Exists(VideoPath);

        /// <summary>A local copy of the take's last frame.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasLastFrame))]
        private string _lastFramePath = string.Empty;

        public bool HasLastFrame => !string.IsNullOrEmpty(LastFramePath) && File.Exists(LastFramePath);

        /// <summary>The take as rendered, kept when ✨ face refine replaces <see cref="VideoPath"/> — a second
        /// refine starts from it again rather than refining a refine. Empty while the take is unrefined.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsFaceRefined))]
        private string _originalVideoPath = string.Empty;

        /// <summary>The take's own last frame, before the refine.</summary>
        public string OriginalLastFramePath { get; set; } = string.Empty;

        public bool IsFaceRefined => !string.IsNullOrEmpty(OriginalVideoPath);

        /// <summary>True while ✨ face refine runs on this beat.</summary>
        [ObservableProperty] private bool _isRefiningFace;

        /// <summary>The same frame as uploaded to ComfyUI's input folder.</summary>
        public string UploadedLastFrame { get; set; } = string.Empty;

        /// <summary>A local copy of the last frame with the opening image's face put back — what the next beat
        /// starts on when face repair is on. Empty until the next beat needs it.</summary>
        public string RepairedFramePath { get; set; } = string.Empty;

        /// <summary>The repaired frame as uploaded to ComfyUI's input folder.</summary>
        public string UploadedRepairedFrame { get; set; } = string.Empty;

        /// <summary>The opening image (as uploaded) and strength the repaired frame was made with — a change to
        /// either makes it stale.</summary>
        public string RepairedFor { get; set; } = string.Empty;

        /// <summary>The text the take was rendered from — an edit after that makes the take stale.</summary>
        public string RenderedText { get; set; } = string.Empty;

        // ── Optional dialogue ────────────────────────────────────────────────────────────────────

        /// <summary>An audio file the character speaks in this beat. When set, the beat's soundtrack is locked to it
        /// (MiniMaxH3NativeAudioLock) and H3 only draws the picture, lips in time with the words.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasDialogue), nameof(DialogueName))]
        private string _dialoguePath = string.Empty;

        public bool HasDialogue => !string.IsNullOrEmpty(DialoguePath) && File.Exists(DialoguePath);

        public string DialogueName => HasDialogue
            ? Path.GetFileName(DialoguePath) + (DialogueSeconds > 0 ? $" · {DialogueSeconds:0.0} s" : string.Empty)
            : "No dialogue";

        /// <summary>The dialogue's length, measured when it is picked; 0 if unknown.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DialogueName))]
        private double _dialogueSeconds;

        /// <summary>The dialogue file as uploaded to ComfyUI's input folder, and the local file it was uploaded from.</summary>
        public string UploadedDialogue { get; set; } = string.Empty;
        public string UploadedDialogueFor { get; set; } = string.Empty;

        /// <summary>The dialogue the take was rendered with — a change after that makes the take stale.</summary>
        public string RenderedDialogue { get; set; } = string.Empty;

        // ── Spoken line, voiced by Yakkinator ────────────────────────────────────────────────────

        /// <summary>The words the character says in this beat; 🗣 Generate turns them into the dialogue audio.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowLineHint))]
        private string _lineText = string.Empty;

        public bool ShowLineHint => string.IsNullOrEmpty(LineText);

        /// <summary>The Yakkinator engine (indextts / echo / indic) and voice the line is spoken with.</summary>
        [ObservableProperty] private string _ttsEngine = string.Empty;
        [ObservableProperty] private string _ttsVoice = string.Empty;

        /// <summary>The voices <see cref="TtsEngine"/> offers; filled by the page.</summary>
        [ObservableProperty] private System.Collections.Generic.IReadOnlyList<string> _ttsVoiceOptions = System.Array.Empty<string>();

        /// <summary>True while Yakkinator is speaking this beat's line.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanPickVoice))]
        private bool _isVoicing;

        public bool CanPickVoice => !IsVoicing;

        /// <summary>The line, engine and voice the current dialogue file was generated from — Send all skips a beat
        /// whose audio still matches.</summary>
        public string VoicedFor { get; set; } = string.Empty;

        private BitmapImage? _thumbnail;
        public BitmapImage? Thumbnail => _thumbnail;

        partial void OnLastFramePathChanged(string value)
        {
            RepairedFramePath = string.Empty;
            UploadedRepairedFrame = string.Empty;
            RepairedFor = string.Empty;
            _thumbnail = null;
            OnPropertyChanged(nameof(Thumbnail));
            FlipPix.UI.Services.AsyncThumbnail.Load(value, 220, 0, bitmap =>
            {
                if (LastFramePath != value) return;
                _thumbnail = bitmap;
                OnPropertyChanged(nameof(Thumbnail));
            });
        }

        /// <summary>Back to not rendered.</summary>
        public void Reset()
        {
            State = FflfBeatState.Pending;
            Status = string.Empty;
            VideoPath = string.Empty;
            LastFramePath = string.Empty;
            UploadedLastFrame = string.Empty;
            OriginalVideoPath = string.Empty;
            OriginalLastFramePath = string.Empty;
            RenderedText = string.Empty;
            RenderedDialogue = string.Empty;
        }
    }
}
