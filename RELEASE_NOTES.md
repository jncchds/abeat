# Release notes

## v0.1.0 — 2026-10-03

- Analysis worker: beat_this/librosa beat tracking, robust grid fit, attack-envelope phase refinement, sections, per-layer onsets, Demucs stems, generated covers.
- Generator: rhythm selection, beam-search flow planner, phrase-keyed patterns, walls (dodge/crouch/side), bombs (reset/accent), lights; Info v2.1 + beatmap v3.3 output.
- Flow analyzer for generated and human-made maps (`abeat check`).
- CLI, ASP.NET Core web app and Docker image bundling the Python worker.
- YouTube / YouTube Music links as song input (yt-dlp).
- React + TypeScript UI aligned with ABook (sidebar, theme toggle) with a Beat Saber colour scheme.
- BeatSaver comparison harness (`abeat fetch-maps`, `compare`, `bench`); parity model calibrated on curated maps (horizontal cuts free the wrist, sliders and dots handled).
- Style prior learned from curated maps (distribution matching for directions and cells), human-level density/doubles per difficulty, travel slack; flow score calibrated so curated maps average ~85.
- Human reference maps in the web app: local import (`POST /api/admin/import`, localhost only), ABeat / Human / Overlay views and per-difficulty comparison.
