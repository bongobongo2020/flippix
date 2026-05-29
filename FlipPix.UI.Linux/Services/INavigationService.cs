using System;
using Avalonia.Controls;

namespace FlipPix.UI.Linux.Services;

public interface INavigationService
{
    void NavigateTo<TWindow>() where TWindow : Window;
    void NavigateTo(Type windowType);
    void NavigateToAndClose<TWindow>(Window? currentWindow = null) where TWindow : Window;
    void NavigateToAndClose(Type windowType, Window? currentWindow = null);
}
