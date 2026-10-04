"""Installs ABeat's analysis runtime into the /runtime volume on container start (stdlib only).

The image holds the server, the UI and the worker *source*; the Python environment (torch for the
detected accelerator, beat_this, Demucs, ...), ArcViewer and yt-dlp's JS runtime are installed here once
and reused across restarts and image updates (a new environment is built only when the lockfile, the
accelerator or the extras change). Models download into /models on first use.

Environment:
  ABEAT_ACCEL      auto (default) | cpu | cuda | cuda12 | rocm | xpu | none (librosa only, no torch)
  ABEAT_EXTRAS     comma list of optional worker extras: lyrics (Whisper), roformer (BS-RoFormer)
  ABEAT_ARCVIEWER  1 (default) fetches the ArcViewer web build, 0 skips it
  ABEAT_PREFETCH   1 (default) downloads the beat_this and Demucs models into /models right away

Progress goes to stdout and /runtime/status.json ({state, accel, extras, worker, message}), which the
server reads to show the state and to hold analyses until the runtime is ready.
"""

from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

RUNTIME = Path(os.environ.get("ABEAT_RUNTIME", "/runtime"))
PROJECT = Path(os.environ.get("ABEAT_ANALYSIS_DIR", "/opt/abeat/analysis"))
STATUS = RUNTIME / "status.json"
ACCELS = ("cpu", "cuda", "cuda12", "rocm", "xpu", "none")
EXTRAS = ("lyrics", "roformer")


def log(msg: str) -> None:
    print(f"[provision] {msg}", flush=True)


def status(state: str, **kw) -> None:
    data = {"state": state, "time": time.strftime("%Y-%m-%dT%H:%M:%S"), **kw}
    tmp = STATUS.with_suffix(".tmp")
    tmp.write_text(json.dumps(data))
    tmp.replace(STATUS)


def detect_accel() -> str:
    """NVIDIA when the container sees an NVIDIA device (--gpus), ROCm with /dev/kfd, Intel XPU with an
    Intel render node, else CPU. NVIDIA GPUs older than Turing need ABEAT_ACCEL=cuda12."""
    if Path("/dev/nvidiactl").exists() or Path("/proc/driver/nvidia/version").exists() or shutil.which("nvidia-smi"):
        return "cuda"
    if Path("/dev/kfd").exists():
        return "rocm"
    for vendor in Path("/sys/class/drm").glob("renderD*/device/vendor"):
        try:
            if vendor.read_text().strip() == "0x8086":
                return "xpu"
        except OSError:
            pass
    return "cpu"


def env_key(accel: str, extras: list[str]) -> str:
    h = hashlib.sha256((PROJECT / "uv.lock").read_bytes())
    h.update(f"{accel}|{','.join(extras)}".encode())
    return h.hexdigest()[:12]


def uv_env() -> dict[str, str]:
    env = dict(os.environ)
    env.update(UV_PYTHON_INSTALL_DIR=str(RUNTIME / "python"), UV_CACHE_DIR=str(RUNTIME / "uv-cache"),
               UV_LINK_MODE="hardlink", UV_COMPILE_BYTECODE="1", UV_PYTHON_PREFERENCE="only-managed")
    return env


def build_env(accel: str, extras: list[str], target: Path) -> None:
    args = ["uv", "sync", "--frozen", "--no-dev", "--project", str(PROJECT)]
    if accel != "none":
        args += ["--extra", "ml", "--extra", accel]
    for e in extras:
        args += ["--extra", e]
    env = uv_env()
    env["UV_PROJECT_ENVIRONMENT"] = str(target)
    log(" ".join(args))
    subprocess.run(args, env=env, check=True)


def prefetch(worker_python: Path) -> None:
    code = ("from beat_this.inference import Audio2Beats; Audio2Beats(checkpoint_path='final0', device='cpu', dbn=False)\n"
            "from demucs.pretrained import get_model; get_model('htdemucs')")
    log("downloading beat_this and Demucs models into /models")
    subprocess.run([str(worker_python), "-c", code], check=True)


