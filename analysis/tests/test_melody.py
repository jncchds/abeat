import numpy as np

from abeat_analysis.melody import FPS, lead_path, segments


def test_lead_path_follows_the_clear_stem_and_rests_between():
    n = 6 * FPS
    lead = np.zeros((2, n))
    lead[0, : 2 * FPS] = 0.8          # vocals lead for 2 s
    lead[1, 3 * FPS: 5 * FPS] = 0.7   # then an instrument after a 1 s break
    lead[0, 3 * FPS: 5 * FPS] = 0.1   # with a little bleed in the vocal stem
    state = lead_path(lead)
    assert (state[10: 2 * FPS - 10] == 1).all()
    assert (state[2 * FPS + 20: 3 * FPS - 20] == 0).all()
    assert (state[3 * FPS + 10: 5 * FPS - 10] == 2).all()
    assert (state[5 * FPS + 20:] == 0).all()


def test_lead_path_does_not_flicker_on_short_dips():
    n = 3 * FPS
    lead = np.zeros((2, n))
    lead[1] = 0.6
    lead[1, FPS: FPS + 5] = 0.0       # 50 ms dropout (a bow change)
    lead[0, FPS: FPS + 5] = 0.5       # where the other stem pokes up
    assert (lead_path(lead)[5:-5] == 2).all()


def test_segments_bridge_short_gaps_and_drop_blips():
    state = np.zeros(10 * FPS, np.int8)
    state[0: 2 * FPS] = 2
    state[2 * FPS + 50: 4 * FPS] = 2      # 0.5 s rest: same segment
    state[6 * FPS: 6 * FPS + 30] = 2      # 0.3 s blip, 2 s after the last: dropped
    state[7 * FPS: 9 * FPS] = 1
    segs = segments(state, ["vocals", "other"])
    assert segs == [{"start": 0.0, "end": 4.0, "src": "other"}, {"start": 7.0, "end": 9.0, "src": "vocals"}]
