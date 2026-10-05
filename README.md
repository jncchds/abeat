# ABeat by CHDS

Automatic Beat Saber map generator: audio file in, playable map (all difficulties, walls, bombs, lights) out.
Songs come from an uploaded file or a YouTube / YouTube Music link (only use audio you have the rights to),
or from an existing Beat Saver map (zip or BeatSaver link), which is kept as a human version to compare with.
A web UI lets you listen, inspect and tune the generator, keeps every generation as a version and
compares any two (or a generation against an imported human map) lane by lane; the same server runs
locally or in Docker.

```
audio ─▶ analysis worker (Python) ─▶ analysis.json + song.egg + cover.jpg
           beats/downbeats, sections, onsets per layer, energy
                       │
                       ▼
         Abeat.Core (C#): rhythm selection ─▶ flow planner (beam search) ─▶ walls + lights
                       │                                                     │
                       ▼                                                     ▼
              Abeat.Cli / Abeat.Web                        Info.dat (v2.1) + *.dat (v3.3) + zip
```

**[Manual](docs/MANUAL.md)**: everything ABeat does, every setting, the CLI and the Docker setup.

## Quick start

### Docker (web UI)

```bash
docker compose up -d             # then open http://localhost:8080
```

One small image (~420 MB) for every machine. On first start it installs the analysis runtime with the
PyTorch build for your hardware (CPU, NVIDIA CUDA, AMD ROCm or Intel XPU; detected, or `ABEAT_ACCEL`)
into bind-mounted folders: `./docker-data/data` (songs, maps: back this up), `./docker-data/models`
(downloaded models) and `./docker-data/runtime` (Python environment, ArcViewer). GPUs:
`docker compose -f docker-compose.yml -f docker-compose.nvidia.yml up -d` (or `.rocm.yml`, `.intel.yml`).
Optional features: `ABEAT_EXTRAS=lyrics,roformer`. Details in the [manual](docs/MANUAL.md#2-running-abeat).

### Local

Requirements: .NET 10 SDK, Node.js 20+ and [uv](https://docs.astral.sh/uv/) (it installs its own Python).

```bash
cd analysis && uv sync --extra ml --extra cpu && cd ..   # torch flavor: cpu, cuda, cuda12, rocm or xpu; add --extra lyrics / roformer
(cd src/abeat-ui && npm install && npm run build)   # builds the UI into src/Abeat.Web/wwwroot
dotnet run --project src/Abeat.Web             # web UI on http://localhost:5080
# or the CLI:
dotnet run --project src/Abeat.Cli -- generate "Artist - Song.mp3" -d all
dotnet run --project src/Abeat.Cli -- generate "https://music.youtube.com/watch?v=..." -d all
dotnet run --project src/Abeat.Cli -- check path/to/any/map.zip    # flow report for any map
dotnet run --project src/Abeat.Cli -- movement work/beatsaver      # hand movement per difficulty
```

Web data defaults to `~/.local/share/abeat` (set `ABEAT_DATA` to change).

`scripts/fetch-arcviewer.sh` downloads [ArcViewer](https://github.com/AllPoland/ArcViewer) (GPL-3.0, ~80 MB)
so the web app can open maps in it from the same server (the container fetches it on first start). Without it the
ArcViewer button uses the public site.

**Playlists**: add versions from a song page ("＋ playlist"), then download the playlist zip and import
it in BSManager (Maps → Import; the `.bplist` inside goes to Playlists → Import). BSManager's one-click
links only work for maps published on BeatSaver.

## How it works

**Analysis** (`analysis/`, Python): beat tracking with [beat_this](https://github.com/CPJKU/beat_this)
(or librosa), a constant-BPM grid fit (or, for drifting live recordings, a tempo map written as v3
BPM events), phase refinement against a sharp attack envelope (spectral-flux
envelopes lag ~50 ms, which players feel), downbeats, energy curve, novelty-based sections with
labels for repeated parts, and onsets per layer (frequency bands, or [Demucs](https://github.com/facebookresearch/demucs)
stems with `--stems`). The audio is padded so grid beat 0 is at t = 0 and written as `song.egg`.

**Vocal onsets** (per song, re-analysis reuses the stems): *spectral flux* (default); *sung notes*,
flux onsets kept only where a pitched voice follows (CREPE), with the melody steering the note row;
or *lyric syllables*, the lyrics forced-aligned to the vocal stem (MMS aligner) and split into
syllables at their vowels. Paste the lyrics on the song page (repeats written out), or let Whisper
transcribe them (`uv sync --extra ml --extra lyrics`); the transcription can be loaded into the
lyrics box to correct it.

**Drums, notes and stems**: drum-stem hits are labelled kick / snare / hat, so hats count less when
choosing notes and the drummer plays the lights. `--pitched notes` transcribes the other and bass
stems with [basic-pitch](https://github.com/spotify/basic-pitch) (pitch steers the row, note lengths
drive arcs). `--roformer` takes the vocal stem from BS-RoFormer
([python-audio-separator](https://github.com/nomadkaraoke/python-audio-separator), extra `roformer`):
cleaner vocals, but slow without a GPU. With lyrics, sections whose words repeat (a returning chorus)
share a label even when the arrangement changes.

**Rhythm selection** (`RhythmSelector`): onsets are grouped into 1/12-beat grid slots (sixteenths, and
triplets only for songs with a triplet feel), scored by layer weight × strength × metric position ×
energy, and picked bar by bar to hit a notes-per-second target that follows section energy. Notes are
not snapped: each lands at the detected time of its sound (corrected for the stem's detection lag). Strong,
isolated hits become doubles. Before a drop the map pauses for a beat or two and lands on a double. Vocal, bass and other stems use spectral-flux onsets (one per sung
syllable or note) rather than energy rises, which fired on consonants and breaths.

**Flow planning** (`FlowPlanner` + `SwingCostModel`): beam search over both sabers' states (position,
last swing direction, parity). Each candidate cut is scored for:

- *physical flow*: parity resets, angle change vs. a clean reversal, saber travel, swing speed,
  crossovers, vision blocks, over-extension
- *musical fit*: row follows brightness, accents prefer big vertical swings; one saber follows the
  melody (vocals) and the other the rhythm (drums, bass), swapping at section changes
- *dynamics*: loud moments pull to the outer cells and bigger moves, soft ones stay small and central
- *variety*: stagnation penalty and per-phrase target cells keyed by section label, so repeated
  choruses get recognisably similar patterns

**Expression** (`Expression`): melody notes held for a beat or more get an arc to the same hand's
next note; notes in a rising or falling melody line get a small cut-angle offset that leans with it;
notes followed by a roll, flam or stutter too fast for single notes become chains (Hard and up).
All of them leave hands, cells and directions alone, so flow is unaffected. Each can be switched off in the
settings.

**Pattern memory**: when a part of the song comes back, the map is planned a second time with a bonus
for playing it the way its first occurrence was played, so a returning chorus brings its patterns back
(`repetition` weight).

**Lights and modes**: classic lights (section palettes, note flashes, drum-driven lasers and rings), or
`--environment pyro` for PyroEnvironment with a v3 group lightshow. `--modes onesaber,90,360` adds One
Saber (planned for one saber over the whole grid) and 90°/360° (the Standard notes with lane rotations).

All weights are in `FlowWeights` and editable in the UI or a settings file (`abeat settings`).

**Evaluation** (`FlowAnalyzer`): the same physical cost model scores any map, including human-made
ones (`abeat check`), so the weights can be calibrated against maps people like.

**Hand movement** (`MovementAnalyzer`, `abeat movement`): for every pair of consecutive swings of one
saber it measures how far the swing turns away from a clean back-and-forth and how far the saber tip
travels between them, and turns that into *strain*, the effective swings per second the move asks for
(turns past 45° and travel past one cell count extra). A map's 90th-percentile strain tells which
difficulty its movement plays like, calibrated on curated maps; the report cards show it as
"plays like …" next to the flow score. The generator matches the human mix of turns, travel and strain
for each difficulty, so Expert+ maps no longer move like a human Hard.

## Benchmark vs human maps

`abeat fetch-maps` downloads top-rated curated BeatSaver maps (no mods); `abeat bench` re-maps each
map's own song and compares. Current results on 18 songs / 62 difficulties with Demucs stems (2026-10-04):

| | ABeat | human |
|---|---|---|
| BPM | exact on 17/18 (1 octave) | |
| note timing vs human (F1 at ±50 ms) | 0.69 (Expert 0.71, Expert+ 0.76) | |
| timing offset | 1–4 ms | |
| notes per second | 3.1 | 3.5 |
| flow score | 94 | 85 |
| direction / position distribution distance | 0.18 / 0.22 | 0 |
| hand strain p90 (swings/s per hand; Easy … Expert+) | 2.7 · 3.1 · 3.5 · 4.0 · 5.8 | 2.8 · 3.2 · 3.9 · 5.1 · 6.5 |
| mean turn / tip travel between swings | 17° / 1.44 cells | 21° / 1.55 cells |

The style prior (`scripts/style_prior.py`) is learned from these maps.

## Layout

| Path | What |
|---|---|
| `analysis/` | Python worker (`abeat-analyze analyze|synth`), uv project |
| `src/Abeat.Core` | Map model, v2/v3/v4 reader, v3 writer (notes, arcs, chains, BPM/rotation events, group lights), generator, analyzer, packager |
| `src/Abeat.Cli` | `abeat generate|analyze|check|movement|settings|synth|fetch-maps|compare|bench` |
| `src/Abeat.Web` | ASP.NET Core API, serves the built UI from `wwwroot/` |
| `src/abeat-ui` | React 19 + TypeScript + Vite UI (ABook layout, Beat Saber palette) |
| `tests/` | xUnit tests for the core |

## Status / next steps

- Calibrate `FlowWeights` and the flow score against curated BeatSaver maps (`abeat check`).
- Learned rhythm selection (which onsets humans map) trained on BeatSaver maps, exported to ONNX.
- Smarter bomb patterns.
- Desktop packaging (e.g. Photino window around the same web UI).
- Optional Chroma (custom colours, gradients, lighting) and Noodle Extensions (custom note/wall paths) output, declared as map requirements/suggestions.

## Credits

ABeat is made by **CHDS**, designed and built together with **Claude** (Anthropic's AI assistant,
via Claude Code), which co-wrote the analysis worker, generator, web UI and Docker setup.
