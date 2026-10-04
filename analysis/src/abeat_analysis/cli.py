"""abeat-analyze: audio file -> work dir with analysis.json, song.egg, cover.jpg.

All times in analysis.json are in seconds of the *output* audio (song.egg), which is padded at the
start so that tempo-grid beat 0 lands exactly at t=0. Beat Saber beat = time * bpm / 60.
"""

from __future__ import annotations

import argparse
import json
import math
import sys
import time
from pathlib import Path

import numpy as np

from . import audio as audio_mod
from . import cover, features, tempo

SCHEMA_VERSION = 1
MIN_LEAD_IN_SEC = 2.0


def log(msg: str) -> None:
    print(f"[abeat-analyze] {msg}", file=sys.stderr, flush=True)


def analyze(args: argparse.Namespace) -> int:
    from . import fetch

    out = Path(args.out).expanduser().resolve()
    out.mkdir(parents=True, exist_ok=True)
    t0 = time.time()

    source_url = None
    if fetch.is_url(args.input):
        got = fetch.fetch(args.input, out / "download", log)
        src, source_url = got.path, got.url
        meta = audio_mod.Metadata(title=got.title, artist=got.artist, cover=got.cover)
    else:
        src = Path(args.input).expanduser().resolve()
        meta = audio_mod.read_metadata(src)
    if args.title:
        meta.title = args.title
    if args.artist:
        meta.artist = args.artist
    log(f"loading {src.name}")
    raw = audio_mod.load_audio(src)
    mono = raw.mono

    log(f"tracking beats (backend={args.beats})")
    tr = tempo.track_beats(mono, raw.sr, args.beats)
    grid = tempo.fit_grid(tr.beats, args.bpm)
    grid = tempo.refine_phase(mono, raw.sr, grid, tr.beats, full_search=tr.backend != "beat_this")
    tracked_db = tr.downbeats if tr.backend == "beat_this" else None
    log(f"bpm={grid.bpm:.2f} first_beat={grid.first_beat:.3f}s residual={grid.residual_ms:.1f}ms "
        f"p98={grid.max_dev_ms:.0f}ms ({tr.backend})")
    tmap = _tempo_map(args, tr.beats, grid, mono, raw.sr)

    # Pad so grid beat 0 is at t=0, plus whole beats of lead-in if the music starts immediately.
    first_beat = tmap.first_beat if tmap else grid.first_beat
    period = 60.0 / (tmap.bpm if tmap else grid.bpm)
    pad = (-first_beat) % period
    first_sound = _first_sound(mono, raw.sr)
    if first_sound + pad < MIN_LEAD_IN_SEC:
        pad += math.ceil((MIN_LEAD_IN_SEC - first_sound - pad) / period) * period
    changes = [{"beat": 0, "bpm": round(grid.bpm, 4)}]
    if tmap:
        # beat numbers on the padded audio: tracked beat 0 is grid beat n0; earlier beats keep the first tempo
        n0 = int(round((first_beat + pad) / period))
        changes = [{"beat": 0 if k == 0 else b + n0, "bpm": bpm} for k, (b, bpm) in enumerate(tmap.changes)]
        k = np.arange(-n0, int(tmap.changes[-1][0] + (raw.duration - tmap.time(tmap.changes[-1][0])) / 60 * tmap.changes[-1][1]) + 2)
        grid_beats = tmap.time(k)
        grid_db = tempo.downbeats_on(mono, raw.sr, grid_beats[(grid_beats > -period / 2) & (grid_beats < raw.duration)], tracked_db)
    else:
        grid_db = tempo.grid_downbeats(mono, raw.sr, grid, raw.duration, tracked_db)
    padded = audio_mod.pad_start(raw, pad)
    y = padded.mono
    sr = padded.sr
    beats, downbeats = tr.beats + pad, grid_db + pad

    layer_source = "bands"
    st = None
    lyrics = None
    vocal_source = None
    if args.stems == "demucs":
        st = _cached_stems(out / "stems", len(y), sr)
        if st is not None:
            log("reusing separated stems from the work dir")
        else:
            try:
                from . import stems

                log("separating stems with demucs (slow on CPU)")
                st = stems.separate(padded.stereo, sr)
            except ImportError:
                log("demucs not installed (ML extra missing); falling back to frequency bands")
    if st is not None:
        # "mix" (not "full") so stem analyses get their own, low, weight for the whole mix
        layers = {"mix": features.detect_onsets(y, sr, 30, 11000)}
        for name, sig in st.items():
            if name == "drums":
                layers[name] = features.drum_onsets(sig, sr)
            else:
                layers[name] = features.tonal_onsets(sig, sr, refine=name != "vocals")
        layer_source = "demucs"
        vocal_source = "flux"
        if "vocals" in st and args.vocals != "flux":
            try:
                from . import vocals

                if args.vocals == "notes":
                    layers["vocals"] = vocals.note_onsets(st["vocals"], sr, log)
                else:
                    text = Path(args.lyrics_file).read_text(encoding="utf-8") if args.lyrics_file else None
                    layers["vocals"], lyrics = vocals.lyric_onsets(st["vocals"], sr, log, args.whisper_model, text)
                vocal_source = args.vocals
            except ImportError as e:
                log(f"vocal {args.vocals} needs an optional extra ({e.name} missing); keeping spectral-flux vocal onsets")
        if args.keep_stems:
            import soundfile as sf

            (out / "stems").mkdir(exist_ok=True)
            for name, sig in st.items():
                # FLAC, not Ogg: libsndfile's Vorbis encoder segfaults on long mono stems
                sf.write(str(out / "stems" / f"{name}.flac"), sig, sr, format="FLAC", subtype="PCM_16")
    else:
        log("detecting onsets per frequency band")
        layers = features.band_onsets(y, sr)

    log("energy + sections")
    energy = features.energy_curve(y, sr)
    secs = features.sections(y, sr, beats, downbeats, energy)

    log("writing song.egg + cover.jpg")
    audio_mod.write_egg(padded, out / "song.egg")
    cover.write_cover(out / "cover.jpg", meta.cover, y, sr, meta.title, meta.artist)

    loudest = max(secs, key=lambda s: s["energy"])
    result = {
        "schemaVersion": SCHEMA_VERSION,
        "source": {"path": str(src), "url": source_url, "title": meta.title, "artist": meta.artist},
        "audio": {"file": "song.egg", "durationSec": round(padded.duration, 3), "sampleRate": sr,
                  "padSec": round(pad, 4)},
        "tempo": {
            "bpm": changes[0]["bpm"],
            "firstBeatSec": round(first_beat + pad, 4),
            "residualMs": round(tmap.residual_ms if tmap else grid.residual_ms, 2),
            "maxDevMs": round(tmap.max_dev_ms if tmap else grid.max_dev_ms, 2),
            "stable": grid.stable,
            "changes": changes,
            "backend": tr.backend,
            "beats": [round(float(b), 4) for b in beats],
            "downbeats": [round(float(b), 4) for b in downbeats],
        },
        "energy": energy,
        "sections": secs,
        "layerSource": layer_source,
        "vocalSource": vocal_source,
        "layers": layers,
        "lyrics": lyrics,
        "cover": "cover.jpg",
        "preview": {"startSec": round(loudest["start"], 2), "durationSec": 12.0},
    }
    (out / "analysis.json").write_text(json.dumps(result, separators=(",", ":")))
    log(f"done in {time.time() - t0:.1f}s -> {out / 'analysis.json'}")
    return 0


