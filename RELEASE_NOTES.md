# Release notes

## v0.2.4 — 2026-10-05

- Tempo from the drum stem: with stems, the beat grid is fitted to the separated drums (kick and snare) instead of the beat tracker, and tempo changes are followed bar by bar on the drums. Steady songs no longer get spurious tempo changes (Everlasting had 38; your taps now sit 27 ms from the grid instead of 43), and songs that slow down and speed back up follow it (Bangaranga: 87 % of strong drum hits on a 16th slot instead of 29 %).

## v0.2.3 — 2026-10-04

- Tap along: **Tap beats** in the player records one run per hand (right first) of you tapping any key on the notes you expect. The runs show as rows in the timeline; each hand is scored against that hand's notes of A and B (offset, F1, precision, recall), and both hands together against all notes and doubles. Saved per song in `taps.json`.
- Docker: `ABEAT_HTTPS_PUBLIC_PORT` sets the host port of the HTTPS listener (compose publishes it), so ArcViewer and map links work when 8443 is published as another port.

## v0.2.2 — 2026-10-04

- No more hands clashing: a note in the cell the other hand cut less than 0.3 s before costs the generator (the checker reports it as a clash below 0.2 s), and doubles may not start or end their swings together (e.g. L↖ next to R↗ in the middle). On 18 curated songs generated maps had 7x the human rate of these; now none, with timing, flow and strain unchanged. New `handClash` weight.

## v0.2.1 — 2026-10-04

- Docker: `ABEAT_HTTPS_HOSTS` names this machine and its LAN addresses for the HTTPS certificate (the container can't see them), so an existing certificate keeps being used and devices that trusted it don't get a new warning.

## v0.2.0 — 2026-10-04

- Drum parts: hits on the drum stem are labelled kick, snare or hat; hats count less when choosing notes (as in human maps), and the drummer plays the lights (kicks on the back lasers, snares on the centre, hats on the rings).
- Pattern memory: when a part of the song comes back, the map plays it the way its first occurrence was played (about 20 % of repeated moments exact copies, human maps ≈10 %; no cost in flow). `repetition` weight.
- Repeated lyrics: with lyrics, a chorus that returns with a different arrangement still gets its section's label.
- Other/bass notes from basic-pitch (option *other/bass: notes*): real pitch for rows and angles, real note lengths for arcs.
- BS-RoFormer vocals (option *vocals: RoFormer*, extra `roformer`): cleaner vocal stem; slow without a GPU.
- Lights and modes: optional Pyro environment with a v3 group lightshow; One Saber, 90° and 360° modes next to Standard.
- Variable tempo follows the audio, not just the beat tracker: the map stays on one BPM and bends only where the music really changes tempo (slow parts speeding up, a drifting live band). Dara's Bangaranga: 77 % of notes within 30 ms of a sound instead of 71 %; a live recording: timing F1 0.70 instead of 0.50; steady songs keep one BPM.
- Re-analysis no longer blocks a song: it runs in a side folder and replaces the analysis only when it succeeds, the song and all versions stay usable meanwhile, failures keep everything, and a Cancel button stops it.
- Docker: one slim image (~420 MB instead of 3.2 GB) for CPU, NVIDIA (CUDA 13 / 12.6), AMD ROCm and Intel XPU. Python, PyTorch for the detected hardware, models and ArcViewer install on first start into bind-mounted `models/` and `runtime/` folders next to `data/`; the web UI shows the progress. The worker runs its models on the GPU when there is one.
- Docs: a full [manual](docs/MANUAL.md). Tests split by area (C#) and a pytest suite for the worker.

## v0.1.9 — 2026-10-04

- Variable tempo: songs whose tempo drifts (live recordings, unquantized bands) get a tempo map instead of one BPM, written as BPM events, so notes stay on the beat throughout (on a drifting test track 100 % of notes within 30 ms of the true grid, against 51 % with one BPM). Automatic only when one BPM can't follow the song; `--tempo constant|variable` forces it. The song page shows the BPM range; timeline, player view and version comparison follow the changes. Human maps with BPM events can now be compared too.
- Chains: a note followed by a drum roll, flam or stutter too fast for single notes gets a chain that continues its cut (Hard and up, sparingly like human maps). Toggle in the settings; drawn on the timeline and the player view.

## v0.1.8 — 2026-10-04

- Expression from the analysis: arcs over held melody notes (from stem onsets, or sung word lengths with lyrics), cut angles that lean with rising/falling melody lines, swing size that follows how hard each moment hits (new `dynamics` weight), and a short pause plus a double before each drop. Toggles in the settings; the timeline and front view draw arcs and angles.
- Hand movement analysis: `abeat movement` measures, for each pair of consecutive swings of one saber, the turn away from a clean back-and-forth and the saber-tip travel, and rates them as strain (effective swings per second). A map's strain p90 tells which difficulty its movement plays like (calibrated on curated maps; orders 99 % of a song's difficulties correctly). Shown in `abeat check`, the generation log and the report cards ("plays like …", strain spikes on the timeline).
- The generator matches the human mix of turns, travel and strain per difficulty (new `movementStyle`, `effort`, `strain` weights): 45° turns are nearly free and 90° turns costly as in human timing, travel within 1.25 cells is free, one saber may take quick runs (same-hand gaps lowered to human values, softer alternation). Generated Expert movement now rates 5.0 on the 1–9 difficulty scale instead of 1.3 (strain p90 4.0 vs 2.7 before; human 5.1), Expert+ 7.8 instead of 4.8; flow score rescaled so curated maps stay ~85.
- Flow check: a dot followed by another dot just reverses the swing (only a dot right before a directional note leads into it), so back-and-forth dot chains no longer count as resets.

## v0.1.7 — 2026-10-03

- ArcViewer bundled: `scripts/fetch-arcviewer.sh` fetches its web build (pinned v0.8.1, GPL-3.0, credited in the sidebar) and the server serves it at /arcviewer/, and from other devices it opens over the https listener (ArcViewer refuses plain-http downloads except from localhost; accept the self-signed certificate once); Docker images include it.
- Playlists: add generated versions from the song page, manage them on a playlist page, download one zip (all maps + .bplist) for BSManager's map import, or the .bplist alone; in game each map's subtitle shows its version.

## v0.1.6 — 2026-10-03

- Songs added by link are downloaded once: re-analysis reuses the audio, title, artist and cover in the work dir instead of calling yt-dlp again (also for songs downloaded before this change).

## v0.1.5 — 2026-10-03

- Lyrics box opens from a "Lyrics" button next to Re-analyze (and when lyric syllables are picked), right under the song header; it was buried below the report cards, out of sight on phones.
- index.html is served with `Cache-Control: no-cache` (hashed assets cached for good), so phones pick up new builds.

## v0.1.4 — 2026-10-03

- Vocal onsets selectable per song (add-song form, or next to Re-analyze): spectral flux, sung notes (flux onsets kept only where CREPE hears a pitched voice; the melody steers the note row; 83 % of vocal onsets on human notes vs 72 % on Cake By The Ocean) or lyric syllables (MMS forced alignment of pasted lyrics, or a Whisper transcription, split at vowels and snapped to vocal attacks).
- Lyrics box on the song page (can start from the transcription to correct it); words shown on the timeline.
- Re-analysis reuses the separated stems, so switching the vocal method skips Demucs. Optional worker extra `lyrics` (faster-whisper) and Docker build arg `LYRICS=1`.

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
