#!/usr/bin/env bash
# Re-ports every FlipPix.UI window to FlipPix.UI.Linux. Needs a built FlipPix.UI.Linux (for the
# view-model types) and the tool built: dotnet build tools/xaml-port -c Release -o /tmp/xaml-port
set -euo pipefail
cd "$(dirname "$0")/../.."
TOOL=${XAML_PORT:-/tmp/xaml-port/xaml-port.dll}
DLL=FlipPix.UI.Linux/bin/Debug/net8.0/FlipPix.UI.Linux.dll
REPORT=${REPORT:-tools/xaml-port/port-report.txt}
: > "$REPORT"

port() { # wpf-file class datatype root [out]
    local out=${5:-FlipPix.UI.Linux/Windows/$(basename "${1%.xaml}").axaml}
    dotnet "$TOOL" --assembly "$DLL" --in "FlipPix.UI/$1" --out "$out" --class "$2" \
        ${3:+--datatype "$3"} ${4:+--root "$4"} --report "$REPORT" --quiet 1
}

port VideoGeneratorWindow.xaml     FlipPix.UI.VideoGeneratorWindow  FlipPix.UI.ViewModels.VideoGeneratorViewModel  win:AppWindow
port ImageGeneratorWindow.xaml     FlipPix.UI.ImageGeneratorWindow  FlipPix.UI.ViewModels.ImageGeneratorViewModel  win:AppWindow
port VideoEnhanceWindow.xaml       FlipPix.UI.VideoEnhanceWindow    FlipPix.UI.ViewModels.Video.VideoEnhanceViewModel win:AppWindow
port StoryVideoWindow.xaml         FlipPix.UI.StoryVideoWindow      FlipPix.UI.ViewModels.StoryVideoViewModel      win:AppWindow
port ExpressJobWindow.xaml         FlipPix.UI.Linux.Windows.ExpressJobWindow        FlipPix.UI.ViewModels.Video.ExpressJobViewModel
port StoryPromptLibraryWindow.xaml FlipPix.UI.Linux.Windows.StoryPromptLibraryWindow FlipPix.UI.ViewModels.StoryPromptLibraryViewModel
port ComfyUIFolderSetupWindow.xaml FlipPix.UI.Linux.Windows.ComfyUIFolderSetupWindow FlipPix.UI.ViewModels.ComfyUIFolderSetupViewModel win:DialogWindow
port SetupChoiceWindow.xaml        FlipPix.UI.Linux.Windows.SetupChoiceWindow       "" win:DialogWindow
port RemoteSetupWindow.xaml        FlipPix.UI.Linux.Windows.RemoteSetupWindow       "" win:DialogWindow
port SettingsWindow.xaml           FlipPix.UI.Linux.Windows.SettingsWindow          FlipPix.Core.Models.ComfyUISettings win:DialogWindow
port ScenePromptLibraryWindow.xaml FlipPix.UI.Linux.Windows.ScenePromptLibraryWindow ""
port PhoneRemoteWindow.xaml        FlipPix.UI.Linux.Windows.PhoneRemoteWindow       ""
port Controls/ProcessingLogPanel.xaml FlipPix.UI.Linux.Controls.ProcessingLogPanel "" "" FlipPix.UI.Linux/Controls/ProcessingLogPanel.axaml
port Controls/SectionHeader.xaml   FlipPix.UI.Linux.Controls.SectionHeader "" "" FlipPix.UI.Linux/Controls/SectionHeader.axaml
# The shared theme (keyed styles → ControlThemes).
dotnet "$TOOL" --assembly "$DLL" --in FlipPix.UI/Themes/SharedStyles.xaml \
    --out FlipPix.UI.Linux/Themes/SharedStyles.axaml --kind styles --report "$REPORT" >/dev/null
echo "report: $REPORT ($(wc -l < "$REPORT") notes)"
