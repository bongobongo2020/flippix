"""Scores the 🧾 sheet bake-off (results/sheets/): ArcFace likeness of every face on each sheet to that person's
face in the original photo, and whether the sheet drew someone else.  Usage: <rope python> sheet_score.py"""
import glob, json, os
import cv2
import onnxruntime as ort
ort.set_default_logger_severity(3)
import numpy as np
from score import Scrfd, ArcFace, MODELS

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, 'results', 'sheets')


def imread(p):
    return cv2.imdecode(np.fromfile(p, np.uint8), cv2.IMREAD_COLOR)


def main():
    det = Scrfd(os.path.join(MODELS, 'det_10g.onnx'))
    arc = ArcFace(os.path.join(MODELS, 'w600k_r50.onnx'))
    t = json.load(open(os.path.join(HERE, 'sheet_tests.json'), encoding='utf-8'))

    # Each person's face in the original photo, picked by left-to-right order.
    ref = {}
    for im in t['images']:
        img = imread(im['path'])
        if max(img.shape[:2]) > 2600:
            s = 2600 / max(img.shape[:2]); img = cv2.resize(img, None, fx=s, fy=s, interpolation=cv2.INTER_AREA)
        det.size = 640
        faces = sorted([f for f in det.detect(img) if f[1] > 0.6], key=lambda f: -(f[0][2] - f[0][0]))[:len(im['faceOrder'])]
        faces.sort(key=lambda f: f[0][0])
        for letter, i in im['faceOrder'].items():
            ref[(im['name'], letter)] = arc.embed(img, faces[i][2])[0]
        print(im['name'], 'faces', len(faces))

    rows = []
    det.size = 1280
    for meta_p in sorted(glob.glob(os.path.join(OUT, '*.json'))):
        if meta_p.endswith(('_req.json', '_graph.json', 'scores.json')):
            continue
        m = json.load(open(meta_p))
        if not m.get('ok'):
            rows.append({**m, 'faces': 0}); continue
        img = imread(os.path.join(OUT, m['name'] + '.png'))
        target = ref[(m['image'], m['letter'])]
        others = [v for (n, l), v in ref.items() if n == m['image'] and l != m['letter']]
        sims, intruders = [], 0
        for box, sc, kps in det.detect(img, 0.5):
            e = arc.embed(img, kps)[0]
            st = float(np.dot(e, target))
            so = max([float(np.dot(e, o)) for o in others], default=-1)
            if so > st and so > 0.3:
                intruders += 1
            else:
                sims.append(st)
        sims.sort(reverse=True)
        rows.append({**{k: m[k] for k in ('name', 'engine', 'image', 'letter', 'seed', 'exec_s')},
                     'faces': len(sims), 'best': round(sims[0], 3) if sims else 0,
                     'top2': round(float(np.mean(sims[:2])), 3) if sims else 0,
                     'mean': round(float(np.mean(sims)), 3) if sims else 0, 'intruders': intruders,
                     'size': f'{img.shape[1]}x{img.shape[0]}'})
    json.dump(rows, open(os.path.join(OUT, 'scores.json'), 'w'), indent=1)
    t['engines'] += sorted({x['engine'] for x in rows} - set(t['engines']))

    print(f"\n{'engine':8} {'best':>5} {'top2':>5} {'mean':>5} {'faces':>5} {'intr':>4} {'fail':>4} {'sec':>5}")
    for eng in t['engines']:
        r = [x for x in rows if x['engine'] == eng]
        ok = [x for x in r if 'best' in x]
        a = lambda k: np.mean([x[k] for x in ok]) if ok else 0
        print(f"{eng:8} {a('best'):5.3f} {a('top2'):5.3f} {a('mean'):5.3f} {a('faces'):5.1f} "
              f"{sum(x['intruders'] for x in ok):4d} {len(r) - len(ok):4d} {np.median([x['exec_s'] for x in ok]) if ok else 0:5.0f}")
    print('\nper person (best, mean over seeds):')
    keys = sorted({(x['image'], x['letter']) for x in rows})
    print(f"{'person':12}" + ''.join(f'{e:>16}' for e in t['engines']))
    for k in keys:
        line = f'{k[0] + " " + k[1]:12}'
        for eng in t['engines']:
            ok = [x for x in rows if (x['image'], x['letter']) == k and x['engine'] == eng and 'best' in x]
            line += f"{np.mean([x['best'] for x in ok]) if ok else 0:8.3f}/{np.mean([x['mean'] for x in ok]) if ok else 0:.3f}"
        print(line)

    # Contact sheets per test person: one row per engine, seeds side by side.
    for k in keys:
        rws = []
        for eng in t['engines']:
            tiles = []
            for x in sorted([x for x in rows if (x['image'], x['letter']) == k and x['engine'] == eng], key=lambda x: x['seed']):
                p = os.path.join(OUT, x['name'] + '.png')
                im = cv2.resize(imread(p), (896, 384)) if os.path.exists(p) else np.zeros((384, 896, 3), np.uint8)
                cv2.putText(im, f"{eng} {x.get('best', 0):.2f}", (8, 28), cv2.FONT_HERSHEY_SIMPLEX, 0.9, (0, 0, 255), 2)
                tiles.append(im)
            if tiles:
                rws.append(np.hstack(tiles + [np.zeros((384, 896, 3), np.uint8)] * (len(t['seeds']) - len(tiles))))
        cv2.imwrite(os.path.join(OUT, f'compare_{k[0]}_{k[1]}.jpg'), np.vstack(rws), [cv2.IMWRITE_JPEG_QUALITY, 85])


if __name__ == '__main__':
    main()
