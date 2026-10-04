# ABeat design

Authoritative design notes. Keep in sync with the code after architectural changes.

## Pipeline

1. **Input**: uploaded audio file or a link (YouTube / YouTube Music via yt-dlp). Links store the
   page URL; metadata comes from YouTube Music fields or an "Artist - Title [junk]" title parse.
2. **Analysis** (`analysis/`, Python, run as a subprocess by `AnalysisRunner`):
   - beat tracking: beat_this (neural) or librosa; constant-BPM grid fitted over the whole song
     (mean inlier interval, outlier-robust least squares), rounded to 0.5 BPM when equivalent
   - variable tempo (`--tempo auto|constant|variable`, `tempo.fit_tempo_map`): tracked beats are numbered
     (`index_beats`: the period follows the kept beats, clipped to -20/+25 % of the song's median, so
     stretches tracked at double tempo or on off-beats don't add beats), a piecewise-linear time-vs-beat
     curve with a knot per bar is fitted (IRLS, mis-tracked beats fade out; second-difference penalty on
     tempo changes), every knot is then locked to the audio (`_lock_knots`: +-40 ms coordinate ascent on
     the kick-weighted attack envelope at the bar's beats, small bend penalty; beat_this's 20 ms frames
     bias per-bar tempos), and the result is the constant grid except in bars where the curve scores
     >= 110 % of it on that envelope (`_keep_grid`, median over 3 bars): beat_this wanders 100-200 ms off
     the beat in some steady songs, so only stretches that really leave one BPM bend the grid. Bars are
     merged into constant-tempo segments while within 10 ms. Auto mode needs the constant grid to miss the
     tracked beats (p98 > 40 ms) and the map's beats to collect >= 1.1x the attack energy of the grid's
     (`on_beat_energy`; a hats-only intro tracked on off-beats no longer bends a steady song).
     `tempo.changes` = [{beat, bpm}], beat 0 first; C# reads it into `TempoMap`, which every
     beat/second conversion goes through (implicit from a plain BPM), and the writer emits v3
     `bpmEvents`; the rhythm selector sizes the minimum gap per bar from the local tempo. Results:
     drifting synth (`synth --drift 0.04`) mean 8 ms from the true grid (one BPM: 51 % of notes within
     30 ms); Coldplay - Paradise (live band) timing F1 vs the human map 0.70 vs 0.50 with one BPM; Dara -
     Bangaranga (slow parts speeding up): 77 % of generated notes within 30 ms of an onset vs 71 %, better
     in every part of the song; steady bench songs keep one BPM or score the same
   - phase refinement against a kick-weighted *attack envelope* (log-energy rise on short
     centred windows) because spectral-flux envelopes lag ~50 ms
   - downbeats on the fixed grid (tracker vote or low-band strength)
   - audio padded so grid beat 0 is t = 0 plus a >= 2 s lead-in, written as `song.egg`
   - onsets per layer: frequency bands (`full/low/mid/high`) or Demucs stems
     (`drums/bass/vocals/other`), each with strength and brightness. Drums use the attack
     envelope; pitched stems use mel spectral flux (`tonal_onsets`), because energy rises in them
     fire on consonants, breaths and vibrato at grid-random times. Bass/other flux peaks are moved
     to the attack peak in the preceding 70 ms; vocals stay unrefined (soft attacks)
   - drum hits labelled (`drum_onsets`, onset field `k`): kick "k", snare "s", hat/cymbal "h" by the
     log-frequency centroid of the power the hit adds (40 ms after vs before; < 400 Hz kick, > 3.6 kHz
     hat), a second letter when >= 35 % of that power lies in another range (snares are often "sh").
     Linear power, not dB rises: a hat lifts the snare band by as many dB as a snare. Synthetic kits
     (tests/test_features.py): every detected kick, snare and hat labelled right. Used by `DrumWeights` in rhythm
     selection (hats count less: humans map kick and snare), `RhythmEvent.Drum` and the lights. Hats at 0.6: Normal F1 0.62 -> 0.65, the rest unchanged (0.4 no better)
   - other/bass onsets selectable (`--pitched flux|notes`, `pitched.py`): `notes` transcribes the stems
     with basic-pitch (its bundled ONNX model on onnxruntime; TensorFlow is overridden away in
     pyproject because it has no Python 3.12 build), one onset per group of notes starting within 40 ms,
     refined to the attack, pitch of the top (other) / bottom (bass) note scaled to the stem's range as
     brightness, note end as `e` (sustain for arcs). Bench (18 curated maps): timing F1 0.68 vs 0.69 with flux,
     position distance 0.21 vs 0.22, so flux stays the default and notes are an option
   - vocal stem separator (`--stems demucs|roformer`): `roformer` replaces the Demucs vocals with
     BS-RoFormer (python-audio-separator, extra `roformer`, model cached in ~/.cache/abeat/separator,
     temp files in the work dir); `work/stems/vocals.roformer` marks cached RoFormer vocals.
     Works end to end; on a 4-core CPU one 8 s chunk takes ~136 s at the model's overlap 4 (~70x real time),
     so the CPU uses overlap 2 (~35x) and logs an estimate: practical only with a GPU.
   - vocal onsets selectable per song (`--vocals`, `AnalysisOptions.VocalOnsets`, `vocals.py`):
     `flux` (default); `notes`: flux onsets kept only where CREPE-tiny hears a pitched voice in the
     next 80 ms (drops breaths/consonants/bleed; Cake By The Ocean: 83 % on human notes vs 72 %),
     pitch drives brightness (row); `lyrics`: MMS forced alignment (torchaudio) of user lyrics
     (`--lyrics-file`, song's `lyrics.txt`) or a faster-whisper transcription (extra `lyrics`),
     one onset per vowel group, shifted ~70 ms earlier (CTC peaks lag) and snapped to the nearest
     vocal attack; words are stored in `analysis.json` `lyrics` and drawn on the timeline.
     Pitch-only note segmentation was tried and timed onsets worse than flux (CREPE window blur).
   - links are downloaded once: `work/download/fetched.json` (+ `cover.bin`) records the file and
     metadata, so re-analysis never calls yt-dlp again (older downloads are found by video id)
   - stems kept in `work/stems` are reused by a re-analysis of the same audio (length check), so
     switching vocal methods skips Demucs
   - energy curve, novelty-based sections snapped to downbeats, clustered labels (A, B, ...); with
     lyrics, sections sharing >= 60 % of their word bigrams (>= 6 words) get one label
     (`merge_lyric_repeats`: a chorus that returns with a different arrangement still repeats its
     words; links only merge labels, verses with the same music and new words stay merged)
   - cover: embedded art, thumbnail, or generated from the spectrum
3. **Rhythm selection** (`RhythmSelector`): onsets snapped to a 1/12-beat grid (sixteenths, plus
   triplets only when the song has a triplet feel: `HasTripletFeel`, >= 12 % of the percussive layer's
   onset strength on eighth-triplets and 1.5x more than on sixteenth off-beats), scored by layer
   weight x strength x metric position x energy, with the "e" sixteenth (x0.6), "a" (x0.8) and
   triplets (x0.85) discounted as human maps rarely use them; bars filled to a
   notes-per-second target that follows section and local energy; strong isolated hits become doubles.
   Each event also carries expression data: `Sustain` (melody layers only: time to the layer's next
   onset, cut at the end of a sung word with lyrics or where energy falls below 60 %), `PitchSlope`
   (brightness change from the previous note of the same melody layer) and `Intensity` (rank of
   0.6 strength + 0.4 energy, so 0.5 is the median). Drops (downbeats where mean energy over 2 s jumps
   >= 0.3 into >= 0.6, one per 16 beats) get a note-free pause of ~0.9 s (1-2 beats, at least the
   same-hand gap) and a forced double on the drop (`DropPause`).
4. **Flow planning** (`FlowPlanner`): beam search over both hands' states (position, swing vector,
   parity, previous cell). Costs from `SwingCostModel`:
   - physical: resets (same parity within 1 s), angle vs clean reversal, saber travel, too-fast
     same-hand hits, crossovers, vision blocks, over-extension
   - musical: row follows brightness, accents prefer vertical swings; dynamics (`FlowWeights.Dynamics`): swing size
     (distance from the grid centre + move from the hand's last cell) follows `Intensity`, zero-mean
     so the cell mix still matches the style prior
   - pattern memory (`FlowWeights.Repetition`): when a section label repeats, the song is planned a second
     time and every event pays -Repetition for the exact cut (hand, cell, direction) the same position of
     the label's first occurrence got in the first plan (first occurrences are pulled to their own cuts,
     so the copy source stays put). `RepetitionAnalyzer` measures it (share of note moments in repeats
     that match the first occurrence; `abeat bench` "repeat g/h"): weight 0.5 makes 21 % of repeated moments exact copies (human maps 10 %, no repetition pass 5 %; 1.5 gave 50 %) at no cost in flow or timing
   - variety: stagnation, per-phrase target cells keyed by section label (`PhraseTargets`), so
     repeated sections reuse similar movement; seeded hash noise
   - style: distribution matching against `style-prior.json` (cut directions, cells per hand, learned
     from curated maps by `scripts/style_prior.py`); each beam path tracks running counts and pays
     log(running share / human share), so the mix matches humans instead of collapsing to the mode
   - hand roles (`FlowWeights.HandRole`): melody-led notes (vocals, other) prefer one hand and
     rhythm-led notes (drums, bass) the other; the roles swap at section changes. Soft, so flow
     wins where the layers don't alternate; the CLI reports the share of notes that kept their role
   - movement (`movement-prior.json`, learned by `abeat movement --write-prior`): each move from a
     hand's last swing (gap <= 1.5 s) falls into a turn-angle x tip-travel bucket and a strain bucket;
     both are distribution-matched like the style prior (`MovementStyle`, `Effort`), and moves above
     the difficulty's human strain p98 pay `Strain`. Effort matching is what makes one saber take
     quick runs instead of strict hand alternation (the alternation bonus is only 0.75)
   - parity: vertical/diagonal swings fix forehand/backhand; horizontal cuts free it; a swing within
     60° of the previous one is a reset; travel within 1.25 cells is free; turns cost
     `TurnCost` (cheap up to 45°, steep beyond, as humans time them)
   - expression (`Expression`, after planning, never changes hands/cells/directions): single
     melody notes that continue a pitch line (two steps the same way, >= 0.06) get a 15° angle offset
     (30° for leaps on Expert+): rising leans vertical cuts "/" and lifts horizontal cut ends
     (`AngleOffsets`, Normal+); arcs (v3 sliders) from a single melody note held >= 1 beat and
     >= 45 % of the way to the same hand's next note 1-4 beats later, unless that swing is a reset or
     a gameplay wall passes in between (`Arcs`, added after walls and bombs); chains (v3 burst sliders,
     `Chains`, Hard+) on notes followed by a fast run in their layer or the drums (`RhythmEvent.BurstCount`:
     onsets each < min(100 ms, 0.85 sixteenth) apart, so steady sixteenth hats never count): links continue
     the head's cut for 2 cells (1 if only that fits), last 1/16-1/4 beat, need a same-hand gap >= 1 beat
     and free cells; at most one per 8 beats (16 on Hard), doubles get two chains or none
5. **Walls** (`WallGenerator`): crouch walls before energy jumps (Hard+), dodge walls in note-free
   gaps (Normal+), side walls in calm sections; all rejected if any note is inside them.
6. **Bombs** (`BombGenerator`): reset bombs where the natural reversal would cut, accent bombs on
   strong single-hand hits (Hard+); checked against `SaberPath` so no bomb is in a swing path.
7. **Lights** (`LightingGenerator`): section palettes (repeated labels share colours), downbeat
   pulses, per-note laser flashes, ring spins/zooms, colour boost in the loudest sections. With
   labelled drum hits the drummer plays the lights instead of the downbeat pulses: kicks (s >= 0.3)
   pulse the back lasers, snares flash the centre, hats flicker the rings in loud sections (each
   light at most every quarter beat). `Environment = "Pyro"` (`GroupLightshow`) switches to
   PyroEnvironment and adds a v3 group lightshow (14 groups in left/right pairs, as a curated Pyro map
   uses them): 0/1 ambient section colour, 2/3 downbeat chases (wave over 1 beat, direction alternating),
   4/5 note flashes per hand, 6/7 snares (white), 8/9 kicks, 10/11 melody notes (held for the note's
   sustain), 12/13 hats (a quarter of the lights at a time); pairs 2/3, 8/9 and 10/11 rotate to a new
   pose at each section (wider when louder). Schema checked against that map (rotation boxes keep their
   events under `l`); not verified in game.
8. **Modes** (`GeneratorSettings.Modes`, written next to Standard for the same difficulties):
   `OneSaber` is planned anew with one (right) saber covering the whole grid (no doubles, rhythm gap =
   same-hand gap, 80 % density, no crossover/hand-role costs, cell prior = mean of both hands, phrase
   targets from both halves); `90Degree` / `360Degree` reuse the Standard objects plus v3
   `rotationEvents` (`RotationGenerator`): early 15° turns (30° in loud sections) on a note after >= 0.5 s
   of rest, every 2 bars (4 on Easy/Normal) and at section changes; 90° keeps the heading within ±45°,
   360° keeps turning and may reverse at a section. The web app shows Standard only; the other modes are
   in the zip / ArcViewer.
9. **Output**: Info.dat v2.1.0 (one difficulty set per mode) + difficulty v3.3.0 (`bpmEvents` for tempo changes, `rotationEvents`, group lights, notes with angle offsets, arcs as `sliders`, chains as `burstSliders`), `song.egg`, `cover.jpg`, zip.

## Container

One slim image (`Dockerfile`: aspnet + app + UI + worker source + uv, ~420 MB) for every accelerator;
nothing heavy is baked in. `docker/entrypoint.sh` (root only to chown the bind mounts to
`ABEAT_UID:ABEAT_GID`, then `setpriv` to that user, keeping the GPU device groups) starts
`docker/provision.py` in the background and the server in the foreground. provision.py detects the
accelerator (`ABEAT_ACCEL=auto`: NVIDIA device -> `cuda`, /dev/kfd -> `rocm`, Intel render node ->
`xpu`, else `cpu`; `cuda12` and `none` by hand), installs uv-managed Python and
`uv sync --frozen --extra ml --extra <accel> [--extra lyrics|roformer]` into
`/runtime/envs/<accel>-<hash of uv.lock+accel+extras>` (uv cache in /runtime, hardlinked), prefetches the
beat_this/Demucs models into /models, fetches ArcViewer into /runtime/arcviewer, deletes older envs and
writes `status.json`. The torch flavors are exclusive extras in analysis/pyproject.toml (`[tool.uv]
conflicts`) with per-extra indexes: cpu, cu130, cu126, rocm7.2, xpu (+ their triton packages); the GPU
wheels carry their own CUDA/ROCm/oneAPI libraries, so one image serves all, the host only provides the
driver (compose override files per vendor). The worker picks its torch device in `accel.device()`
(`ABEAT_DEVICE` overrides). deno for yt-dlp is a wheel in the env. Volumes: /data, /models, /runtime.

## Evaluation

`FlowAnalyzer` scores any map (v2/v3/v4) with the physical part of the cost model plus wall-clash
and bomb-hit checks. Flow score = 100 * exp(-mean cost / 7), calibrated so curated human maps average ~85.
Sliders/windows (same hand < 90 ms apart) count as one swing; dots take the direction leading into
the next note. Use it to compare generated maps with
human ones (`abeat check`).

`MovementAnalyzer` (part of every `FlowReport`) measures each pair of consecutive swings of one hand
(same swing model: 0.6-cell overshoot, stacks/sliders = one swing, dots resolved, angle offsets
applied; gaps > 1.5 s are rests and skipped):
- turn: angle between the new swing and a clean reversal of the last (0 = down→up, 90 = down→left)
- travel: saber-tip distance from the last swing's exit to the new swing's entry, grid cells
- strain = (1 + 0.7 per 45° of turn beyond 45° + 0.5 per cell of travel beyond 1) / gap: effective
  swings per second. Shape fitted on curated maps: humans give 0° and 45° moves the same minimum gap
  and any travel up to ~2.5 cells, but 90° turns ~1.7x the time
- the map's strain p90 maps to a continuous difficulty rank (1 Easy ... 9 Expert+) by interpolating
  the human medians per difficulty (`movement-prior.json`): orders 98.9 % of within-song difficulty
  pairs correctly (78 % for swing rate alone), and lands within one level for 95 % of difficulties
- moves above the difficulty's human p98 are `Strain` issues (shown on the timeline)

`abeat movement [dir]` prints per-map reports and, for a folder of maps, per-difficulty tables (mean
over maps, and the share of moves per turn x travel cell with their median gap); `--csv` dumps every
move, `--write-prior` rewrites the prior.

## Web app

- `SongStore`: `data/songs/{id}/` with `source/`, `meta.json`, `work/` (analysis), `settings.json`
  (current settings), `reference/` (human map) and `generations/{yyyyMMdd-HHmmss-fff}/`
  (`Generations`: `generation.json` with app version, draft flag and the BPM/padding it was written
  on, its `settings.json`, `map/` + `map.zip`). File based; survives restarts via the Docker volume.
  A pre-history `map/` folder is moved into `generations/` on first access.
- `AnalysisQueue`: one analysis at a time, then generation with the song's saved settings, so every
  upload ends with a downloadable map. Every analysis writes `work.next/` (seeded with the cached
  `stems/` and `download/`) and `SongStore.CommitNextWorkDir` swaps it in only on success, so the
  current analysis and all versions stay usable during a re-analysis and after it fails or is cancelled
  (`POST /songs/{id}/cancel` kills the worker). `SongMeta.HasAnalysis` = usable (the UI gates on it, not
  on `Status`, which is the job state); `AnalysisRevision` tells clients to reload. Jobs wait for
  `WorkerRuntime` (container: /runtime/status.json written by provision.py) and run its worker.
- API: `/api/songs` (list, upload), `/api/songs/url`, `/api/songs/{id}` (+ `/analysis`, `/audio`,
  `/cover`, `/settings`, `/map.zip[?version=]` with CORS for ArcViewer (newest generation by default),
  `POST /generate[?draft=true]`, `POST /reanalyze`).
- Versions: every generate appends a generation; `draft=true` (UI auto-regenerate) replaces the newest
  generation if it is a draft, so tweaking settings does not flood the history.
  `GET /versions` lists generations (newest first, with note-timing F1 vs the human map per
  difficulty) plus `human`; `GET|DELETE /versions/{v}`, `GET /versions/{v}/settings`;
  `GET /compare?a=&ad=&b=&bd=` runs `MapComparer` on any two (version, difficulty) pairs (B = reference).
  All versions are re-timed onto the current analysis grid (`Generations.OnGrid`, through both tempo maps), so they stay
  aligned after a re-analysis.
- Playlists (`PlaylistStore`, `data/playlists/{id}.json`): entries are (song, generation). `/api/playlists`
  CRUD, `POST /{id}/entries`, `GET /{id}/download.zip` (one folder per entry with Info.dat `_songSubName`
  set to "ABeat #n", plus a `.bplist` referencing each map by level hash = SHA-1 of Info.dat + difficulty
  files in Info.dat order) and `/{id}/playlist.bplist`. Human reference maps can't be added. BSManager's
  one-click links (`beatsaver://`, `bsplaylist://`) resolve maps through api.beatsaver.com only, so
  generated maps are installed with BSManager's zip import instead.
- ArcViewer (GPL-3.0, AllPoland) is fetched by `scripts/fetch-arcviewer.sh` (pinned v0.8.1 deploy commit)
  into the git-ignored `src/Abeat.Web/arcviewer` (Docker: `/opt/abeat/app/arcviewer`, or
  `ABEAT_ARCVIEWER_DIR`) and served same-origin at `/arcviewer/`; `/api/config` reports `arcViewer`
  and the UI falls back to the public site without it. ArcViewer (Unity) refuses plain-http downloads
  except from localhost, so other devices open it and the zip through the https listener
  (`ABEAT_HTTPS_PORT`, self-signed LAN certificate). Routing runs after the static files so the SPA
  fallback doesn't swallow `/arcviewer/`.
- Lyrics: `GET|PUT /api/songs/{id}/lyrics` (`lyrics.txt`), passed to the worker on re-analysis.
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
