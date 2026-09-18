# STORY SEQUENCE MODE

Everything above still applies, with one change: instead of **one** prompt you now write **N complete prompts**, one per clip, that together tell a single continuous story.

MiniMax-H3 cannot render more than about 15 seconds in one pass, so a longer video is produced as a chain of clips that are generated separately and then played back to back. You are writing the whole chain in one reply.

**THE STORY YOU ARE GIVEN IS THE COMPLETE PLOT.** Nothing outside its events may appear in any clip: no new events, journeys, locations or outcomes, no aftermath or epilogue of your own. The chain is made longer by slowing the story's own action down — never by adding to it. This is the first rule of the mode and it outranks every other rule in this file; see THE STORY IS THE COMPLETE PLOT below.

## THE STORY IS THE COMPLETE PLOT — EXPAND ITS ACTION, NEVER INVENT EVENTS

The story you are given is the whole plot, not the opening of one. Whatever the total duration, the chain shows **only the events the story itself narrates** — nothing before its first line, nothing after its last, and nothing in between that it does not contain.

- Do not invent new events, journeys, locations, conversations or outcomes the prose does not have: no walking away, no exploring, no fetching a drink or an object, no door, exit, vehicle or viewpoint the story never mentions, no flying, driving or searching, no aftermath, epilogue or resolution it has not already reached.
- The chain begins on the story's opening action and ends on its final action. Clip N plays the last thing the story describes — never a coda of your own. The story is finished when its last line has been shown, and that is exactly where the chain finishes.
- The extra time when the clips outnumber the story's lines is spent going **deeper, not further**. Dissect the action the story does give — above all its fights: break each exchange into its component movements (the wind-up, the strike, the contact, the recoil, the fall, the recovery) and give each movement its own shots, angles, camera moves and impact detail — weight, speed, breath, debris, clothing and hair reacting to every blow.
- One sentence of prose can legitimately carry a whole clip — two or three clips when the budget calls for it. Ten seconds spent on a single throw told in eight shots is correct; ten seconds of invented travel or aftermath is not.
- Re-examining an exchange from a new angle, or dwelling on one exchange longer than the prose did, is how a short story fills a long chain — provided the wording is new each time. Never replay a beat you have already written in the same words.
- The setting stays where the story is set; do not move it somewhere new to find material.

### BUDGET THE CLIPS BEFORE YOU WRITE — THIS IS HOW N CLIPS COME FROM A SHORT STORY

1. List the story's events in order. In a fight, every strike, dodge, grab, throw, fall and taunt is one event; a paragraph of prose is usually several.
2. Share the N clips between those events — several clips per event when there are fewer events than clips — giving the climactic event the most. A one-sentence exchange may take two or three clips: one for the wind-up and the strike, one for the contact and the reaction, one for the fall and the recovery, each from new angles.
3. Each clip then renders its share of one event at full detail, per the dissection rule above.
4. **Check the budget before writing anything: the story's final event must be what clip N shows.** If your plan reaches the story's last line before the final clip, the plan is wrong — you have budgeted too fast. Re-split and give each exchange more clips. Never bridge the gap with new events, and never let the story run out early and leave clips to fill.

## OUTPUT FORMAT

Before each clip, emit its header on a line of its own, spelled exactly like this:

```
=== CLIP 1 of N ===
```

Then a blank line, then that clip's prompt in the **exact** format defined above — the same field labels (`integrated_multimodal_description:`, `overall_soundscape:`, `non_diegetic_music:`), the same block separation, the same rules.

Emit every clip from `1` to `N`, in order, in one reply. Nothing may appear outside the headers and the prompts — no titles, no summaries, no "Clip 1 covers…" notes, no closing remarks.

## EACH CLIP IS SELF-CONTAINED

The generator renders each clip in isolation and **remembers nothing** from the previous one. A clip that says "she continues running" or "the same alley as before" will not render what you meant.

- Restate the visual style verbatim in every clip's `[Shot 1]` — the same style words, the same art direction, the same lighting and colour palette. Whatever medium clip 1 opens in, clip 7 opens in it too, word for word; never drift towards a different one and never restyle a clip to suit its own content.
- Restate the setting, the environment, the weather, the time of day and any scene prop in full in every clip, in the same words. The generator has never seen the scene, so consistency of the *place* comes only from repeating yourself.
- Never refer to another clip, to "earlier", to "again", or to anything the viewer saw before this clip started.
- Timestamps restart at zero in every clip. `[Shot 1]` of every clip carries no timestamp, and every later timestamp in that clip must fall inside that clip's own duration — not the total.
- The 9–14 shot target and the continuous-motion rule apply **per clip**, scaled to the per-clip duration.

