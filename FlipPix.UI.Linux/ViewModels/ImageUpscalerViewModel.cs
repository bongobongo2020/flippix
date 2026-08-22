using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.Core.Services;
using FlipPix.UI.Linux.Models;
using FlipPix.UI.Linux.Services;
using WpfApp = System.Windows.Application;

namespace FlipPix.UI.Linux.ViewModels
{
    /// <summary>
    /// 🔍 Image Upscaler tab: tiled super-resolution through SeedVR2.
    ///
    /// <para>The image is cut into overlapping tiles, each tile is upscaled by the SeedVR2 diffusion
    /// transformer, and the tiles are blended back together — so peak VRAM is set by the tile size,
    /// not by the output resolution. That is what puts a 4K–8K result within reach of a card that
    /// could never hold the whole frame at once.</para>
    ///
    /// <para>Two modes share every setting: <b>Single</b> upscales one uploaded picture, <b>Batch</b>
    /// points at a folder and walks every image in it, mirroring the input tree into the output
    /// folder. Batch runs strictly one image at a time — the tiles within a single image already
    /// saturate the GPU, so overlapping images would only trade throughput for OOM risk.</para>
    ///
    /// Workflow: workflow/image/seedvr2/seedvr2-tiling-upscaleAPI.json.
    /// Needs the SeedVR2 pack (its models auto-download) plus moonwhaler/comfyui-seedvr2-tilingupscaler,
    /// which the missing-node resolver offers to install on the first submit.
    /// </summary>
    public class ImageUpscalerViewModel : INotifyPropertyChanged
    {
        private const string WorkflowFile = "workflow/image/seedvr2/seedvr2-tiling-upscaleAPI.json";
        private const string SavePrefix = "seedvr2_upscale";

        // Workflow node ids in seedvr2-tiling-upscaleAPI.json.
        private const string LoadImageNode = "1";
        private const string DitLoaderNode = "2";
        private const string VaeLoaderNode = "3";
        private const string UpscalerNode = "4";

        private static readonly string[] ImageExtensions =
            { ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".tif", ".tiff" };

        private readonly ComfyUIService _comfyUIService;
        private readonly SettingsService _settingsService;
        private readonly IFileDialogService _fileDialogService;
        private readonly ComfyUIImageRetriever _imageRetriever;
        private readonly IAppLogger _logger;

        // Mode
        private bool _isBatchMode;

        // Single-image source
        private string _sourceImagePath = string.Empty;
        private BitmapImage? _sourceImageSource;
        private bool _hasSourceImage;

        // Batch source
        private string _batchFolderPath = string.Empty;
        private bool _includeSubfolders;
        private bool _skipExisting = true;
        private string _outputFolderPath = string.Empty;

        // Model
        private string _selectedDitModel = "seedvr2_ema_3b_fp8_e4m3fn.safetensors";

        // Upscale settings
        private int _newResolution = 2048;
        private string _resolutionTarget = "longest";
        private long _seed = 100;
        private bool _randomizeSeed = true;

        // Tiling settings
        private int _tileWidth = 512;
        private int _tileHeight = 512;
        private int _tilePadding = 32;
        private int _tileUpscaleResolution = 1024;
        private string _tilingStrategy = "Chess";
        private int _tileBatchSize = 1;

        // Blending / colour
        private string _blendingMethod = "auto";
        private int _maskBlur;
        private double _antiAliasingStrength;
        private string _colorCorrection = "lab";

        // VRAM
        private int _blocksToSwap;
        private bool _tiledVae;
        private bool _keepModelLoaded = true;

        // Run state
        private bool _isGenerating;
        private double _progress;
        private string _statusMessage = "Upload an image, or point at a folder to upscale in batch";
        private string _logOutput = string.Empty;
        private CancellationTokenSource? _cts;