def _tempo_map(args: argparse.Namespace, beats: np.ndarray, grid: "tempo.Grid", y: np.ndarray, sr: int):
    """A variable tempo map when the song drifts: asked for (--tempo variable), or automatically when
    the constant grid misses tracked beats by > 40 ms (p98) and the map more than halves that."""
    if args.tempo == "constant" or args.bpm or (args.tempo == "auto" and grid.stable):
        return None
    tm = tempo.fit_tempo_map(beats)
    if tm is None or len(tm.changes) < 2:
        return None
    if args.tempo == "auto" and tm.max_dev_ms > 0.5 * grid.max_dev_ms:
        log(f"tempo drifts (p98 {grid.max_dev_ms:.0f} ms) but a tempo map fits no better (p98 {tm.max_dev_ms:.0f} ms); keeping one BPM")
        return None
    tm = tempo.refine_map_phase(y, sr, tm)
    bpms = [b for _, b in tm.changes]
    log(f"variable tempo: {len(tm.changes)} segments, {min(bpms):.1f}-{max(bpms):.1f} BPM, "
        f"residual={tm.residual_ms:.1f}ms p98={tm.max_dev_ms:.0f}ms")
    return tm


def _first_sound(y: np.ndarray, sr: int, threshold_db: float = -40) -> float:
    peak = np.max(np.abs(y)) + 1e-9
    above = np.nonzero(np.abs(y) > peak * 10 ** (threshold_db / 20))[0]
    return float(above[0] / sr) if len(above) else 0.0


