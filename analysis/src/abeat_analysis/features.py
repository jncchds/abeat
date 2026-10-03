"""Onsets per layer, energy curve and song structure (sections)."""

from __future__ import annotations

import librosa
import numpy as np
import scipy.ndimage
import scipy.signal
from scipy.cluster.hierarchy import fcluster, linkage

SR = 22050
HOP = 256  # ~11.6 ms; fine enough for 1/16 notes at 200 BPM

# Pseudo-stems used when no source separation is available: frequency bands of the mix.
BANDS = {
    "low": (20, 200),  # kick, bass
    "mid": (200, 3000),  # vocals, leads, snare body
    "high": (3000, 11000),  # hats, cymbals, transients
}


def _norm01(x: np.ndarray, lo_pct=5, hi_pct=95) -> np.ndarray:
    lo, hi = np.percentile(x, lo_pct), np.percentile(x, hi_pct)
    if hi - lo < 1e-9:
        return np.zeros_like(x)
    return np.clip((x - lo) / (hi - lo), 0, 1)


ENV_HOP = 64  # samples at the input rate (~1.5 ms at 44.1 kHz)


def attack_envelope(y: np.ndarray, sr: int, fmin: float | None = None, fmax: float | None = None,
                    win: int = 256) -> tuple[np.ndarray, np.ndarray]:
    """Half-wave rectified rise of log energy in a band, with centered windows.

    Unlike spectral flux on long STFT frames this peaks right at the attack (no ~50 ms lag), which
    matters because Beat Saber players feel timing errors of a few tens of ms.
    """
    nyq = sr / 2
    if fmin and fmax and fmax < nyq:
        sos = scipy.signal.butter(4, [fmin, fmax], btype="band", fs=sr, output="sos")
    elif fmax and fmax < nyq:
        sos = scipy.signal.butter(4, fmax, btype="low", fs=sr, output="sos")
    elif fmin:
        sos = scipy.signal.butter(4, fmin, btype="high", fs=sr, output="sos")
    else:
        sos = None
    x = scipy.signal.sosfiltfilt(sos, y) if sos is not None else y
    k = np.hanning(win)
    e = scipy.signal.fftconvolve(x.astype(np.float64) ** 2, k / k.sum(), mode="same")[::ENV_HOP]
    le = 10 * np.log10(np.maximum(e, 1e-12))
    d = np.maximum(np.diff(le, prepend=le[0]), 0)
    # ignore rises in near-silence (noise floor)
    loud = np.clip((le - (le.max() - 50)) / 30, 0, 1)
    d = d * loud
    # integrate the rise over ~6 ms so a sharp attack is one peak
    d = scipy.ndimage.uniform_filter1d(d, size=4)
    t = np.arange(len(d)) * ENV_HOP / sr
    return d, t


BAND_WIN = {"full": 256, "low": 1024, "mid": 512, "high": 128}


def detect_onsets(y: np.ndarray, sr: int, fmin: float | None = None, fmax: float | None = None,
                  win: int = 256, min_gap: float = 0.05) -> list[dict]:
    """Onsets with strength (0..1) and brightness (0..1, log spectral centroid) for one signal/band."""
    d, t = attack_envelope(y, sr, fmin, fmax, win)
    if d.max() <= 0:
        return []
    fps = sr / ENV_HOP
    # adaptive threshold: local mean over ~0.5 s plus a fraction of the global level
    local = scipy.ndimage.uniform_filter1d(d, size=int(0.5 * fps))
    thresh = local * 1.5 + np.percentile(d[d > 0], 75) * 0.5 if np.any(d > 0) else local
    peaks, _ = scipy.signal.find_peaks(d, height=thresh, distance=max(1, int(min_gap * fps)))
    if len(peaks) == 0:
        return []
    ref = np.percentile(d[peaks], 95)

    ya = librosa.resample(y, orig_sr=sr, target_sr=SR) if sr != SR else y
    centroid = librosa.feature.spectral_centroid(y=ya, sr=SR, hop_length=HOP)[0]
    log_c = np.clip(np.log2(np.maximum(centroid, 60) / 60) / np.log2(8000 / 60), 0, 1)
    cframes = np.minimum(librosa.time_to_frames(t[peaks] + 0.02, sr=SR, hop_length=HOP), len(log_c) - 1)
    return [
        {"t": round(float(t[p]), 4), "s": round(float(min(1.0, d[p] / ref)), 3), "br": round(float(log_c[c]), 3)}
        for p, c in zip(peaks, cframes)
    ]


def band_onsets(y: np.ndarray, sr: int) -> dict[str, list[dict]]:
    layers = {"full": detect_onsets(y, sr, 30, 11000, BAND_WIN["full"])}
    for name, (lo, hi) in BANDS.items():
        layers[name] = detect_onsets(y, sr, lo, hi, BAND_WIN[name])
    return layers


