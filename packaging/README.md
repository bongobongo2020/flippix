# FlipPix on Arch Linux

FlipPix's Linux build is `FlipPix.UI.Linux`, an Avalonia port of the WPF app. It talks to the
same ComfyUI server, reads the same `workflow/` and `prompts/` trees, and stores its settings in
the same JSON shape.

**Just want it installed?** `../install-arch.sh` does everything below in one command — pacman
dependencies, `makepkg -si`, desktop entry — and `--uninstall` reverses it. Pass `--local` to
install into `~/.local` without root or a pacman package. The rest of this file is the manual
route and the things the one-liner can't tell you.

## Build

```bash
sudo pacman -S --needed dotnet-sdk-8.0 ffmpeg
./packaging/build-linux.sh              # framework-dependent, needs dotnet-runtime-8.0
./packaging/build-linux.sh --self-contained   # bundles the runtime instead
./packaging/build-linux.sh --tarball          # also writes flippix-linux-x64.tar.gz
```

Run it without installing:

```bash
./launch-linux.sh
```

## Package

```bash
cd packaging/arch
makepkg -si
```

The PKGBUILD builds from your working tree (it locates the repo root from `$startdir`). To
package a different checkout, set `_repo`:

```bash
_repo=/path/to/flippix makepkg -si
```

Installed layout:

| Path | Contents |
| --- | --- |
| `/usr/lib/flippix/` | assemblies, `libSkiaSharp.so`, `workflow/`, `prompts/` |
| `/usr/bin/flippix` | launcher script |
| `/usr/share/applications/flippix.desktop` | desktop entry |
| `/usr/share/pixmaps/flippix.png` | icon |

The package is framework-dependent (~57 MB) and depends on `dotnet-runtime-8.0`, so the runtime
gets security updates through pacman rather than being vendored.

## Runtime configuration

| Variable | Effect |
| --- | --- |
| `FLIPPIX_FFMPEG` | Absolute path to `ffmpeg`, overriding PATH discovery |
| `FLIPPIX_FFPROBE` | Absolute path to `ffprobe` |
| `FLIPPIX_SOFTWARE_RENDER=1` | Skip GLX/EGL and render in software |
| `DOTNET_ROOT` | .NET install root (defaults to `/usr/share/dotnet`) |

### Where files go

FlipPix follows the XDG Base Directory spec:

| Kind | Location |
| --- | --- |
| Settings, prompt history, scene library | `$XDG_CONFIG_HOME/FlipPix` (`~/.config/FlipPix`) |
| Persisted queues | `~/.config/FlipPix/queue` |
| Logs | `$XDG_STATE_HOME/FlipPix/logs` (`~/.local/state/FlipPix/logs`) |
| Generated stills | `~/Pictures/flippix-images` |
| Generated clips | `~/Videos/flippix-vids` |

Install `xdg-user-dirs` if you want the last two to follow your localized folder names; without
it they fall back to literal `Pictures`/`Videos` under `$HOME`.

### Wayland

Avalonia 11.2 has no Wayland backend, so a Wayland session runs FlipPix through XWayland. That is
transparent apart from fractional scaling, which the X11 backend reads from the usual
`GDK_SCALE` / `QT_SCALE_FACTOR` environment variables.

## Verification checklist

Verified on a real Arch machine (2026-08-23, RTX 4090 render box at `10.0.0.10:8188` running
ComfyUI 0.33.0). Items still worth re-checking after changes:

1. **Launch** — `flippix` opens the splash and then the main window; no GL errors in
   `~/.local/state/FlipPix/logs`. If the window is black, retry with `FLIPPIX_SOFTWARE_RENDER=1`.
   ✅ verified
2. **ffmpeg discovery** — the log line `Using ffmpeg from /usr/bin` appears at startup. With
   ffmpeg uninstalled you should get the warning naming `pacman -S ffmpeg`, not a crash.
3. **Single config dir** — after a run, `ls ~/.config` shows `FlipPix` and **no** lowercase
   `flippix`. Two directories would mean a casing regression has come back. ✅ verified
4. **Dialogs** — a message box (e.g. submit with no ComfyUI server) appears and dismisses without
   freezing the UI. A hang here means the nested dispatcher frame in `WindowsCompat.cs` regressed.
5. **File pickers** — browse for an image, reopen the same button, and confirm it reopens in the
   previous folder; confirm it survives a restart (persisted per `persistKey` in `settings.json`).
