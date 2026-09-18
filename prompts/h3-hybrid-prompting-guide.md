# Hybrid FL + ref prompting

Use with `--mode r2v --unet hybrid --turbo-profile lightx-8step-pk`.  
Playbook: [`compositions/hybrid-fl-ref.md`](compositions/hybrid-fl-ref.md).

This is the **documented exception** to “do not mix I2VA/FL alignment with R2V six-section.” Alignment lives **inside** `detailed_description`. Extra pictures exist only on the R2V node.

## Rules

1. Six sections in order: `subject_definitions` → `summary` → `retention_analysis` → `detailed_description` → `overall_soundscape` → `non_diegetic_music`.
2. `summary` starts with `[keyframe completion + reference generation]`.
3. `--ref-image` order **is** `<Picture 1>`, `<Picture 2>`, … Connection order, 1-based.
4. Each timeline picture: one job, `fully_preserved` **at its timestamp**. A cut replaces pose, wardrobe, **and** background.
5. Extra non-timeline refs (garment, close-up face, identity sheet) get a job that is **not** a keyframe: `partially_preserved`, “never a keyframe”, “do not insert as any video frame.”
6. Timeline stills that share a timestamp path must share **shot class**. Extra refs **may** be a different class (product close-up, face crop) because they are not cut-ins.
7. With images present, lock with `exactly as shown in <Picture N> without reinterpretation` — do not re-describe geometry.
8. Do not invent outfits. Do not keep the previous shot’s room after a cut.
9. `non_diegetic_music: N/A` unless the user asked for a score.

## Template (9 s, three cuts at 0 / 3 / 6)

```
subject_definitions:
<Picture 1> is the opening keyframe for [Shot 1] at 0.00 seconds. Job: exact first frame only — pose, wardrobe, and background as shown, without reinterpretation.
<Picture 2> is the cut-in keyframe for [Shot 2] at 3.00 seconds. Job: exact Shot 2 frame only — pose, wardrobe, and background as shown, without reinterpretation.
<Picture 3> is the cut-in keyframe for [Shot 3] at 6.00 seconds. Job: exact Shot 3 frame only — pose, wardrobe, and background as shown, without reinterpretation. Not a continuation of <Picture 2>.
<Subject 1> is the same adult in all three pictures. Face and identity stay consistent; wardrobe and background switch with each picture.

summary:
[keyframe completion + reference generation] Generate a nine-second live-action reel of <Subject 1> with three hard cuts. At 0.00 seconds the frame is exactly <Picture 1>. At 3.00 seconds cut to exactly <Picture 2>. At 6.00 seconds cut to exactly <Picture 3>. Then continue from <Picture 3> through 9.00 seconds with no end-frame lock. Each cut replaces pose, outfit, and background together.

retention_analysis:
<Picture 1> (appears in [Shot 1] at 0.00s): fully_preserved - opening frame lock; Shot 1 set and wardrobe only.
<Picture 2> (appears in [Shot 2] at 3.00s): fully_preserved - middle cut-in lock; Shot 2 set and wardrobe only.
<Picture 3> (appears in [Shot 3] at 6.00s): fully_preserved - third cut-in lock; Shot 3 set and wardrobe only; not a 9.00 end-frame lock; not a style transfer onto <Picture 2>.
<Subject 1> (appears in [Shot 1], [Shot 2], [Shot 3]): fully_preserved - same person; each shot matches that shot's picture exactly.

detailed_description:
How the reference pictures align with the target video — Picture 1 (from Shot 1) aligns with the 0.00-second mark of the target video; Picture 2 (from Shot 2) aligns with the 3.00-second mark of the target video; Picture 3 (from Shot 3) aligns with the 6.00-second mark of the target video.
The target video is live-action and cinematic. No on-screen text. No extra people. Do not invent a new outfit. Do not blend rooms across cuts.
[Shot 1] A static camera. At 0.00 seconds the frame is exactly <Picture 1> without reinterpretation. <Subject 1> holds that pose and breathes once. Wardrobe and background remain as in <Picture 1> only.
[Shot 2] At 00:03.000, a hard cut. The frame is exactly <Picture 2> without reinterpretation. The camera stays static. Small motion only (head turn, hair, breath).
[Shot 3] At 00:06.000, a hard cut. The frame is exactly <Picture 3> without reinterpretation. Replace the previous shot completely. The camera stays static. Small motion continues until 9.00 seconds with no end-frame lock.

overall_soundscape:
Quiet indoor room tone, a soft breath at each cut, light fabric rustle.

non_diegetic_music:
N/A
```

