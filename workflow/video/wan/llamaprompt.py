#!/usr/bin/env python3
"""
llamaprompt.py — Two-step prompt generation via llama.cpp (mirrors genprompt.py / Gemini flow).

Step 1: Extract frames from the motion-reference video → ask llama to describe the actions.
Step 2: Send reference image + action description → ask llama for the final SCAIL prompt.
Optionally submit the result directly to ComfyUI.

Usage:
    python llamaprompt.py <video_path> <image_path> [options]

Examples:
    python llamaprompt.py fight.mp4 ref.png
    python llamaprompt.py fight.mp4 ref.png --submit
    python llamaprompt.py fight.mp4 ref.png --skip 184 --frames 81 --submit --comfy http://localhost:8188
"""

import argparse
import base64
import copy
import json
import os
import subprocess
import sys
import tempfile
import uuid

import requests

# ── Defaults ──────────────────────────────────────────────────────────────────

LLAMA_URL = "http://192.168.1.138:8080"
COMFY_URL = "http://localhost:8188"
WORKFLOW  = os.path.join(os.path.dirname(__file__),
                         "SCAIL+Video+Multi-Character+Motion+Transfer+V1API.json")

# Number of frames to sample from the video for motion analysis
VIDEO_SAMPLE_FRAMES = 8

_SUMMARY_PROMPT = (
    'Please summarize this video. No line breaks. '
    'You should focus on the movements of the main character or characters. '
    'You should avoid describing the lighting and the detailed background. '
    'You should not describe the camera movements. '
    'Depict the body movements in a concise manner rather than inferring what the '
    'character or characters might be doing or being too detailed. '
    'Example 1: "A young woman dances on an escalator. She wears a gray long-sleeved '
    'top and blue skinny jeans. Her long hair cascades down her shoulders as she sways '
    'to the rhythm, her body moving freely in sync with the music." '
    'Example 2: "A woman is dancing in a bright room. She is wearing a black '
    'short-sleeved top with a pink pattern, a pink pleated skirt, black boots, and a '
    'pink hat. She is performing a dance routine, moving her arms and legs in various '
    'ways, including spreading her arms, crossing her arms, raising her hands, and '
    'placing her hands on her head."'
)

_FINAL_PROMPT_SYSTEM = """\
You are part of a team of bots that creates videos.
You work with an assistant bot that will draw anything you say.
You will be prompted by people looking to create detailed, amazing videos.
The way to accomplish this is to combine the object and the scene in the image to make a new description.
Generally, remove the main object and the scene in the provided caption, replace them with the object and the scene in the image, keep the action in the provided caption.
For example, if the provided caption shows a guy wearing an orange outfit is waving his hands in a room, and the image shows a girl in a red dress in a bar, you should rewrite the caption to be the description "a girl in a red dress is waving her hands in a bar".
You should use the image to describe the scene and the main object, the provided caption to describe the action, then make a detailed and descriptive description of the video.
There are a few rules to follow:
Avoid describing the lights and the camera. Avoid mentioning the color.
Especially, if the character is in anime style, you should mention the anime style. Begin with sentences like An Anime Character..., A humanoid figure..., A Disney Princess..., etc.
You will only ever output a single video description per user request.\
"""


# ── Helpers ───────────────────────────────────────────────────────────────────

def _to_b64(path: str) -> tuple[str, str]:
    ext  = os.path.splitext(path)[1].lower().lstrip('.')
    mime = {'jpg': 'image/jpeg', 'jpeg': 'image/jpeg',
            'png': 'image/png',  'webp': 'image/webp'}.get(ext, 'image/jpeg')
    with open(path, 'rb') as f:
        return base64.b64encode(f.read()).decode('utf-8'), mime


