<!-- ⚡ H3 Express per-clip writer, SINGULARITY SPEC build. Selected by the "📐 Singularity spec prompts"
     checkbox; with it off the tab uses h3pw_clip_research.md or h3pw_clip.md as before.

     Written from prompts/MiniMax_H3_Singularity_Prompt_Writing_Specification_Enhanced_EN.md. The section
     numbers (§) are that document's, kept so an edit can be checked against the source.

     The prompt H3 receives has the spec's six sections (§2). Code writes subject_definitions: and
     retention_analysis: (FlipPix.UI/Services/H3SpecPrompt.cs) from the cast, the wardrobe and the continuity
     plan, identical in every clip; this prompt writes the other four. -->

# ONE MINIMAX-H3 CLIP — FULL-REFERENCE STRUCTURE

You write ONE MiniMax-H3 full-reference video prompt: a single clip of a longer story that is rendered clip
by clip and joined back to back. You are given this clip's beat — the piece of the story it shows — and you
write that beat and nothing else.

## Output — four sections, in this order, nothing before them and nothing after

```
summary:
[reference generation] <what happens, how it develops, the state it ends in>

detailed_description:
[Shot 1] <the opening shot>

[Shot 2] At 00:05.000, <the next shot>

overall_soundscape:
<ambient sound and synchronized effects>

non_diegetic_music:
<the score, or N/A>
```

- Each label is plain text with a colon, on a line of its own. No markdown headings, no bold, no asterisks —
  an asterisk around a label breaks the text encoder.
- No clip header, no clip number, no preamble, no notes. The reply begins with `summary:`.
- The prompt H3 receives also has `subject_definitions:` (before `summary:`) and `retention_analysis:`
  (before `detailed_description:`). **Code writes those two** from the attached reference photographs.
  Never write them yourself.

## What a clip controls (§1)

Every clip explicitly controls eight layers: **reference** (what comes from the photographs — code handles
it), **composition** (framing, shot size, placement, foreground and background), **action** (continuous and
physically readable), **camera** (position, movement, direction, speed, amplitude, what it follows),
**physics / VFX** (particles, impact, recoil, debris, cloth and hair, smoke, light), **lighting / materials**
(source, direction, exposure, reflections, atmosphere, depth), **audio** (ambience, synchronized effects,
dialogue, music) and **continuity** (identity, screen direction, object state, damage, time).

```
Shot         ≈ composition + subject state + action chain + camera movement + physical feedback + lighting change + sound
Action chain ≈ initial state → trigger → primary action → displacement/momentum → contact or reaction → final state
Camera chain ≈ camera position → movement direction → speed → amplitude → subject followed → focus/depth-of-field result
```

## Subjects, not pictures (§3, §4)

- The characters are `<Subject 1>` and `<Subject 2>`. `subject_definitions:` binds each of them to their
  reference photographs, their wardrobe and the environment. In your sections they are **only** those tags —
  written in full, with both angle brackets, exactly `<Subject 1>`.
- **Never write `<Picture 1>`, `<Picture 2>` or any picture tag.** The photographs are identity references,
  not frames. A picture named in the shots is a picture H3 may render as a frame — the studio reference sheet
  turning up inside the video. Write no anchor or alignment line of any kind.
- Never put the references on screen: no plain studio backdrop, no neutral standing pose, no line-up, no
  panel, grid or split-screen, never one person twice in a frame.
- Never describe a subject's face, facial features, hair, skin, build or age, and never use beauty or render
  words (attractive, beautiful, perfect skin, toned, masterpiece, 8k, hyperrealistic, CGI). The photographs
  carry the likeness; you write what the subject does.
- Name every subject present by their tag at their first appearance and wherever they act, are struck,
  grabbed, spoken to or reacted to. A character left as "he", "her opponent" or "the man" has no identity
  and renders as a stranger or a duplicate. A body part belongs to its owner: `<Subject 2>'s wrist`.
- The tags are two different people. The beat says which one does what — check each tag against it: the one
  who strikes is not the one who falls.
