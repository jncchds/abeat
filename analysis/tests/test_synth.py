"""The synthetic test track is deterministic and its drift truth is consistent."""

import numpy as np

from abeat_analysis import synth


def test_beat_times_without_drift_are_a_grid():
    b = synth.beat_times(128, 0.37, 64)
    assert np.allclose(np.diff(b), 60 / 128)


def test_drift_wanders_both_ways():
    bpm = 60 / np.diff(synth.beat_times(112, 0.37, 320, 0.04))
    assert bpm.min() < 110 and bpm.max() > 116
