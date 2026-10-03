"""Audio loading, metadata extraction and Beat Saber audio export (.egg = Ogg Vorbis)."""

from __future__ import annotations

import io
import subprocess
import tempfile
from dataclasses import dataclass
from pathlib import Path

import numpy as np
import soundfile as sf

OUTPUT_SR = 44100


@dataclass
class LoadedAudio:
    stereo: np.ndarray  # shape (n, 2), float32, at `sr`
    sr: int

    @property
    def mono(self) -> np.ndarray:
        return self.stereo.mean(axis=1).astype(np.float32)

    @property
    def duration(self) -> float:
        return len(self.stereo) / self.sr


def _ffmpeg_exe() -> str | None:
    try:
        import imageio_ffmpeg

        return imageio_ffmpeg.get_ffmpeg_exe()
    except Exception:
        return None


def load_audio(path: Path) -> LoadedAudio:
    """Decode any audio file to float32 stereo at OUTPUT_SR.

    libsndfile handles wav/flac/ogg/mp3; anything else (m4a, opus in mp4, ...) goes through
    the ffmpeg binary bundled with imageio-ffmpeg.
    """
    try:
        data, sr = sf.read(str(path), dtype="float32", always_2d=True)
    except Exception:
        data, sr = _load_with_ffmpeg(path)

    if data.shape[1] == 1:
        data = np.repeat(data, 2, axis=1)
    elif data.shape[1] > 2:
        data = data[:, :2]

    if sr != OUTPUT_SR:
        import librosa

        data = librosa.resample(data.T, orig_sr=sr, target_sr=OUTPUT_SR, res_type="soxr_hq").T
        sr = OUTPUT_SR
    return LoadedAudio(np.ascontiguousarray(data, dtype=np.float32), sr)


def _load_with_ffmpeg(path: Path) -> tuple[np.ndarray, int]:
    exe = _ffmpeg_exe()
    if exe is None:
        raise RuntimeError(f"Cannot decode {path}: unsupported by libsndfile and no ffmpeg available")
    proc = subprocess.run(
        [exe, "-v", "error", "-i", str(path), "-f", "wav", "-acodec", "pcm_f32le", "-ar", str(OUTPUT_SR), "-"],
        capture_output=True,
        check=True,
    )
    data, sr = sf.read(io.BytesIO(proc.stdout), dtype="float32", always_2d=True)
    return data, sr


def pad_start(audio: LoadedAudio, seconds: float) -> LoadedAudio:
    n = int(round(seconds * audio.sr))
    if n <= 0:
        return audio
    pad = np.zeros((n, 2), dtype=np.float32)
    return LoadedAudio(np.concatenate([pad, audio.stereo]), audio.sr)


def write_egg(audio: LoadedAudio, path: Path, quality: float = 0.6) -> None:
    """Write Ogg Vorbis. Beat Saber/BeatSaver expect the file to be named *.egg."""
    peak = float(np.max(np.abs(audio.stereo))) if audio.stereo.size else 0.0
    data = audio.stereo / peak * 0.98 if peak > 1.0 else audio.stereo
    try:
        with sf.SoundFile(str(path), "w", samplerate=audio.sr, channels=2, format="OGG", subtype="VORBIS") as f:
            f.compression_level = 1.0 - quality
            f.write(data)
    except Exception:
        _write_egg_ffmpeg(data, audio.sr, path)


def _write_egg_ffmpeg(data: np.ndarray, sr: int, path: Path) -> None:
    exe = _ffmpeg_exe()
    if exe is None:
        raise RuntimeError("Cannot write .egg: libsndfile failed and no ffmpeg available")
    with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as tmp:
        sf.write(tmp.name, data, sr, subtype="FLOAT")
        subprocess.run(
            [exe, "-v", "error", "-y", "-i", tmp.name, "-c:a", "libvorbis", "-q:a", "6", "-f", "ogg", str(path)],
            check=True,
        )
    Path(tmp.name).unlink(missing_ok=True)


@dataclass
class Metadata:
    title: str
    artist: str
    cover: bytes | None  # embedded cover art, if any


def read_metadata(path: Path) -> Metadata:
    title, artist, cover = path.stem, "", None
    try:
        import mutagen

        f = mutagen.File(str(path))
        if f is not None and f.tags is not None:
            tags = f.tags
            title = _first_tag(tags, ["TIT2", "title", "\xa9nam", "TITLE"]) or title
            artist = _first_tag(tags, ["TPE1", "artist", "\xa9ART", "ARTIST"]) or artist
            cover = _cover_art(f)
    except Exception:
        pass
    # "Artist - Title" file names are common when tags are missing
    if not artist and " - " in title:
        artist, title = (s.strip() for s in title.split(" - ", 1))
    return Metadata(title=title, artist=artist, cover=cover)


def _first_tag(tags, keys: list[str]) -> str | None:
    for k in keys:
        try:
            v = tags.get(k)
        except Exception:
            v = None
        if v is None:
            continue
        if hasattr(v, "text"):
            v = v.text
        if isinstance(v, list):
            v = v[0] if v else None
        if v:
            return str(v).strip()
    return None


def _cover_art(f) -> bytes | None:
    tags = f.tags
    for key in list(getattr(tags, "keys", lambda: [])()):
        if str(key).startswith("APIC"):
            return tags[key].data
    if "covr" in tags:
        return bytes(tags["covr"][0])
    pictures = getattr(f, "pictures", None)
    if pictures:
        return pictures[0].data
    return None
