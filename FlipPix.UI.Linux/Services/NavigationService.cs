using System;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace FlipPix.UI.Linux.Services;

public class NavigationService : INavigationService
{
    private readonly IServiceProvider _serviceProvider;

    public NavigationService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    public void NavigateTo<TWindow>() where TWindow : Window
        => NavigateTo(typeof(TWindow));

    public void NavigateTo(Type windowType)
    {
        if (windowType == null) throw new ArgumentNullException(nameof(windowType));
        if (!typeof(Window).IsAssignableFrom(windowType))
            throw new ArgumentException($"Type {windowType.Name} must derive from Window");

        var window = _serviceProvider.GetService(windowType) as Window;
        if (window != null)
            window.Show();
        else
            throw new InvalidOperationException($"Could not resolve window of type {windowType.Name}");
    }

    public void NavigateToAndClose<TWindow>(Window? currentWindow = null) where TWindow : Window
        => NavigateToAndClose(typeof(TWindow), currentWindow);

    public void NavigateToAndClose(Type windowType, Window? currentWindow = null)
    {
        if (windowType == null) throw new ArgumentNullException(nameof(windowType));
        if (!typeof(Window).IsAssignableFrom(windowType))
            throw new ArgumentException($"Type {windowType.Name} must derive from Window");

        var window = _serviceProvider.GetService(windowType) as Window;
        if (window != null)
        {
            window.Show();
            currentWindow?.Close();
        }
        else
        {
            throw new InvalidOperationException($"Could not resolve window of type {windowType.Name}");
        }
    }
}
