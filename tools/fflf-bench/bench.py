"""⛓ FFLF model / LoRA bake-off: renders the same two-beat story with each config in configs.json.

Each config renders beat 1 from the opening image and beat 2 from beat 1's last frame — exactly the FFLF
chain — with the graphs built by the app's own FflfGraph.Build (fflf-bench.exe). Results land in
results/<config>/ with ComfyUI's own execution timings. Scoring is score.py.

Usage:  python bench.py [config-name ...]       (no names: every config not yet rendered)
"""

import json
import os
import subprocess
import sys
import time
import urllib.parse
import urllib.request
import uuid

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, '..', '..'))
EXE = os.path.join(HERE, 'bin', 'Release', 'net10.0-windows', 'fflf-bench.exe')
TEMPLATE = os.path.join(REPO, 'workflow', 'video', 'h3-minimax', 'h3-erosmax-2pass.json')
RESULTS = os.path.join(HERE, 'results')
SETTINGS = os.path.expandvars(r'%APPDATA%\FlipPix\settings.json')

# The two beats of the open story's first two paragraphs, and their lengths.
SEEDS = [424242, 424243]


def http(url, data=None, headers=None, timeout=120):
    req = urllib.request.Request(url, data=data, headers=headers or {})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return r.read()


def upload(server, path, name):
    boundary = uuid.uuid4().hex
    body = (f'--{boundary}\r\nContent-Disposition: form-data; name="image"; filename="{name}"\r\n'
            'Content-Type: image/png\r\n\r\n').encode() + open(path, 'rb').read() + \
           f'\r\n--{boundary}\r\nContent-Disposition: form-data; name="overwrite"\r\n\r\ntrue\r\n--{boundary}--\r\n'.encode()
    out = json.loads(http(f'{server}/upload/image', body, {'Content-Type': f'multipart/form-data; boundary={boundary}'}))
    return (out.get('subfolder') + '/' if out.get('subfolder') else '') + out['name']


def files_of(outputs, node):
    found = []
    for v in (outputs.get(node) or {}).values():
        if isinstance(v, list):
            found += [f for f in v if isinstance(f, dict) and 'filename' in f]
    return found


def download(server, f, dest):
    q = urllib.parse.urlencode({'filename': f['filename'], 'subfolder': f.get('subfolder', ''), 'type': f.get('type', 'output')})
    open(dest, 'wb').write(http(f'{server}/view?{q}', timeout=600))


def run_graph(server, graph):
    body = json.dumps({'prompt': graph, 'client_id': 'flippix-fflf-bench'}).encode()
    try:
        pid = json.loads(http(f'{server}/prompt', body, {'Content-Type': 'application/json'}))['prompt_id']
    except urllib.error.HTTPError as e:
        raise RuntimeError(e.read().decode()[:3000])
    t0 = time.time()
    while True:
        time.sleep(3)
        h = json.loads(http(f'{server}/history/{pid}', timeout=60))
        if pid in h and h[pid].get('status', {}).get('completed') is not None:
            e = h[pid]
            if e['status'].get('status_str') != 'success' or not e['status'].get('completed'):
                msgs = [m for m in e['status'].get('messages', []) if m[0] == 'execution_error']
                raise RuntimeError(json.dumps(msgs)[:3000])
            stamps = {m[0]: m[1].get('timestamp') for m in e['status'].get('messages', [])}
            secs = ((stamps.get('execution_success') or 0) - (stamps.get('execution_start') or 0)) / 1000.0
            return e['outputs'], secs, time.time() - t0
        if time.time() - t0 > 3600:
            raise RuntimeError('timed out')


def main():
    cfgs = json.load(open(os.path.join(HERE, 'configs.json'), encoding='utf-8'))
    base = cfgs['base']
    st = json.load(open(SETTINGS, encoding='utf-8-sig'))
    server = st['BaseUrl'].rstrip('/')
    h = st['H3Fflf']
    beats = [p.strip() for p in h['BeatsText'].replace('\r', '').split('\n\n') if p.strip()][:2]
    import re
    seconds = [float(re.search(r'\[(\d+(?:\.\d+)?)\s*s\]', b).group(1)) for b in beats]
    beats = [re.sub(r'^\d+\.\s*', '', b) for b in beats]

    os.makedirs(RESULTS, exist_ok=True)
    opening = upload(server, h['InitialImagePath'], 'fflfbench_opening.png')
    sheets = [s for s in h.get('CharacterSheetList', []) if os.path.exists(s['Path'])]
    sheet_names = [upload(server, s['Path'], f"fflfbench_sheet_{s['Letter']}.png") for s in sheets]
    sheet_labels = [s['Label'] for s in sheets]

    want = set(sys.argv[1:])
    for cfg in cfgs['configs']:
        name = cfg['name']
        if want and name not in want:
            continue
        out_dir = os.path.join(RESULTS, name)
        meta_path = os.path.join(out_dir, 'meta.json')
        if not want and os.path.exists(meta_path) and json.load(open(meta_path)).get('ok'):
            continue
        os.makedirs(out_dir, exist_ok=True)
        render = dict(base, **{k: v for k, v in cfg.items() if k not in ('name', 'note', 'seeds', 'promptPrefix')})
        meta = {'name': name, 'note': cfg.get('note', ''), 'render': render, 'beats': [], 'ok': False}
        start = opening
        print(f'[{time.strftime("%H:%M:%S")}] {name}', flush=True)
        try:
            for i, (beat, sec) in enumerate(zip(beats, seconds)):
                r = dict(render, startImage=start, castImages=sheet_names, seed=cfg.get('seeds', SEEDS)[i],
                         outputPrefix=f'video/fflf-bench/{name}/beat_{i + 1:02d}')
                req = {'template': TEMPLATE, 'beat': beat, 'seconds': sec, 'style': h.get('StyleText', ''),
                       'setting': h.get('SettingText', ''), 'wardrobe': h.get('WardrobeText', ''),
                       'description': h.get('OpeningImageDescription', ''), 'sheetLabels': sheet_labels,
                       'isFirstBeat': i == 0, 'render': r, 'promptPrefix': cfg.get('promptPrefix', '')}
                rp, gp = os.path.join(out_dir, f'req_{i + 1}.json'), os.path.join(out_dir, f'graph_{i + 1}.json')
                json.dump(req, open(rp, 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
                subprocess.run([EXE, rp, gp], check=True, capture_output=True)
                outputs, exec_s, wall_s = run_graph(server, json.load(open(gp, encoding='utf-8')))
                vids = files_of(outputs, '40') or [f for n in outputs for f in files_of(outputs, n)
                                                      if f['filename'].lower().endswith(('.mp4', '.webm', '.mov'))]
                lasts = files_of(outputs, 'fflf_last_save')
                video = os.path.join(out_dir, f'beat_{i + 1:02d}.mp4')
                last = os.path.join(out_dir, f'beat_{i + 1:02d}_last.png')
                download(server, [v for v in vids if v['filename'].lower().endswith(('.mp4', '.webm', '.mov'))][-1], video)
                download(server, lasts[-1], last)
                meta['beats'].append({'seconds': sec, 'exec_s': round(exec_s, 1), 'wall_s': round(wall_s, 1)})
                print(f'    beat {i + 1}: {exec_s:.0f} s', flush=True)
                start = upload(server, last, f'fflfbench_{name}_last{i + 1}.png')
            meta['ok'] = True
        except Exception as e:
            meta['error'] = str(e)
            print('    FAILED', str(e)[:600], flush=True)
        json.dump(meta, open(meta_path, 'w'), indent=1)


if __name__ == '__main__':
    main()
