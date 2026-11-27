"""Build the "Long Form" MiniMax H3 API prompt on the Context-Loop pack.

Design (why each piece is there):
  * One Context-Loop run instead of N independent FFLF renders: every beat continues from the
    previous beat's *sampled latent* (39-frame AV prefix), never from a decoded/upscaled PNG.
    That removes the decode -> RTX VSR -> PNG -> re-encode -> near-clean frame-0 pin cycle that
    compounds texture/colour every beat.
  * incoming_transition = color_drift_av (Color-Stable Drift AV): the carried prefix is presented at
    the sampler's own noise level (Drift-Control) so a clean predecessor cannot take over the
    noisiest steps (the documented red/processed-look drift), and a low-frequency colour delta
    pulls the prefix back toward scene 1 - an anchor that has not drifted.
  * The opening image rides along as <Picture 1> on EVERY beat (ref_image_size=max) as the identity /
    place reference, and is the frame-0 guide on beat 1 only.
  * Assemble twice: raw, and with scene_1_anchor colour stabilisation (delivery-only polish).

Usage: python build_longform.py story.json opening.png out.json [--run NAME] [--transition color_drift_av]
"""
import argparse, json, re

MODEL = "h3-minimax/10Eros_Max_SingularityV2_PRISM2_PikaFlex_0.85_int8HQ.safetensors"
CLIP = "qwen3vl_32b_minimax_h3-w4a8_convrot.safetensors"
VIDEO_VAE = "minimax_h3_video_vae_int8_convrot.safetensors"
AUDIO_VAE = "minimax_h3_audio_vae_fp32.safetensors"

SUBJECTS = """<Subject 1> is A, the woman on the left in <Picture 1>: adult woman, curvy athletic build, light olive skin, dark wavy shoulder-length hair, oval face with dark eyes, small pendant necklace; wearing a dark teal sports bra, black shorts, black sneakers with white soles; a healed scar on her right thigh.
<Subject 2> is B, the man on the right in <Picture 1>: large heavily muscled middle-aged man, tanned skin, slicked-back black hair greying at the sides, short grey-black beard, stern face; wearing a navy t-shirt with a small logo, a worn, stained brown-grey canvas apron, dark shorts, brown leather work boots.
<Subject 3> is the place in <Picture 1>: a small grocery store produce aisle. Left: refrigerated produce shelves stacked with red apples and oranges, leafy greens, misting water. Right: shelves of packaged goods, snacks and spray bottles. Centre background: a shopping cart with vegetables, a round wall clock, a ceiling air-conditioning unit, long fluorescent tube lights. Beige tiled floor with a damp sheen. Cool fluorescent light, slightly gritty photographic look."""

STYLE = ("Cinematic action sequence, grounded grocery-store brawl. Framing: full body to waist-up, eye-level, "
         "medium focal length. Camera: fluid handheld tracking, no cuts, continuous motion. Light: cool overhead "
         "fluorescent light, the same colour palette and exposure as <Picture 1> throughout. Pacing: fast, physically "
         "believable choreography with cause and effect.")


def beats_from_story(story):
    text = story.get("StoryText") or story["Fflf"]["BeatsText"]
    out = []
    for m in re.finditer(r"^\s*\d+\.\s*\[(\d+(?:\.\d+)?)\s*s\]\s*(.+?)(?=^\s*\d+\.\s*\[|\Z)", text, re.S | re.M):
        secs, body = float(m.group(1)), " ".join(m.group(2).split())
        action, _, sound = body.partition("Sound:")
        out.append((secs, action.strip(), sound.strip()))
    return out


