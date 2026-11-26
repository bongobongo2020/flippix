"""🥷 Martial-pipeline bake-off: the same two beats, opening image and character sheets as bench.py, rendered with
the Martial tab's own MartialGraph.Build (fflf-bench.exe martial). The sheets go in as Martial references, labelled.
Results land in results/MT_*/ in bench.py's layout, so score.py scores them with the rest.
Usage: python martial.py [config ...]"""
import json, os, re, subprocess, sys, time
from bench import EXE, SETTINGS, RESULTS, SEEDS, upload, run_graph, files_of, download

HERE = os.path.dirname(os.path.abspath(__file__))
# The tab's own picture roles (H3MartialViewModel.PictureRoles).
FIRST = ["is the first frame of this shot: the video starts exactly on it and the action moves on from there.",
         "is the first frame of this shot — the last moment of the shot before: the video starts exactly on it and the action carries straight on."]
IDENTITY = "shows how the characters look; keep their faces, hair, build and clothing identical to it throughout."
VIDEO_EXT = ('.mp4', '.webm', '.mov')


def main():
    cfgs = json.load(open(os.path.join(HERE, 'martial_configs.json'), encoding='utf-8'))
    st = json.load(open(SETTINGS, encoding='utf-8-sig'))
    server = st['BaseUrl'].rstrip('/')
    h = st['H3Fflf']
    beats = [p.strip() for p in h['BeatsText'].replace('\r', '').split('\n\n') if p.strip()][:2]
    seconds = [float(re.search(r'\[(\d+(?:\.\d+)?)\s*s\]', b).group(1)) for b in beats]
    beats = [re.sub(r'^\d+\.\s*(\[[^\]]*\]\s*)?', '', b) for b in beats]
    opening = upload(server, h['InitialImagePath'], 'fflfbench_opening.png')
    sheets = [s for s in h.get('CharacterSheetList', []) if os.path.exists(s['Path'])]
    sheet_names = [upload(server, s['Path'], f"fflfbench_sheet_{s['Letter']}.png") for s in sheets]
    sheet_roles = [f"is the character sheet of {s['Label']}: that one person from every side; keep it exactly as shown throughout the video."
                   for s in sheets]
    want = set(sys.argv[1:])
    for cfg in cfgs['configs']:
        name = cfg['name']
        if want and name not in want:
            continue
        out = os.path.join(RESULTS, name)
        meta_p = os.path.join(out, 'meta.json')
        if not want and os.path.exists(meta_p) and json.load(open(meta_p)).get('ok'):
            continue
        os.makedirs(out, exist_ok=True)
        r = dict(cfgs['base'], **{k: v for k, v in cfg.items() if k not in ('name', 'note', 'seeds')})
        triggers = r.pop('triggers', '')
        meta = {'name': name, 'note': cfg.get('note', ''), 'render': r, 'pipeline': 'martial', 'beats': [], 'ok': False}
        start = opening
        print(f'[{time.strftime("%H:%M:%S")}] {name}', flush=True)
        try:
            for i, (beat, sec) in enumerate(zip(beats, seconds)):
                # As the tab: the opening image rides along as the identity picture from beat 2 (Keep initial reference).
                pictures = [start] + ([opening] if i > 0 else []) + sheet_names
                roles = [FIRST[min(i, 1)]] + ([IDENTITY] if i > 0 else []) + sheet_roles
                render = dict(r, pictures=pictures, firstFrame=start, seed=cfg.get('seeds', SEEDS)[i],
                              outputPrefix=f'video/fflf-bench/{name}/beat_{i + 1:02d}')
                req = {'beat': beat, 'seconds': sec, 'style': h.get('StyleText', ''), 'setting': h.get('SettingText', ''),
                       'wardrobe': h.get('WardrobeText', ''), 'triggers': triggers, 'roles': roles, 'render': render}
                rp, gp = os.path.join(out, f'req_{i + 1}.json'), os.path.join(out, f'graph_{i + 1}.json')
                json.dump(req, open(rp, 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
                info = json.loads(subprocess.run([EXE, 'martial', rp, gp], check=True, capture_output=True, text=True).stdout)
                outputs, exec_s, wall_s = run_graph(server, json.load(open(gp, encoding='utf-8')))
                vids = [f for f in files_of(outputs, info['video']) if f['filename'].lower().endswith(VIDEO_EXT)] or \
                       [f for n in outputs for f in files_of(outputs, n) if f['filename'].lower().endswith(VIDEO_EXT)]
                lasts = files_of(outputs, info['last'])
                video, last = os.path.join(out, f'beat_{i + 1:02d}.mp4'), os.path.join(out, f'beat_{i + 1:02d}_last.png')
                download(server, vids[-1], video)
                download(server, lasts[-1], last)
                meta['beats'].append({'seconds': sec, 'exec_s': round(exec_s, 1), 'wall_s': round(wall_s, 1)})
                print(f'    beat {i + 1}: {exec_s:.0f} s', flush=True)
                start = upload(server, last, f'fflfbench_{name}_last{i + 1}.png')
            meta['ok'] = True
        except Exception as e:
            meta['error'] = str(e)
            print('    FAILED', str(e)[:800], flush=True)
        json.dump(meta, open(meta_p, 'w'), indent=1)


if __name__ == '__main__':
    main()
