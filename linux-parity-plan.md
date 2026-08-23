# Linux ⇄ Windows Parity Plan

Goal: make the Linux build (`FlipPix.UI.Linux`, Avalonia) accurately mimic every window and tab
of the Windows build (`FlipPix.UI`, WPF), **including the full MiniMax H3 workflow family**, and
stop shipping the stale, divergent UI that is currently installed.

---

## 1. Diagnosis — why the installed Linux app feels broken

### 1.1 The installed binary is the wrong generation of the app

| Fact | Value |
|---|---|
| Installed app | `~/.local/opt/flippix/FlipPix.UI.Linux` (self-contained publish) |
| Built | Aug 23, 10:53 — from `~/flippix/publish-linux` |
| Built from | the **stale Linux project on the default branch** (`flippix-prompt-image`) |
| Proves it | the installed DLL contains `VACE`/`WanAnimate` strings and **zero** `H3Cast`/`MiniMax` strings |

The repo contains **two generations** of the Linux UI, and the checkout on the default branch has
the old one:

| | Windows (WPF, `FlipPix.UI`) — **canonical = newer branch build** | Linux (Avalonia, `FlipPix.UI.Linux`) **installed build (default branch)** |
|---|---|---|
| Video tabs | Scail 2, 10Eros ConvRot, **MiniMax I2V**, **MiniMax FFLF**, **MiniMax Character**, **H3 Cast**, **H3 Cast Hybrid**, **H3 Ensemble**, **H3 Chain** — 9 tabs, 7 of them MiniMax-H3-family | FFLF, Story Video, VACE, Infinite Talk, WanAnimate, WAN SCAIL — **no MiniMax/H3 anything** |
| Image tabs | Image Generator, Story Image Q, Amateur, Camera Angle, Editor, Control, Ideogram, Qwen Edit, Restore, Image Upscaler — grouped by Create/Edit/Advanced pills | Image Generator, Story Image Q, Analyze, Control, Log — ungrouped, most tabs missing |
| Design | unified design system (`SharedStyles.xaml`) | old orange-gradient "Camera Edit" chrome |
| Safety nets | missing-model/node auto-install, VRAM tier routing (16 GB graphs), scene prompt library | none of these |

### 1.2 The real port already exists — on the wrong branch

`origin/h3cast-tagged-references` (8 commits, Aug 14–22, forked at `e4a8dbd`) contains a
near-complete, synchronized port:

- **`bc8c4e6` / `57c9e92` — "port the missing tabs / remaining WPF tabs to Avalonia."** Every
  current Windows tab becomes a `Views/*View.axaml` UserControl bound to its own sub-VM:
  Scail 2, 10Eros ConvRot, MiniMax I2V (the evolved MiniMax H3 tab), MiniMax FFLF, MiniMax
  Character, H3 Cast, H3 Cast Hybrid, H3 Ensemble, H3 Chain + Amateur, Camera Angle, Editor,
  Ideogram, Qwen Edit, Restore, Image Upscaler.
- Ported design system (`Themes/SharedStyles.axaml`, same resource keys as WPF), controls
  (`VideoPreview`, `MaskPaintCanvas`, `ProcessingLogPanel`, `SectionHeader`), and services
  (`VramContext`, `WorkflowLocator`, `ScenePromptLibrary`, `ChunkPromptCacheService`,
  `CharacterSheetSplitter`, `CastPromptStamp`, `HybridCastPrompt`, `UserPaths` …).
- Packaging that actually works on Arch: `install-arch.sh` (pacman/makepkg or `--local`),
  `packaging/` (PKGBUILD, `build-linux.sh`), `launch-linux.sh`, XDG-conformant paths.
- Its own README documents the **remaining gaps** honestly (see §6).

The default branch never received any of this. Its one Linux-side commit (`bc00e12`, the
first-run shutdown-race fix) is *newer* than the branch and **must be preserved** in the merge —
the branch still has the race (`App.axaml.cs` there has no `ShutdownMode` handling).

### 1.3 Environment fallout visible on this machine