def energy_curve(y: np.ndarray, sr: int, hop_sec: float = 0.1) -> dict:
    hop = int(sr * hop_sec)
    rms = librosa.feature.rms(y=y, frame_length=hop * 4, hop_length=hop)[0]
    db = librosa.amplitude_to_db(rms + 1e-9)
    smooth = scipy.ndimage.uniform_filter1d(db, size=max(1, int(1.0 / hop_sec)))
    return {"hopSec": hop_sec, "values": [round(float(v), 3) for v in _norm01(smooth, 5, 99)]}


def sections(y: np.ndarray, sr: int, beats: np.ndarray, downbeats: np.ndarray, energy: dict,
             min_bars: int = 4) -> list[dict]:
    """Novelty-based segmentation on beat-synchronous timbre+harmony features, boundaries snapped to
    downbeats, then sections clustered into labels (A, B, C...) so repeated parts share a label."""
    duration = len(y) / sr
    if len(beats) < 16:
        return [{"start": 0.0, "end": round(duration, 3), "label": "A", "energy": 0.5}]
    if sr != SR:
        y = librosa.resample(y, orig_sr=sr, target_sr=SR)
    hop = 512
    mfcc = librosa.feature.mfcc(y=y, sr=SR, n_mfcc=13, hop_length=hop)
    chroma = librosa.feature.chroma_cqt(y=y, sr=SR, hop_length=hop)
    rms = librosa.feature.rms(y=y, hop_length=hop)
    frames = librosa.time_to_frames(beats, sr=SR, hop_length=hop)
    feat = np.vstack([
        librosa.util.normalize(librosa.util.sync(mfcc, frames), axis=1),
        librosa.util.sync(chroma, frames),
        librosa.util.normalize(librosa.util.sync(librosa.amplitude_to_db(rms), frames), axis=1),
    ])
    feat = scipy.ndimage.median_filter(feat, size=(1, 3))
    # cosine self-similarity
    f = feat / (np.linalg.norm(feat, axis=0, keepdims=True) + 1e-9)
    ssm = f.T @ f
    # checkerboard novelty, kernel spanning 8 beats each side
    k = 8
    g = np.outer(np.hanning(2 * k), np.hanning(2 * k))
    kernel = g * np.block([[np.ones((k, k)), -np.ones((k, k))], [-np.ones((k, k)), np.ones((k, k))]])
    n = ssm.shape[0]
    padded = np.pad(ssm, k, mode="edge")
    novelty = np.array([np.sum(padded[i:i + 2 * k, i:i + 2 * k] * kernel) for i in range(n)])
    novelty = np.maximum(novelty, 0)

    beat_times = np.concatenate([[0.0], beats])  # sync() prepends a segment from frame 0
    beat_times = beat_times[:n]
    beats_per_bar = 4
    min_gap = min_bars * beats_per_bar
    peaks, _ = scipy.signal.find_peaks(novelty, distance=min_gap, height=np.percentile(novelty, 60))
    bounds = [0.0]
    for p in peaks:
        t = float(beat_times[p])
        if len(downbeats):
            t = float(downbeats[np.argmin(np.abs(downbeats - t))])
        if t - bounds[-1] >= min_bars * 4 * 60 / _bpm(beats) * 0.9:
            bounds.append(t)
    bounds.append(duration)
    if duration - bounds[-2] < 4 * 60 / _bpm(beats):  # avoid tiny trailing section
        bounds.pop(-2)

    # summarize each section, cluster into labels
    seg_feats, seg_energy = [], []
    ev = np.asarray(energy["values"])
    hop_sec = energy["hopSec"]
    for a, b in zip(bounds[:-1], bounds[1:]):
        mask = (beat_times >= a) & (beat_times < b)
        seg_feats.append(feat[:, mask].mean(axis=1) if mask.any() else feat.mean(axis=1))
        ea, eb = int(a / hop_sec), max(int(a / hop_sec) + 1, int(b / hop_sec))
        seg_energy.append(float(ev[ea:eb].mean()) if ea < len(ev) else 0.0)
    labels = _cluster(np.array(seg_feats))
    return [
        {"start": round(a, 3), "end": round(b, 3), "label": lab, "energy": round(e, 3)}
        for a, b, lab, e in zip(bounds[:-1], bounds[1:], labels, seg_energy)
    ]


def _bpm(beats: np.ndarray) -> float:
    return 60.0 / float(np.median(np.diff(beats)))


def _cluster(x: np.ndarray, threshold: float = 0.05) -> list[str]:
    if len(x) == 1:
        return ["A"]
    xn = x / (np.linalg.norm(x, axis=1, keepdims=True) + 1e-9)
    z = linkage(xn, method="average", metric="cosine")
    ids = fcluster(z, t=threshold, criterion="distance")
    # relabel in order of first appearance: A, B, C...
    mapping: dict[int, str] = {}
    out = []
    for i in ids:
        if i not in mapping:
            mapping[i] = chr(ord("A") + len(mapping))
        out.append(mapping[i])
    return out
