; FlipPix Installer Script for Inno Setup
; Download Inno Setup from: https://jrsoftware.org/isdl.php

#define MyAppName "FlipPix"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "FlipPix"
#define MyAppURL "https://github.com/bongobongo2020/flippix"
#define MyAppExeName "FlipPix.UI.exe"

[Setup]
; NOTE: The value of AppId uniquely identifies this application.
AppId={{A5E8B9C3-4D2F-4A1E-9B3C-7F6E8D9A2B1C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
; Uncomment the following line to run in administrative install mode
;PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=.
OutputBaseFilename=FlipPix-Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; Uncomment to use custom icon
;SetupIconFile=flippix.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "installcomfyui"; Description: "Install a minimal ComfyUI now (image generation + editing). Auto-detects your GPU VRAM and downloads ComfyUI + core models (~23 GB)."; GroupDescription: "ComfyUI backend:"; Flags: unchecked

[Files]
Source: "publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "publish\workflow\*"; DestDir: "{app}\workflow"; Flags: ignoreversion recursesubdirs createallsubdirs
; Exclude .pdb files and output folder (and all its contents)
Source: "publish\*"; DestDir: "{app}"; Excludes: "*.pdb,output,output\*"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "flippix.png"; DestDir: "{app}"; Flags: ignoreversion
Source: "INSTALL.txt"; DestDir: "{app}"; Flags: ignoreversion
; ComfyUI setup scripts — let the in-app "Install minimal ComfyUI now" button (and manual
; double-click) work from an installed copy. The bats expect scripts\ next to them ({app}\scripts),
; and setup-comfyui-fresh.ps1 reads its manifests + the {app}\workflow tree from there.
Source: "Install-ComfyUI-Minimal.bat"; DestDir: "{app}"; Flags: ignoreversion
Source: "Install-ComfyUI.bat"; DestDir: "{app}"; Flags: ignoreversion
Source: "Install-ComfyUI-WSL.bat"; DestDir: "{app}"; Flags: ignoreversion
Source: "Backup-ComfyUI.bat"; DestDir: "{app}"; Flags: ignoreversion
Source: "scripts\*"; DestDir: "{app}\scripts"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; If "Install ComfyUI" was ticked, run the minimal installer in its own console after files are
; copied. It auto-detects VRAM, downloads ComfyUI + core models, and writes the install path +
; auto-start script into FlipPix settings, so the app then auto-detects and launches ComfyUI on
; first run (no hunting for a .bat). nowait so Setup doesn't block on the long download;
; runascurrentuser so an admin (Program Files) install still targets the real user's profile/AppData.
Filename: "{app}\Install-ComfyUI-Minimal.bat"; WorkingDir: "{app}"; StatusMsg: "Starting the minimal ComfyUI installer in a new window..."; Tasks: installcomfyui; Flags: runascurrentuser nowait
; Launch FlipPix on finish — but not when we just kicked off the ComfyUI install (open FlipPix once
; that finishes and it will auto-detect + auto-start ComfyUI).
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Tasks: not installcomfyui; Flags: nowait postinstall skipifsilent

[Code]
procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpWelcome then
  begin
    WizardForm.NextButton.Caption := SetupMessage(msgButtonNext);
  end;
end;
