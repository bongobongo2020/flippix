using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Application = System.Windows.Application;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// "Scail 2" tab — a single, low-scroll page that chains two existing pipelines:
    ///
    ///   Stage A (Klein char-swap, from Qwen-Edit): a frame grabbed from the base scene video at the
    ///   current scrub position + Character 1 + Character 2 → LLM analysis → Flux.2 Klein char-swap
    ///   workflow → a composited still with both people replaced.
    ///
    ///   Stage B (WAN SCAIL II): that composited still becomes the character image and the same base
    ///   video becomes the driving video → LLM appearance/motion analysis over the marked range →
    ///   SCAIL2 workflow → final motion-transfer video.
    ///
    /// Subclasses <see cref="WanScailGgufViewModel"/> to inherit the entire SCAIL II back end
    /// (driving video, trim markers, motion analysis, queue, generation, result playback). It only
    /// adds the Klein char-swap front-end and the two auto-chain triggers. All numeric/advanced
    /// settings are hidden and defaulted (subject "person", animation mode, fps 24, maxEdge 1280,
    /// random seed, VRAM-optimize on — defaults come from the base view model).
    /// </summary>
    public class Scail2ViewModel : WanScailGgufViewModel
    {
        // Two-character path uses the dedicated Flux.2 Character Replacer (2 characters): LoadImage 40 =
        // Character 1 (left), 60 = Character 2 (right), 39 = pose source (base scene frame). Like the
        // single-character path the left/right prompt is baked into the workflow (node 41), so no separate
        // LLM analysis is needed.
        private const string KleinCharReplacer2CharWorkflowFile = "workflow/image/klein/Flux.2 Character Replacer - 2characters.json";
        private const string KleinCharReplacer2CharSavePrefix = "char_replaced_2c";

        // Single-character path uses the dedicated Flux.2 Character Replacer (v2.4): node 40 = subject
        // (Character 1 likeness), node 39 = pose source (base scene frame). Stage 47 builds a neutral
        // posed body from the pose image (cropped widescreen), stage 9 composites the subject onto it,
        // so the result keeps the base scene's pose + orientation with Character 1's likeness. The prompt
        // is fixed inside the workflow, so no separate LLM analysis is needed.
        private const string KleinCharReplacerWorkflowFile = "workflow/image/klein/Flux.2 Character Replacer v2.4.json";
        private const string KleinCharReplacerSavePrefix = "char_replaced";

        // Own references — the base keeps these private, so Scail 2 stores its own copies from DI.
        private readonly IFileDialogService _fileDialogService;

        // ── Character 1 (replaces the LEFT person) ───────────────────────────
        private string _char1ImagePath = string.Empty;
        private BitmapImage? _char1ImageSource;
        private bool _hasChar1Image;

        // ── Character 2 (replaces the RIGHT person) ──────────────────────────
        private string _char2ImagePath = string.Empty;
        private BitmapImage? _char2ImageSource;
        private bool _hasChar2Image;

        // ── Klein char-swap result (also pushed into the inherited CharacterImagePath) ──
        private BitmapImage? _charSwapResultSource;
        private bool _hasCharSwapResult;
        private bool _isCharSwapping;
        private string _charSwapStatus = "Load a base video and characters, scrub to a frame, then press Swap";

        private CancellationTokenSource? _charSwapCts;


        public Scail2ViewModel(
            ComfyUIService comfyUIService,
            LMStudioService lmStudioService,
            IAppLogger logger,
            FlipPix.Core.Services.SettingsService settingsService,
            IServiceProvider? serviceProvider,
            WorkflowQueueCoordinator workflowCoordinator,
            IFileDialogService fileDialogService)
            : base(comfyUIService, lmStudioService, logger, settingsService, serviceProvider, workflowCoordinator, fileDialogService)
        {
            _fileDialogService = fileDialogService ?? throw new ArgumentNullException(nameof(fileDialogService));

            // SAM3 subject default for the SCAIL II stage (settings are hidden).
            Subject = "person";

            BrowseChar1Command = new RelayCommand(async () => await BrowseChar1Async(), () => !IsCharSwapping);
            BrowseChar2Command = new RelayCommand(async () => await BrowseChar2Async(), () => !IsCharSwapping);
            BrowseFinalImageCommand = new RelayCommand(async () => await BrowseFinalImageAsync(), () => !IsCharSwapping);
            SwapCharactersCommand = new RelayCommand(async () => await RunCharSwapStageAsync(), () => CanSwapCharacters);
            SwapChar1OnlyCommand = new RelayCommand(async () => await RunChar1OnlySwapAsync(), () => CanSwapChar1Only);
            RemoveChar1Command = new RelayCommand(ClearChar1Image, () => HasChar1Image && !IsCharSwapping);
            RemoveChar2Command = new RelayCommand(ClearChar2Image, () => HasChar2Image && !IsCharSwapping);

            // Reset the char-swap stage whenever a different base video is loaded.
            PropertyChanged += OnSelfPropertyChanged;

            AddLog("Scail 2 initialized");
        }

        private void OnSelfPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(InputVideoPath))
            {
                // New base video → the previous char-swap composite no longer applies. The character
                // image (and its derived SCAIL II input) is cleared so a new clip can't be generated
                // against the old composite; the user must run the swap again for the new video.
                CharSwapResultSource = null;
                HasCharSwapResult = false;
                CharacterImagePath = string.Empty;
                RefreshSwapReadiness();
            }
        }

        #region Character inputs

        public string Char1ImagePath
        {
            get => _char1ImagePath;
            private set { _char1ImagePath = value; OnPropertyChanged(); }
        }

        public BitmapImage? Char1ImageSource
        {
            get => _char1ImageSource;
            private set { _char1ImageSource = value; OnPropertyChanged(); }
        }

        public bool HasChar1Image
        {
            get => _hasChar1Image;
            private set
            {
                _hasChar1Image = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(NoChar1Image));
                RemoveChar1Command?.NotifyCanExecuteChanged();
                RefreshSwapReadiness();
            }
        }

        public bool NoChar1Image => !_hasChar1Image;

        public string Char2ImagePath
        {
            get => _char2ImagePath;
            private set { _char2ImagePath = value; OnPropertyChanged(); }
        }

        public BitmapImage? Char2ImageSource
        {
            get => _char2ImageSource;
            private set { _char2ImageSource = value; OnPropertyChanged(); }
        }

        public bool HasChar2Image
        {
            get => _hasChar2Image;
            private set { _hasChar2Image = value; OnPropertyChanged(); OnPropertyChanged(nameof(NoChar2Image)); RemoveChar2Command?.NotifyCanExecuteChanged(); RefreshSwapReadiness(); }
        }

        public bool NoChar2Image => !_hasChar2Image;

        #endregion

        #region Char-swap result / status

        public BitmapImage? CharSwapResultSource
        {
            get => _charSwapResultSource;
            private set { _charSwapResultSource = value; OnPropertyChanged(); }
        }

        public bool HasCharSwapResult
        {
            get => _hasCharSwapResult;
            private set { _hasCharSwapResult = value; OnPropertyChanged(); OnPropertyChanged(nameof(NoCharSwapResult)); }
        }

        public bool NoCharSwapResult => !_hasCharSwapResult;

        public bool IsCharSwapping
        {
            get => _isCharSwapping;
            private set
            {
                if (_isCharSwapping == value) return;
                _isCharSwapping = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanSwapCharacters));
                OnPropertyChanged(nameof(CanSwapChar1Only));
                BrowseChar1Command.NotifyCanExecuteChanged();
                BrowseChar2Command.NotifyCanExecuteChanged();
                BrowseFinalImageCommand.NotifyCanExecuteChanged();
                SwapCharactersCommand.NotifyCanExecuteChanged();
                SwapChar1OnlyCommand.NotifyCanExecuteChanged();
                RemoveChar1Command.NotifyCanExecuteChanged();
                RemoveChar2Command.NotifyCanExecuteChanged();
            }
        }

        public string CharSwapStatus
        {
            get => _charSwapStatus;
            private set { if (_charSwapStatus != value) { _charSwapStatus = value; OnPropertyChanged(); } }
        }

        #endregion

        #region Commands

        public RelayCommand BrowseChar1Command { get; }
        public RelayCommand BrowseChar2Command { get; }
        public RelayCommand BrowseFinalImageCommand { get; }
        public RelayCommand SwapCharactersCommand { get; }
        public RelayCommand SwapChar1OnlyCommand { get; }
        public RelayCommand RemoveChar1Command { get; }
        public RelayCommand RemoveChar2Command { get; }

        // The two-character swap needs a base video + both characters.
        public bool CanSwapCharacters => HasInputVideo && HasChar1Image && HasChar2Image && !IsCharSwapping;

        // The single-character button only needs a base video + Character 1.
        public bool CanSwapChar1Only => HasInputVideo && HasChar1Image && !IsCharSwapping;

        private async Task BrowseChar1Async()
        {
            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select Character 1 (replaces the LEFT person)",
                "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.webp",
                persistKey: "scail2.char1");
            if (!string.IsNullOrEmpty(path)) SetChar1Image(path);
        }

        private async Task BrowseChar2Async()
        {
            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select Character 2 (replaces the RIGHT person)",
                "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.webp",
                persistKey: "scail2.char2");
            if (!string.IsNullOrEmpty(path)) SetChar2Image(path);
        }

        // Lets the user supply their own final image instead of (or in place of) a Klein char-swap
        // composite. The picked image becomes the SCAIL II character image directly.
        private async Task BrowseFinalImageAsync()
        {
            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select the final image (used as the character image)",
                "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.webp",
                persistKey: "scail2.final");
            if (!string.IsNullOrEmpty(path)) SetFinalImage(path);
        }

        public void SetFinalImage(string path)
        {
            if (!File.Exists(path)) return;
            try { CharSwapResultSource = LoadBitmap(path); }
            catch (Exception ex) { AddLog($"ERROR loading final image: {ex.Message}"); return; }
            HasCharSwapResult = true;
            // Drives the inherited preview + enables the SCAIL II stage.
            CharacterImagePath = path;
            AddLog($"Scail 2: final image = {Path.GetFileName(path)} (user-supplied)");
            RefreshSwapReadiness();
        }

        public void SetChar1Image(string path)
        {
            if (!File.Exists(path)) return;
            Char1ImagePath = path;
            try { Char1ImageSource = LoadBitmap(path); HasChar1Image = true; AddLog($"Scail 2: Character 1 = {Path.GetFileName(path)}"); }
            catch (Exception ex) { AddLog($"ERROR loading Character 1: {ex.Message}"); return; }
            // A changed character invalidates any existing composite; the user re-runs the swap.
            CharSwapResultSource = null;
            HasCharSwapResult = false;
            RefreshSwapReadiness();
        }

        public void SetChar2Image(string path)
        {
            if (!File.Exists(path)) return;
            Char2ImagePath = path;
            try { Char2ImageSource = LoadBitmap(path); HasChar2Image = true; AddLog($"Scail 2: Character 2 = {Path.GetFileName(path)}"); }
            catch (Exception ex) { AddLog($"ERROR loading Character 2: {ex.Message}"); return; }
            // A changed character invalidates any existing composite; the user re-runs the swap.
            CharSwapResultSource = null;
            HasCharSwapResult = false;
            RefreshSwapReadiness();
        }

        // Remove an uploaded character so it isn't sent into the swap (e.g. keep only Character 1).
        // Clears any existing composite so it rebuilds from the remaining inputs.
        public void ClearChar1Image()
        {
            if (IsCharSwapping) return;
            Char1ImagePath = string.Empty;
            Char1ImageSource = null;
            HasChar1Image = false;
            CharSwapResultSource = null;
            HasCharSwapResult = false;
            AddLog("Scail 2: Character 1 removed");
            RefreshSwapReadiness();
        }

        public void ClearChar2Image()
        {
            if (IsCharSwapping) return;
            Char2ImagePath = string.Empty;
            Char2ImageSource = null;
            HasChar2Image = false;
            CharSwapResultSource = null;
            HasCharSwapResult = false;
            AddLog("Scail 2: Character 2 removed");
            RefreshSwapReadiness();
        }

        #endregion

        #region Stage A — Klein char-swap

        /// <summary>
        /// Called from the view when the user positions the base video (pauses on a frame or moves a
        /// marker). Records that a frame was deliberately chosen so the Swap button knows a frame is
        /// ready. The swap itself never auto-runs — it waits for an explicit button press.
        /// </summary>
        public void NotifyScrubbed()
        {
            RefreshSwapReadiness();
        }

        // Updates the Swap buttons' enabled state and the status hint. The char-swap is explicit:
        // this never launches it — the user presses "Swap characters" / "Swap (Char 1)" when ready.
        private void RefreshSwapReadiness()
        {
            OnPropertyChanged(nameof(CanSwapCharacters));
            OnPropertyChanged(nameof(CanSwapChar1Only));
            SwapCharactersCommand?.NotifyCanExecuteChanged();
            SwapChar1OnlyCommand?.NotifyCanExecuteChanged();

            if (IsCharSwapping) return;
            if (HasCharSwapResult)
            {
                CharSwapStatus = "Character image ready — set the In/Out markers to generate the video";
                return;
            }
            if (!HasInputVideo)
            {
                CharSwapStatus = "Load a base video, then add characters and scrub to a frame";
                return;
            }
            if (!HasChar1Image)
            {
                CharSwapStatus = "Add Character 1 (and optionally Character 2), then press Swap";
                return;
            }
            CharSwapStatus = HasChar2Image
                ? "Scrub to a frame showing both people, then press “Swap characters”"
                : "Scrub to a frame, then press “Swap (Char 1)” — or add Character 2 for a two-person swap";
        }

        public async Task RunCharSwapStageAsync()
        {
            if (IsCharSwapping) return;
            if (!HasInputVideo || !HasChar1Image || !HasChar2Image) return;

            _charSwapCts?.Dispose();
            _charSwapCts = CancellationTokenSource.CreateLinkedTokenSource(App.ShutdownToken);
            var token = _charSwapCts.Token;
            string? baseStill = null;

            try
            {
                IsCharSwapping = true;
                HasCharSwapResult = false;
                AddLog("=== Scail 2: char-swap stage ===");

                // 1) Grab the current scrub frame as the base still for the swap.
                CharSwapStatus = "Grabbing base frame…";
                baseStill = await ExtractFrameAtAsync(InputVideoPath, PlaybackPositionSeconds, token);
                if (baseStill == null || !File.Exists(baseStill))
                    throw new Exception("Could not grab a frame from the base video (is ffmpeg installed?).");
                AddLog($"Base frame at {PlaybackPositionSeconds:F2}s → {Path.GetFileName(baseStill)}");

                // 2) Run the Flux.2 Character Replacer (2 characters) workflow. Character 1 → left
                //    (node 40), Character 2 → right (node 60), base frame = pose (node 39). The left/right
                //    mapping is baked into the workflow prompt, so no LLM analysis is needed.
                CharSwapStatus = "Running 2-character replacer (subjects + pose)…";
                using var lease = await _workflowCoordinator.AcquireAsync("Scail2CharSwap", token);

                if (!_comfyUIService.IsConnected)
                {
                    await _comfyUIService.ConnectAsync(token);
                    AddLog("Connected to ComfyUI");
                }

                // Character 1 = left (node 40), Character 2 = right (node 60), base frame = pose (node 39).
                var uploadedChar1 = await _comfyUIService.UploadImageAsync(Char1ImagePath, token);
                var uploadedChar2 = await _comfyUIService.UploadImageAsync(Char2ImagePath, token);
                var uploadedBase = await _comfyUIService.UploadImageAsync(baseStill, token);
                AddLog($"Uploaded char1(left)={uploadedChar1} char2(right)={uploadedChar2} pose(base)={uploadedBase}");

                var workflow = BuildCharReplacer2CharWorkflow(uploadedChar1, uploadedChar2, uploadedBase);
                await ExecuteKleinAndAdoptAsync(workflow, KleinCharReplacer2CharSavePrefix, token);

                CharSwapStatus = "Character image ready — set the In/Out markers to generate the video";
                AddLog("=== Char-swap complete ===");
            }
            catch (OperationCanceledException)
            {
                CharSwapStatus = "Char-swap cancelled";
                AddLog("Char-swap cancelled");
            }
            catch (Exception ex)
            {
                CharSwapStatus = $"Char-swap failed: {ex.Message}";
                AddLog($"ERROR (char-swap): {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (!string.IsNullOrEmpty(baseStill))
                    try { File.Delete(baseStill); } catch { }
                IsCharSwapping = false;
            }
        }

        // Single-character swap: drop Character 1 onto the current base frame's pose using the Flux.2
        // Character Replacer (v2.4) workflow. Character 1 is the subject (likeness), the base frame is
        // the pose source. The workflow rebuilds a neutral posed body from the base frame and composites
        // Character 1 onto it, holding the original pose + widescreen orientation with Character 1's
        // likeness. Like the full swap, the result becomes the SCAIL II character image.
        public async Task RunChar1OnlySwapAsync()
        {
            if (IsCharSwapping) return;
            if (!HasInputVideo || !HasChar1Image) return;

            _charSwapCts?.Dispose();
            _charSwapCts = CancellationTokenSource.CreateLinkedTokenSource(App.ShutdownToken);
            var token = _charSwapCts.Token;
            string? baseStill = null;

            try
            {
                IsCharSwapping = true;
                HasCharSwapResult = false;
                AddLog("=== Scail 2: single-character swap (Character 1, Character Replacer v2.4) ===");

                CharSwapStatus = "Grabbing base frame…";
                baseStill = await ExtractFrameAtAsync(InputVideoPath, PlaybackPositionSeconds, token);
                if (baseStill == null || !File.Exists(baseStill))
                    throw new Exception("Could not grab a frame from the base video (is ffmpeg installed?).");
                AddLog($"Base frame at {PlaybackPositionSeconds:F2}s → {Path.GetFileName(baseStill)}");

                CharSwapStatus = "Running Character Replacer (subject + pose)…";
                using var lease = await _workflowCoordinator.AcquireAsync("Scail2CharSwap", token);

                if (!_comfyUIService.IsConnected)
                {
                    await _comfyUIService.ConnectAsync(token);
                    AddLog("Connected to ComfyUI");
                }

                // Character 1 = subject/likeness (node 40); base frame = pose source (node 39).
                var uploadedSubject = await _comfyUIService.UploadImageAsync(Char1ImagePath, token);
                var uploadedPose = await _comfyUIService.UploadImageAsync(baseStill, token);
                AddLog($"Uploaded subject(char1)={uploadedSubject} pose(base)={uploadedPose}");

                var workflow = BuildCharReplacerWorkflow(uploadedSubject, uploadedPose);
                await ExecuteKleinAndAdoptAsync(workflow, KleinCharReplacerSavePrefix, token);

                CharSwapStatus = "Character image ready — set the In/Out markers to generate the video";
                AddLog("=== Single-character swap complete ===");
            }
            catch (OperationCanceledException)
            {
                CharSwapStatus = "Swap cancelled";
                AddLog("Single-character swap cancelled");
            }
            catch (Exception ex)
            {
                CharSwapStatus = $"Swap failed: {ex.Message}";
                AddLog($"ERROR (single swap): {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (!string.IsNullOrEmpty(baseStill))
                    try { File.Delete(baseStill); } catch { }
                IsCharSwapping = false;
            }
        }

        // Shared tail for both swap paths: run the built Klein workflow, retrieve the image, save it,
        // and adopt it as the SCAIL II character image. The save prefix differs per workflow
        // (2-character replacer vs. single Character Replacer), so it's passed in for the output-file match.
        private async Task ExecuteKleinAndAdoptAsync(JsonElement workflow, string savePrefix, CancellationToken token)
        {
            CharSwapStatus = "Compositing the character image…";
            var promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, null, token);
            AddLog($"Klein submitted: {promptId}");

            var bytes = await RetrieveKleinImageAsync(promptId, savePrefix, token);
            if (bytes == null)
                throw new Exception("No image returned from the Klein workflow — check ComfyUI logs.");

            var outDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "output", "scail2");
            Directory.CreateDirectory(outDir);
            var resultPath = Path.Combine(outDir, $"charswap_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            await File.WriteAllBytesAsync(resultPath, bytes, token);

            Application.Current?.Dispatcher.Invoke(() =>
            {
                CharSwapResultSource = LoadBitmap(resultPath);
                HasCharSwapResult = true;
                // Drives the inherited preview + enables the SCAIL II stage.
                CharacterImagePath = resultPath;
            });
            AddLog($"Saved character image: {Path.GetFileName(resultPath)}");
        }

        // Flux.2 Character Replacer (2 characters): LoadImage 40 = Character 1 (left), 60 = Character 2
        // (right), 39 = pose source (base scene frame). Stage 47 strips the two people in the pose frame,
        // stages 44/50 clean up each character reference, stage 9 composites both onto the scene with
        // three stacked reference latents (output node 9:65, saved by node 27 with the "char_replaced_2c"
        // prefix). The left/right mapping (image 2 = left, image 3 = right) is baked into the workflow
        // prompt (node 41). We reseed every noise source so each run varies.
        private JsonElement BuildCharReplacer2CharWorkflow(string uploadedChar1, string uploadedChar2, string uploadedPose)
        {
            var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, KleinCharReplacer2CharWorkflowFile);
            if (!File.Exists(workflowPath))
                throw new FileNotFoundException($"Character Replacer (2 characters) workflow not found: {workflowPath}");

            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(workflowPath))
                ?? throw new InvalidOperationException("Failed to parse Character Replacer (2 characters) workflow JSON");

            var rng = new Random();

            UpdateNode(dict, "40", inputs => inputs["image"] = uploadedChar1); // Character 1 → left
            UpdateNode(dict, "60", inputs => inputs["image"] = uploadedChar2); // Character 2 → right
            UpdateNode(dict, "39", inputs => inputs["image"] = uploadedPose);  // pose = base scene frame

            // Reseed every noise source so successive runs differ.
            UpdateNode(dict, "42", inputs => inputs["value"] = rng.Next(1, 2_000_000_000));               // main combine seed (PrimitiveInt)
            UpdateNode(dict, "44:187", inputs => inputs["noise_seed"] = rng.NextInt64(0, 999_999_999_999_999L)); // Character 1 clean-up
            UpdateNode(dict, "50:187", inputs => inputs["noise_seed"] = rng.NextInt64(0, 999_999_999_999_999L)); // Character 2 clean-up
            UpdateNode(dict, "47:199", inputs => inputs["noise_seed"] = rng.NextInt64(0, 999_999_999_999_999L)); // posed-body strip

            return JsonSerializer.SerializeToElement(dict);
        }

        // Flux.2 Character Replacer (v2.4): LoadImage 40 = subject (likeness), LoadImage 39 = pose source.
        // Stage 47 rebuilds a neutral posed body from the pose image, stage 9 composites the subject onto
        // it (output node 9:65, saved by node 27 with the "char_replaced" prefix). We swap the two images
        // and reseed every noise source so each run varies. Everything else (prompts, sizing) is left as
        // authored — the graph already sizes the output to the pose image, keeping the widescreen aspect.
        private JsonElement BuildCharReplacerWorkflow(string uploadedSubject, string uploadedPose)
        {
            var workflowPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, KleinCharReplacerWorkflowFile);
            if (!File.Exists(workflowPath))
                throw new FileNotFoundException($"Character Replacer workflow not found: {workflowPath}");

            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(workflowPath))
                ?? throw new InvalidOperationException("Failed to parse Character Replacer workflow JSON");

            var rng = new Random();

            UpdateNode(dict, "40", inputs => inputs["image"] = uploadedSubject); // subject = Character 1
            UpdateNode(dict, "39", inputs => inputs["image"] = uploadedPose);     // pose = base scene frame

            // Reseed every noise source so successive runs differ.
            UpdateNode(dict, "42", inputs => inputs["value"] = rng.Next(1, 2_000_000_000));        // main combine seed (PrimitiveInt)
            UpdateNode(dict, "44:187", inputs => inputs["noise_seed"] = rng.NextInt64(0, 999_999_999_999_999L)); // subject clean-up
            UpdateNode(dict, "47:199", inputs => inputs["noise_seed"] = rng.NextInt64(0, 999_999_999_999_999L)); // posed-body build
            UpdateNode(dict, "28:174", inputs => inputs["noise_seed"] = rng.NextInt64(0, 999_999_999_999_999L)); // face fix

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

        private async Task<byte[]?> RetrieveKleinImageAsync(string promptId, string savePrefix, CancellationToken token)
        {
            var baseUrl = _settingsService.Settings?.BaseUrl ?? "http://127.0.0.1:8188";
            Uri uri;
            try { uri = new Uri(baseUrl); } catch { uri = new Uri("http://127.0.0.1:8188"); }
            bool isRemote = !string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);

            const int maxRetries = 30;
            const int retryDelayMs = 5000;

            if (isRemote)
            {
                for (int i = 0; i < maxRetries; i++)
                {
                    if (i > 0) await Task.Delay(retryDelayMs, token);
                    token.ThrowIfCancellationRequested();
                    var files = await _comfyUIService.HttpClient.GetOutputFilesForPromptAsync(promptId);
                    var imgFile = files.FirstOrDefault(f =>
                        Path.GetFileName(f).StartsWith(savePrefix, StringComparison.OrdinalIgnoreCase) && IsImageExt(f));
                    imgFile ??= files.FirstOrDefault(f =>
                        IsImageExt(f) && !Path.GetFileName(f).StartsWith("ComfyUI_temp_", StringComparison.OrdinalIgnoreCase));
                    if (imgFile != null)
                    {
                        var data = await _comfyUIService.HttpClient.DownloadOutputImageAsync(imgFile);
                        if (data != null) return data;
                    }
                }
                return null;
            }

            var outputDir = _settingsService.Settings?.OutputFolderPath;
            if (string.IsNullOrEmpty(outputDir)) { AddLog("ERROR: Output folder not configured"); return null; }
            for (int i = 0; i < maxRetries; i++)
            {
                if (i > 0) await Task.Delay(retryDelayMs, token);
                token.ThrowIfCancellationRequested();
                var files = Directory.GetFiles(outputDir, $"{savePrefix}*.png", SearchOption.AllDirectories)
                    .Where(f => !Path.GetFileName(f).StartsWith("ComfyUI_temp_", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTime).ToList();
                if (files.Any())
                {
                    var latest = files[0];
                    var age = DateTime.Now - File.GetLastWriteTime(latest);
                    if (age.TotalSeconds < 180) return await File.ReadAllBytesAsync(latest, token);
                }
            }
            return null;
        }

        // Single-frame grab at an arbitrary timestamp (full resolution, PNG).
        private async Task<string?> ExtractFrameAtAsync(string videoPath, double timeSeconds, CancellationToken token)
        {
            var ffmpegPath = FindFFmpeg();
            if (ffmpegPath == null) return null;

            var tempFile = Path.Combine(Path.GetTempPath(), $"scail2_base_{Guid.NewGuid():N}.png");
            var ts = Math.Max(0, timeSeconds);

            await Task.Run(() =>
            {
                var si = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-loglevel quiet -nostats -ss {ts:F3} -i \"{videoPath}\" -frames:v 1 -q:v 2 \"{tempFile}\" -y",
                    UseShellExecute = false,
                    RedirectStandardError = false,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(si);
                if (proc != null && !proc.WaitForExit(30000))
                    try { proc.Kill(); } catch { }
            }, token);

            return File.Exists(tempFile) ? tempFile : null;
        }

        #endregion

        #region Stage B — SCAIL II motion transfer

        /// <summary>
        /// Triggered by the explicit "Generate video" button once the In/Out range is set: runs the
        /// inherited appearance/motion analysis (over the trimmed range) and enqueues the SCAIL II job,
        /// which auto-processes. No-op until the char-swap character image exists.
        /// </summary>
        public async Task OnTrimFinalizedAsync()
        {
            if (IsCharSwapping || IsProcessingQueue) return;
            if (!HasCharacterImage || !HasInputVideo) return;

            try
            {
                CharSwapStatus = "Analyzing motion and generating the video…";
                AddLog("=== Scail 2: motion-transfer stage ===");
                await AnalyzeImageAsync();
                AddAllChunksToQueue(); // enqueues all chunks → ProcessQueueAsync runs automatically
            }
            catch (Exception ex)
            {
                CharSwapStatus = $"Generate failed: {ex.Message}";
                AddLog($"ERROR (motion-transfer): {ex.Message}");
            }
        }

        #endregion

        #region Stage B — workflow (high-res-fix long video)

        // Scail 2 uses the "Long Videos High-Res Fix" SCAIL-2 workflow instead of the simple
        // single-node one inherited from WanScailGgufViewModel. It loops over the whole clip
        // internally (VideoChunkPlanner + forLoopStart/forLoopEnd, BlendVideoChunks crossfade),
        // so the C# side still runs one whole-video execution (FramesPerChunk == int.MaxValue,
        // inherited). The node layout is completely different from the simple workflow, so the
        // whole UpdateWorkflowParameters mapping is overridden below.
        protected override string WorkflowFileName =>
            Path.Combine("video", "wan", "scail2LongVideosHighResFix_v10 (1).json");

        protected override JsonElement UpdateWorkflowParameters(
            JsonElement workflow,
            string characterImageName,
            string videoName,
            int startFrame,
            int framesInChunk,
            string prompt,
            string negativePrompt,
            int fps,
            int maxEdge,
            long seed,
            int outputWidth = 0,
            int outputHeight = 0,
            WanScailQueueItem? item = null)
        {
            // Drop the terminal preview sinks (pose-mask preview 105, input-video preview 110, and
            // the SAM object-id preview 245) so the only video that lands in /history is the final
            // combine (node 238). TryGetVideoFromHistoryAsync takes the first mp4 it finds and does
            // not filter out save_output=false temp previews, so leaving these in risks returning a
            // preview clip instead of the result. They are terminal (nothing reads their output), so
            // removing them only skips preview-only rendering.
            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(workflow.GetRawText())
                ?? throw new InvalidOperationException("Failed to parse SCAIL2 hi-res workflow JSON");
            foreach (var previewNode in new[] { "105", "110", "245" })
                dict.Remove(previewNode);
            var workflowJson = JsonSerializer.Serialize(dict);

            var subject = string.IsNullOrWhiteSpace(item?.Subject) ? "person" : item!.Subject.Trim();

            // Node 39 "Activate replacement mode?" is the inverse of ReplaceBackground: replacement
            // mode keeps the original background (character only), while leaving it off regenerates
            // the whole frame (character + background).
            bool replaceBackground = item?.ReplaceBackground ?? true;
            bool replacementMode = !replaceBackground;

            // The new workflow takes a start offset and a max length in SECONDS (nodes 38 / 46) and
            // derives skip_first_frames / frame_load_cap internally, so convert the frame-based trim.
            // framesInChunk == int.MaxValue means the frame count was unknown — leave the authored
            // max-duration default in that case.
            int skipFrames = item?.TrimSkipFrames ?? 0;
            int capFrames = item?.TrimFrameCap ?? 0;
            double skipSeconds = fps > 0 && skipFrames > 0 ? skipFrames / (double)fps : 0;
            double maxDurationSeconds =
                capFrames > 0 && fps > 0 ? capFrames / (double)fps
                : (framesInChunk > 0 && framesInChunk < int.MaxValue && fps > 0 ? framesInChunk / (double)fps : 0);

            AddLog($"Updating SCAIL2 hi-res workflow: whole video, fps={fps}, subject=\"{subject}\", " +
                   $"replacementMode={replacementMode}, skip={skipSeconds:F2}s, " +
                   $"maxDuration={(maxDurationSeconds > 0 ? maxDurationSeconds.ToString("F1") + "s" : "authored")}");

            // Node 94: main character / reference image (LoadImage)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "94", "image", characterImageName);

            // Node 120: reference (driving) video. skip/cap/force_rate stay wired to helper nodes.
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "120", "video", videoName);

            // Node 40: target frame rate (easy float)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "40", "value", fps);

            // Node 41: positive prompt, Node 22: negative prompt (CLIPTextEncode)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "41", "text", prompt);
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "22", "text", negativePrompt);

            // Node 256:101: SAM3 subject to detect / track
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "256:101", "text", subject);

            // Node 39: replacement (keep background) vs. animation (regenerate everything)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "39", "value", replacementMode);

            // Node 38: skip first N seconds (trim in-point)
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "38", "value", skipSeconds);

            // Node 46: max video duration in seconds — only when we know a concrete length.
            if (maxDurationSeconds > 0)
                WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "46", "value", maxDurationSeconds);

            // Seed: drive both the global seed controller (174) and the sampler noise (145) so the
            // run is reproducible regardless of which one the sampler ultimately reads.
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "174", new Dictionary<string, object>
            {
                { "value", seed },
                { "last_seed", seed }
            });
            WorkflowNodeUpdater.UpdateNodeInput(ref workflowJson, "145", "noise_seed", seed);

            // Node 238: final combine — match the frame rate and pin into the wan_scail subfolder so
            // the filesystem-polling fallback (OutputSubfolder = "wan_scail") can find it.
            WorkflowNodeUpdater.UpdateNodeInputMultiple(ref workflowJson, "238", new Dictionary<string, object>
            {
                { "frame_rate", fps },
                { "filename_prefix", "wan_scail/SCAIL2_hires" },
                { "save_output", true }
            });

            AddLog("✓ SCAIL2 hi-res workflow nodes updated");
            return JsonSerializer.Deserialize<JsonElement>(workflowJson);
        }

        #endregion

        #region Helpers

        private static BitmapImage LoadBitmap(string path)
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        private static bool IsImageExt(string f) =>
            f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);

        #endregion
    }
}
