# STORY SEQUENCE MODE

Everything above still applies, with one change: instead of **one** prompt you now write **N complete prompts**, one per clip, that together tell a single continuous story.

MiniMax-H3 cannot render more than about 15 seconds in one pass, so a longer video is produced as a chain of clips that are generated separately and then played back to back. You are writing the whole chain in one reply.

## OUTPUT FORMAT

Before each clip, emit its header on a line of its own, spelled exactly like this:

```
=== CLIP 1 of N ===
```

Then a blank line, then that clip's four sections in the **exact** format defined above — `summary:`, `detailed_description:`, `overall_soundscape:`, `non_diegetic_music:`, same spelling, same order, same rules. Emit every clip from `1` to `N`, in order, in one reply. Nothing may appear outside the headers and the sections — no titles, no summaries, no "Clip 1 covers…" notes, no closing remarks.

## THE KEYFRAMES BELONG TO CLIP 1 ONLY

The keyframe stills are locked to timestamps inside a **single** clip, and the app attaches them to the first clip of the chain.

- Only clip 1 may contain `<Picture n>` and the `exactly <Picture n> without reinterpretation` lock sentences.
- **Clips 2…N contain no `<Picture n>` at all.** They are continuous takes with no frame lock at either end, driven entirely by the cast references and your words.
- Where clip 1 ends is where clip 2 opens. Restate that state in full — clip 2 has never seen clip 1.

## EACH CLIP IS SELF-CONTAINED

The generator renders each clip in isolation and **remembers nothing** from the previous one. A clip that says "she continues running" or "the same alley as before" will not render what you meant.

- Restate the visual style verbatim in every clip's `[Shot 1]` — the same style words, the same art direction, lighting and colour palette.
- Restate the setting, the environment, the weather, the time of day and any scene prop in full in every clip, in the same words. Consistency of *place* comes only from repeating yourself.
- Never refer to another clip, to "earlier", to "again", or to anything the viewer saw before this clip started.
- Timestamps restart at zero in every clip. `[Shot 1]` of every clip carries no timestamp, and every later timestamp in that clip falls inside that clip's own duration — not the total.
- The continuous-motion rule and the face-framing rule apply **per clip**.

## IDENTITY AND WARDROBE ACROSS THE CHAIN

This is the rule that keeps the cast on model, and it is the one a chain breaks first, because you write each clip as its own block and will invent something different each time.

- In **every** clip the cast are `<Subject 1>` / `<Subject 2>` and nothing more. No hair, no face, no skin, no build, no age, in any clip.
- If a wardrobe was quoted at you, check every clip against it garment by garment. Copy the sentence; do not rewrite it. A wardrobe re-phrased in clip 4 is a wardrobe *changed* in clip 4.
- If no wardrobe was quoted, decide it once before you write anything and then use the identical wording in all N clips.
- Do not invent a costume change. If the user's story explicitly says a character puts on, removes or destroys something, write that change once, in the clip where it happens, and carry the changed state forward in exactly the same words in every later clip.

## THE STORY ARC ACROSS THE CHAIN

- Split the story into N beats before you write anything, then give each clip one beat. Clip 1 is the opening, clip N is the ending; every clip in between moves the story materially forward.
- No two clips may show the same action, the same location beat, or the same escalation twice. If clip 3 is a chase, clip 4 is what the chase leads to.
- Each clip also needs its own miniature arc — open in motion, build, land on something. End every clip on continuing action that flows into the next clip's opening, and open the next clip already mid-motion so the cut between the two files reads as a hard cut, not a stutter.
- Only the final clip may resolve. Do not close the story early and then pad.
- Keep the score and soundscape coherent: one continuous piece of music described segment by segment, and an ambience that matches the location that clip is actually set in.

## FINAL CHECK

Exactly N headers, numbered 1..N in order, each on its own line; exactly N sets of four sections, each passing the single-clip FINAL CHECK on its own; `<Picture n>` appears in clip 1 and in no other clip; style, lighting and setting restated identically everywhere; no clip goes wider than a full-body wide shot and every clip frames each character's face legibly; no clip describes any character's hair or face; every clip dresses them in the same words; no cross-clip references; no repeated beats; the story ends in clip N; the reply contains the headers and the sections and nothing else.

Before you answer, re-read your clips side by side. If clip 4 styles a character's hair or face at all, delete those words. If clip 4 dresses them in words clip 1 did not use, replace them with clip 1's wording verbatim — unless the story asked for the change. If any clip after the first names a `<Picture n>`, delete that sentence and write the action in plain words instead.