        // Result
        private BitmapImage? _resultImageSource;
        private bool _hasResult;
        private string _resultImagePath = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public ImageUpscalerViewModel(
            ComfyUIService comfyUIService,
            IAppLogger logger,
            SettingsService settingsService,
            IFileDialogService fileDialogService,
            ComfyUIImageRetriever imageRetriever)
        {
            _comfyUIService = comfyUIService ?? throw new ArgumentNullException(nameof(comfyUIService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _fileDialogService = fileDialogService ?? throw new ArgumentNullException(nameof(fileDialogService));
            _imageRetriever = imageRetriever ?? throw new ArgumentNullException(nameof(imageRetriever));

            BrowseSourceImageCommand = new RelayCommand(async () => await BrowseSourceImageAsync(), () => !IsBusy);
            BrowseBatchFolderCommand = new RelayCommand(async () => await BrowseBatchFolderAsync(), () => !IsBusy);
            BrowseOutputFolderCommand = new RelayCommand(async () => await BrowseOutputFolderAsync(), () => !IsBusy);
            RescanBatchFolderCommand = new RelayCommand(ScanBatchFolder, () => !IsBusy && HasBatchFolder);
            GenerateCommand = new RelayCommand(async () => await GenerateAsync(), () => CanGenerate);
            CancelCommand = new RelayCommand(Cancel, () => IsBusy);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);
            OpenResultImageCommand = new RelayCommand(OpenResultImage, () => HasResult);
        }

        // ── Option lists ─────────────────────────────────────────────────────
        /// <summary>The SeedVR2 pack's DiT checkpoints. 3B is the fast default; 7B is sharper and far
        /// heavier; the "sharp" variants push detail harder at the cost of more artefacting.</summary>
        public ObservableCollection<string> DitModels { get; } = new()
        {
            "seedvr2_ema_3b_fp8_e4m3fn.safetensors",
            "seedvr2_ema_3b_fp16.safetensors",
            "seedvr2_ema_3b-Q8_0.gguf",
            "seedvr2_ema_3b-Q4_K_M.gguf",
            "seedvr2_ema_7b_fp8_e4m3fn_mixed_block35_fp16.safetensors",
            "seedvr2_ema_7b_fp16.safetensors",
            "seedvr2_ema_7b-Q4_K_M.gguf",
            "seedvr2_ema_7b_sharp_fp8_e4m3fn_mixed_block35_fp16.safetensors",
            "seedvr2_ema_7b_sharp_fp16.safetensors",
            "seedvr2_ema_7b_sharp-Q4_K_M.gguf"
        };

        public ObservableCollection<string> ResolutionTargets { get; } = new() { "longest", "shortest" };
        public ObservableCollection<string> TilingStrategies { get; } = new() { "Chess", "Linear" };

        public ObservableCollection<string> BlendingMethods { get; } = new()
            { "auto", "multiband", "bilateral", "content_aware", "linear", "simple" };

        public ObservableCollection<string> ColorCorrections { get; } = new()
            { "lab", "wavelet", "wavelet_adaptive", "hsv", "adain", "none" };

        /// <summary>Rows of the folder batch, in the order they will run.</summary>
        public ObservableCollection<ImageUpscaleBatchItem> BatchItems { get; } = new();

        // ── Mode ─────────────────────────────────────────────────────────────
        public bool IsBatchMode
        {
            get => _isBatchMode;
            set
            {
                if (_isBatchMode == value) return;
                _isBatchMode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsSingleMode));
                OnPropertyChanged(nameof(EffectiveOutputFolder));
                OnPropertyChanged(nameof(CanGenerate));
                OnPropertyChanged(nameof(GenerateButtonText));
                NotifyCommands();
            }
        }

        public bool IsSingleMode
        {
            get => !_isBatchMode;
            set { if (value) IsBatchMode = false; }
        }

        public string GenerateButtonText => IsBatchMode
            ? $"🚀 Upscale {PendingCount} Image{(PendingCount == 1 ? "" : "s")}"
            : "🚀 Upscale Image";

        // ── Single-image source ──────────────────────────────────────────────
        public string SourceImagePath
        {
            get => _sourceImagePath;
            set { _sourceImagePath = value; OnPropertyChanged(); }
        }

        public BitmapImage? SourceImageSource
        {
            get => _sourceImageSource;
            set { _sourceImageSource = value; OnPropertyChanged(); }
        }

