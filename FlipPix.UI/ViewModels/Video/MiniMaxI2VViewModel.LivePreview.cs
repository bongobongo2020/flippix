namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// The live sampler preview — what ComfyUI shows on a graph with KJNodes' <c>ModelPreviewOverrideKJ</c>:
    /// the clip coming out of the noise, a little clearer at every step.
    ///
    /// <para>The node sends a <c>kj_preview_override</c> message per sampler step with the whole clip as it
    /// stands, decoded by the tiny VAE: a JPEG of the starting noise first, then an MP4 (608×352 at 12 fps on
    /// the 2-Pass first pass). <see cref="LivePreviewFeed"/> writes each to a rotating temp file and the view
    /// loops the newest and crossfades to the next — see Controls/LivePreviewPlayer. Only shown while this tab
    /// is rendering; the 🌹 2-Pass graph carries the node, the others do not.</para>
    /// </summary>
    public partial class MiniMaxI2VViewModel
    {
        private readonly LivePreviewFeed _livePreview = new("live_preview");

        private string _livePreviewPath = string.Empty;
        private string _livePreviewCaption = string.Empty;

        /// <summary>The newest preview on disk: an .mp4 to loop, or a .jpg still. Empty when there is none.</summary>
        public string LivePreviewPath
        {
            get => _livePreviewPath;
            private set
            {
                if (_livePreviewPath == value) return;
                _livePreviewPath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasLivePreview));
            }
        }

        public bool HasLivePreview => _livePreviewPath.Length > 0;

        /// <summary>"Live preview · pass 1 · step 3/8".</summary>
        public string LivePreviewCaption
        {
            get => _livePreviewCaption;
            private set { if (_livePreviewCaption != value) { _livePreviewCaption = value; OnPropertyChanged(); } }
        }

        private void InitLivePreview() => _comfyUIService.LivePreviewReceived += OnLivePreviewReceived;

        /// <summary>Takes the preview down at the start and end of each item, so a take never opens on the
        /// last one's clip.</summary>
        private void ClearLivePreview() => OnUiThread(() =>
        {
            _livePreview.Reset();
            LivePreviewPath = string.Empty;
            LivePreviewCaption = string.Empty;
        });

        /// <summary>Runs on the socket's thread: decode and write here, hand the path to the UI thread.</summary>
        private void OnLivePreviewReceived(object? sender, string raw)
        {
            if (!IsProcessing) return;
            if (_livePreview.Write(raw) is not { } frame) return;

            var label = frame.Pass == 1 ? "pass 1" : $"pass {frame.Pass} · refining";
            var caption = frame.Step == 0
                ? $"Live preview · {label} · starting noise"
                : $"Live preview · {label} · step {frame.Step}/{frame.Total}";

            OnUiThread(() =>
            {
                if (!IsProcessing) return;
                LivePreviewPath = frame.Path;
                LivePreviewCaption = caption;
            });
        }
    }
}
