# XProtect setup

## Load the generated clips as cameras

Each generated clip plays in XProtect as its own StableFPS camera (a driver that plays a file as a camera). To XProtect
it is an ordinary camera: live view, recording, an ID. That makes it a valid source for Hafnia VLM. Background and
Milestone's rules: `guides/stablefps-custom-clips.md` in the project notes.

**Camera names** come from the scenario IDs in `<camera>_scenarios_user.json`, written by the plug-in's
*Save selection*: suggestion *n* of the suggestions file is `<prefix>_s<n>`, the user's own scenarios are
`<prefix>_u1`, `_u2`, ... A camera named `<id>` or `<id>_<anything>` is matched to that scenario by the plug-in, so
`vandalism_s1_rock_window` is scenario `vandalism_s1`.

### 1. Convert (any machine with ffmpeg)

```bash
python3 scripts/convert_clips.py rock.mp4=vandalism_s1_rock_window \
                                 cleaning.mp4=vandalism_s6_cleaning_window \
                                 graffiti.mp4=vandalism_u1_graffiti -o stablefps_out
```

Output: raw H.264 files `832x480_<camera name>` (832 × 480 px, 10 fps, a keyframe every second, no audio, no
container) and `clips.json`. A 10-second clip at 24 fps becomes 100 frames at 10 fps, still 10 seconds.

### 2. Install (p043, PowerShell as Administrator)

Copy the whole `stablefps_out` folder to `C:\ScenarioLab\stablefps`, then:

```powershell
cd C:\src\scenario-lab
powershell -NoProfile -ExecutionPolicy Bypass -File .\xprotect-setup\Install-ScenarioClips.ps1
```

- **Files** step: stops the Recording Server for a few minutes (every camera pauses recording), copies the clips,
  changes the revision number in `StableFPS.xml` (backup in `C:\ScenarioLab\backup`), starts it again, waits 2.5 min.
- **Cameras** step: one hardware device per clip on `127.0.0.1:200`, `201`, ... with the settings copied from
  Milestone's `bin_pushed_over_onto_sidewalk` camera (read only), the file setting pointing at our clip, camera named
  like the clip, in the camera group *Scenario Lab*.
- Run one step at a time with `-Step Files` or `-Step Cameras`. `-Step Inspect` changes nothing and prints the
  StableFPS settings of Milestone's camera and of ours: use it if something looks wrong.

Then in Smart Client make a view **Scenario Lab** with the new cameras (Milestone asked us not to use
Hackathon > Cameras). Give them a minute to record before testing.

### If the Cameras step fails: by hand in Management Client

For each clip:

1. Recording Servers → right-click the server → **Add hardware** → **Manual** → address `127.0.0.1`, port `200`
   (then `201`, `202`), any user name/password → driver **Milestone → StableFPS** (no auto-detect).
2. Name the hardware and its camera exactly like the clip's camera name (e.g. `vandalism_s1_rock_window`).
3. Hardware → **Settings**: `000 Frames per second` = 10, `100 VideoCodec` = H264,
   `102 VideoH264Files` = the clip (`832x480_vandalism_s1_rock_window`). Everything else default.
4. Enable only the camera.

If the clip is not in the `102 VideoH264Files` list, the driver hasn't re-read its files: run `-Step Files` again.

**Never:** ports 100–199, changing Milestone's 11 cameras, running `InstallStableFPS.bat`, stopping the Recording
Server within 90 seconds of starting it.