def extract_frames(video_path: str, n_frames: int,
                   skip_first: int = 0, frame_cap: int = 0) -> list[str]:
    """
    Extract n_frames evenly-spaced JPEG frames from video_path using ffmpeg.
    Returns list of temp file paths (caller must delete them).
    Respects skip_first (frames to skip) and frame_cap (max frames to consider).
    """
    # Use ffprobe to get total frame count
    probe_cmd = [
        'ffprobe', '-v', 'error',
        '-select_streams', 'v:0',
        '-show_entries', 'stream=nb_frames,r_frame_rate,duration',
        '-of', 'json', video_path,
    ]
    try:
        probe = subprocess.run(probe_cmd, capture_output=True, text=True, timeout=30)
        info   = json.loads(probe.stdout)
        stream = info.get('streams', [{}])[0]
        fps_str  = stream.get('r_frame_rate', '24/1')
        parts    = fps_str.split('/')
        fps      = float(parts[0]) / float(parts[1]) if len(parts) == 2 else float(fps_str)
        nb       = stream.get('nb_frames')
        duration = float(stream.get('duration') or 0)
        total    = int(nb) if nb and str(nb) != 'N/A' else int(duration * fps)
    except Exception:
        fps, total = 24.0, 0

    start = max(0, skip_first)
    end   = (start + frame_cap) if frame_cap > 0 else total
    end   = min(end, total) if total > 0 else end
    span  = max(1, end - start)

    tmp_dir = tempfile.mkdtemp()
    paths   = []

    for i in range(n_frames):
        frame_idx = start + int(i * span / n_frames)
        timestamp = frame_idx / fps if fps > 0 else i
        out_path  = os.path.join(tmp_dir, f'frame_{i:03d}.jpg')
        cmd = [
            'ffmpeg', '-y', '-ss', f'{timestamp:.4f}',
            '-i', video_path,
            '-frames:v', '1',
            '-q:v', '3',
            out_path,
        ]
        subprocess.run(cmd, capture_output=True, timeout=30)
        if os.path.isfile(out_path):
            paths.append(out_path)

    return paths


def _stream_llama(payload: dict, llama_url: str, label: str = '') -> str:
    """POST payload to llama, stream output, return full text."""
    resp = requests.post(f"{llama_url}/v1/chat/completions",
                         json=payload, stream=True, timeout=300)
    resp.raise_for_status()
    collected = []
    if label:
        print(f'[{label}] ', end='', flush=True)
    for raw in resp.iter_lines():
        if not raw:
            continue
        line = raw.decode('utf-8') if isinstance(raw, bytes) else raw
        if not line.startswith('data: '):
            continue
        chunk = line[6:].strip()
        if chunk == '[DONE]':
            break
        try:
            delta = json.loads(chunk)['choices'][0]['delta']
            text  = delta.get('content') or delta.get('reasoning_content') or ''
            if text:
                print(text, end='', flush=True)
                collected.append(text)
        except Exception:
            pass
    print()
    return ''.join(collected).strip()


def step1_video_summary(video_path: str, llama_url: str, model: str,
                        skip_first: int, frame_cap: int,
                        n_frames: int, temperature: float) -> str:
    """Extract frames from video, send to llama, get action description."""
    print(f'[step1] Extracting {n_frames} frames from video...')
    frame_paths = extract_frames(video_path, n_frames, skip_first, frame_cap)
    if not frame_paths:
        raise RuntimeError('Failed to extract any frames from video — is ffmpeg installed?')
    print(f'[step1] Got {len(frame_paths)} frames. Querying llama...')

    content = []
    for fp in frame_paths:
        b64, mime = _to_b64(fp)
        content.append({'type': 'image_url',
                        'image_url': {'url': f'data:{mime};base64,{b64}'}})
    content.append({'type': 'text', 'text': _SUMMARY_PROMPT})

    payload = {
        'model':       model,
        'messages':    [{'role': 'user', 'content': content}],
        'stream':      True,
        'temperature': temperature,
        'max_tokens':  1024,
    }

    result = _stream_llama(payload, llama_url, label='step1')

    # Cleanup temp frames
    for fp in frame_paths:
        try:
            os.unlink(fp)
        except Exception:
            pass
    try:
        os.rmdir(os.path.dirname(frame_paths[0]))
    except Exception:
        pass

    return result


