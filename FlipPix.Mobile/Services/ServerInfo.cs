using CommunityToolkit.Mvvm.ComponentModel;
using FlipPix.Mobile.ViewModels;
using FlipPix.Remote.Contracts;

namespace FlipPix.Mobile.Services;

/// <summary>
/// What the paired computer offers, from its /status: which picture looks it can make (the iOS
/// Companion offers only Photo) and the model credits the app must show (MiniMax H3's license
/// requires "MiniMax H3" on screen wherever video is made).
/// </summary>
public sealed partial class ServerInfo : ObservableObject
{
    public static ServerInfo Current { get; } = new();

    [ObservableProperty] private IReadOnlyList<LookOption> _looks = ViewModels.Looks.All;

    /// <summary>"Video by MiniMax H3", or "" when the computer names no video model.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVideoCredit))]
    private string _videoCredit = "";

    /// <summary>Every credit, one line, for the Connect page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCredits))]
    private string _creditsText = "";

    public bool HasVideoCredit => VideoCredit.Length > 0;
    public bool HasCredits => CreditsText.Length > 0;

    public void Apply(StatusDto status)
    {
        // An older desktop sends no list: it offers every look.
        Looks = status.Looks.Count == 0
            ? ViewModels.Looks.All
            : ViewModels.Looks.All.Where(l => status.Looks.Contains(l.Key)).ToList();
        VideoCredit = status.Credits.FirstOrDefault(c => c.StartsWith("Video", StringComparison.OrdinalIgnoreCase)) ?? "";
        CreditsText = string.Join("  ·  ", status.Credits);
    }

    /// <summary>Asks the computer again; keeps what it knew when the computer doesn't answer.</summary>
    public async Task RefreshAsync()
    {
        if (!AppServices.Settings.IsPaired) return;
        try { Apply(await AppServices.Remote.StatusAsync()); }
        catch (RemoteException) { /* offline: the Connect page says so */ }
    }
}
