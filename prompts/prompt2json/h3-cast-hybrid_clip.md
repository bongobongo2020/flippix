# ONE CLIP OF A CHAIN

Everything above still applies, with one change: you are writing **one** clip of a longer story that is
being rendered clip by clip and joined back to back. The request names which clip this is, and gives you
the beat it shows. Write that beat and nothing else.

Output the same four sections in the same order — `summary:`, `detailed_description:`,
`overall_soundscape:`, `non_diegetic_music:` — and nothing around them. **No clip header, no clip number,
no preamble, no commentary.** The reply begins with `summary:` and ends when the music section ends.

## THIS CLIP IS RENDERED ALONE

The generator renders this clip in isolation and remembers nothing from the one before it.

- Restate the visual style verbatim in `[Shot 1]` — the same style words every clip of this chain uses.
- Restate the setting, the environment, the weather, the time of day and any scene prop in full, in the
  words the request gives you. Consistency of *place* across a chain comes only from repeating yourself.
- Never refer to another clip, to "earlier", to "again", to "continues", or to anything the viewer saw
  before this clip started. A clip that says "the same rooftop as before" will not render what you meant.
- Timestamps restart at zero. `[Shot 1]` carries no timestamp; every later one falls inside **this
  clip's** duration, not the chain's total.

## THE BEAT IS THE WHOLE CONTENT

- Expand only the action the request's beat describes. Break its movements down — the approach, the
  wind-up, the contact, the recoil, the recovery — and give each its own shot, angle and camera move.
  That is where the seconds come from.
- Invent no event the beat does not contain: no new location, no new character, no journey, no
  conversation, no outcome.
- You are told what the clip before showed and what the clip after will show. Do **not** show either.
  Open already in motion, and end mid-action on the way to the next beat, so the cut reads as a hard cut
  rather than a stutter.
- Only the chain's final clip may resolve the story. If the request does not say this is the last clip,
  do not close anything.

## THE CAST IS THE SAME IN EVERY CLIP

The same reference sheets are attached to every clip of this chain, so the same characters are available
throughout.

- Name every character the beat involves by their tag, at their first appearance and wherever they are
  struck, grabbed, named or reacted to after it. A character who appears only as an untagged pronoun —
  "he", "his chest", "the man", "her opponent" — has no identity in this clip, and the generator renders
  them as a duplicate of the character that IS tagged.
- Write the tag in full every time, with both angle brackets, exactly as the request spells it.
- The tags are not interchangeable. The beat says which character does what: check every tag against it
  before you write it — the one who strikes is not the one who falls.
- Their casting and wardrobe never change across the chain. Copy the quoted wardrobe wording; a wardrobe
  rephrased in clip 4 is a wardrobe *changed* in clip 4.

## KEYFRAMES

The request says whether this clip has any. If it lists none, write **no `<Picture n>` anywhere** — this
clip is a continuous take with no frame lock at either end, driven entirely by the cast references and
your words.