def beat_prompt(i, n, secs, action, sound):
    first = i == 0
    pic = ("<Picture 1> is the first frame of [Shot 1]: the video starts exactly on it." if first else
           "<Picture 1> is the opening frame of the story, given ONLY as the identity, wardrobe, place, light and "
           "colour reference. This shot is a continuation: it carries straight on from the preceding moment of "
           "motion, not from the pose in <Picture 1>; never jump back to that pose.")
    retention = ("<Picture 1> ([Shot 1] first frame): fully_preserved - the video begins on this exact frame." if first else
                 "<Picture 1> (reference): reference - the same faces, hair, build, skin, clothing, place, light and "
                 "colour palette as <Picture 1>; do not copy its pose or framing.")
    return f"""subject_definitions:
{pic}
{SUBJECTS}

summary:
[{'keyframe completion + ' if first else 'continuation + '}reference generation] Beat {i + 1} of {n}: a single continuous {secs:g}-second shot with <Subject 1> and <Subject 2> in <Subject 3>: {action}

retention_analysis:
{retention}
<Subject 1> (appears in [Shot 1]): fully_preserved - the same face, hair, build, skin and clothing in every frame, also in motion and close to the camera.
<Subject 2> (appears in [Shot 1]): fully_preserved - the same face, hair, beard, build, skin and clothing in every frame, also in motion and close to the camera.
<Subject 3> (appears in [Shot 1]): fully_preserved - the same aisle, props, light and colour palette throughout.

detailed_description:
{STYLE}
[Shot 1] A is <Subject 1>. B is <Subject 2>. {'The shot opens on <Picture 1>. ' if first else ''}{action} No one speaks.

overall_soundscape:
{sound or 'Fluorescent hum, footsteps on tile.'}

non_diegetic_music:
N/A"""


