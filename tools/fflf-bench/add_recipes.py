r"""Adds the bake-off winners (RESULTS.md) to FlipPix's 📒 Recipes — three on ⛓ FFLF, one on 🥷 Martial — in
%APPDATA%\FlipPix\settings.json. Each tab keeps its own list.
Close FlipPix first — a running app writes its own copy of the settings back over this one.
Re-running replaces recipes of the same name; other recipes are left alone. A .bak is written beside the file."""
import json, os, shutil, datetime

p = os.path.expandvars(r'%APPDATA%\FlipPix\settings.json')
s = json.load(open(p, encoding='utf-8-sig'))
f = s['H3Fflf']
now = datetime.datetime.now().isoformat(timespec='seconds')
TE = 'qwen3vl_32b_minimax_h3-w4a8_convrot.safetensors'
B25 = 'h3-minimax/minimax_h3_hybrid_fl2va_ref2va_b25-49-int8.safetensors'
TAO = 'H3/taomate_h3_3step_comfy.safetensors'


def recipe(name, model, steps, lora='', strength=0.0, extras=(), f=f, tab_json=''):
    # Dials the bake-off didn't vary keep the tab's current values; the tested ones are set as rendered.
    return {
        'Name': name, 'Saved': now, 'DiffusionModel': model, 'TextEncoder': TE,
        'Lora': lora, 'LoraStrength': strength,
        'ExtraLoras': [{'On': True, 'Name': n, 'Strength': v} for n, v in extras],
        'FirstPassSteps': steps, 'BeatSeconds': f.get('BeatSeconds', 5),
        'AspectRatio': '16:9 (Widescreen)', 'FirstPassMegapixels': 0.3, 'FinalMegapixels': 1.2,
        'PreviewOnly': False, 'LockFirstFrame': True,
        'KeepInitialReference': f.get('KeepInitialReference', True), 'MaxFidelityReferences': True,
        'RepairHandoffFace': f.get('RepairHandoffFace', True), 'FaceRepairStrength': f.get('FaceRepairStrength', 1.0),
        'FaceRefine': f.get('FaceRefine', False), 'FaceRefineStrength': f.get('FaceRefineStrength', 0.30),
        'Seed': 424242, 'TabJson': tab_json,
    }


new = [
    recipe('Bake-off: best likeness (10Eros hybrid-act, 6 steps)',
           'h3-minimax/10Eros_Max_8stepTurbo_v1.0_hybrid_activation_learned_HQ.safetensors', 6),
    recipe('Bake-off: fastest (b25 + TaoMate 3-step)', B25, 3, TAO, 1.0),
    recipe('Bake-off: martial (b25 + TaoMate + Bunny weapon 0.45)', B25, 3, TAO, 1.0,
           [('H3/Bunny_weapon_combatV1.safetensors', 0.45)]),
]

# 🥷 Martial: its LoRAs, refine pass and triggers live in the tab's own part of the recipe (TabJson, an
# FflfMartialStory without references or keyframes). Tested in the Martial pipeline: refine 6 steps matched 10 on
# likeness and saved ~90 s a 10 s clip. MaxFidelityReferences is the tab's own setting (off by default).
m = s['H3Martial']
martial_tab = json.dumps({
    'RefineModel': '', 'TextEncoder': TE,
    'Loras': [{'On': True, 'Name': TAO, 'Strength': 1.0, 'OnRefine': True},
              {'On': True, 'Name': 'H3/Bunny_weapon_combatV1.safetensors', 'Strength': 0.45, 'OnRefine': True}],
    'References': [], 'Guides': [], 'Triggers': '', 'Attention': m.get('Attention') or 'comfy kitchen attention',
    'RefineStrength': 0.6, 'RefineSteps': 6, 'RtxUpscale': m.get('RtxUpscale', False), 'RtxScale': m.get('RtxScale', 2.0),
})
martial = recipe('Bake-off: martial (TaoMate + Bunny 0.45, refine 6)', B25, 3, f=m, tab_json=martial_tab)
martial.update(MaxFidelityReferences=m.get('MaxFidelityReferences', False), AspectRatio=m.get('AspectRatio') or '16:9 (Widescreen)')


def put(tab, recipes):
    names = {r['Name'] for r in recipes}
    tab['Recipes'] = [r for r in tab.get('Recipes') or [] if r.get('Name') not in names] + recipes
    return len(tab['Recipes'])


nf, nm = put(f, new), put(m, [martial])
shutil.copy2(p, p + '.bak')
json.dump(s, open(p, 'w', encoding='utf-8'), indent=2, ensure_ascii=False)
print(f'saved {len(new)} FFLF recipes and 1 Martial recipe; FFLF now has {nf}, Martial {nm}')
