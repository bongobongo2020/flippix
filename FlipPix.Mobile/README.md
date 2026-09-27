# FlipPix Mobile

A remote for the FlipPix desktop, on an Android phone. The phone asks for pictures, videos and
stories and shows what they look like as they're made. The computer makes them, with its own
ComfyUI and writing assistant settings. The Library shows everything in the computer's output
folder, not only what the phone asked for.

- `FlipPix.Mobile/`: all UI and logic (Avalonia 11, `net8.0`). It references only
  `FlipPix.Remote.Contracts`, the wire format. It has no workflows, ComfyUI client or LLM client.
- `FlipPix.Mobile.Android/`: the Android head (`net9.0-android`, min API 26). It holds no logic.
- The desktop side is `FlipPix.Remote` (see its README). Both desktop builds host it.

## Build and install

```sh
# Debug build straight onto a running emulator or a USB-debugging phone
dotnet build FlipPix.Mobile.Android -t:Install -c Debug

# A signed release APK to sideload
dotnet publish FlipPix.Mobile.Android -c Release
#   -> FlipPix.Mobile.Android/bin/Release/net9.0-android/publish/com.flippix.mobile-Signed.apk
```

The release APK is signed with the SDK's debug keystore. That works for sideloading. A Play
Store upload needs a real keystore (`AndroidSigningKeyStore` and friends).

## First run: pairing

1. On the computer, click 📱 in the Image Generator header and turn on **Let my phone connect**.
   The window shows a 6-digit code and the computer's address.
2. On the phone, FlipPix opens on **Connect to your computer**. It broadcasts on the Wi-Fi
   (UDP 47801) and lists every FlipPix that answers. Tap yours, or type the address.
3. Type the code. The phone gets a token; the computer keeps only its hash. A code pairs one phone
   and is then replaced, and five wrong tries replace it too.

Tap the computer's name in the header to see what it can do right now (ComfyUI, writing assistant,
output folder, queue) or to disconnect. Removing the phone on the computer signs it out at once.

## Pages

| Page    | What it does |
|---------|--------------|
| Library | The output folder, newest first, grouped by day, 3 per row, virtualised. All / Pictures / Videos, and one chip per top-level folder. Tap for the viewer. |
| Image   | Prompt, look (Photo, Dream, Detail), shape and count. **Polish with AI** rewrites a few words into a prompt. **From a photo** writes a prompt from a phone photo. More can be queued while one develops. |
| Video   | 1–4 photos plus a sentence give a 5/10/15 s MiniMax I2V video. **Suggest what happens** asks the writing assistant. **New take** re-renders the same scene on a new seed. |
| Story   | A story plus an optional cast gives 3/6/12 shots of 10 s. The strip shows each shot; the card plays the finished ones back to back. **Film the rest** retries unfinished shots from their written scenes. |

**The viewer** swipes between items. For a picture on the computer it offers **Describe**
(the vision model says what's in it), **Similar** (a prompt for a picture like it, into the
Image page), **Animate** (into the Video page as a reference), **Prompt** (when the phone made
it: the prompt, look and shape it was made with) and **Save** (the full file, through the system
save sheet).

## How it stays in step

- Jobs run on the computer one at a time, in order; the phone shows "2nd in line".
- The phone long-polls `GET /jobs?since=N`. The computer holds the request until something
  changes, so progress arrives live. The phone can lock, sleep or lose Wi-Fi; the job carries on,
  and the phone catches up when it asks again. Coming back to the foreground retries at once.
- Pictures come as JPEG thumbnails (360 px) and previews (1600 px), cached on disk in the app's
  cache folder and in memory (the newest 160). Videos stream straight into Android's VideoView
  with the token in the URL, since VideoView sends no headers. The computer answers ranges, so seeking works.

## Traps

- **Never touch `AppServices` (or create a `DispatcherTimer`) before Avalonia starts**, e.g. from
  `MainActivity.CustomizeAppBuilder`. `AppServices` builds `JobsHub` in a static constructor; a
  `DispatcherTimer` made that early binds Avalonia's dispatcher before the Android one exists, and
  from then on **no posted job ever runs**: no `await` continuation, no `Dispatcher.UIThread.Post`.
  The app looks alive (touch still works) but every async result is lost. That is why the device
  name lives in `Services/DeviceInfo`, and `JobsHub` makes its timer on `Start`.
- A string `Content` on a Button treats `_` as an access key: `minimax_i2v` showed as
  `minimaxi2v`. Put names in a `TextBlock`.
- Inter has no emoji; keep them out of phone text.
- API 35+ is edge to edge: `InsetsManager.DisplayEdgeToEdge = true` or the safe-area padding reads 0.
  The activity theme must derive from `Theme.AppCompat`.
- On the emulator, adb taps can start Gboard's stylus-handwriting tutorial, which eats typed text:
  `adb shell settings put secure stylus_handwriting_enabled 0`.

## Design

These are tokens, not ad-hoc colours: booth `#1B1D2A`, surface `#252838`, raised `#30344A`,
paper text `#F2EEE6`, muted `#A3A6B8`. Brand orange `#FF6B35` is used for one action per page,
with **dark** text on it (about 6.4:1; white would be 2.8:1). Glow `#FFC48A` marks work in
progress. All of them live in `App.axaml`.

A tile takes its picture's shape before the picture exists: `Controls/AspectPanel` sizes itself
from its width and a ratio, and its children can't change that. While the sampler runs, a warm fill rises inside the tile.

## Checking changes without a phone

The views can be rendered headlessly (`Avalonia.Headless` + `Avalonia.Skia`,
`UseHeadlessDrawing = false`, then `window.CaptureRenderedFrame()`) at 412×915. Call
`AvaloniaSynchronizationContext.InstallIfNeeded()` after setup, then pump
`Dispatcher.UIThread.RunJobs()` while waiting for the server. Point the phone settings file
(`%LOCALAPPDATA%\FlipPixMobile\remote.json`) at a desktop host to render real data. Do this after
any XAML change: a bad resource key compiles clean and only fails at load. Then check on the
emulator anyway: the dispatcher trap above never shows up headless.