def build(story, opening, run, transition, width, height, steps, seed, max_beats=None):
    beats = beats_from_story(story)[:max_beats]
    # Context-Loop's duration_seconds is the RAW length, and AV transitions carry a 39-frame prefix
    # (1.625 s) that is trimmed before delivery - add it back so each beat delivers its own length.
    prefix = 39 / 24 if transition.endswith("_av") else 0.0
    plan = {"defaults": {"steps": steps},
            "shots": [{"id": f"beat_{i + 1:02d}", "duration_seconds": round(s + (prefix if i else 0.0), 3),
                       "prompt": beat_prompt(i, len(beats), s, a, snd)}
                      for i, (s, a, snd) in enumerate(beats)]}
    g = {}
    def node(nid, cls, **inputs):
        g[str(nid)] = {"class_type": cls, "inputs": inputs, "_meta": {"title": f"LF {cls}"}}
    L = lambda nid, out=0: [str(nid), out]

    node(1, "UNETLoader", unet_name=MODEL, weight_dtype="default")
    node(2, "CLIPLoader", clip_name=CLIP, type="minimax", device="default")
    node(3, "VAELoader", vae_name=VIDEO_VAE)
    node(4, "VAELoader", vae_name=AUDIO_VAE)
    node(5, "ModelAttentionBackend", model=L(1), attention="comfy kitchen attention")
    node(6, "H3PowerLoraStack", model=L(5), quantized_layers="auto", adaln_port="auto",
         lora_1={"on": True, "lora": "H3/AmateurHour_H3_000017500.safetensors", "strength": 0.6})
    node(7, "MiniMaxH3SigmaShift", model=L(6, 0), shift_video=12.0, shift_audio=3.0)

    node(8, "MiniMaxH3GenerationProfile", scene_continuity="Visual continuity", audio_profile="Generate audio")
    node(9, "MiniMaxH3AdvancedPolicy", chain_policy=L(8), incoming_transition=transition)
    node(10, "MiniMaxH3ChainPlanModern", plan_json=json.dumps(plan, ensure_ascii=False), chain_policy=L(9),
         run_name=run, generation_fingerprint="", width=width, height=height, encode_mode="video", crop="disabled",
         default_duration_seconds=5.0, default_steps=steps, base_seed=seed, segment_crf=18, video_blend_frames=0)
    node(11, "MiniMaxH3ChainPreflight", plan=L(10), start_clip=1, scene_range="", verify_resume_history=True)
    node(12, "MiniMaxH3ChainLoopStart", plan=L(11), start_clip=1, scene_range="", verify_resume_history=True)
    node(13, "MiniMaxH3ChainCurrent", state=L(12, 1), align_audio_reference=False)

    node(14, "LoadImage", image=opening)
    node(15, "MiniMaxH3ChainFirstSceneImage", state=L(13), image=L(14))
    node(16, "MiniMaxH3ReferenceToVideo", clip=L(2), vae=L(3), audio_vae=L(4), prompt=L(13, 4),
         width=L(13, 8), height=L(13, 9), length=L(13, 6), ref_image_size="max",
         **{"ref_images.ref_image_0": L(14)})
    # Beat 1 only: pin the opening image at frame 0 (lazy switch - AddGuide refuses a missing image).
    node(17, "MiniMaxH3AddGuide", positive=L(16, 0), latent=L(16, 1), frame_idx=0, vae=L(3), image=L(15, 0))
    node(18, "ComfySwitchNode", switch=L(15, 1), on_true=L(17), on_false=L(16, 0))
    node(19, "MiniMaxH3ChainContext", state=L(13), conditioning=L(18), vae=L(3), latent=L(16, 1),
         audio_vae=L(4), model=L(7))

    node(20, "BasicGuider", model=L(19, 4), conditioning=L(19, 0))
    node(21, "BasicScheduler", model=L(19, 4), scheduler="simple", steps=L(13, 7), denoise=1.0)
    node(22, "KSamplerSelect", sampler_name="res_multistep")
    node(23, "RandomNoise", noise_seed=L(13, 5))
    node(24, "SamplerCustomAdvanced", noise=L(23), guider=L(20), sampler=L(22), sigmas=L(21), latent_image=L(19, 3))
    node(25, "VAEDecode", samples=L(24, 0), vae=L(3))
    node(26, "VAEDecodeAudio", samples=L(24, 0), vae=L(4))
    node(27, "MiniMaxH3LoopTrim", images=L(25), trim_frames=L(19, 1), audio=L(26), fps=24.0, match_tail=True,
         state=L(13), audio_trim_mode="sync_with_video")
    node(28, "MiniMaxH3ChainSegmentSave", state=L(13), images=L(27, 0), sampled_latent=L(24, 0), audio=L(27, 1),
         images_with_overlap=L(27, 2))
    node(29, "MiniMaxH3ChainReview", state=L(13), segment=L(28), enabled=False, play_notification_sound=False,
         auto_continue_timeout_minutes=0.0, unload_models_while_waiting=False, assemble_partial_on_stop=True,
         partial_audio_source="checkpointed", audio=L(27, 1))
    node(30, "MiniMaxH3ChainLoopEnd", flow=L(12, 0), state=L(13), images=L(27, 0), sampled_latent=L(24, 0),
         segment=L(29), between_scene_cleanup="off", execution_mode="recursive")
    node(31, "MiniMaxH3ChainAssemble", manifest=L(30), audio_source="plan", filename=f"{run}_raw", audio_bitrate=256,
         copy_to_output=True, output_subfolder="video/longform", color_stabilization="off")
    node(32, "MiniMaxH3ChainAssemble", manifest=L(30), audio_source="plan", filename=f"{run}_stab", audio_bitrate=256,
         copy_to_output=True, output_subfolder="video/longform", color_stabilization="scene_1_anchor")
    return g, plan


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("story"); ap.add_argument("opening"); ap.add_argument("out")
    ap.add_argument("--run", default="longform_test")
    ap.add_argument("--transition", default="color_drift_av")
    ap.add_argument("--width", type=int, default=960); ap.add_argument("--height", type=int, default=544)
    ap.add_argument("--steps", type=int, default=20)
    ap.add_argument("--seed", type=int, default=508508584765108)
    ap.add_argument("--max-beats", type=int)
    a = ap.parse_args()
    g, plan = build(json.load(open(a.story, encoding="utf-8")), a.opening, a.run, a.transition,
                    a.width, a.height, a.steps, a.seed, a.max_beats)
    json.dump({"prompt": g}, open(a.out, "w", encoding="utf-8"), indent=1, ensure_ascii=False)
    print(f"{len(plan['shots'])} beats, {sum(s['duration_seconds'] for s in plan['shots']):g} s -> {a.out}")
