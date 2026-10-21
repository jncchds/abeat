"""The lead melody: one line through the song, whichever voice or instrument carries it.

Stems don't say where the melody is: a violin or synth lead lands in "other" or, often, in "vocals"
(Demucs files bright monophonic leads there), and singers stop for instrumental breaks. So the lead
is found by what it sounds like:

1. CREPE (tiny) on both pitched stems gives each a pitch and a periodicity per 10 ms. Periodicity is
   high when one clear pitch dominates the stem (a voice, a violin, a lead synth) and low for chords,
   pads, noise and silence.
2. Lead score per stem and frame = periodicity x the stem's share of the pitched sound (the louder
   stem carries the lead) x the overall level (quiet passages count less).
3. A Viterbi path over {none, vocals, other} with a cost for switching picks the lead stem per
   frame; "none" wins where neither stem is clearly pitched (drum breaks, pads, silence).
4. The notes are basic-pitch's transcription of the lead stem: each note starting while its stem
   leads, on the pitch CREPE hears there (or an octave off when nothing else matches), refined to
   the attack. Repeated notes on one pitch (bow changes, re-sung syllables) that basic-pitch merged
   come back from the stem's own onsets where the pitch holds.

Notes keep their stem as `src`; whether that stem holds a voice is not decided here.
"""

from __future__ import annotations

from pathlib import Path
from typing import Callable

import librosa
import numpy as np

Log = Callable[[str], None]
Note = tuple[float, float, int, float]  # start, end (s), MIDI pitch, amplitude

FPS = 100  # CREPE frames per second (10 ms hop)
STEMS = ("vocals", "other")
REST = 0.25        # "none" emission: a stem must score above this to lead
START = 0.3        # cost of the lead starting or stopping
SWITCH = 1.0       # cost of the lead moving to the other stem (bleed and bow changes make short dips)
SHARE_FULL = 0.5   # stem share of the pitched loudness from which it counts fully
CACHE_VERSION = 1


def _stem_pitch(y: np.ndarray, sr: int, log: Log, name: str) -> tuple[np.ndarray, np.ndarray]:
    from .vocals import SR16, _pitch

    y16 = librosa.resample(y, orig_sr=sr, target_sr=SR16) if sr != SR16 else y
    midi, per, _ = _pitch(y16, log, f"{name} pitch (CREPE tiny)")
    return midi, per


