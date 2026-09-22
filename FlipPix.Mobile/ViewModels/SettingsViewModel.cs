using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlipPix.Mobile.Services;

namespace FlipPix.Mobile.ViewModels;

public enum LinkState { Unknown, Checking, Ok, Failed }

public partial class SettingsViewModel : ObservableObject
{
    private readonly MobileSettings _settings = AppServices.Settings;
    private readonly Action _onSaved;

    public SettingsViewModel(Action onSaved)
    {
        _onSaved = onSaved;
        _comfyUrl = _settings.ComfyUrl;
        _llmUrl = _settings.LlmUrl;
        _llmModel = _settings.LlmModel;
        if (_llmModel.Length > 0) LlmModels.Add(_llmModel);
    }

    [ObservableProperty] private string _comfyUrl;
    [ObservableProperty] private string _llmUrl;
    [ObservableProperty] private string _llmModel;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ComfyOk), nameof(ComfyFailed), nameof(ComfyChecking))]
    private LinkState _comfyState;
    [ObservableProperty] private string _comfyMessage = "Where your ComfyUI server listens, usually port 8188.";

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(LlmOk), nameof(LlmFailed), nameof(LlmChecking))]
    private LinkState _llmState;
    [ObservableProperty] private string _llmMessage = "LM Studio, llama.cpp or any OpenAI-style server. It writes your stories and video prompts.";

    public bool ComfyOk => ComfyState == LinkState.Ok;
    public bool ComfyFailed => ComfyState == LinkState.Failed;
    public bool ComfyChecking => ComfyState == LinkState.Checking;
    public bool LlmOk => LlmState == LinkState.Ok;
    public bool LlmFailed => LlmState == LinkState.Failed;
    public bool LlmChecking => LlmState == LinkState.Checking;

    public ObservableCollection<string> LlmModels { get; } = new();

    partial void OnComfyUrlChanged(string value) => ComfyState = LinkState.Unknown;
    partial void OnLlmUrlChanged(string value) => LlmState = LinkState.Unknown;

    [RelayCommand]
    private async Task TestComfyAsync()
    {
        ComfyState = LinkState.Checking;
        ComfyMessage = "Checking…";
        var problem = await ComfyGateway.ProbeAsync(ComfyUrl);
        ComfyState = problem == null ? LinkState.Ok : LinkState.Failed;
        ComfyMessage = problem ?? $"Connected to {MobileSettings.NormalizeUrl(ComfyUrl)}.";
    }

    [RelayCommand]
    private async Task TestLlmAsync()
    {
        LlmState = LinkState.Checking;
        LlmMessage = "Checking…";
        try
        {
            var models = await LlmClient.ListModelsAsync(LlmUrl);
            var keep = LlmModel;
            LlmModels.Clear();
            foreach (var m in models) LlmModels.Add(m);
            LlmModel = models.Contains(keep) ? keep : models.FirstOrDefault() ?? "";
            LlmState = LinkState.Ok;
            LlmMessage = models.Count == 0
                ? "Connected, but no model is loaded yet."
                : $"Connected. {models.Count} model{(models.Count == 1 ? "" : "s")} available.";
        }
        catch (Exception ex)
        {
            LlmState = LinkState.Failed;
            LlmMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        _settings.ComfyUrl = MobileSettings.NormalizeUrl(ComfyUrl);
        _settings.LlmUrl = MobileSettings.NormalizeUrl(LlmUrl);
        _settings.LlmModel = LlmModel ?? "";
        ComfyUrl = _settings.ComfyUrl;
        LlmUrl = _settings.LlmUrl;
        _settings.Save();
        AppServices.Comfy.Configure(_settings.ComfyUrl);

        // Saving checks both, so the dots in the header tell the truth straight away.
        var checks = new List<Task> { TestComfyAsync() };
        if (_settings.LlmUrl.Length > 0) checks.Add(TestLlmAsync());
        await Task.WhenAll(checks);
        // The LLM check may have picked the loaded model for an empty field; keep that choice.
        _settings.LlmModel = LlmModel ?? "";
        _settings.Save();
        if (ComfyOk) _onSaved();
    }
}
