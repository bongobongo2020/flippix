using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace FlipPix.Core.Models;

/// <summary>
/// Every dial of 🌀 MiniMax I2V's <b>🌹 ErosMax Turbo 2-Pass</b> workflow — "H3 Turbo 2-Pass ErosMax b7
/// (experimental, RefMods v3)", converted to h3-erosmax-2pass.json by tools/convert_h3_erosmax_2pass.py.
/// Persisted in settings, and cloned onto each queue item at Add to Queue so a queue drained after the card
/// moved still renders what was queued.
///
/// <para><b>Every default is the authored workflow's value</b>, so the card opens exactly on the graph its
/// author tuned and <see cref="Reset"/> puts it back. The node each value lands on is named beside it.</para>
///
/// <para>The two lists (<see cref="Loras"/>, <see cref="RefMods"/>) are deep-copied by <see cref="Clone"/>;
/// everything else is a plain value the reflection copy handles.</para>
/// </summary>
public class MiniMaxI2VTwoPassOptions : INotifyPropertyChanged
{
    // ── Authored values ─────────────────────────────────────────────────────────────────────────────
    public const string AuthoredDiffusionModel = "h3-minimax/10Eros_Max_h3_8stepTURBO_beta7_int8HQ.safetensors";
    public const string AuthoredTextEncoder = "qwen3vl_32b_minimax_h3-w4a8_convrot.safetensors";
    public const string AuthoredVideoVae = "minimax_h3_video_vae_int8_convrot.safetensors";
    /// <summary>The Turbo LoRA, by file name. The author keeps it under Minimax_H3/; the tab resolves it
    /// against whatever loras/ the server lists before it is submitted.</summary>
    public const string AuthoredTurboLora = "minimax_h3_fl2v_lightx2v_turbo_4step_v0.1_comfy.safetensors";
    public const string AuthoredAspectRatio = "4:3 (Standard)";
    public const string AuthoredLatentUpscaler = "minimax_h3_latent_upscaler_3d_fp16.safetensors";
    public const string AuthoredManualSigmas = "0.9035, 0.6316, 0.3158, 0.000";
    public const int MaxLoras = 8;
    public const int MaxRefMods = 8;

    private string _diffusionModel = AuthoredDiffusionModel;
    private string _textEncoder = AuthoredTextEncoder;
    private string _videoVae = AuthoredVideoVae;
    private bool _useTurboLora;
    private string _turboLora = AuthoredTurboLora;
    private double _turboLoraStrength = 1.0;
    private string _quantizedLayers = "auto";
    private string _adalnPort = "auto";

    private string _aspectRatio = AuthoredAspectRatio;
    private double _firstPassMegapixels = 0.3;
    private double _finalMegapixels = 1.0;
    private double _durationSeconds = 15.0;
    private bool _maxFidelityReferences;

    private string _sampler = "er_sde";
    private string _firstPassScheduler = "sgm_uniform";
    private int _firstPassSteps = 8;
    private string _secondPassScheduler = "beta";
    private int _secondPassSteps = 3;
    private double _secondPassDenoise = 0.4;
    private bool _useManualSigmas;
    private string _manualSigmas = AuthoredManualSigmas;
    private double _shiftVideo = 12.0;
    private double _shiftAudio = 3.0;
    private string _latentUpscaler = AuthoredLatentUpscaler;
    private bool _previewOnly;
    private bool _useFirstPassAudio = true;
    private bool _livePreviewRefiningPass = true;

    private string _attentionBackend = "comfy kitchen attention";
    private bool _useSparseAttention = true;
    private string _sparseMethod = "vsa";
    private double _sparseKeepPercent = 10.0;
    private double _sparseTau = 1.3;
    private double _sparseStart;
    private double _sparseEnd = 1.0;
    private string _denseBlocks = "0,1";
    private string _sinkConditioning = "exact_kv_and_rows";

    private bool _useRtxUpscale = true;
    private double _rtxScale = 1.2;
    private string _rtxQuality = "ULTRA";
    private int _frameGenMultiplier = 1;
    private string _frameGenMode = "HIGH";
    private double _grainStrength = 0.005;
    private int _crf = 18;
    private string _encoderPreset = "medium";

    private bool _continueMode;
    private bool _previousHasAudio = true;
    private bool _useLastFrameReference;
    private bool _useSourceAudioGuide = true;
    private int _guideFrames = 56;

    private double _refModRetention = 1.0;
    private string _refModCurveDirection = "constant";
    private string _refModCurveShape = "linear";
    private double _refModCurveValue = 1.0;
    private int _refModScrambleSeed = -1;
    private int _refModMaxTokens;

