using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace FlipPix.Core.Models;

/// <summary>
/// Every dial of 🌀 MiniMax I2V's <b>All-In-One Ultra (Full-Res Tiled)</b> workflow — the one graph on
/// that tab that is not h3-minimax-i2v.json. Persisted in settings, and copied onto each queue item at
/// Add to Queue so a queue drained after the card moved still renders what was queued.
///
/// <para><b>Every default is the authored workflow's value</b> (workflow/video/h3-minimax/Minimax H3
/// All-In-One Ultra (Full-Res Tiled).json, converted to h3-minimax-ultra.json by
/// tools/convert_h3_ultra.py). That graph was tested on the server for best quality, so the card opens
/// exactly on it and <see cref="Reset"/> puts it back. The node each value lands on is named beside it.</para>
///
/// <para>Notifies on change so the tab can bind straight to it and persist on every edit.</para>
/// </summary>
public class MiniMaxI2VUltraOptions : INotifyPropertyChanged
{
    // ── Authored values ─────────────────────────────────────────────────────────────────────────────
    public const string AuthoredDiffusionModel = "h3-minimax/minimax_h3_hybrid_fl2va_ref2va_b25-49-int8.safetensors";
    public const string AuthoredTextEncoder = "qwen3vl_32b_minimax_h3_nvfp4_awq.safetensors";
    /// <summary>The second CLIPLoader in the authored graph, shipped bypassed.</summary>
    public const string AlternateTextEncoder = "qwen3vl_32b_minimax_h3_ultra_uncensored_heretic_int8_convrot.safetensors";
    public const string AuthoredAspectRatio = "16:9 (Widescreen)";
    public const double AuthoredMegapixels = 2.0;
    public const double AuthoredDurationSeconds = 15.0;
    public const string AuthoredAttention = "comfy kitchen attention";
    public const string MysticLora = "H3/MysticXXX_MMH3-V4.safetensors";
    public const string DmadLora = "H3/minimax_h3_dmad_full_dareties_v4_step600_comfy_fro.safetensors";
    public const string CombatLora = "H3/H3_Combat_V2.safetensors";
    public const string MotionRepairLora = "H3/Motion_Repair_V2.safetensors";
    public const string FightFlowLora = "H3/H3_Fight_Flow_V1.safetensors";
    public const string SpeedSliderLora = "H3/H3_speed_slider_1.1.safetensors";
    public const string AuthoredUpscaler = "h3_upscaler_lms_v0.1.safetensors";

    private string _diffusionModel = AuthoredDiffusionModel;
    private string _textEncoder = AuthoredTextEncoder;
    private string _aspectRatio = AuthoredAspectRatio;
    private double _megapixels = AuthoredMegapixels;
    private double _durationSeconds = AuthoredDurationSeconds;
    private bool _maxFidelityReferences = true;
    private string _attentionBackend = AuthoredAttention;
    private bool _useSparseAttention = true;
    private double _sparseTau = 1.3;
    private double _sparseStart = 0.2;
    private double _sparseEnd = 1.0;
    private bool _useMystic = true;
    private double _mysticStrength = 0.5;
    private bool _useDmad = true;
    private double _dmadStrength = 0.75;
    private bool _highMotionFix;
    private double _combatStrength = 0.65;
    private double _motionRepairStrength = 0.35;
    private double _fightFlowStrength = 0.35;
    private double _speedSliderStrength = 3.0;
    private double _shiftVideo = 12.0;
    private double _shiftAudio = 3.0;
    private string _scheduler = "beta";
    private int _steps = 10;
    private double _cfg = 1.0;
    private int _transitionStep = 6;
    private double _lowresScale = 0.6;
    private double _rho = 0.0;
    private double _wMin = 0.5;
    private double _wMax = 1.0;
    private string _upscalerModel = AuthoredUpscaler;
    private bool _highresTiling = true;
    private bool _upscalerUnload = true;
    private bool _lowVramBlockSwap;
    private int _blockSwapBlocks = -1;

    // ── Models (100 UNETLoader, 33 CLIPLoader) ──────────────────────────────────────────────────────
    public string DiffusionModel { get => _diffusionModel; set => Set(ref _diffusionModel, value); }
    public string TextEncoder { get => _textEncoder; set => Set(ref _textEncoder, value); }

    // ── Canvas and length (112 ResolutionSelector, 111 Duration → 66 frame expression) ──────────────
    public string AspectRatio { get => _aspectRatio; set => Set(ref _aspectRatio, value); }
    public double Megapixels { get => _megapixels; set => Set(ref _megapixels, value); }
    public double DurationSeconds { get => _durationSeconds; set => Set(ref _durationSeconds, value); }

    /// <summary>121 MiniMaxH3ReferenceToVideo.ref_image_size: "max" as authored, "match" when off.</summary>
    public bool MaxFidelityReferences { get => _maxFidelityReferences; set => Set(ref _maxFidelityReferences, value); }

