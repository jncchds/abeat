"""Optional source separation with Demucs (extra "ml"). Returns mono stems at the input rate."""

from __future__ import annotations

import numpy as np


def separate(stereo: np.ndarray, sr: int, model_name: str = "htdemucs") -> dict[str, np.ndarray]:
    import torch
    from demucs.apply import apply_model
    from demucs.pretrained import get_model

    model = get_model(model_name)
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
        out = apply_model(model, wav[None], device="cpu", split=True, overlap=0.25, progress=False)[0]
    out = out * std + mean
    stems = {name: out[i].mean(0).numpy() for i, name in enumerate(model.sources)}
    if sr != model.samplerate:
        import librosa

        stems = {k: librosa.resample(v, orig_sr=model.samplerate, target_sr=sr) for k, v in stems.items()}
    return stems