def _cached_stems(folder: Path, length: int, sr: int) -> dict[str, np.ndarray] | None:
    """Stems kept by an earlier analysis of the same audio (same padding, so the same length)."""
    names = ["drums", "bass", "other", "vocals"]
    if not all((folder / f"{n}.flac").exists() for n in names):
        return None
    import soundfile as sf

    out = {}
    for n in names:
        sig, file_sr = sf.read(str(folder / f"{n}.flac"), dtype="float32")
        if file_sr != sr or abs(len(sig) - length) > sr // 100:
            return None
        out[n] = sig[:length] if len(sig) >= length else np.pad(sig, (0, length - len(sig)))
    return out


def synth(args: argparse.Namespace) -> int:
    from . import synth as synth_mod

    truth = synth_mod.write(Path(args.output), bpm=args.bpm, offset=args.offset, drift=args.drift)
    Path(args.output).with_suffix(".truth.json").write_text(json.dumps(truth, indent=1))
    log(f"wrote {args.output} ({truth['bpm']} BPM, first beat {truth['firstBeatSec']}s)")
    return 0


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(prog="abeat-analyze")
    sub = p.add_subparsers(dest="cmd", required=True)

    a = sub.add_parser("analyze", help="analyze an audio file")
    a.add_argument("input", help="audio file, or a YouTube / YouTube Music URL (downloaded with yt-dlp)")
    a.add_argument("--title", help="override song title")
    a.add_argument("--artist", help="override artist")
    a.add_argument("-o", "--out", required=True, help="output work directory")
    a.add_argument("--beats", choices=["auto", "librosa", "beat_this"], default="auto",
                   help="beat tracker; auto = beat_this if installed, else librosa")
    a.add_argument("--stems", choices=["none", "demucs"], default="none")
    a.add_argument("--keep-stems", action="store_true")
    a.add_argument("--vocals", choices=["flux", "notes", "lyrics"], default="flux",
                   help="vocal onsets (needs --stems): spectral flux, sung notes (CREPE pitch) or "
                        "lyrics syllables (Whisper + forced alignment, extra 'lyrics')")
    a.add_argument("--whisper-model", default="small", help="faster-whisper model for --vocals lyrics")
    a.add_argument("--lyrics-file", help="lyrics text (repeats written out) for --vocals lyrics; skips transcription")
    a.add_argument("--bpm", type=float, default=None, help="override detected BPM (implies a constant tempo)")
    a.add_argument("--tempo", choices=["auto", "constant", "variable"], default="auto",
                   help="tempo map: auto = BPM changes only when one BPM can't follow the song (live recordings)")
    a.set_defaults(func=analyze)

    s = sub.add_parser("synth", help="write a synthetic test track")
    s.add_argument("output")
    s.add_argument("--bpm", type=float, default=128.0)
    s.add_argument("--offset", type=float, default=0.37)
    s.add_argument("--drift", type=float, default=0.0, help="relative tempo wander, e.g. 0.03 (live-band style)")
    s.set_defaults(func=synth)

    args = p.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