- **Two config directories**: `~/.config/FlipPix` *and* lowercase `~/.config/flippix`
  (window positions). Windows folds case together, ext4 doesn't — fixed on the branch by
  `Services/UserPaths.cs`, still broken in the installed build.
- **Logs inside the config dir** (`~/.config/FlipPix/Logs`) instead of
  `~/.local/state/FlipPix/logs` (XDG state) — also fixed by `UserPaths`.
- Desktop entry is a hand-rolled copy pointing at the local publish; the branch ships a proper
  one via packaging.
- Two repo copies exist (`~/Projects/flippix` on the default branch, `~/flippix` used to build) —
  consolidate to one before rebuilding.

---

## 2. Canonical tab set — **DECIDED: the newer branch (9 video tabs, MiniMax I2V)**

The branch moved the **Windows** UI forward too. The maintainer's call: **the
`h3cast-tagged-references` generation is canonical** — the Windows version to mimic is the one
whose video window has 🌀 **MiniMax I2V** (references + continuations + queue), *not* the older
single-shot 🌀 MiniMax H3 tab. Background for the record:

| Video tabs | Default branch (older) | `h3cast-tagged-references` ← **canonical** |
|---|---|---|
| Scail 2, 10Eros ConvRot, MiniMax FFLF, MiniMax Character, H3 Cast, H3 Chain | ✅ | ✅ |
| 🌀 MiniMax H3 (single-shot I2V) | ✅ (`MiniMaxH3ViewModel`) | **removed** — superseded by **🌀 MiniMax I2V** (same H3 model/workflows, richer flow) |
| 🪪👥⚡ H3 Cast Hybrid, 🎬🎭 H3 Ensemble | — | ✅ (new) |
| 🌀 MiniMax H3 T2V, 🥽 VR 180, 🔊 Video Sound, 🪪 FaceID Char Sheet | VM built, **no tab** on Windows | VM built, no tab on either side |

Consequences for the rest of this plan:

- The classic **MiniMax H3** and **MiniMax H3 T2V** tabs are **out of scope**. `MiniMaxH3ViewModel`
  no longer exists on the branch (verified), and `MiniMaxH3TextToVideoViewModel` survives there
  only as unbound dead code — it joins the Phase 1 deletion list.
- Nothing needs re-porting for this decision: the branch's Linux build already has all seven
  H3-family tabs (I2V / FFLF / Character / Cast / Cast Hybrid / Ensemble / Chain). The decision
  only removes optional work and settles Phase 1's deletion list.
- Image window canonical set: the 10 tabs listed in §1.1, including **🔍 Image Upscaler** (Edit
  group) — it ships on the branch's Windows build and its Linux view is already ported.

---

## 3. Phase 0 — Land the existing port (repo reconciliation)  · *~0.5 day*

1. **Consolidate checkouts**: work in `~/Projects/flippix`; archive or update `~/flippix`.
2. **Merge**:
   ```bash
   git checkout flippix-prompt-image
   git merge origin/h3cast-tagged-references
   ```
   Expected conflict surface: `FlipPix.UI.Linux/App.axaml.cs` (branch rewrite vs `bc00e12`
   startup fix) and `README.md`. Resolution: take the branch's file, then re-apply `bc00e12`'s
   behavior — `ShutdownMode.OnExplicitShutdown` for the setup→main-window handoff,
   `OnLastWindowClose` after the main window shows, and non-swallowed `ShowMainWindowAsync`
   (no `_ =` fire-and-forget).
3. **Build & smoke-test on Linux**:
   ```bash
   ./packaging/build-linux.sh && ./launch-linux.sh
   ```
4. **Reinstall locally** over the stale build (and delete it first, so old assemblies don't
   linger): `./install-arch.sh --local --self-contained`, or keep the pacman route.
5. **Housekeeping on this machine**: remove `~/.config/flippix` (lowercase) after confirming
   nothing valuable is in it; move `~/.config/FlipPix/Logs` → `~/.local/state/FlipPix/logs`
   (the new build writes there itself).
6. Push the merge so the default branch is never this far behind again.

