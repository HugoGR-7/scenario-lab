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

Lesson from p043 (25 Oct 2026): x264's raw output plays in VLC but the Recording Server rejected every frame
("Unable to add one or more frames to the frame collector, Error code = 5"). Compared with Milestone's own working
clip, ours had 3-byte start codes (00 00 01) before every keyframe, SPS/PPS (the stream's parameter packets)
repeated before every keyframe, an SEI packet (x264's settings text) and colour/aspect tags. Milestone's clip has
SPS and PPS once, then only frames, every packet behind a 4-byte start code (00 00 00 01). The driver evidently cuts
frames at 4-byte start codes, so this script rewrites our stream into exactly that layout (rewrite_like_milestone).
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


def split_nals(data):
    """Split an Annex B stream into NAL unit payloads (without their start codes)."""
    starts = []
    i = 0
    while True:
        i = data.find(b"\x00\x00\x01", i)
        if i < 0:
            break
        starts.append(i + 3)
        i += 3
    nals = []
    for n, begin in enumerate(starts):
        end = starts[n + 1] - 3 if n + 1 < len(starts) else len(data)
        nal = data[begin:end]
        # A 4-byte start code is a zero byte + 00 00 01: that zero belongs to the start code, not the payload.
        if n + 1 < len(starts) and nal.endswith(b"\x00"):
            nal = nal[:-1]
        nals.append(nal)
    return nals


def rewrite_like_milestone(path):
    """SPS (type 7) and PPS (type 8) once at the start, no SEI (6) or other extras, frames only (5 = keyframe,
    1 = other frame), every NAL behind a 4-byte start code. Returns the list of NAL types written."""
    nals = split_nals(path.read_bytes())
    sps = next(n for n in nals if n[0] & 0x1F == 7)
    pps = next(n for n in nals if n[0] & 0x1F == 8)
    frames = [n for n in nals if n[0] & 0x1F in (1, 5)]
    out = [sps, pps] + frames
    path.write_bytes(b"".join(b"\x00\x00\x00\x01" + n for n in out))
    return [n[0] & 0x1F for n in out]


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("clips", nargs="*", help="<input video>=<camera name>")
    parser.add_argument("--fix", nargs="+", metavar="RAW_FILE",
                        help="only rewrite already converted raw files in place (no re-encode), then exit")
    parser.add_argument("-o", "--out", default="stablefps_out", help="output folder (default: stablefps_out)")
    args = parser.parse_args()
    if args.fix:
        for f in args.fix:
            types = rewrite_like_milestone(Path(f))
            print(f"{f}: SPS, PPS, {types.count(5)} keyframes + {types.count(1)} other frames, 4-byte start codes")
        return
    if not args.clips:
        parser.error("give at least one <input video>=<camera name>")

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
             "-vf", f"scale={WIDTH}:{HEIGHT},fps={FPS},setsar=0",    # size and rate of Milestone's clips, no aspect tag
             "-c:v", "libx264", "-pix_fmt", "yuv420p", "-profile:v", "high", "-crf", "23",
             # No colour tags in the stream (Milestone's clip has none).
             "-color_primaries", "unknown", "-color_trc", "unknown", "-colorspace", "unknown", "-color_range", "unknown",
             "-g", str(FPS), "-keyint_min", str(FPS), "-sc_threshold", "0",   # a keyframe exactly every second
             "-bf", "0",                                             # no B-frames (frames predicted from later ones)
             "-f", "h264", str(dst)])                                # raw Annex B stream, no container

        types = rewrite_like_milestone(dst)
        check = probe(dst, raw_h264=True)
        ok = check["codec_name"] == "h264" and check["width"] == WIDTH and check["height"] == HEIGHT \
            and check["pix_fmt"] == "yuv420p" and int(check.get("has_b_frames", 0)) == 0
        print(f"    check: {check['codec_name']} {check.get('profile')} {check['width']}x{check['height']} "
              f"{check['pix_fmt']} b-frames={check.get('has_b_frames')} frames={check.get('nb_read_frames')} "
              f"size={dst.stat().st_size / 1e6:.1f} MB  {'OK' if ok else 'CHECK THIS'}")
        print(f"    layout: SPS, PPS, then {types.count(5)} keyframes + {types.count(1)} other frames, 4-byte start codes")
        manifest.append({"camera": name, "file": dst.name, "source": src.name})

    (out / "clips.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"\nDone. Copy the whole '{out}' folder to p043 (e.g. C:\\ScenarioLab\\stablefps), then run "
          f"xprotect-setup\\Install-ScenarioClips.ps1 there.")


if __name__ == "__main__":
    main()
