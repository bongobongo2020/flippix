import json
import copy

# Read the original workflow
with open(r'c:\Users\x2\Documents\GitHub\flippix-prompt-image\workflow\Wan2.2+ZIT-sub-svi-API.json', 'r', encoding='utf-8') as f:
    workflow = json.load(f)

# Define the sections to create
# Key change: We will NOT chain the overlap nodes deeply to avoid recursion
sections_to_create = [
    {'new_id': '102', 'template_id': '92', 'prev_id': '92'},
    {'new_id': '112', 'template_id': '92', 'prev_id': '102'},
    {'new_id': '122', 'template_id': '92', 'prev_id': '112'},
    {'new_id': '132', 'template_id': '92', 'prev_id': '122'},
    {'new_id': '142', 'template_id': '92', 'prev_id': '132'},
    {'new_id': '152', 'template_id': '92', 'prev_id': '142'},
]

def get_section_nodes(section_id):
    """Get all node IDs for a given section"""
    return {
        'positive_prompt': f'{section_id}:13',
        'negative_prompt': f'{section_id}:12',
        'high_lora': f'{section_id}:45',
        'low_lora': f'{section_id}:46',
        'high_no_lora': f'{section_id}:44',
        'ksampler1': f'{section_id}:43',
        'ksampler2': f'{section_id}:7',
        'ksampler3': f'{section_id}:8',
        'svi_pro': f'{section_id}:41',
        'vae_decode': f'{section_id}:9',
        'video_combine': f'{section_id}:48:52',
        'video_combine_prune': f'{section_id}:48:51',
    }

# Get template from section 92
template_nodes = get_section_nodes('92')

# Track the last accumulated batch (from overlap nodes)
accumulated_batches = ['53:9', '82:47', '90:47', '92:47']  # Already exist

for i, section_info in enumerate(sections_to_create):
    new_id = section_info['new_id']
    template_id = section_info['template_id']
    prev_id = section_info['prev_id']

    new_nodes = get_section_nodes(new_id)

    # Clone each node from template section (excluding overlap nodes)
    for node_key, template_node_key in template_nodes.items():
        if template_node_key in workflow:
            # Create a deep copy of the node
            new_node = copy.deepcopy(workflow[template_node_key])

            # Update title to include section number
            if '_meta' in new_node and 'title' in new_node['_meta']:
                new_node['_meta']['title'] = f"{new_node['_meta']['title']} (Section {new_id})"

            # Update inputs that reference nodes within the section
            if 'inputs' in new_node:
                for input_key, input_value in new_node['inputs'].items():
                    if isinstance(input_value, list) and len(input_value) > 0:
                        ref_node = input_value[0]
                        if isinstance(ref_node, str) and ref_node.startswith(f'{template_id}:'):
                            # Replace the reference
                            if ref_node in template_nodes.values():
                                for key, val in template_nodes.items():
                                    if val == ref_node:
                                        new_node['inputs'][input_key][0] = new_nodes[key]
                                        break

            workflow[new_nodes[node_key]] = new_node

    # Update WanImageToVideoSVIPro to use prev_samples from previous section's ksampler3
    svi_pro_node = workflow[new_nodes['svi_pro']]
    if 'inputs' in svi_pro_node and 'prev_samples' in svi_pro_node['inputs']:
        svi_pro_node['inputs']['prev_samples'] = [f'{prev_id}:8', 0]

    # Update seeds for KSampler nodes to make them unique
    import random
    for ksampler_key in [new_nodes['ksampler1'], new_nodes['ksampler2'], new_nodes['ksampler3']]:
        if ksampler_key in workflow:
            ksampler = workflow[ksampler_key]
            if 'inputs' in ksampler and 'noise_seed' in ksampler['inputs']:
                ksampler['inputs']['noise_seed'] = random.randint(100000000000000, 999999999999999)

    # Update video combine prefix - but don't save intermediate videos (save_output = false)
    video_combine = workflow[new_nodes['video_combine']]
    if 'inputs' in video_combine:
        video_combine['inputs']['filename_prefix'] = f"Wan2.2-I2V-sub-svi/section{new_id}"
        video_combine['inputs']['save_output'] = False  # Don't save intermediate videos

    accumulated_batches.append(new_nodes['vae_decode'])

