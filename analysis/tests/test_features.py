"""Drum labels and lyric-based section labels."""

import numpy as np

from abeat_analysis import features, synth

SR = 44100


def test_drum_hits_are_labelled_kick_snare_hat():
    rng = np.random.default_rng(7)
    beat = 60 / 128
    out = np.zeros(int(40 * SR))
    truth = []

    def add(sig, t, gain):
        i = int(t * SR)
        out[i:i + len(sig)] += sig[: len(out) - i] * gain

    kick, snare = synth.kick(), synth.snare(rng)
    for b in range(64):
        t = 1 + b * beat
        if b % 2 == 0:
            add(kick, t, 0.9)
            truth.append((t, "k"))
        else:
            add(snare, t, 0.5)
            truth.append((t, "s"))
        add(synth.hat(rng), t + beat / 2, 0.6)
        truth.append((t + beat / 2, "h"))
    onsets = features.drum_onsets(out.astype(np.float32), SR)
    labelled = right = 0
    for t, kind in truth:
        near = [o for o in onsets if abs(o["t"] - t) < 0.03]
        if near:
            labelled += 1
            right += near[0]["k"][0] == kind
    assert labelled >= 0.7 * len(truth)
    assert right >= 0.95 * labelled


def _sections(parts):
    words, secs, t = [], [], 0.0
    for label, text in parts:
        start = t
        for w in text.split():
            words.append({"w": w, "t": t, "e": t + 0.3})
            t += 0.5
        t += 1
        secs.append({"start": start, "end": t, "label": label, "energy": 0.5})
    return secs, words


def test_returning_chorus_gets_one_label():
    chorus = "we are the champions my friends and we keep on fighting till the end"
    secs, words = _sections([
        ("A", "i paid my dues time after time i done my sentence but committed no crime"),
        ("B", chorus),
        ("A", "i took my bows and my curtain calls you brought me fame and fortune"),
        ("C", chorus),  # arranged differently, so the audio clustering missed it
        ("D", "la la la la la la la"),
    ])
    assert [s["label"] for s in features.merge_lyric_repeats(secs, words)] == ["A", "B", "A", "B", "C"]


def test_different_words_never_split_a_label():
    secs, words = _sections([("A", "one two three four five six seven"), ("A", "eight nine ten eleven twelve thirteen fourteen")])
    assert [s["label"] for s in features.merge_lyric_repeats(secs, words)] == ["A", "A"]