    // ── Attention (120 ModelAttentionBackend, 204 BlockSparseAttention sol-attn) ────────────────────
    public string AttentionBackend { get => _attentionBackend; set => Set(ref _attentionBackend, value); }
    public bool UseSparseAttention { get => _useSparseAttention; set => Set(ref _useSparseAttention, value); }
    public double SparseTau { get => _sparseTau; set => Set(ref _sparseTau, value); }
    public double SparseStart { get => _sparseStart; set => Set(ref _sparseStart, value); }
    public double SparseEnd { get => _sparseEnd; set => Set(ref _sparseEnd, value); }

    // ── LoRAs (186 MysticXXX, 242 DMAD; High motion fix group 215/217/216/218 behind switch 222) ────
    public bool UseMystic { get => _useMystic; set => Set(ref _useMystic, value); }
    public double MysticStrength { get => _mysticStrength; set => Set(ref _mysticStrength, value); }
    public bool UseDmad { get => _useDmad; set => Set(ref _useDmad, value); }
    public double DmadStrength { get => _dmadStrength; set => Set(ref _dmadStrength, value); }

    /// <summary>223 "Boolean(Fighting High Motion Fix)" — off as authored.</summary>
    public bool HighMotionFix { get => _highMotionFix; set => Set(ref _highMotionFix, value); }
    public double CombatStrength { get => _combatStrength; set => Set(ref _combatStrength, value); }
    public double MotionRepairStrength { get => _motionRepairStrength; set => Set(ref _motionRepairStrength, value); }
    public double FightFlowStrength { get => _fightFlowStrength; set => Set(ref _fightFlowStrength, value); }
    public double SpeedSliderStrength { get => _speedSliderStrength; set => Set(ref _speedSliderStrength, value); }

    // ── Sigma shift (219) ───────────────────────────────────────────────────────────────────────────
    public double ShiftVideo { get => _shiftVideo; set => Set(ref _shiftVideo, value); }
    public double ShiftAudio { get => _shiftAudio; set => Set(ref _shiftAudio, value); }

    // ── Schedule (165 BasicScheduler; 164 KSamplerSelect stays euler — SelfLift is Euler-only) ──────
    public string Scheduler { get => _scheduler; set => Set(ref _scheduler, value); }
    public int Steps { get => _steps; set => Set(ref _steps, value); }

    // ── SelfLift (224 SelfLiftH3Sampler) ────────────────────────────────────────────────────────────
    public double Cfg { get => _cfg; set => Set(ref _cfg, value); }
    public int TransitionStep { get => _transitionStep; set => Set(ref _transitionStep, value); }
    public double LowresScale { get => _lowresScale; set => Set(ref _lowresScale, value); }
    public double Rho { get => _rho; set => Set(ref _rho, value); }
    public double WMin { get => _wMin; set => Set(ref _wMin, value); }
    public double WMax { get => _wMax; set => Set(ref _wMax, value); }
    public string UpscalerModel { get => _upscalerModel; set => Set(ref _upscalerModel, value); }
    public bool HighresTiling { get => _highresTiling; set => Set(ref _highresTiling, value); }
    public bool UpscalerUnload { get => _upscalerUnload; set => Set(ref _upscalerUnload, value); }

    // ── Low VRAM (138 UniBlockSwap — authored bypassed) ─────────────────────────────────────────────
    public bool LowVramBlockSwap { get => _lowVramBlockSwap; set => Set(ref _lowVramBlockSwap, value); }
    public int BlockSwapBlocks { get => _blockSwapBlocks; set => Set(ref _blockSwapBlocks, value); }

    /// <summary>Whether every value is the authored workflow's — what the card's "as tested" badge reads.</summary>
    [JsonIgnore]
    public bool IsAuthored
    {
        get
        {
            var fresh = new MiniMaxI2VUltraOptions();
            foreach (var p in typeof(MiniMaxI2VUltraOptions).GetProperties())
            {
                if (!p.CanWrite || p.GetIndexParameters().Length > 0) continue;
                if (!Equals(p.GetValue(this), p.GetValue(fresh))) return false;
            }
            return true;
        }
    }

    /// <summary>Puts every dial back on the authored workflow's value.</summary>
    public void Reset() => CopyFrom(new MiniMaxI2VUltraOptions());

    /// <summary>An independent copy, for freezing onto a queue item.</summary>
    public MiniMaxI2VUltraOptions Clone()
    {
        var copy = new MiniMaxI2VUltraOptions();
        copy.CopyFrom(this);
        return copy;
    }

    public void CopyFrom(MiniMaxI2VUltraOptions other)
    {
        foreach (var p in typeof(MiniMaxI2VUltraOptions).GetProperties())
        {
            if (!p.CanWrite || p.GetIndexParameters().Length > 0) continue;
            p.SetValue(this, p.GetValue(other));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Re-announces a property, for a view whose list was rebuilt under an unchanged value.</summary>
    public void Refresh(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

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
}
