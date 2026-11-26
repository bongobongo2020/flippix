"""🧾 Character-sheet bake-off: draws a sheet for every person in each test image (sheet_tests.json) with each
image-edit engine, built by the app's own FflfSheetGraph (fflf-bench.exe sheet). Results: results/sheets/.
Scoring is sheet_score.py.   Usage: python sheets.py [engine ...]"""
import json, os, subprocess, sys, time
from bench import EXE, REPO, SETTINGS, upload, run_graph, files_of, download

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, 'results', 'sheets')


def main():
    t = json.load(open(os.path.join(HERE, 'sheet_tests.json'), encoding='utf-8'))
    st = json.load(open(SETTINGS, encoding='utf-8-sig'))
    server = st['BaseUrl'].rstrip('/')
    os.makedirs(OUT, exist_ok=True)
    photos = {}
    for im in t['images']:
        if im['description'] == 'FROM_SETTINGS':
            im['description'] = st['H3Fflf']['OpeningImageDescription']
        photos[im['name']] = upload(server, im['path'], f"sheetbench_{im['name']}.png")
    engines = sys.argv[1:] or t['engines']
    # One engine at a time, so each model loads once.
    for spec in engines:
        # "qwen21@1536": an engine with Qwen 2.1's reference resolution set.
        engine, _, res = spec.partition('@')
        for im in t['images']:
            for letter in im['faceOrder']:
                for seed in t['seeds']:
                    name = f"{engine}{'r' + res if res else ''}_{im['name']}_{letter}_{seed}"
                    png, meta_p = os.path.join(OUT, name + '.png'), os.path.join(OUT, name + '.json')
                    if os.path.exists(meta_p) and json.load(open(meta_p)).get('ok'):
                        continue
                    req = {'engine': engine, 'repo': REPO, **({'resolution': int(res)} if res else {}), 'photo': photos[im['name']],
                           'description': im['description'], 'letter': letter, 'seed': seed,
                           'prefix': f'fflf-bench/sheets/{name}'}
                    rp, gp = os.path.join(OUT, name + '_req.json'), os.path.join(OUT, name + '_graph.json')
                    json.dump(req, open(rp, 'w', encoding='utf-8'), ensure_ascii=False)
                    info = json.loads(subprocess.run([EXE, 'sheet', rp, gp], check=True, capture_output=True, text=True).stdout)
                    meta = {'name': name, 'engine': engine + ('r' + res if res else ''), 'image': im['name'], 'letter': letter, 'seed': seed,
                            'prompt': info['prompt'], 'ok': False}
                    try:
                        outputs, exec_s, _ = run_graph(server, json.load(open(gp, encoding='utf-8')))
                        f = files_of(outputs, info['saveNode']) or [x for n in outputs for x in files_of(outputs, n)]
                        download(server, f[-1], png)
                        meta.update(ok=True, exec_s=round(exec_s, 1))
                        print(f'[{time.strftime("%H:%M:%S")}] {name}: {exec_s:.0f} s', flush=True)
                    except Exception as e:
                        meta['error'] = str(e)[:2000]
                        print(f'{name} FAILED {str(e)[:400]}', flush=True)
                    json.dump(meta, open(meta_p, 'w'), indent=1)


if __name__ == '__main__':
    main()
