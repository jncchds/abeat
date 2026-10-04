"""Tempo grid and tempo map fitting on synthetic beat times (no audio, no ML models)."""

import numpy as np

from abeat_analysis import synth, tempo


def drifting_beats(n=320, bpm=112.0, drift=0.04, jitter=0.008, seed=1):
    truth = synth.beat_times(bpm, 0.37, n, drift)
    rng = np.random.default_rng(seed)
    return truth, truth + rng.normal(0, jitter, len(truth))


def test_constant_grid_recovers_bpm_and_phase():
    beats = 0.5 + np.arange(200) * 60 / 128 + np.random.default_rng(0).normal(0, 0.005, 200)
    g = tempo.fit_grid(beats)
    assert g.bpm == 128.0
    assert abs(g.first_beat - 0.5) < 0.01
    assert g.stable


def test_index_beats_skips_doubled_and_counts_dropped_beats():
    period = 0.5
    beats = list(np.arange(40) * period)
    beats += [20 + k * period / 2 for k in range(16)]  # a stretch tracked at double tempo
    beats = np.array(sorted(set(beats) - {5.0, 5.5}))  # two missed beats
    t, idx = tempo.index_beats(beats)
    assert np.allclose(t, idx * period)  # every kept beat numbered by its true position
    assert idx[-1] == round(t[-1] / period)


def test_tempo_map_follows_a_drifting_band():
    truth, tracked = drifting_beats()
    tm = tempo.fit_tempo_map(tracked)
    assert tm is not None and len(tm.changes) > 5
    err = np.abs(tm.time(np.arange(len(truth))) - truth)
    assert np.mean(err) < 0.01 and np.max(err) < 0.03
    const = tempo.fit_grid(tracked)
    assert not const.stable  # one BPM can't follow it


def test_tempo_map_of_a_steady_song_is_one_tempo():
    beats = 0.2 + np.arange(300) * 60 / 120
    tm = tempo.fit_tempo_map(beats)
    assert len(tm.changes) == 1
    assert abs(tm.bpm - 120) < 0.01


def drum_track(beats, seed=3):
    """Kick on every beat, hat on every off-beat, at the given beat times (synth's drum sounds)."""
    rng = np.random.default_rng(seed)
    y = np.zeros(int((beats[-1] + 1) * synth.SR), np.float32)
    k = synth.kick()
    for i, b in enumerate(beats):
        n = int(b * synth.SR)
        y[n:n + len(k)] += k[:len(y) - n]
        if i + 1 < len(beats):
            h = synth.hat(rng)
            m = int((b + beats[i + 1]) / 2 * synth.SR)
            y[m:m + len(h)] += h[:len(y) - m]
    return y


def test_drum_tempo_keeps_one_bpm_for_steady_drums():
    beats = 0.6 + np.arange(240) * 60 / 128
    dt = tempo.drum_tempo(drum_track(beats), synth.SR, 126.5)
    assert abs(dt.grid.bpm - 128) < 0.01
    period = 60 / 128
    phase = (dt.grid.first_beat - 0.6) % period
    assert min(phase, period - phase) < 0.006
    assert dt.gain < 1.15  # auto mode keeps one BPM


def test_drum_tempo_follows_a_slow_down_that_speeds_back_up():
    # 136 BPM, a sudden drop to 114 BPM that speeds back up over 32 beats, twice (as in Dara - Bangaranga)
    bpm = np.full(300, 136.0)
    for s in (80, 200):
        bpm[s:s + 32] = np.linspace(114, 136, 32)
    beats = 0.5 + np.concatenate([[0.0], np.cumsum(60 / bpm)])
    dt = tempo.drum_tempo(drum_track(beats), synth.SR, 136)
    assert dt.gain >= 1.15  # auto mode takes the map
    t = tempo.map_beats(dt.tmap, beats[-1])
    near = np.abs(beats[:, None] - t[None, :]).min(axis=1)
    assert np.mean(near) < 0.008 and np.percentile(near, 95) < 0.02