def fetch_arcviewer() -> None:
    """ArcViewer (GPL-3.0, github.com/AllPoland/ArcViewer): the pinned files of scripts/fetch-arcviewer.sh."""
    script = (Path(__file__).parent / "fetch-arcviewer.sh").read_text()
    commit = re.search(r"COMMIT=(\w+)", script).group(1)
    files = re.search(r'FILES="([^"]+)"', script).group(1).split()
    dest = RUNTIME / "arcviewer"
    if (dest / ".commit").exists() and (dest / ".commit").read_text() == commit:
        return
    log(f"fetching ArcViewer {commit[:8]} (~82 MB)")
    base = f"https://raw.githubusercontent.com/AllPoland/ArcViewer/{commit}/"
    tmp = RUNTIME / "arcviewer.tmp"
    shutil.rmtree(tmp, ignore_errors=True)
    for f in files:
        (tmp / f).parent.mkdir(parents=True, exist_ok=True)
        for attempt in range(3):
            try:
                with urllib.request.urlopen(base + f, timeout=60) as r, open(tmp / f, "wb") as out:
                    shutil.copyfileobj(r, out)
                break
            except OSError:
                if attempt == 2:
                    raise
                time.sleep(2)
    (tmp / ".commit").write_text(commit)
    shutil.rmtree(dest, ignore_errors=True)
    tmp.rename(dest)


def main() -> int:
    RUNTIME.mkdir(parents=True, exist_ok=True)
    accel = os.environ.get("ABEAT_ACCEL", "auto").strip().lower() or "auto"
    if accel == "auto":
        accel = detect_accel()
        log(f"accelerator: {accel} (detected; set ABEAT_ACCEL to override)")
    if accel not in ACCELS:
        status("failed", message=f"unknown ABEAT_ACCEL '{accel}' (auto, {', '.join(ACCELS)})")
        return 1
    extras = sorted({e.strip().lower() for e in os.environ.get("ABEAT_EXTRAS", "").split(",") if e.strip()})
    unknown = [e for e in extras if e not in EXTRAS]
    if unknown:
        status("failed", accel=accel, message=f"unknown ABEAT_EXTRAS {unknown} (known: {', '.join(EXTRAS)})")
        return 1

    key = env_key(accel, extras)
    target = RUNTIME / "envs" / f"{accel}-{key}"
    worker = target / "bin" / "abeat-analyze"
    info = dict(accel=accel, extras=extras, worker=str(worker))
    try:
        if not (target / ".ready").exists():
            status("installing", message=f"installing the analysis runtime ({accel}); the first start downloads 1-5 GB", **info)
            shutil.rmtree(target, ignore_errors=True)
            build_env(accel, extras, target)
            if accel != "none" and os.environ.get("ABEAT_PREFETCH", "1") == "1":
                status("installing", message="downloading models", **info)
                prefetch(target / "bin" / "python")
            (target / ".ready").write_text(key)
        # environments of older lockfiles / other accelerators are dead weight
        for old in (RUNTIME / "envs").iterdir():
            if old != target and old.is_dir():
                log(f"removing old environment {old.name}")
                shutil.rmtree(old, ignore_errors=True)
        if os.environ.get("ABEAT_ARCVIEWER", "1") == "1":
            status("installing", message="fetching ArcViewer", **info)
            try:
                fetch_arcviewer()
            except OSError as e:  # the viewer is optional; the UI falls back to the public site
                log(f"ArcViewer download failed ({e}); using the public site")
        status("ready", message=f"analysis runtime ready ({accel})", **info)
        log("ready")
        return 0
    except subprocess.CalledProcessError as e:
        status("failed", message=f"installing the runtime failed ({e}); see the container log", **info)
        return 1


if __name__ == "__main__":
    sys.exit(main())
