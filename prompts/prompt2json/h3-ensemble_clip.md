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
  before this clip started. A clip that says "the same alley as before" will not render what you meant.
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

## WHO IS IN THIS CLIP

The request lists the subject tags for this clip. **Only those characters are on screen.**

- Name every listed subject at least once, by their tag.
- Name **no** subject the request did not list for this clip. Each clip is sent only the reference
  photographs of the subjects its own text names, so naming somebody who is not in the beat spends a
  reference slot that the characters who *are* in it needed.
- Casting never changes across the chain: whoever `<Subject 3>` plays here, they play in every clip.
- A subject the request marked NOT a person is not a person in this clip either — no human face, no
  human hands, no human clothing, and never a person standing in for them.

## KEYFRAMES

The request says whether this clip has any. If it lists none, write **no `<Picture n>` anywhere** — this
clip is a continuous take with no frame lock at either end, driven entirely by the cast references and
your words.
