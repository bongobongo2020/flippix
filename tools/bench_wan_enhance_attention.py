#!/usr/bin/env python3
"""
Measures what the Targeted Wan Enhance sampler actually costs, and what the available
speed levers are worth on this box.

Isolates one WanVideoSampler pass — one context window (81 frames), one canvas, denoise 1.0 so
every step runs — and times it end to end for each variant. SAM3 and the composite chain are cut
out so the number is the sampler and nothing else.

Levers under test:
  attention  sdpa            torch SDPA (what the converted graph shipped with)
             comfy           routes to comfy.ldm.modules.attention.optimized_attention, which on
                             this server is Comfy Kitchen int8 attention (--use-ck-attention)
  precision  bf16            bf16 accumulate
             fp16_fast       sets torch.backends.cuda.matmul.allow_fp16_accumulation
  quant      fp8_e4m3fn_scaled       fp8 weights, bf16 matmul (weights are upcast)
             fp8_e4m3fn_scaled_fast  real fp8 matmul (needs CC>=8.9, and merged LoRAs)
  swap       blocks_to_swap  how many of the 40 transformer blocks live on the CPU

Run:  python tools/bench_wan_enhance_attention.py --video <name in ComfyUI/input>
"""
import argparse
import json
import subprocess
import time
import urllib.error
import urllib.request

SERVER = "http://10.0.0.10:8188"
SSH_HOST = "10.0.0.10"
LOG = ("/home/x2/aug5/ComfyUI-Easy-Install/ComfyUI-Easy-Install/ComfyUI/"
       "user/comfyui_8188.log")

WAN_MODEL = "wan/Wan2_2-T2V-A14B-LOW_fp8_e4m3fn_scaled_KJ.safetensors"
VAE = "Wan2_1_VAE_bf16.safetensors"
T5 = "umt5-xxl-enc-bf16.safetensors"
LORAS = [
    ("WAN/Wan2.2-Fun-A14B-InP-LOW-HPS2.1_resized_dynamic_avg_rank_15_bf16.safetensors", 0.6),
    ("WAN/Wan14B_RealismBoost.safetensors", 0.55),
    ("WAN/wan2.2_t2v_A14b_low_noise_lora_rank64_lightx2v_4step_1217.safetensors", 0.6),
]

VARIANTS = [
    # name,             attention, precision,   quantization,             swap, merge_loras
    ("baseline",        "sdpa",    "bf16",      "fp8_e4m3fn_scaled",      25,   False),
    ("ck-attn",         "comfy",   "bf16",      "fp8_e4m3fn_scaled",      25,   False),
    ("fp16_fast",       "sdpa",    "fp16_fast", "fp8_e4m3fn_scaled",      25,   False),
    ("fp8-fast",        "sdpa",    "bf16",      "fp8_e4m3fn_scaled_fast", 25,   True),
    ("ck+fp8fast",      "comfy",   "bf16",      "fp8_e4m3fn_scaled_fast", 25,   True),
    ("ck+fp8fast+swap0","comfy",   "bf16",      "fp8_e4m3fn_scaled_fast", 0,    True),
    ("no-loras",        "comfy",   "bf16",      "fp8_e4m3fn_scaled_fast", 25,   True),
]


def build(video, width, height, frames, steps, attention, precision, quant, swap,
          merge_loras, with_loras=True):
    g = {
        "1": {"class_type": "VHS_LoadVideo", "inputs": {
            "video": video, "force_rate": 0, "custom_width": 0, "custom_height": 0,
            "frame_load_cap": frames, "skip_first_frames": 0, "select_every_nth": 1,
            "format": "AnimateDiff"}},
        "2": {"class_type": "ImageResizeKJv2", "inputs": {
            "image": ["1", 0], "width": width, "height": height, "upscale_method": "lanczos",
            "keep_proportion": "stretch", "pad_color": "0, 0, 0", "crop_position": "center",
            "divisible_by": 16, "device": "cpu"}},
        "3": {"class_type": "WanVideoVAELoader", "inputs": {
            "model_name": VAE, "precision": "bf16", "use_cpu_cache": False, "verbose": False}},
        "4": {"class_type": "WanVideoEncode", "inputs": {
            "vae": ["3", 0], "image": ["2", 0], "enable_vae_tiling": False,
            "tile_x": 272, "tile_y": 272, "tile_stride_x": 144, "tile_stride_y": 128,
            "noise_aug_strength": 0, "latent_strength": 1}},
        "5": {"class_type": "WanVideoTextEncodeCached", "inputs": {
            "model_name": T5, "precision": "bf16",
            "positive_prompt": "Ultra high detail video, 8K, UHD, ultra realistic.",
            "negative_prompt": "blurry, low quality, static",
            "quantization": "disabled", "use_disk_cache": False, "device": "gpu"}},
        "6": {"class_type": "WanVideoBlockSwap", "inputs": {
            "blocks_to_swap": swap, "offload_img_emb": False, "offload_txt_emb": False,
            "use_non_blocking": True, "vace_blocks_to_swap": 0, "prefetch_blocks": 1,
            "block_swap_debug": False}},
        "10": {"class_type": "WanVideoModelLoader", "inputs": {
            "model": WAN_MODEL, "base_precision": precision, "quantization": quant,
            "load_device": "offload_device", "attention_mode": attention,
            "rms_norm_function": "default", "block_swap_args": ["6", 0]}},
        "11": {"class_type": "WanVideoEmptyEmbeds", "inputs": {
            "width": ["2", 1], "height": ["2", 2], "num_frames": frames}},
        "12": {"class_type": "WanVideoSampler", "inputs": {
            "model": ["10", 0], "image_embeds": ["11", 0], "text_embeds": ["5", 0],
            "samples": ["4", 0], "steps": steps, "cfg": 1, "shift": 5, "seed": 42,
            "force_offload": True, "scheduler": "unipc/beta", "riflex_freq_index": 0,
            "denoise_strength": 1.0, "batched_cfg": False, "rope_function": "comfy",
            "start_step": 0, "end_step": -1, "add_noise_to_samples": False}},
        "13": {"class_type": "WanVideoDecode", "inputs": {
            "vae": ["3", 0], "samples": ["12", 0], "enable_vae_tiling": False,
            "tile_x": 272, "tile_y": 272, "tile_stride_x": 144, "tile_stride_y": 128,
            "normalization": "default"}},
        "14": {"class_type": "VHS_VideoCombine", "inputs": {
            "images": ["13", 0], "frame_rate": 24, "loop_count": 0,
            "filename_prefix": "bench/wanattn", "format": "video/h264-mp4",
            "pix_fmt": "yuv420p", "crf": 28, "save_metadata": False, "trim_to_audio": False,
            "pingpong": False, "save_output": False}},
    }
    if with_loras:
        prev = None
        for i, (name, strength) in enumerate(LORAS):
            nid = str(20 + i)
            inputs = {"lora": name, "strength": strength, "low_mem_load": False,
                      "merge_loras": merge_loras}
            if prev:
                inputs["prev_lora"] = [prev, 0]
            g[nid] = {"class_type": "WanVideoLoraSelect", "inputs": inputs}
            prev = nid
        g["10"]["inputs"]["lora"] = [prev, 0]
    return g


