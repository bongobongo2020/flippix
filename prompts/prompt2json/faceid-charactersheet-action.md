You will receive a single frame taken from a reference video. Your task is to write ONE short LTX text-to-video action caption describing the person in the frame and the action they are performing, so a FaceID character-sheet video generator can reproduce that action.

MANDATORY OUTPUT RULE:

The final response must begin exactly with `ref_t2v:` followed by the caption. Output exactly one line containing only that (`ref_t2v:` + the caption). Do not add headings, explanations, quotation marks, Markdown, or code fences.

SUBJECT:

Start the caption with the subject category you can see in the frame: `A person`, `A man`, or `A woman`. Then merge in the clearly visible appearance — face and hair, body build and posture, and clothing and accessories — using concrete, neutral, visual words only.

FACIAL DETAILS (when the face is visible, include at least 3 clearly visible ones): age group, skin tone, hair color, hair length, hair texture, hairstyle, face shape, eyebrows, eye color (only if clear), facial hair, glasses.

BODY DETAILS (when visible): build (slim/lean/average/broad/athletic/stocky), shoulder width, posture, stance. Do not invent exact height, weight, or hidden features.

CLOTHING AND ACCESSORIES (when visible): upper/lower garments, outerwear, footwear, hats, jewelry, dominant colors, patterns, and clearly visible materials (leather, denim, metal, knit, silk-like).

ACTION AND SCENE:

This is the most important part — describe the ONE clear action or performance the person is doing in the frame (for example: speaking to camera, singing into a microphone, walking through a room, dancing, gesturing while talking, playing an instrument). Infer the continuous motion the still frame is a moment of, and keep it simple and achievable in a few seconds of video. Add the visible environment and a natural camera framing (for example: medium shot, close-up, medium-wide shot). Keep lighting and mood consistent with the frame.

DO NOT: identify or name the person; infer ethnicity, nationality, religion, occupation, personality, health, or background; invent details you cannot see; describe body parts or clothing outside the frame; add extra people, props, or camera cuts that are not visible.

STYLE:

One flowing sentence, present tense, photorealistic and cinematic in tone. Order the caption as: age group and skin tone, subject noun, hair and facial details, body build and posture, clothing and accessories, then the action, environment, and camera framing.

EXAMPLE:

`ref_t2v: A tan-skinned adult man with short dark curly hair, a trimmed beard, and thick eyebrows, with a lean build and upright posture, wearing a dark green jacket over a gray t-shirt, sings into a handheld microphone in a dimly lit studio, medium close-up shot.`

FINAL CHECK before answering: the output begins exactly with `ref_t2v:`; visible facial, body, and clothing details are included; exactly one continuous action is described; no invented or off-frame details; the output is a single line with no explanation.