**Acceptance**: app launches to the Image Generator window; the Video Generator window shows the
nine ported tabs; `MiniMax`, `H3Cast`, `Scail2` strings present in the installed DLL; only one
config dir exists after a run.

---

## 4. Phase 1 — Make the Linux windows mimic the Windows windows exactly  · *~2–3 days* — **✅ DONE (2026-08-23)**

> Completed in one pass. Smoke-verified tab census (via the new `FLIPPIX_SMOKE=1` hook):
> Image window **10 tabs** in canonical order incl. ✏️ Editor; Video window **exactly 9**
> canonical tabs. Legacy VM classes kept on disk one deprecation cycle (nothing constructs
> them); the six legacy video tabs, the Analyze/Log tabs and their pass-through regions
> are gone, and the dead `NavigateToImageGeneratorCommand` binding was replaced with a
> working handler.

The port currently *adds* the new tabs *next to* the old ones instead of *replacing* the old UI.

### 4.1 Video Generator window (`Windows/VideoGeneratorWindow.axaml`)

1. **Remove or retire the legacy tabs** that Windows does not have: `FFLF`, `Story Video`,
   `VACE`, `Infinite Talk`, `WanAnimate`, `WAN SCAIL` (they sit after the ported tabs today).
   Preferred: delete the tab XAML + the parent-VM pass-through properties that feed them
   (the brittle `MainVM Backward Compatibility Properties` region that caused the stale-binding
   bugs like the VACE-tab one), and keep the VMs behind a hidden `--legacy-tabs` setting until
   the next release. Tab order must match Windows exactly.
2. **Delete the four dead sub-VMs** (`Vr180VM`, `VideoSoundVM`, `FaceIdCharSheetVM`,
   `MiniMaxH3T2VVM`) — constructed and never bound on either platform (decision §2).
3. Header/nav parity: same title text, same `Navigate:` bar as WPF (`Image Generator` button,
   `SelectedTabIndex` persistence).

### 4.2 Image Generator window (`Windows/ImageGeneratorWindow.axaml`)

1. **Add the Create / Edit / Advanced group pills** (WPF: `GroupPillStyle` RadioButtons bound to
   `SelectedNavGroup` → `IsCreateGroup`/`IsEditGroup`/`IsAdvancedGroup` in
   `ImageGeneratorViewModel`; port the same properties + `IsVisible` bindings on each `TabItem`).
   Verified: the branch's Linux `ImageGeneratorViewModel` has **none** of these flags today — the
   window's own XAML comment admits the tabs are "simply shown" ungrouped.
2. **Drop tabs the canonical Windows build doesn't have**: `Analyze` and `Log` (fold Analyze into
   the Image Generator tab's "Image Analysis" input mode, as Windows does; Log belongs in each
   tab's Processing Log panel). Reorder to the canonical physical order — Image Generator,
   Story Image Q, Amateur, Camera Angle, Editor, Control, Ideogram, Qwen Edit, Restore,
   Image Upscaler — with the pills filtering Create / Edit / Advanced visibility exactly as WPF
   does.
3. **Nav bar parity**: keep only `🎬 Video Generator`, `✨ Enhance Video`, the pills, `⚙️
   Settings`, `✕ Exit`. Remove the buttons that open Linux-only side windows (`📷 Camera Edit`,
   `🔊 I2V2A`, `📖 Story Video`, `🧠 Ollama`, `🔍 Analyze`) — Camera Angle is already a tab; I2V2A /
   Story Video / Ollama duplicate functionality that lives in tabs on Windows (retire the
   windows + VMs + DI registrations after a deprecation release, or hide behind the legacy flag).
4. Apply `SharedStyles` tokens to the remaining old-chrome areas (header, nav, status bar) so
   the two builds are visually indistinguishable.

### 4.3 Enhance Video, Settings, dialogs

- Enhance Video already has Interpolate/Upscale — verify SeedVR2 option parity with Windows.
- Settings: see Phase 3.
- Keep `ScenePromptLibraryWindow` (already ported) reachable from the MiniMax Character tab like
  on Windows.

