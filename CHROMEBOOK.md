# FlipPix on a Chromebook

FlipPix runs on a Chromebook (Chromebook Plus or any model with Linux support) in ChromeOS's
**Linux development environment**. It is the same app as on Windows — every window and tab of the
Image Generator, Video Generator and Enhance Video — built from the same view models and workflows.

The heavy lifting (image and video generation) happens in **ComfyUI on a PC with an NVIDIA GPU**.
A Chromebook has no such GPU, so FlipPix connects to ComfyUI over your network, exactly as the
Windows app does with its "Remote Server" option.

## 1. Turn on Linux

Settings → About ChromeOS → Developers → **Linux development environment** → Turn on.
Give it at least 10 GB of disk.

## 2. Install FlipPix

Open the **Terminal** app and run:

```bash
git clone https://github.com/bongobongo2020/flippix
cd flippix
./install-chromebook.sh
```

The script installs ffmpeg, libvlc (video playback) and the emoji font, builds FlipPix (it fetches a
.NET SDK into `~/.dotnet` the first time), and installs it to `~/.local/opt/flippix`. FlipPix then
shows up in the ChromeOS launcher under **Linux apps**; from a terminal, run `flippix`.

To update later: `git pull && ./install-chromebook.sh --no-deps`.
To remove: `./install-chromebook.sh --uninstall` (your settings in `~/.config/FlipPix` are kept).

## 3. Connect to ComfyUI on your PC

On the PC, start ComfyUI listening on the network:

```
python main.py --listen 0.0.0.0 --port 8188
```

(The FlipPix Windows installer's ComfyUI can do the same — add `--listen 0.0.0.0` to its launch
script.) Allow port 8188 through the PC's firewall.

FlipPix reads the generated files from ComfyUI's **output folder**, so the Chromebook needs to see
that folder:

1. On the PC, share ComfyUI's `output` folder (right-click → Properties → Sharing).
2. On the Chromebook, open **Files** → ⋮ → **Services** → **SMB file share** (or *Add network file
   share*) and add `\\<PC name or IP>\output`.
3. Right-click the new share in Files → **Share with Linux**. It appears under
   `/mnt/chromeos/SMB/…` inside Linux.

First launch asks *Where is your ComfyUI installed?* → choose **Remote Server**, enter
`http://<PC IP>:8188`, and pick the shared folder under `/mnt/chromeos`. The same fields are in
⚙️ Settings later, along with the LM Studio / llama-server address used for prompt analysis.

Pictures and videos you want to use as inputs need to be somewhere Linux can see: the Linux files
folder, or any Files folder you **Share with Linux**.

## Notes

- **Video playback** is in-app (libvlc), with sound through ChromeOS.
- **Display scaling** follows ChromeOS. If FlipPix looks blurry, right-click it in the launcher and
  untick *Use low density* (or set `GDK_SCALE`/`AVALONIA_GLOBAL_SCALE_FACTOR` in `~/.local/bin/flippix`).
- **Graphics acceleration**: the Linux container renders on the CPU unless ChromeOS exposes the GPU
  to it; FlipPix detects this and falls back to software rendering automatically.
- **Logs**: `~/.local/state/FlipPix/launch.log` and `~/.config/FlipPix/Logs/`.
- Windows-only maintenance tools in Settings (WSL setup, the Windows ComfyUI bundle restore) explain
  what to do instead; *Back up ComfyUI* works with the system `tar`.

## How the Linux build is put together (for maintainers)

`FlipPix.UI.Linux` compiles the Windows app's own `ViewModels`, `Models` and `Services` folders
(see its `.csproj`), so every tab behaves exactly as on Windows. WPF APIs those classes touch —
`MessageBox`, `Dispatcher`, `BitmapImage`, brushes, file dialogs, the dialog windows they open — are
provided by `FlipPix.UI.Linux/Compat`. The windows are Avalonia XAML generated from the WPF XAML by
`tools/xaml-port`; after changing a WPF window, re-run `tools/xaml-port/port-all.sh` and port any
code-behind changes by hand (`FlipPix.UI.Linux/Windows/*.axaml.cs`).