    // ── Models (30 UNETLoader, 15 CLIPLoader, 3 video VAE, 36 Turbo LoRA, 101 H3PowerLoraStack) ──────
    public string DiffusionModel { get => _diffusionModel; set => Set(ref _diffusionModel, value); }
    public string TextEncoder { get => _textEncoder; set => Set(ref _textEncoder, value); }
    public string VideoVae { get => _videoVae; set => Set(ref _videoVae, value); }

    /// <summary>36 "Load TURBO LoRA (skip if model includes it)" — bypassed as authored: b7 has it baked in.</summary>
    public bool UseTurboLora { get => _useTurboLora; set => Set(ref _useTurboLora, value); }
    public string TurboLora { get => _turboLora; set => Set(ref _turboLora, value); }
    public double TurboLoraStrength { get => _turboLoraStrength; set => Set(ref _turboLoraStrength, value); }

    /// <summary>H3PowerLoraStack rows, in load order. The authored row (RealismSlider) ships off, so the
    /// list opens empty.</summary>
    public List<MiniMaxI2VLora> Loras { get; set; } = new();
    public string QuantizedLayers { get => _quantizedLayers; set => Set(ref _quantizedLayers, value); }
    public string AdalnPort { get => _adalnPort; set => Set(ref _adalnPort, value); }

    // ── Canvas and length (20 ResolutionSelector, 23 final MP, 17 duration → 151:19 frames) ─────────
    public string AspectRatio { get => _aspectRatio; set => Set(ref _aspectRatio, value); }
    public double FirstPassMegapixels { get => _firstPassMegapixels; set => Set(ref _firstPassMegapixels, value); }
    public double FinalMegapixels { get => _finalMegapixels; set => Set(ref _finalMegapixels, value); }
    public double DurationSeconds { get => _durationSeconds; set => Set(ref _durationSeconds, value); }

    /// <summary>151:11 ref_image_size: "match" as authored, "max" when on.</summary>
    public bool MaxFidelityReferences { get => _maxFidelityReferences; set => Set(ref _maxFidelityReferences, value); }

    // ── Sampling (18 sampler, 24 pass-1 scheduler, 102 / 185 pass-2 sigmas, 38 shift, 151:26 upscaler) ─
    public string Sampler { get => _sampler; set => Set(ref _sampler, value); }
    public string FirstPassScheduler { get => _firstPassScheduler; set => Set(ref _firstPassScheduler, value); }
    public int FirstPassSteps { get => _firstPassSteps; set => Set(ref _firstPassSteps, value); }
    public string SecondPassScheduler { get => _secondPassScheduler; set => Set(ref _secondPassScheduler, value); }
    public int SecondPassSteps { get => _secondPassSteps; set => Set(ref _secondPassSteps, value); }
    public double SecondPassDenoise { get => _secondPassDenoise; set => Set(ref _secondPassDenoise, value); }

    /// <summary>"Use Scheduler or manual, not both": on reads 185 ManualSigmas instead of 102.</summary>
    public bool UseManualSigmas { get => _useManualSigmas; set => Set(ref _useManualSigmas, value); }
    public string ManualSigmas { get => _manualSigmas; set => Set(ref _manualSigmas, value); }
    public double ShiftVideo { get => _shiftVideo; set => Set(ref _shiftVideo, value); }
    public double ShiftAudio { get => _shiftAudio; set => Set(ref _shiftAudio, value); }
    public string LatentUpscaler { get => _latentUpscaler; set => Set(ref _latentUpscaler, value); }

    /// <summary>94 "Preview - skip second pass".</summary>
    public bool PreviewOnly { get => _previewOnly; set => Set(ref _previewOnly, value); }

    /// <summary>87 "Use first pass audio?" — on as authored. Preview always uses it.</summary>
    public bool UseFirstPassAudio { get => _useFirstPassAudio; set => Set(ref _useFirstPassAudio, value); }

    /// <summary>Not in the authored graph: a copy of its ModelPreviewOverrideKJ on the second pass, so the
    /// live preview keeps sharpening through the refinement. Display only — the render is unchanged.</summary>
    public bool LivePreviewRefiningPass { get => _livePreviewRefiningPass; set => Set(ref _livePreviewRefiningPass, value); }

    // ── Attention (37 ModelAttentionBackend, 100 BlockSparseAttention) ──────────────────────────────
    public string AttentionBackend { get => _attentionBackend; set => Set(ref _attentionBackend, value); }

