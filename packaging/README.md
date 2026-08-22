# FlipPix on Arch Linux

FlipPix's Linux build is `FlipPix.UI.Linux`, an Avalonia port of the WPF app. It talks to the
same ComfyUI server, reads the same `workflow/` and `prompts/` trees, and stores its settings in
the same JSON shape.

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

These were **not** run against a live Arch machine — the build was cross-compiled from Windows and
verified by compiling and publishing only. Please confirm on real hardware:

1. **Launch** — `flippix` opens the splash and then the main window; no GL errors in
   `~/.local/state/FlipPix/logs`. If the window is black, retry with `FLIPPIX_SOFTWARE_RENDER=1`.
2. **ffmpeg discovery** — the log line `Using ffmpeg from /usr/bin` appears at startup. With
   ffmpeg uninstalled you should get the warning naming `pacman -S ffmpeg`, not a crash.
3. **Single config dir** — after a run, `ls ~/.config` shows `FlipPix` and **no** lowercase
   `flippix`. Two directories would mean a casing regression has come back.
4. **Dialogs** — a message box (e.g. submit with no ComfyUI server) appears and dismisses without
   freezing the UI. A hang here means the nested dispatcher frame in `WindowsCompat.cs` regressed.
5. **File pickers** — browse for an image, reopen the same button, and confirm it reopens in the
   previous folder; confirm it survives a restart (persisted per `persistKey` in `settings.json`).
6. **Open / reveal** — "Open folder" launches your file manager; "reveal" selects the file in
   Nautilus/Dolphin/Nemo/Thunar, or at minimum opens the containing folder.
7. **Video post-processing** — render something that merges chunks, and confirm ffprobe-derived
   dimensions and durations are correct rather than zero.

## Known gaps

- **The ported tabs have no UI yet.** All 25 ViewModels from the WPF build (the H3/MiniMax family,
  Ideogram, QwenEdit, Scail2, VideoSound, VR180, ImageUpscaler, ErosConvRot, FaceIdCharSheet,
  Restore, InpaintEditor, KleinInpaint) compile and are ready, but they are not yet constructed by
  the host ViewModels or given XAML tabs. WPF's two windows are ~12,800 lines of XAML against
  Avalonia's ~1,600.
- **Ten stale bindings** in `VideoGeneratorWindow.axaml` reference `Story*` members that do not
  exist on the ViewModel (`StoryAddToQueueCommand`, `StoryProcessQueueCommand`, `StoryLogOutput`,
  and so on). These predate this work; Avalonia's runtime bindings fail silently, so that part of
  the Story panel is inert.
- **`MissingModelResolver` / `MissingNodeResolver`** are not ported — they drive WPF dialogs, so
  mid-submit model and node installation is unavailable on Linux.
- **`WanScailViewModel`'s base workflow**,
  `workflow/video/wan/SCAIL+Video+Multi-Character+Motion+Transfer+V1API.json`, is absent from the
  repo on both platforms. Only the GGUF subclass, which uses `SCAIL2_simple (1).json`, resolves.
