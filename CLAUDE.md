# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.
Design details live in `AGENTS.md` (authoritative design doc); user-facing docs in `README.md`.

## Commands

### Backend (.NET 10, run from repo root)

```bash
dotnet build Abeat.sln                         # build all projects
dotnet test tests/Abeat.Core.Tests             # unit tests (one file per area)
dotnet run --project src/Abeat.Web             # web server on http://localhost:5080 (serves wwwroot)
dotnet run --project src/Abeat.Cli -- generate <audio|YouTube URL|analysis dir> -d all
dotnet run --project src/Abeat.Cli -- check <map folder|zip>
```

### Analysis worker (Python, `analysis/`)

```bash
uv sync --extra ml --extra cpu --extra lyrics --extra roformer   # the full local env (torch CPU)
# torch flavor extras are exclusive: cpu | cuda (CUDA 13) | cuda12 | rocm | xpu; drop ml + flavor for librosa only
.venv/bin/python -m pytest -q tests   # worker tests
.venv/bin/abeat-analyze analyze <audio|URL> -o <work dir> [--beats auto|librosa|beat_this] [--stems demucs] [--vocals flux|notes|lyrics] [--lyrics-file f]
.venv/bin/abeat-analyze synth samples/synth128.wav    # deterministic test track with known BPM/offset
```

`uv add` re-syncs without extras; run the full `uv sync` line above afterwards. Never put venvs or models in /tmp (small RAM tmpfs).

### Frontend (run from `src/abeat-ui/`)

```bash
npm install
npm run dev      # Vite dev server, proxies /api to http://localhost:5080
npm run build    # TypeScript check + Vite build -> src/Abeat.Web/wwwroot/ (git-ignored)
npm run lint
npx tsc --noEmit -p tsconfig.app.json   # type-check only
```

### Docker

```bash
docker build -t abeat .                 # slim image (~420 MB): app + worker source, no Python/models
docker compose up -d                    # http://localhost:8080; ./docker-data/{data,models,runtime}
docker compose -f docker-compose.yml -f docker-compose.nvidia.yml up -d   # GPU (also .rocm.yml, .intel.yml)
```

## Architecture

```
React SPA (src/abeat-ui, built into Abeat.Web/wwwroot)
        ↕ REST /api
ASP.NET Core (src/Abeat.Web) ── AnalysisQueue ──▶ Python worker (analysis/, subprocess)
        ↕                                              beats, sections, onsets, energy → analysis.json
Abeat.Core: RhythmSelector → FlowPlanner (beam search, SwingCostModel) → walls / bombs / lights → v3 map
```

| Project | Purpose |
|---|---|
| `analysis/` | Python worker: audio I/O, yt-dlp fetch, beat tracking + grid fit, onsets, sections, cover |
| `src/Abeat.Core` | Map model, readers/writers, generation pipeline, `FlowAnalyzer`, packaging |
| `src/Abeat.Cli` | `abeat` command line |
| `src/Abeat.Web` | Minimal API, file-based `SongStore`, `AnalysisQueue` background service |
| `src/abeat-ui` | React 19 + TypeScript + Vite SPA (same structure and theme base as ABook) |
| `tests/Abeat.Core.Tests` | xUnit tests |

## Mandatory workflow rules

1. **After every code change**: `dotnet build Abeat.sln` and, for UI changes, `npx tsc --noEmit -p tsconfig.app.json` and `npm run lint` in `src/abeat-ui`. Fix all errors; fix warnings when safe.
2. **After every committed change**: version check-bump flow:
   a. If the current `VERSION` has already been pushed (`git log origin/main..HEAD -- VERSION` is empty and the version exists on origin), bump the patch number and add a `## v{version} — {date}` heading to `RELEASE_NOTES.md`; otherwise reuse the current version.
   b. Make sure the current version's heading carries today's date.
   c. Append a bullet for the change under that heading.
3. **After architectural changes**: update `AGENTS.md` and `README.md`.
4. Generated maps must stay playable: keep `FlowAnalyzer` reporting zero wall clashes and bomb hits on generated maps, and keep the generator deterministic per seed (tests cover this).
5. Never commit copyrighted audio or downloaded maps (`samples/`, `work/`, `out/`, `data/` are ignored).

## Technical gotchas

- All analysis times are seconds in the padded `song.egg`; grid beat 0 is t = 0, so `beat = t * bpm / 60`.
- Spectral-flux onset envelopes lag ~50 ms; use `features.attack_envelope` for anything timing-critical.
- `string.GetHashCode` is randomized per process in .NET; use the stable hashes in `PhraseTargets`/`FlowPlanner`.
- yt-dlp's JS runtime for YouTube is the `deno` wheel in the worker env (the runner puts the env's bin on PATH).
- The container installs the worker env at start (`docker/provision.py` → /runtime/status.json); the server
  holds analyses until it is ready (`WorkerRuntime`). Re-analysis writes `work.next/`, swapped in on success.
- Web data defaults to `~/.local/share/abeat`; a relative `ABEAT_DATA` resolves against the project directory.
