#!/usr/bin/env python3
"""
Simple script to run WanVideo SCAIL workflow in chunks
"""
import json
import os
import sys
import subprocess
import requests
from pathlib import Path

def load_workflow(workflow_path):
    """Load the base workflow"""
    with open(workflow_path, 'r') as f:
        return json.load(f)

def modify_workflow_for_chunk(workflow, video_path, image_path, skip_frames, chunk_id):
    """Modify workflow for a specific chunk"""
    # Create a copy to avoid modifying the original
    chunk_workflow = json.loads(json.dumps(workflow))

    # Update video path and skip frames
    chunk_workflow["130"]["inputs"]["video"] = video_path
    chunk_workflow["130"]["inputs"]["skip_first_frames"] = skip_frames

    # Update image path
    chunk_workflow["106"]["inputs"]["image"] = image_path

    # Update filename for output
    filename_prefix = f"WanVideo_SCAIL_chunk_{skip_frames}"
    chunk_workflow["139"]["inputs"]["filename_prefix"] = filename_prefix

    return chunk_workflow

def submit_to_comfyui(workflow, server_url="http://127.0.0.1:8188"):
    """Submit workflow to ComfyUI"""
    prompt_id = subprocess.check_output([
        "curl", "-X", "POST", f"{server_url}/prompt",
        "-H", "Content-Type: application/json",
        "-d", json.dumps({"prompt": workflow})
    ], text=True)

    return json.loads(prompt_id)["prompt_id"]

def main():
    # Configuration
    workflow_path = "publish/workflow/wanvideo_SCAIL_API_final.json"
    video_path = input("Enter path to reference video: ").strip('"')
    image_path = input("Enter path to reference image: ").strip('"')
    total_frames = int(input("Enter total number of frames in video: "))

    chunk_size = 81
    total_chunks = (total_frames + chunk_size - 1) // chunk_size

    print(f"\nProcessing {total_chunks} chunks of {chunk_size} frames each...")

    # Load base workflow
    workflow = load_workflow(workflow_path)

    # Process each chunk
    for chunk_id in range(total_chunks):
        skip_frames = chunk_id * chunk_size
        print(f"\n--- Processing Chunk {chunk_id + 1}/{total_chunks} ---")
        print(f"Frames: {skip_frames} to {min(skip_frames + chunk_size - 1, total_frames - 1)}")

        # Modify workflow for this chunk
        chunk_workflow = modify_workflow_for_chunk(
            workflow, video_path, image_path, skip_frames, chunk_id
        )

        # Save chunk workflow for debugging
        chunk_file = f"publish/workflow/chunk_{chunk_id}_workflow.json"
        with open(chunk_file, 'w') as f:
            json.dump(chunk_workflow, f, indent=2)
        print(f"Saved chunk workflow to: {chunk_file}")

        # Submit to ComfyUI
        try:
            prompt_id = submit_to_comfyui(chunk_workflow)
            print(f"Submitted to ComfyUI with prompt_id: {prompt_id}")
            print(f"Check ComfyUI web interface for progress")
        except Exception as e:
            print(f"Error submitting to ComfyUI: {e}")
            print("Make sure ComfyUI is running at http://127.0.0.1:8188")

        # Wait for user before next chunk
        if chunk_id < total_chunks - 1:
            input("\nPress Enter to continue to next chunk...")

if __name__ == "__main__":
    main()