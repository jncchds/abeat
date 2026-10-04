"""Optional source separation with Demucs (extra "ml"). Returns mono stems at the input rate."""

from __future__ import annotations

import numpy as np


def _load(get_model, name: str):
    """Load from the local Hugging Face cache without contacting the Hub when the model is already
    there (no network round-trip, no "unauthenticated requests to the HF Hub" warning); download only
    the first time. huggingface_hub reads HF_HUB_OFFLINE at import, so decide before importing it."""
    import os
    from pathlib import Path

    cache = Path(os.environ.get("HF_HUB_CACHE") or Path(os.environ.get("HF_HOME") or Path.home() / ".cache" / "huggingface") / "hub")
    if any(cache.glob("models--adefossez--*")):
        os.environ.setdefault("HF_HUB_OFFLINE", "1")
    return get_model(name)


def separate(stereo: np.ndarray, sr: int, model_name: str = "htdemucs") -> dict[str, np.ndarray]:
    import torch
    from demucs.apply import apply_model
    from demucs.pretrained import get_model

    from .accel import device

    model = _load(get_model, model_name)
    model.eval()
    if sr != model.samplerate:
        import librosa

        stereo = librosa.resample(stereo.T, orig_sr=sr, target_sr=model.samplerate).T
    wav = torch.from_numpy(np.ascontiguousarray(stereo.T)).float()
    ref = wav.mean(0)
    mean, std = ref.mean(), ref.std() + 1e-8
    wav = (wav - mean) / std
    torch.set_num_threads(max(1, torch.get_num_threads()))
    with torch.no_grad():
        out = apply_model(model, wav[None], device=device(), split=True, overlap=0.25, progress=False)[0].cpu()
    out = out * std + mean
    stems = {name: out[i].mean(0).numpy() for i, name in enumerate(model.sources)}
    if sr != model.samplerate:
        import librosa

        stems = {k: librosa.resample(v, orig_sr=model.samplerate, target_sr=sr) for k, v in stems.items()}
    return stems


ROFORMER_MODEL = "model_bs_roformer_ep_317_sdr_12.9755.ckpt"  # BS-RoFormer (viperx), vocals SDR ~13 dB vs ~9 for htdemucs


def roformer_vocals(stereo: np.ndarray, sr: int, workdir, log) -> np.ndarray:
    """Vocal stem from BS-RoFormer via python-audio-separator (extra "roformer"). Much cleaner vocals than
    htdemucs (less bleed from leads and pads, so fewer false vocal onsets), but slow on CPU: use a GPU or
    expect several minutes per minute of audio. Model (~600 MB) is cached in ~/.cache/abeat/separator."""
    import os
    import shutil
    import tempfile
    from pathlib import Path

    import imageio_ffmpeg
    import soundfile as sf
    from audio_separator.separator import Separator

    work = Path(tempfile.mkdtemp(prefix="roformer-", dir=workdir))  # on disk next to the analysis, not /tmp
    try:
        # audio-separator shells out to "ffmpeg"; use the bundled imageio-ffmpeg binary
        (work / "bin").mkdir()
        os.symlink(imageio_ffmpeg.get_ffmpeg_exe(), work / "bin" / "ffmpeg")
        os.environ["PATH"] = str(work / "bin") + os.pathsep + os.environ.get("PATH", "")
        src = work / "mix.wav"
        sf.write(str(src), stereo, sr, subtype="FLOAT")
        models = Path(os.environ.get("ABEAT_SEPARATOR_MODELS") or Path.home() / ".cache" / "abeat" / "separator")
        sep = Separator(output_dir=str(work), model_file_dir=str(models), log_level=30, output_single_stem="Vocals")
        log(f"separating vocals with BS-RoFormer ({ROFORMER_MODEL}; slow on CPU)")
        sep.load_model(ROFORMER_MODEL)
        out = [work / f for f in sep.separate(str(src))]
        vocal = next(f for f in out if "vocal" in f.name.lower())
        y, file_sr = sf.read(str(vocal), dtype="float32", always_2d=True)
        y = y.mean(axis=1)
        if file_sr != sr:
            import librosa

            y = librosa.resample(y, orig_sr=file_sr, target_sr=sr)
        n = stereo.shape[0]
        return y[:n] if len(y) >= n else np.pad(y, (0, n - len(y)))
    finally:
        shutil.rmtree(work, ignore_errors=True)
