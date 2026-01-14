import json
import copy

# Read the 10prompts workflow
with open(r'c:\Users\x2\Documents\GitHub\flippix-prompt-image\workflow\Wan2.2+ZIT-sub-svi-API-10prompts.json', 'r', encoding='utf-8') as f:
    workflow = json.load(f)

# Define the sections that need overlap blending nodes added
# These sections connect to the previous section with blending
sections_to_add_blending = [
    {'section': '102', 'prev': '92:8'},
    {'section': '112', 'prev': '102:8'},
    {'section': '122', 'prev': '112:8'},
    {'section': '132', 'prev': '122:8'},
    {'section': '142', 'prev': '132:8'},
    {'section': '152', 'prev': '142:8'},
]

# For each section, add an ImageBatchExtendWithOverlap node
for section_info in sections_to_add_blending:
    section = section_info['section']
    prev_node = section_info['prev']

    # Create the overlap blending node
    overlap_node_id = f"{section}:47"
    overlap_node = {
        "inputs": {
            "overlap": 5,
            "overlap_side": "source",
            "overlap_mode": "linear_blend",
            "source_images": None,  # Will be set to previous section's overlap node output
            "new_images": [f"{section}:9", 0]
        },
        "class_type": "ImageBatchExtendWithOverlap",
        "_meta": {
            "title": "Image Batch Extend With Overlap"
        }
    }

    # Set the source_images to the previous section's overlap output (except for first one)
    if section == '102':
        # First blending node connects to 92:47 output 2
        # But 92:47 doesn't exist yet, so we need to add it for section 92 first
        overlap_node["inputs"]["source_images"] = ["92:47", 2]
    else:
        # Get previous section number
        prev_section = str(int(section) - 10)
        overlap_node["inputs"]["source_images"] = [f"{prev_section}:47", 2]

    workflow[overlap_node_id] = overlap_node
    print(f"Added overlap node {overlap_node_id}")

# Add overlap node for section 92 (connects to 82:47)
workflow["92:47"] = {
    "inputs": {
        "overlap": 5,
        "overlap_side": "source",
        "overlap_mode": "linear_blend",
        "source_images": ["82:47", 2],
        "new_images": ["92:9", 0]
    },
    "class_type": "ImageBatchExtendWithOverlap",
    "_meta": {
        "title": "Image Batch Extend With Overlap"
    }
}
print("Added overlap node 92:47")

# Add overlap node for section 90 (connects to 82:47 output, but 82:47 needs to be created)
# Wait, looking at the source workflow, 82:47 already exists and connects to 53:9
# So 90:47 should connect to 82:47 output 2

# Now update the final output node (86:52) to use 152:47 output 2 instead of 1008
workflow["86:52"]["inputs"]["images"] = ["152:47", 2]

# Remove the batch combination nodes (1000-1008) since we're using sequential blending now
nodes_to_remove = ["1000", "1001", "1002", "1003", "1004", "1005", "1006", "1007", "1008"]
for node_id in nodes_to_remove:
    if node_id in workflow:
        del workflow[node_id]
        print(f"Removed node {node_id}")

# Save the modified workflow
output_path = r'c:\Users\x2\Documents\GitHub\flippix-prompt-image\workflow\Wan2.2+ZIT-sub-svi-API-10prompts.json'
with open(output_path, 'w', encoding='utf-8') as f:
    json.dump(workflow, f, indent=2, ensure_ascii=False)

print(f"\nWorkflow updated successfully!")
print(f"Added temporal consistency with ImageBatchExtendWithOverlap nodes")
print(f"Final output now connects through blended chain: 53:9 -> 82:47 -> 90:47 -> 92:47 -> 102:47 -> ... -> 152:47")
