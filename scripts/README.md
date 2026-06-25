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

---

# Backup / Restore a working ComfyUI (clone an existing install)

Instead of installing ComfyUI from scratch, **snapshot a known-good remote install and
restore it anywhere**. This is the lowest-friction path for a new FlipPix machine: the
snapshot bundles the entire Python environment (`python_embeded`) *and* every custom node,
so restoring is literally **extract + run** — no pip, no venv, nothing to reinstall.

- **`Backup-ComfyUI.bat`** (repo root) — one-click. Double-click it. Connects to the
  remote ComfyUI over SSH and downloads a restore-anywhere bundle to
  `%USERPROFILE%\FlipPix-ComfyUI-Backup`.
- **`backup-comfyui-remote.ps1`** — the PowerShell backup the `.bat` runs (Windows side).
- **`restore-comfyui.sh`** — the restore script (runs on Ubuntu / WSL). It is copied into
  the bundle folder automatically, so the download is self-contained.

The default source is the portable **ComfyUI-Easy-Install** tree at
`~/jun1/ComfyUI-Easy-Install` on `x2@192.168.1.10` (Python 3.12 `python_embeded`, ~115
custom nodes). The no-models bundle is roughly **20–25 GB** (mostly `python_embeded` +
custom nodes).

## Back up (on Windows)

```powershell
# default: x2@192.168.1.10, ~/jun1/ComfyUI-Easy-Install, models EXCLUDED
.\scripts\backup-comfyui-remote.ps1

# write somewhere with room for ~25 GB
.\scripts\backup-comfyui-remote.ps1 -OutDir D:\backups

# a different machine / path / key
.\scripts\backup-comfyui-remote.ps1 -User bob -RemoteHost 10.0.0.5 -RemotePath ~/ComfyUI `
    -IdentityFile $env:USERPROFILE\.ssh\id_ed25519
```

What it does: opens **one** SSH connection and streams a compressed snapshot straight to a
`.tar.gz` here (no temp file on the remote; raw byte stream, no PowerShell mangling). The
snapshot includes **`python_embeded`** (the whole environment with all deps installed), the
ComfyUI source, **all custom nodes (code + git, kept as-is)**, the bundled launchers,
`user/` workflows + settings, a manifest (ComfyUI commit, python version, every node's git
remote+commit) and `requirements-freeze.txt`. Excluded by default: `__pycache__`, `*.pyc`,
`output/`, `temp/`, the machine-specific run-config dotfiles, and `models/` (on this box
`ComfyUI/models` is a symlink to external storage). Opt models in with `-IncludeModels`.

Uses the built-in Windows **OpenSSH client** (`ssh.exe`). Set up an SSH key to skip the
password prompt:

```powershell
ssh-keygen -t ed25519
type $env:USERPROFILE\.ssh\id_ed25519.pub | ssh x2@192.168.1.10 "cat >> ~/.ssh/authorized_keys"
```

## Hosting the bundle (Hugging Face)

The backup writes a `<bundle>.tar.gz.sha256` next to the archive. Upload both to a
Hugging Face model repo so users can fetch + verify + restore in one command. One-time:

```bash
pip install -U "huggingface_hub[cli]"
hf auth login
hf repo create flippix-comfyui --repo-type model
```

Upload (re-run whenever you refresh the bundle — upload under the stable name so users
can omit `--hf-file`):

```bash
hf upload <user>/flippix-comfyui flippix-comfyui-no-models-*.tar.gz        flippix-comfyui.tar.gz
hf upload <user>/flippix-comfyui flippix-comfyui-no-models-*.tar.gz.sha256 flippix-comfyui.tar.gz.sha256
```

Cloudflare R2 / Backblaze B2 work too (zero/cheap egress) — just host the `.tar.gz` +
`.sha256` and point users at the URL with `curl -L -C -`.

### Windows bundle (for users on Windows without WSL)

The Linux bundle's `python_embeded` is Linux/CUDA, so it **won't run natively on Windows**.
For bare-Windows users, publish a second, Windows bundle so they get the same extract-and-run
experience natively (no WSL):

1. On a working **Windows** ComfyUI (e.g. the portable build from `Install-ComfyUI.bat`),
   make a snapshot with the FlipPix **"Back up this ComfyUI"** button, or with
   `tar.exe -czf flippix-comfyui-windows.tar.gz -C <parent> <comfyui-folder> --exclude=*/models …`.
2. Upload it (and its `.sha256`) under a Windows name so the restore tooling finds it:
   ```bash
   hf upload <user>/flippix-comfyui flippix-comfyui-windows.tar.gz        flippix-comfyui-windows.tar.gz
   hf upload <user>/flippix-comfyui flippix-comfyui-windows.tar.gz.sha256 flippix-comfyui-windows.tar.gz.sha256
   ```

You can keep the Linux and Windows bundles in the **same** HF repo (different filenames).

## Restore (on Ubuntu / WSL)

**One command, straight from Hugging Face** (downloads if missing, verifies the sha256,
restores):

```bash
bash restore-comfyui.sh --hf <user>/flippix-comfyui
# private/gated repo: export HF_TOKEN=hf_xxx first
```

Or restore a bundle folder you copied over manually:

```bash
bash restore-comfyui.sh flippix-comfyui-no-models-YYYYMMDD-HHMMSS.tar.gz
bash restore-comfyui.sh <bundle>.tar.gz ~/ComfyUI        # custom target dir
```

### Restore on Windows (native, no WSL)

`restore-comfyui-windows.ps1` is the Windows sibling — it downloads the **Windows** bundle
from Hugging Face, verifies the sha256, and extracts it with Windows' built-in `tar.exe`:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\restore-comfyui-windows.ps1 -HfRepo <user>/flippix-comfyui
```

