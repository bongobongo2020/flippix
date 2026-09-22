using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlipPix.Mobile.Services;

namespace FlipPix.Mobile.ViewModels;

public enum Page { Image, Video, Story, Settings }

public partial class MainViewModel : ObservableObject
{
    private Page _lastWorkPage = Page.Image;

    public MainViewModel()
    {
        Image = new ImageViewModel();
        // The viewer is full screen: header and nav step aside while it is open.
        Image.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ImageViewModel.IsViewerOpen)) return;
            OnPropertyChanged(nameof(ShowHeader));
            OnPropertyChanged(nameof(ShowNav));
        };
        Settings = new SettingsViewModel(onSaved: () => { Image.Refresh(); Go(_lastWorkPage); });
        // First run lands in Settings: nothing else can work without a server address.
        _page = AppServices.Settings.IsComfyConfigured ? Page.Image : Page.Settings;
        if (AppServices.Settings.IsComfyConfigured) _ = CheckServerAsync();
    }

    public ImageViewModel Image { get; }
    public SettingsViewModel Settings { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImage), nameof(IsVideo), nameof(IsStory), nameof(IsSettings), nameof(ShowHeader), nameof(ShowNav))]
    private Page _page;

    public bool IsImage => Page == Page.Image;
    public bool IsVideo => Page == Page.Video;
    public bool IsStory => Page == Page.Story;
    public bool IsSettings => Page == Page.Settings;
    public bool ShowHeader => Page != Page.Settings && !(IsImage && Image.IsViewerOpen);
    public bool ShowNav => ShowHeader && !KeyboardOpen;

    /// <summary>Set by the view: while typing, the bottom nav would only cost screen height.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNav))]
    private bool _keyboardOpen;

    [ObservableProperty] private bool _serverOnline;
    [ObservableProperty] private string _serverLabel = "Not connected";

    [RelayCommand]
    private void Go(Page page)
    {
        if (page != Page.Settings) _lastWorkPage = page;
        Page = page;
        if (page != Page.Settings) _ = CheckServerAsync();
    }

    [RelayCommand] private void OpenSettings() => Go(Page.Settings);
    [RelayCommand] private void CloseSettings() => Go(_lastWorkPage);

    /// <summary>
    /// Android's back gesture: close the innermost thing that is open. False means nothing was,
    /// and the system should leave the app.
    /// </summary>
    public bool HandleBack()
    {
        if (Image.IsViewerOpen) { Image.CloseViewerCommand.Execute(null); return true; }
        if (Page == Page.Settings && AppServices.Settings.IsComfyConfigured) { CloseSettings(); return true; }
        if (Page != Page.Image && Page != Page.Settings) { Go(Page.Image); return true; }
        return false;
    }

    private async Task CheckServerAsync()
    {
        var problem = await ComfyGateway.ProbeAsync(AppServices.Settings.ComfyUrl);
        ServerOnline = problem == null;
        ServerLabel = problem == null ? "Server ready" : "Server offline";
    }
}