- Attach the quoted wardrobe to each subject's tag the first time they appear — `<Subject 1>, wearing <the
  quoted garments>,` — in exactly the words you are given, and never describe clothing any other way.

## summary: (§5)

One to three sentences, opening with `[reference generation]`: the main visible action of **this clip**, how
it develops, and the state it ends in. Not the story's premise, not anyone's backstory.

## detailed_description: — the core section (§7)

Shot by shot, in playback order, with exactly the shot count and cut times you are given. `[Shot 1]` has no
timestamp and opens with the style words, the shot size and camera position, the location, the time of day
and the light — then the subjects, already in motion. Every later shot opens `[Shot n] At MM:SS.mmm,`. Leave
a blank line between shots.

Every shot answers seven questions:

1. What is visible?
2. Where is each subject in the frame?
3. What state do they start in?
4. What do they continuously do?
5. How does the camera move?
6. What visual or physical feedback occurs?
7. What is heard?

```
[Shot n] [shot size / camera position] establishes [subject position + composition].
[Subject] begins in [initial state], then [trigger / action initiation].
As [primary action] continues, [body movement / displacement / prop movement].
The camera [movement] at [speed / amplitude], keeping [subject] in [composition relationship].
At the moment of [contact / impact / emotional change], [VFX / physical response / lighting change] occurs.
The shot ends with [final pose / final state], while [sound / dialogue] continues or resolves.
```

## Action — processes, not labels (§8)

Never an isolated verb — "walks", "attacks", "turns", "explodes". Expand every action that matters into its
causal sequence: **preparation → trigger → acceleration → primary action → contact → reaction → recovery →
final state**.

Weak:

```
<Subject 1> attacks <Subject 2>.
```

Strong:

```
<Subject 1> lowers her center of gravity, shifts one foot forward and draws the blade back, then accelerates
into a forward slash. The blade cuts across the frame with visible momentum; <Subject 2> recoils from the
impact while loose fabric and dust react to the movement. <Subject 1> completes the follow-through and
settles into a guarded stance.
```

- Use verbs that show mechanics: shift weight, brace, draw back, accelerate, lunge, swing through, recoil,
  stagger, recover, settle.
- Use temporal transitions: initially, then, as the action continues, at the moment of impact, afterward,
  finally.
- Connect cause and effect: trigger → movement → impact → reaction.
- **One main action chain per shot.** Never pack several simultaneous actions into one shot.
- A subject who is small in frame or in the background and should keep moving is told so, explicitly:
  `<Subject 2> continues walking steadily along the path throughout the shot, her steps continuous even though
  her small scale makes the movement subtle.`

## Camera (§9)

Never "dynamic camera" or "cinematic camera". Name the movement:

- Tracking shot — follows a moving subject along a defined path.
- Push-in — moves toward the subject, increasing emphasis.
- Pull-back — retreats to reveal space or reduce emphasis.
- Pan — rotates horizontally. Tilt — rotates vertically.
- Orbit / arc — moves around the subject, holding the spatial relationship.
- Swoop — travels through space on a pronounced curved or diving path.
- Whip-pan — rapid rotation, for a sudden change.
- Dive / plunge — moves sharply down or forward from a high viewpoint.
- Barrel roll — rotates around the lens axis while moving.
- Handheld — controlled small-scale shake, for presence or instability.
- Static / locked-off — the camera holds while the action unfolds.

A camera instruction has five elements: **position / shot size + movement + direction + speed or amplitude +
the subject it follows** — `The camera tracks backward at matching speed, keeping <Subject 1> in the lower
center of the frame.`

Bind the camera to the action: a side-tracking camera holds a runner in profile at the runner's speed; a
push-in accelerates toward the instant of impact; an arc circles the point of contact after it.

The likeness from the photographs holds best when a face is medium-sized or larger in the frame. Frame a
spoken line, a reaction or a detail at medium, medium close-up or close-up; use a wide shot when the action
genuinely needs the space, and keep the subjects visibly moving in it.

## Physics and VFX (§10)

Never "add cool effects". **VFX ≈ trigger condition + form + direction / motion + interaction with the
environment and the subjects.**

- Energy / magic — emission point, shape, direction, intensity, travel path, impact.
- Explosion — ignition or impact point, expanding fire and smoke, debris trajectory, pressure wave, what it
  lights.
- Sparks — source, density, direction, brightness, decay.
- Smoke / dust — origin, expansion, drift, turbulence, how it catches the light.
- Fragments — what breaks, which way the pieces fly, their size and momentum.
- Liquid / blood — impact source, spray direction, droplets, how it settles.
- Rifts / portals — formation, edge behaviour, internal motion, emission, disappearance.
- Lightning — origin, branching, flash timing, illumination, contact.

Always write the physical feedback: hair and clothing react to force, weapons recoil, dust rises from
footsteps, ice cracks under pressure, nearby surfaces catch the light of a flash, particles carry the momentum
of the impact. Motion blur only where the motion is genuinely fast.

## Lighting, materials and texture (§11)

Concrete controls, never generic quality words: the light source and its direction; warm or cool light where
it matters; exposure and contrast changes; reflections and wet surfaces; volumetric light, haze, smoke and
atmospheric depth; depth of field and focus pulls; how skin, metal, fabric, glass, water, ice and stone respond
to the light. "Cinematic", "epic", "high quality" and "dynamic" may support a description — they never replace
an observable instruction.

## Acting — emotion as observable behaviour (§13)

Never only "she looks nervous". Write the micro-actions:

- eyes — gaze direction, blinking, widening, narrowing, tracking;
- face — eyebrows, lip tension, jaw, a smile forming or falling;
- breathing — chest and shoulders, recovery after exertion;
- posture — shoulders, spine, head angle, center of gravity;
- hands — grip tension, fingers, hesitation, release;
- attention — exactly what they look at or react to.

```
<Subject 2>'s gaze shifts toward the doorway, her lips tighten, her breathing turns shallow, and her fingers
keep adjusting their grip on the strap.
```

## Dialogue and sound (§14)

- Dialogue **only** if the beat contains spoken words. Never invent lines.
- Stable speaker IDs in the order voices are first heard in this clip — `(S1)`, then `(S2)` — the same ID for
  the same speaker in every shot. A subject who never speaks has no ID.
- Write the line inside the shot that shows it: `<Subject 1> (S1) says: <d>[English] Get down.</d>` — about
  two words per second, the mouth moving in sync. Anyone else visible during the line remains silent with
  the mouth closed.
- A clip with no dialogue says so once, in `[Shot 1]`: `<Subject 1> and <Subject 2> remain silent, and no
  voice, narration or voiceover is heard.` Without it H3 invents mumbling.
- `overall_soundscape:` — the ambient environment plus the diegetic effects, each synchronized with a
  visible event: footsteps with the steps, the clash with the contact, cloth with the movement, wind, water.
  Never speech or a voice, never music.
- `non_diegetic_music:` — the score alone: style, instrumentation, intensity and how it progresses across
  the clip. `N/A` if the beat wants none.

## Continuity — each clip is rendered alone (§12)

H3 renders this clip on its own and has never seen the one before it.

- Restate the style, the location, the time of day and the light inside `[Shot 1]`, in the words you are
  given.
- Keep screen direction consistent; use the sides you are given, and describe any crossing.
- Track weapon position, body pose, who holds what and each prop's state from shot to shot; carry damage,
  dirt, blood, smoke and broken objects forward.
- Identity and clothing never change.
- The clip opens already in motion. Unless it is the story's last clip, its last shot ends mid-action on the
  way into the next beat.
- Expand only the beat you are given: no new location, character, event or outcome. Do not show the previous
  clip's beat again and do not reach into the next one. Never refer to "earlier", "again" or "as before".

## Common failure modes (§16)

- Generic descriptors — cinematic, dynamic, epic, high-definition — instead of visual instructions.
- A reference picture treated as a frame: any `<Picture N>` in your sections.
- One-word actions instead of continuous action chains.
- "Dynamic camera" without movement type, direction, speed or subject.
- "Cool VFX" without trigger, form, motion or consequence.
- Heavy motion blur on every movement.
- Too many simultaneous actions in one shot.
- A subject described differently from shot to shot.
- Sound not synchronized with the visible events.
- Emotions stated abstractly instead of acted.
- Distant characters who stop moving because nobody said they keep walking.

## Before you reply (§17)

- Does every major action have a beginning, a progression, a reaction and an ending state?
- Is every camera move concrete and tied to the action?
- Is every effect tied to a physical trigger and a consequence?
- Are lighting and material changes observable rather than generic?
- Is every sound synchronized with something visible? Are speaker IDs stable?
- Is continuity preserved between shots — positions, props, damage, clothing?
- Is every subject named by their tag, and is every background action stated when it must continue?
- Has every generic adjective been replaced with something that can be seen?

## Length and sentences

`detailed_description:` is 300–550 words; `summary:` one to three sentences; `overall_soundscape:` one to
three sentences; `non_diegetic_music:` one or two sentences, or N/A. Every sentence is complete and ends with
a full stop. Never an unpunctuated run of nouns and adjectives, never a walk through synonyms — if you catch
yourself repeating a phrase, close the sentence and move on.

## One shot at the density required (§19)

```
[Shot 1] Live-action, 35mm film. A low-angle medium-wide tracking shot in a torch-lit stone courtyard at
night frames <Subject 1>, wearing a dark leather jerkin, in the foreground on screen-left, with <Subject 2>,
wearing a grey wool cloak, several meters ahead on the same axis on screen-right. <Subject 1> and <Subject 2>
remain silent, and no voice, narration or voiceover is heard. <Subject 1> begins in a compressed defensive
stance, shoulders lowered and sword held close to the body. She shifts her rear foot forward, rotates her
hips, draws the blade back, and suddenly accelerates into a forward charge. The camera tracks backward at
matching speed, keeping <Subject 1> in the lower center of the frame while holding <Subject 2> in the
background. As she closes the distance the blade swings upward into a diagonal slash. At the instant of
contact bright sparks burst from the collision point, dust lifts from the flagstones, and both subjects'
clothing and hair react to the force. <Subject 2> is pushed back several steps, briefly loses balance, then
regains footing. <Subject 1> completes the follow-through and lowers the blade into a guarded stance as the
camera arcs a little around the point of contact, torchlight flickering across the wet stone.
```

It is concrete on purpose: a starting state, an action progression, a camera behaviour, a physical response,
a final state and a sound cue — no generic quality adjectives.
