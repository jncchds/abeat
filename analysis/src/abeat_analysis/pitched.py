"""Note onsets for the pitched stems ("other", "bass") from Spotify's basic-pitch.

basic-pitch transcribes polyphonic audio into notes (start, end, MIDI pitch, amplitude). Compared with
spectral flux this gives one onset per played note instead of per timbre change, the pitch of the
melody (the top voice of "other", the bottom of "bass") for the note row and angle offsets, and how
long each note is held (for arcs). It runs its bundled ONNX model with onnxruntime; TensorFlow is not
needed (see the uv override in pyproject.toml).
"""

from __future__ import annotations

import os
import tempfile
from pathlib import Path
from typing import Callable

import numpy as np

Log = Callable[[str], None]


def _transcribe(y: np.ndarray, sr: int, workdir: Path) -> list[tuple[float, float, int, float]]:
    import soundfile as sf
    from basic_pitch import ICASSP_2022_MODEL_PATH
    from basic_pitch.inference import predict

    # basic-pitch reads a file; keep the temp file next to the analysis (not in /tmp, often a small tmpfs)
    fd, tmp = tempfile.mkstemp(suffix=".wav", dir=workdir)
    os.close(fd)
    try:
        sf.write(tmp, y, sr, subtype="FLOAT")
        _, _, notes = predict(tmp, ICASSP_2022_MODEL_PATH, onset_threshold=0.5, frame_threshold=0.3,
                              minimum_note_length=58, melodia_trick=True)
    finally:
        os.unlink(tmp)
    return [(float(s), float(e), int(p), float(a)) for s, e, p, a, *_ in notes]


def note_onsets(y: np.ndarray, sr: int, log: Log, top: bool, workdir: Path, refine: bool = True) -> list[dict]:
    """One onset per group of notes starting together (within 40 ms): time of the earliest, refined to
    the strongest attack in the 50 ms before it to 20 ms after; strength from the loudest note; pitch of
    the top note (`top`, melody) or bottom note (bass) scaled by the stem's own range as brightness; end
    of that note as `e` (seconds)."""
    from .features import attack_envelope

    notes = sorted(_transcribe(y, sr, workdir))
    if not notes:
        return []
    pitches = np.array([n[2] for n in notes])
    lo, hi = np.percentile(pitches, 5), np.percentile(pitches, 95)
    groups: list[list[tuple[float, float, int, float]]] = []
    for n in notes:
        if groups and n[0] - groups[-1][0][0] < 0.04:
            groups[-1].append(n)
        else:
            groups.append([n])
    d, t = attack_envelope(y, sr, 60 if not top else 150, 4000, 512) if refine else (None, None)
    ref = np.percentile([max(n[3] for n in g) for g in groups], 95)
    out = []
    for g in groups:
        lead = max(g, key=lambda n: n[2]) if top else min(g, key=lambda n: n[2])
        start = g[0][0]
        if refine:
            m = (t >= start - 0.05) & (t <= start + 0.02)
            if m.any() and d[m].max() > 0:
                start = float(t[m][np.argmax(d[m])])
        if out and start - out[-1]["t"] < 0.04:
            continue
        out.append({
            "t": round(start, 4),
            "s": round(float(min(1.0, max(n[3] for n in g) / ref)), 3),
            "br": round(float(np.clip((lead[2] - lo) / max(1.0, hi - lo), 0, 1)), 3),
            "e": round(lead[1], 4),
        })
    log(f"basic-pitch: {len(notes)} notes -> {len(out)} onsets")
    return out
