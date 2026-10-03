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


CACHE = "fetched.json"


def _cached(url: str, out_dir: Path) -> Fetched | None:
    """An earlier download of the same link in this work dir (re-analysis), without touching the network."""
    try:
        meta = json.loads((out_dir / CACHE).read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return _legacy(url, out_dir)
    path = out_dir / meta.get("file", "")
    if url not in (meta.get("url"), meta.get("requested")) or not path.is_file():
        return None
    cover_path = out_dir / "cover.bin"
    cover = cover_path.read_bytes() if cover_path.is_file() else None
    return Fetched(path, meta.get("title", ""), meta.get("artist", ""), cover, meta.get("url") or url)


_YT_ID = re.compile(r"[?&]v=([\w-]{6,})|youtu\.be/([\w-]{6,})")


def _legacy(url: str, out_dir: Path) -> Fetched | None:
    """Downloads made before fetched.json existed: yt-dlp named the file after the video id, and the
    previous analysis.json next to the download folder has the title and artist."""
    m = _YT_ID.search(url)
    prev = out_dir.parent / "analysis.json"
    if not m or not prev.is_file():
        return None
    vid = m.group(1) or m.group(2)
    files = [f for f in out_dir.glob(f"{vid}.*") if f.suffix not in (".json", ".bin", ".part", ".ytdl")]
    if len(files) != 1:
        return None
    try:
        src = json.loads(prev.read_text(encoding="utf-8")).get("source", {})
    except ValueError:
        return None
    cover = out_dir.parent / "cover.jpg"
    return Fetched(files[0], src.get("title", ""), src.get("artist", ""), cover.read_bytes() if cover.is_file() else None,
                   src.get("url") or url)


def fetch(url: str, out_dir: Path, log=print) -> Fetched:
    out_dir.mkdir(parents=True, exist_ok=True)
    if (hit := _cached(url, out_dir)) is not None:
        log(f"using the already downloaded audio ({hit.path.name})")
        return hit
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
    page = info.get("webpage_url") or url
    if cover:
        (out_dir / "cover.bin").write_bytes(cover)
    (out_dir / CACHE).write_text(json.dumps({"requested": url, "url": page, "file": path.name, "title": title,
                                             "artist": artist}), encoding="utf-8")
    return Fetched(path, title, artist, cover, page)


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
