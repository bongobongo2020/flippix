<!-- The H3 Experimental tab's story-chain layer, laid over the official MiniMax base guide
     (h3pw_guide_base.md) by the h3_prompt_writer tool. It carries the rules a chain of
     separately-rendered clips needs that a single prompt does not: clip headers, per-clip
     self-containment, tag-only identity, the locked wardrobe — and, first among equals, the
     action-expansion rule that scales the story's own fight across the chain's runtime
     without inventing anything the story does not narrate. -->

# STORY CHAIN MODE

The brief describes ONE continuous story. You write **N complete prompts**, one per clip, that
together play the whole story from its first action to its last. MiniMax-H3 renders about 15
seconds per pass, so the chain is produced clip by clip and joined back to back; total runtime
is N × S seconds, with S stated in the brief.

> **Override of the continuous-shot default:** these chain briefs explicitly require the
> multi-shot structure — a fight told at the pacing of a music video or a game cinematic IS
> the user's intent here, so the 9–14 shots per clip demanded below are required structure,
> never "cuts introduced solely for cinematic embellishment". The wrapper's single-take
> default does not apply to this mode.

Before each clip, emit its header on a line of its own, spelled exactly like this:

```
=== CLIP 1 of N ===
```

Then a blank line, then that clip's prompt in the **exact** format of the base guide — the same
three field labels (`integrated_multimodal_description:`, `overall_soundscape:`,
`non_diegetic_music:`), the same rules, nothing else. Emit every clip from 1 to N in order, in
one reply. Nothing may appear outside the headers and the prompts.

## EXPAND THE ACTION — ONLY THE ACTION, NOTHING ELSE

**The brief's story is the complete plot.** Whatever the total runtime, the chain shows only
the events the story itself narrates — nothing before its first line, nothing after its last,
nothing in between that it does not contain. The runtime is longer than the prose, and every
extra second is bought by **slowing the story's own action down** — above all its fights:

- Break each fight exchange into its component movements — the wind-up, the strike, the
  contact, the recoil, the fall, the recovery — and give each movement its own shots, angles
  and camera moves, with impact detail: weight, speed, breath, debris, clothing and hair
  reacting to every blow.
- **Shot count is scaled to the clip's own S seconds and it is a floor, not a suggestion:**
  a clip of roughly 15 seconds carries **9–14 shots** — one cut every one to one-and-a-half
  seconds, exactly the pacing of a music video or a game cinematic. Scale proportionally
  for other lengths, and never write fewer than 8 shots into a 15-second clip: a fight told
  in three long shots is a wrong clip no matter how well it is worded. Every shot after the
  first opens with its own timestamp in the guide's `MM:SS.mmm` form, strictly increasing,
  all inside the clip's own duration.
