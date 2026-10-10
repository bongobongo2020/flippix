using System.Windows;
using System.Windows.Input;

namespace FlipPix.UI.Services;

/// <summary>
/// Centralized keyboard shortcut handling service.
/// Provides consistent shortcuts across all windows and tabs.
/// </summary>
public class KeyboardShortcutService
{
    private readonly Dictionary<(Key Key, ModifierKeys Modifiers), ShortcutAction> _shortcuts = new();
    private readonly Dictionary<string, List<(Key Key, ModifierKeys Modifiers)>> _contextShortcuts = new();

    /// <summary>
    /// Represents a keyboard shortcut action.
    /// </summary>
    public class ShortcutAction
    {
        public required string Name { get; init; }
        public required string Description { get; init; }
        public required Action Execute { get; init; }
        public string? Context { get; init; }
        public bool IsGlobal { get; init; } = true;
    }

    public KeyboardShortcutService()
    {
        // Register default global shortcuts
        RegisterDefaultShortcuts();
    }

    private void RegisterDefaultShortcuts()
    {
        // These will be registered by individual windows
    }

    /// <summary>
    /// Registers a keyboard shortcut.
    /// </summary>
    public void RegisterShortcut(Key key, ModifierKeys modifiers, ShortcutAction action)
    {
        var shortcutKey = (key, modifiers);
        _shortcuts[shortcutKey] = action;

        if (action.Context != null)
        {
            if (!_contextShortcuts.TryGetValue(action.Context, out var list))
            {
                list = new List<(Key, ModifierKeys)>();
                _contextShortcuts[action.Context] = list;
            }
            list.Add(shortcutKey);
        }
    }

    /// <summary>
    /// Registers multiple shortcuts at once.
    /// </summary>
    public void RegisterShortcuts(IEnumerable<(Key Key, ModifierKeys Modifiers, ShortcutAction Action)> shortcuts)
    {
        foreach (var (key, modifiers, action) in shortcuts)
        {
            RegisterShortcut(key, modifiers, action);
        }
    }

    /// <summary>
    /// Unregisters a keyboard shortcut.
    /// </summary>
    public void UnregisterShortcut(Key key, ModifierKeys modifiers)
    {
        var shortcutKey = (key, modifiers);
        if (_shortcuts.TryGetValue(shortcutKey, out var action))
        {
            _shortcuts.Remove(shortcutKey);

            if (action.Context != null && _contextShortcuts.TryGetValue(action.Context, out var list))
            {
                list.Remove(shortcutKey);
            }
        }
    }

    /// <summary>
    /// Unregisters all shortcuts for a specific context.
    /// </summary>
    public void UnregisterContext(string context)
    {
        if (_contextShortcuts.TryGetValue(context, out var shortcuts))
        {
            foreach (var key in shortcuts)
            {
                _shortcuts.Remove(key);
            }
            _contextShortcuts.Remove(context);
        }
    }

    /// <summary>
    /// Handles a key press event.
    /// </summary>
    /// <returns>True if the shortcut was handled.</returns>
    public bool HandleKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var shortcutKey = (key, modifiers);

        if (_shortcuts.TryGetValue(shortcutKey, out var action))
        {
            action.Execute();
            e.Handled = true;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Gets all registered shortcuts for display in help.
    /// </summary>
    public IEnumerable<ShortcutAction> GetAllShortcuts() => _shortcuts.Values;

    /// <summary>
    /// Gets shortcuts for a specific context.
    /// </summary>
    public IEnumerable<(string Shortcut, ShortcutAction Action)> GetShortcutsForContext(string context)
    {
        if (!_contextShortcuts.TryGetValue(context, out var keys))
            yield break;

        foreach (var key in keys)
        {
            if (_shortcuts.TryGetValue(key, out var action))
            {
                yield return (FormatShortcut(key.Item1, key.Item2), action);
            }
        }
    }

    /// <summary>
    /// Formats a shortcut for display.
    /// </summary>
    public static string FormatShortcut(Key key, ModifierKeys modifiers)
    {
        var parts = new List<string>();

        if (modifiers.HasFlag(ModifierKeys.Control))
            parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt))
            parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift))
            parts.Add("Shift");

        parts.Add(key.ToString());

        return string.Join("+", parts);
    }

    /// <summary>
    /// Attaches keyboard handling to a window.
    /// </summary>
    public void AttachToWindow(Window window)
    {
        window.PreviewKeyDown += (s, e) => HandleKeyDown(e);
    }

    /// <summary>
    /// Attaches keyboard handling to a framework element.
    /// </summary>
    public void AttachToElement(FrameworkElement element)
    {
        element.PreviewKeyDown += (s, e) => HandleKeyDown(e);
    }
}

