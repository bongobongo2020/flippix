# FlipPix UI Revamp — Action Plan

Goal: make the app lean, consistent, and easy on the eyes **without changing any
behavior or view-model logic**. The strategy is high-leverage: a central design-token
system + WPF *implicit styles* in `Themes/SharedStyles.xaml` (merged globally in
`App.xaml`) so the entire app — all 23 tabs across both monster windows — restyles at
once, instead of editing 11,700 lines by hand.

Scope: WPF project `FlipPix.UI` only. The Avalonia `FlipPix.UI.Linux` mirror is out of
scope for this pass.

Baseline: solution builds with 0 errors (69 pre-existing warnings, untouched).

---

## Phase 1 — Design foundation (global restyle)  ✅ DONE (build clean)

- [x] **1.1 Token palette.** Replace the clashing ad-hoc hexes (orange, amber, 3 blues,
  2 greens, indigo, red) with one accent + a neutral ramp + semantic success/danger,
  defined as `Color` + `SolidColorBrush` resources in `SharedStyles.xaml`.
- [x] **1.2 Re-point existing keyed styles** (`SectionPanelStyle`, `PrimaryButtonStyle`,
  `SecondaryButtonStyle`, `DangerButtonStyle`, `NavButtonStyle`, `HeaderTextStyle`) at the
  new tokens. Soften cards (lighter border, less padding/margin).
- [x] **1.3 Implicit control styles** (no `x:Key`, cascade app-wide):
  `TextBox`, `ComboBox`, `Expander`, `ProgressBar`, `TabControl`, `TabItem`.
  Flat modern tab strip with an accent underline on the selected tab.
- [x] **1.4 Flatten window headers.** Replace the orange→amber `LinearGradientBrush`
  headers in the 3 main windows with a flat brand bar; tighten padding.
- [x] **1.5 De-noise tab headers.**
- [x] **1.6 (added) Single-row scrollable tab strip.** Retemplated `TabControl`
  so 10-13 tabs scroll horizontally in one row instead of wrapping into 2-3 rows. Remove inline `FontWeight="Bold" FontSize="13"`
  from every `TabItem` (the implicit style now governs weight/size consistently).

## Phase 2 — Structural fixes  ✅ DONE (build clean)

- [x] **2.1 Fix Video window double-scroll.** `VideoGeneratorWindow` nests the whole
  `TabControl` inside `ScrollViewer > StackPanel` while each tab also scrolls. Make the
  TabControl fill a star-sized row like `ImageGeneratorWindow` does.
- [x] **2.2 Nav-bar consistency.** Stop hardcoding a different `Background` per nav
  button; use one nav style so the bar reads as one unit.

## Phase 3 — Kill duplication (proof done; rollout pending)

- [x] **3.1 Extract `ProcessingLogPanel` UserControl** — built with a `LogText`
  dependency property (each tab binds its own `*LogOutput`), plus `Header`,
  `IsLogExpanded`, `MaxLogHeight`. Wired into the FFLF tab as a verified proof
  (18-line block → 1 line). Build clean.
- [x] **3.2 Roll `ProcessingLogPanel` across the Video window.** Done — all 12
  light/collapsible log panels (Story, LTX23, Wan22, Infinite Talk, T2V, Long Video,
  WAN Scail, LTX Control, VR180, Video Sound, Seed Director, FFLF-Dasiwa) now use the
  shared control, each binding its own `*LogOutput` and preserving `Grid.Row`/`Header`/
  `MaxLogHeight`. Build clean. ~210 lines of duplicated XAML removed.
  - **Skipped Scail 2** (dark-themed tab `#0B1020` — light panel would clash).
- [x] **3.2b Image window logs reviewed — intentionally left as-is.** They are a
  *separate, internally-consistent dark terminal style* (`#111827`/`#1F2937`) and several
  are deliberately always-visible (not collapsible). Forcing the light collapsible panel
  there would change behavior and is a debatable aesthetic call, so it's out of scope for
  this pass. They don't contribute to the "busy/ugly" problem — they're already uniform.
- [ ] **3.3 `QueuePanel` UserControl — NOT recommended as a single control.** The per-tab
  queues differ heavily (columns, commands, item templates per feature), so one shared
  control would need so much parameterization it'd be worse than the duplication. Leave as
  future per-need refactors, not a blanket extraction.

## Phase 4 — Information architecture (Image window)  ✅ DONE (build clean)

- [x] **4.1 Grouped navigation.** `ImageGeneratorWindow` now shows a 3-pill group
  selector (Create / Edit / Advanced) above the tab strip; each `TabItem` is visibility-
  bound to its group (`IsCreateGroup`/`IsEditGroup`/`IsAdvancedGroup` on
  `ImageGeneratorViewModel`), so only 2-4 tabs show at once instead of all 10. Picking a
  group lands on its first tab via `SelectedNavGroup`. New `GroupPillStyle` in
  `SharedStyles.xaml`.
- [x] **4.2 Merge true duplicates — `Editor` + `Editor 2`.** Combined into one **Editor**
  tab with a Paint-mask / Auto-detect pill toggle (`EditorMode` + `IsPaintEditor`/
  `IsAutoDetectEditor`) that swaps the two existing VM-bound panels (`InpaintEditor` /
  `KleinInpaintEditor`). 10 tabs → 9.
- [x] **4.3 Tab 1 single-flow rebuild.** The two duplicate Generation-Settings panels
  (text-prompt vs image-analysis) collapsed into one shared `ContentControl` bound to
  `ActiveGenerationVM` (switches between the generator and the analyzer; both expose
  identical settings members). Shared "Generate" binds `PrimaryGenerateCommand` (aliased
  per VM to preserve each mode's action). Saved-prompts + example-prompts moved into
  collapsed expanders. Right-column queue/result kept per-mode (the two modes genuinely
  diverge: `PromptQueue` + Send-to-Camera/Video vs `QueueItems` + LM-Studio/model/reprocess).
- [x] **4.4 De-rainbow.** Removed 54 per-header inline `Foreground` overrides (headers now
  use the neutral `HeaderTextStyle`), tokenized the cream saved-prompt boxes, and unified
  66 loud button/badge backgrounds to `SuccessBrush`/`DangerBrush`/`BrandBrush`. Dark
  terminal log panels left as-is (per §3.2b).

  Remaining (other windows / future): Scail variants and `FFLF`/`FFLF-Dasiwa` live in the
  Video window and are out of scope for this Image-window pass.

---

## Verification after every phase
1. `dotnet build FlipPix.sln -c Debug` → must stay at 0 errors.
2. Launch the app, click through tabs, confirm no broken layout / missing controls.

## Rollback
Each phase is a separate commit; the token file is additive, so reverting one commit
restores the prior look without touching view-models.