    /// <summary>"Default/fast: VSA enabled … Quality: sparse node bypassed."</summary>
    public bool UseSparseAttention { get => _useSparseAttention; set => Set(ref _useSparseAttention, value); }
    public string SparseMethod { get => _sparseMethod; set => Set(ref _sparseMethod, value); }
    /// <summary>keep_percent, for vsa and sla.</summary>
    public double SparseKeepPercent { get => _sparseKeepPercent; set => Set(ref _sparseKeepPercent, value); }
    /// <summary>tau, for sol-attn.</summary>
    public double SparseTau { get => _sparseTau; set => Set(ref _sparseTau, value); }
    public double SparseStart { get => _sparseStart; set => Set(ref _sparseStart, value); }
    public double SparseEnd { get => _sparseEnd; set => Set(ref _sparseEnd, value); }
    public string DenseBlocks { get => _denseBlocks; set => SetText(ref _denseBlocks, value); }
    public string SinkConditioning { get => _sinkConditioning; set => Set(ref _sinkConditioning, value); }

    // ── Output (39 RTX SR, 173/155 FrameGen, 174 grain, 152:135/136 Lokken encode) ──────────────────
    public bool UseRtxUpscale { get => _useRtxUpscale; set => Set(ref _useRtxUpscale, value); }
    public double RtxScale { get => _rtxScale; set => Set(ref _rtxScale, value); }
    public string RtxQuality { get => _rtxQuality; set => Set(ref _rtxQuality, value); }

    /// <summary>173 "FrameGen multiplier: 1 = OFF; 2–5 = 48–120 FPS". Normal mode only.</summary>
    public int FrameGenMultiplier { get => _frameGenMultiplier; set => Set(ref _frameGenMultiplier, value); }
    public string FrameGenMode { get => _frameGenMode; set => Set(ref _frameGenMode, value); }

    /// <summary>174 "Output grain strength — 0 = OFF" (Lokken).</summary>
    public double GrainStrength { get => _grainStrength; set => Set(ref _grainStrength, value); }
    public int Crf { get => _crf; set => Set(ref _crf, value); }

    /// <summary>178 "Output saving quality — encoder preset".</summary>
    public string EncoderPreset { get => _encoderPreset; set => Set(ref _encoderPreset, value); }

    // ── Continue a video (103, 117, 131, 144, 153 guide window) ─────────────────────────────────────

    /// <summary>103 "Normal / Continue". Not persisted: a session opens on Normal, so a take is never
    /// silently queued as an extension of a video picked days ago.</summary>
    [JsonIgnore]
    public bool ContinueMode { get => _continueMode; set => Set(ref _continueMode, value); }
    public bool PreviousHasAudio { get => _previousHasAudio; set => Set(ref _previousHasAudio, value); }
    public bool UseLastFrameReference { get => _useLastFrameReference; set => Set(ref _useLastFrameReference, value); }
    public bool UseSourceAudioGuide { get => _useSourceAudioGuide; set => Set(ref _useSourceAudioGuide, value); }

    /// <summary>Guide window, in frames: 22, 39 or 56 (56 as authored).</summary>
    public int GuideFrames { get => _guideFrames; set => Set(ref _guideFrames, value); }

    // ── RefMods (181 MiniMaxH3RefModsLoader, 151:182 MiniMaxH3RefModApply) ──────────────────────────
    public List<MiniMaxI2VRefModSlot> RefMods { get; set; } = new();
    public double RefModRetention { get => _refModRetention; set => Set(ref _refModRetention, value); }
    public string RefModCurveDirection { get => _refModCurveDirection; set => Set(ref _refModCurveDirection, value); }
    public string RefModCurveShape { get => _refModCurveShape; set => Set(ref _refModCurveShape, value); }
    public double RefModCurveValue { get => _refModCurveValue; set => Set(ref _refModCurveValue, value); }
    public int RefModScrambleSeed { get => _refModScrambleSeed; set => Set(ref _refModScrambleSeed, value); }
    public int RefModMaxTokens { get => _refModMaxTokens; set => Set(ref _refModMaxTokens, value); }

    /// <summary>The RefMod slots that reach the graph: a file chosen, at a strength above 0.</summary>
    [JsonIgnore]
    public IReadOnlyList<MiniMaxI2VRefModSlot> ActiveRefMods =>
        RefMods.Where(m => m.IsActive).Take(MaxRefMods).ToList();