## Two-still first + last

Same six sections. Picture 1 at **0.00**, Picture 2 at **S.SS** (clip duration), `fully_preserved` as opening and ending locks. Prefer a **single continuous take** (camera motion, no cuts) unless the user asked for cuts. Extra refs start at Picture 3.

## Extra-ref patterns (not cut-ins)

Use when Picture 1 is the **only** timeline lock. Remaining pictures steer quality (hidden garment, close-up face) without becoming frames. Continuous take. Alignment names **only** Picture 1 at 0.00s.

| Pattern | Picture 1 | Extra pictures | When |
|---------|-----------|----------------|------|
| **Garment-only** | Opening lock (full scene) | Picture 2 = underwear / outfit item | Reveal or swap clothes that are not visible (or not detailed) on the opener |
| **Garment + face** | Opening lock, often **wider** | Picture 2 = garment; Picture 3 = tight face crop | Zoom/orbit toward the face; opener is too far for likeness |

Shared extra-ref wording:

- Job: one attribute only (garment **or** face). Not a person-as-scene. Not a pose. Not a background.
- Retention: `partially_preserved` — never `fully_preserved` as a timestamp lock.
- Alignment: “Picture N does not align with any timestamp as a frame.”
- Name what **stays** vs what **comes off** (e.g. crop top stays; mini skirt off → Picture 2 thong).

### Template — first frame + garment-only (8 s)

```
subject_definitions:
<Picture 1> is the opening keyframe for [Shot 1] at 0.00 seconds. Job: exact first frame only — pose, visible wardrobe, and background as shown, without reinterpretation. Not a last-frame lock.
<Picture 2> is a close-up garment reference. Job: underwear / outfit-item appearance only (cut, color, fabric as shown). Not a person. Not a pose. Not a background. Not a timeline keyframe. Do not insert <Picture 2> as any video frame.
<Subject 1> is the same adult from <Picture 1> throughout. Face, body, and location stay hers; only the named outer garment comes off to reveal the item from <Picture 2>.

summary:
[keyframe completion + reference generation] Generate an eight-second live-action clip of <Subject 1>. At 0.00 seconds the frame is exactly <Picture 1>. One continuous take, no hard cuts, no end-frame lock. She removes the named outer garment and reveals the item from <Picture 2> worn underneath. The camera pans and orbits around her body. <Picture 2> is a wardrobe reference only and must never appear as a cut-in still.

retention_analysis:
<Picture 1> (appears in [Shot 1] at 0.00s): fully_preserved - opening frame lock; set, pose, and visible wardrobe at 0.00 seconds only.
<Picture 2> (never a keyframe): partially_preserved - retain only the garment appearance so the reveal matches <Picture 2>; do not reproduce the product close-up as a frame; do not replace the location, face, or body.
<Subject 1> (appears in [Shot 1]): fully_preserved - same person and location; wardrobe change is the named reveal only.

detailed_description:
How the reference pictures align with the target video — Picture 1 aligns with the 0.00-second mark of the target video as the exact first frame. Picture 2 does not align with any timestamp as a frame; it is a hidden-garment reference used only after the named outer garment is removed. There is no last-frame lock at 8.00 seconds.
The target video is live-action and cinematic. No on-screen text. No extra people. Do not invent a different garment. Do not cut to <Picture 2>. Do not change the location from <Picture 1>.
[Shot 1] One single continuous take. At 0.00 seconds the frame is exactly <Picture 1> without reinterpretation. Brief hold, then she removes the named outer garment and reveals the item exactly as shown in <Picture 2>. Camera slowly pans and orbits (front, three-quarter, back). After the reveal she stays in that garment, same set, until 8.00 seconds. No hard cuts. No end-frame lock.

overall_soundscape:
Ambience matching the opening still, fabric rustle, quiet breath.

non_diegetic_music:
N/A
```