        public bool HasSourceImage
        {
            get => _hasSourceImage;
            set
            {
                _hasSourceImage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(NoSourceImage));
                OnPropertyChanged(nameof(CanGenerate));
                NotifyCommands();
            }
        }

        public bool NoSourceImage => !_hasSourceImage;

        // ── Batch source ─────────────────────────────────────────────────────
        public string BatchFolderPath
        {
            get => _batchFolderPath;
            set
            {
                _batchFolderPath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasBatchFolder));
                OnPropertyChanged(nameof(EffectiveOutputFolder));
                NotifyCommands();
            }
        }

        public bool HasBatchFolder => !string.IsNullOrWhiteSpace(_batchFolderPath) && Directory.Exists(_batchFolderPath);

        /// <summary>Walk nested folders too. The tree is mirrored under the output folder.</summary>
        public bool IncludeSubfolders
        {
            get => _includeSubfolders;
            set
            {
                if (_includeSubfolders == value) return;
                _includeSubfolders = value;
                OnPropertyChanged();
                if (HasBatchFolder && !IsBusy) ScanBatchFolder();
            }
        }

        /// <summary>Skip images whose output already exists, so an interrupted batch resumes where it
        /// stopped instead of paying for the finished ones again.</summary>
        public bool SkipExisting
        {
            get => _skipExisting;
            set
            {
                if (_skipExisting == value) return;
                _skipExisting = value;
                OnPropertyChanged();
                if (HasBatchFolder && !IsBusy) ScanBatchFolder();
            }
        }

        public string OutputFolderPath
        {
            get => _outputFolderPath;
            set { _outputFolderPath = value; OnPropertyChanged(); OnPropertyChanged(nameof(EffectiveOutputFolder)); }
        }

        /// <summary>Where results land: the chosen folder, else "upscaled" beside the batch input,
        /// else the app's own upscaled-images folder for single shots.</summary>
        public string EffectiveOutputFolder
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(_outputFolderPath)) return _outputFolderPath;
                if (IsBatchMode && HasBatchFolder) return Path.Combine(_batchFolderPath, "upscaled");
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "upscaled-images");
            }
        }

        public int PendingCount => BatchItems.Count(i => i.Status == ImageUpscaleBatchStatus.Pending
                                                      || i.Status == ImageUpscaleBatchStatus.Running);

        public string BatchSummary => BatchItems.Count == 0
            ? "No images scanned"
            : $"{BatchItems.Count} found · {BatchItems.Count(i => i.Status == ImageUpscaleBatchStatus.Done)} done · " +
              $"{BatchItems.Count(i => i.Status == ImageUpscaleBatchStatus.Failed)} failed · " +
              $"{BatchItems.Count(i => i.Status == ImageUpscaleBatchStatus.Skipped)} skipped";

        // ── Model ────────────────────────────────────────────────────────────
        public string SelectedDitModel
        {
            get => _selectedDitModel;
            set { _selectedDitModel = value; OnPropertyChanged(); }
        }

        // ── Upscale settings ─────────────────────────────────────────────────
        /// <summary>Target pixels on the side named by <see cref="ResolutionTarget"/>. Aspect is kept.</summary>
        public int NewResolution
        {
            get => _newResolution;
            set
            {
                _newResolution = Math.Max(16, Math.Min(16384, (int)(Math.Round(value / 16.0) * 16)));
                OnPropertyChanged();
                OnPropertyChanged(nameof(NewResolutionText));
            }
        }

        public string NewResolutionText => $"{_newResolution} px ({_resolutionTarget} side)";

        public string ResolutionTarget
        {
            get => _resolutionTarget;
            set { _resolutionTarget = value; OnPropertyChanged(); OnPropertyChanged(nameof(NewResolutionText)); }
        }

        public long Seed
        {
            get => _seed;
            set { _seed = value; OnPropertyChanged(); }
        }

        public bool RandomizeSeed
        {
            get => _randomizeSeed;
            set { _randomizeSeed = value; OnPropertyChanged(); }
        }

        // ── Tiling settings ──────────────────────────────────────────────────
        public int TileWidth
        {
            get => _tileWidth;
            set { _tileWidth = Clamp8(value, 64, 8192); OnPropertyChanged(); OnPropertyChanged(nameof(TileSizeText)); }
        }

        public int TileHeight
        {
            get => _tileHeight;
            set { _tileHeight = Clamp8(value, 64, 8192); OnPropertyChanged(); OnPropertyChanged(nameof(TileSizeText)); }
        }

        public string TileSizeText => $"{_tileWidth} × {_tileHeight}";

        /// <summary>Overlap between neighbouring tiles. More overlap hides seams but costs time.</summary>
        public int TilePadding
        {
            get => _tilePadding;
            set { _tilePadding = Clamp8(value, 0, 8192); OnPropertyChanged(); OnPropertyChanged(nameof(TilePaddingText)); }
        }

        public string TilePaddingText => $"{_tilePadding} px";

        /// <summary>Ceiling on the resolution any single tile is upscaled to — the real VRAM dial.</summary>
        public int TileUpscaleResolution
        {
            get => _tileUpscaleResolution;
            set
            {
                _tileUpscaleResolution = Clamp8(value, 64, 8192);
                OnPropertyChanged();
                OnPropertyChanged(nameof(TileUpscaleResolutionText));
            }
        }

        public string TileUpscaleResolutionText => $"{_tileUpscaleResolution} px";

        public string TilingStrategy
        {
            get => _tilingStrategy;
            set { _tilingStrategy = value; OnPropertyChanged(); }
        }

        /// <summary>Tiles handed to SeedVR2 per call. Snapped to its required 4n+1 pattern.</summary>
        public int TileBatchSize
        {
            get => _tileBatchSize;
            set
            {
                var snapped = Math.Max(1, Math.Min(21, value));
                _tileBatchSize = snapped <= 1 ? 1 : ((snapped - 1) / 4) * 4 + 1;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TileBatchSizeText));
            }
        }

        public string TileBatchSizeText => $"{_tileBatchSize} tile{(_tileBatchSize == 1 ? "" : "s")} / call";

        // ── Blending / colour ────────────────────────────────────────────────
        public string BlendingMethod
        {
            get => _blendingMethod;
            set { _blendingMethod = value; OnPropertyChanged(); }
        }

        /// <summary>0 = multi-band frequency separation (best detail); 1–3 minimal blur; 4+ traditional.</summary>
        public int MaskBlur
        {
            get => _maskBlur;
            set { _maskBlur = Math.Max(0, Math.Min(64, value)); OnPropertyChanged(); OnPropertyChanged(nameof(MaskBlurText)); }
        }

        public string MaskBlurText => _maskBlur == 0 ? "0 (multi-band)" : _maskBlur.ToString();

        public double AntiAliasingStrength
        {
            get => _antiAliasingStrength;
            set
            {
                _antiAliasingStrength = Math.Max(0, Math.Min(1, value));
                OnPropertyChanged();
                OnPropertyChanged(nameof(AntiAliasingStrengthText));
            }
        }

        public string AntiAliasingStrengthText => _antiAliasingStrength <= 0 ? "off" : $"{_antiAliasingStrength:0.00}";

        public string ColorCorrection
        {
            get => _colorCorrection;
            set { _colorCorrection = value; OnPropertyChanged(); }
        }

        // ── VRAM ─────────────────────────────────────────────────────────────
        /// <summary>Transformer blocks parked off the GPU. Trades speed for VRAM headroom; it needs an
        /// offload device, which is why anything above 0 forces CPU offload in the built workflow.</summary>
        public int BlocksToSwap
        {
            get => _blocksToSwap;
            set { _blocksToSwap = Math.Max(0, Math.Min(36, value)); OnPropertyChanged(); OnPropertyChanged(nameof(BlocksToSwapText)); }
        }

        public string BlocksToSwapText => _blocksToSwap == 0 ? "off" : $"{_blocksToSwap} blocks";

        /// <summary>Tile the VAE encode/decode too — the other place a large frame blows up VRAM.</summary>
        public bool TiledVae
        {
            get => _tiledVae;
            set { _tiledVae = value; OnPropertyChanged(); }
        }

        /// <summary>Keep the DiT and VAE resident between runs. Worth a lot across a batch; it needs an
        /// offload device, so the weights park in system RAM rather than being dropped and reloaded.</summary>
        public bool KeepModelLoaded
        {
            get => _keepModelLoaded;
            set { _keepModelLoaded = value; OnPropertyChanged(); }
        }

        // ── Run state ────────────────────────────────────────────────────────
        public bool IsGenerating
        {
            get => _isGenerating;
            set
            {
                _isGenerating = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(CanGenerate));
                NotifyCommands();
            }
        }

        public bool IsBusy => _isGenerating;

        public double Progress
        {
            get => _progress;
            set { _progress = value; OnPropertyChanged(); OnPropertyChanged(nameof(ProgressText)); }
        }

        public string ProgressText => $"{Progress:F0}%";

        public string StatusMessage
        {
            get => _statusMessage;
            set { _statusMessage = value; OnPropertyChanged(); }
        }

        public string LogOutput
        {
            get => _logOutput;
            set { _logOutput = value; OnPropertyChanged(); }
        }

        // ── Result ───────────────────────────────────────────────────────────
        public BitmapImage? ResultImageSource
        {
            get => _resultImageSource;
            set { _resultImageSource = value; OnPropertyChanged(); }
        }

        public bool HasResult
        {
            get => _hasResult;
            set
            {
                _hasResult = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(NoResult));
                OpenResultFolderCommand.NotifyCanExecuteChanged();
                OpenResultImageCommand.NotifyCanExecuteChanged();
            }
        }

        public bool NoResult => !_hasResult;

        public string ResultImagePath
        {
            get => _resultImagePath;
            set { _resultImagePath = value; OnPropertyChanged(); }
        }

        public bool CanGenerate => !IsBusy && (IsBatchMode ? PendingCount > 0 : HasSourceImage);

        // ── Commands ─────────────────────────────────────────────────────────
        public RelayCommand BrowseSourceImageCommand { get; }
        public RelayCommand BrowseBatchFolderCommand { get; }
        public RelayCommand BrowseOutputFolderCommand { get; }
        public RelayCommand RescanBatchFolderCommand { get; }
        public RelayCommand GenerateCommand { get; }
        public RelayCommand CancelCommand { get; }
        public RelayCommand OpenResultFolderCommand { get; }
        public RelayCommand OpenResultImageCommand { get; }

        private void NotifyCommands()
        {
            BrowseSourceImageCommand.NotifyCanExecuteChanged();
            BrowseBatchFolderCommand.NotifyCanExecuteChanged();
            BrowseOutputFolderCommand.NotifyCanExecuteChanged();
            RescanBatchFolderCommand.NotifyCanExecuteChanged();
            GenerateCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
        }

        // ── Browse / scan ────────────────────────────────────────────────────
        private async Task BrowseSourceImageAsync()
        {
            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select Image to Upscale",
                "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.tif;*.tiff",
                persistKey: "upscaler.source-image");
            if (!string.IsNullOrEmpty(path))
                SetSourceImage(path);
        }

        public void SetSourceImage(string path)
        {
            if (!File.Exists(path)) return;
            SourceImagePath = path;
            try
            {
                SourceImageSource = LoadBitmap(path);
                HasSourceImage = true;
                AddLog($"Source: {Path.GetFileName(path)}");
            }
            catch (Exception ex) { AddLog($"ERROR loading image: {ex.Message}"); }
        }

        private async Task BrowseBatchFolderAsync()
        {
            var path = await _fileDialogService.OpenFolderDialogAsync(
                "Select Folder of Images to Upscale",
                persistKey: "upscaler.batch-folder");
            if (string.IsNullOrEmpty(path)) return;
            IsBatchMode = true;
            BatchFolderPath = path;
            ScanBatchFolder();
        }

        private async Task BrowseOutputFolderAsync()
        {
            var path = await _fileDialogService.OpenFolderDialogAsync(
                "Select Output Folder",
                showNewFolderButton: true,
                persistKey: "upscaler.output-folder");
            if (string.IsNullOrEmpty(path)) return;
            OutputFolderPath = path;
            if (IsBatchMode && HasBatchFolder) ScanBatchFolder();
        }

        /// <summary>Rebuild the batch list from the folder. Rows whose output already exists are marked
        /// Skipped up front (when <see cref="SkipExisting"/>), so the count on the button is the work
        /// actually left to do.</summary>
        public void ScanBatchFolder()
        {
            BatchItems.Clear();
            if (!HasBatchFolder)
            {
                RefreshBatchCounts();
                return;
            }

            try
            {
                var search = IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                var outputRoot = Path.GetFullPath(EffectiveOutputFolder)
                    .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

                var files = Directory.EnumerateFiles(BatchFolderPath, "*.*", search)
                    .Where(f => ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    // Never re-upscale our own results when the output folder sits inside the input tree.
                    .Where(f => !Path.GetFullPath(f).StartsWith(outputRoot, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var file in files)
                {
                    var item = new ImageUpscaleBatchItem(file);
                    if (SkipExisting && File.Exists(ResolveOutputPath(file)))
                    {
                        item.Status = ImageUpscaleBatchStatus.Skipped;
                        item.Detail = "already upscaled";
                    }
                    BatchItems.Add(item);
                }

                AddLog($"Scanned {BatchFolderPath}: {files.Count} image(s), {PendingCount} to do");
                StatusMessage = PendingCount > 0
                    ? $"{PendingCount} image(s) ready to upscale"
                    : files.Count == 0
                        ? "No images found in that folder"
                        : "Nothing to do — every image already has a result";
            }
            catch (Exception ex)
            {
                AddLog($"ERROR scanning folder: {ex.Message}");
                StatusMessage = $"Could not scan folder: {ex.Message}";
            }

            RefreshBatchCounts();
        }

        private void RefreshBatchCounts()
        {
            OnPropertyChanged(nameof(PendingCount));
            OnPropertyChanged(nameof(BatchSummary));
            OnPropertyChanged(nameof(GenerateButtonText));
            OnPropertyChanged(nameof(CanGenerate));
            NotifyCommands();
        }

        /// <summary>Where one source file's result is written. In batch the input tree is mirrored under
        /// the output folder, so two same-named files in different subfolders can't collide.</summary>
        private string ResolveOutputPath(string sourcePath)
        {
            var root = EffectiveOutputFolder;
            var name = Path.GetFileNameWithoutExtension(sourcePath) + "_upscaled.png";

            if (IsBatchMode && HasBatchFolder)
            {
                var relativeDir = Path.GetDirectoryName(
                    Path.GetRelativePath(BatchFolderPath, sourcePath)) ?? string.Empty;
                return Path.Combine(root, relativeDir, name);
            }

            return Path.Combine(root, name);
        }

        // ── Generate ─────────────────────────────────────────────────────────
        private async Task GenerateAsync()
        {
            if (!CanGenerate) return;
            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(App.ShutdownToken);

            try
            {
                IsGenerating = true;
                Progress = 0;
                AddLog("=== Image Upscaler ===");
                AddLog($"Model: {SelectedDitModel}");
                AddLog($"Target {NewResolution}px {ResolutionTarget} side | tiles {TileSizeText} +{TilePadding} | tile cap {TileUpscaleResolution}px");

                StatusMessage = "Connecting to ComfyUI...";
                if (!_comfyUIService.IsConnected)
                {
                    await _comfyUIService.ConnectAsync(_cts.Token);
                    AddLog("Connected");
                }

                if (IsBatchMode)
                    await RunBatchAsync(_cts.Token);
                else
                    await RunSingleAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Cancelled";
                AddLog("Cancelled");
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
                AddLog($"ERROR: {ex.GetType().Name}: {ex.Message}");
                _logger.LogError($"Image upscaler: {ex}");
            }
            finally
            {
                IsGenerating = false;
                RefreshBatchCounts();
                AddLog("=== Image Upscaler ended ===");
            }
        }

        private async Task RunSingleAsync(CancellationToken token)
        {
            var outputPath = ResolveOutputPath(SourceImagePath);
            StatusMessage = $"Upscaling {Path.GetFileName(SourceImagePath)}...";

            await UpscaleOneAsync(SourceImagePath, outputPath, 0, 1, token);

            Progress = 100;
            StatusMessage = $"Done! {Path.GetFileName(outputPath)}";
        }

        private async Task RunBatchAsync(CancellationToken token)
        {
            var queue = BatchItems.Where(i => i.Status == ImageUpscaleBatchStatus.Pending).ToList();
            var total = queue.Count;
            AddLog($"Batch: {total} image(s) from {BatchFolderPath} → {EffectiveOutputFolder}");

            var succeeded = 0;
            var failed = 0;

            for (var i = 0; i < total; i++)
            {
                token.ThrowIfCancellationRequested();
                var item = queue[i];
                item.Status = ImageUpscaleBatchStatus.Running;
                RefreshBatchCounts();

                StatusMessage = $"[{i + 1}/{total}] {item.FileName}";
                var outputPath = ResolveOutputPath(item.SourcePath);

                try
                {
                    await UpscaleOneAsync(item.SourcePath, outputPath, i, total, token);
                    item.Status = ImageUpscaleBatchStatus.Done;
                    item.OutputPath = outputPath;
                    item.Detail = Path.GetFileName(outputPath);
                    succeeded++;
                }
                catch (OperationCanceledException)
                {
                    // Hand the row back to the queue so a rerun picks it up where this one stopped.
                    item.Status = ImageUpscaleBatchStatus.Pending;
                    item.Detail = "cancelled";
                    throw;
                }
                catch (Exception ex)
                {
                    // One bad file shouldn't abandon the rest of the folder — record it and carry on.
                    item.Status = ImageUpscaleBatchStatus.Failed;
                    item.Detail = ex.Message;
                    failed++;
                    AddLog($"ERROR on {item.FileName}: {ex.Message}");
                    _logger.LogError($"Image upscaler batch item '{item.SourcePath}': {ex}");
                }

                RefreshBatchCounts();
            }

            Progress = 100;
            StatusMessage = failed == 0
                ? $"Batch done — {succeeded} image(s) upscaled into {EffectiveOutputFolder}"
                : $"Batch done — {succeeded} upscaled, {failed} failed (see log)";
        }

        /// <summary>Upload → run the workflow → write the result. <paramref name="index"/> and
        /// <paramref name="total"/> only shape the progress bar, so single and batch share one path.</summary>
        private async Task UpscaleOneAsync(string sourcePath, string outputPath, int index, int total, CancellationToken token)
        {
            var slotStart = 100.0 * index / total;
            var slotSize = 100.0 / total;

            Progress = slotStart;
            var uploaded = await _comfyUIService.UploadImageAsync(sourcePath, token);
            AddLog($"Uploaded {Path.GetFileName(sourcePath)} as {uploaded}");

            var seed = RandomizeSeed ? Random.Shared.NextInt64(0, uint.MaxValue) : Seed;
            if (RandomizeSeed) Seed = seed;

            var workflow = BuildWorkflow(uploaded, seed);

            var progressReporter = new Progress<FlipPix.ComfyUI.Models.ProgressMessage>(msg =>
            {
                if (msg.Data?.Value == null || msg.Data?.Max == null || msg.Data.Max <= 0) return;
                var fraction = (double)msg.Data.Value / msg.Data.Max;
                WpfApp.Current?.Dispatcher.Invoke(() =>
                {
                    // Leave the last tenth of the slot for retrieval and writing the file.
                    Progress = slotStart + fraction * slotSize * 0.9;
                    var prefix = total > 1 ? $"[{index + 1}/{total}] " : string.Empty;
                    StatusMessage = $"{prefix}Tile {msg.Data.Value}/{msg.Data.Max}";
                });
            });

            var promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, progressReporter, token);
            AddLog($"Executed: {promptId}");

            var images = await _imageRetriever.GetOutputImagesAsync(
                _comfyUIService.HttpClient,
                _settingsService,
                _logger,
                AddLog,
                expectedPattern: SavePrefix,
                promptId: promptId,
                ct: token);

            if (images.Count == 0)
                throw new InvalidOperationException("ComfyUI returned no upscaled image — check its console for the node's error");

            var outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir)) Directory.CreateDirectory(outputDir);
            await File.WriteAllBytesAsync(outputPath, images[^1], token);
            AddLog($"Saved: {outputPath}");

            ResultImagePath = outputPath;
            WpfApp.Current?.Dispatcher.Invoke(() =>
            {
                try { ResultImageSource = LoadBitmap(outputPath); }
                catch (Exception ex) { AddLog($"ERROR loading result: {ex.Message}"); }
            });
            HasResult = true;

            Progress = slotStart + slotSize;
        }

        // ── Workflow building ────────────────────────────────────────────────
        private JsonElement BuildWorkflow(string uploadedImage, long seed)
        {
            var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, WorkflowFile);
            if (!File.Exists(workflowPath))
                throw new FileNotFoundException($"Workflow not found: {workflowPath}");

            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(workflowPath))
                ?? throw new InvalidOperationException("Failed to parse workflow JSON");

            // BlockSwap and cross-run caching both need somewhere to park the weights; with
            // offload_device left at "none" the loader silently ignores them.
            var needsOffload = BlocksToSwap > 0 || KeepModelLoaded;
            var offloadDevice = needsOffload ? "cpu" : "none";

            UpdateNode(dict, LoadImageNode, inputs => inputs["image"] = uploadedImage);

            UpdateNode(dict, DitLoaderNode, inputs =>
            {
                inputs["model"] = SelectedDitModel;
                inputs["blocks_to_swap"] = BlocksToSwap;
                inputs["offload_device"] = offloadDevice;
                inputs["cache_model"] = KeepModelLoaded;
            });

            UpdateNode(dict, VaeLoaderNode, inputs =>
            {
                inputs["encode_tiled"] = TiledVae;
                inputs["decode_tiled"] = TiledVae;
                inputs["offload_device"] = offloadDevice;
                inputs["cache_model"] = KeepModelLoaded;
            });

            UpdateNode(dict, UpscalerNode, inputs =>
            {
                inputs["seed"] = seed;
                inputs["new_resolution"] = NewResolution;
                inputs["resolution_target"] = ResolutionTarget;
                inputs["tile_width"] = TileWidth;
                inputs["tile_height"] = TileHeight;
                inputs["tile_padding"] = TilePadding;
                inputs["tile_upscale_resolution"] = TileUpscaleResolution;
                inputs["tiling_strategy"] = TilingStrategy;
                inputs["tile_batch_size"] = TileBatchSize;
                inputs["mask_blur"] = MaskBlur;
                inputs["anti_aliasing_strength"] = AntiAliasingStrength;
                inputs["blending_method"] = BlendingMethod;
                inputs["color_correction"] = ColorCorrection;
            });

            return JsonSerializer.SerializeToElement(dict);
        }

        private static void UpdateNode(
            Dictionary<string, JsonElement> dict,
            string nodeId,
            Action<Dictionary<string, object>> updater)
        {
            if (!dict.ContainsKey(nodeId)) return;
            var node = JsonSerializer.Deserialize<Dictionary<string, object>>(dict[nodeId].GetRawText());
            if (node == null || !node.ContainsKey("inputs")) return;
            var inputs = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(node["inputs"]));
            if (inputs == null) return;
            updater(inputs);
            node["inputs"] = inputs;
            dict[nodeId] = JsonSerializer.SerializeToElement(node);
        }

        // ── Helpers ──────────────────────────────────────────────────────────
        private void Cancel()
        {
            AddLog("Cancel requested");
            StatusMessage = "Cancelling...";
            _cts?.Cancel();
        }

        private static int Clamp8(int value, int min, int max)
            => Math.Max(min, Math.Min(max, (int)(Math.Round(value / 8.0) * 8)));

        private static BitmapImage LoadBitmap(string path)
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        private void OpenResultFolder()
        {
            if (!string.IsNullOrEmpty(ResultImagePath) && File.Exists(ResultImagePath))
            {
                Process.Start("explorer.exe", $"/select,\"{ResultImagePath}\"");
                return;
            }
            var folder = EffectiveOutputFolder;
            if (Directory.Exists(folder))
                Process.Start("explorer.exe", $"\"{folder}\"");
        }

        private void OpenResultImage()
        {
            if (!string.IsNullOrEmpty(ResultImagePath) && File.Exists(ResultImagePath))
                Process.Start(new ProcessStartInfo(ResultImagePath) { UseShellExecute = true });
        }

        private void AddLog(string message)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            WpfApp.Current?.Dispatcher.Invoke(() => LogOutput = LogOutput + line + "\n");
            _logger.LogInfo(message);
        }
    }
}
