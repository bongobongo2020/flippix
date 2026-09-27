# FlipPix.Remote

The desktop half of the phone remote: a small LAN web server inside FlipPix (WPF and Linux
builds alike), the jobs it runs for the phone, and an index of the output folder. The phone app
is `FlipPix.Mobile`; the wire format both share is `FlipPix.Remote.Contracts`.

## Turning it on

Image Generator header → 📱 → **Let my phone connect**. The setting and the paired phones live in
`%AppData%\FlipPix\remote.json` (not in `settings.json`, whose Save rewrites every field). Jobs,
uploads and thumbnails live in `%LocalAppData%\FlipPix\remote\`.

- TCP **47800**: the API (Kestrel). UDP **47801**: discovery.
- Windows asks once whether FlipPix may use the network; allow private networks.
- The Linux package needs `aspnet-runtime-8.0` (in the PKGBUILD); the Windows publish is
  self-contained, so it carries ASP.NET Core with it.

## Layout

| Folder     | What |
|------------|------|
| `Host/`    | `RemoteHost` (the window binds to it; start/stop, pairing code, devices), `RemoteEndpoints` (routes), `DiscoveryResponder` (UDP), `RemoteConfig`. |
| `Jobs/`    | `JobManager` (queue, one worker, long-poll revisions, `jobs.json`/`made.json`), the three runners, `UploadStore`. |
| `Library/` | `LibraryIndex` (background scan of the output folder), `Thumbnailer` (ImageSharp for pictures, poster PNG or ffmpeg for videos). |
| `Engine/`  | The recipes the phone used to run itself: `ImageLooks`, `VideoRecipe`, `StoryRecipe`, `ComfyGateway`, `LlmClient`, and a stand-in `LMStudioService` for the linked story chain. |

The story chain (`StoryBeatSheet`, `StoryContinuity`, `ClipChainWriter`, `LlmSampling`) is compiled
in from `FlipPix.UI/Services` as linked source. That puts types named `FlipPix.UI.Services.*` in
this assembly, so **both desktop projects reference it with `Aliases="remote"`**, and only their
`Services/PhoneRemote.cs` names its types (`extern alias remote;`). Keep those linked files BCL-only.

## Settings it follows

Each job reads the desktop's settings when it starts: `BaseUrl` for ComfyUI, `LMStudioSettings`
(BaseUrl + SelectedModel) for the writing assistant, and `ResolveOutputFolder(isRemote)` for the
library. It has its own ComfyUI client, without the desktop's missing-model/missing-node
resolvers: those open dialogs, and nobody is at the desktop to answer them.

## API (all under `/api/v1`)

`GET /hello` and `POST /pair` are open; everything else needs `Authorization: Bearer <token>` or
`?access_token=` (for video players).

| Route | |
|-------|--|
| `GET /status` | ComfyUI reachable, LLM label, output folder, queue counts |
| `POST /uploads` | a photo (raw body) → id; stored normalised (EXIF applied and stripped, ≤1536 px, JPEG 92) |
| `GET /jobs?since=N&wait=25` | long poll: returns when the revision passes N |
| `POST /jobs` | `JobRequest` (image, video or story) → `JobDto` |
| `POST /jobs/{id}/cancel`, `/retry`, `DELETE /jobs/{id}` | |
| `GET /jobs/{id}/items/{i}/file\|thumb\|preview` | from the output folder when visible, else proxied from ComfyUI `/view` with Range passed through |
| `GET /library?kind&folder&offset&limit` | newest first, plus folder counts |
| `GET /library/{id}` and `/{id}/file\|thumb\|preview` | id = base64url of the relative path, checked to stay inside the folder |
| `POST /assist` | polish, image-prompt, describe, video-idea (the desktop's LLM; pictures by reference) |

## Things that bit

- **Video Helper Suite writes three files per video**: `x.mp4` (silent), `x-audio.mp4` and `x.png`
  (first frame). The library lists one entry: the audio file, with the PNG as its poster.
- **The desktop copies finished videos** into named folders (`MiniMaxI2V/` beside `minimax_i2v/`,
  `H3Express/` beside `h3_express/`) with the same size and timestamp, so the index de-duplicates
  on (kind, size, mtime). 1,267 entries became 881 on this machine.
- **Scanning `Z:\output` over SMB takes ~9 s**, so requests never wait for a scan (at most 3 s on
  the very first). A finished job adds its file to the index directly. An SMB client caches "not
  found" for a few seconds, so that add retries, and a scan that began before the file existed
  keeps it.
- **ComfyUI progress is reported inline** (`InlineProgress`). `Progress<T>` posts each step to
  the thread pool, where steps overtake each other and a late one can arrive after the item was
  marked done. `JobContext.Report` also drops steps for items no longer working.
- **Start and stop run on the thread pool**: building the web host freezes a window otherwise.
  `PhoneRemote.Stop` blocks on disposal from a pool thread, since blocking the UI thread on a task
  that resumes there deadlocks until the timeout.
