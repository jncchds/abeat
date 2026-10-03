"""Download audio from YouTube / YouTube Music (or anything yt-dlp supports) for personal use."""

from __future__ import annotations

import json
import re
import shutil
import subprocess
import sys
import urllib.request
from dataclasses import dataclass
from pathlib import Path

URL_RE = re.compile(r"^(https?://|ytsearch\d*:)", re.I)

# "[NCS Release]", "(Official Video)", "(Lyrics)", ... are not part of the song title
JUNK_RE = re.compile(
    r"\s*[\[(][^\])]*\b(official|video|audio|lyrics?|lyric video|visuali[sz]er|ncs|release|hd|hq|4k|mv|m/v|"
    r"music video|copyright|free download|out now)\b[^\])]*[\])]",
    re.I,
)


@dataclass
class Fetched:
    path: Path
    title: str
    artist: str
    cover: bytes | None
    url: str


def is_url(s: str) -> bool:
    return bool(URL_RE.match(s))


def _js_runtime() -> list[str]:
    # yt-dlp needs a JS runtime for YouTube; it uses deno by default, fall back to node/bun
    for rt in ("deno", "node", "bun"):
        if shutil.which(rt):
            return [] if rt == "deno" else ["--js-runtimes", rt]
    return []


def fetch(url: str, out_dir: Path, log=print) -> Fetched:
    out_dir.mkdir(parents=True, exist_ok=True)
    cmd = [sys.executable, "-m", "yt_dlp", *_js_runtime(), "-f", "bestaudio/best", "--no-playlist",
           "--no-progress", "-o", str(out_dir / "%(id)s.%(ext)s"), "--print-json", "--no-simulate", url]
    log(f"downloading {url}")
    proc = subprocess.run(cmd, capture_output=True, text=True)
    if proc.returncode != 0:
        err = [line for line in proc.stderr.splitlines() if "ERROR" in line] or proc.stderr.splitlines()[-3:]
        raise RuntimeError("download failed: " + " | ".join(err))
    info = json.loads(proc.stdout.strip().splitlines()[-1])
    path = Path(info.get("_filename") or info.get("filename") or "")
    if not path.exists():
        matches = sorted(out_dir.glob(f"{info['id']}.*"))
        if not matches:
            raise RuntimeError("download finished but no file found")
        path = matches[0]

    title, artist = _title_artist(info)
    cover = None
    if info.get("thumbnail"):
        try:
            with urllib.request.urlopen(info["thumbnail"], timeout=20) as r:
                cover = r.read()
        except Exception:
            pass
    log(f"downloaded '{artist} - {title}' ({info.get('duration', 0)}s)")
    return Fetched(path, title, artist, cover, info.get("webpage_url") or url)


def _title_artist(info: dict) -> tuple[str, str]:
    # YouTube Music provides proper track/artist fields
    if info.get("track") and (info.get("artist") or info.get("creator")):
        artist = info.get("artist") or info.get("creator")
        return info["track"], artist.split(",")[0].strip()
    title = JUNK_RE.sub("", info.get("title") or "").strip()
    if " - " in title:
        artist, title = (s.strip() for s in title.split(" - ", 1))
        return title, artist
    uploader = re.sub(r"\s*-\s*Topic$", "", info.get("uploader") or info.get("channel") or "")
    return title, uploader