- **Keep each clip tight: 350–500 English words in `integrated_multimodal_description`**
  (the guide's own generation-task range). A clip that runs past ~600 words has stopped
  being a prompt and become prose — the chain has to fit N of these in one reply, so cut
  the words, never the shots.
- One sentence of prose can legitimately carry a whole clip — two or three when the budget
  calls for it. Fifteen seconds spent on a single throw told in ten shots is correct; fifteen
  seconds of invented travel, arrivals, searching, dialogue or aftermath is not.
- The clip's length decides the depth of the dissection: scale the shot count to the clip's
  own S seconds (the brief gives the per-clip target), keep every timestamp inside S, and let
  the extra shots go *deeper into the same exchange* — closer, slower, from a new angle —
  never sideways into a new event.
- Re-examining an exchange from a new angle or dwelling on one exchange longer than the prose
  did is how a short story fills a long chain, provided the wording is new each time. Never
  replay a beat you have already written in the same words.

### BUDGET THE CLIPS BEFORE YOU WRITE

1. List the story's events in order. In a fight, every strike, dodge, grab, throw, fall and
   taunt is one event; a paragraph of prose is usually several.
2. Share the N clips between those events — several clips per event when there are fewer
   events than clips — giving the climactic event the most.
3. **Check the budget before writing anything: the story's final event is what clip N shows.**
   If your plan reaches the story's last line before the final clip, the plan is wrong — you
   have budgeted too fast. Re-split and give each exchange more clips. Never bridge the gap
   with new events, and never let the story run out early and leave clips to fill.

## THE CHAIN RUNS CONTINUOUSLY

The clips are consecutive segments of one continuous take, cut every S seconds:

- End every clip on continuing action — mid-strike, mid-pursuit, mid-landing — that flows
  naturally into the next clip's opening, and open the next clip already in motion at that
  same moment, so the cut between the two files reads as a hard cut inside one ongoing action,
  never a stutter or a restart.
- Keep one continuous piece of score across the chain (`non_diegetic_music` in each clip
  describes the segment of the same track under that clip) and an ambience that matches the
  location that clip is actually set in.
- The story's peak is the chain's peak, wherever the prose puts it; the story's final event is
  the chain's resolution, budgeted to land in clip N. Do not manufacture a rise or a coda the
  story does not have.

## EACH CLIP IS SELF-CONTAINED

The generator renders each clip in isolation and **remembers nothing** from the previous one:

- Restate the visual style verbatim in every clip's `[Shot 1]` — the same style words, art
  direction, lighting and palette. Whatever medium clip 1 opens in, clip 8 opens in too, word
  for word.
- Restate the setting, environment, weather, time of day and scene props in full in every
  clip, in the same words. Consistency of the place comes only from repeating yourself.
- Never refer to another clip, to "earlier", to "again", or to anything the viewer saw before
  this clip started.
- Timestamps restart at zero in every clip. `[Shot 1]` carries no timestamp; every later
  timestamp falls inside that clip's own S seconds.

## IDENTITY COMES FROM THE TAGS — NEVER DESCRIBE THE CAST

The characters reach the generator as **reference images** attached to every clip, addressed
as `<Picture 1>`, `<Picture 2>` (the brief says which tag is which character). Those images —
not your words — fix their faces, hair and build, and you have not seen them:

- Refer to each character only by their tag. Write no word for hair, face, skin, build or age —
  not even vague ones. The tag carries all of it.
- Do write what they **do**: posture, movement, gesture, contact with the scene, what their
  body and any prop they hold is doing through the whole shot. Action is yours (within the
  story's events); identity is not.

## BOTH FIGHTERS ARE TAGGED IN EVERY CLIP — THE HARD RULE THAT KEEPS TWO PEOPLE ON SCREEN

When the cast has two characters and the story puts them in the same fight — which in a
two-hander is **every clip of the chain** — both of them are on screen, and both of them are
**named by their tags**, in every single clip:

- Name BOTH tags at each fighter's first appearance in EVERY clip — `<Picture 1>` for one,
  `<Picture 2>` for the other — and use the tags wherever either is named after that. Each clip
  is rendered on its own with no memory of the clips around it, so a fighter left untagged in
  clip 6 has **no identity at all** in clip 6: the generator will cast that fighter from
  whichever references it was handed, and a clip that names only `<Picture 1>` renders
  `<Picture 1>` fighting a duplicate of themselves. That is the failure this rule exists to
  prevent.
- A fighter may NEVER appear only as an untagged pronoun or label — no "he", "his chest",
  "the man", "the bodybuilder", "his kneeling shoulder" standing in for a character the clip
  has not tagged. Wherever the prose names, strikes, grabs or reacts to either fighter, the
  tag stands in for the name: "drives her knee into <Picture 2>'s nose", "<Picture 2> roars".
- Close-ups count too: a shot of "a jawline taking the impact" is a shot of
  `<Picture 2>`'s jawline — say so.
- Both fighters wear the quoted wardrobe, both are named in every clip, and no clip of a
  two-person fight is ever written around only one of them.
- Never refer to "the story", "this clip", "the chain", the budget, or the viewer in the
  prompts — write only what is seen and heard on screen.

## THE WARDROBE IS SETTLED — COPY IT, NEVER REPHRASE IT

The brief quotes a wardrobe. It was decided once, ahead of your reply, and is written into
every clip's prompt ahead of your description — the one block of text guaranteed identical
across the chain. Your job is not to contradict it:

- State the outfit attached to the tag the first time each character appears in a clip —
  `<Picture 1>, wearing …` — copying the quoted wording, and keep it identical everywhere
  else you mention it.
- The quoted wardrobe is the **only** clothing wording you may use. Never re-dress the cast
  from the story's own prose — no "the black sports bra that showed off her firm breasts",
  no "his tank top tight against his massive chest": where the story describes clothing
  differently or more floridly than the quote, **the quote wins, word for word**.
- Never put a character in a garment the wardrobe does not list, never drop one it does, and
  never invent a costume change. The only clothing that may change is a change the story
  itself explicitly narrates — write it once, in the clip where it happens, and carry the
  changed state forward in the same words.

## FINAL CHECK

Exactly N headers numbered 1..N, each on its own line; exactly N prompts, each obeying the
base guide's own format on its own; **each clip carries its full shot count — 9–14 shots for
a 15-second clip, one cut every 1–1.5 seconds, never fewer than 8**; style and setting
restated identically in every clip; no
clip describes the cast's hair or faces; **every clip of a two-person fight names BOTH
fighters' tags — no fighter appears only as an untagged pronoun**; every clip dresses them in
the quoted wardrobe wording; **no clip contains an event, location or outcome the story does
not narrate — the chain expands the story's own action, its fights above all, and invents
nothing**; the budget holds — the story's final event is what clip N shows; no cross-clip
references; no repeated beats; every clip opens in motion and ends mid-action; no
"the story"/"this clip" meta-language anywhere; the reply contains the headers and the
prompts and nothing else.
