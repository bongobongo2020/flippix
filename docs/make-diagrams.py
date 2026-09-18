# Regenerates the README diagrams (feature-map.svg, pipeline.svg) in place: python docs/make-diagrams.py
import html
import os

OUT = os.path.dirname(os.path.abspath(__file__))

BG = "#0d1220"
PANEL = "#151d2e"
CARD = "#1b2537"
STROKE = "#26324a"
TEXT = "#e8eef8"
MUTED = "#93a3bd"
FONT = "'Segoe UI',Inter,Helvetica,Arial,sans-serif"

PURPLE = "#8b7bff"
TEAL = "#34d3bd"
AMBER = "#f0a93b"


def esc(s):
    return html.escape(s, quote=True)


def card(x, y, w, h, accent, title, sub, rx=9):
    """One feature card: accent bar on the left, title, muted subtitle."""
    o = []
    o.append(f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="{rx}" fill="{CARD}" stroke="{STROKE}"/>')
    o.append(f'<rect x="{x}" y="{y+7}" width="3" height="{h-14}" rx="1.5" fill="{accent}"/>')
    o.append(f'<text x="{x+14}" y="{y+19}" fill="{TEXT}" font-family="{FONT}" font-size="12.5" '
             f'font-weight="600">{esc(title)}</text>')
    if sub:
        o.append(f'<text x="{x+14}" y="{y+34}" fill="{MUTED}" font-family="{FONT}" font-size="10.5">{esc(sub)}</text>')
    return "\n".join(o)


def group_label(x, y, text, accent):
    return (f'<text x="{x}" y="{y}" fill="{accent}" font-family="{FONT}" font-size="10.5" '
            f'font-weight="700" letter-spacing="1.4">{esc(text.upper())}</text>')


# ---------------------------------------------------------------- feature map
COLS = [
    {
        "accent": PURPLE,
        "title": "Image Generator  ·  main window",
        "note": "Create / Edit / Advanced tab groups",
        "blocks": [
            ("Create", [
                ("Image Generator", "Z-Image · Qwen 2512 · Klein Flux.2 · Anima · Krea2 turbo + LoRA stacks"),
                ("Story Image Q", "Batch a whole story into keyframes, hand off to video"),
                ("Amateur", "Amateur/phone-camera realism pass with LoRA support"),
                ("Ideogram", "Prompt + draggable bounding-box composition, 2x refine pass"),
            ]),
            ("Edit", [
                ("Editor", "Paint an inpaint mask over any image and re-render the region"),
                ("Qwen Edit", "Swap one or two characters into a base scene (Qwen-Image-Edit 2511)"),
                ("Restore", "Flux.2 Klein restoration + pixel-drift realign and blend-back"),
            ]),
            ("Advanced", [
                ("Camera Angle", "Low / high / bird's-eye / rotation re-shoots that keep identity"),
                ("Control", "Klein ControlNet from a pose image or video · Krea2 two-reference edit"),
            ]),
        ],
    },
    {
        "accent": TEAL,
        "title": "Video Generator  ·  11 pipelines",
        "note": "Every tab: analyze → prompt → queue → ComfyUI",
        "blocks": [
            ("Story & motion", [
                ("Story Video Generator", "Folder of stills → clips · 5 stacks: Sulphur 2, 10Eros, LTX-22-B, Dasiwa, Wan 2.2"),
                ("Infinite Talk", "Audio-driven talking video, 81-frame chunked Wan 2.1"),
                ("Scail 2", "Klein character swap → SCAIL II motion transfer on one tab"),
                ("VR 180", "Flat clip → equirectangular SBS 3D via LTX IC-LoRA + depth"),
                ("Video Sound", "Re-generate a clip with synced speech and sound effects"),
            ]),
            ("Character & face", [
                ("10Eros ConvRot", "Face ref → 4 LTX FaceID seed previews → full-res finish"),
                ("FaceID Char Sheet", "Character image + audio + control video in one shot"),
            ]),
            ("MiniMax H3 family", [
                ("MiniMax H3", "Image as first frame → H3 prompt → video with synced audio"),
                ("MiniMax FFLF", "First+last frame (or a folder of pairs) → 3 seed previews → turbo finish"),
                ("MiniMax H3 T2V", "One image → dense ~15s multi-shot prompt → long-form video"),
                ("MiniMax Character", "1-2 character refs act out a scene image, story mode splits clips"),
            ]),
        ],
    },
    {
        "accent": AMBER,
        "title": "Enhance & platform",
        "note": "Shared services behind every tab",
        "blocks": [
            ("Enhance Video window", [
                ("Interpolate", "GIMM frame interpolation for smooth slow motion"),
                ("Upscale", "RTX Super Resolution or SeedVR2 7B INT8, selectable scale"),
            ]),
            ("Platform services", [
                ("LLM analysis", "LM Studio / Ollama / llama-server vision analyze, saved server profiles"),
                ("Prompt library", "Per-tab system prompts in prompts/ + persistent scene library"),
                ("Queue engine", "Per-tab queues with pause / resume / cancel, persisted across restarts"),
                ("Missing model resolver", "Offers download, locate-folder or register when a model is absent"),
                ("Missing node resolver", "Detects unknown class types and git-clones the node pack"),
                ("LoRA manager", "Local or network LoRA folders, multi-slot Power LoRA stacks"),
                ("16 GB tier", "Auto-swaps to memory-optimised workflow/16gb graphs on smaller GPUs"),
                ("ComfyUI lifecycle", "Auto-start, crash restart, fresh install, clone/backup + restore"),
            ]),
        ],
    },
]

W, PAD, GAP = 1280, 34, 22
COL_W = (W - 2 * PAD - 2 * GAP) // 3
CARD_H, CARD_GAP = 44, 7
HEAD_H = 52
BLOCK_LABEL_H = 24


def build_feature_map():
    body = []
    top = 128
    max_bottom = 0
    for i, col in enumerate(COLS):
        x = PAD + i * (COL_W + GAP)
        accent = col["accent"]
        # measure
        h = HEAD_H + 12
        for _, items in col["blocks"]:
            h += BLOCK_LABEL_H + len(items) * (CARD_H + CARD_GAP)
        h += 8
        body.append(f'<rect x="{x}" y="{top}" width="{COL_W}" height="{h}" rx="14" fill="{PANEL}" stroke="{STROKE}"/>')
        body.append(f'<rect x="{x}" y="{top}" width="{COL_W}" height="4" rx="2" fill="{accent}"/>')
        body.append(f'<text x="{x+18}" y="{top+27}" fill="{TEXT}" font-family="{FONT}" font-size="14" '
                    f'font-weight="700">{esc(col["title"])}</text>')
        body.append(f'<text x="{x+18}" y="{top+43}" fill="{MUTED}" font-family="{FONT}" '
                    f'font-size="10.5">{esc(col["note"])}</text>')
        y = top + HEAD_H + 14
        for label, items in col["blocks"]:
            body.append(group_label(x + 18, y + 8, label, accent))
            y += BLOCK_LABEL_H
            for title, sub in items:
                body.append(card(x + 14, y, COL_W - 28, CARD_H, accent, title, sub))
                y += CARD_H + CARD_GAP
        max_bottom = max(max_bottom, top + h)

    height = max_bottom + 56
    out = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{height}" '
           f'viewBox="0 0 {W} {height}" role="img" aria-label="FlipPix feature map">',
           f'<rect width="{W}" height="{height}" fill="{BG}"/>']
    out.append(f'<text x="{PAD}" y="52" fill="{TEXT}" font-family="{FONT}" font-size="27" font-weight="700">'
               f'FlipPix &#183; what the app does</text>')
    out.append(f'<text x="{PAD}" y="78" fill="{MUTED}" font-family="{FONT}" font-size="13">'
               f'A Windows desktop front-end that turns ~30 hand-tuned ComfyUI graphs into single-purpose tabs, '
               f'each with its own LLM prompt writer and job queue.</text>')
    out.append(f'<rect x="{PAD}" y="96" width="{W-2*PAD}" height="1" fill="{STROKE}"/>')
    out.extend(body)
    out.append(f'<text x="{PAD}" y="{height-22}" fill="{MUTED}" font-family="{FONT}" font-size="10.5">'
               f'Windows 11 &#183; .NET 8 WPF (MVVM) &#183; drives a local or remote ComfyUI over HTTP + WebSocket'
               f'</text>')
    out.append("</svg>")
    return "\n".join(out)


