# ABeat design

Authoritative design notes. Keep in sync with the code after architectural changes.

## Pipeline

1. **Input**: uploaded audio file or a link (YouTube / YouTube Music via yt-dlp). Links store the
   page URL; metadata comes from YouTube Music fields or an "Artist - Title [junk]" title parse.
2. **Analysis** (`analysis/`, Python, run as a subprocess by `AnalysisRunner`):
   - beat tracking: beat_this (neural) or librosa; constant-BPM grid fitted over the whole song
     (mean inlier interval, outlier-robust least squares), rounded to 0.5 BPM when equivalent
   - with stems, tempo comes from the drum stem (`tempo.drum_tempo`; stems are separated on the unpadded
     audio before the tempo step, cached padded stems are trimmed by their extra leading samples):
     the constant grid is the BPM/phase that puts the most kick-weighted attack energy on beats and
     off-beats (`fit_drum_grid`, round BPM kept within 0.2 %); `track_bars` follows tempo changes by DP
     over (bar boundary, bar length) scoring the drum energy at each bar's 16ths (beats 1, 8ths .5,
     16ths .2) with a cost on bar-length *changes* (lam 100), so a sudden slow-down that ramps back up is
     cheap while off-beat or doubled bars are not; knots merge into segments within 5 ms. Rolls, fills
     and drumless stretches give the DP nothing to lock to, and it bent the tempo through them
     (Bangaranga's intro at 172 BPM): `settle_unsupported` keeps a stretch of bars > 3 % off the song's
     tempo only when it puts >= 2x the drum energy per bar on its 16ths that steady bars would (real
     slow-downs ~3x, the intro 1.4x); otherwise every 4th beat-tracker beat (on the whole mix) replaces
     it when those join the neighbouring knots within 60 ms in whole bars without skipped or doubled
     beats, a lead-in without them gets steady bars back from where the tempo settles, and anything else
     stays as tracked (a steady fill between the same knots fitted the taps worse). Auto takes the
     map only when it puts >= 15 % more drum energy on the beats than one BPM (steady songs <= 3 %, Dara -
     Bangaranga 38 %, Coldplay - Paradise live 45 %), and falls back to the tracker path below when
     drums are in < 30 % of bars. Checked against two tap-along runs per song and strong drum onsets:
     Everlasting (steady 140, the tracker path had made 38 tempo changes) taps 43 -> 27 ms from the grid,
     strong drum hits on a 16th slot (+-15 ms) 41 -> 100 %; Bangaranga taps 48 -> 32 ms, hits 29 -> 87 %;
     Paradise hits 64 -> 95 % (human notes 17 -> 18 ms)
   - variable tempo without stems (`--tempo auto|constant|variable`, `tempo.fit_tempo_map`): tracked beats are numbered
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
     pitch drives brightness (lights, angle offsets); `lyrics`: MMS forced alignment (torchaudio) of user lyrics
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
3. **Rhythm selection** (`RhythmSelector`): onsets grouped into 1/12-beat grid slots (sixteenths, plus
   triplets only when the song has a triplet feel: `HasTripletFeel`, >= 12 % of the percussive layer's
   onset strength on eighth-triplets and 1.5x more than on sixteenth off-beats), scored by layer
   weight x strength x metric position x energy, with the "e" sixteenth (x0.6), "a" (x0.8) and
   triplets (x0.85) discounted as human maps rarely use them; bars filled to a
   notes-per-second target that follows section and local energy; strong isolated hits become doubles.
   Pulse fill: a bar whose scored slots can't reach its target (pads, breakdowns: the lead only in the
   weak full mix) is topped up on beats, then off-beat eighths, at the slot's onset time or the grid
   time (layer "pulse"), unless the bar is quieter than 0.35 energy. Tap-along runs follow an eighth
   pulse there (half-beat gaps most common, 75-100 % of taps within 40 ms of a sixteenth). Everlasting's
   quiet sections: tap recall 29-43 % -> 57-67 %, Expert F1 0.66 -> 0.68; bench unchanged (F1 0.69).
   Tried and dropped (no gain on taps): filling from locally clear onsets of any layer, halving steady
   four-on-the-floor kicks, a flatter energy->density curve, basic-pitch notes for Everlasting's other
   stem. A best-layer-per-section oracle reaches only tap F1 0.76 / 0.63 (generated 0.73 / 0.68), so
   per-section lead-layer switching has little headroom; the rest is the tapper's choice and timing.
   Slots only choose notes: each event's `Time`/`Beat` is the time of the slot's strongest onset (minus
   its layer's lag behind the drum stem, `LayerLag`: median offset of strong onsets from the sixteenth
   grid relative to the drums, e.g. flux vocals ~+16 ms), so maps carry fractional beats. `GridBeat`
   keeps the slot position for structure (`BeatInSection`, phrase steps, section repeats, drops). The
   min gap is checked in seconds on those times; an onset off its slot by more than ~0.3 of a grid step
   still contributes nothing, which keeps lower difficulties to their subdivision.
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
   - musical: accents prefer vertical swings; dynamics (`FlowWeights.Dynamics`): swing size
     (distance from the centre columns + move from the hand's last cell) follows `Intensity`, zero-mean
     so the cell mix still matches the style prior. Note height never follows the audio (brightness or
     loudness): rows come only from the style prior, figure vocabulary, phrase targets and flow
   - figure vocabulary (`FlowWeights.Figure`, 25): a figure (hand, cell, direction; One Saber: either
     hand's) or double shape (left figure, right figure) outside the difficulty's vocabulary in
     `style-prior.json` pays this, so it only appears when nothing else fits (0 % on generated maps).
     Vocabulary = used >= 2 times by >= 10 % of the difficulty's curated maps (doubles: 5 %), hands
     pooled with their mirror image. Curated playlist 1116456 + map 37114 (89 maps): Easy 30 figures
     per hand (7 top-row), Normal 39 (7), Hard 48 (11), Expert 70 (20), Expert+ 96 (30), covering
     97-99.7 % of human notes; top-row share 18 / 21 / 24 / 26 / 26 %
   - pattern memory (`FlowWeights.Repetition`): when a section label repeats, the song is planned a second
     time and every event pays -Repetition for the exact cut (hand, cell, direction) the same position of
     the label's first occurrence got in the first plan (first occurrences are pulled to their own cuts,
     so the copy source stays put). `RepetitionAnalyzer` measures it (share of note moments in repeats
     that match the first occurrence; `abeat bench` "repeat g/h"): weight 0.5 makes 21 % of repeated moments exact copies (human maps 10 %, no repetition pass 5 %; 1.5 gave 50 %) at no cost in flow or timing
   - variety: stagnation, per-phrase target cells keyed by section label (`PhraseTargets`, drawn with
     the difficulty's human cell shares), so repeated sections reuse similar movement; seeded hash noise
   - style: distribution matching against `style-prior.json` (cut directions, cells per hand, learned
     from curated maps by `scripts/style_prior.py`, which reads map folders or zips); each beam path tracks running counts and pays
     log(running share / human share), so the mix matches humans instead of collapsing to the mode
   - hand roles (`FlowWeights.HandRole`, `HandRoles`): lead/support split as players tap along. Runs of
     one section label whose mean event energy is >= 80 % of the loudest run (the drops) are led by the
     drop hand (odd seeds: left; one hand for all difficulties), all other runs by the other hand, so
     the hands swap places where the song changes character; the support hand takes bar downbeats.
     Soft (1.5 per single note on the wrong hand), so flow still wins; the CLI reports the share of
     notes that kept their role. Tuned against tap-along runs (Bangaranga, Everlasting): per-note hand
     agreement with the taps 40-59 % -> 53-77 %, lead-hand share per section 52-63 % -> 61-79 % (taps 65-80 %).
     Stem-based roles (melody vs rhythm hand) did not match: taps don't split by stem
   - bursts (`DifficultyProfile.BurstGapSec`, Easy..Expert+ 0.3 / 0.18 / 0.14 / 0.12 / 0.1 s): curated maps
     put 5-19 % of a hand's gaps under our regular same-hand gap (in ~70 % of maps), ~70 % of them clean
     back-and-forth flips, runs of 2 (p90 4-7), one grid step finer than the level's usual (eighths on
     Hard/Expert, sixteenths on Expert+). A same-hand gap under `MinSameHandGapSec` skips the TooFast cost
     when it is >= the burst gap, turns <= 45° from a clean reversal, travels within the free slack, isn't a
     reset and keeps the run (`HandState.BurstRun`, part of the beam merge key) at <= `BurstNotes` (4, Expert+
     6); it pays `FlowWeights.Burst` (0: the effort/strain priors, learned from curated maps with their bursts,
     set the rate). 4 bench songs: 5-8 % of same-hand gaps on Easy/Normal/Hard/Expert+ (Expert 1.6 %: its
     events rarely fall between 0.12 and 0.18 s), 90-100 % flips, flow unchanged, no resets; a -1 bonus gave
     8-13 % with twice the strain spikes. Burst gap = same-hand gap turns bursts off (UI "burst gap")
   - note jump speed and jump distance (`DifficultyProfile`) are the curated medians, Easy..Expert+:
     NJS 12 / 13 / 14 / 16 / 17.5, jump distance 23 / 21 / 20 / 19.5 / 18 m. Humans shorten the jump
     as levels get faster (less on screen in dense streams); the old defaults grew it 18 -> 26 m and were
     1-2 NJS slower below Expert. Dots cost 1.6 on Easy/Normal too (were 0.5/0.8: 8-9 % dots vs curated 2-3 %)
   - speed defaults follow the curated playlist (median moments/s per map, 5th percentile of gaps;
     Easy/Normal/Hard/Expert/Expert+): base density 1.5/2.4/3.1/3.7/4.5 notes/s, any-hand gap
     0.35/0.2/0.15/0.12/0.1 s, same-hand gap 0.45/0.3/0.24/0.18/0.13 s (curated same-hand p5
     0.47/0.33/0.23/0.21/0.18, p1 0.43/0.24/0.23/0.18/0.13). Bench (20 older maps): 3.6 vs 3.5 human
     notes/s, F1 0.68 -> 0.69 (recall 0.67 -> 0.73), flow 91.0 -> 89.5 (human 85.1). Taps sit at 0.20-0.25 s per hand (eighths at 136-140 BPM). Human maps
     go further: Teuflum's Falling (126 BPM) Expert+ has one-hand sixteenth bursts (0.119 s, direction
     flips, runs up to 8) in 27 % of its same-hand gaps (Expert 8 %, Hard none) and ~11 notes/s in drops
     (ours ~4.3). Per song, `GeneratorSettings.ProfileOverrides` (UI "Speed and density") replaces any of
     BaseNps / MaxNps / MinGapSec / MinSameHandGapSec per difficulty; unset fields keep the defaults
     (Expert+ 0.11 / 0.1 gaps generated Falling with bursts and no resets)
   - movement (`movement-prior.json`, learned by `abeat movement --write-prior`): each move from a
     hand's last swing (gap <= 1.5 s) falls into a turn-angle x tip-travel bucket and a strain bucket;
     both are distribution-matched like the style prior (`MovementStyle`, `Effort`), and moves above
     the difficulty's human strain p98 pay `Strain`. Effort matching is what makes one saber take
     quick runs instead of strict hand alternation (there is no alternation bonus)
   - parity: vertical/diagonal swings fix forehand/backhand; horizontal cuts free it; a swing within
     60° of the previous one is a reset; travel within 1.25 cells is free; turns cost
     `TurnCost` (cheap up to 45°, steep beyond, as humans time them)
   - hand clashes: a cut in the cell the other hand cut < 0.3 s before pays `HandClash` (full below
     0.2 s; curated maps 0.6 per 1000 notes there); doubles must not swing into each other's note
     (`DoubleClash`) nor start or end their swings within half a cell of each other (L↖ x1 + R↗ x2)
   - expression (`Expression`, after planning, never changes hands/cells/directions): single
     melody notes that continue a pitch line (two steps the same way, >= 0.06) get a 15° angle offset
     (30° for leaps on Expert+): rising leans vertical cuts "/" and lifts horizontal cut ends
     (`AngleOffsets`, Normal+); arcs (v3 sliders, `Arcs`, added after walls and bombs): of all same-hand
     gaps of 1-4 beats (>= 0.35 s), single melody notes whose sustain covers >= 30 % of the gap are
     ranked by that cover and the best `ArcShare` of the gaps (Easy 5 %, Normal/Hard 9 %, Expert 12 %,
     Expert+ 13 %: medians of curated maps that use arcs; 93 % of curated arcs join a note to the same
     hand's next one, median 1 beat) get an arc to the next note, unless that swing is a reset or a
     gameplay wall passes in between. Generated maps went from ~0 to 4-13 arcs/min (curated median
     1 / 6 / 8 / 10 / 11 per minute Easy..Expert+); chains (v3 burst sliders,
     `Chains`, Hard+) on notes followed by a fast run in their layer or the drums (`RhythmEvent.BurstCount`:
     onsets each < min(100 ms, 0.85 sixteenth) apart, so steady sixteenth hats never count): links continue
     the head's cut for 2 cells (1 if only that fits), last 1/16-1/4 beat, need a same-hand gap >= 1 beat
     and free cells; at most one per 120 / 50 / 30 s on Hard / Expert / Expert+ (curated maps average 0.5 /
     1.0 / 1.6 a minute, four in five none; one per 8-16 beats had made ~3.4/min), doubles get two chains or none
5. **Walls** (`WallGenerator`): crouch walls before energy jumps (Hard+, `CrouchWalls`, off by default:
   2-10 % of curated maps have any full-width overhead wall), dodge walls in note-free gaps (Normal+),
   side walls in calm sections, and rhythm walls (`RhythmWalls`): 1/8-beat walls at lane 0/3, y 2,
   height 3 (top row and above, clear of the other rows' notes), alternating sides, on the strongest
   kick/snare hits (drum strength above the song's median; `low` band onsets without stems), strongest
   first, >= 1.2 s apart in sections >= 80 % of the loudest section's energy and 2.4 s from 55 % (Easy
   twice that): ~30-40 a minute in loud songs.
   Curated maps: side-lane walls in 80-93 % of maps, median 22-51 a minute, mostly 1/8 beat at y 2 /
   height 3, half on a note moment and half between. All walls are rejected if a note is inside them
   (±0.25 beat), and bombs are never placed inside a wall.
6. **Bombs** (`BombGenerator`): reset bombs where the natural reversal would cut, accent bombs on
   strong single-hand hits (Hard+); checked against `SaberPath` so no bomb is in a swing path.
7. **Lights** (`LightingGenerator`): section palettes (repeated labels share colours), downbeat
   pulses, per-note laser flashes, ring spins/zooms, colour boost in the loudest sections. With
   labelled drum hits the drummer plays the lights instead of the downbeat pulses: kicks (s >= 0.3)
   pulse the back lasers, snares flash the centre, hats flicker the rings in loud sections (each
   light at most every quarter beat). Build-ups (`SongShape.BuildUps`: from the start of the section
   a drop ends, at most 16 beats, to the drop; drops are `SongShape.Drops`, shared with the drop pause)
   spin the rings every bar, then beat, then half beat, with laser speeds rising 2 -> 8 and the back
   lasers strobing; drops flash every light white (values 9-12), zoom the rings and switch the boost on.
   **Environments** (`EnvironmentCatalog`, `GeneratorSettings.Environment`): all 46 environments of
   moddable Beat Saber (up to 1.40; GlassDesert stays the 90/360 one), each with a display name, a
   lighting system and a character (drive: band backbeat .. four-on-the-floor, intensity, darkness,
   typical BPM), plus the light layout learned by `scripts/environment_prior.py` into the embedded
   `environment-prior.json`: for 14 well-rated (curated first) maps per environment, read through
   HTTP range requests (only the .dat files; cached in `work/env-survey`), the basic event types used
   by >= 15 % of maps with value ranges of the special ones, and per light group the share of maps
   lighting / rotating / translating it, the axes, 90th-percentile magnitudes (angles normalized to
   ±180°) and a light-count lower bound from filters. Classic environments use `LightingGenerator`
   plus their extra channels: 6/7 flicker with the hats, 10/11 hold the melody, set pieces 16-19 go up
   on loud sections and drops and down in quiet ones (value ranges <= 3 are selectors, e.g.
   Interscope's cars: odd type on loud, even on quiet). Group environments (Weave and later) use
   `GroupLightshow`: groups lit by >= 30 % of maps, consecutive ids used alike paired as left/right
   units, roles by prominence (kicks, ambient section wash that breathes in quiet parts, note flashes
   per hand, white snares, melody holds, downbeat chases, hat flickers; fallbacks share units when an
   environment has few, extra units join the chases). Groups rotated / translated by >= 30 % of maps
   move along their main axis within their learned range (rotation capped at 90°): slow 16-beat drifts
   in quiet sections, downbeat swings fanned out with energy in louder ones, bar pumps for rails; over a
   build-up they fold together (fan closed, rails pulled in) while the chases speed up and the ambient
   strobes to white, and on the drop everything flashes white and bursts open. With two or more rotating
   groups the most used one traces the melody (onset brightness within the song's 10th-90th percentile -> tilt). Special events 40-43 fire on
   drops with their most common value. Standard/One Saber difficulties of group environments drop the
   classic events (those types mean other things there); 90/360 keep them for GlassDesert. Rotation /
   translation boxes hold the previous value at the box beat and ease in-out to the target, so a move
   starts on its beat. `"Auto"` (default) picks by `SongShape.Character` (drive from the share of loud
   grid beats with a kick, darkness from mean onset brightness 0.8 -> 0.64, intensity from BPM, loud
   share and drum rate) against each environment's character, tempo compared up to doubling, +0.25 for
   group environments and a small seed jitter so other seeds can land on another fitting one. Not
   verified in game.
   **Colours** (`CoverPalette`, `CoverColors` on by default, `ColorOverrides`): the cover (decoded with
   StbImageSharp, sampled to ~96 px) is binned into 24 hues weighted by saturation x value (pixels under
   0.25 of either ignored; under 4 % colourful coverage keeps the game colours); the top hue and the top
   one >= 70° away with >= 12 % of its weight (else +150°) are the sabers, the one nearer 15° (red-orange)
   on the left; lights use the same hues (saturation >= 0.75), boost lights a third cover hue >= 40° from
   both (else the saber hues turned by 30°), walls the top hue, white a 15 % tint of it. Hand-set slots
   ("#rrggbb") apply over the cover's colours or, with those off, over the game defaults; the UI's Detect
   copies the cover palette into the overrides. Written as an Info v2.1 `_colorSchemes` entry (plus
   `_environmentNames` [environment, GlassDesert] so 90/360 keep theirs) and SongCore `_colorLeft` ...
   `_obstacleColor` per difficulty. An uploaded cover (`POST /api/songs/{id}/cover`, JPEG/PNG by magic
   bytes, <= 10 MB) is stored as `songs/{id}/cover.jpg|png` beside `work/` so re-analysis keeps it;
   `SongStore.Analysis` points `Cover` at it (`../cover.png`, so writers use only the file name);
   `DELETE` restores the original.
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
and bomb-hit checks; hand clashes are clashing doubles and same-cell cuts by the other hand < 0.2 s apart. Flow score = 100 * exp(-mean cost / 7), calibrated so curated human maps average ~85.
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
  (current settings), `reference/` (human map: its .dat files and cover; `reference.zip` is built from them
  with `source/` audio for `map.zip?version=human`) and `generations/{yyyyMMdd-HHmmss-fff}/`
  (`Generations`: `generation.json` with app version, draft flag and the BPM/padding it was written
  on, its `settings.json`, `map/` + `map.zip`). File based; survives restarts via the Docker volume.
  A pre-history `map/` folder is moved into `generations/` on first access.
- `ABEAT_MARK_AI=true`: `Generations.Save` appends " (AI)" to the level author (map files have no AI flag).
- `AnalysisQueue`: one analysis at a time; with `ABEAT_AUTO_GENERATE=true` (default off) it then generates
  with the song's saved settings (also for `/admin/import`), otherwise maps are made from the song page. Every analysis writes `work.next/` (seeded with the cached
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
  (`ABEAT_HTTPS_PORT`, self-signed LAN certificate; `/api/config` reports `ABEAT_HTTPS_PUBLIC_PORT` when the
  container publishes it under another host port). Routing runs after the static files so the SPA
  fallback doesn't swallow `/arcviewer/`.
- Machine-local UI extensions: `src/abeat-ui/src/extensions.ts` eagerly globs the git-ignored
  `src/local/*/index.ts`; an extension's `versionLinks(song, version, zipUrl)` adds buttons next to Zip/ArcViewer.
  Nothing under `src/local/` (or files it relies on that a local tool installs) belongs in the repo.
- Lyrics: `GET|PUT /api/songs/{id}/lyrics` (`lyrics.txt`), passed to the worker on re-analysis.
- Tap along: `GET|PUT /api/songs/{id}/taps` (`taps.json`: run 0 right hand, run 1 left, song.egg seconds);
  the UI scores them in `utils/taps.ts` (per-hand median offset removed, ±70 ms matching).
- Reference maps: `POST /api/admin/import {path}` (loopback only) imports an analysis work dir or a
  human map folder with its `abeat-work` analysis; the human map is kept in `reference/` and served as
  version `human`. From the UI: `POST /api/songs` with a `.zip` and `POST /api/songs/url` with a BeatSaver
  link / `!bsr` / bare key (`BeatSaverClient.ParseKey`, `ByKeyAsync` on `maps/id/{key}`, then the
  version's zip) both go through `SongStore.ImportMapZip`: unzip under `data/incoming/` (<= 1 GB
  unpacked, entries outside refused), the shortest-path `Info.dat` is the map, the song file must sit
  inside it, then `Import` with the add form's analysis options; the song is queued for analysis.
- Song page comparison: A (gold) and B (violet) each pick a version + difficulty; with both shown every
  timeline lane splits into an A row (top) and a B row (bottom), notes without a counterpart in the
  other version (±50 ms) are ringed in their version's colour and marked on an "only" strip, and the
  player view shows both grids.
- UI: React SPA with ABook's layout (collapsible sidebar, theme toggle) and a Beat Saber palette. The
  sidebar lists the 10 songs with the most recent generated version (`lastGeneratedUtc` on `GET
  /api/songs`, computed from the newest saved version, else the add time); the **Songs** page (`/songs`)
  lists and filters all of them
  (blue saber = accent, red saber = secondary). Canvases redraw per animation frame from the
  `<audio>` element's current time.