Then run `run_nvidia_gpu.bat` in the extracted folder and point FlipPix at `127.0.0.1:8188`.
Needs an NVIDIA GPU + driver. If no Windows bundle is published yet, it says so.

### In-app buttons (FlipPix → Settings → "ComfyUI Backup & Restore")

FlipPix's Settings window has **Set up / Restore ComfyUI** and **Back up this ComfyUI**
buttons that drive all of the above for you:

- **Set up / Restore** detects your environment: **WSL** present → runs the Linux `--hf`
  restore in a console; **no WSL** → offers the native Windows bundle (`restore-comfyui-windows.ps1`)
  or installing WSL. The Hugging Face repo is configurable in the same panel.
- **Back up this ComfyUI** snapshots the locally-configured ComfyUI path to a `.tar.gz`
  (+ `.sha256`) with `tar.exe` — use it to produce the Windows bundle to publish.

For the portable (`python_embeded`) bundle this just unpacks, makes the launchers
executable, and replaces any dangling `models` symlink with an empty `models/` dir — **no
build step**. Then launch with the bundled, VRAM-auto-detecting launcher (Enter through its
prompts for defaults):

```bash
cd <restored>/ && ./run_nvidia_gpu.sh     # interactive, auto VRAM
cd <restored>/ && ./run.sh                # non-interactive shortcut, :8188
```

ComfyUI serves on `http://0.0.0.0:8188`. **A NVIDIA GPU + recent driver is required**
(`python_embeded` ships CUDA-built PyTorch; in WSL a recent Windows NVIDIA driver exposes
the GPU). Then point FlipPix at it (host = the machine's IP, port `8188`).

If the source instead used a plain **venv** (no `python_embeded`), restore rebuilds it:
torch (`cu121`, or `--cpu`) + ComfyUI + each node's requirements. Extra flags for that
path: `--exact` (install `requirements-freeze.txt` verbatim), `--skip-deps` (extract only).
That path needs `sudo apt install -y python3 python3-venv git` first.

After restoring without models, point `ComfyUI/models` at your weights (symlink or
`extra_model_paths.yaml`) or download them with [`flippix-models.txt`](flippix-models.txt).

---

## Support

- Main [COMFYUI_SETUP.md](../COMFYUI_SETUP.md) for detailed model/workflow notes
- FlipPix issues: https://github.com/bongobongo2020/flippix/issues
- ComfyUI docs: https://docs.comfy.org/

## License

Provided for convenience in setting up FlipPix. Please respect the licenses of ComfyUI, the
custom nodes, and the models being downloaded.
