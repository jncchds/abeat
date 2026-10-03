# Release notes

## v0.1.0 — 2026-10-03

- Analysis worker: beat_this/librosa beat tracking, robust grid fit, attack-envelope phase refinement, sections, per-layer onsets, Demucs stems, generated covers.
- Generator: rhythm selection, beam-search flow planner, phrase-keyed patterns, walls (dodge/crouch/side), bombs (reset/accent), lights; Info v2.1 + beatmap v3.3 output.
- Flow analyzer for generated and human-made maps (`abeat check`).
- CLI, ASP.NET Core web app and Docker image bundling the Python worker.
- YouTube / YouTube Music links as song input (yt-dlp).
- React + TypeScript UI aligned with ABook (sidebar, theme toggle) with a Beat Saber colour scheme.
