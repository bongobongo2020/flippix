using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace FlipPix.UI.Linux.Controls;

/// <summary>
/// WPF's &lt;MouseBinding MouseAction="LeftClick|LeftDoubleClick|RightClick" Command=... /&gt; on any
/// element (a thumbnail Border, a list row): ctrl:Click.Command / DoubleCommand / RightCommand.
/// </summary>
public static class Click
{
    public static readonly AttachedProperty<ICommand?> CommandProperty =
        AvaloniaProperty.RegisterAttached<Control, ICommand?>("Command", typeof(Click));
    public static readonly AttachedProperty<object?> CommandParameterProperty =
        AvaloniaProperty.RegisterAttached<Control, object?>("CommandParameter", typeof(Click));
    public static readonly AttachedProperty<ICommand?> DoubleCommandProperty =
        AvaloniaProperty.RegisterAttached<Control, ICommand?>("DoubleCommand", typeof(Click));
    public static readonly AttachedProperty<object?> DoubleCommandParameterProperty =
        AvaloniaProperty.RegisterAttached<Control, object?>("DoubleCommandParameter", typeof(Click));
    public static readonly AttachedProperty<ICommand?> RightCommandProperty =
        AvaloniaProperty.RegisterAttached<Control, ICommand?>("RightCommand", typeof(Click));
    public static readonly AttachedProperty<object?> RightCommandParameterProperty =
        AvaloniaProperty.RegisterAttached<Control, object?>("RightCommandParameter", typeof(Click));

    static Click()
    {
        CommandProperty.Changed.AddClassHandler<Control>((c, _) => Hook(c));
        DoubleCommandProperty.Changed.AddClassHandler<Control>((c, _) => Hook(c));
        RightCommandProperty.Changed.AddClassHandler<Control>((c, _) => Hook(c));
    }

    public static ICommand? GetCommand(Control c) => c.GetValue(CommandProperty);
    public static void SetCommand(Control c, ICommand? v) => c.SetValue(CommandProperty, v);
    public static object? GetCommandParameter(Control c) => c.GetValue(CommandParameterProperty);
    public static void SetCommandParameter(Control c, object? v) => c.SetValue(CommandParameterProperty, v);
    public static ICommand? GetDoubleCommand(Control c) => c.GetValue(DoubleCommandProperty);
    public static void SetDoubleCommand(Control c, ICommand? v) => c.SetValue(DoubleCommandProperty, v);
    public static object? GetDoubleCommandParameter(Control c) => c.GetValue(DoubleCommandParameterProperty);
    public static void SetDoubleCommandParameter(Control c, object? v) => c.SetValue(DoubleCommandParameterProperty, v);
    public static ICommand? GetRightCommand(Control c) => c.GetValue(RightCommandProperty);
    public static void SetRightCommand(Control c, ICommand? v) => c.SetValue(RightCommandProperty, v);
    public static object? GetRightCommandParameter(Control c) => c.GetValue(RightCommandParameterProperty);
    public static void SetRightCommandParameter(Control c, object? v) => c.SetValue(RightCommandParameterProperty, v);

    private static readonly AttachedProperty<bool> HookedProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Hooked", typeof(Click));

    private static void Hook(Control c)
    {
        if (c.GetValue(HookedProperty)) return;
        c.SetValue(HookedProperty, true);
        c.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Bubble, handledEventsToo: false);
        c.AddHandler(InputElement.DoubleTappedEvent, OnDoubleTapped, RoutingStrategies.Bubble);
    }

    private static void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Control c) return;
        if (!c.IsPointerOver) return;
        var (command, parameter) = e.InitialPressMouseButton switch
        {
            MouseButton.Left => (GetCommand(c), GetCommandParameter(c)),
            MouseButton.Right => (GetRightCommand(c), GetRightCommandParameter(c)),
            _ => (null, null)
        };
        if (command?.CanExecute(parameter) == true)
        {
            command.Execute(parameter);
            e.Handled = true;
        }
    }

    private static void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control c) return;
        var command = GetDoubleCommand(c);
        var parameter = GetDoubleCommandParameter(c);
        if (command?.CanExecute(parameter) == true)
        {
            command.Execute(parameter);
            e.Handled = true;
        }
    }
}

/// <summary>
/// The control a ContextMenu opened on — WPF's ContextMenu.PlacementTarget, which Avalonia does
/// not expose for binding. Set as each menu opens; bound as (ctrl:Menus.Target).
/// </summary>
public static class Menus
{
    public static readonly AttachedProperty<Control?> TargetProperty =
        AvaloniaProperty.RegisterAttached<ContextMenu, Control?>("Target", typeof(Menus));

    public static Control? GetTarget(ContextMenu m) => m.GetValue(TargetProperty);
    public static void SetTarget(ContextMenu m, Control? v) => m.SetValue(TargetProperty, v);

    static Menus()
    {
        // Opening by right-click: ContextRequested reaches the owner just before the menu opens.
        Control.ContextRequestedEvent.AddClassHandler<Control>((c, _) =>
        {
            if (c.ContextMenu is { } menu) SetTarget(menu, c);
        }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        Control.ContextMenuProperty.Changed.AddClassHandler<Control>((c, e) =>
        {
            if (e.NewValue is ContextMenu menu) SetTarget(menu, c);
        });
    }

    /// <summary>Touch the class so its static handlers register (called at startup).</summary>
    public static void Initialize() { }

    /// <summary>Opens an owner's ContextMenu under it, as WPF code does with PlacementTarget + IsOpen.</summary>
    public static void Open(Control owner)
    {
        if (owner.ContextMenu is not { } menu) return;
        SetTarget(menu, owner);
        menu.Placement = PlacementMode.Bottom;
        menu.Open(owner);
    }
}