6. **Open / reveal** — "Open folder" launches your file manager; "reveal" selects the file in
   Nautilus/Dolphin/Nemo/Thunar, or at minimum opens the containing folder.
7. **Video post-processing** — render something that merges chunks, and confirm ffprobe-derived
   dimensions and durations are correct rather than zero. ✅ verified (all seven H3 tabs rendered
   end-to-end; chunk joins and the H3 Chain assembly produce playable, correctly-sized clips)
8. **The tabs** — open each of the nine video tabs and the ten pill-grouped image tabs, and
   confirm they render rather than throwing at window load. ✅ verified via `FLIPPIX_SMOKE=1`
   (opens both windows, logs the tab census, exits) plus live renders driven by
   `tools/render-harness`, which exercises the real generate pipelines per tab.
9. **Poster frames** — load a clip in Scail 2 and confirm a frame appears in the preview and
   follows the scrub slider. An empty black panel with only the filename means ffmpeg was not
   found or could not read the container.

## Known gaps

- **No video plays inside the app.** Avalonia has no MediaElement, so every tab that showed a
  clip now uses `Controls/VideoPreview`: ffmpeg grabs a poster frame, the play button hands the
  file to the desktop's own player, and a scrub slider moves the poster rather than a playhead.
  Where WPF let you scrub a live preview (Scail 2's base scene, Qwen Edit's base video), the
  slider drives the poster frame instead — the still under the playhead is the frame those tabs
  actually work from, so the flow survives, but it does not animate.
- **The mask painter is FlipPix's own.** Avalonia has no InkCanvas, so the Editor tab paints on
  `Controls/MaskPaintCanvas`, which keeps strokes as points and rasterizes them at the source
  image's resolution. Pressure and stylus tips are not modelled — a stroke is a round-capped line
  of the chosen width.
- **The ComfyUI Backup & Restore settings panel is Windows-only** — it drives the Windows
  installer bundle. Everything else in Settings is at parity, including the VRAM tier selector.
- **Four ViewModels still have no tab**: `Vr180ViewModel`, `VideoSoundViewModel`,
  `FaceIdCharSheetViewModel` and `MiniMaxH3TextToVideoViewModel` are no longer constructed —
  the same four are built and never bound on Windows.
- **The window/tab set matches the WPF app exactly**: the nine video tabs in the same order, the
  ten image tabs grouped through the same Create / Edit / Advanced pills, and the legacy
  Avalonia-era video tabs (FFLF, Story Video, VACE, Infinite Talk, WanAnimate, WAN SCAIL) are
  gone. Their ViewModels stay in the tree for one deprecation cycle.
- **`MissingModelResolver` / `MissingNodeResolver` are ported** — mid-submit model and node
  installation works, with Linux ComfyUI python layouts recognized
  (`venv/bin/python`, `.venv/bin/python`, `/usr/bin/python3`) for targeted pip fixes. A remote
  server can't be auto-repaired (no local `custom_nodes` to clone into) — the dialog points at
  ComfyUI-Manager instead, as on Windows.
- **`WanScailViewModel`'s base workflow**,
  `workflow/video/wan/SCAIL+Video+Multi-Character+Motion+Transfer+V1API.json`, is absent from the
  repo on both platforms. Only the GGUF subclass, which uses `SCAIL2_simple (1).json`, resolves.

## Porting more tabs

`tools/port_tab_to_avalonia.py` lifts one `<TabItem>` out of a WPF window and writes an Avalonia
UserControl:

```bash
python tools/port_tab_to_avalonia.py \
    --source FlipPix.UI/VideoGeneratorWindow.xaml --start 57 --end 490 \
    --class Scail2View --datacontext Scail2VM \
    --out FlipPix.UI.Linux/Views/Video/Scail2View.axaml
```

It handles the mechanical differences — keyed styles to ControlThemes, `Visibility` to
`IsVisible`, triggers to `IsVisible` bindings or `Classes.x` plus an inline style, tooltips,
`DisplayMemberPath`, `ItemContainerStyle` — and prints what it could not: MediaElements it
swapped for `VideoPreview`, controls Avalonia does not have, and the code-behind handlers the new
view needs. Always read the generated file afterwards; the report is a to-do list, not a receipt.

The design tokens live in `FlipPix.UI.Linux/Themes/SharedStyles.axaml` under the same `x:Key`
names as the WPF `SharedStyles.xaml`, which is what lets a tab transcribe across nearly verbatim.
