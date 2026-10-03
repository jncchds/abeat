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
     (`drums/bass/vocals/other`), each with strength and brightness
   - energy curve, novelty-based sections snapped to downbeats, clustered labels (A, B, ...)
   - cover: embedded art, thumbnail, or generated from the spectrum
3. **Rhythm selection** (`RhythmSelector`): onsets snapped to a 1/12-beat grid (sixteenths and
   triplets), scored by layer weight x strength x metric position x energy; bars filled to a
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

- `SongStore`: `data/songs/{id}/` with `source/`, `meta.json`, `work/` (analysis), `settings.json`,
  `map/` + `map.zip`. File based; survives restarts via the Docker volume.
- `AnalysisQueue`: one analysis at a time, then generation with the song's saved settings, so every
  upload ends with a downloadable map.
- API: `/api/songs` (list, upload), `/api/songs/url`, `/api/songs/{id}` (+ `/analysis`, `/audio`,
  `/cover`, `/settings`, `/map`, `/map.zip` with CORS for ArcViewer, `POST /generate`, `POST /reanalyze`).
- UI: React SPA with ABook's layout (collapsible sidebar, theme toggle) and a Beat Saber palette
  (blue saber = accent, red saber = secondary). Canvases redraw per animation frame from the
  `<audio>` element's current time.