def step2_final_prompt(image_path: str, action_description: str,
                       llama_url: str, model: str,
                       temperature: float, max_tokens: int) -> str:
    """Send reference image + action description → final SCAIL prompt."""
    print('[step2] Generating final prompt from image + action description...')
    b64, mime = _to_b64(image_path)
    content = [
        {'type': 'text',      'text': _FINAL_PROMPT_SYSTEM},
        {'type': 'image_url', 'image_url': {'url': f'data:{mime};base64,{b64}'}},
        {'type': 'text',      'text': f'Provided Caption: {action_description}'},
    ]
    payload = {
        'model':       model,
        'messages':    [{'role': 'user', 'content': content}],
        'stream':      True,
        'temperature': temperature,
        'max_tokens':  max_tokens,
    }
    return _stream_llama(payload, llama_url, label='step2')


def build_scail_workflow(workflow_path: str, prompt: str,
                         comfy_image: str, comfy_video: str,
                         skip_first_frames: int, frame_load_cap: int,
                         fps: int, resolution: int, seed: int) -> dict:
    with open(workflow_path, encoding='utf-8') as f:
        wf = json.load(f)
    p = copy.deepcopy(wf)
    if seed < 0:
        seed = int(uuid.uuid4().int % (2 ** 32))

    p['112']['inputs']['prompt'] = prompt                    # CR Prompt Text
    if comfy_image:
        p['52']['inputs']['image'] = comfy_image             # LoadImage
    if comfy_video:
        p['65']['inputs']['video'] = comfy_video             # VHS_LoadVideo
    p['65']['inputs']['skip_first_frames'] = int(skip_first_frames)
    p['65']['inputs']['frame_load_cap']    = int(frame_load_cap)
    p['135']['inputs']['Number']           = max(1, int(fps))
    p['144']['inputs']['Number']           = max(256, int(resolution))
    p['152']['inputs']['value']            = seed
    return p


def submit_workflow(workflow: dict, comfy_url: str) -> tuple[str, str]:
    client_id = str(uuid.uuid4())
    resp = requests.post(f"{comfy_url}/prompt",
                         json={'prompt': workflow, 'client_id': client_id},
                         timeout=30)
    resp.raise_for_status()
    return resp.json().get('prompt_id', ''), client_id