def log_marks():
    """(sampling-start, sampling-end) timestamps currently in the server log."""
    out = subprocess.run(
        ["ssh", SSH_HOST, f"grep -n 'Sampling start\\|Sampling end' {LOG} | tail -4"],
        capture_output=True, text=True, timeout=60).stdout
    return out.strip().splitlines()


def parse_ts(line):
    return line.split("[")[1].split("]")[0]


def to_seconds(ts):
    t = ts.split(" ")[1]
    h, m, s = t.split(":")
    return int(h) * 3600 + int(m) * 60 + float(s)


def run_variant(name, graph, timeout):
    before = log_marks()
    payload = json.dumps({"prompt": graph, "client_id": "bench"}).encode()
    req = urllib.request.Request(f"{SERVER}/prompt", data=payload,
                                 headers={"Content-Type": "application/json"})
    try:
        pid = json.loads(urllib.request.urlopen(req, timeout=120).read())["prompt_id"]
    except urllib.error.HTTPError as e:
        return name, None, f"rejected: {e.read().decode()[:400]}"

    wall0 = time.time()
    deadline = time.time() + timeout
    while time.time() < deadline:
        time.sleep(5)
        hist = json.loads(urllib.request.urlopen(f"{SERVER}/history/{pid}", timeout=60).read())
        if pid not in hist:
            continue
        st = hist[pid].get("status", {})
        if st.get("status_str") == "error":
            msg = ""
            for m in st.get("messages", []):
                if m[0] == "execution_error":
                    msg = f"{m[1].get('node_type')}: {m[1].get('exception_message','')[:200]}"
            return name, None, f"error: {msg}"
        if st.get("completed"):
            wall = time.time() - wall0
            after = log_marks()
            new = [l for l in after if l not in before]
            starts = [parse_ts(l) for l in new if "Sampling start" in l]
            ends = [parse_ts(l) for l in new if "Sampling end" in l]
            if starts and ends:
                return name, to_seconds(ends[-1]) - to_seconds(starts[-1]), f"wall {wall:.0f}s"
            return name, None, f"no sampling marks (wall {wall:.0f}s)"
    return name, None, "timeout"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--video", required=True, help="filename already in ComfyUI/input")
    ap.add_argument("--width", type=int, default=768)
    ap.add_argument("--height", type=int, default=432)
    ap.add_argument("--frames", type=int, default=81, help="one context window's worth")
    ap.add_argument("--steps", type=int, default=6)
    ap.add_argument("--timeout", type=int, default=1800)
    ap.add_argument("--only", default=None, help="comma-separated variant names")
    args = ap.parse_args()

    wanted = set(args.only.split(",")) if args.only else None
    print(f"{args.frames} frames @ {args.width}x{args.height}, {args.steps} steps, denoise 1.0\n")
    results = []
    for name, attention, precision, quant, swap, merge in VARIANTS:
        if wanted and name not in wanted:
            continue
        graph = build(args.video, args.width, args.height, args.frames, args.steps,
                      attention, precision, quant, swap, merge,
                      with_loras=(name != "no-loras"))
        label, secs, note = run_variant(name, graph, args.timeout)
        results.append((label, secs, note))
        shown = f"{secs:7.1f}s  ({secs / args.steps:5.2f} s/step)" if secs else "     --"
        print(f"  {label:<18} {shown}   {note}")

    base = next((s for n, s, _ in results if n == "baseline" and s), None)
    if base:
        print("\nspeedup vs baseline:")
        for n, s, _ in results:
            if s:
                print(f"  {n:<18} {base / s:.2f}x")


if __name__ == "__main__":
    raise SystemExit(main())