**Acceptance**: side-by-side screenshots of the three windows match the Windows build tab-for-tab,
pill-for-pill, button-for-button; no bindings silently fall back to the parent VM (grep each
legacy tab name in the XAML to prove removal).

---

## 5. Phase 2 — MiniMax H3 workflow functional parity  · *~2 days (mostly QA)` — **4/7 rendered ✅ (2026-08-23), 3 blocked server-side**

> Server: `10.0.0.10:8188` (ComfyUI 0.33.0, RTX 4090 23.5 GB → full-fat tier). All five H3 graph
> validators (`tools/verify_h3*.py`) pass against the live server, plus the two image-side ones.
> Live renders via `tools/render-harness` (dev-only VM driver, committed): **i2v 140 s/5.6 MB ✅ ·
> fflf 124 s/3.3 MB ✅ · hybrid 242 s/12.9 MB ✅ · ensemble 327 s/14.7 MB ✅**.
>
> Bugs found & fixed along the way:
> - `LTX_lora_loader` missing required `mode` — `h3-cast-hybrid.json` node 5, `plagueh3.json`
>   node 5511 → now `"minimax"` (every Hybrid/Ensemble submit would have been rejected).
> - `SaveVideo` missing newly required `codec.encoding.color_space` — `h3-cast-hybrid.json`
>   node 38, `plagueh3.json` node 5480 → now `"auto"` (found by live render, not validators).
> - False positive in `verify_h3cast_tagged.py` (RTXVideoSuperResolution is dynamic-combo).
>
> **Blocked server-side**: MiniMax Character, H3 Cast, H3 Chain all fail at
> `MiniMaxH3MemoryEfficientSageAttentionPatch` — "sageattention is not new enough version or
> could not determine CUDA architecture" on 10.0.0.10 (their Ref2VA-family graphs hardwire that
> node; the four passing tabs' graphs don't use it). Fix on the server — upgrade sageattention in
> ComfyUI's python env (`pip install -U sageattention`), then re-run harness stages
> `character`, `cast`, `chain`. No SSH access to the box from here (publickey only).

Workflows are shared JSON — the risk is not the graphs but everything around them. For **each**
H3 tab (MiniMax I2V, MiniMax FFLF, MiniMax Character, H3 Cast, H3 Cast Hybrid, H3 Ensemble,
H3 Chain), run one full job on Linux and confirm:

1. **Analyze** — the tab's system prompt loads from `prompts/prompt2json/`, the configured LLM
   server (LM Studio / Ollama / llama-server profile) is called, and the four-block H3 prompt
   renders in the editor for manual editing.
2. **Patch** — `WorkflowNodeUpdater` injects prompt/seed/resolution/duration/LoRAs/references
   into the right nodes of the correct `workflow/video/h3-minimax/*.json`; 16 GB tier routing
   picks `workflow/16gb` variants when `VramContext` says so (verify in the log line).
3. **Queue** — item lands in the per-tab queue, persists across restart
   (`~/.config/FlipPix/queue`), pauses/resumes/cancels; `WorkflowQueueCoordinator` serializes
   across tabs.
4. **Execute & collect** — progress streams over WebSocket; outputs are pulled from `/history`
   (video *and* audio), poster frame previews appear, FFmpeg joins/chains complete (H3 Chain's
   continuous take, FFLF's keyframe chain, Character's multi-clip story run, Cast's sheet-driven
   clips with wardrobe stamping).
5. **Uploads** — character refs / scene images / first-last frame pairs upload and are referenced
   as `<Picture n>` correctly (H3 Cast sheet generation via Qwen-Image-Edit + `CharacterSheetSplitter`).

Fix anything that diverges; the port was verified by headless instantiation only, so expect
binding bugs to surface here rather than at build time.

---

## 6. Phase 3 — Close the documented Linux gaps  · *~3–4 days*

