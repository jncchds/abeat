"""Beat / downbeat tracking and fitting a constant-BPM grid suitable for Beat Saber."""

from __future__ import annotations

from dataclasses import dataclass

import librosa
import numpy as np

ANALYSIS_SR = 22050
HOP = 512


@dataclass
class TempoResult:
    beats: np.ndarray  # seconds (original audio time)
    downbeats: np.ndarray
    backend: str


@dataclass
class Grid:
    bpm: float
    first_beat: float  # time of grid beat 0 (may be negative before padding)
    residual_ms: float  # RMS deviation of detected beats from the fixed grid
    max_dev_ms: float
    stable: bool


def track_beats(y: np.ndarray, sr: int, backend: str = "auto") -> TempoResult:
    if backend in ("auto", "beat_this"):
        try:
            return _beat_this(y, sr)
        except ImportError:
            if backend == "beat_this":
                raise
    return _librosa_beats(y, sr)


def _beat_this(y: np.ndarray, sr: int) -> TempoResult:
    from beat_this.inference import Audio2Beats  # optional dependency (extra "ml")

    a2b = Audio2Beats(checkpoint_path="final0", device="cpu", dbn=False)
    beats, downbeats = a2b(y, sr)
    return TempoResult(np.asarray(beats, float), np.asarray(downbeats, float), "beat_this")


def _librosa_beats(y: np.ndarray, sr: int) -> TempoResult:
    if sr != ANALYSIS_SR:
        y = librosa.resample(y, orig_sr=sr, target_sr=ANALYSIS_SR)
        sr = ANALYSIS_SR
    env = librosa.onset.onset_strength(y=y, sr=sr, hop_length=HOP, aggregate=np.median)
    _, beat_frames = librosa.beat.beat_track(onset_envelope=env, sr=sr, hop_length=HOP, tightness=200)
    beats = librosa.frames_to_time(beat_frames, sr=sr, hop_length=HOP)
    downbeats = _estimate_downbeats(y, sr, beats)
    return TempoResult(beats, downbeats, "librosa")


def _estimate_downbeats(y: np.ndarray, sr: int, beats: np.ndarray, meter: int = 4) -> np.ndarray:
    """Pick the bar phase whose beats carry the most low-frequency onset energy (kick drums)."""
    if len(beats) < meter * 2:
        return beats[:1]
    mel = librosa.feature.melspectrogram(y=y, sr=sr, hop_length=HOP, n_mels=64, fmax=8000)
    low = librosa.onset.onset_strength(S=librosa.power_to_db(mel[:8]), sr=sr, hop_length=HOP)
    frames = np.clip(librosa.time_to_frames(beats, sr=sr, hop_length=HOP), 0, len(low) - 1)
    strength = low[frames]
    scores = [strength[p::meter].mean() for p in range(meter)]
    phase = int(np.argmax(scores))
    return beats[phase::meter]


def fit_grid(beats: np.ndarray, bpm_override: float | None = None) -> Grid:
    """Least-squares fit of a constant tempo grid to detected beats."""
    if len(beats) < 4:
        bpm = bpm_override or 120.0
        return Grid(bpm, float(beats[0]) if len(beats) else 0.0, 0.0, 0.0, False)

    if bpm_override:
        period = 60.0 / bpm_override
        idx = np.round((beats - beats[0]) / period)
        first = float(np.median(beats - idx * period))
    else:
        # Frame-quantized beat times bias the median IBI, so estimate the period over the whole span,
        # then refine with a least-squares fit that ignores outliers (dropped/shifted beats).
        span = beats[-1] - beats[0]
        ibi = np.diff(beats)
        med = float(np.median(ibi))
        p0 = float(ibi[np.abs(ibi - med) < 0.1 * med].mean())  # mean, not median: beats are frame-quantized
        period = span / max(1, round(span / p0))
        first = float(beats[0])
        keep = np.ones(len(beats), bool)
        for _ in range(3):
            idx = np.round((beats - first) / period)
            period, first = np.polyfit(idx[keep], beats[keep], 1)
            dev = beats - (first + np.round((beats - first) / period) * period)
            keep = np.abs(dev) < max(0.03, 3 * np.std(dev[keep]))
        period, first = float(period), float(first)
        idx = np.round((beats - first) / period)

    bpm = 60.0 / period
    # Prefer round-ish BPMs when they fit equally well (common for produced music)
    if not bpm_override:
        rounded = round(bpm * 2) / 2
        if abs(rounded - bpm) < 0.15:
            bpm, period = rounded, 60.0 / rounded
            first = float(np.median(beats - idx * period))

    return _with_stats(beats, bpm, first)


def _with_stats(beats: np.ndarray, bpm: float, first: float) -> Grid:
    period = 60.0 / bpm
    dev = beats - (first + np.round((beats - first) / period) * period)
    residual = float(np.sqrt(np.mean(dev**2)) * 1000)
    max_dev = float(np.percentile(np.abs(dev), 98) * 1000)
    return Grid(bpm=bpm, first_beat=first, residual_ms=residual, max_dev_ms=max_dev, stable=max_dev < 40)


def refine_phase(y: np.ndarray, sr: int, grid: Grid, beats: np.ndarray, full_search: bool) -> Grid:
    """Shift the grid to maximize onset energy on grid beats (kick-weighted).

    full_search=True searches a whole beat period, which also fixes trackers locking onto off-beats
    (common with librosa on hat-heavy intros). Otherwise only +-30 ms of fine alignment.
    """
    from .features import attack_envelope

    full, env_t = attack_envelope(y, sr, 30, 11000, 256)
    low, _ = attack_envelope(y, sr, None, 180, 1024)
    env = full / (full.max() + 1e-9) + 1.5 * low / (low.max() + 1e-9)

    period = 60.0 / grid.bpm
    duration = len(y) / sr
    k = np.arange(int((duration - grid.first_beat) / period) + 4) - 2
    width = period / 2 if full_search else 0.03
    shifts = np.arange(-width, width, 0.002)
    scores = []
    for s in shifts:
        t = grid.first_beat + s + k * period
        t = t[(t >= 0) & (t < duration)]
        scores.append(np.interp(t, env_t, env).sum())
    best = float(shifts[int(np.argmax(scores))])
    # keep the tempo-stability stats of the original fit; the tracker may have been on off-beats
    return Grid(grid.bpm, grid.first_beat + best, grid.residual_ms, grid.max_dev_ms, grid.stable)


def grid_downbeats(y: np.ndarray, sr: int, grid: Grid, duration: float, tracked: np.ndarray | None,
                   meter: int = 4) -> np.ndarray:
    """Downbeats on the fixed grid. Use the tracker's downbeats to vote for the bar phase if
    available, otherwise pick the phase with the strongest low-frequency onsets."""
    period = 60.0 / grid.bpm
    n0 = int(np.ceil(-grid.first_beat / period))
    beats = grid.first_beat + np.arange(n0, int((duration - grid.first_beat) / period) + 1) * period
    if tracked is not None and len(tracked) >= 4 and len(tracked) < len(beats) * 0.6:
        idx = np.round((tracked - grid.first_beat) / period).astype(int) - n0
        phase = int(np.bincount(idx % meter, minlength=meter).argmax())
        return beats[phase::meter]
    return _estimate_downbeats(y, sr, beats, meter)
