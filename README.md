# ABeat by CHDS

Automatic Beat Saber map generator: audio file in, playable map (all difficulties, walls, bombs, lights) out.
Songs come from an uploaded file or a YouTube / YouTube Music link (only use audio you have the rights to).
A web UI lets you listen, inspect and tune the generator; the same server runs locally or in Docker.

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

## Quick start

### Docker (web UI)

```bash
docker compose up --build        # then open http://localhost:8080
```

`--build-arg ML=0` builds a much smaller image without torch (librosa beat tracking only).
Data (uploads, analyses, maps, settings) lives in the `/data` volume.

### Local

Requirements: .NET 10 SDK, Node.js 20+ and [uv](https://docs.astral.sh/uv/) (it installs its own Python).

```bash
cd analysis && uv sync --extra ml && cd ..     # drop --extra ml for the light version
(cd src/abeat-ui && npm install && npm run build)   # builds the UI into src/Abeat.Web/wwwroot
dotnet run --project src/Abeat.Web             # web UI on http://localhost:5080
# or the CLI:
dotnet run --project src/Abeat.Cli -- generate "Artist - Song.mp3" -d all
dotnet run --project src/Abeat.Cli -- generate "https://music.youtube.com/watch?v=..." -d all
dotnet run --project src/Abeat.Cli -- check path/to/any/map.zip    # flow report for any map
```

Web data defaults to `~/.local/share/abeat` (set `ABEAT_DATA` to change).

## How it works

**Analysis** (`analysis/`, Python): beat tracking with [beat_this](https://github.com/CPJKU/beat_this)
(or librosa), a constant-BPM grid fit, phase refinement against a sharp attack envelope (spectral-flux
envelopes lag ~50 ms, which players feel), downbeats, energy curve, novelty-based sections with
labels for repeated parts, and onsets per layer (frequency bands, or [Demucs](https://github.com/facebookresearch/demucs)
stems with `--stems`). The audio is padded so grid beat 0 is at t = 0 and written as `song.egg`.

**Rhythm selection** (`RhythmSelector`): onsets are snapped to a 1/12-beat grid (sixteenths, and
triplets only for songs with a triplet feel), scored by layer weight × strength × metric position ×
energy, and picked bar by bar to hit a notes-per-second target that follows section energy. Strong,
isolated hits become doubles. Vocal, bass and other stems use spectral-flux onsets (one per sung
syllable or note) rather than energy rises, which fired on consonants and breaths.

**Flow planning** (`FlowPlanner` + `SwingCostModel`): beam search over both sabers' states (position,
last swing direction, parity). Each candidate cut is scored for:

- *physical flow*: parity resets, angle change vs. a clean reversal, saber travel, swing speed,
  crossovers, vision blocks, over-extension
- *musical fit*: row follows brightness, accents prefer big vertical swings; one saber follows the
  melody (vocals) and the other the rhythm (drums, bass), swapping at section changes
- *variety*: stagnation penalty and per-phrase target cells keyed by section label, so repeated
  choruses get recognisably similar patterns

All weights are in `FlowWeights` and editable in the UI or a settings file (`abeat settings`).

**Evaluation** (`FlowAnalyzer`): the same physical cost model scores any map, including human-made
ones (`abeat check`), so the weights can be calibrated against maps people like.

## Benchmark vs human maps

`abeat fetch-maps` downloads top-rated curated BeatSaver maps (no mods); `abeat bench` re-maps each
map's own song and compares. Current results on 18 songs / 62 difficulties (2026-10-03):

| | ABeat | human |
|---|---|---|
| BPM | exact on 17/18 (1 octave) | |
| note timing vs human (F1 at ±50 ms) | 0.66 (Expert+ 0.71) | |
| timing offset | 1–4 ms | |
| notes per second | 3.2 | 3.5 |
| flow score | 97 | 84 |
| direction / position distribution distance | 0.17 / 0.22 | 0 |

The style prior (`scripts/style_prior.py`) is learned from these maps.

## Layout

| Path | What |
|---|---|
| `analysis/` | Python worker (`abeat-analyze analyze|synth`), uv project |
| `src/Abeat.Core` | Map model, v2/v3/v4 reader, v3 writer, generator, analyzer, packager |
| `src/Abeat.Cli` | `abeat generate|analyze|check|settings|synth` |
| `src/Abeat.Web` | ASP.NET Core API, serves the built UI from `wwwroot/` |
| `src/abeat-ui` | React 19 + TypeScript + Vite UI (ABook layout, Beat Saber palette) |
| `tests/` | xUnit tests for the core |

## Status / next steps

- Calibrate `FlowWeights` and the flow score against curated BeatSaver maps (`abeat check`).
- Learned rhythm selection (which onsets humans map) trained on BeatSaver maps, exported to ONNX.
- Arcs/chains, BPM changes for live-tempo songs, smarter bomb patterns.
- Desktop packaging (e.g. Photino window around the same web UI).
- Optional Chroma (custom colours, gradients, lighting) and Noodle Extensions (custom note/wall paths) output, declared as map requirements/suggestions.

## Credits

ABeat is made by **CHDS**, designed and built together with **Claude** (Anthropic's AI assistant,
via Claude Code), which co-wrote the analysis worker, generator, web UI and Docker setup.
