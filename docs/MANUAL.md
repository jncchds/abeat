# ABeat manual

ABeat by CHDS turns a song into a playable Beat Saber map: notes for every difficulty, arcs, chains,
walls, bombs and a lightshow, written as a standard custom level you can load in the game, preview in
ArcViewer or install with BSManager. This manual describes everything it does and every knob it has.

- [1. Quick start](#1-quick-start)
- [2. Running ABeat](#2-running-abeat)
  - [Docker](#docker) · [GPUs](#gpus) · [Environment variables](#environment-variables) · [Updating](#updating-and-disk-use) · [Running without Docker](#running-without-docker)
- [3. The web app](#3-the-web-app)
  - [Adding songs](#adding-songs) · [The song page](#the-song-page) · [Versions and comparison](#versions-and-comparison) · [Settings panel](#settings-panel) · [Playlists](#playlists) · [Debug panel](#debug-panel)
- [4. Analysis options](#4-analysis-options)
- [5. How a map is made](#5-how-a-map-is-made)
- [6. Generator settings reference](#6-generator-settings-reference)
- [7. Game modes, lights and environments](#7-game-modes-lights-and-environments)
- [8. Reading the reports](#8-reading-the-reports)
- [9. Command line](#9-command-line)
- [10. Files and formats](#10-files-and-formats)
- [11. Troubleshooting](#11-troubleshooting)
- [12. Credits and licences](#12-credits-and-licences)

---

## 1. Quick start

```bash
docker compose up -d          # http://localhost:8080
```

1. Open http://localhost:8080. On the very first start a banner says the analysis runtime is being
   installed (Python, PyTorch and the models: 1–5 GB, a few minutes). You can add songs meanwhile; they
   wait in the queue.
2. Drop an audio file on **Add a song**, or paste a YouTube / YouTube Music link.
3. The song is analysed (beats, sections, separated stems; a few minutes on a CPU, much less on a GPU)
   and a map for the selected difficulties is generated.
4. On the song page: listen and watch the timeline, tweak settings (the map regenerates in seconds),
   **⬇ Download map** or open it in **ArcViewer**.

Only use audio you have the rights to. Generated maps are for your own use; BeatSaver requires
automatically generated maps to be declared as such.

## 2. Running ABeat

### Docker

The image is small (≈420 MB): the server, the web UI and the analysis worker's source. Everything heavy
lives in three folders you bind into the container:

| Folder in the container | Default on the host (compose) | Contents | Back up? |
|---|---|---|---|
| `/data` | `./docker-data/data` | songs, analyses, generated maps and their versions, playlists, settings | **yes** |
| `/models` | `./docker-data/models` | ML models (beat_this, Demucs, Whisper, BS-RoFormer), downloaded on first use | no, re-downloadable |
| `/runtime` | `./docker-data/runtime` | Python, the worker environment for your accelerator, ArcViewer, package cache | no, re-created on start |

`docker-compose.yml` sets this up; `ABEAT_HOME=/path docker compose up -d` puts the three folders
elsewhere. Plain `docker run`:

```bash
docker run -d --name abeat -p 8080:8080 -p 8443:8443 \
  -e ABEAT_UID=$(id -u) -e ABEAT_GID=$(id -g) \
  -v ~/abeat/data:/data -v ~/abeat/models:/models -v ~/abeat/runtime:/runtime \
  abeat:latest
```

The container starts as root only to hand the three folders to `ABEAT_UID:ABEAT_GID` (default 1000 in
compose), then runs as that user, so the files on the host belong to you.

On every start the entrypoint checks `/runtime`: if the environment for the current accelerator,
extras and lockfile is there, the server is ready at once; otherwise it is installed in the background
while the web UI already runs (a banner shows the progress, analyses wait for it). An image update that
changes the worker's dependencies builds a new environment and deletes the old one; the package cache in
`/runtime/uv-cache` makes that mostly a local copy.

Ports: 8080 is HTTP. 8443 is HTTPS with a self-signed certificate, needed only to open ArcViewer from
another device (phone, headset): ArcViewer refuses plain-HTTP downloads except from localhost. Accept the
certificate once per device. Publishing 8443 under another host port (`-p 5443:8443`)? Tell the app
with `ABEAT_HTTPS_PUBLIC_PORT=5443`, or its ArcViewer links point at 8443.

### GPUs

One image serves every machine: the PyTorch build is chosen when the runtime is installed.

| Hardware | Compose command | `ABEAT_ACCEL` | Host needs |
|---|---|---|---|
| any CPU | `docker compose up -d` | `cpu` (auto) | nothing |
| NVIDIA Turing (RTX 20) or newer | `docker compose -f docker-compose.yml -f docker-compose.nvidia.yml up -d` | `cuda` (CUDA 13) | driver ≥ 580 + NVIDIA Container Toolkit |
| older NVIDIA (GTX 10, Volta) or driver < 580 | same, with `ABEAT_ACCEL=cuda12` | `cuda12` (CUDA 12.6) | driver ≥ 560 + Container Toolkit |
| AMD (ROCm 7.2 supported cards) | `… -f docker-compose.rocm.yml up -d` | `rocm` | amdgpu kernel driver |
| Intel Arc / Data Center GPU | `… -f docker-compose.intel.yml up -d` | `xpu` | Intel GPU driver |

`auto` (the default) picks `cuda` when the container sees an NVIDIA device, `rocm` when `/dev/kfd` is
present, `xpu` for an Intel render node, else `cpu`. The GPU wheels bring their own CUDA/ROCm/oneAPI
libraries, so no special base image is needed; only the driver comes from the host. On a Mac (Docker
Desktop has no GPU passthrough) the CPU build is used.

What runs on the GPU: beat tracking (beat_this), stem separation (Demucs, BS-RoFormer) and CREPE pitch.
Whisper transcription stays on the CPU (int8). `ABEAT_DEVICE=cpu` forces the CPU even with a GPU build.

`ABEAT_ACCEL=none` installs no PyTorch at all: beats come from librosa and onsets from frequency bands
(no stems). Smallest and fastest to install, clearly less accurate.

### Environment variables

| Variable | Default | Meaning |
|---|---|---|
| `ABEAT_ACCEL` | `auto` | `auto`, `cpu`, `cuda`, `cuda12`, `rocm`, `xpu`, `none` (see above) |
| `ABEAT_EXTRAS` | empty | optional worker features, comma separated: `lyrics` (Whisper transcription for lyric-syllable onsets), `roformer` (BS-RoFormer vocal separation) |
| `ABEAT_UID`, `ABEAT_GID` | image user 1654 (compose: 1000) | owner of files in the bound folders |
| `ABEAT_DEVICE` | auto | torch device for the worker (`cpu`, `cuda`, `cuda:1`, `xpu`) |
| `ABEAT_ARCVIEWER` | `1` | `0` skips downloading ArcViewer (the UI then links to the public site) |
| `ABEAT_PREFETCH` | `1` | download the beat_this and Demucs models while installing (else on first use) |
| `ABEAT_HTTPS_PORT` | `8443` | HTTPS listener (self-signed); unset to disable |
| `ABEAT_HTTPS_PUBLIC_PORT` | `ABEAT_HTTPS_PORT` | port browsers reach HTTPS on, used in ArcViewer and map links; set it when the container's 8443 is published under another host port (compose publishes this port, so setting it in `.env` is enough) |
| `ABEAT_HTTP_PUBLIC_PORT` | `8080` | compose only: host port for HTTP |
| `ABEAT_HTTPS_HOSTS` | — | in a container: this machine's name and LAN IPs (`mypc,192.168.1.20`) for the HTTPS certificate, so phones and headsets can open ArcViewer; the certificate (`/data/https-cert.pfx`) is kept as long as it covers them |
| `ABEAT_HTTP_PORT` | `8080` | HTTP listener when HTTPS is on |
| `HF_TOKEN` | — | optional Hugging Face token (faster model downloads) |

Outside Docker: `ABEAT_DATA` (data folder, default `~/.local/share/abeat`; a relative path resolves
against the project directory), `ABEAT_ANALYSIS_DIR` (the `analysis/` project), `ABEAT_WORKER` (an
explicit `abeat-analyze` executable), `ABEAT_ARCVIEWER_DIR`, `ABEAT_SEPARATOR_MODELS` (BS-RoFormer
model cache, default `~/.cache/abeat/separator`).

### Updating and disk use

```bash
docker compose pull   # or: docker compose build
docker compose up -d
```

`/data` is never touched by updates. Typical sizes: runtime 2 GB (CPU) to 6 GB (CUDA/ROCm), models
0.2 GB (+0.5 GB Whisper small, +0.6 GB BS-RoFormer), data ~30 MB per song (audio, stems, versions).
Deleting `/runtime` or `/models` is always safe; they come back on the next start.

Coming from the old image with a named `abeat-data` volume:

```bash
docker run --rm -v abeat-data:/from -v "$PWD/docker-data/data:/to" alpine cp -a /from/. /to/
```

### Running without Docker

Requirements: .NET 10 SDK, [uv](https://docs.astral.sh/uv/), Node 22 (for the UI).

```bash
cd analysis && uv sync --extra ml --extra cpu      # or cuda / cuda12 / rocm / xpu; add --extra lyrics, --extra roformer
cd ../src/abeat-ui && npm install && npm run build  # UI into src/Abeat.Web/wwwroot
cd ../.. && dotnet run --project src/Abeat.Web       # http://localhost:5080
scripts/fetch-arcviewer.sh                           # optional: bundle ArcViewer
```

The CLI (`dotnet run --project src/Abeat.Cli -- …`) works on its own without the web server, see
[Command line](#9-command-line).

## 3. The web app

### Adding songs

**Drop an audio file** (mp3, ogg, wav, flac, m4a, …, up to 300 MB) or **paste a link** (YouTube,
YouTube Music; title, artist and cover are taken from the page). Links are downloaded once and kept, so
re-analysing never downloads again.

**Analysis options** on the add form: beat tracker, **Separate stems (Demucs)** (recommended; needed
for vocal-led maps and drum labels) and the vocal onset method (see [Analysis options](#4-analysis-options)).
The song list shows each song's state: Queued, Analyzing, Generating, Ready or Failed. Analyses run one
at a time; the generator itself is fast and runs right after.

### The song page

**Header**: cover, title, artist, the reference mapper when a human map was imported, and facts: BPM
(or a BPM range marked *variable* for songs with tempo changes), duration, beat tracker, onset source,
vocal method, number of sections, link to the source page.

**Actions**:
- **⬇ Download map**: the zip of version A (see below), ready for the game's `CustomLevels` folder.
- **ArcViewer**: opens the map in the bundled ArcViewer (3D preview with audio).
- **＋ playlist**: adds version A to a playlist.
- **vocals: …**, **tempo: …**, **other/bass: …**, **vocals: Demucs/RoFormer**: analysis options for the
  next re-analysis.
- **Lyrics**: paste the lyrics (repeats written out) for lyric-syllable onsets; can start from the
  Whisper transcription to correct it.
- **Re-analyze**: runs the analysis again with the selected options and generates a new version.
- **Cancel**: stops a queued or running analysis.
- **Delete**: removes the song with all versions.

**While a re-analysis runs** the song stays fully usable: the current analysis, all versions,
comparisons and regenerating keep working. A banner shows the progress. The new analysis replaces the
old one only when it succeeds; if it fails or is cancelled, the banner says so and nothing is lost.

**Player**: play/pause (space), position and current beat, playback speed, and *follow* (the timeline
scrolls with the music).

**Timeline** (scroll or drag to move, pinch or −/+ to zoom):
- sections, coloured per label (repeated parts share a colour) with their energy, and the energy curve;
- bar and beat lines;
- one strip per onset layer (stems or frequency bands), darker = stronger;
- the lyrics, when the vocals were aligned to lyrics;
- twelve note lanes (top row first, columns left to right) with notes (cut arrow or dot, angle offsets
  shown), arcs, chains, bombs and walls (red: dodge walls, amber: crouch walls);
- flow issues of version A as triangles under the lanes (resets, vision blocks, crossovers, clashes,
  strain spikes).

With two versions shown, every lane splits: A on top, B below; notes only one side has are ringed in
that version's colour and marked on the *only* strip.

**Player view**: the 4×3 grid as the player sees it, with the next two beats of notes, arcs, chains and
bombs approaching. One per version when comparing.

**Report cards** per difficulty: notes and notes per second, flow score, resets, vision blocks, strain,
"plays like" difficulty, turn/travel, NJS and jump distance, timing F1 against the human map when there
is one (see [Reading the reports](#8-reading-the-reports)).

### Versions and comparison

Every generation is kept as a **version** (time, app version, and timing F1 against the human map per
difficulty). Pick any version and difficulty as **A** (gold) and **B** (violet); the human map, when
imported, is a version too. **Auto-regenerate** (on by default) writes a *draft* that the next change
replaces, so tweaking doesn't flood the history; **Generate** keeps a version for good. Per version:
load its settings back, delete it, or prune everything except A and B.

The comparison card shows A against B: timing F1, precision/recall at ±50 ms, median offset,
direction and position distribution distances, doubles and notes per second.

All versions are re-timed onto the current analysis, so they stay aligned after a re-analysis that
changed the BPM or the padding.

### Tapping along

**Tap beats** (player bar) records how you hear the song, one run per hand, right hand first. Each run
plays the song from the start, and every key press (or touch on the pad) is a note that hand would cut.
Esc stops a run early. The timeline shows the runs as *tap R* / *tap L* rows above the lanes, with taps
that have no note nearby drawn tall. The table scores each hand against the same hand's notes of A (and
B): your median offset (latency, removed before matching), F1, precision (taps that have a note), recall
(notes that have a tap) at ±70 ms, and *any* (taps that have a note of either hand). *Both* merges the two
runs and compares them with all notes; the doubles line counts the moments both hands hit. A run stopped
early only counts up to where it stopped. Runs are saved per song in `taps.json`, with the padding of the song.egg they were recorded on, so a re-analysis doesn't shift them.

### Settings panel

Changes regenerate the map at once (with auto-regenerate on). Every setting is described in the
[reference](#6-generator-settings-reference). Sections:

- **Difficulties**: Easy … Expert+.
- **Extra modes**: One Saber, 90°, 360° (written next to Standard; see [Game modes](#7-game-modes-lights-and-environments)).
- **Environment**: Default (classic lights) or Pyro (v3 group lightshow).
- **General**: density, seed (a different seed gives a different but equally valid map), beam width,
  and switches for lights, bombs, walls (dodge, crouch), arcs, chains, angle offsets, pause before drops.
- **Flow weights**: every cost of the planner, each a slider (hover for a hint).
- **Onset layers**: how much each stem or band counts when choosing which sounds become notes.

### Playlists

Add versions to playlists from the song page. The playlist page lists the maps; **Install with
BSManager** downloads one zip with all maps and a `.bplist` for BSManager's import (each map's subtitle
shows its version, e.g. "ABeat #3"); the `.bplist` alone is also available. Human reference maps can't be
added.

### Debug panel

Ctrl+Shift+D (or `?debug=1`): download or play each separated stem in the player (same timing as the
map), download `analysis.json`.

## 4. Analysis options

| Option | Values | What it does |
|---|---|---|
| Beat tracker | **auto**, beat_this, librosa | beat_this (neural, accurate) when installed, else librosa |
| Stems | **on**, off | Demucs separates drums, bass, vocals and other. Off: frequency bands of the mix (faster, no vocal focus, no drum labels) |
| Vocal separator | **Demucs**, RoFormer | BS-RoFormer gives cleaner vocals (less bleed from leads and pads, fewer false vocal notes). Needs the `roformer` extra and, in practice, a GPU: a 4-core CPU needs about 35 minutes per minute of audio (Cancel stops it); its 640 MB model downloads on first use |
| Vocal onsets | **flux**, notes, lyrics | *flux*: every new sung sound. *notes*: only where a pitched voice follows (CREPE), the melody steers the row. *lyrics*: one note per syllable, from lyrics you paste (or a Whisper transcription with the `lyrics` extra), aligned to the vocals |
| Other/bass onsets | **flux**, notes | *notes*: transcribed by basic-pitch; real pitch for rows and angles, real note lengths for arcs. Timing is about the same as flux |
| Tempo | **auto**, constant, variable | *auto*: one BPM, unless the song really changes tempo (live band, slow parts speeding up). With stems the tempo is read from the drum stem: the BPM that puts kick and snare on the beats, and bars followed through tempo changes, used only when they sit clearly (>= 15 %) better on the drums than one BPM. Without stems the beat tracker decides, which is less precise |
| BPM override | number | forces a constant BPM (CLI `--bpm`) |

Re-analysis reuses the separated stems and the downloaded audio, so switching vocal or tempo methods
is quick. With lyrics, parts whose words repeat (a returning chorus) get the same section label even
when the arrangement changes.

## 5. How a map is made

1. **Analysis** (Python worker): beats and downbeats, a beat grid (constant, or a tempo map), audio
   padded so beat 0 is at 0 s, separated stems, onsets per stem with strength and brightness (pitch),
   drum hits labelled kick/snare/hat, energy curve, sections with labels for repeated parts, cover.
2. **Rhythm** (`RhythmSelector`): onsets snap to a 1/12-beat grid (sixteenths; triplets only for songs
   with a triplet feel), each slot scored by layer weight × strength × position in the bar × energy;
   bars are filled up to a notes-per-second target that follows the energy. The strongest isolated hits
   become doubles; before a drop the map pauses for a beat or two and lands on a double.
3. **Flow** (`FlowPlanner`): a beam search over both sabers places every note (hand, cell, cut
   direction) so the swings flow: parity (forehand/backhand) is respected, resets are avoided, the hands
   move like in curated human maps (cell and direction mix, turn angles and travel, strain per
   difficulty), rows follow the pitch, loud moments swing bigger, one saber follows the melody and the
   other the rhythm. When a part of the song comes back, a second pass makes it echo the patterns of
   its first occurrence.
4. **Expression**: angle offsets that lean with a rising or falling melody, arcs over held notes,
   chains over rolls and stutters too fast for single notes.
5. **Walls and bombs**: crouch walls before big energy jumps, dodge walls in gaps, side walls in calm
   parts; bombs that signal resets and decorate accents. Nothing is ever placed where a saber or the
   player has to be.
6. **Lights**: section palettes, notes flashing in their saber's colour, the drums playing the lasers.
7. **Output**: Info.dat (v2.1) and one v3.3 file per difficulty and mode, `song.egg`, `cover.jpg`, zip.

Every generated map is checked by the same analyzer that scores human maps; resets, wall clashes and
bombs in a saber's path are kept at zero.

## 6. Generator settings reference

Settings are stored per song (`settings.json`) and with every version. `abeat settings file.json`
writes the defaults to edit for the CLI.

### General

| Setting | Default | Meaning |
|---|---|---|
| `difficulties` | Expert, ExpertPlus | which difficulties to write |
| `modes` | none | extra modes: `OneSaber`, `90Degree`, `360Degree` |
| `density` | 1.0 | notes-per-second multiplier |
| `seed` | 1 | variation; the same seed always gives the same map |
| `beamWidth` | 64 | search width: higher = better flow, slower |
| `leadInSec` | 1.5 | note-free time at the start |
| `lights` | on | lightshow |
| `environment` | Default | `Default` (classic events) or `Pyro` (v3 group lights) |
| `walls`, `dodgeWalls`, `crouchWalls` | on | walls, single-lane walls in gaps (Normal+), overhead walls before drops (Hard+) |
| `bombs` | on | reset bombs (Normal+) and accent bombs (Hard+) |
| `arcs` | on | arcs over held melody notes |
| `chains` | on | chains over fast rolls (Hard+) |
| `angleOffsets` | on | cut angles leaning with the melody (Normal+) |
| `dropPause` | on | short pause and a double on each drop |
| `levelAuthor` | ABeat by CHDS | mapper name in the map |
| `layerWeights` | vocals 1.5, drums 0.9, other 0.6, bass 0.4, mix 0.25 (bands: mid 1.0, low 0.9, full 0.5, high 0.35) | how much each layer counts when choosing notes |
| `drumWeights` | kick 1.0, snare 1.0, hat 0.6 | extra factor for drum hits by kind (humans map kicks and snares more than hats) |
| `profileOverrides` | — | per-difficulty profile (below), for files edited by hand |

### Difficulty profiles

| Field | Easy | Normal | Hard | Expert | Expert+ | Meaning |
|---|---|---|---|---|---|---|
| NoteJumpSpeed | 10 | 11 | 13 | 16 | 18 | how fast notes approach |
| JumpDistance (m) | 18 | 20 | 22 | 24 | 26 | converted to a jump offset for the BPM |
| BaseNps / MaxNps | 1.5/2.4 | 2.1/3.3 | 2.7/4.2 | 3.3/5.4 | 4.2/7.5 | notes per second at mid energy / at most |
| MinGapSec | 0.45 | 0.3 | 0.22 | 0.16 | 0.12 | minimum time between any two notes |
| MinSameHandGapSec | 0.45 | 0.36 | 0.36 | 0.25 | 0.21 | minimum time between notes of one hand |
| Subdivision | 1 | 2 | 2 | 4 | 4 | finest grid: beats, eighths, sixteenths |
| AllowTriplets | – | – | – | yes | yes | triplets in songs with a triplet feel |
| DoubleRate | 0.15 | 0.17 | 0.17 | 0.15 | 0.22 | share of moments that become two-hand doubles |
| DotCost | 0.5 | 0.8 | 1.6 | 2.2 | 2.2 | cost of a dot note (lower = more dots) |
| BombRate | – | – | 0.04 | 0.06 | 0.08 | chance of an accent bomb on a strong one-hand hit |

### Flow weights

Costs the planner adds up for every note; higher = avoided more. The *physical* ones also score any map
in the analyzer.

| Weight | Default | What it penalises or rewards |
|---|---|---|
| `reset` | 40 | a swing with the same parity as the last (a reset) without time to re-wind |
| `slowReset` | 2.5 | the same after a long pause (allowed, mildly discouraged) |
| `angle` | 1.2 | turning away from a clean reversal of the last swing |
| `travel` | 0.9 | saber travel between swings beyond 1.25 cells |
| `visionBlock` | 2.0 | notes in the middle centre that hide what follows |
| `crossover` | 3.0 | a hand reaching into the other hand's side |
| `handClash` | 8 | the other hand cut the same cell less than 0.3 s ago (the sabers meet) |
| `tooFast` | 25 | the same hand faster than the profile's same-hand gap |
| `horizontal` | 0 | extra cost for horizontal cuts |
| `styleCell`, `styleDirection` | 0.7, 0.6 | keep the mix of cells and cut directions like curated human maps |
| `pitch` | 0.5 | note row follows the pitch of the sound |
| `emphasis` | 0.8 | strong accents prefer big vertical swings |
| `noise` | 0.6 | random variety (what the seed changes) |
| `repeat` | 0.5 | the exact same note as the hand's last one |
| `stagnation` | 1.2 | a hand staying on the same cell |
| `target` | 0.45 | pull towards per-phrase target cells (movement around the grid; repeated sections move alike) |
| `handRole` | 0.8 | a melody note on the rhythm hand or vice versa (roles swap per section) |
| `dynamics` | 1.0 | swing size follows how hard the moment hits |
| `movementStyle` | 1.0 | match the human mix of turns and travel between swings |
| `effort` | 2.0 | match the human mix of strain (effective swings per second per hand) |
| `strain` | 3.0 | moves more strenuous than the difficulty's human ceiling |
| `repetition` | 0.5 | pattern memory: bonus for playing a returning part like its first occurrence (0 = off). 0.5 makes about 20 % of repeated moments exact copies (human maps ≈10 %) |

## 7. Game modes, lights and environments

**Standard** is always written. **Extra modes** use the same difficulties:
- **One Saber**: planned again for one (right) saber that covers the whole grid; no doubles, the rhythm
  leaves the hand its recovery time, 80 % of the density.
- **90°** and **360°**: the Standard notes plus lane rotations: a 15° turn (30° in loud parts) on a note
  after a breath, every two bars (four on Easy/Normal) and at section changes. 90° stays within ±45°;
  360° keeps turning and may reverse at a new section.

The web app shows Standard; the other modes are in the zip and in ArcViewer.

**Lights**, classic (Default environment): each section gets a palette (repeated parts the same),
notes flash the side lasers in their saber's colour, kicks pulse the back lasers, snares flash the
centre, hats flicker the rings in loud parts, rings spin and zoom, colour boost in the loudest parts.
Without drum labels (no stems) the back lasers pulse on the downbeats.

**Pyro** (environment setting): PyroEnvironment with a v3 group lightshow: ambient section colour,
downbeat chases, note flashes per hand, snares in white, kicks, melody notes, hat flickers, and light
groups that turn to a new pose at every section. Its file format is checked against a curated Pyro map;
it has not been verified in the game yet.

**Arcs** connect a held melody note to the same hand's next note when the sound lasts. **Chains** follow
a note with links along its cut when a roll or stutter follows it. **Angle offsets** tilt cuts 15° (30°
on Expert+ for leaps) with a rising or falling melody.

## 8. Reading the reports

| Value | Meaning |
|---|---|
| **flow score** | 0–100, 100 = perfectly smooth; curated human maps average about 85, generated ones about 93 |
| **resets** | swings that need a re-wind (same parity twice without time); generated maps have none |
| **vision blocks / crossovers / clashes** | hidden notes, hands crossing, sabers meeting: doubles where one saber swings into the other's note or both swings start (or end) in the same spot, and one hand cutting the cell the other cut less than 0.2 s before |
| **strain** | 90th percentile of effective swings per second per hand (turns and travel make a swing count more); *spikes* = moves above the difficulty's human ceiling |
| **plays like** | the difficulty the hand movement corresponds to, from curated maps (1 Easy … 9 Expert+) |
| **turn / travel** | mean turn away from a clean back-and-forth, mean saber-tip travel between swings (cells) |
| **NJS / JD** | note jump speed and jump distance (m) |
| **F1 vs human** | note timing against an imported human map: share of notes within ±50 ms |
| **repeat** (bench) | share of note moments in repeated sections played like the first occurrence |

## 9. Command line

```bash
dotnet run --project src/Abeat.Cli -- <command> [options]
```

| Command | What it does |
|---|---|
| `generate <audio \| URL \| analysis dir>` | analyse (cached in `work/`) and write a map to `out/<Artist - Title>` + zip |
| `analyze <audio>` | analysis only, prints a summary |
| `check <map folder \| zip \| Info.dat>` | flow report for any map, generated or human |
| `settings [file.json]` | write the default generator settings to edit |
| `movement [map \| dir]` | hand movement per difficulty (`--csv`, `--write-prior` relearns the movement prior) |
| `fetch-maps` | download curated BeatSaver maps for benchmarking (`--count`, `--per-mapper`, `-o`) |
| `compare <map.zip \| folder>` | map the human map's own song and compare |
| `bench [dir]` | compare every map in a folder, write `bench.csv` |
| `synth <out.wav>` | synthetic test track with known BPM |

Options of `generate` (most also apply to `analyze`):

| Option | Meaning |
|---|---|
| `-o, --out <dir>` | output folder |
| `-d, --difficulties <list>` | `easy,normal,hard,expert,expertplus` or `all` |
| `--modes <list>` | `onesaber,90,360` |
| `--environment default\|pyro` | lighting environment |
| `--density <x>`, `--seed <n>`, `--beam <n>` | as in the settings |
| `--settings <file.json>` | generator settings file |
| `--work <dir>` | analysis folder (default `work/<name>`) |
| `--beats auto\|librosa\|beat_this` | beat tracker |
| `--no-stems` | no stem separation |
| `--roformer` | BS-RoFormer vocals |
| `--vocals flux\|notes\|lyrics`, `--lyrics <file>` | vocal onsets, lyrics to align |
| `--pitched flux\|notes` | other/bass onsets |
| `--tempo auto\|constant\|variable`, `--bpm <x>` | tempo handling |
| `--reanalyze` | ignore the cached analysis |
| `--no-lights`, `--no-walls`, `--no-zip` | leave those out |

The worker can also be run directly: `analysis/.venv/bin/abeat-analyze analyze <audio> -o <dir> [--stems demucs|roformer] [--vocals …] [--pitched …] [--tempo …]`.

## 10. Files and formats

```
data/
  songs/<id>/
    meta.json            title, artist, state, analysis options
    settings.json        current generator settings
    lyrics.txt           pasted lyrics
    taps.json            tap-along runs (seconds of song.egg)
    source/              uploaded audio
    work/                analysis: analysis.json, song.egg (padded audio), cover.jpg, stems/*.flac, download/
    reference/           imported human map (difficulty files)
    generations/<time>/  one per version: generation.json, settings.json, map/, map.zip
  playlists/<id>.json
```

A map folder holds `Info.dat` (v2.1.0, one difficulty set per mode), `<Difficulty><Mode>.dat` (beatmap
v3.3.0: notes with angle offsets, bombs, walls, arcs, chains, BPM and rotation events, classic and group
lights), `song.egg` and `cover.jpg`. Copy the folder into `Beat Saber_Data/CustomLevels`, or import the zip
with BSManager or ModAssistant-compatible tools.

`analysis.json`: times in seconds of `song.egg`, whose beat 0 is at 0 s. Tempo (BPM, tempo changes,
beats, downbeats), energy curve, sections (start, end, label, energy), onset layers (time, strength,
brightness, drum kind `k`, note end `e`), lyrics with word times, preview start.

## 11. Troubleshooting

| Problem | What to do |
|---|---|
| Banner "installing the analysis runtime" stays long | first start downloads 1–5 GB; `docker compose logs -f` shows progress |
| Banner "installing … failed" | read `docker compose logs`; often no network or no disk space. Restart to retry |
| GPU not used | check `docker compose logs` for "accelerator: …" and the worker's "ML device: …" line; NVIDIA needs the Container Toolkit and the nvidia compose file; older NVIDIA cards need `ABEAT_ACCEL=cuda12` |
| Permission errors in the bound folders | set `ABEAT_UID`/`ABEAT_GID` to your user (`id -u`, `id -g`) |
| YouTube link fails | yt-dlp may need an update: rebuild/pull the image (or `uv sync` locally) |
| Notes drift away from the music | the song may have a changing tempo: re-analyse with *tempo: variable*; or the beat tracker picked a wrong grid: try *tempo: one BPM* or a BPM override |
| Vocals mapped on instrument sounds | try *vocals: notes* or the RoFormer separator |
| Re-analysis failed | the previous analysis and versions are kept; the banner shows the error |
| ArcViewer on a phone shows nothing | open the page over https on port 8443 and accept the certificate once |

## 12. Credits and licences

ABeat is made by **CHDS**, designed and built together with **Claude** (Anthropic's AI assistant).
It builds on [beat_this](https://github.com/CPJKU/beat_this), [Demucs](https://github.com/facebookresearch/demucs),
[librosa](https://librosa.org), [torchcrepe](https://github.com/maxrmorrison/torchcrepe),
[basic-pitch](https://github.com/spotify/basic-pitch) (Apache-2.0),
[python-audio-separator](https://github.com/nomadkaraoke/python-audio-separator) with BS-RoFormer weights,
[faster-whisper](https://github.com/SYSTRAN/faster-whisper), torchaudio's MMS aligner,
[yt-dlp](https://github.com/yt-dlp/yt-dlp) and [deno](https://deno.com).
[ArcViewer](https://github.com/AllPoland/ArcViewer) by AllPoland (GPL-3.0) is downloaded at runtime and
served unmodified.
