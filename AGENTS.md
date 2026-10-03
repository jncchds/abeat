# ABeat design

Authoritative design notes. Keep in sync with the code after architectural changes.

## Pipeline

1. **Input**: uploaded audio file or a link (YouTube / YouTube Music via yt-dlp). Links store the
   page URL; metadata comes from YouTube Music fields or an "Artist - Title [junk]" title parse.
2. **Analysis** (`analysis/`, Python, run as a subprocess by `AnalysisRunner`):
   - beat tracking: beat_this (neural) or librosa; constant-BPM grid fitted over the whole song
     (mean inlier interval, outlier-robust least squares), rounded to 0.5 BPM when equivalent
   - phase refinement against a kick-weighted *attack envelope* (log-energy rise on short
     centred windows) because spectral-flux envelopes lag ~50 ms
   - downbeats on the fixed grid (tracker vote or low-band strength)
   - audio padded so grid beat 0 is t = 0 plus a >= 2 s lead-in, written as `song.egg`
   - onsets per layer: frequency bands (`full/low/mid/high`) or Demucs stems
     (`drums/bass/vocals/other`), each with strength and brightness. Drums use the attack
     envelope; pitched stems use mel spectral flux (`tonal_onsets`), because energy rises in them
     fire on consonants, breaths and vibrato at grid-random times. Bass/other flux peaks are moved
     to the attack peak in the preceding 70 ms; vocals stay unrefined (soft attacks)
   - energy curve, novelty-based sections snapped to downbeats, clustered labels (A, B, ...)
   - cover: embedded art, thumbnail, or generated from the spectrum
3. **Rhythm selection** (`RhythmSelector`): onsets snapped to a 1/12-beat grid (sixteenths, plus
   triplets only when the song has a triplet feel: `HasTripletFeel`, >= 12 % of the percussive layer's
   onset strength on eighth-triplets and 1.5x more than on sixteenth off-beats), scored by layer
   weight x strength x metric position x energy, with the "e" sixteenth (x0.6), "a" (x0.8) and
   triplets (x0.85) discounted as human maps rarely use them; bars filled to a
   notes-per-second target that follows section and local energy; strong isolated hits become doubles.
4. **Flow planning** (`FlowPlanner`): beam search over both hands' states (position, swing vector,
   parity, previous cell). Costs from `SwingCostModel`:
   - physical: resets (same parity within 1 s), angle vs clean reversal, saber travel, too-fast
     same-hand hits, crossovers, vision blocks, over-extension
   - musical: row follows brightness, accents prefer vertical swings
   - variety: stagnation, per-phrase target cells keyed by section label (`PhraseTargets`), so
     repeated sections reuse similar movement; seeded hash noise
   - style: distribution matching against `style-prior.json` (cut directions, cells per hand, learned
     from curated maps by `scripts/style_prior.py`); each beam path tracks running counts and pays
     log(running share / human share), so the mix matches humans instead of collapsing to the mode
   - hand roles (`FlowWeights.HandRole`): melody-led notes (vocals, other) prefer one hand and
     rhythm-led notes (drums, bass) the other; the roles swap at section changes. Soft, so flow
     wins where the layers don't alternate; the CLI reports the share of notes that kept their role
   - parity: vertical/diagonal swings fix forehand/backhand; horizontal cuts free it; a swing within
     60° of the previous one is a reset; travel within 0.75 cells is free
5. **Walls** (`WallGenerator`): crouch walls before energy jumps (Hard+), dodge walls in note-free
   gaps (Normal+), side walls in calm sections; all rejected if any note is inside them.
6. **Bombs** (`BombGenerator`): reset bombs where the natural reversal would cut, accent bombs on
   strong single-hand hits (Hard+); checked against `SaberPath` so no bomb is in a swing path.
7. **Lights** (`LightingGenerator`): section palettes (repeated labels share colours), downbeat
   pulses, per-note laser flashes, ring spins/zooms, colour boost in the loudest sections.
8. **Output**: Info.dat v2.1.0 + difficulty v3.3.0, `song.egg`, `cover.jpg`, zip.

## Evaluation

`FlowAnalyzer` scores any map (v2/v3/v4) with the physical part of the cost model plus wall-clash
and bomb-hit checks. Flow score = 100 * exp(-mean cost / 11), calibrated so curated human maps average ~85.
Sliders/windows (same hand < 90 ms apart) count as one swing; dots take the direction leading into
the next note. Use it to compare generated maps with
human ones (`abeat check`).

## Web app

- `SongStore`: `data/songs/{id}/` with `source/`, `meta.json`, `work/` (analysis), `settings.json`
  (current settings), `reference/` (human map) and `generations/{yyyyMMdd-HHmmss-fff}/`
  (`Generations`: `generation.json` with app version, draft flag and the BPM/padding it was written
  on, its `settings.json`, `map/` + `map.zip`). File based; survives restarts via the Docker volume.
  A pre-history `map/` folder is moved into `generations/` on first access.
- `AnalysisQueue`: one analysis at a time, then generation with the song's saved settings, so every
  upload ends with a downloadable map.
- API: `/api/songs` (list, upload), `/api/songs/url`, `/api/songs/{id}` (+ `/analysis`, `/audio`,
  `/cover`, `/settings`, `/map.zip[?version=]` with CORS for ArcViewer (newest generation by default),
  `POST /generate[?draft=true]`, `POST /reanalyze`).
- Versions: every generate appends a generation; `draft=true` (UI auto-regenerate) replaces the newest
  generation if it is a draft, so tweaking settings does not flood the history.
  `GET /versions` lists generations (newest first, with note-timing F1 vs the human map per
  difficulty) plus `human`; `GET|DELETE /versions/{v}`, `GET /versions/{v}/settings`;
  `GET /compare?a=&ad=&b=&bd=` runs `MapComparer` on any two (version, difficulty) pairs (B = reference).
  All versions are re-timed onto the current analysis grid (`Generations.OnGrid`), so they stay
  aligned after a re-analysis.
- Reference maps: `POST /api/admin/import {path}` (loopback only) imports an analysis work dir or a
  human map folder with its `abeat-work` analysis; the human map is kept in `reference/` and served as
  version `human`.
- Song page comparison: A (gold) and B (violet) each pick a version + difficulty; with both shown every
  timeline lane splits into an A row (top) and a B row (bottom), notes without a counterpart in the
  other version (±50 ms) are ringed in their version's colour and marked on an "only" strip, and the
  player view shows both grids.
- UI: React SPA with ABook's layout (collapsible sidebar, theme toggle) and a Beat Saber palette
  (blue saber = accent, red saber = secondary). Canvases redraw per animation frame from the
  `<audio>` element's current time.
