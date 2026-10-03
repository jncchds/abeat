# Release notes

## v0.1.3 — 2026-10-03

- Phone-friendly song page: header, player and A/B pickers stack and wrap, difficulty buttons fit (or scroll), two compact report-card columns, side-by-side player views, no horizontal page scroll; timeline pinch-to-zoom and −/+ zoom buttons.

## v0.1.2 — 2026-10-03

- Versions in the web app: every generation is kept (auto-regenerate updates a single draft), versions can be deleted or pruned to the two being compared, and their settings loaded back; maps record the app version and stay aligned after re-analysis.
- Comparison: pick any version + difficulty as A and as B (the human map is a version too); split lanes (A top, B bottom), notes only one side has are ringed and marked on an "only" strip, A-vs-B F1 / offset / distribution distances, both player views.

## v0.1.1 — 2026-10-03

- Steadier note timing: vocal/bass/other onsets from spectral flux (was ~8/s of grid-random consonant and breath hits), triplets only for songs with a triplet feel, "e" sixteenths discounted. Cake By The Ocean Expert F1 vs the human map 0.76 → 0.81, band-analysis bench Expert+ 0.70 → 0.74.
- Hand roles: one saber follows the melody, the other the rhythm, swapping at section changes (`HandRole` weight; CLI reports how many notes kept their role).

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
- Doubles where a saber swings into the other hand's note are forbidden (analyzer reports hand clashes).
- Vocals first: Demucs stems on by default, vocals weighted highest, layers compared by rank, strongest layer leads each slot.
- fetch-maps: max 2 maps per mapper, rating + recently curated.
- Optional HTTPS listener with a self-signed LAN certificate (ABEAT_HTTPS_PORT).
- Hidden debug panel (Ctrl+Shift+D or ?debug=1): download or play separated stems, download analysis.json; stems are kept as FLAC.
- Demucs loads its model offline when cached (no Hugging Face Hub warning or network call per analysis).
