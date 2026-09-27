using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlipPix.Mobile.Services;
using FlipPix.Remote.Contracts;

namespace FlipPix.Mobile.ViewModels;

public enum Page { Library, Image, Video, Story }

/// <summary>
/// The shell: four pages, the full-screen viewer over them, and the connect sheet. The phone is a
/// remote, so nothing works until it is paired; an unpaired phone opens on the connect sheet.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    public MainViewModel()
    {
        Viewer = new ViewerViewModel();
        Library = new LibraryViewModel(Viewer.Open);
        Image = new ImageViewModel(Viewer.Open);
        Video = new VideoViewModel(Viewer.Open);
        Story = new StoryViewModel(Viewer.Open);
        Connect = new ConnectViewModel(OnPaired, OnForgotten);

        Viewer.UsePromptHandler = (prompt, look, shape) =>
        {
            Image.UsePrompt(prompt, look, shape);
            Go(Page.Image);
        };
        Viewer.AnimateHandler = slot =>
        {
            Video.AddFromLibrary(slot);
            Go(Page.Video);
        };
        Viewer.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ViewerViewModel.IsOpen)) ChromeChanged(); };
        AppServices.Jobs.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(JobsHub.IsOnline)) UpdateServerLabel(); };
        AppServices.Jobs.Changed += UpdateNowMaking;
        AppServices.Remote.Unpaired += () => Dispatcher.UIThread.Post(OnUnpaired);

        if (AppServices.Settings.IsPaired)
        {
            AppServices.Jobs.Start();
            Library.OnShown();
        }
        else
        {
            _isConnectOpen = true;
            Connect.OnShown();
        }
        UpdateServerLabel();
    }

    public LibraryViewModel Library { get; }
    public ImageViewModel Image { get; }
    public VideoViewModel Video { get; }
    public StoryViewModel Story { get; }
    public ConnectViewModel Connect { get; }
    public ViewerViewModel Viewer { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLibrary), nameof(IsImage), nameof(IsVideo), nameof(IsStory))]
    private Page _page = Page.Library;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHeader), nameof(ShowNav), nameof(CanCloseConnect))]
    private bool _isConnectOpen;

    /// <summary>Set by the view: while typing, the bottom nav would only cost screen height.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNav))]
    private bool _keyboardOpen;

    [ObservableProperty] private bool _serverOnline;
    [ObservableProperty] private string _serverLabel = "Not connected";
    /// <summary>The paired computer's name even while it can't be reached, and how it is: the iPad sidebar shows both.</summary>
    [ObservableProperty] private string _computerName = "No computer";
    [ObservableProperty] private string _connectionText = "Tap to connect";

    /// <summary>What the computer is making right now, whichever page asked for it (the iPad sidebar shows it).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMaking))]
    private JobVm? _nowMaking;

    /// <summary>"2 more waiting", or "" when nothing else is in line.</summary>
    [ObservableProperty] private string _waitingText = "";

    public bool IsMaking => NowMaking != null;

    public bool IsLibrary => Page == Page.Library;
    public bool IsImage => Page == Page.Image;
    public bool IsVideo => Page == Page.Video;
    public bool IsStory => Page == Page.Story;
    public bool ShowHeader => !IsConnectOpen && !Viewer.IsOpen;
    public bool ShowNav => ShowHeader && !KeyboardOpen;
    public bool CanCloseConnect => AppServices.Settings.IsPaired;

    private void ChromeChanged()
    {
        OnPropertyChanged(nameof(ShowHeader));
        OnPropertyChanged(nameof(ShowNav));
    }

    [RelayCommand]
    private void Go(Page page)
    {
        Viewer.Close();
        IsConnectOpen = false;
        Page = page;
        if (page == Page.Library) Library.OnShown();
        AppServices.Jobs.Nudge();
    }

    [RelayCommand]
    private void OpenConnect()
    {
        IsConnectOpen = true;
        Connect.OnShown();
    }

    [RelayCommand]
    private void CloseConnect()
    {
        if (!AppServices.Settings.IsPaired) return;
        IsConnectOpen = false;
        if (IsLibrary) Library.OnShown();
    }

    private void OnPaired()
    {
        Library.Reset();
        AppServices.Jobs.Start();
        OnPropertyChanged(nameof(CanCloseConnect));
        UpdateServerLabel();
        // A moment on the "Connected" card, then straight to what's been made.
        DispatcherTimer.RunOnce(() =>
        {
            if (!IsConnectOpen || !AppServices.Settings.IsPaired) return;
            IsConnectOpen = false;
            Page = Page.Library;
            Library.OnShown();
        }, TimeSpan.FromSeconds(1.2));
    }

    private void OnForgotten()
    {
        AppServices.Jobs.Stop();
        Library.Reset();
        OnPropertyChanged(nameof(CanCloseConnect));
        UpdateServerLabel();
    }

    private void OnUnpaired()
    {
        var settings = AppServices.Settings;
        if (!settings.IsPaired) return;
        settings.Token = "";
        settings.Save();
        AppServices.Remote.Configure("", "", "");
        OnForgotten();
        Viewer.Close();
        IsConnectOpen = true;
        Connect.ShowUnpaired();
    }

    private void UpdateNowMaking()
    {
        var active = AppServices.Jobs.Jobs.Where(j => j.IsActive).ToList();
        // The list is newest first, so the one being made is the running one, or else the oldest waiting.
        NowMaking = active.FirstOrDefault(j => j.IsRunning) ?? active.LastOrDefault();
        var waiting = active.Count - (NowMaking == null ? 0 : 1);
        WaitingText = waiting == 0 ? "" : $"{waiting} more waiting";
    }

    /// <summary>The sidebar's "now making" card: to the page that asked for it.</summary>
    [RelayCommand]
    private void ShowNowMaking()
    {
        if (NowMaking is not { } job) return;
        Go(job.Kind switch { JobKinds.Video => Page.Video, JobKinds.Story => Page.Story, _ => Page.Image });
    }

    private void UpdateServerLabel()
    {
        var paired = AppServices.Settings.IsPaired;
        ServerOnline = paired && AppServices.Jobs.IsOnline;
        ServerLabel = !paired ? "Not connected"
            : ServerOnline ? AppServices.Settings.ServerName
            : "Offline";
        ComputerName = paired ? AppServices.Settings.ServerName : "No computer";
        ConnectionText = !paired ? "Tap to connect" : ServerOnline ? "Connected" : "Can't reach it right now";
    }

    /// <summary>
    /// Android's back gesture: close the innermost thing that is open. False means nothing was,
    /// and the system should leave the app.
    /// </summary>
    public bool HandleBack()
    {
        if (Viewer.IsOpen) { Viewer.Close(); return true; }
        if (IsConnectOpen && CanCloseConnect) { CloseConnect(); return true; }
        if (IsConnectOpen) return false;
        if (Page != Page.Library) { Go(Page.Library); return true; }
        return false;
    }
}