# ── CLI ───────────────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(
        description='Two-step llama.cpp prompt generator for the SCAIL motion-transfer workflow')

    parser.add_argument('video_path',
        help='Path to the motion-reference video (drives Step 1)')
    parser.add_argument('image_path',
        help='Path to the character reference image (drives Step 2)')

    parser.add_argument('--llama', default=LLAMA_URL,
        help=f'llama.cpp server URL (default: {LLAMA_URL})')
    parser.add_argument('--model', default='',
        help='Model name to request (leave blank for server default)')
    parser.add_argument('--temperature', type=float, default=0.7)
    parser.add_argument('--max-tokens', type=int, default=2048,
        help='Max tokens for the final prompt (step 2)')
    parser.add_argument('--video-frames', type=int, default=VIDEO_SAMPLE_FRAMES,
        help=f'Number of frames to sample from video for step 1 (default: {VIDEO_SAMPLE_FRAMES})')

    parser.add_argument('--skip', type=int, default=0,
        dest='skip_frames',
        help='skip_first_frames: video frames to skip before sampling (default: 0)')
    parser.add_argument('--frame-cap', type=int, default=0,
        help='Max frames to consider from video (0 = all, default: 0)')

    parser.add_argument('--output', default='',
        help='Save generated prompt to this text file')

    # ComfyUI submission
    parser.add_argument('--submit', action='store_true',
        help='Submit the SCAIL workflow to ComfyUI after generating the prompt')
    parser.add_argument('--comfy', default=COMFY_URL,
        help=f'ComfyUI URL (default: {COMFY_URL})')
    parser.add_argument('--workflow', default=WORKFLOW,
        help='Path to the SCAIL workflow JSON')
    parser.add_argument('--comfy-image', default='',
        help='ComfyUI filename for the character image (node 52). '
             'Defaults to basename of image_path.')
    parser.add_argument('--comfy-video', default='',
        help='ComfyUI filename for the motion video (node 65). '
             'Defaults to basename of video_path.')
    parser.add_argument('--fps', type=int, default=24)
    parser.add_argument('--resolution', type=int, default=1280)
    parser.add_argument('--seed', type=int, default=-1)

    args = parser.parse_args()

    for label, path in [('video', args.video_path), ('image', args.image_path)]:
        if not os.path.isfile(path):
            print(f'ERROR: {label} not found: {path}', file=sys.stderr)
            sys.exit(1)

    sep = '─' * 60
    print(sep)
    print(f'  llama : {args.llama}')
    print(f'  video : {args.video_path}')
    print(f'  image : {args.image_path}')
    print(sep)

    # ── Step 1: video → action description ────────────────────────────────────
    try:
        action_summary = step1_video_summary(
            video_path=args.video_path,
            llama_url=args.llama,
            model=args.model,
            skip_first=args.skip_frames,
            frame_cap=args.frame_cap,
            n_frames=args.video_frames,
            temperature=args.temperature,
        )
    except requests.exceptions.ConnectionError:
        print(f'\nERROR: cannot reach llama server at {args.llama}', file=sys.stderr)
        sys.exit(1)
    except Exception as e:
        print(f'\nERROR (step 1): {e}', file=sys.stderr)
        sys.exit(1)

    print(sep)

    # ── Step 2: image + action → final prompt ─────────────────────────────────
    try:
        final_prompt = step2_final_prompt(
            image_path=args.image_path,
            action_description=action_summary,
            llama_url=args.llama,
            model=args.model,
            temperature=args.temperature,
            max_tokens=args.max_tokens,
        )
    except requests.exceptions.ConnectionError:
        print(f'\nERROR: cannot reach llama server at {args.llama}', file=sys.stderr)
        sys.exit(1)
    except Exception as e:
        print(f'\nERROR (step 2): {e}', file=sys.stderr)
        sys.exit(1)

    print(sep)
    print(f'[done] Final prompt:\n{final_prompt}')
    print(sep)

    if args.output:
        with open(args.output, 'w', encoding='utf-8') as f:
            f.write(final_prompt)
        print(f'[done] Prompt saved → {args.output}')

    if not args.submit:
        print('[done] Use --submit to send to ComfyUI.')
        return

    if not os.path.isfile(args.workflow):
        print(f'ERROR: workflow not found: {args.workflow}', file=sys.stderr)
        sys.exit(1)

    comfy_image = args.comfy_image or os.path.basename(args.image_path)
    comfy_video = args.comfy_video or os.path.basename(args.video_path)

    print(f'[submit] Building SCAIL workflow...')
    workflow = build_scail_workflow(
        workflow_path=args.workflow,
        prompt=final_prompt,
        comfy_image=comfy_image,
        comfy_video=comfy_video,
        skip_first_frames=args.skip_frames,
        frame_load_cap=args.frame_cap if args.frame_cap > 0 else 81,
        fps=args.fps,
        resolution=args.resolution,
        seed=args.seed,
    )

    print(f'[submit] Submitting to ComfyUI at {args.comfy}...')
    try:
        prompt_id, client_id = submit_workflow(workflow, args.comfy)
    except requests.exceptions.ConnectionError:
        print(f'ERROR: cannot reach ComfyUI at {args.comfy}', file=sys.stderr)
        sys.exit(1)
    except Exception as e:
        print(f'ERROR: {e}', file=sys.stderr)
        sys.exit(1)

    print(f'[submit] Queued!  prompt_id={prompt_id}')
    print(f'[submit] Poll:    {args.comfy}/history/{prompt_id}')


if __name__ == '__main__':
    main()
