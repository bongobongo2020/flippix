using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlipPix.Core.Models;
using FlipPix.UI.Models;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// Spoken lines voiced by the Yakkinator running on this PC. Each beat has a line, an engine and a voice;
    /// 🗣 on a beat sends its line to Yakkinator and the WAV that comes back becomes the beat's dialogue — the
    /// same as picking a file with 🎙 Browse. 🗣 Send all does every beat with a line, one after another.
    /// </summary>
    public partial class H3FflfViewModel
    {
        private readonly YakkinatorClient _yak = new();
        private readonly Dictionary<string, IReadOnlyList<string>> _yakVoiceCache = new();
        private CancellationTokenSource? _voiceCts;

        public IReadOnlyList<YakEngine> YakEngines => YakkinatorClient.Engines;

        /// <summary>The engine and voice the page voices with — changing either sets it on every beat.</summary>
        [ObservableProperty] private string _selectedYakEngine = YakkinatorClient.IndexTts;
        [ObservableProperty] private string _selectedYakVoice = string.Empty;
        [ObservableProperty] private IReadOnlyList<string> _yakVoices = Array.Empty<string>();
        [ObservableProperty] private string _yakStatus = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SendAllLabel), nameof(CanPickYakVoice))]
        private bool _isSendingAll;

        public bool CanPickYakVoice => !IsSendingAll;

        public string SendAllLabel => IsSendingAll ? "⏳ Voicing…" : "🗣 Send all";

        public RelayCommand<FflfBeat> GenerateLineCommand { get; private set; } = null!;
        public RelayCommand SendAllLinesCommand { get; private set; } = null!;
        public RelayCommand StopVoicingCommand { get; private set; } = null!;
        public RelayCommand RefreshYakVoicesCommand { get; private set; } = null!;

        private bool AnyVoicing => Beats.Any(b => b.IsVoicing);

        /// <summary>Runs in the constructor, while the page is still loading.</summary>
        private void InitVoice(H3FflfSettings s)
        {
            GenerateLineCommand = new RelayCommand<FflfBeat>(b => { if (b != null) _ = GenerateLineAsync(b); },
                b => b != null && !b.IsVoicing && !b.IsRendering && !IsSendingAll && !string.IsNullOrWhiteSpace(b.LineText));
            SendAllLinesCommand = new RelayCommand(() => _ = SendAllLinesAsync(),
                () => !IsSendingAll && !AnyVoicing && Beats.Any(b => !string.IsNullOrWhiteSpace(b.LineText)));
            StopVoicingCommand = new RelayCommand(() => _voiceCts?.Cancel(), () => IsSendingAll || AnyVoicing);
            RefreshYakVoicesCommand = new RelayCommand(() => { _yakVoiceCache.Clear(); _ = LoadYakVoicesAsync(userAsked: true); });

            // Fields, not properties: still loading, so nothing is copied onto the beats or saved back.
#pragma warning disable MVVMTK0034
            _selectedYakEngine = YakEngines.Any(e => e.Id == s.YakEngine) ? s.YakEngine : YakkinatorClient.IndexTts;
            _selectedYakVoice = s.YakVoice ?? string.Empty;
#pragma warning restore MVVMTK0034
            _ = LoadYakVoicesAsync(userAsked: false);
        }

        private void RefreshVoiceCommands()
        {
            GenerateLineCommand?.NotifyCanExecuteChanged();
            SendAllLinesCommand?.NotifyCanExecuteChanged();
            StopVoicingCommand?.NotifyCanExecuteChanged();
        }

        partial void OnSelectedYakEngineChanged(string value)
        {
            if (IsLoading) return;
            foreach (var b in Beats.Where(b => !b.IsVoicing)) b.TtsEngine = value;
            _ = LoadYakVoicesAsync(userAsked: false);
        }

        partial void OnSelectedYakVoiceChanged(string value)
        {
            if (IsLoading || string.IsNullOrEmpty(value)) return;
            foreach (var b in Beats.Where(b => !b.IsVoicing && b.TtsEngine == SelectedYakEngine)) b.TtsVoice = value;
        }

        /// <summary>A beat that joins the page: voiced with the page's engine and voice unless it has its own.</summary>
        private void AdoptVoice(FflfBeat beat)
        {
            if (string.IsNullOrEmpty(beat.TtsEngine)) beat.TtsEngine = SelectedYakEngine;
            if (string.IsNullOrEmpty(beat.TtsVoice) && beat.TtsEngine == SelectedYakEngine) beat.TtsVoice = SelectedYakVoice;
            if (_yakVoiceCache.TryGetValue(beat.TtsEngine, out var voices)) beat.TtsVoiceOptions = voices;
            else _ = VoicesForAsync(beat.TtsEngine, userAsked: false);
        }

        /// <summary>A beat's line, engine or voice edited on its card.</summary>
        private void OnBeatVoiceChanged(FflfBeat beat, string? property)
        {
            switch (property)
            {
                case nameof(FflfBeat.TtsEngine):
                    if (_yakVoiceCache.TryGetValue(beat.TtsEngine, out var voices)) ApplyOptions(beat, voices);
                    else _ = VoicesForAsync(beat.TtsEngine, userAsked: false);
                    break;
                case nameof(FflfBeat.LineText):
                case nameof(FflfBeat.TtsVoice):
                    break;
                case nameof(FflfBeat.IsVoicing):
                    RefreshVoiceCommands();
                    return;
                default:
                    return;
            }
            RefreshVoiceCommands();
            if (!IsLoading) ScheduleStorySave();
        }

        private static void ApplyOptions(FflfBeat beat, IReadOnlyList<string> voices)
        {
            beat.TtsVoiceOptions = voices;
            if (voices.Count > 0 && !voices.Contains(beat.TtsVoice, StringComparer.OrdinalIgnoreCase))
                beat.TtsVoice = voices[0];
        }

        private async Task LoadYakVoicesAsync(bool userAsked)
        {
            var engine = SelectedYakEngine;
            var voices = await VoicesForAsync(engine, userAsked);
            if (engine != SelectedYakEngine) return;
            YakVoices = voices;
            if (voices.Count > 0 && !voices.Contains(SelectedYakVoice, StringComparer.OrdinalIgnoreCase))
                SelectedYakVoice = voices[0];
            if (userAsked && voices.Count > 0)
                AddLog($"Yakkinator: {voices.Count} {Name(engine)} voice{(voices.Count == 1 ? "" : "s")}.");
        }

        /// <summary>The voices of one engine, read once and handed to every beat that uses it.</summary>
        private async Task<IReadOnlyList<string>> VoicesForAsync(string engine, bool userAsked)
        {
            if (string.IsNullOrEmpty(engine)) return Array.Empty<string>();
            if (!_yakVoiceCache.TryGetValue(engine, out var voices))
            {
                try
                {
                    voices = await _yak.GetVoicesAsync(engine);
                    _yakVoiceCache[engine] = voices;
                    YakStatus = voices.Count == 0
                        ? $"No {Name(engine)} voices — add one to Yakkinator's voice library."
                        : "Yakkinator connected.";
                }
                catch (Exception ex)
                {
                    YakStatus = ex is YakkinatorException ? ex.Message : $"Yakkinator: {ex.Message}";
                    if (userAsked) AddLog(YakStatus);
                    return Array.Empty<string>();
                }
            }
            OnUi(() => { foreach (var b in Beats.Where(b => b.TtsEngine == engine)) ApplyOptions(b, voices); });
            return voices;
        }

        private static string Name(string engine) => YakkinatorClient.Engines.FirstOrDefault(e => e.Id == engine)?.Name ?? engine;

        private static string VoiceKey(FflfBeat b) => $"{b.TtsEngine}|{b.TtsVoice}|{b.LineText.Trim()}";

        private async Task GenerateLineAsync(FflfBeat beat)
        {
            _voiceCts ??= new CancellationTokenSource();
            var cts = _voiceCts;
            try { await VoiceBeatAsync(beat, cts.Token); }
            finally { if (!AnyVoicing && !IsSendingAll && ReferenceEquals(_voiceCts, cts)) { _voiceCts = null; cts.Dispose(); } }
        }

        /// <summary>🗣 Send all: every beat with a line, in order. A beat whose audio already came from the same
        /// line, engine and voice is left as it is.</summary>
        private async Task SendAllLinesAsync()
        {
            var todo = Beats.Where(b => !string.IsNullOrWhiteSpace(b.LineText)).ToList();
            if (todo.Count == 0) return;
            _voiceCts?.Dispose();
            _voiceCts = new CancellationTokenSource();
            var ct = _voiceCts.Token;
            IsSendingAll = true;
            RefreshVoiceCommands();
            int voiced = 0, skipped = 0, failed = 0;
            AddLog($"🗣 Sending {todo.Count} line{(todo.Count == 1 ? "" : "s")} to Yakkinator…");
            try
            {
                foreach (var beat in todo)
                {
                    if (ct.IsCancellationRequested) break;
                    if (beat.HasDialogue && beat.VoicedFor == VoiceKey(beat)) { skipped++; continue; }
                    if (beat.IsRendering)
                    {
                        AddLog($"{beat.Label}: rendering right now — voice it once it lands.");
                        failed++;
                        continue;
                    }
                    if (await VoiceBeatAsync(beat, ct)) voiced++;
                    else if (!ct.IsCancellationRequested) failed++;
                }
            }
            finally
            {
                IsSendingAll = false;
                _voiceCts?.Dispose();
                _voiceCts = null;
                RefreshVoiceCommands();
            }
            var summary = $"🗣 Send all: {voiced} voiced" +
                          (skipped > 0 ? $", {skipped} already up to date" : string.Empty) +
                          (failed > 0 ? $", {failed} not voiced" : string.Empty) +
                          (ct.IsCancellationRequested ? " — stopped." : ".");
            YakStatus = summary;
            AddLog(summary);
        }

        /// <summary>Sends one beat's line to Yakkinator and makes the WAV that comes back its dialogue.</summary>
        private async Task<bool> VoiceBeatAsync(FflfBeat beat, CancellationToken ct)
        {
            var line = beat.LineText.Trim();
            if (line.Length == 0) return false;
            if (string.IsNullOrEmpty(beat.TtsEngine)) beat.TtsEngine = SelectedYakEngine;
            if (string.IsNullOrEmpty(beat.TtsVoice))
            {
                beat.Status = "🗣 Pick a voice first.";
                AddLog($"{beat.Label}: pick a Yakkinator voice for the line.");
                return false;
            }

            var key = VoiceKey(beat);
            var engine = beat.TtsEngine;
            var voice = beat.TtsVoice;
            var folder = Path.Combine(ScenePromptLibrary.FolderFor(Profile.LibraryKey), "dialogue");
            var path = Path.Combine(folder, $"beat{beat.Number:00}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.wav");

            beat.IsVoicing = true;
            beat.Status = $"🗣 {Name(engine)} · {voice}…";
            AddLog($"{beat.Label}: voicing \"{Trim(line, 60)}\" with {Name(engine)} · {voice}.");
            try
            {
                await _yak.GenerateAsync(engine, voice, line, path, s => OnUi(() => beat.Status = "🗣 " + s), ct);
                var seconds = await Task.Run(() => WavSeconds(path) is var w && w > 0 ? w : GetVideoDuration(path));

                beat.IsVoicing = false;
                if (beat.IsRendering)
                {
                    AddLog($"{beat.Label}: line voiced ({Path.GetFileName(path)}), but the beat is rendering — 🎙 Browse to it once the take lands.");
                    return false;
                }
                SetDialogue(beat, path, seconds);
                beat.VoicedFor = key;
                beat.Status = $"🗣 Voiced · {seconds:0.0} s";
                ScheduleStorySave();
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                beat.Status = "🗣 Stopped.";
                return false;
            }
            catch (Exception ex)
            {
                beat.Status = "⚠ " + ex.Message;
                YakStatus = ex.Message;
                AddLog($"{beat.Label}: Yakkinator could not voice the line — {ex.Message}");
                return false;
            }
            finally
            {
                beat.IsVoicing = false;
                RefreshVoiceCommands();
            }
        }

        private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";

        /// <summary>A PCM WAV's length from its header, without ffmpeg; 0 if it can't be read.</summary>
        public static double WavSeconds(string path)
        {
            try
            {
                using var r = new BinaryReader(File.OpenRead(path));
                if (new string(r.ReadChars(4)) != "RIFF") return 0;
                r.ReadInt32();
                if (new string(r.ReadChars(4)) != "WAVE") return 0;
                int byteRate = 0;
                while (r.BaseStream.Position + 8 <= r.BaseStream.Length)
                {
                    var id = new string(r.ReadChars(4));
                    var size = r.ReadInt32();
                    if (id == "fmt ")
                    {
                        var start = r.BaseStream.Position;
                        r.ReadInt16(); r.ReadInt16(); r.ReadInt32();
                        byteRate = r.ReadInt32();
                        r.BaseStream.Position = start + size;
                    }
                    else if (id == "data")
                    {
                        return byteRate > 0 ? (double)size / byteRate : 0;
                    }
                    else r.BaseStream.Position += size + (size & 1);
                }
            }
            catch { }
            return 0;
        }
    }
}