### Template — first frame + garment + close-up face (8 s)

Same as garment-only, plus Picture 3. Use when a zoom toward the face would otherwise drift off the opener.

```
subject_definitions:
<Picture 1> is the opening keyframe for [Shot 1] at 0.00 seconds. Job: exact first frame only — pose, visible wardrobe, and background as shown, without reinterpretation. Not a last-frame lock. This still may be a wider / more zoomed-out framing.
<Picture 2> is a close-up garment reference. Job: underwear / outfit-item appearance only. Not a person. Not a pose. Not a background. Not a timeline keyframe. Do not insert <Picture 2> as any video frame.
<Picture 3> is a close-up face identity reference of the same adult. Job: facial identity only (face shape, features, skin, likeness as shown) so that when the camera zooms in, her face stays hers. Not a pose lock. Not a wardrobe lock. Not a background. Not a timeline keyframe. Do not insert <Picture 3> as a cut-in still or replace the scene with a face-crop layout.
<Subject 1> is the same adult throughout. Body, visible opener wardrobe, and location come from <Picture 1>. Face must stay consistent with <Picture 3> especially as the lens gets closer. After the named outer garment comes off she wears the item from <Picture 2>.

summary:
[keyframe completion + reference generation] Generate an eight-second live-action clip of <Subject 1>. At 0.00 seconds the frame is exactly <Picture 1>. One continuous take, no hard cuts, no end-frame lock. She removes the named outer garment and reveals the item from <Picture 2>. The camera slowly zooms in and pans around her body. As the framing gets closer to her face, her likeness must follow <Picture 3>, not drift. <Picture 2> and <Picture 3> are references only and must never appear as cut-in stills.

retention_analysis:
<Picture 1> (appears in [Shot 1] at 0.00s): fully_preserved - opening frame lock; set, pose, body, and visible wardrobe at 0.00 seconds only. Wider framing; do not keep this wider face if it conflicts with <Picture 3> once the camera is close.
<Picture 2> (never a keyframe): partially_preserved - garment appearance only after the reveal; do not reproduce the product close-up as a frame.
<Picture 3> (never a keyframe): partially_preserved - facial identity only; apply more strongly as the camera zooms toward her face; do not reproduce the tight face-crop as a video frame; do not change outfit or location to match a close-up still layout.
<Subject 1> (appears in [Shot 1]): fully_preserved - same person and location; named reveal only; face tracks <Picture 3> on the zoom-in.

detailed_description:
How the reference pictures align with the target video — Picture 1 aligns with the 0.00-second mark of the target video as the exact first frame. Picture 2 does not align with any timestamp as a frame; it is a hidden-garment reference. Picture 3 does not align with any timestamp as a frame; it is a face-identity reference that becomes more important as the camera moves closer. There is no last-frame lock at 8.00 seconds.
The target video is live-action and cinematic. No on-screen text. No extra people. Do not cut to <Picture 2> or <Picture 3>. Do not change the location from <Picture 1>.
[Shot 1] One single continuous take. At 0.00 seconds the frame is exactly <Picture 1> without reinterpretation. Brief hold, then the named reveal matching <Picture 2>. Camera slowly zooms in and pans around her body, then eases closer to her face. When the framing is close, her face must match <Picture 3> without reinterpretation of identity; do not punch-cut to the face still. Same set until 8.00 seconds. No hard cuts. No end-frame lock.

overall_soundscape:
Ambience matching the opening still, fabric rustle, quiet breath as the camera moves closer.

non_diegetic_music:
N/A
```
