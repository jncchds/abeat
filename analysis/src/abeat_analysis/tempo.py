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

    from .accel import device

    a2b = Audio2Beats(checkpoint_path="final0", device=device(), dbn=False)
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
    """Downbeats on the fixed grid (see `downbeats_on`)."""
    period = 60.0 / grid.bpm
    n0 = int(np.ceil(-grid.first_beat / period))
    beats = grid.first_beat + np.arange(n0, int((duration - grid.first_beat) / period) + 1) * period
    return downbeats_on(y, sr, beats, tracked, meter)


def downbeats_on(y: np.ndarray, sr: int, beats: np.ndarray, tracked: np.ndarray | None, meter: int = 4) -> np.ndarray:
    """Every `meter`-th grid beat, at the bar phase the tracker's downbeats vote for if available,
    otherwise the phase with the strongest low-frequency onsets."""
    if tracked is not None and len(tracked) >= 4 and len(tracked) < len(beats) * 0.6:
        idx = np.clip(np.searchsorted(beats, tracked), 1, len(beats) - 1)
        idx = np.where(np.abs(beats[idx - 1] - tracked) < np.abs(beats[idx] - tracked), idx - 1, idx)
        phase = int(np.bincount(idx % meter, minlength=meter).argmax())
        return beats[phase::meter]
    return _estimate_downbeats(y, sr, beats, meter)


@dataclass
class TempoMap:
    """Piecewise-constant tempo over integer beat indices of the tracked beats: from beat
    `changes[k][0]` on (relative to tracked beat 0) the tempo is `changes[k][1]` BPM. `first_beat` is the
    time of beat 0; beats before it continue the first tempo."""

    first_beat: float
    changes: list[tuple[int, float]]
    residual_ms: float
    max_dev_ms: float

    @property
    def bpm(self) -> float:
        return self.changes[0][1]

    def time(self, beat: np.ndarray | float) -> np.ndarray:
        beat = np.asarray(beat, float)
        t = np.full(beat.shape, self.first_beat)
        starts = [b for b, _ in self.changes] + [np.inf]
        acc = self.first_beat
        for k, (b0, bpm) in enumerate(self.changes):
            lo = -np.inf if k == 0 else b0
            hi = starts[k + 1]
            m = (beat >= lo) & (beat < hi)
            t[m] = acc + (beat[m] - b0) * 60.0 / bpm
            if np.isfinite(hi):
                acc += (hi - b0) * 60.0 / bpm
        return t

    def shifted(self, s: float) -> "TempoMap":
        return TempoMap(self.first_beat + s, self.changes, self.residual_ms, self.max_dev_ms)


