# FlipPix Mobile

A remote for the FlipPix desktop, on an Android phone or an iPad. The phone asks for pictures, videos and
stories and shows what they look like as they're made. The computer makes them, with its own
ComfyUI and writing assistant settings. The Library shows everything in the computer's output
folder, not only what the phone asked for.

- `FlipPix.Mobile/`: all UI and logic (Avalonia 11, `net8.0`). It references only
  `FlipPix.Remote.Contracts`, the wire format. It has no workflows, ComfyUI client or LLM client.
- `FlipPix.Mobile.Android/`: the Android head (`net9.0-android`, min API 26). It holds no logic.
- `FlipPix.Mobile.iOS/`: the iPad (and iPhone) head (`net9.0-ios`, iOS 15+): the app delegate, the
  device name and an AVPlayer for videos. It is not in `FlipPix.sln`, since iOS builds only on a Mac
  and the solution has to keep building on Windows.
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

### iPad

Needs a Mac with Xcode. Each .NET iOS workload is tied to one Xcode: 18.5 wants Xcode 16.4, 26.x
wants Xcode 26. Pin the workload set to match (`dotnet workload update --version 9.0.304` gives iOS
18.5), or the build stops with "requires Xcode 26.5".

```sh
# Simulator
dotnet build FlipPix.Mobile.iOS -c Debug -r iossimulator-arm64
xcrun simctl install booted FlipPix.Mobile.iOS/bin/Debug/net9.0-ios/iossimulator-arm64/FlipPix.Mobile.iOS.app
xcrun simctl launch booted com.flippix.mobile

# A real iPad, plugged in: provisions, builds, signs, installs and launches
FlipPix.Mobile.iOS/deploy-ipad.sh
```

`deploy-ipad.sh` works with a free Apple ID (Personal Team) signed in to Xcode. .NET can't fetch a
provisioning profile, so the script builds an empty Xcode app (via `xcodegen`) with the same bundle
id and lets `xcodebuild -allowProvisioningUpdates` make the certificate, register the iPad and
fetch the profile. A free team needs a bundle id of its own, so the device build is
`com.zaheerahmed.flippix` (`BUNDLE_ID=` to change it), and its profile lasts 7 days: run the
script again to renew. It passes `ValidateXcodeVersion=false`, since the pinned workload builds
fine with Xcode 27. On the iPad: turn on Developer Mode, and trust the Apple ID under
Settings > General > VPN & Device Management the first time.

## The iPad studio layout

`Views/AppShell` picks the layout by width: under 700 points the phone layout (`MainView`), from
700 up the studio (`Views/Studio/`). That is every iPad in either orientation and most Split View
sizes. Both read the same view models, so a resize across the line keeps what was typed and queued.

- A sidebar in place of the bottom bar: the pages, **Now making** (whatever the computer is
  working on, from any page; tap to go there), and the computer with its connection state.
- Each making page is a composer panel beside its results: the prompt, look and shape on the left
  with the orange button at its foot; the contact sheet, takes or films on the right.
- The Library grid fits as many ~210 pt columns as the width allows (`LibraryViewModel.Columns`).
- The viewer is full screen, with an inspector beside the picture (below it when narrower than
  1000 pt) that spells out what each action does.
- Below 1100 pt the shell wears the `compact` class: the sidebar folds to a rail and the composer
  narrows. Styles are in `Styles/Studio.axaml`; the phone's stay in `App.axaml`.
- With a keyboard: ⌘1–⌘4 switch pages, ⌘Return makes, Esc backs out, ← → walk the viewer.
- Hover states everywhere, since an iPad is often driven by a trackpad.
- The studio takes the safe area itself (`StudioShell.ApplyInsets`), so the sidebar and viewer run
  under the status bar and home indicator; the phone layout is padded by `AppShell`.

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
save sheet; on iPhone and iPad, straight into Files › On My iPad › FlipPix, on that device only).

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
- **iPad discovery**: iOS asks for Local Network permission the first time, and on a real device a
  UDP broadcast also needs Apple's multicast entitlement (`com.apple.developer.networking.multicast`),
  which has to be requested. Without it the "On this Wi-Fi" list stays empty; typing the address
  always works. The simulator found nothing either in testing.
- iOS reaches the computer over plain HTTP: `Info.plist` allows local networking and arbitrary loads.
- `*.png` is git-ignored repo-wide: the iOS icon (`Assets.xcassets/AppIcon.appiconset/icon-1024.png`,
  rendered from `Views/Studio/BrandMark`) must be added with `git add -f`, as the Android one was.
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
`UseHeadlessDrawing = false`, then `window.CaptureRenderedFrame()`) at 412×915, and host
`AppShell` rather than `MainView` at iPad sizes (1376×1032, 1032×1376, 744×1133) to see the studio. Call
`AvaloniaSynchronizationContext.InstallIfNeeded()` after setup, then pump
`Dispatcher.UIThread.RunJobs()` while waiting for the server. Point the phone settings file
(`%LOCALAPPDATA%\FlipPixMobile\remote.json`) at a desktop host to render real data. Do this after
any XAML change: a bad resource key compiles clean and only fails at load. Then check on the
emulator anyway: the dispatcher trap above never shows up headless.
