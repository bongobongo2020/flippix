"""Checks the 🌀 MiniMax I2V tab's stereo-layout detector against pictures it should and should
not fire on.

Mirrors MiniMaxI2VReference.DetectStereoLayout line for line - the same 320px preview, the same
luma weights, the same parallax search and the same three constants - then runs it over synthetic
stereo pairs, flat photographs, and the near-misses that would fool a simpler test: two unrelated
pictures packed like a pair, a landscape mirrored in water, an image mirrored left to right.

The point is the GAP. A threshold is only safe if true pairs and everything else land far apart,
so the run prints where each side actually fell and fails if any case lands on the wrong side.

Usage:  python tools/verify_stereo_detect.py [extra-image.jpg ...]
        Extra images are reported without an expected answer - handy for checking a real
        VR180 still, or whatever the detector just got wrong.

Needs Pillow (pip install pillow); the app itself needs nothing extra.
"""
import sys
from PIL import Image, ImageDraw, ImageFilter
import random

THRESHOLD = 0.55
MARGIN = 0.75
MIN_CONTRAST = 6.0
PREVIEW_W = 320
SHIFT_STEP = 4


def detect(img):
    """The C# DetectStereoLayout, in Python."""
    if img.width != PREVIEW_W:
        h = max(1, round(img.height * PREVIEW_W / img.width))
        img = img.resize((PREVIEW_W, h), Image.LANCZOS)
    w, h = img.size
    if w < 32 or h < 32:
        return 'None', 0, 0, 0
    px = img.convert('RGB').load()
    L = [[0.299 * px[x, y][0] + 0.587 * px[x, y][1] + 0.114 * px[x, y][2]
          for x in range(w)] for y in range(h)]

    n = 0; total = 0.0
    for y in range(0, h, 2):
        for x in range(0, w, 2):
            total += L[y][x]; n += 1
    mean = total / n
    dev = 0.0
    for y in range(0, h, 2):
        for x in range(0, w, 2):
            dev += abs(L[y][x] - mean)
    contrast = dev / n
    if contrast < MIN_CONTRAST:
        return 'None', contrast, 0, 0

    half_w, half_h = w // 2, h // 2

    def best(second_origin_x, second_origin_y, span_w, span_h):
        """Lowest mean |difference| between the two halves over the candidate parallax shifts.
        The range is 10% of ONE EYE's width, which is the frame for over-under and half of it
        for side-by-side."""
        max_shift = max(SHIFT_STEP, span_w // 10)
        lo = None
        for dx in range(-max_shift, max_shift + 1, SHIFT_STEP):
            acc = 0.0; count = 0
            for y in range(0, span_h, 2):
                for x in range(max_shift, span_w - max_shift, 2):
                    acc += abs(L[y][x] - L[second_origin_y + y][second_origin_x + x + dx])
                    count += 1
            if count == 0:
                continue
            score = acc / count
            if lo is None or score < lo:
                lo = score
        return (lo if lo is not None else float('inf')) / contrast

    sbs = best(half_w, 0, half_w, h)
    ou = best(0, half_h, w, half_h)

    if sbs < THRESHOLD and sbs < ou * MARGIN:
        return 'SideBySide', contrast, sbs, ou
    if ou < THRESHOLD and ou < sbs * MARGIN:
        return 'OverUnder', contrast, sbs, ou
    return 'None', contrast, sbs, ou


def scene(w, h, seed=1):
    random.seed(seed)
    img = Image.new('RGB', (w, h))
    d = ImageDraw.Draw(img)
    for y in range(h):
        d.line([(0, y), (w, y)], fill=(30 + y * 120 // h, 40, 90 + y * 100 // h))
    for _ in range(40):
        x0, y0 = random.randrange(w), random.randrange(h)
        s = random.randrange(10, max(12, w // 6))
        d.ellipse([x0, y0, x0 + s, y0 + s // 2],
                  fill=(random.randrange(256), random.randrange(256), random.randrange(256)))
    for _ in range(15):
        x0, y0 = random.randrange(w), random.randrange(h)
        d.rectangle([x0, y0, x0 + random.randrange(5, 60), y0 + random.randrange(5, 60)],
                    fill=(random.randrange(256), random.randrange(256), random.randrange(256)))
    return img.filter(ImageFilter.GaussianBlur(0.6))


def smooth(w, h, seed=1):
    return scene(w, h, seed).filter(ImageFilter.GaussianBlur(6))


def pair(eye_w, eye_h, disparity, layout, seed=1, maker=scene):
    base = maker(eye_w + abs(disparity) + 8, eye_h, seed)
    left = base.crop((0, 0, eye_w, eye_h))
    right = base.crop((disparity, 0, disparity + eye_w, eye_h))
    if layout == 'sbs':
        out = Image.new('RGB', (eye_w * 2, eye_h))
        out.paste(left, (0, 0)); out.paste(right, (eye_w, 0))
    else:
        out = Image.new('RGB', (eye_w, eye_h * 2))
        out.paste(left, (0, 0)); out.paste(right, (0, eye_h))
    return out


def unrelated(w, h, layout):
    a, b = scene(w, h, 1), scene(w, h, 99)
    if layout == 'sbs':
        out = Image.new('RGB', (w * 2, h)); out.paste(a, (0, 0)); out.paste(b, (w, 0))
    else:
        out = Image.new('RGB', (w, h * 2)); out.paste(a, (0, 0)); out.paste(b, (0, h))
    return out


def reflection(w, h):
    top = scene(w, h, 7)
    out = Image.new('RGB', (w, h * 2))
    out.paste(top, (0, 0)); out.paste(top.transpose(Image.FLIP_TOP_BOTTOM), (0, h))
    return out


def mirrored(w, h):
    left = scene(w, h, 11)
    out = Image.new('RGB', (w * 2, h))
    out.paste(left, (0, 0)); out.paste(left.transpose(Image.FLIP_LEFT_RIGHT), (w, 0))
    return out


CASES = [
    ('SBS, 1:1 eyes, 3% parallax',          pair(960, 960, 28, 'sbs'),   'SideBySide'),
    ('SBS, 16:9 eyes, 2% parallax',         pair(960, 540, 20, 'sbs'),   'SideBySide'),
    ('SBS, 6% parallax',                    pair(960, 960, 58, 'sbs'),   'SideBySide'),
    ('SBS, extreme 10% parallax',           pair(960, 960, 96, 'sbs'),   'SideBySide'),
    ('SBS, smooth content, 6% parallax',    pair(960, 960, 58, 'sbs', maker=smooth), 'SideBySide'),
    ('SBS, duplicated mono (0 parallax)',   pair(960, 960, 0,  'sbs'),   'SideBySide'),
    ('Over-under, 1:1 eyes, 3% parallax',   pair(960, 960, 28, 'ou'),    'OverUnder'),
    ('Over-under, 16:9 eyes (16:18 frame)', pair(960, 540, 20, 'ou'),    'OverUnder'),
    ('Over-under, 10% parallax',            pair(960, 960, 96, 'ou'),    'OverUnder'),
    ('Over-under, smooth, 6% parallax',     pair(960, 960, 58, 'ou', maker=smooth),  'OverUnder'),
    ('Flat mono photo, 16:9',               scene(1920, 1080),           'None'),
    ('Flat mono photo, 2:1 panorama',       scene(1920, 960),            'None'),
    ('Flat mono portrait 3:4',              scene(960, 1280),            'None'),
    ('Smooth mono 2:1 panorama',            smooth(1920, 960),           'None'),
    ('Two unrelated scenes, packed SBS',    unrelated(960, 960, 'sbs'),  'None'),
    ('Two unrelated scenes, packed OU',     unrelated(960, 960, 'ou'),   'None'),
    ('Landscape + water reflection (OU)',   reflection(960, 540),        'None'),
    ('Mirrored image packed SBS',           mirrored(960, 960),          'None'),
    ('Uniform grey (no contrast)',          Image.new('RGB', (1920, 960), (128, 128, 128)), 'None'),
]

for path in sys.argv[1:]:
    CASES.append((path.split('\\')[-1].split('/')[-1], Image.open(path), '?'))

print('%-40s %-11s %-11s %8s %7s %7s' % ('case', 'expected', 'detected', 'contrast', 'sbs', 'ou'))
print('-' * 90)
failed = 0
worst_true, best_false = 0.0, 9.9
for label, img, expected in CASES:
    got, contrast, sbs, ou = detect(img)
    ok = expected == '?' or got == expected
    failed += not ok
    if expected == 'SideBySide':
        worst_true = max(worst_true, sbs)
    elif expected == 'OverUnder':
        worst_true = max(worst_true, ou)
    elif expected == 'None' and contrast >= MIN_CONTRAST:
        best_false = min(best_false, min(sbs, ou))
    print('%-40s %-11s %-11s %8.1f %7.3f %7.3f %s'
          % (label[:40], expected, got, contrast, sbs, ou, '' if ok else '  <-- FAIL'))

print()
print('worst true pair scored %.3f; best non-pair scored %.3f; threshold %.2f sits in the gap'
      % (worst_true, best_false, THRESHOLD))
print('all cases correct' if not failed else '%d case(s) failed' % failed)
sys.exit(1 if failed else 0)
