// WPF's Window, as far as the shared view models drive one: they construct a dialog by its WPF
// name (ExpressJobWindow, ScenePromptLibraryWindow, ...), set Owner/WindowStartupLocation, then
// Show() or ShowDialog() it and read the result. Here each of those names is a small facade
// (Compat/WindowFacades.cs) deriving from this class, which builds the real Avalonia window on
// show and forwards the lifecycle.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using AvWindow = Avalonia.Controls.Window;

namespace System.Windows
{
    public abstract class Window
    {
        private static readonly List<Window> Open = new();

        private AvWindow? _view;
        private WindowState _state = WindowState.Normal;

        internal static IEnumerable<Window> OpenWindows => Open.ToArray();

        public Window? Owner { get; set; }
        public WindowStartupLocation WindowStartupLocation { get; set; } = WindowStartupLocation.CenterOwner;
        public bool? DialogResult { get; set; }
        public bool IsActive => _view?.IsActive ?? false;
        public string Title { get; set; } = string.Empty;

        public WindowState WindowState
        {
            get => _view == null ? _state : _view.WindowState switch
            {
                Avalonia.Controls.WindowState.Minimized => WindowState.Minimized,
                Avalonia.Controls.WindowState.Maximized => WindowState.Maximized,
                _ => WindowState.Normal
            };
            set
            {
                _state = value;
                if (_view != null)
                    _view.WindowState = value switch
                    {
                        WindowState.Minimized => Avalonia.Controls.WindowState.Minimized,
                        WindowState.Maximized => Avalonia.Controls.WindowState.Maximized,
                        _ => Avalonia.Controls.WindowState.Normal
                    };
            }
        }

        public event EventHandler? Closed;

        /// <summary>Builds the Avalonia window this facade stands for. Called once per show.</summary>
        protected abstract AvWindow CreateView();

        /// <summary>The live Avalonia window, once shown.</summary>
        protected AvWindow? View => _view;

        /// <summary>Lets the Avalonia view close the dialog with a result, as WPF's DialogResult setter does.</summary>
        public void Close(bool? result)
        {
            DialogResult = result;
            _view?.Close();
        }

        public void Close() => _view?.Close();

        public void Activate() => _view?.Activate();

        public void Show()
        {
            var view = Prepare(out var owner);
            if (owner != null) view.Show(owner);
            else view.Show();
        }

        public bool? ShowDialog()
        {
            var view = Prepare(out var owner);
            owner ??= ActiveAvaloniaWindow();
            if (owner == null)
            {
                // No window to be modal over (startup); show it and wait for it to close.
                view.Show();
                WaitForClose(view);
                return DialogResult;
            }

            var task = view.ShowDialog(owner);
            if (!Dispatcher.UIThread.CheckAccess())
            {
                task.GetAwaiter().GetResult();
                return DialogResult;
            }

            var frame = new DispatcherFrame();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            Dispatcher.UIThread.PushFrame(frame);
            return DialogResult;
        }

        private static void WaitForClose(AvWindow view)
        {
            if (!Dispatcher.UIThread.CheckAccess()) return;
            var frame = new DispatcherFrame();
            view.Closed += (_, _) => frame.Continue = false;
            Dispatcher.UIThread.PushFrame(frame);
        }

        private AvWindow Prepare(out AvWindow? owner)
        {
            DialogResult = null;
            _view = CreateView();
            if (!string.IsNullOrEmpty(Title)) _view.Title = Title;
            owner = Owner?._view ?? (WindowStartupLocation == WindowStartupLocation.CenterScreen ? null : ActiveAvaloniaWindow());
            _view.WindowStartupLocation = owner != null
                ? Avalonia.Controls.WindowStartupLocation.CenterOwner
                : Avalonia.Controls.WindowStartupLocation.CenterScreen;
            WindowState = _state;

            Open.Add(this);
            _view.Closed += (_, _) =>
            {
                Open.Remove(this);
                Closed?.Invoke(this, EventArgs.Empty);
            };
            return _view;
        }

        internal static AvWindow? ActiveAvaloniaWindow()
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
                return null;
            return desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow;
        }
    }
}
