"""Syllable-level onsets for the vocal stem.

Two methods (both need the "ml" extra; "lyrics" also the "lyrics" extra):

* notes  - spectral-flux onsets kept only where CREPE hears a pitched voice right after them (drops
           breaths, consonants and bleed), with the sung pitch steering the note row.
* lyrics - torchaudio's MMS forced aligner times every character of the lyrics (pasted by the user,
           or transcribed from the stem by Whisper), and each word is split into syllables at its
           vowel groups.

Lyric syllables are placed at their vowel (snapped to the nearest vocal attack): the moment a sung
syllable is heard "on the beat" (its perceptual centre) is close to where the vowel starts, not where
the consonant before it does.
"""

from __future__ import annotations

import re
import unicodedata
from typing import Callable

import librosa
import numpy as np
import scipy.ndimage

SR16 = 16000
HOP = 160  # 10 ms

Log = Callable[[str], None]


def _onset(t: float, s: float, br: float) -> dict:
    return {"t": round(float(t), 4), "s": round(float(min(1.0, max(0.0, s))), 3), "br": round(float(min(1.0, max(0.0, br))), 3)}


def _pitch(y16: np.ndarray, log: Log) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """Per 10 ms frame: MIDI pitch, periodicity (voicing confidence) and A-weighted loudness (dB)."""
    import torch
    import torchcrepe

    audio = torch.from_numpy(np.ascontiguousarray(y16)).float()[None]
    log("vocal pitch (CREPE tiny)")  # "full" is ~25x slower on CPU for little gain in onset timing
    with torch.no_grad():
        f0, per = torchcrepe.predict(audio, SR16, HOP, 50.0, 1100.0, model="tiny", decoder=torchcrepe.decode.viterbi,
                                     return_periodicity=True, batch_size=1024, device="cpu")
        per = torchcrepe.filter.median(per, 3)
        loud = torchcrepe.loudness.a_weighted(audio, SR16, HOP)
    f0, per, loud = f0[0].numpy(), per[0].numpy(), loud[0].numpy()
    n = min(len(f0), len(per), len(loud))
    midi = 69 + 12 * np.log2(np.maximum(f0[:n], 1e-3) / 440.0)
    return midi, per[:n], loud[:n]


def note_onsets(y: np.ndarray, sr: int, log: Log) -> list[dict]:
    """Spectral-flux onsets of the stem kept only where the voice is pitched right after them, with the
    sung pitch as brightness (so the note row follows the melody).

    Segmenting notes from CREPE alone timed onsets poorly (its 64 ms window blurs where a note starts),
    while flux times them well but also fires on breaths, consonants and bleed from other instruments.
    Filtering by voicing keeps the well-timed onsets of actual sung syllables: on a reference song 83 %
    of them land on a human-mapped note, against 72 % for unfiltered flux."""
    from .features import tonal_onsets

    y16 = librosa.resample(y, orig_sr=sr, target_sr=SR16) if sr != SR16 else y
    midi, per, loud = _pitch(y16, log)
    fps = SR16 / HOP
    voiced = (per > 0.45) & (loud > loud.max() - 45)
    voiced = scipy.ndimage.binary_closing(voiced, np.ones(3))  # bridge 1-2 frame dropouts

    out = []
    for o in tonal_onsets(y, sr, refine=False):
        i = int(o["t"] * fps)
        ahead = voiced[i:i + int(0.08 * fps)]
        if len(ahead) == 0 or ahead.mean() < 0.5:
            continue
        note = slice(i, i + int(0.15 * fps))
        pitch = np.median(midi[note][voiced[note]])
        out.append(_onset(o["t"], o["s"], (pitch - 45) / 30))  # low notes low, high notes high
    return out


# ── lyrics ──

_VOWELS = set("aeiouy")


def _normalize(word: str) -> str:
    w = unicodedata.normalize("NFKD", word.lower())
    return re.sub(r"[^a-z']", "", w).strip("'")


