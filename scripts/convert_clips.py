#!/usr/bin/env python3
"""
convert_clips.py - turn generated MP4 clips into files the StableFPS camera driver can play.

Run on any machine with ffmpeg and ffprobe (Hugo's Ubuntu PC, or p043 if ffmpeg is installed there):

    python3 scripts/convert_clips.py clips/rock.mp4=vandalism_s1_rock_window \
                                     clips/cleaning.mp4=vandalism_s6_cleaning_window \
                                     clips/graffiti.mp4=vandalism_u1_graffiti \
                                     -o stablefps_out

Each argument is  <input video>=<camera name>.  The camera name is what XProtect and the plug-in will call the camera:
it must start with the scenario ID from <camera>_scenarios_user.json (vandalism_s1, vandalism_u1, ...), then
optionally "_" and a readable tail. Letters, digits, "_" and "-" only.

What the StableFPS driver wants (guides/stablefps-custom-clips.md, Milestone's README):
  - a raw H.264 stream in Annex B form (start codes before each NAL unit, the H.264 packets), no MP4 container,
    no audio, no file extension;
  - name <width>x<height>_<name>, e.g. 832x480_vandalism_s1_rock_window;
  - 10 fps (frames per second) here, one keyframe every 10 frames (= every second).
24 fps in -> 10 fps out drops frames evenly; a 10-second clip stays 10 seconds (100 frames).
"""
import argparse
import json
import re
import shutil
import subprocess
import sys
from pathlib import Path

WIDTH, HEIGHT, FPS = 832, 480, 10
NAME_OK = re.compile(r"^[A-Za-z0-9_-]+$")


def run(cmd):
    result = subprocess.run(cmd, capture_output=True, text=True)
    if result.returncode != 0:
        sys.exit(f"Command failed:\n  {' '.join(cmd)}\n{result.stderr.strip()}")
    return result.stdout


def probe(path, raw_h264=False):
    cmd = ["ffprobe", "-v", "error"]
    if raw_h264:
        cmd += ["-f", "h264"]
    cmd += ["-select_streams", "v:0", "-count_frames",
            "-show_entries", "stream=codec_name,profile,width,height,pix_fmt,has_b_frames,nb_read_frames,r_frame_rate",
            "-of", "json", str(path)]
    return json.loads(run(cmd))["streams"][0]


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("clips", nargs="+", help="<input video>=<camera name>")
    parser.add_argument("-o", "--out", default="stablefps_out", help="output folder (default: stablefps_out)")
    args = parser.parse_args()

    for tool in ("ffmpeg", "ffprobe"):
        if shutil.which(tool) is None:
            sys.exit(f"{tool} not found: install ffmpeg first (Ubuntu: sudo apt install ffmpeg).")

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    manifest = []

    for item in args.clips:
        if "=" not in item:
            sys.exit(f"'{item}': write it as <input video>=<camera name>.")
        src, name = item.rsplit("=", 1)
        src = Path(src)
        if not src.is_file():
            sys.exit(f"Not found: {src}")
        if not NAME_OK.match(name):
            sys.exit(f"Camera name '{name}': only letters, digits, '_' and '-'.")

        dst = out / f"{WIDTH}x{HEIGHT}_{name}"
        info = probe(src)
        print(f"{src.name}: {info.get('width')}x{info.get('height')}, {info.get('r_frame_rate')} fps, "
              f"{info.get('nb_read_frames')} frames  ->  {dst.name}")

        run(["ffmpeg", "-y", "-v", "error", "-i", str(src),
             "-an",                                                  # no audio
             "-vf", f"scale={WIDTH}:{HEIGHT},setsar=1,fps={FPS}",    # the size and rate of Milestone's own clips
             "-c:v", "libx264", "-pix_fmt", "yuv420p",
             "-profile:v", "high", "-preset", "veryfast", "-crf", "20",
             "-g", str(FPS), "-keyint_min", str(FPS), "-sc_threshold", "0",   # a keyframe exactly every second
             "-bf", "0",                                             # no B-frames (frames predicted from later ones)
             "-f", "h264", str(dst)])                                # raw Annex B stream, no container

        check = probe(dst, raw_h264=True)
        ok = check["codec_name"] == "h264" and check["width"] == WIDTH and check["height"] == HEIGHT \
            and check["pix_fmt"] == "yuv420p" and int(check.get("has_b_frames", 0)) == 0
        print(f"    check: {check['codec_name']} {check.get('profile')} {check['width']}x{check['height']} "
              f"{check['pix_fmt']} b-frames={check.get('has_b_frames')} frames={check.get('nb_read_frames')} "
              f"size={dst.stat().st_size / 1e6:.1f} MB  {'OK' if ok else 'CHECK THIS'}")
        manifest.append({"camera": name, "file": dst.name, "source": src.name})

    (out / "clips.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"\nDone. Copy the whole '{out}' folder to p043 (e.g. C:\\ScenarioLab\\stablefps), then run "
          f"xprotect-setup\\Install-ScenarioClips.ps1 there.")


if __name__ == "__main__":
    main()
