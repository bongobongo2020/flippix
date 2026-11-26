# FFLF bake-off — 2026-11-26

49 setups, each rendering the same two beats of the open story (supermarket fight, beat 1 = 4 s, beat 2 = 6 s
starting on beat 1's last frame), 16:9, 0.3 → 1.2 MP, frame 0 locked, full-size references, both character sheets,
seed 424242/424243 (seed-2 runs: 777001/777002). Graphs built by the app's own `FflfGraph.Build`, rendered on the
RTX 4090 at 10.0.0.10.

- **like**: ArcFace similarity of each person's face to their face in the opening image, averaged over frames where
  they are recognisable (A = the woman, B = the man). Higher is better; 0.5+ is a clear match at these face sizes.
- **seen**: share of frames where that person's face is recognisable at all (low = blurred, turned away or melted).
- **exec**: ComfyUI execution time for both beats (10 s of video). Everything includes the 3-step refine pass and decode.
- **move**: subject motion (optical flow, camera move removed). **warp**: how much the flow *cannot* explain —
  morphing, smearing, limbs popping. Want high move with low warp.

| setup | like | A | B | seenA | exec s | move | warp |
|---|---|---|---|---|---|---|---|
| **D** 10Eros Max 8-step hybrid-activation, no LoRA, **6 steps** | **0.60** | 0.53 | 0.67 | 0.78 | 182 | 3.3 | 6.9 |
| D same, 8 steps (3 runs: 2 seeds; w4a8 / nvfp4 / w4g128 encoders) | 0.57–0.58 | 0.47–0.54 | 0.62–0.68 | 0.67–0.87 | 198–205 | 3.0–3.6 | 7.0–7.6 |
| **N** b25 hybrid + TaoMate 3-step, 3 steps (2 seeds) | 0.54–0.56 | 0.42–0.49 | 0.63–0.66 | 0.87–0.89 | **172** | 2.3–2.7 | 7.5 |
| W fl2va + turbo-8 1.0 + wushu 0.8 + physics 0.3, triggers | 0.56 | 0.46 | 0.67 | 0.89 | 232 | 3.1 | 7.7 |
| **BW_N** N + Bunny weapon combat 0.45 (refine 0.25) | 0.54 | 0.47 | 0.60 | 0.84 | 180 | 2.8 | 8.5 |
| N + Action A3 mix | 0.53 | 0.48 | 0.58 | 0.76 | 182 | 3.2 | 10.2 |
| N + DaSiWa 0.6 / SMACK 0.6 / A2 mix | 0.51–0.52 | 0.41–0.45 | 0.57–0.63 | 0.76–0.89 | 173–178 | 3.0–3.1 | 9.0–9.8 |
| W b25 / fl2va + Martial tab authored stack | 0.51–0.52 | 0.45 | 0.57–0.59 | 0.78–0.91 | 233–236 | 3.1–3.5 | 8.4–9.1 |
| D + Action A2 mix | 0.51 | 0.46 | 0.57 | 0.67 | 225 | 4.4 | 10.4 |
| B app default (PikaFlex + DMAD 4-step 0.25), 2 seeds | 0.48–0.50 | 0.40–0.41 | 0.56–0.59 | 0.51–0.56 | 218 | 4.2–4.5 | 10.6–11.2 |
| E fused refdelta mystic, 2 seeds | 0.49 | 0.41–0.43 | 0.55–0.58 | 0.82–0.89 | 198 | 3.4–3.5 | 9.2–9.8 |
| G b25 + lightx2v 4-step v1.0, 4 steps (2 seeds; 3 encoders; 6 steps) | 0.47–0.51 | 0.38–0.43 | 0.55–0.59 | 0.80–0.98 | 178–200 | 2.8–3.6 | 10.3–11.7 |
| M b25 + Pika 8-step HD 1.0 (2 seeds) | 0.47–0.49 | 0.39–0.41 | 0.54–0.57 | 0.60–0.73 | 244–247 | 2.1–2.2 | 7.5 |
| P ref2va + Pika 1.0 | 0.49 | 0.45 | 0.52 | 0.96 | 248 | 1.9 | 6.4 |
| DMAD DARE-TIES (PikaFlex 0.25 / ref2va 4-step / b25 4-step) | 0.45–0.49 | 0.38–0.41 | 0.53–0.60 | 0.33–0.51 | 179–219 | 4.0–4.4 | 12.4–13.4 |
| Pika + DARE-TIES 0.25 on b25 | 0.47 | 0.40 | 0.55 | 0.87 | 254 | 2.3 | 8.7 |
| F/I/K/L turbo-8 LoRA on b25 / b15 / fl2va (+ realism) | 0.46–0.48 | 0.39–0.42 | 0.52–0.57 | 0.67–0.80 | 228–230 | 2.6–3.5 | 9.5–10.4 |
| C 10Eros b7 turbo (the workflow's authored model) | 0.47 | 0.41 | 0.53 | 0.44 | 211 | 3.7 | 10.9 |
| J ref2va + ref2v lightx2v 4-step | 0.46 | 0.41 | 0.51 | 0.47 | 182 | 4.2 | 9.1 |
| S Singularity PikaFlex + DARE-TIES 0.25 + human motion 0.9 + physics 0.3 + Motion Repair 0.5 + Fight Flow 0.65 (± soft refine) | 0.45 | 0.37–0.40 | 0.50–0.52 | 0.49–0.62 | 224–228 | 4.9–5.0 | 12.0–12.5 |
| Bunny weapon on fl2va 0.45 / 0.8, on Pika 0.45 | 0.43–0.44 | 0.36–0.39 | 0.48–0.53 | 0.62–0.78 | 230–259 | 3.4–4.5 | 8.7–11.6 |
| Combat V2 1.0 on fl2va (BUNNY stack mix) | 0.42 | 0.38 | 0.47 | 0.38 | 236 | 5.2 | 15.8 |
| G + Action A2 mix; Pika + A2 / A3 | 0.41–0.45 | 0.36–0.41 | 0.46–0.49 | 0.31–0.33 | 183–260 | 3.3–5.0 | 10.4–16.8 |
| **A your current FFLF settings** (b25 + camera motion 0.9 + Combat 0.35 + Motion Repair 0.25, 16 steps) | 0.46 | 0.41 | 0.52 | **0.16** | **290** | 5.0 | 10.0 |

Full numbers: `results/scores.json`; frame sheets: `results/<setup>/contact.jpg` and `action.jpg`.

Caveats: one opening image, one story, two seeds for the leaders and one for the rest — differences under ~0.03
like are within seed noise. Character sheet A is itself only 0.51 like the opening face, which caps A's scores.

# 🧾 Character-sheet engines — 2026-11-26

7 people from 4 photos (`sheet_tests.json`: both supermarket images, a single portrait, a couple), 2 seeds each,
graphs built by the app's own `FflfSheetGraph.Build` (`sheets.py`). **best** = ArcFace likeness of the sheet's
most similar face to that person's face in the photo (usually the close-up panel); **mean** = over every face on
the sheet that is that person; **wrong** = faces closer to the *other* person in the photo. Scorer: `sheet_score.py`.

| engine | best | mean | wrong | s / sheet |
|---|---|---|---|---|
| **Qwen Image 2.1 Edit, reference 2048** (new default) | **0.73** | **0.49** | 0 | 27 |
| Qwen Image 2.1 Edit, reference 1536 | 0.71 | 0.48 | 0 | 19 |
| Qwen Image 2.1 Edit, reference 1024 | 0.60 | 0.41 | 0 | 15 |
| Qwen Image 2.1 Edit, photo at its own size (old graph) | ~0.66 | 0.44 | 0 | 16–41; **~600 on a 20 MP photo** |
| FireRed Image Edit 1.1 (8-step lightning) | 0.51 | 0.36 | 7 | 16 |
| Flux2 Klein 9B | 0.49 | 0.30 | 0 | 9 |
| Qwen Image Edit 2511 (8-step lightning) | 0.46 | 0.31 | 1 | 9 |
| Flux2 Klein 9B True v2 | 0.39 | 0.26 | 0 | 17 |
| FIBO 2 | 0.38 | 0.29 | 2 | 3 |

FIBO 2 edits pixel for pixel: it crops the photo to the wide sheet and keeps the photo's pose (a crouching
"close-up"). Comparison sheets: `results/sheets/compare_<image>_<person>.jpg`.

# 🥷 Martial pipeline — 2026-11-26

The same two beats, opening image and character sheets (sent as labelled Martial references), rendered with the
Martial tab's own `MartialGraph.Build` (`martial.py`, `martial_configs.json`): 0.3 → 1.2 MP, refine 0.6 / 10 steps
unless noted, one seed. Martial's finish is 1504×832 with no RTX step, against FFLF's 1808×1000, so faces are ~17%
smaller. Some of the likeness gap to the FFLF table is resolution, not quality. Compare Martial rows with each other.

| setup | like | seenA | exec s | move | warp |
|---|---|---|---|---|---|
| MT TaoMate + Bunny 0.45, Bunny on pass 1 only | 0.44 | 0.36 | 325 | 2.4 | 8.3 |
| MT TaoMate + Bunny 0.45 (the tab now) | 0.42 | 0.42 | 324 | 2.4 | 8.0 |
| **MT same, refine 6 steps** | 0.42 | 0.51 | **232** | 2.5 | 7.9 |
| MT TaoMate only | 0.42 | 0.80 | 322 | 1.6 | 7.0 |
| MT b25 + lightx2v 4-step | 0.37 | 0.18 | 336 | 2.4 | 10.2 |
| MT authored stack (turbo-8 0.8, physics, wushu 0.4, camera), triggers | 0.37 | 0.16 | 386 | 2.9 | 8.5 |
| MT TaoMate + Bunny at 0.8 MP first pass | 0.36 | 0.49 | 433 | 2.0 | 8.3 |
| MT wushu 0.8 + turbo-8 + physics, triggers | 0.35 | 0.42 | 380 | 2.9 | 9.2 |
| MT 10Eros hybrid-act, 6 steps | 0.34 | 0.16 | 325 | 3.1 | 9.0 |
| *for reference, FFLF tab: TaoMate + Bunny (BW_N)* | *0.54* | *0.84* | *180* | *2.8* | *8.5* |