def index_beats(beats: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """Integer beat numbers for tracked beats, counting skipped beats where an interval spans several
    periods and dropping off-beat or doubled detections (under 0.75 of a period after the last kept beat).
    The period follows the kept beats (median of the last 8 steps), clipped to -20/+25 % of the song's
    median interval, so stretches tracked at double tempo (busy outros) don't redefine the beat."""
    song = float(np.median(np.diff(beats)))
    keep, idx, steps = [0], [0], [song]
    for i in range(1, len(beats)):
        local = float(np.clip(np.median(steps[-8:]), 0.8 * song, 1.25 * song))
        ratio = (beats[i] - beats[keep[-1]]) / local
        if ratio < 0.75:
            continue
        n = max(1, int(round(ratio)))
        steps.append((beats[i] - beats[keep[-1]]) / n)
        keep.append(i)
        idx.append(idx[-1] + n)
    return beats[np.array(keep)], np.array(idx)


def fit_tempo_map(beats: np.ndarray, knot_beats: int = 4, smooth: float = 3.0, merge_ms: float = 10.0,
                  audio: tuple[np.ndarray, int] | None = None, grid: Grid | None = None) -> TempoMap | None:
    """Piecewise-constant tempo for drifting (live, unquantized) recordings.

    Beat time as a function of beat number is fitted with a piecewise-linear curve (a knot every
    `knot_beats` beats, so one tempo per bar), robustly (iteratively reweighted, so mis-tracked beats
    don't bend it) and with a penalty on tempo changes between neighbouring bars (tracker jitter of
    ~10-20 ms would otherwise show up as tempo noise). Neighbouring bars are then merged greedily into
    one tempo as long as no tracked beat moves by more than `merge_ms` from the fitted curve's time.

    With `audio` (mono, sr) every knot is then nudged (up to +-40 ms) to put the beats on the audio's
    kick-weighted attacks (`_lock_knots`): the tracker's 20 ms frames bias per-bar tempos, and over a long
    steady stretch after a tempo change that drifts the grid off the beat.

    With `grid` (the song's constant grid) and `audio`, the result is the constant grid except where the
    curve sits clearly better on the audio's attacks (`_keep_grid`): beat trackers wander by 100 ms and
    more in some songs even where the tempo is steady, and only the stretches where the music really
    leaves one BPM (a slow part speeding up, a live band drifting) should bend the grid."""
    if len(beats) < 4 * knot_beats:
        return None
    t, idx = index_beats(beats)
    n_knots = int(np.ceil(idx[-1] / knot_beats)) + 1
    # design matrix: linear interpolation between knot times
    pos = idx / knot_beats
    j = np.minimum(np.floor(pos).astype(int), n_knots - 2)
    f = pos - j
    A = np.zeros((len(t), n_knots))
    A[np.arange(len(t)), j] = 1 - f
    A[np.arange(len(t)), j + 1] = f
    D = np.zeros((n_knots - 2, n_knots))
    for k in range(n_knots - 2):
        D[k, k:k + 3] = [1, -2, 1]
    w = np.ones(len(t))
    for _ in range(8):
        W = A * w[:, None]
        T = np.linalg.solve(A.T @ W + smooth * D.T @ D, W.T @ t)
        r = np.abs(t - A @ T)
        w = np.where(r < 0.025, 1.0, (0.025 / np.maximum(r, 1e-9)) ** 2)  # mis-tracked beats fade out
    if audio is not None:
        T = _lock_knots(T, knot_beats, *audio)
        if grid is not None:
            T = _keep_grid(T, knot_beats, grid, *audio)

    # merge bars into constant-tempo segments while the curve's beat times stay within merge_ms
    knot_beat = np.arange(n_knots) * knot_beats
    segs = []  # (first knot, last knot)
    a = 0
    while a < n_knots - 1:
        b = a + 1
        while b < n_knots - 1:
            ts = T[a] + (T[b + 1] - T[a]) * (np.arange(a, b + 2) - a) / (b + 1 - a)
            if np.max(np.abs(ts - T[a:b + 2])) * 1000 > merge_ms:
                break
            b += 1
        segs.append((a, b))
        a = b
    changes = []
    for a, b in segs:
        bpm = round(60.0 * (knot_beat[b] - knot_beat[a]) / (T[b] - T[a]), 3)
        if changes and abs(bpm - changes[-1][1]) < 1e-3:
            continue
        changes.append((int(knot_beat[a]), bpm))
    tm = TempoMap(float(T[0]), changes, 0.0, 0.0)
    dev = (t - tm.time(idx))[w > 0.5]
    return TempoMap(float(T[0]), changes, float(np.sqrt(np.mean(dev ** 2)) * 1000), float(np.percentile(np.abs(dev), 98) * 1000))


def _keep_grid(T: np.ndarray, knot_beats: int, grid: Grid, y: np.ndarray, sr: int) -> np.ndarray:
    """The constant grid, except in bars where the fitted curve sits clearly better on the audio.

    Per bar, the attack energy at the beats and off-beats of the (audio-locked) curve is compared with
    the constant grid's beats nearest in time; bars where the curve scores >= 110 % (median filter over
    three bars) keep the curve, all others take the grid. Knots between two grid bars sit exactly on the
    grid, so steady stretches keep one exact tempo; the curve's knots inside its runs are kept."""
    import scipy.ndimage
    import scipy.signal

    env, et = _beat_envelope(y, sr)
    env = scipy.ndimage.maximum_filter1d(env, size=max(1, int(0.03 / (et[1] - et[0]))))
    period = 60.0 / grid.bpm
    G = grid.first_beat + np.round((T - grid.first_beat) / period) * period
    half = np.arange(2 * knot_beats) / (2 * knot_beats)

    def bar(times: np.ndarray, a: int) -> float:
        t = times[a] + (times[a + 1] - times[a]) * half
        return float(np.interp(t, et, env, left=0, right=0).sum())

    curve_bar = np.array([bar(T, a) >= 1.1 * bar(G, a) for a in range(len(T) - 1)], float)
    curve_bar = scipy.signal.medfilt(curve_bar, 3) > 0.5
    use_curve = np.zeros(len(T), bool)
    use_curve[1:-1] = curve_bar[:-1] & curve_bar[1:]  # knots inside a run of curve bars
    return np.where(use_curve, T, G)


def _lock_knots(T: np.ndarray, knot_beats: int, y: np.ndarray, sr: int, max_ms: float = 40.0,
                frozen: np.ndarray | None = None) -> np.ndarray:
    """Coordinate ascent on the knot times: each knot moves to where the beats of its two neighbouring
    bars collect the most attack energy (envelope max within +-10 ms of each beat), with a small
    penalty on tempo changes so silence or a free-time break doesn't make the curve wander."""
    import scipy.ndimage

    env, et = _beat_envelope(y, sr)
    hop = et[1] - et[0]
    env = scipy.ndimage.maximum_filter1d(env, size=max(1, int(0.02 / hop)))
    T = T.copy()
    T0 = T.copy()
    frac = np.arange(knot_beats) / knot_beats

    def bar_score(a: int) -> float:  # beats of the bar between knots a and a+1
        if a < 0 or a + 1 >= len(T):
            return 0.0
        tb = T[a] + (T[a + 1] - T[a]) * frac
        return float(np.interp(tb, et, env, left=0, right=0).sum())

    def bend(a: int) -> float:  # tempo change around knot a, in ms of period per beat
        if a <= 0 or a + 1 >= len(T):
            return 0.0
        return ((T[a + 1] - T[a]) - (T[a] - T[a - 1])) / knot_beats * 1000

    for step in (0.008, 0.004, 0.002):
        for _ in range(3):
            moved = False
            for a in range(len(T)):
                if frozen is not None and frozen[a]:
                    continue
                lo = T[a - 1] + 0.2 if a > 0 else -np.inf
                hi = T[a + 1] - 0.2 if a + 1 < len(T) else np.inf
                best, best_val = T[a], None
                for cand in T[a] + np.arange(-3, 4) * step:
                    if not (lo < cand < hi) or abs(cand - T0[a]) > max_ms / 1000:
                        continue
                    old, T[a] = T[a], cand
                    val = bar_score(a - 1) + bar_score(a) - 0.002 * sum(bend(k) ** 2 for k in (a - 1, a, a + 1))
                    T[a] = old
                    if best_val is None or val > best_val + 1e-9:
                        best, best_val = cand, val
                if best != T[a]:
                    T[a], moved = best, True
            if not moved:
                break
    return T


def _beat_envelope(y: np.ndarray, sr: int) -> tuple[np.ndarray, np.ndarray]:
    from .features import attack_envelope

    full, env_t = attack_envelope(y, sr, 30, 11000, 256)
    low, _ = attack_envelope(y, sr, None, 180, 1024)
    return full / (full.max() + 1e-9) + 1.5 * low / (low.max() + 1e-9), env_t


def _map_beats(tm: TempoMap, duration: float) -> np.ndarray:
    last = int(tm.changes[-1][0] + (duration - tm.time(tm.changes[-1][0])) * tm.changes[-1][1] / 60) + 2
    return tm.time(np.arange(-4, last))


def refine_map_phase(y: np.ndarray, sr: int, tm: TempoMap, width: float = 0.03) -> TempoMap:
    """Shift a tempo map by up to +-width s to put the most kick-weighted attack energy on its beats."""
    env, env_t = _beat_envelope(y, sr)
    duration = len(y) / sr
    grid = _map_beats(tm, duration)
    best, best_score = 0.0, -1.0
    for s in np.arange(-width, width, 0.002):
        g = grid + s
        g = g[(g >= 0) & (g < duration)]
        score = np.interp(g, env_t, env).sum()
        if score > best_score:
            best, best_score = float(s), score
    return tm.shifted(best)


def on_beat_energy(y: np.ndarray, sr: int, beats: np.ndarray) -> float:
    """Mean kick-weighted attack energy within 15 ms of the given beat times: how well a grid sits on
    the music, independent of where the tracker happened to put its beats."""
    env, env_t = _beat_envelope(y, sr)
    beats = beats[(beats >= 0.02) & (beats < len(y) / sr - 0.02)]
    if len(beats) == 0:
        return 0.0
    near = np.stack([np.interp(beats + d, env_t, env) for d in np.arange(-0.015, 0.0151, 0.003)])
    return float(near.max(axis=0).mean())


def grid_beats(grid: Grid, duration: float) -> np.ndarray:
    period = 60.0 / grid.bpm
    n0 = int(np.ceil(-grid.first_beat / period))
    return grid.first_beat + np.arange(n0, int((duration - grid.first_beat) / period) + 1) * period


def map_beats(tm: TempoMap, duration: float) -> np.ndarray:
    return _map_beats(tm, duration)