/// <summary>
/// Common keyboard shortcuts used throughout the application.
/// </summary>
public static class CommonShortcuts
{
    /// <summary>Search/Command palette: Ctrl+K</summary>
    public static (Key Key, ModifierKeys Modifiers) Search => (Key.K, ModifierKeys.Control);

    /// <summary>Quick generate: Ctrl+Enter</summary>
    public static (Key Key, ModifierKeys Modifiers) Generate => (Key.Enter, ModifierKeys.Control);

    /// <summary>Save: Ctrl+S</summary>
    public static (Key Key, ModifierKeys Modifiers) Save => (Key.S, ModifierKeys.Control);

    /// <summary>Open: Ctrl+O</summary>
    public static (Key Key, ModifierKeys Modifiers) Open => (Key.O, ModifierKeys.Control);

    /// <summary>New: Ctrl+N</summary>
    public static (Key Key, ModifierKeys Modifiers) New => (Key.N, ModifierKeys.Control);

    /// <summary>Settings: Ctrl+,</summary>
    public static (Key Key, ModifierKeys Modifiers) Settings => (Key.OemComma, ModifierKeys.Control);

    /// <summary>Next tab: Ctrl+Tab</summary>
    public static (Key Key, ModifierKeys Modifiers) NextTab => (Key.Tab, ModifierKeys.Control);

    /// <summary>Previous tab: Ctrl+Shift+Tab</summary>
    public static (Key Key, ModifierKeys Modifiers) PreviousTab => (Key.Tab, ModifierKeys.Control | ModifierKeys.Shift);

    /// <summary>Go to tab 1-9: Ctrl+1 through Ctrl+9</summary>
    public static (Key Key, ModifierKeys Modifiers) GoToTab(int tabIndex)
    {
        var key = tabIndex switch
        {
            1 => Key.D1,
            2 => Key.D2,
            3 => Key.D3,
            4 => Key.D4,
            5 => Key.D5,
            6 => Key.D6,
            7 => Key.D7,
            8 => Key.D8,
            9 => Key.D9,
            _ => Key.None
        };
        return (key, ModifierKeys.Control);
    }

    /// <summary>Cancel/Close: Escape</summary>
    public static (Key Key, ModifierKeys Modifiers) Cancel => (Key.Escape, ModifierKeys.None);

    /// <summary>Help: F1</summary>
    public static (Key Key, ModifierKeys Modifiers) Help => (Key.F1, ModifierKeys.None);

    /// <summary>Refresh: F5</summary>
    public static (Key Key, ModifierKeys Modifiers) Refresh => (Key.F5, ModifierKeys.None);

    /// <summary>Undo: Ctrl+Z</summary>
    public static (Key Key, ModifierKeys Modifiers) Undo => (Key.Z, ModifierKeys.Control);

    /// <summary>Redo: Ctrl+Y</summary>
    public static (Key Key, ModifierKeys Modifiers) Redo => (Key.Y, ModifierKeys.Control);

    /// <summary>Copy: Ctrl+C</summary>
    public static (Key Key, ModifierKeys Modifiers) Copy => (Key.C, ModifierKeys.Control);

    /// <summary>Paste: Ctrl+V</summary>
    public static (Key Key, ModifierKeys Modifiers) Paste => (Key.V, ModifierKeys.Control);
}
