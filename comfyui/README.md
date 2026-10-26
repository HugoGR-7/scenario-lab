# ComfyUI workflows: suggestions and clips

The two steps of Scenario Lab that need a GPU (graphics processor). They ran on a local PC with ComfyUI to make the
demo data in `demo/`. They do **not** run on the hackathon machine: the plug-in reads their output as files.

| Workflow | Input | Output | Models |
|---|---|---|---|
| `stage_a_scenarios_from_snapshot.json` | The snapshot from page ① (`C:\ScenarioLab\snapshots\*.png`) | `<camera>_scenarios.json`: 8 suggested scenarios | Qwen3-VL 8B |
| `stage_b_video_from_scenario.json` | The same snapshot + one scenario title | A 10-second video that starts from the snapshot | Qwen3-VL 8B (prompt writer), LTX-2.5 22B distilled (image-to-video) |

```
snapshot.png ──▶ Stage A ──▶ <camera>_scenarios.json ──▶ plug-in page ①: tick / add own ──▶ <camera>_scenarios_user.json
                                                                                                   │ one title at a time
snapshot.png ─────────────────────────────────────────────────────────────────────────▶ Stage B ◀──┘
                                                                                           │
                                                   video.mp4 ──▶ scripts/convert_clips.py ──▶ Install-ScenarioClips.ps1
```

## Requirements

- A recent ComfyUI with the core nodes `TextGenerate` (text generation with Qwen3-VL) and the LTX-2.5 nodes; Stage B
  also uses nodes from **ComfyUI-LTXVideo** (Lightricks). Both workflows use **comfyui-itools** for its text preview
  node. Install missing node packs with ComfyUI Manager (*Install Missing Custom Nodes*).
- Models (folder under `ComfyUI/models/`). Each workflow also lists them in a *Model Links* note:
  - `text_encoders/`: Qwen3-VL 8B ([Comfy-Org/Qwen3-VL](https://huggingface.co/Comfy-Org/Qwen3-VL)),
    `gemma4-12b-with-proj-ltx-2.5` and `gemma4_e2b_it` (LTX-2.5 text encoder and prompt enhancer)
  - `diffusion_models/`: `ltx-2.5-22b-distilled-transformer` ([Lightricks/LTX-2.5](https://huggingface.co/Lightricks/LTX-2.5);
    accept the licence on Hugging Face first)
  - `vae/`: `ltx-2.5-audio-vae` and `ltx-2.5-video-vae-conv` (lower memory) or `ltx-2.5-video-vae` (best quality)
- The demo used int8 versions of the LTX transformer and the Gemma 12B encoder to fit a 16 GB GPU. Pick the variant
  your GPU can hold in the *Load LTX* and *Load CLIP* nodes.

## Stage A: scenarios from a snapshot

1. Load the snapshot in **Load Image**.
2. Run. Qwen3-VL works in two passes:
   1. **Scene inventory:** what is really in the view (surfaces, objects, whether people or vehicles can be there,
      lighting), without positions or paths.
   2. **Scenarios:** 8 events that could plausibly happen there, using only elements from the inventory. Each one must
      be visible without sound and end in a clear visible result. At most 2 people or vehicles. The categories are
      mixed (vandalism, intrusion/theft, accident, safety hazard, suspicious behaviour), plus **one harmless
      look-alike** that mirrors another event with innocent intent. That one is the false-alarm test.
3. Copy the JSON from the output preview and save it as `C:\ScenarioLab\suggestions\<camera>_scenarios.json` on the
   XProtect machine (for the demo: `demo/suggestions/vandalism_scenarios.json`).

Format: `[{"title": "...", "category": "...", "where": "..."}, ...]`. The plug-in marks any category containing
"harmless" as *should NOT trigger*.

## Stage B: a clip from one scenario

1. Load the same snapshot in **Load Image**: it becomes the first frame, so the clip shows exactly that camera view.
2. Write the scenario in the **Prompt** node as `Title: <scenario title>`, for example
   `Title: A man is cleaning the windows.`
3. Run. Qwen3-VL rewrites the title into a 2–3 sentence prompt for the video model. It keeps the action inside 10
   seconds of real time (long actions are shown starting, not finished) and always ends with "The camera is a CCTV
   camera, no movement, tilt, panning or zoom." LTX-2.5 then generates 10 seconds at 24 fps, 960 × 544.
4. The video is saved in ComfyUI's `output` folder.

Video models still drift: the camera may move slightly, or the action may not happen. Generate a few takes per
scenario and keep the best one.

## From video to XProtect camera

Name each clip after its scenario ID from `<camera>_scenarios_user.json`, then convert and install it (main README,
"Your own scenarios and clips"):

```bash
python3 scripts/convert_clips.py rock.mp4=vandalism_s1_rock_window cleaning.mp4=vandalism_s6_cleaning_window \
                                 graffiti.mp4=vandalism_u1_graffiti -o my_clips
```

The demo's three clips (`demo/stablefps`) were made this way. The cleaning clip is suggestion #6, *A worker is cleaning
the wall with a brush*, generated as a man wiping the window with a cloth. The graffiti clip is the user's own
scenario.
