# XProtect and Hafnia setup

Three scripts, run on the XProtect machine in PowerShell **as Administrator**, from the repository folder. Each one
checks by name first and only creates what is missing, so running it again is safe.

| Script | What it does | Restarts anything? |
|---|---|---|
| `Install-ScenarioClips.ps1` | Loads the generated clips (`demo\stablefps`) as StableFPS cameras, one per clip | Yes: the Recording Server, about 4 minutes, once |
| `Setup-ScenarioLab.ps1` | Demo suggestions into `C:\ScenarioLab`, the analytics event, the alarm definition and its sources, the Hafnia saved prompt and gateway rule | No |
| `Diagnose-ScenarioClips.ps1` | Read only: compares our clip cameras with one of Milestone's, and shows the Recording Server's device log | No |

## 1. Load the clips as cameras

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\xprotect-setup\Install-ScenarioClips.ps1
```

- **Files** step: waits until the Recording Server has run for 90 seconds, stops it (every camera pauses recording for
  a few minutes), copies the clips into `StableFPS_Data\Video\H264`, changes the revision number in `StableFPS.xml`
  (so the driver re-reads its file list; backup in `C:\ScenarioLab\backup`), starts it again and waits 2.5 minutes.
- **Cameras** step: one StableFPS hardware device per clip on `127.0.0.1:200`, `201`, `202`, ... (100–199 belong to
  Milestone). Frame rate and codec are copied from Milestone's `bin_pushed_over_onto_sidewalk` camera (read only), the
  file setting points at our clip, the camera gets the clip's name and goes into the camera group *Scenario Lab*.
- One step at a time: `-Step Files` or `-Step Cameras`. `-Step Inspect` changes nothing and prints the settings.
- Your own clips: `-ClipFolder <folder>` with clips made by `scripts/convert_clips.py` (see the main README).

Check: in Smart Client, the three cameras `vandalism_s1_rock_window`, `vandalism_s6_cleaning_window` and
`vandalism_u1_graffiti` play their clip in a loop.

## 2. Alarm, Hafnia prompt and rule, demo suggestions

First, by hand (there is no command for it): Management Client > **Tools > Options > Analytics Events**: *Enable*,
port 9090, *All network addresses*, OK.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\xprotect-setup\Setup-ScenarioLab.ps1
```

It asks once for the Hafnia gateway password (the Windows Administrator password); the password is never stored.

| Step (`-Step`) | Creates or checks |
|---|---|
| `Folders` | `C:\ScenarioLab\snapshots`, `C:\ScenarioLab\suggestions`, and copies `demo\suggestions\vandalism_scenarios.json` there |
| `AnalyticsEvent` | Analytics event `Scenario Lab detection` (Configuration REST API, as the current Windows user) |
| `Alarm` | Alarm definition `Scenario Lab: scenario detected` on that event; sources **All Cameras** |
| `Hafnia` | Saved prompt `Scenario Lab`, gateway rule `Scenario Lab` (`SCENARIO_DETECTED` → `Scenario Lab detection`) |

**Alarm sources.** An alarm only appears for cameras in the alarm definition's sources. With *All Cameras* (the
default) every clip camera is covered, including ones added later. `-AlarmSources ScenarioLab` lists only the snapshot
camera `vandalism` and the cameras in `demo\stablefps\clips.json`. If the script cannot change the sources, it prints
the clicks: Management Client > Alarms > Alarm Definitions > *Scenario Lab: scenario detected* > Sources.

## When a clip camera shows no video

Run `Diagnose-ScenarioClips.ps1 > C:\ScenarioLab\diagnose.txt` and read the end of it.

- `Unable to add one or more frames to the frame collector … Error code = 5` every minute: the Recording Server
  rejects the clip's frames. Seen on 25 Oct 2026 with plain x264 output. The clip must have Milestone's byte layout:
  SPS and PPS (the stream's parameter packets) once at the start, then only frames, every packet behind a 4-byte start
  code `00 00 00 01`, no SEI and no colour or aspect tags. `scripts/convert_clips.py` writes exactly that; the clips in
  `demo\stablefps` are already in that form.
- The clip is not in the camera's **102 VideoH264Files** list: the driver has not re-read its files. Run
  `Install-ScenarioClips.ps1 -Step Files` again. The driver plays the file named in **102 VideoH264Files**.

**Never:** ports 100–199, changing Milestone's 11 cameras or their files, running `InstallStableFPS.bat`, stopping the
Recording Server within 90 seconds of starting it.