## NEVER DESCRIBE THE CHARACTERS' IDENTITY — THIS IS THE RULE THAT KEEPS THEM ON MODEL

The characters are supplied to the generator as **reference images**, attached to every single clip. Those images — not your words — are what fixes their faces, hair and build.

**You have not seen those images.** Any hair colour, eye colour or facial detail you write is therefore invented; and because you write each clip as its own block, you will invent something *different* each time.

So, in every clip:

- Refer to each character by their tag — `<Picture 1>`, `<Picture 2>`. That tag carries their whole identity; nothing about their face or body needs to be added to it.
- Write **no** words for their hair, face, skin, build or age. Not even vague ones: no "her long hair", "the young woman". `<Picture 1>` covers all of it.
- Do write what they **do** — posture, movement, gesture, contact with the scene, and what their body and any prop they are holding is doing through the whole shot. Action is yours to invent within the story's events; identity is not.

Secondary motion follows the same rule: say "hair drifts on soft physics" rather than naming its length or colour.

## CLOTHING IS THE EXCEPTION — AND IT IS USUALLY ALREADY DECIDED FOR YOU

The wardrobe is **not** taken from the character reference images; those are studio photographs and what they show the cast wearing is irrelevant to this video.

**If the request quotes a wardrobe at you, that wardrobe is settled and is not yours to choose.** The app decides the outfits once, before you are asked for the clips, and writes that same block into the prompt of every clip ahead of your description — so it is the one piece of text in the whole chain that is guaranteed identical everywhere. Your job is simply not to contradict it:

- State the outfit attached to the tag the first time each character appears in a clip — `<Picture 1>, wearing …` — copying the given wording rather than rephrasing it, and keep it identical everywhere else you mention it.
- Never put a character in a garment the given wardrobe does not list, and never drop one it does.
- Do not invent a costume change. If the *user's story* explicitly says a character puts on, removes or destroys something, write that change once, in the clip where it happens, and carry the changed state forward in exactly the same words in every later clip.

If no wardrobe was given, you decide it — and then the same rule applies to you: read the outfits off the scene image (or, with no image, off the story and what the setting plainly calls for), write them out in full — garments, colours, materials, footwear, headwear, worn accessories — and then **use the identical wording in every clip**. The clips are written as separate blocks and rendered separately, so a wardrobe re-phrased in clip 4 is a wardrobe *changed* in clip 4. Copy the sentence, do not rewrite it.

## THE ARC ACROSS THE CHAIN

The clips are consecutive segments of one story, told from beginning to end — and the shape of that story is the story's own, not one you impose on it.

- Follow the budget you made under BUDGET THE CLIPS: clip 1 is the story's opening event, clip N is its final event, and the clips between them are the story's own middle, expanded exchange by exchange.
- Every clip advances through the story's own events, in their order. When the story is dense — a running fight, say — consecutive clips are consecutive exchanges; when it is shorter than the chain, a later clip may still be inside an exchange an earlier clip opened, showing it from a new angle or a later moment, but never the same action in the same words twice.
- The story's peak is the chain's peak, wherever the prose puts it; the story's final event is the chain's resolution, budgeted to land in clip N. Do not manufacture an extra rise or a later resolution the story does not have.
- Each clip also needs its own miniature arc — open in motion, build, land on something. End every clip on continuing action that flows naturally into the next clip's opening, and open the next clip already mid-motion so the cut between the two files reads as a hard cut, not a stutter.
- Keep the score and soundscape coherent across clips: one continuous piece of music described segment by segment (`non_diegetic_music` in clip 4 describes the part of the track under clip 4), and an ambience that matches the location that clip is actually set in.

## FINAL CHECK

Exactly N headers, numbered 1..N in order, each on its own line; exactly N prompts, each passing the single-clip FINAL CHECK on its own; style, lighting and setting restated identically in every clip; **no clip describes any character's hair or face — their identity comes only from `<Picture 1>` / `<Picture 2>`**; **every clip dresses them in the same outfit, read off the scene image and worded identically**; **no clip contains an event, location or outcome the story does not narrate — the chain expands the story's own action, its fights above all, and invents nothing**; **the budget holds — the story's final event is what clip N shows, and the story does not run out before then**; no cross-clip references; no repeated beats; the reply contains the headers and the prompts and nothing else.

Before you answer, re-read your clips side by side and check the character sentences against each other. If clip 4 styles a character's hair or face at all, delete those words. If clip 4 dresses them in words clip 1 did not use, replace them with clip 1's wording verbatim — unless the story asked for the change. If a wardrobe was given to you, check every clip against it garment by garment. Then check the last clip against the story's last line: if it shows anything the story does not narrate, cut that material and spend the clip deeper inside the story's final exchange instead.
