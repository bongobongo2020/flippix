using System;
using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Linux.Services;
using FlipPix.UI.Linux.ViewModels.Video;
using Microsoft.Extensions.DependencyInjection;

namespace FlipPix.UI.Linux.ViewModels
{
    /// <summary>
    /// Composer ViewModel for the Video Generator window.
    ///
    /// Mirrors the WPF window's tab set exactly: nine tabs, each bound to its own
    /// sub-ViewModel through a Views/*View UserControl. The legacy Avalonia-era tabs
    /// (FFLF, Story Video, VACE, Infinite Talk, WanAnimate, WAN SCAIL) and the
    /// sub-ViewModels only they used were removed; the classes stay in the tree for
    /// one deprecation cycle, but nothing constructs or binds them.
    ///
    /// MainVM is retained because the window's status bar (StatusBarMessage,
    /// ComfyUIServer, ComfyUIPort) is fed from it, exactly as the WPF window's
    /// status bar is.
    /// </summary>
    public partial class VideoGeneratorViewModel : ObservableObject, IDisposable
    {
        private bool _disposed = false;
        private readonly ComfyUIService _comfyUIService;
        private readonly LMStudioService _lmStudioService;
        private readonly IAppLogger _logger;
        private readonly FlipPix.Core.Services.SettingsService _settingsService;
        private readonly IServiceProvider? _serviceProvider;
        private readonly WorkflowQueueCoordinator _workflowCoordinator;
        private readonly IFileDialogService _fileDialogService;

        public event EventHandler? PlayRequested;

        /// <summary>
        /// Core i2v ViewModel. No tab binds it any more (the WPF window has no FFLF/
        /// Story tab either); the window status bar reads its connection + status
        /// properties.
        /// </summary>
        public VideoGeneratorMainViewModel MainVM { get; }

        /// <summary>
        /// Scail 2 - unified char-swap (Klein) → SCAIL II motion-transfer flow on one tab.
        /// </summary>
        public Scail2ViewModel Scail2VM { get; }

        /// <summary>
        /// 10Eros ConvRot - single face-reference image + prompt, generate 4 LTX 2.3 FaceID
        /// seed previews (reroll for more), then re-render the chosen seed(s) at full resolution.
        /// </summary>
        public ErosConvRotViewModel ErosConvRotVM { get; }

        /// <summary>
        /// MiniMax I2V - MiniMax H3 in Ref2VA mode: up to four reference pictures and a draft
        /// idea become the six-field Ref2VA prompt, and one submission renders video with synchronized
        /// audio - optionally continued past H3's ~15s ceiling by up to three further passes that each
        /// pick up out of the tail of the one before.
        /// </summary>
        public MiniMaxI2VViewModel MiniMaxI2VVM { get; }

        /// <summary>
        /// MiniMax FFLF - H3 in FL2VA mode, driven as a keyframe chain: an opening frame plus
        /// up to four stills the take has to pass through, one clip between each pair, rendered as the
        /// base pass plus continuation passes inside a single submission.
        /// </summary>
        public MiniMaxFflfViewModel MiniMaxFflfVM { get; }

        /// <summary>
        /// MiniMax Character - reference-to-video: one or two character images stay on model as
        /// H3 reference frames while a third "scene" image (never uploaded) is analyzed into the multi-shot
        /// prompt they act out, with optional Wan 2.2 / LTX 2.3 refinement passes.
        /// </summary>
        public MiniMaxCharacterViewModel MiniMaxCharacterVM { get; }

        /// <summary>
        /// H3 Cast - the same reference-to-video idea as MiniMaxCharacterVM, but each character
        /// arrives as an ordinary photo and is turned into a three-panel Qwen-Image-Edit-2511 character
        /// sheet (front, back, face close-up) first; the video then runs through the face-refiner graph,
        /// whose second H3 pass re-generates the tracked face crops against those same sheets.
        /// </summary>
        public H3CastViewModel H3CastVM { get; }

        /// <summary>
        /// H3 Chain - MiniMax H3 run as an autoregressive chain: two reference images and a
        /// soundtrack become one continuous take of arbitrary length, rendered as N segments inside a
        /// single ComfyUI submission where each segment continues out of the last frame of the one
        /// before it, and assembled and muxed against the song by the workflow itself.
        /// </summary>
        public H3ChainViewModel H3ChainVM { get; }

        /// <summary>
        /// H3 Cast Hybrid - the H3 Cast pipeline on MiniMax H3's hybrid fl2va+ref2va checkpoint,
        /// which completes supplied keyframes and generates from the character sheets in one pass: stills
        /// pinned to timestamps become hard frame locks, the sheets ride along as identity references that
        /// must never become frames, and the alignment between the two is stated in the prompt text rather
        /// than wired into a first/last-frame node.
        /// </summary>
        public H3CastHybridViewModel H3CastHybridVM { get; }

        /// <summary>
        /// H3 Ensemble - the H3 Cast Hybrid pipeline widened from a two-hander to a cast of up to
        /// five, plus a photograph of the location that is both what the language model reads the setting off
        /// and a reference wired into the generator. The nine reference slots are divided between whoever a
        /// clip actually names, so a five-character story renders as a chain of two- and three-handers.
        /// </summary>
        public H3EnsembleViewModel H3EnsembleVM { get; }

