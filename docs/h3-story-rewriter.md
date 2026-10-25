# H3 Video Editor — Story mode with the H3 rewriter and location plates

Story mode (🎬 H3 Video Editor → STORY MODE) turns a `story.txt` into a finished film:
analyze → cast photos → location plates → clip prompts → render → upscale → join.
This branch adds three things to that chain.

| Step | What it does | Where |
|---|---|---|
| 👤 Looks & voices | One LLM pass per story decides each character's face, hair, age, build and voice. The look goes into the auto-generated cast photo next to the locked wardrobe. The voice is written into every clip where that character speaks. | `H3VideoEditorViewModel.StoryRewriter.cs` → `DeriveStoryLooksAsync` |
| 🏞️ Location plates | One empty picture for each place in the continuity plan (same place, hour and light). The LLM writes it from the I2V tab's plate brief (`prompts/prompt2json/h3-location-plate.md`), and Qwen Image 2512 renders it. Each clip sends its place's plate after the cast pictures, declared as its own `<Subject N>`. At most 6 plates per story. | `WriteLocationPlatePromptsAsync`, `RenderLocationPlatesAsync`, `Render.cs` |
| ✍ H3 rewriter | The LLM writes each beat to the official MiniMax H3 full-reference format (`prompts/h3-rewriter/flippix_beat_writer.md`). It writes summary, description, soundscape and score; the app still writes `subject_definitions` and `retention_analysis`. Each reply is checked (shot and cut-time syntax, one shot for chained clips, tags, `<d>` language tags and speaker IDs, spoken lines). A reply that fails is sent back once with the reasons. A clip that still fails uses the template. | `Services/H3RewriterBeatWriter.cs` |

The Story panel has two new controls: **Prompts** (`h3-rewriter` / `template`) and **🏞️ Plates**.
Both are saved in settings and are on by default.

## Running it with llama.cpp and the H3 rewriter LoRA

1. Start llama-server with Qwen3.6-27B and the rewriter LoRA. The H3Llama installer's **H3 Writer** shortcut does this:
   ```
   llama-server -m Qwen_Qwen3.6-27B-IQ3_XXS.gguf --lora qwen36-27b-rewriter-q8_0.gguf \
     -ngl 999 -c 16384 -fa on -ctk q8_0 -ctv q8_0 --jinja --host 0.0.0.0 --port 8080
   ```
2. In FlipPix **Settings → LLM server**, set the URL to that machine, e.g. `http://192.168.1.50:8080`.
3. Import the story, pick **Prompts: h3-rewriter**, keep **🏞️ Plates** ticked, and click **Analyze**.

The editor turns the LoRA **off** for the story analysis (cast, wardrobe, looks, beats, plate prompts). It turns it **on** only while it writes clip prompts, through llama-server's `/lora-adapters` endpoint. Afterwards it puts the scales back as it found them. With LM Studio, Ollama or any server that has no `/lora-adapters`, this step is skipped and the same model writes everything.

**VRAM.** Run llama-server on a different GPU or machine from ComfyUI, or stop it before generation starts. Qwen3.6-27B and H3 do not fit on one 16 GB card together.

## Picture numbering

Cast pictures are `<Picture 1>`–`<Picture 4>` (the Picture slots, filled in order). The plate becomes the next number: with two characters it is `<Picture 3>` / `<Subject 3>`. If you change the Picture slots after **Populate**, the log warns that the plate number no longer matches. Re-run Populate to fix it.
