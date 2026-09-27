using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FlipPix.Mobile.ViewModels;
using FlipPix.Mobile.Views.Studio;

namespace FlipPix.Mobile.Views;

/// <summary>
/// The root: the phone layout (<see cref="MainView"/>) on a narrow screen, the studio layout
/// (<see cref="StudioShell"/>) from <see cref="StudioWidth"/> up, which is any iPad in either orientation
/// and most Split View sizes. Both read the same view models, so a resize across the line keeps what was
/// typed, what is queued and where the library was. Also owns what both layouts need from the platform:
/// the safe area, the keyboard, the back gesture, and a tap outside a text field putting the keyboard away.
/// </summary>
public sealed class AppShell : UserControl
{
    public const double StudioWidth = 700;

    private TopLevel? _top;
    private MainView? _phone;
    private StudioShell? _studio;

    public AppShell()
    {
        Background = (Avalonia.Media.IBrush?)Application.Current?.FindResource("BoothBrush");
        // Tunnel, so it runs before a button handles the tap; the tap itself still goes through.
        AddHandler(PointerPressedEvent, OnAnyPointerPressed, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) => Choose(Bounds.Width);
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Choose(e.NewSize.Width);
    }

    /// <summary>Each layout is built the first time it is needed and kept, so going back is instant.</summary>
    private void Choose(double width)
    {
        if (Vm is not { } vm || width <= 0) return;
        Control next = width >= StudioWidth
            ? _studio ??= new StudioShell { DataContext = vm }
            : _phone ??= new MainView { DataContext = vm };
        if (!ReferenceEquals(Content, next)) Content = next;
        // The phone's library is three across by design; the studio sets its own count from its width.
        if (next is MainView) vm.Library.Columns = 3;
        ApplyInsets();
    }

    private void OnAnyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual v && v.FindAncestorOfType<TextBox>(includeSelf: true) != null) return;
        if (_top?.FocusManager?.GetFocusedElement() is TextBox) _top.FocusManager.ClearFocus();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _top = TopLevel.GetTopLevel(this);
        if (_top == null) return;

        // Android's back gesture arrives here; handled means the app stays open.
        _top.BackRequested += OnBackRequested;
        _top.KeyDown += OnKeyDown;

        // Android 15+ draws apps edge to edge, and iOS always does: keep content out from under the
        // status bar, the home indicator and the keyboard.
        if (_top.InsetsManager is { } insets)
        {
            // Opting in makes the platform report real insets instead of zero while still
            // drawing under the bars (which Android 15 enforces regardless).
            insets.DisplayEdgeToEdge = true;
            insets.SystemBarColor = Avalonia.Media.Color.Parse("#1B1D2A");
            insets.SafeAreaChanged += OnInsetsChanged;
        }
        if (_top.InputPane is { } pane) pane.StateChanged += OnInsetsChanged;
        ApplyInsets();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_top != null)
        {
            _top.BackRequested -= OnBackRequested;
            _top.KeyDown -= OnKeyDown;
            if (_top.InsetsManager is { } insets) insets.SafeAreaChanged -= OnInsetsChanged;
            if (_top.InputPane is { } pane) pane.StateChanged -= OnInsetsChanged;
        }
        _top = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnInsetsChanged(object? sender, EventArgs e) => ApplyInsets();

    private void ApplyInsets()
    {
        var safe = _top?.InsetsManager?.SafeAreaPadding ?? default;
        // An open keyboard replaces the gesture-bar inset rather than adding to it.
        var keyboard = _top?.InputPane is { State: InputPaneState.Open } pane ? pane.OccludedRect.Height : 0;
        var insets = new Thickness(safe.Left, safe.Top, safe.Right, Math.Max(safe.Bottom, keyboard));
        // The phone layout sits inside the insets. The studio takes them itself, so its sidebar, viewer
        // and welcome run under the status bar and home indicator instead of leaving a band of booth.
        if (Content is StudioShell studio)
        {
            Padding = default;
            studio.ApplyInsets(insets);
        }
        else Padding = insets;
        if (Vm is { } vm) vm.KeyboardOpen = keyboard > 0;
    }

    private void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && vm.HandleBack()) e.Handled = true;
    }

    /// <summary>
    /// An iPad with a keyboard: Escape backs out like the back gesture, ⌘1–⌘4 switch pages. Inside the
    /// viewer the arrows move between items (the viewer handles those itself).
    /// </summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm || e.Handled) return;
        if (e.Key == Key.Escape && vm.HandleBack())
        {
            e.Handled = true;
            return;
        }
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Meta) && !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        if (vm.IsConnectOpen) return;
        Page? page = e.Key switch
        {
            Key.D1 => Page.Library,
            Key.D2 => Page.Image,
            Key.D3 => Page.Video,
            Key.D4 => Page.Story,
            _ => null,
        };
        if (page is { } p)
        {
            vm.GoCommand.Execute(p);
            e.Handled = true;
        }
    }
}