# ------------------------------------------------------------------- pipeline
def build_pipeline():
    W2, H2 = 1280, 386
    o = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{W2}" height="{H2}" viewBox="0 0 {W2} {H2}" '
         f'role="img" aria-label="FlipPix processing pipeline">',
         f'<rect width="{W2}" height="{H2}" fill="{BG}"/>',
         f'<defs><marker id="a" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" '
         f'orient="auto-start-reverse"><path d="M0,0 L10,5 L0,10 z" fill="{MUTED}"/></marker></defs>']
    o.append(f'<text x="34" y="48" fill="{TEXT}" font-family="{FONT}" font-size="24" font-weight="700">'
             f'How a job flows</text>')
    o.append(f'<text x="34" y="72" fill="{MUTED}" font-family="{FONT}" font-size="12.5">'
             f'The same path runs behind every tab &#8212; only the workflow JSON and the system prompt change.</text>')

    stages = [
        (PURPLE, "1 &#183; Input", ["Image, first/last frame pair", "video, audio or reference cast",
                                    "picked in the tab"]),
        (PURPLE, "2 &#183; Analyze", ["Vision LLM reads the input", "with that tab's system prompt",
                                      "from prompts/prompt2json/"]),
        (TEAL, "3 &#183; Patch", ["Workflow JSON is loaded and", "prompt, seed, size, duration,",
                                  "LoRAs and refs are injected"]),
        (TEAL, "4 &#183; Queue", ["WorkflowQueueCoordinator serialises", "jobs; per-tab queues pause,",
                                  "resume and survive restarts"]),
        (AMBER, "5 &#183; Execute", ["ComfyUI runs the graph;", "progress streams back over",
                                     "WebSocket, errors surface"]),
        (AMBER, "6 &#183; Collect", ["Images / videos / audio pulled", "from /history, previewed,",
                                     "optionally joined by FFmpeg"]),
    ]
    bw, bh, gap2, y0 = 180, 132, 26, 112
    x = 34
    for accent, title, lines in stages:
        o.append(f'<rect x="{x}" y="{y0}" width="{bw}" height="{bh}" rx="12" fill="{PANEL}" stroke="{STROKE}"/>')
        o.append(f'<rect x="{x}" y="{y0}" width="{bw}" height="3.5" rx="1.75" fill="{accent}"/>')
        o.append(f'<text x="{x+16}" y="{y0+32}" fill="{TEXT}" font-family="{FONT}" font-size="13.5" '
                 f'font-weight="700">{title}</text>')
        for j, ln in enumerate(lines):
            o.append(f'<text x="{x+16}" y="{y0+56+j*17}" fill="{MUTED}" font-family="{FONT}" '
                     f'font-size="10.8">{esc(ln)}</text>')
        if x + bw + gap2 < 34 + 6 * (bw + gap2):
            ax = x + bw + 5
            o.append(f'<line x1="{ax}" y1="{y0+bh/2}" x2="{ax+gap2-10}" y2="{y0+bh/2}" stroke="{MUTED}" '
                     f'stroke-width="1.6" marker-end="url(#a)"/>')
        x += bw + gap2

    # supporting rail
    ry = y0 + bh + 46
    o.append(f'<text x="34" y="{ry-14}" fill="{MUTED}" font-family="{FONT}" font-size="10.5" '
             f'font-weight="700" letter-spacing="1.4">ALWAYS ON</text>')
    rail = [
        ("Missing models", "download, locate or register"),
        ("Missing nodes", "resolve pack, git clone, reboot"),
        ("Auto-start / restart", "watches a local ComfyUI process"),
        ("16 GB tier", "swaps in memory-lean graphs"),
        ("Enhance pass", "interpolate + RTX / SeedVR2 upscale"),
    ]
    rw = (W2 - 68 - 4 * 18) / 5
    rx = 34
    for title, sub in rail:
        o.append(card(rx, ry, rw, 52, TEAL, title, sub))
        rx += rw + 18
    o.append("</svg>")
    return "\n".join(o)


with open(os.path.join(OUT, "feature-map.svg"), "w", encoding="utf-8") as f:
    f.write(build_feature_map())
with open(os.path.join(OUT, "pipeline.svg"), "w", encoding="utf-8") as f:
    f.write(build_pipeline())
print("ok", OUT)
