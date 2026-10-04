"""Which torch device the ML models run on."""

from __future__ import annotations

import os
from functools import cache


@cache
def device() -> str:
    """ABEAT_DEVICE if set (cpu, cuda, cuda:1, xpu, ...), else the first GPU torch can use (CUDA, or ROCm,
    which torch also reports as "cuda"; Intel "xpu"), else the CPU."""
    forced = os.environ.get("ABEAT_DEVICE", "").strip()
    if forced:
        return forced
    import torch

    if torch.cuda.is_available():
        return "cuda"
    if hasattr(torch, "xpu") and torch.xpu.is_available():
        return "xpu"
    return "cpu"
