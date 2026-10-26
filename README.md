# Scenario Lab

Hafnia Hackathon 2026 project (Milestone Systems × NVIDIA).

**See how your XProtect system reacts to rare situations, on your own cameras, before they happen.**
Scenario Lab is a Smart Client plug-in. It takes a snapshot of one of your cameras through XProtect and proposes
edge cases that fit that view. You tick the ones you care about or add your own, and generated clips of exactly those
situations play as XProtect cameras. You then test one detection question with Hafnia VLM (vision-language model) on
every clip, and see PASS or FAIL per clip, plus real XProtect alarms, before tuning anything on live footage.

## How it works

```
 XProtect camera ──snapshot──▶ scene analysis (VLM) ──▶ suggested scenarios ──▶ user ticks / adds own
                                                                                     │
 Smart Client Alarm Manager ◀── gateway rule ◀── Hafnia VLM ◀── clip cameras ◀── video generation
        (alarm)                (keyword)        (one question,     (StableFPS,      (image-to-video,
                                                 every clip)        one per clip)     snapshot as 1st frame)
```

1. **Snapshot through XProtect.** The plug-in lists the cameras, shows live video, and picks a frame from the
   recordings at full resolution.
2. **Suggested scenarios.** A vision-language model looks at the snapshot and proposes edge cases for that exact
   view, each with a category and where it happens. Harmless look-alikes are included on purpose, because a false alarm
   is also a failure. The user ticks some and adds their own.