    /// <summary>Whether every value is the authored workflow's — what the card's "as tested" badge reads.
    /// The continue switch is a per-take choice, not a dial, and is left out.</summary>
    [JsonIgnore]
    public bool IsAuthored
    {
        get
        {
            var fresh = new MiniMaxI2VTwoPassOptions();
            foreach (var p in ScalarProperties())
            {
                if (p.Name == nameof(ContinueMode)) continue;
                if (!Equals(p.GetValue(this), p.GetValue(fresh))) return false;
            }
            return Loras.Count(l => !string.IsNullOrWhiteSpace(l.Name) && l.Strength > 0) == 0
                   && ActiveRefMods.Count == 0;
        }
    }

    /// <summary>Puts every dial back on the authored workflow's value. The continue switch stays put.</summary>
    public void Reset()
    {
        var keepContinue = ContinueMode;
        CopyFrom(new MiniMaxI2VTwoPassOptions());
        ContinueMode = keepContinue;
    }

    /// <summary>An independent copy, for freezing onto a queue item.</summary>
    public MiniMaxI2VTwoPassOptions Clone()
    {
        var copy = new MiniMaxI2VTwoPassOptions();
        copy.CopyFrom(this);
        return copy;
    }

    public void CopyFrom(MiniMaxI2VTwoPassOptions other)
    {
        foreach (var p in ScalarProperties())
            p.SetValue(this, p.GetValue(other));
        Loras = other.Loras.Select(l => new MiniMaxI2VLora { Name = l.Name, Strength = l.Strength }).ToList();
        RefMods = other.RefMods.Select(m => m.Clone()).ToList();
        Refresh(nameof(Loras));
        Refresh(nameof(RefMods));
    }

    private static IEnumerable<System.Reflection.PropertyInfo> ScalarProperties() =>
        typeof(MiniMaxI2VTwoPassOptions).GetProperties().Where(p =>
            p.CanWrite && p.GetIndexParameters().Length == 0 &&
            !p.PropertyType.IsGenericType);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Re-announces a property, for a view whose list was rebuilt under an unchanged value, and
    /// for the two lists, which are edited in place.</summary>
    public void Refresh(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAuthored)));
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        // A slider snapped to 0.05 hands back 0.6000000000000001; four places is finer than any widget here
        // and keeps "is this the authored value" an honest comparison.
        if (value is double d) value = (T)(object)System.Math.Round(d, 4);
        // A ComboBox whose list is being rebuilt pushes null/empty back through its two-way binding; a blank
        // name is never a choice anyone made, so it is not allowed to overwrite one.
        if (value is null || value is string { Length: 0 }) return;
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAuthored)));
    }

    /// <summary>For free-text fields, where empty is a real value ("no dense blocks").</summary>
    private void SetText(ref string field, string? value, [CallerMemberName] string? name = null)
    {
        value ??= string.Empty;
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAuthored)));
    }
}

/// <summary>
/// One slot of the ErosMax 2-Pass workflow's MiniMaxH3RefModsLoader: a saved reference from
/// <c>models/refmods/</c>, how strongly and how many times it is injected, and which of its halves load.
/// "(none)" or strength 0 disables the slot, as on the node.
/// </summary>
public class MiniMaxI2VRefModSlot : INotifyPropertyChanged
{
    public const string None = "(none)";

    private string _mod = None;
    private double _strength = 1.0;
    private int _copies = 1;
    private string _components = "All";
    private double _visualStrength = 1.0;
    private double _audioStrength = 1.0;

    public string Mod { get => _mod; set => Set(ref _mod, string.IsNullOrWhiteSpace(value) ? None : value); }
    public double Strength { get => _strength; set => Set(ref _strength, System.Math.Clamp(System.Math.Round(value, 2), 0, 1)); }
    public int Copies { get => _copies; set => Set(ref _copies, System.Math.Clamp(value, 1, 10)); }

    /// <summary>"All", "Visual" or "Audio".</summary>
    public string Components { get => _components; set => Set(ref _components, string.IsNullOrWhiteSpace(value) ? "All" : value); }
    public double VisualStrength { get => _visualStrength; set => Set(ref _visualStrength, System.Math.Clamp(System.Math.Round(value, 2), 0, 1)); }
    public double AudioStrength { get => _audioStrength; set => Set(ref _audioStrength, System.Math.Clamp(System.Math.Round(value, 2), 0, 1)); }

    [JsonIgnore]
    public bool IsActive => _mod != None && _strength > 0;

    public MiniMaxI2VRefModSlot Clone() => new()
    {
        Mod = Mod, Strength = Strength, Copies = Copies, Components = Components,
        VisualStrength = VisualStrength, AudioStrength = AudioStrength,
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
    }
}