Priority order (from the branch's own "Linux gaps" list):

1. **Missing-model / missing-node resolvers** — port `ModelInstallerService`,
   `NodeInstallerService`, `MissingModelResolver`, `MissingNodeResolver` and the
   `MissingModelsWindow` / `MissingNodesWindow` dialogs to Avalonia (MsBox or proper windows),
   then wire `httpClient.MissingModelResolver/MissingNodeResolver` in `App.axaml.cs` exactly
   like the WPF `App.xaml.cs` does. This is the biggest functional gap: on Windows a missing
   checkpoint offers to download/locate/register; on Linux the submit just fails.
2. **Settings window parity** — port the missing sections: GPU VRAM tier selector, crash
   detection & auto-restart (+ a `.sh` restart script field instead of `.bat`), remote output
   folder, remote LoRA folder, Krea2 LoRA folder, LLM server profiles. (ComfyUI backup/restore
   can stay Windows-only initially.)
3. **In-app video playback** — current `VideoPreview` shows ffmpeg poster frames only. Either
   accept and document it, or integrate `LibVLCSharp.Avalonia` behind the same control interface
   (recommended: keeps scrub previews working where VLC can't seek frame-accurately).
4. **Mask painter** — `MaskPaintCanvas` is functional; add pressure/stylus support only if
   tablets are in scope.

---

## 7. Phase 4 — Packaging, docs, CI  · *~0.5 day*

1. Standardize on `packaging/build-linux.sh` + `install-arch.sh` for this machine; regenerate
   the desktop entry (`MimeType`, `StartupNotify` etc. from `packaging/arch/flippix.desktop`).
2. Update the root `README.md` Linux-gaps section as items close; update `packaging/README.md`
   verification checklist results.
3. Add a GitHub Actions workflow: `dotnet build FlipPix.UI.Linux` + publish on `ubuntu-latest`
   (and ideally `archlinux:base` container) so the Linux project can never silently rot again —
   the root cause of this whole situation.

---

## 8. QA matrix

| # | Check | Pass criterion |
|---|---|---|
| 1 | First run, remote server path | setup → main window handoff; no vanish (the `bc00e12` race) |
| 2 | One config dir | `ls ~/.config` shows `FlipPix` only; logs under `~/.local/state/FlipPix/logs` |
| 3 | Tab census | Video window shows exactly the 9 canonical tabs (Scail 2 → H3 Chain) in order; Image window shows the 10 canonical tabs, pill-grouped; no legacy tabs remain |
| 4 | Each H3 tab end-to-end | one generated clip with audio per tab; queue persists across restart |
| 5 | Missing model | submit with an uninstalled checkpoint → resolver dialog offers install |
| 6 | 16 GB tier | log line shows memory-optimized workflow when tier is forced |
| 7 | Legacy removal | no legacy tab names in XAML; no pass-through `MainVM` region |
| 8 | Headless view test | every `Views/*View` instantiates without missing-resource exceptions |
| 9 | Poster frame / player | Scail 2 scrub slider moves the frame; play hands off to desktop player |
| 10 | Merge integrity | `git log` shows port + fix; `dotnet build` clean on Linux |

---

## 9. Order of work & effort

| Phase | What | Effort |
|---|---|---|
| 0 | Merge the port, rebuild, reinstall (§3) | 0.5 d |
| 1 | Tab/nav/pill parity + legacy removal (§4) | 2–3 d |
| 2 | H3 functional QA + fixes (§5) | 2 d |
| 3 | Resolvers, settings, playback (§6) | 3–4 d |
| 4 | Packaging/docs/CI (§7) | 0.5 d |

Phase 0 alone replaces the "broken" installed app with the real port (all H3 tabs present);
Phases 1–2 make it an accurate mimic; Phase 3 closes the functional gaps Windows users get.

## 10. Risks

- **Merge conflict** in `App.axaml.cs` — resolution defined in §3.2; keep the race fix.
- **Removing legacy tabs** may orphan users of VACE/Mocha/LTX2Audio/WanAnimate — gate behind a
  `--legacy-tabs` flag for one release before deleting VMs/windows.
- **Hidden binding bugs** in ported tabs (headless tests can't see them) — that's what Phase 2's
  per-tab end-to-end runs are for.
- **Workflow drift** — the branch added/changed `h3-minimax/*.json`; don't hand-merge workflow
  JSONs, take the branch's versions wholesale.