3. **Generated clips.** An image-to-video model renders each chosen scenario, with the snapshot as the first frame.
   Each clip plays in XProtect as its own camera (Milestone's StableFPS driver), so to XProtect and Hafnia it is an
   ordinary recorded camera.
4. **Test with Hafnia.** One detection question for every clip, plus the original camera's real recording from the
   snapshot moment, so the question is checked on real footage too. One request per clip. The table shows expected vs. got, PASS or FAIL, and Hafnia's
   answer.
5. **Back into XProtect.** A "detected" answer raises the analytics event *Scenario Lab detection* through a Hafnia
   gateway rule, and the alarm *Scenario Lab: scenario detected* in Smart Client's Alarm Manager.

**What is live and what is prepared.** Steps 1, 4 and 5 run live on the XProtect machine. Steps 2 and 3 need a GPU
(graphics processor) and models the hackathon machine does not have. So this repository ships their output for the
`vandalism` camera: the suggestions (`demo/suggestions`) and three generated clips (`demo/stablefps`), produced with
ComfyUI: Qwen3-VL for the scene analysis, LTX-2.5 for image-to-video. The two workflows are in `comfyui/`. On a real site both would run on a local GPU server,
so customer footage never leaves the site. Any model works as long as it writes the same JSON and clips (see
"Your own scenarios and clips").

## Repository layout

| Folder | Contents |
|---|---|
| `comfyui/` | The two ComfyUI workflows that made the demo data: Stage A (snapshot → scenarios), Stage B (snapshot + scenario → video). Details: `comfyui/README.md` |
| `plugin/` | The C# MIP (Milestone Integration Platform) Smart Client plug-in: the *Scenario Lab* tab. Details: `plugin/README.md` |
| `demo/suggestions/` | `vandalism_scenarios.json`: the 8 scenarios suggested for the `vandalism` camera's empty street |
| `demo/stablefps/` | The 3 generated clips, ready for StableFPS (raw H.264, 832×480, 10 fps, 10 s), and `clips.json` (camera name → file, scenario) |
| `xprotect-setup/` | Scripts that set everything up in XProtect and the Hafnia gateway. Details: `xprotect-setup/README.md` |
| `hafnia/` | How a test becomes an alarm: prompt, keyword instruction, gateway rule, names |
| `scripts/` | `Build-Plugin.ps1` (build + deploy), `Check-BuildEnv.ps1`, `convert_clips.py` (any video → StableFPS clip) |

## Set it up (on the hackathon machine)

Everything below runs on the XProtect machine over Remote Desktop, in **PowerShell as Administrator**. It takes about
15 minutes, most of it waiting for the Recording Server to restart.

**0. Tools.** Git for Windows, and Build Tools for Visual Studio 2022 with the workload ".NET desktop build tools"
and the .NET Framework 4.8 targeting pack. `scripts\Check-BuildEnv.ps1` checks them. MilestonePSTools is already on
the machine.

**1. Get the code**

```powershell
git clone https://github.com/HugoGR-7/scenario-lab C:\src\scenario-lab
cd C:\src\scenario-lab
```

**2. Switch on analytics events (by hand, no command exists).** Management Client > Tools > Options > *Analytics
Events* tab: *Enable*, port 9090, *All network addresses*, OK.

**3. Load the generated clips as cameras.** This restarts the Recording Server once, and every camera pauses
recording for about 4 minutes.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\xprotect-setup\Install-ScenarioClips.ps1
```

You get three new cameras on ports 200–202, in the camera group *Scenario Lab*: `vandalism_s1_rock_window`,
`vandalism_s6_cleaning_window` and `vandalism_u1_graffiti`. Check that they play in Smart Client.

**4. Alarm, Hafnia prompt and rule, demo suggestions.** It asks for the gateway password (= the Windows
Administrator password); the password is never stored.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\xprotect-setup\Setup-ScenarioLab.ps1
```

**5. Build and deploy the plug-in.** Close Smart Client first.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build-Plugin.ps1
```

**6. Open Smart Client** (`localhost`, Windows authentication) and the **Scenario Lab** tab. Optionally, make a private
view *Scenario Lab* with `vandalism` and the three clip cameras.

Wait a few minutes after step 3 before testing, so the clip cameras have some recording. Recordings are kept 24 hours.

## Run the demo

Uses **4 Hafnia requests** (of 500 a day).

**Page ① Snapshot & scenarios**

1. Select the camera `vandalism`. Below the live view, use the slider to find a moment when the street is empty.
   Pick a frame early in that empty stretch, then press **Save this frame as snapshot**.
2. Press **Load suggestions**: the 8 scenarios for this view appear. *A worker is cleaning the wall with a brush* is
   marked "should NOT trigger" (category *harmless look-alike*).
3. Tick **#1** *A person is breaking a window with a rock* and **#6** *A worker is cleaning the wall with a brush*.
4. Under *Add your own scenario* type `A person sprays graffiti on the wall`, keep *Should trigger the alarm*, press
   **Add**. It must be the first one you add, because it becomes `vandalism_u1`, the name of its clip camera.
5. Press **Save selection**. This writes `C:\ScenarioLab\suggestions\vandalism_scenarios_user.json`, the input for video
   generation. Then press **Next: test the clips →**.

**Page ② Generated clips & Hafnia test**

6. Four rows, each with its camera: the three scenarios and the original camera. Click a row to watch its clip live.
7. Type the gateway password and press **Connect**.
8. Keep the question *Is someone vandalising the building in this video, for example breaking a window or painting on
   the wall?* and press **Run all (4 requests)**. The live view follows each clip as it is tested.

| Row | Expected | Typical result |
|---|---|---|
| Rock through the window | detect | detected, PASS |
| Cleaning the window (harmless look-alike) | no detect | not detected, PASS |
| Graffiti (user's own scenario) | detect | detected, PASS |
| Original camera: `vandalism`, real recording from the snapshot moment (its 10-second loop shows a man throwing a stone through a window) | detect | detected, PASS |

9. Open **Alarm Manager**: the rock, graffiti and original-camera runs each raised *Scenario Lab: scenario detected* on
   their own camera. It can take a few minutes.

If a row fails, change the question and run that row again (**Run selected**, 1 request). This is the point of the
tool: the question that will run on live cameras gets tuned against the site's own edge cases first.

Every test is appended to `C:\ScenarioLab\runs.jsonl`.

## Files the plug-in reads and writes

| Path | Written by | Contents |
|---|---|---|
| `C:\ScenarioLab\snapshots\<camera>_<UTC time>.png` | Page ① | The snapshot, full resolution, from XProtect's recording |
| `C:\ScenarioLab\suggestions\<camera>_scenarios.json` | Scene analysis (here: `demo/suggestions`) | `[{"title", "category", "where"}, ...]` |
| `C:\ScenarioLab\suggestions\<camera>_scenarios_user.json` | Page ①, *Save selection* | Snapshot, time, and the chosen scenarios with `id`, `source` (suggested / user), `expected` (detect / no detect) |
| `C:\ScenarioLab\runs.jsonl` | Page ② | One line per Hafnia test: scenario, camera, clip window, question, expected, got, answer |

**Scenario IDs and camera names.** Suggestion *n* of `<camera>_scenarios.json` gets the ID `<camera>_s<n>`; the user's
own scenarios get `<camera>_u1`, `_u2`, … The clip camera for a scenario is named after its ID, optionally followed by
`_` and a readable tail: `vandalism_s1_rock_window` is scenario `vandalism_s1`. Page ② finds the cameras that way.

## Your own scenarios and clips (optional)

The demo data is ready to use. To try another camera or other scenarios:

1. **Suggestions:** run Stage A (`comfyui/`), or any vision-language model, on a snapshot from page ① and save its answer as
   `C:\ScenarioLab\suggestions\<camera>_scenarios.json` in the format above.
2. **Clips:** generate one video per chosen scenario with Stage B, or any image-to-video model, with the snapshot as the first frame (static camera, one clear
   action, about 10 seconds). Convert them, with ffmpeg installed:

   ```bash
   python3 scripts/convert_clips.py rock.mp4=vandalism_s1_rock_window cleaning.mp4=vandalism_s6_cleaning_window -o my_clips
   ```

   It writes StableFPS clips in the layout the Recording Server accepts, plus `clips.json`. Plain ffmpeg/x264 output is
   **not** accepted (see `xprotect-setup/README.md`, "When a clip camera shows no video").
3. **Install:** copy the folder to the machine and run
   `Install-ScenarioClips.ps1 -ClipFolder <folder>`.

## Hafnia requests

Requests are only sent from the **Run** buttons: one per clip, never on a timer or on motion, and refused once 450 of
the 500 daily requests are used. Nothing in XProtect starts a Hafnia request from the alarm or the analytics event.

## Build requirements

- Target: .NET Framework 4.8; NuGet package `MilestoneSystems.VideoOS.Platform` 26.1.* (matches XProtect 2026 R1).
- Built with full MSBuild (Build Tools for Visual Studio 2022), like Milestone's samples; `dotnet build` does not
  work for this old-style WPF (Windows Presentation Foundation) project.

No passwords, tokens or API keys are ever committed to this repository.
