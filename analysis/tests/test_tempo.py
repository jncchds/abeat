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