        // Bound to the main TabControl so code can switch tabs programmatically.
        // 0 = Scail 2 tab, matching the WPF window's tab order.
        private int _selectedTabIndex = 0;
        public int SelectedTabIndex
        {
            get => _selectedTabIndex;
            set
            {
                if (_selectedTabIndex != value)
                {
                    _selectedTabIndex = value;
                    OnPropertyChanged();
                }
            }
        }

        public VideoGeneratorViewModel(
            ComfyUIService comfyUIService,
            LMStudioService lmStudioService,
            IAppLogger logger,
            FlipPix.Core.Services.SettingsService settingsService,
            IServiceProvider? serviceProvider = null,
            IFileDialogService? fileDialogService = null)
        {
            _comfyUIService = comfyUIService ?? throw new ArgumentNullException(nameof(comfyUIService));
            _lmStudioService = lmStudioService ?? throw new ArgumentNullException(nameof(lmStudioService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _serviceProvider = serviceProvider;
            _workflowCoordinator = serviceProvider?.GetRequiredService<WorkflowQueueCoordinator>()
                ?? throw new InvalidOperationException("WorkflowQueueCoordinator is required");
            _fileDialogService = fileDialogService ?? serviceProvider?.GetRequiredService<IFileDialogService>()
                ?? throw new InvalidOperationException("IFileDialogService is required");

            // Initialize sub-ViewModels
            MainVM = new VideoGeneratorMainViewModel(
                comfyUIService,
                lmStudioService,
                logger,
                settingsService,
                serviceProvider,
                _workflowCoordinator,
                _fileDialogService);

            Scail2VM = new Scail2ViewModel(
                comfyUIService, lmStudioService, logger, settingsService,
                serviceProvider, _workflowCoordinator, _fileDialogService);

            ErosConvRotVM = new ErosConvRotViewModel(
                comfyUIService, lmStudioService, logger, settingsService,
                serviceProvider, _workflowCoordinator, _fileDialogService);

            MiniMaxI2VVM = new MiniMaxI2VViewModel(
                comfyUIService, lmStudioService, logger, settingsService,
                serviceProvider, _workflowCoordinator, _fileDialogService);

            MiniMaxFflfVM = new MiniMaxFflfViewModel(
                comfyUIService, lmStudioService, logger, settingsService,
                serviceProvider, _workflowCoordinator, _fileDialogService);

            MiniMaxCharacterVM = new MiniMaxCharacterViewModel(
                comfyUIService, lmStudioService, logger, settingsService,
                serviceProvider, _workflowCoordinator, _fileDialogService);

            H3CastVM = new H3CastViewModel(
                comfyUIService, lmStudioService, logger, settingsService,
                serviceProvider, _workflowCoordinator, _fileDialogService);

            H3ChainVM = new H3ChainViewModel(
                comfyUIService, lmStudioService, logger, settingsService,
                serviceProvider, _workflowCoordinator, _fileDialogService);

            H3CastHybridVM = new H3CastHybridViewModel(
                comfyUIService, lmStudioService, logger, settingsService,
                serviceProvider, _workflowCoordinator, _fileDialogService);

            H3EnsembleVM = new H3EnsembleViewModel(
                comfyUIService, lmStudioService, logger, settingsService,
                serviceProvider, _workflowCoordinator, _fileDialogService);

            // Forward PlayRequested events from sub-VMs
            MainVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            Scail2VM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            ErosConvRotVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            MiniMaxI2VVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            MiniMaxFflfVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            MiniMaxCharacterVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            H3CastVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            H3ChainVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            H3CastHybridVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            H3EnsembleVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);

            // The status bar reads MainVM's StatusBarMessage / ComfyUIServer /
            // ComfyUIPort through this VM's pass-through properties, so MainVM's
            // change notifications have to be re-fired here. The tab VMs bind
            // directly through their own DataContext and need no forwarding.
            MainVM.PropertyChanged += ForwardPropertyChanged;

            _logger.LogInfo("VideoGeneratorViewModel initialized with sub-ViewModels");
        }

        private void ForwardPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == null) return;
            OnPropertyChanged(e.PropertyName);
        }

        #region Window chrome (status bar) — fed from MainVM, as the WPF window's is

        public string StatusBarMessage { get => MainVM.StatusBarMessage; set => MainVM.StatusBarMessage = value; }
        public string ComfyUIServer { get => MainVM.ComfyUIServer; set => MainVM.ComfyUIServer = value; }
        public string ComfyUIPort { get => MainVM.ComfyUIPort; set => MainVM.ComfyUIPort = value; }

        #endregion

        #region Public Methods

        /// <summary>
        /// Sets the image path from external sources (e.g., edit camera).
        /// </summary>
        public void SetImagePath(string imagePath)
        {
            MainVM.SetImagePath(imagePath);
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            (MainVM as IDisposable)?.Dispose();
            (Scail2VM as IDisposable)?.Dispose();
            (ErosConvRotVM as IDisposable)?.Dispose();
            (MiniMaxI2VVM as IDisposable)?.Dispose();
            (MiniMaxFflfVM as IDisposable)?.Dispose();
            (MiniMaxCharacterVM as IDisposable)?.Dispose();
            (H3CastVM as IDisposable)?.Dispose();
            (H3ChainVM as IDisposable)?.Dispose();
            (H3CastHybridVM as IDisposable)?.Dispose();
            (H3EnsembleVM as IDisposable)?.Dispose();
        }

        #endregion
    }
}
