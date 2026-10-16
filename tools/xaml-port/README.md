# xaml-port

Generates `FlipPix.UI.Linux`'s Avalonia windows from `FlipPix.UI`'s WPF XAML, so the Linux /
Chromebook build tracks the Windows UI instead of drifting from it.

```bash
dotnet build FlipPix.UI.Linux                                   # the converter reads its view-model types
dotnet build tools/xaml-port -c Release -o /tmp/xaml-port
tools/xaml-port/port-all.sh                                     # rewrites FlipPix.UI.Linux/Windows/*.axaml
dotnet build FlipPix.UI.Linux                                   # compiled bindings check every path
```

`port-all.sh` lists which WPF file becomes which Avalonia file, and with which view model as its
`x:DataType`. Everything the converter could not translate faithfully is listed in
`port-report.txt`; the remaining items are handled in the windows' code-behind.

What it translates:

| WPF | Avalonia |
|---|---|
| `Visibility="{Binding X, Converter=BooleanToVisibility}"` | `IsVisible="{Binding X}"` (and the other visibility converters) |
| keyed `Style` + `Style="{StaticResource K}"` | `ControlTheme` + `Theme="{StaticResource K}"` |
| implicit `Style TargetType=...` | `Style Selector="..."` |
| inline `<X.Style>` setters | a local `ControlTheme` derived from the base style (keeps WPF's trigger-over-setter precedence) |
| `Trigger IsMouseOver/IsEnabled/IsChecked...` | `:pointerover`, `:disabled`, `:checked`... |
| `DataTrigger` | a bound style class `Classes.x="{Binding ...}"` + a selector on it |
| `ControlTemplate.Triggers` with `TargetName` | `/template/ Type#Name` selectors |
| `DataContext="{Binding X}"`, `ItemsSource`, `DataTemplate` | `x:DataType` from the real view-model types (reflection), so bindings are compiled and checked |
| `RelativeSource AncestorType=... DataContext.X` | `$parent[...].((vm:Type)DataContext).X` |
| `ContextMenu.PlacementTarget` | `ctrl:Menus.Target` |
| `MouseBinding` / `KeyBinding` | `ctrl:Click.Command` / `Window.KeyBindings` |
| `MediaElement`, `InkCanvas` | `ctrl:VideoPreview` (libvlc), `ctrl:MaskPaintCanvas` |
| `GroupBox`, `StatusBar`, `LayoutTransform`, `DropShadowEffect` | equivalent Avalonia shapes |

`--kind styles` converts a `ResourceDictionary` (the shared theme) to an Avalonia `Styles` file.