def _syllable_starts(word: str) -> list[int]:
    """Indices of the first letter of each vowel group (y counts as a vowel except word-initially);
    a final silent "e" after a consonant does not make a syllable ("cake", but not "the" or "be")."""
    groups = []
    k = 0
    while k < len(word):
        if word[k] in _VOWELS and not (word[k] == "y" and k == 0):
            groups.append(k)
            while k < len(word) and word[k] in _VOWELS:
                k += 1
        else:
            k += 1
    if len(groups) > 1 and word.endswith("e") and groups[-1] == len(word) - 1 and word[-2] not in _VOWELS and not word.endswith("le"):
        groups.pop()
    return groups or [0]


def lyric_onsets(y: np.ndarray, sr: int, log: Log, model_size: str = "small", text: str | None = None) -> tuple[list[dict], dict]:
    """Syllable onsets plus the lyrics (words with times) for display. With `text` the given lyrics
    are aligned to the whole stem; otherwise Whisper transcribes it first."""
    y16 = (librosa.resample(y, orig_sr=sr, target_sr=SR16) if sr != SR16 else y).astype(np.float32)
    env_db = librosa.amplitude_to_db(librosa.feature.rms(y=y16, hop_length=HOP)[0] + 1e-9)
    lo, hi = np.percentile(env_db, [20, 99])

    def level(t: float) -> float:
        return (env_db[min(len(env_db) - 1, int(t * SR16 / HOP))] - lo) / max(1e-6, hi - lo)

    if text and text.strip():
        words, info = _align_text(y16, text, log), {"source": "user", "language": None}
    else:
        words, info = _transcribe_and_align(y16, log, model_size)

    # CTC aligners mark a letter where their output peaks, ~70 ms after it sounds; snap each syllable to
    # the strongest vocal attack shortly before that (or just shift it when there is none)
    from .features import detect_onsets

    attacks = detect_onsets(y, sr, 150, 4000, 512, 0.04)
    at = np.array([o["t"] for o in attacks])
    a_s = np.array([o["s"] for o in attacks])

    def snap(t: float) -> float:
        m = (at >= t - 0.15) & (at <= t + 0.03)
        return float(at[m][np.argmax(a_s[m])]) if m.any() else t - 0.07

    onsets: list[dict] = []
    for w in words:
        for k in _syllable_starts(w["norm"]):
            t = snap(w["letters"][k])
            onsets.append(_onset(t, 0.5 * level(t) + 0.5 * w["score"], 0.5))
    onsets.sort(key=lambda o: o["t"])
    # drop duplicates closer than 40 ms (aligner collisions)
    dedup: list[dict] = []
    for o in onsets:
        if dedup and o["t"] - dedup[-1]["t"] < 0.04:
            if o["s"] > dedup[-1]["s"]:
                dedup[-1] = o
            continue
        dedup.append(o)
    lyrics = {**info, "words": [{"w": w["w"], "t": round(w["t"], 3), "e": round(w["e"], 3)} for w in words]}
    return dedup, lyrics


def _mms():
    import torchaudio

    bundle = torchaudio.pipelines.MMS_FA
    return bundle


def _emission(model, y16: np.ndarray, chunk_sec: float = 20.0):
    """MMS emissions for the whole signal, computed in chunks (attention cost grows with length)."""
    import torch

    parts = []
    step = int(chunk_sec * SR16)
    with torch.inference_mode():
        for a in range(0, len(y16), step):
            clip = torch.from_numpy(y16[a:a + step])[None]
            if clip.shape[1] < SR16 // 10:
                break
            e, _ = model(clip)
            parts.append(e[0])
    em = torch.cat(parts)
    return em, len(y16) / SR16 / em.shape[0]


