using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FlipPix.Mobile.ViewModels;

namespace FlipPix.Mobile.Views;

public partial class MainView : UserControl
{
    private TopLevel? _top;

    public MainView()
    {
        InitializeComponent();
        // A tap anywhere outside a text field puts the keyboard away, as phone users expect. Tunnel,
        // so it runs before a button handles the tap; the tap itself still goes through.
        AddHandler(PointerPressedEvent, OnAnyPointerPressed, RoutingStrategies.Tunnel);
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

        // Android 15+ draws apps edge to edge: keep content out from under the status bar,
        // the gesture bar and the keyboard.
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
        Padding = new Thickness(safe.Left, safe.Top, safe.Right, Math.Max(safe.Bottom, keyboard));
        if (DataContext is MainViewModel vm) vm.KeyboardOpen = keyboard > 0;
    }

    private void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.HandleBack()) e.Handled = true;
    }
}
