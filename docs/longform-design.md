# Long Form — H3 stories past 60 s without colour or face drift

Status: prototype tested on ubuntu-server, 2026-11-27. Workflow: `workflow/video/h3-minimax/h3-longform.json`
(API format, built by `tools/longform/build_longform.py`). Meter: `tools/longform/drift_meter.py`.

## Why FFLF drifts

1. **The hand-off frame is post-processed.** `fflf_last` reads `RTXVideoSuperResolution` (ULTRA, deblur MEDIUM, 1.2×),
   not the VAE decode. Each beat starts on a sharpened, re-toned frame; over 15 beats that compounds.
2. **Pixel round trip every beat.** decode → upscale → PNG → re-encode → frame-0 guide. Each cycle adds texture and
   palette error (the community measures roughly 1.1–1.3× fine texture per join).
3. **H3 pins visual conditions near-clean (`visual_cond_noise_aug` 0.999) from the noisiest step.** A generated frame
   fed back at frame 0 takes over the early trajectory — Context-Loop's research reproduced the red/processed drift
   and showed it disappears when the predecessor condition is noised to match the step.
4. **Correcting against the previous beat cannot work** — it has already drifted (H3MD Director's dead end). The
   anchor must be something that has not drifted: beat 1 / the opening image. Keep Reference adds the opening image
   only as a weak reference beside a much stronger frame-0 pin; Face Refine repairs only the face crop.

## The Long Form graph

One ComfyUI prompt runs the whole story on the Context-Loop pack (`MiniMaxH3Chain*`), beat by beat, recursively:

| Piece | Setting | What it fixes |
|---|---|---|
| Continuity | `MiniMaxH3AdvancedPolicy` → `color_drift_av` (Color-Stable Drift AV) | Next beat continues from the previous beat's **sampled latent** (39-frame AV prefix, no VAE round trip); the prefix is noised to the sampler's level each step (Drift-Control) and gets a low-frequency colour delta toward beat 1 |
| Identity | opening image as `<Picture 1>` on **every** beat, `ref_image_size: max`; frame-0 `AddGuide` on beat 1 only (lazy `ComfySwitchNode`) | an undrifted identity/place/palette anchor all the way through |
| Prompt | scenes 2+ say the shot *continues the motion*; `<Picture 1>` is reference only, "never jump back to that pose" | prevents snapping back to the opening pose |
| Sampling | PikaFlex prim2 Singularity, AmateurHour 0.6, shift 12/3, `res_multistep` · `simple` · 20 steps, 960×544 | Drift-Control's validated recipe |
| Delivery | two Assembles: raw, and `color_stabilization: scene_1_anchor` | optional delivery polish |

Beat lengths: Context-Loop's `duration_seconds` is the raw length including the 1.625 s carried prefix, so the builder
adds it to beats 2+. Upscale (RTX VSR, latent upscaler, SeedVR2) goes **after assembly only** — never in the loop.

## Test (same 15-beat grocery story, same model + LoRA)

Drift meter, every 3 s, against the video's own first second. dE = mean-colour distance (lower is better);
face = ArcFace cosine, best of A/B.

| window | FFLF baseline dE (2 runs) | Long Form dE raw / stab | FFLF face | Long Form face (A / B) |
|---|---|---|---|---|
| 0–10 s | 2.7 / 3.1 | 2.5 / 2.0 | 0.38 / 0.61 | 0.43 / 0.54 |
| 10–30 s | 6.6 / 5.0 | 5.5 / 4.1 | 0.06 / 0.26 | 0.10 / 0.18 |
| 30–60 s | 8.6 / 11.3 | **4.0 / 3.5** | 0.14 / 0.08 | **0.21 / 0.30** |
| 60–90 s | 8.8 / 12.8 | **4.9 / 4.5** | 0.20 / 0.11 | 0.18 / 0.11 |

Against the opening image itself, B scores 0.45 at 30–60 s (baseline ≈ 0.08).

- Colour stops compounding: Long Form stays at dE 4–5 where FFLF climbs to 9–13 (18 by 90 s).
- Visually the shopkeeper keeps his face, grey beard, navy tee and stained apron to the end, and the store layout and
  palette hold; in the baseline his apron turns black and his face and the store are redrawn by ~24 s.
- ArcFace is a noisy judge here (fight poses, profiles, small faces at 960×544); the contact sheets are the clearer
  evidence. Sharpness reads low because frame 0 is the pinned full-detail opening image; Long Form renders single-pass
  at 0.5 MP and needs the post-assembly upscale.
- Speed: 15 beats in 36 min on the 4090 (~2.2 min/beat), faster than two-pass FFLF.
- The first test delivered 73 s instead of 91 s (the prefix issue above, now fixed in the builder).

## Next

1. Quick fix in today's FFLF: take `fflf_last` from the VAE decode (`151:25`), not RTX VSR.
2. FlipPix "Long Form" mode on the FFLF tab: send the story as one Context-Loop plan; per-beat Redo = `scene_range`
   resume from the saved checkpoint; read progress from `output/h3_chains/<run>/segments`.
3. Optional re-anchor: every ~20 s pin an authored keyframe (character-sheet engine) as the beat's end frame, so
   error cannot build up beyond one segment.
4. Character sheets as extra `<Picture N>` refs (the test used only the opening image).