def _align_text(y16: np.ndarray, text: str, log: Log) -> list[dict]:
    """Aligns user lyrics to the whole stem. A star token between lines soaks up ad-libs, repeats
    missing from the text and instrumental parts."""
    import torch
    import torchaudio.functional as AF

    bundle = _mms()
    model = bundle.get_model(with_star=True).eval()
    dictionary = bundle.get_dict(star="*")
    log("aligning the given lyrics to the vocals (MMS forced aligner)")
    em, spf = _emission(model, y16)

    raw_words: list[str] = []
    tokens: list[int] = [dictionary["*"]]
    word_tokens: list[tuple[int, int]] = []  # (first token index, count) per word
    for line in text.splitlines():
        for raw in line.split():
            norm = _normalize(raw)
            if not norm or any(c not in dictionary for c in norm):
                norm = "".join(c for c in norm if c in dictionary)
                if not norm:
                    continue
            word_tokens.append((len(tokens), len(norm)))
            tokens.extend(dictionary[c] for c in norm)
            raw_words.append(raw)
        tokens.append(dictionary["*"])
    if not raw_words:
        return []
    if len(tokens) > em.shape[0]:
        log("lyrics are longer than the audio allows; aligning the first part only")
    targets = torch.tensor([tokens[:em.shape[0]]], dtype=torch.int32)
    labels, scores = AF.forced_align(em[None], targets, blank=0)
    labels, scores = labels[0], scores[0].exp()
    spans = AF.merge_tokens(labels, scores)  # one span per target token, in order
    out = []
    for raw, (first, count) in zip(raw_words, word_tokens):
        if first + count > len(spans):
            break
        sp = spans[first:first + count]
        letters = [s.start * spf for s in sp]
        out.append({"w": raw, "norm": _normalize(raw), "letters": letters, "t": letters[0], "e": sp[-1].end * spf,
                    "score": float(np.mean([s.score for s in sp]))})
    # words whose letters were dropped by the dictionary filter would misindex syllables
    return [w for w in out if len(w["letters"]) == len(w["norm"])]


def _transcribe_and_align(y16: np.ndarray, log: Log, model_size: str) -> tuple[list[dict], dict]:
    import torch
    from faster_whisper import WhisperModel

    log(f"transcribing vocals (whisper {model_size})")
    wm = WhisperModel(model_size, device="cpu", compute_type="int8")
    segments, info = wm.transcribe(y16, word_timestamps=True, condition_on_previous_text=False, vad_filter=False, beam_size=5)
    segments = list(segments)
    log(f"lyrics: {sum(len(s.words or []) for s in segments)} words ({info.language})")

    bundle = _mms()
    model = bundle.get_model(with_star=False).eval()
    tokenizer, aligner = bundle.get_tokenizer(), bundle.get_aligner()
    out: list[dict] = []
    for seg in segments:
        words = [w for w in (seg.words or []) if _normalize(w.word)]
        if not words:
            continue
        a = max(0.0, words[0].start - 0.4)
        b = min(len(y16) / SR16, words[-1].end + 0.4)
        clip = torch.from_numpy(y16[int(a * SR16):int(b * SR16)])[None]
        norm = [_normalize(w.word) for w in words]
        try:
            with torch.inference_mode():
                emission, _ = model(clip)
                spans = aligner(emission[0], tokenizer(norm))
            spf = clip.shape[1] / emission.shape[1] / SR16
        except Exception:
            spans = None  # alignment failed (e.g. more letters than audio frames): fall back to word times
        for wi, (w, text) in enumerate(zip(words, norm)):
            if spans is not None and len(spans[wi]) == len(text):
                letters = [a + sp.start * spf for sp in spans[wi]]
                score, end = float(np.mean([sp.score for sp in spans[wi]])), a + spans[wi][-1].end * spf
            else:
                letters = [w.start + (w.end - w.start) * k / max(1, len(text)) for k in range(len(text))]
                score, end = 0.5, w.end
            out.append({"w": w.word.strip(), "norm": text, "letters": letters, "t": letters[0], "e": end, "score": score})
    return out, {"source": f"whisper-{model_size}", "language": info.language}
