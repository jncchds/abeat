"""Deterministic synthetic test track with known BPM, offset and section structure.

Used for tests and smoke runs without shipping copyrighted audio.
"""

from __future__ import annotations

from pathlib import Path

import numpy as np
import soundfile as sf

SR = 44100

# (name, bars, parts)
STRUCTURE = [
    ("intro", 8, {"hat", "pad"}),
    ("verse", 16, {"kick", "snare", "hat", "bass"}),
    ("build", 8, {"kick", "roll", "hat", "bass"}),
    ("drop", 16, {"kick", "snare", "hat", "bass", "lead"}),
    ("break", 8, {"pad", "lead"}),
    ("drop", 16, {"kick", "snare", "hat", "bass", "lead"}),
    ("outro", 8, {"hat", "pad"}),
]

LEAD = [72, 74, 76, 79, 76, 74, 72, 67, 69, 72, 74, 72, 69, 67, 64, 67]  # MIDI, one per 8th note pair
BASS = [36, 36, 43, 41]  # per bar


def _env(n, attack=0.002, decay=0.15):
    t = np.arange(n) / SR
    return np.minimum(1, t / attack) * np.exp(-t / decay)


def _tone(freq, dur, wave="sine", decay=0.2):
    n = int(dur * SR)
    t = np.arange(n) / SR
    if wave == "saw":
        x = 2 * ((t * freq) % 1) - 1
    elif wave == "square":
        x = np.sign(np.sin(2 * np.pi * freq * t))
    else:
        x = np.sin(2 * np.pi * freq * t)
    return x * _env(n, decay=decay)


def _midi(m):
    return 440.0 * 2 ** ((m - 69) / 12)


def kick():
    n = int(0.35 * SR)
    t = np.arange(n) / SR
    freq = 50 + 120 * np.exp(-t / 0.03)
    return np.sin(2 * np.pi * np.cumsum(freq) / SR) * _env(n, decay=0.12)


def snare(rng):
    n = int(0.2 * SR)
    return (rng.standard_normal(n) * 0.7 + _tone(190, 0.2, decay=0.05)[:n]) * _env(n, decay=0.06)


def hat(rng):
    n = int(0.05 * SR)
    x = rng.standard_normal(n)
    x = np.diff(x, prepend=0)  # crude high-pass
    return x * _env(n, decay=0.015) * 0.5


def render(bpm: float = 128.0, offset: float = 0.37, seed: int = 7) -> tuple[np.ndarray, dict]:
    rng = np.random.default_rng(seed)
    beat = 60.0 / bpm
    total_bars = sum(b for _, b, _ in STRUCTURE)
    total = offset + total_bars * 4 * beat + 2.0
    out = np.zeros(int(total * SR) + SR)

    def add(sig, t, gain=1.0):
        i = int(round(t * SR))
        j = min(len(out), i + len(sig))
        out[i:j] += sig[: j - i] * gain

    k, s = kick(), snare(rng)
    sections = []
    bar0 = 0
    for name, bars, parts in STRUCTURE:
        start = offset + bar0 * 4 * beat
        sections.append({"name": name, "start": start, "end": start + bars * 4 * beat})
        for bar in range(bars):
            tb = start + bar * 4 * beat
            for b in range(4):
                t = tb + b * beat
                if "kick" in parts:
                    add(k, t, 0.9)
                if "snare" in parts and b in (1, 3):
                    add(s, t, 0.5)
                if "hat" in parts:
                    add(hat(rng), t + beat / 2, 0.35)
                if "bass" in parts:
                    m = BASS[bar % 4]
                    add(_tone(_midi(m), beat * 0.9, "saw", decay=0.25), t + beat / 2, 0.25)
            if "roll" in parts:
                div = 2 if bar < bars // 2 else 4
                for i in range(4 * div):
                    add(s, tb + i * beat / div, 0.25 + 0.3 * bar / bars)
            if "lead" in parts:
                for i in range(8):
                    m = LEAD[(bar * 8 + i) % len(LEAD)]
                    if (bar + i) % 5 == 4:
                        continue  # leave gaps so the rhythm is not perfectly uniform
                    add(_tone(_midi(m), beat * 0.45, "square", decay=0.12), tb + i * beat / 2, 0.12)
            if "pad" in parts and bar % 2 == 0:
                for m in (60, 64, 67):
                    add(_tone(_midi(m), beat * 8, "sine", decay=2.0), tb, 0.08)
        bar0 += bars

    out = out / np.max(np.abs(out)) * 0.9
    truth = {"bpm": bpm, "firstBeatSec": offset, "sections": sections}
    return np.stack([out, out], axis=1).astype(np.float32), truth


def write(path: Path, **kw) -> dict:
    audio, truth = render(**kw)
    sf.write(str(path), audio, SR)
    return truth