def _rms(y: np.ndarray, sr: int, n: int) -> np.ndarray:
    """Linear RMS per 10 ms frame, smoothed over 50 ms."""
    hop = sr // FPS
    m = min(n, len(y) // hop)
    r = np.zeros(n)
    r[:m] = np.sqrt(np.mean(y[: m * hop].astype(np.float64).reshape(m, hop) ** 2, axis=1))
    return np.convolve(r, np.ones(5) / 5, "same")


def lead_path(lead: np.ndarray, rest: float = REST, start: float = START, switch: float = SWITCH) -> np.ndarray:
    """Viterbi over state 0 ("none") and 1..k (the rows of `lead`, k x n scores). State per frame."""
    k, n = lead.shape
    emit = np.vstack([np.full(n, rest), lead])
    trans = -switch * (1 - np.eye(k + 1))  # [to, from]
    trans[0, 1:] = trans[1:, 0] = -start
    score = np.zeros(k + 1)
    back = np.zeros((n, k + 1), np.int8)
    for f in range(n):
        cand = score[None, :] + trans
        back[f] = cand.argmax(1)
        score = cand.max(1) + emit[:, f]
    out = np.zeros(n, np.int8)
    s = int(score.argmax())
    for f in range(n - 1, -1, -1):
        out[f] = s
        s = back[f, s]
    # a stem leading for under 150 ms inside another's lead is a blip (bleed, a bow change), not a switch
    blip = int(0.15 * FPS)
    bounds = np.concatenate([[0], np.flatnonzero(np.diff(out)) + 1, [n]])
    runs = [(int(a), int(b), int(out[a])) for a, b in zip(bounds[:-1], bounds[1:])]
    for i, (a, b, s) in enumerate(runs):
        if s == 0 or b - a >= blip:
            continue
        # nearest stem runs on either side, across rests shorter than a blip
        j = i - 1
        while j >= 0 and runs[j][2] == 0 and runs[j][1] - runs[j][0] < blip:
            j -= 1
        k = i + 1
        while k < len(runs) and runs[k][2] == 0 and runs[k][1] - runs[k][0] < blip:
            k += 1
        if j >= 0 and k < len(runs) and runs[j][2] == runs[k][2] not in (0, s):
            out[runs[j][1]:runs[k][0]] = runs[j][2]
    return out


def _inputs(stems: dict[str, np.ndarray], sr: int, log: Log, workdir: Path, cache: Path | None) -> dict:
    """CREPE pitch/periodicity and basic-pitch notes per pitched stem (cached next to the stems)."""
    names = [k for k in STEMS if k in stems]
    key = np.array([CACHE_VERSION, len(stems[names[0]])] + [float(np.abs(stems[k][::997]).sum()) for k in names])
    if cache is not None and cache.exists():
        try:
            z = np.load(cache, allow_pickle=False)
            if np.allclose(z["key"], key, rtol=1e-6):
                log("reusing melody pitch analysis")
                return {k: z[k] for k in z.files}
        except Exception:  # noqa: BLE001 - a broken cache is recomputed
            pass
    from .pitched import _transcribe

    res: dict[str, np.ndarray] = {"key": key}
    for k in names:
        res[k + "_midi"], res[k + "_per"] = _stem_pitch(stems[k], sr, log, k)
        log(f"transcribing {k} notes (basic-pitch)")
        res[k + "_notes"] = np.array(_transcribe(stems[k], sr, workdir), dtype=np.float64).reshape(-1, 4)
    if cache is not None:
        cache.parent.mkdir(parents=True, exist_ok=True)
        tmp = cache.with_suffix(".tmp.npz")
        np.savez_compressed(tmp, **res)
        tmp.replace(cache)
    return res


def extract(stems: dict[str, np.ndarray], sr: int, log: Log, workdir: Path,
            flux: dict[str, list[dict]] | None = None, cache: Path | None = None) -> tuple[list[dict], dict]:
    """Melody onsets ({t, s, br, e, src}) and {"segments": [{start, end, src}]} from the pitched stems.
    `flux` holds the stems' spectral-flux onsets (for repeated notes)."""
    from .features import attack_envelope

    names = [k for k in STEMS if k in stems]
    if not names:
        return [], {"segments": []}
    z = _inputs(stems, sr, log, workdir, cache)
    n = min(len(z[k + "_midi"]) for k in names)
    amp = {k: _rms(stems[k], sr, n) for k in names}
    total = sum(amp.values()) + 1e-9
    level = np.minimum(1.0, total / max(1e-9, np.percentile(total, 90)))
    # a stem as loud as the other counts fully (voice and violin together must not halve each other);
    # only a clearly quieter stem (bleed, accompaniment) is marked down
    lead = np.vstack([z[k + "_per"][:n] * np.minimum(1.0, amp[k] / total / SHARE_FULL) * np.sqrt(level) for k in names])
    state = lead_path(lead)

    found: list[tuple[float, float, float, float, int, int]] = []  # start, end, pitch, strength, stem, octave?
    for si, k in enumerate(names, 1):
        midi, per = z[k + "_midi"][:n], z[k + "_per"][:n]
        for s0, e0, p, a in z[k + "_notes"]:
            f0 = int(s0 * FPS)
            seg = slice(f0, min(n, f0 + 10))
            on = state[seg] == si
            if len(on) == 0 or on.mean() < 0.5:
                continue
            d = float(np.median(np.abs(midi[seg][on] - p)))
            octave = 0 if d <= 1.0 else 1 if abs(d - 12) <= 1.0 else -1
            if octave >= 0:
                found.append((s0, e0, p, float(lead[si - 1][seg][on].mean()), si, octave))
    found.sort()
    notes: list[list] = []
    for m in found:  # one note per 40 ms: the pitch match beats the octave match, then the stronger
        if notes and m[0] - notes[-1][0] < 0.04:
            if (m[5], -m[3]) < (notes[-1][5], -notes[-1][3]):
                notes[-1] = list(m)
            continue
        notes.append(list(m))

    if flux:  # repeated notes basic-pitch merged: the stem's own onsets inside a note, same pitch
        extra = []
        for s0, e0, p, s, si, _ in notes:
            k = names[si - 1]
            for o in flux.get(k, []):
                if s0 + 0.08 < o["t"] < e0 - 0.08:
                    f = int(o["t"] * FPS)
                    if f + 8 <= n and abs(np.median(z[k + "_midi"][f:f + 8]) - p) <= 1 and z[k + "_per"][f:f + 8].mean() > 0.4:
                        extra.append([o["t"], e0, p, s, si, 0])
        notes = sorted(notes + extra)
        for i in range(len(notes) - 1):  # a note ends where the next one of the line starts
            notes[i][1] = min(notes[i][1], notes[i + 1][0]) if notes[i + 1][0] > notes[i][0] else notes[i][1]

    env = {k: attack_envelope(stems[k], sr, 150, 4000, 512) for k in names}
    pitches = np.array([m[2] for m in notes]) if notes else np.zeros(1)
    lo, hi = np.percentile(pitches, 5), np.percentile(pitches, 95)
    ref = np.percentile([m[3] for m in notes], 95) if notes else 1.0
    out: list[dict] = []
    for s0, e0, p, s, si, _ in notes:
        d, t = env[names[si - 1]]
        w = (t >= s0 - 0.05) & (t <= s0 + 0.02)
        start = float(t[w][np.argmax(d[w])]) if w.any() and d[w].max() > 0 else s0
        if out and start - out[-1]["t"] < 0.04:
            continue
        out.append({"t": round(start, 4), "s": round(float(min(1.0, s / max(ref, 1e-9))), 3),
                    "br": round(float(np.clip((p - lo) / max(1.0, hi - lo), 0, 1)), 3),
                    "e": round(float(max(e0, start + 0.03)), 4), "src": names[si - 1]})
    segs = segments(state, names)
    share = {k: sum(s["end"] - s["start"] for s in segs if s["src"] == k) for k in names}
    log(f"melody: {len(out)} notes; lead " + ", ".join(f"{k} {v:.0f} s" for k, v in share.items()))
    return out, {"segments": segs}


def segments(state: np.ndarray, names: list[str], gap: float = 1.5, min_len: float = 1.0) -> list[dict]:
    """Stretches where a stem leads, bridging gaps (rests, the other stem briefly) under `gap` seconds."""
    runs: list[list] = []
    f, n = 0, len(state)
    while f < n:
        g = f
        while g < n and state[g] == state[f]:
            g += 1
        if state[f] > 0:
            src = names[state[f] - 1]
            if runs and runs[-1][2] == src and f / FPS - runs[-1][1] < gap:
                runs[-1][1] = g / FPS
            else:
                runs.append([f / FPS, g / FPS, src])
        f = g
    return [{"start": round(a, 2), "end": round(b, 2), "src": s} for a, b, s in runs if b - a >= min_len]