print(f"Created {len(sections_to_create)} new sections (total 10 sections)")
print(f"Total nodes: {len(workflow)}")

# Now create a NEW batch combine node that combines all VAE decode outputs
# This will be a simple ImageBatchMulti with 10 inputs

# We need to build this step by step since ImageBatchMulti only supports 2 inputs at a time
# We'll create a tree structure that doesn't recurse too deeply

# First level: combine pairs
batch_combines = {}
pairs = [
    ('53:9', '82:9', 'b1'),
    ('90:9', '92:9', 'b2'),
    ('102:9', '112:9', 'b3'),
    ('122:9', '132:9', 'b4'),
    ('142:9', '152:9', 'b5'),
]

next_id = 1000
for img1, img2, name in pairs:
    node_id = str(next_id)
    next_id += 1
    workflow[node_id] = {
        "inputs": {
            "inputcount": 2,
            "Update inputs": None,
            "image_1": [img1, 0],
            "image_2": [img2, 0]
        },
        "class_type": "ImageBatchMulti",
        "_meta": {"title": f"Combine {name}"}
    }
    batch_combines[name] = [node_id, 0]

# Second level: combine the pairs
second_level = [
    (batch_combines['b1'], batch_combines['b2'], 'b12'),
    (batch_combines['b3'], batch_combines['b4'], 'b34'),
]

for img1, img2, name in second_level:
    node_id = str(next_id)
    next_id += 1
    workflow[node_id] = {
        "inputs": {
            "inputcount": 2,
            "Update inputs": None,
            "image_1": img1,
            "image_2": img2
        },
        "class_type": "ImageBatchMulti",
        "_meta": {"title": f"Combine {name}"}
    }
    batch_combines[name] = [node_id, 0]

# Third level: combine the second level with remaining
third_level = [
    (batch_combines['b12'], batch_combines['b34'], 'b1234'),
]

for img1, img2, name in third_level:
    node_id = str(next_id)
    next_id += 1
    workflow[node_id] = {
        "inputs": {
            "inputcount": 2,
            "Update inputs": None,
            "image_1": img1,
            "image_2": img2
        },
        "class_type": "ImageBatchMulti",
        "_meta": {"title": f"Combine {name}"}
    }
    batch_combines[name] = [node_id, 0]

# Fourth level: add the last section
fourth_level = [
    (batch_combines['b1234'], batch_combines['b5'], 'final'),
]

for img1, img2, name in fourth_level:
    node_id = str(next_id)
    next_id += 1
    workflow[node_id] = {
        "inputs": {
            "inputcount": 2,
            "Update inputs": None,
            "image_1": img1,
            "image_2": img2
        },
        "class_type": "ImageBatchMulti",
        "_meta": {"title": f"Combine {name}"}
    }
    batch_combines[name] = [node_id, 0]

# Update the final output node to use our combined batch
workflow['86:52']['inputs']['images'] = batch_combines['final']
workflow['86:52']['inputs']['filename_prefix'] = 'Wan2.2-I2V-sub-svi/final'

print(f"Created batch combination tree to avoid deep recursion")
print(f"Total nodes after combining: {len(workflow)}")

# Save the expanded workflow
output_path = r'c:\Users\x2\Documents\GitHub\flippix-prompt-image\workflow\Wan2.2+ZIT-sub-svi-API-10prompts.json'
with open(output_path, 'w', encoding='utf-8') as f:
    json.dump(workflow, f, indent=2, ensure_ascii=False)

print(f"Saved expanded workflow to: {output_path}")
print(f"NOTE: This version does NOT use overlap nodes for sections 5-10 to avoid recursion issues")
print(f"Each section uses prev_samples from the previous section for continuity")
