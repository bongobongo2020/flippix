# FlipPix Setup

## Install FlipPix (one click)

New to FlipPix? **Double-click `Install-FlipPix.bat`** in the repo root. It opens a retro
Windows 98-style setup wizard that:

- lets you choose the install folder (defaults to `%LOCALAPPDATA%\Programs\FlipPix`, no admin needed),
- creates desktop / Start Menu shortcuts,
- can **also install ComfyUI** for you (ticking the box launches the ComfyUI installer below),
- copies FlipPix and can launch it when done.

FlipPix is published self-contained, so end users need **no .NET runtime**. If the repo already
has a built `publish\` folder it's used directly; otherwise the wizard builds it with
`dotnet publish` (that build step needs the .NET 8 SDK).

The wizard is `scripts\flippix-installer.ps1` (WinForms, intentionally classic-themed). The `.bat`
just launches it with the right execution policy in STA mode.

---

# ComfyUI Setup

There is **one** ComfyUI installer: it provisions a fresh, self-contained ComfyUI, installs every
custom-node pack the FlipPix workflows need, and (optionally) downloads the model weights.

- **`Install-ComfyUI.bat`** (repo root) — the one-click entry point. Double-click it.
- **`setup-comfyui-fresh.ps1`** — the PowerShell installer the `.bat` runs. Windows only
  (it uses the official ComfyUI Windows portable build with bundled Python + torch/CUDA).
- **`flippix-custom-nodes.txt`** — the curated custom-node list (one git URL per line).
- **`flippix-models.txt`** — the model manifest (`path | size | url` per line). Single source
  of truth for which weights get downloaded; edit URLs here once.

## One-click install

Just **double-click `Install-ComfyUI.bat`** in the repo root. It runs the PowerShell installer
with the correct execution policy, so you never need to open a terminal.

To pass options, run it from a prompt (or append them after the filename):

```powershell
# default install to %USERPROFILE%\ComfyUI_FlipPix
Install-ComfyUI.bat

# custom location
Install-ComfyUI.bat -InstallDir D:\AI\ComfyUI
```

You can also call the PowerShell script directly:

```powershell
# from the flippix repo root
powershell -ExecutionPolicy Bypass -File scripts\setup-comfyui-fresh.ps1

# add FlipPix nodes to a ComfyUI portable you already have
.\scripts\setup-comfyui-fresh.ps1 -ExistingComfyDir "C:\ComfyUI_windows_portable"
```

## What it does

1. Ensures `git` and a 7-Zip extractor are available (auto-downloads `7zr.exe`; tries `winget` for git).
2. Downloads the official ComfyUI Windows portable build (bundles embedded Python + torch/CUDA).
3. Extracts it into the install directory.
4. Installs ComfyUI-Manager + all curated node packs from `flippix-custom-nodes.txt`, plus their
   Python requirements, into the embedded Python.
5. Runs ComfyUI-Manager's `cm-cli` over the actual workflow files to auto-install any niche packs
   not in the curated list.
6. Copies the FlipPix workflow library into the install.
7. **Models:** asks for your *current* ComfyUI models folder. If it already exists, the new
   install is pointed at it (via `extra_model_paths.yaml`) and **nothing is downloaded**. Only
   if the folder doesn't exist does it offer to create it and download the FlipPix models
   (~45 GB, listed in `flippix-models.txt`) there.

## Model options

```powershell
# reuse weights you already have, download nothing
.\scripts\setup-comfyui-fresh.ps1 -ModelsDir "C:\ComfyUI_windows_portable\ComfyUI\models"

# unattended: create + download if the folder is missing
.\scripts\setup-comfyui-fresh.ps1 -ModelsDir D:\AI\models -DownloadModels

# don't touch models at all
.\scripts\setup-comfyui-fresh.ps1 -SkipModels
```

## Requirements

- Windows 10/11
- ~60 GB free disk space (models ~45 GB, ComfyUI + nodes ~5 GB, working space ~10 GB)
- Stable internet connection
- `git` (the installer offers to install it via `winget` if missing)

`curl.exe` (built into modern Windows) is used for fast, resumable downloads.

## After installation

1. **Launch ComfyUI:** run `run_nvidia_gpu.bat` in the install's portable root.
2. **First launch** loads all custom nodes (1–2 minutes) — watch the console for any that fail.
3. **Open** http://127.0.0.1:8188 and load a FlipPix workflow to verify (no red "missing" nodes).
   If a node is still missing, use Manager → *Install Missing Custom Nodes*, then restart.
4. **Point FlipPix** at this ComfyUI folder when prompted on startup.

If you skipped the model download, just re-run the installer — finished files are skipped and
interrupted ones resume.

## Troubleshooting

- **Git not found:** install from https://git-scm.com/download/win (select "Git from the command
  line…"), reopen the terminal, and re-run. The installer also tries `winget install Git.Git`.
- **Download fails / times out:** re-run the installer; it resumes partial files and skips
  completed ones. For a stubborn file, grab it manually from the URL in `flippix-models.txt`.
- **Red "missing" nodes after launch:** open the workflow, use Manager → *Install Missing Custom
  Nodes*, then restart ComfyUI.
- **Not enough disk space:** install to a larger drive with `-InstallDir`, and/or reuse an
  existing models folder with `-ModelsDir` so weights aren't duplicated.

## Support

- Main [COMFYUI_SETUP.md](../COMFYUI_SETUP.md) for detailed model/workflow notes
- FlipPix issues: https://github.com/bongobongo2020/flippix/issues
- ComfyUI docs: https://docs.comfy.org/

## License

Provided for convenience in setting up FlipPix. Please respect the licenses of ComfyUI, the
custom nodes, and the models being downloaded.
