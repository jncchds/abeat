"""Cover image: embedded art if present, otherwise generated from the song's spectrum."""

from __future__ import annotations

import colorsys
import hashlib
import io
from pathlib import Path

import librosa
import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

SIZE = 512


def write_cover(path: Path, embedded: bytes | None, y: np.ndarray, sr: int, title: str, artist: str) -> None:
    if embedded:
        try:
            img = Image.open(io.BytesIO(embedded)).convert("RGB")
            _square(img).save(path, quality=90)
            return
        except Exception:
            pass
    _generated(y, sr, title, artist).save(path, quality=90)


def _square(img: Image.Image) -> Image.Image:
    s = min(img.size)
    left, top = (img.width - s) // 2, (img.height - s) // 2
    return img.crop((left, top, left + s, top + s)).resize((SIZE, SIZE), Image.LANCZOS)


def _generated(y: np.ndarray, sr: int, title: str, artist: str) -> Image.Image:
    seed = int(hashlib.sha1(f"{artist}/{title}".encode()).hexdigest()[:8], 16)
    hue = (seed % 360) / 360
    c1 = np.array(colorsys.hsv_to_rgb(hue, 0.8, 0.35)) * 255
    c2 = np.array(colorsys.hsv_to_rgb((hue + 0.55) % 1, 0.9, 0.95)) * 255

    mel = librosa.feature.melspectrogram(y=y, sr=sr, n_mels=128, hop_length=2048)
    db = librosa.power_to_db(mel, ref=np.max)
    v = np.clip((db + 60) / 60, 0, 1)[::-1]  # low frequencies at the bottom
    v = np.array(Image.fromarray((v * 255).astype(np.uint8)).resize((SIZE, SIZE), Image.BILINEAR)) / 255.0
    v = v ** 1.5
    rgb = (c1[None, None, :] * (1 - v[..., None]) + c2[None, None, :] * v[..., None]).astype(np.uint8)
    img = Image.fromarray(rgb).filter(ImageFilter.GaussianBlur(2))

    draw = ImageDraw.Draw(img, "RGBA")
    draw.rectangle((0, SIZE - 120, SIZE, SIZE), fill=(0, 0, 0, 150))
    try:
        big, small = ImageFont.load_default(size=40), ImageFont.load_default(size=24)
    except TypeError:
        big = small = ImageFont.load_default()
    draw.text((24, SIZE - 105), _ellipsize(title, 22), font=big, fill=(255, 255, 255, 255))
    draw.text((24, SIZE - 50), _ellipsize(artist, 36), font=small, fill=(220, 220, 220, 255))
    return img


def _ellipsize(s: str, n: int) -> str:
    return s if len(s) <= n else s[: n - 1] + "…"
