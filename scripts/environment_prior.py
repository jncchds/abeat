#!/usr/bin/env python3
"""Survey how curated BeatSaver maps light each Beat Saber environment.

For every environment the game offers, reads the lightshows of up to N well-rated maps that use it
(only the .dat files are fetched from each zip, via HTTP range requests; cached under
work/env-survey) and writes src/Abeat.Core/Generation/environment-prior.json:

- classic environments: the share of maps using each basic event type, and value ranges of the
  ring / laser-speed types;
- group environments (v3/v4 lightshows): per light group, the share of maps lighting / rotating /
  translating it, how often it is switched on, the axes and magnitudes used, a lower bound on its
  light count from filters, the poses mappers move it to (angles per rotation axis, home first: the one
  it rests at longest, with the share of time it rests there; distance percentiles per axis) and its
  mirror partner and how often it turns; per environment the median length of eased rotations / translations,
  and how the basic light types (0-7, plus 8-12 where the environment switches lights with them, e.g. Lizzo's
  peaches and letters) are used next to the groups (e.g. Pyro's runway and projectors), plus the step types
  (STEP_TYPES: The Second's ring spacing) as mappers pulse them.

    python3 scripts/environment_prior.py [maps per environment, default 14]
    python3 scripts/environment_prior.py --lit-only   # only (re)compute the lit shares from the cache

Per light group and basic light type it also stores `lit`: the median share of the playing time (first to
last note) the maps keep it lit, maps that never light it counting as dark (runways and floors mappers keep
on vs lights they only flash).
"""
import collections
import statistics
import io
import json
import os
import sys
import time
import urllib.request
import zipfile

API = "https://api.beatsaver.com"
UA = "ABeat/0.1 (+https://github.com/jncchds/abeat)"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CACHE = os.path.join(ROOT, "work", "env-survey")
OUT = os.path.join(ROOT, "src", "Abeat.Core", "Generation", "environment-prior.json")

ENVIRONMENTS = [
    "DefaultEnvironment", "OriginsEnvironment", "TriangleEnvironment", "NiceEnvironment", "BigMirrorEnvironment",
    "DragonsEnvironment", "KDAEnvironment", "MonstercatEnvironment", "CrabRaveEnvironment", "PanicEnvironment",
    "RocketEnvironment", "GreenDayEnvironment", "GreenDayGrenadeEnvironment", "TimbalandEnvironment",
    "FitBeatEnvironment", "LinkinParkEnvironment", "BTSEnvironment", "KaleidoscopeEnvironment",
    "InterscopeEnvironment", "SkrillexEnvironment", "BillieEnvironment", "HalloweenEnvironment", "GagaEnvironment",
    "GlassDesertEnvironment",
    "WeaveEnvironment", "PyroEnvironment", "EDMEnvironment", "TheSecondEnvironment", "LizzoEnvironment",
    "TheWeekndEnvironment", "RockMixtapeEnvironment", "Dragons2Environment", "Panic2Environment", "QueenEnvironment",
    "LinkinPark2Environment", "TheRollingStonesEnvironment", "LatticeEnvironment", "DaftPunkEnvironment",
    "HipHopEnvironment", "ColliderEnvironment", "BritneyEnvironment", "Monstercat2Environment",
    "MetallicaEnvironment", "GridEnvironment", "ColdplayEnvironment", "ProdigyEnvironment",
]

# maps the user picked as references for an environment's style, surveyed before the search results
PINNED = {"TheWeekndEnvironment": ["37114"]}  # Taylor Swift - Style (Scorefam)
# basic event types whose value is a position step rather than a light, a speed or a trigger (game
# facts: The Second's small rings sit value x 0.5 m apart, values clamped to 0..9)
STEP_TYPES = {"TheSecondEnvironment": {9: (0, 9)}}


def get_json(url):
    req = urllib.request.Request(url, headers={"User-Agent": UA})
    with urllib.request.urlopen(req, timeout=60) as r:
        return json.load(r)


class RangeFile(io.RawIOBase):
    """Seekable read-only view of a remote file through HTTP range requests (64 KiB blocks)."""
    BLOCK = 1 << 16

    def __init__(self, url):
        self.url, self.pos, self.blocks = url, 0, {}
        req = urllib.request.Request(url, headers={"User-Agent": UA, "Range": "bytes=0-0"})
        with urllib.request.urlopen(req, timeout=60) as r:
            if r.status != 206:
                raise IOError("no range support")
            self.size = int(r.headers["Content-Range"].split("/")[1])

    def seekable(self): return True
    def readable(self): return True
    def tell(self): return self.pos

    def seek(self, off, whence=0):
        self.pos = off if whence == 0 else self.pos + off if whence == 1 else self.size + off
        return self.pos

    def block(self, i):
        if i not in self.blocks:
            a = i * self.BLOCK
            b = min(self.size, a + self.BLOCK) - 1
            req = urllib.request.Request(self.url, headers={"User-Agent": UA, "Range": f"bytes={a}-{b}"})
            with urllib.request.urlopen(req, timeout=60) as r:
                self.blocks[i] = r.read()
        return self.blocks[i]

    def read(self, n=-1):
        if n < 0:
            n = self.size - self.pos
        out = bytearray()
        while n > 0 and self.pos < self.size:
            blk = self.block(self.pos // self.BLOCK)
            off = self.pos % self.BLOCK
            chunk = blk[off:off + n]
            out += chunk
            self.pos += len(chunk)
            n -= len(chunk)
        return bytes(out)

    def readinto(self, b):
        data = self.read(len(b))
        b[:len(data)] = data
        return len(data)


def fetch_dats(map_id, url):
    """The map's .dat files (name -> bytes), cached on disk."""
    folder = os.path.join(CACHE, map_id)
    if os.path.isdir(folder) and os.listdir(folder):
        return {n: open(os.path.join(folder, n), "rb").read() for n in os.listdir(folder)}
    try:
        src = RangeFile(url)
    except IOError:
        # the CDN sometimes ignores ranges; then fetch the whole zip
        req = urllib.request.Request(url, headers={"User-Agent": UA})
        with urllib.request.urlopen(req, timeout=120) as r:
            src = io.BytesIO(r.read())
    z = zipfile.ZipFile(src)
    files = {}
    for n in z.namelist():
        if "/" in n or not n.lower().endswith(".dat"):
            continue
        files[n] = z.read(n)
    os.makedirs(folder, exist_ok=True)
    for n, data in files.items():
        with open(os.path.join(folder, n), "wb") as f:
            f.write(data)
    return files


def candidates(env, count):
    """Well-rated maps declaring the environment: curated first, then the best-rated others."""
    seen, out = set(), []
    for key in PINNED.get(env, []):
        try:
            d = get_json(f"{API}/maps/id/{key}")
        except Exception as e:
            print(f"  {key}: {e}", file=sys.stderr)
            continue
        v = d["versions"][0]
        seen.add(d["id"])
        out.append((d["id"], v["downloadURL"], any(x.get("chroma") for x in v["diffs"])))
    for curated in ("&curated=true", ""):
        for page in range(6):
            if len(out) >= count:
                return out
            try:
                docs = get_json(f"{API}/search/text/{page}?sortOrder=Rating&environments={env}{curated}")["docs"]
            except Exception as e:
                print(f"  search failed: {e}", file=sys.stderr)
                break
            if not docs:
                break
            for d in docs:
                v = d["versions"][0]
                diffs = v["diffs"]
                if d["id"] in seen or d.get("automapper") or d.get("declaredAi", "None") != "None":
                    continue
                if any(x.get("ne") or x.get("me") for x in diffs) or not any(x.get("environment") == env for x in diffs):
                    continue
                if not curated and d["stats"].get("score", 0) < 0.7:
                    continue
                seen.add(d["id"])
                out.append((d["id"], v["downloadURL"], any(x.get("chroma") for x in diffs)))
            time.sleep(0.3)
    return out


def ci(files):
    return {n.lower(): data for n, data in files.items()}


def env_diffs(files, env):
    """(difficulty json, lightshow json or None) of every difficulty played in the environment."""
    f = ci(files)
    if "info.dat" not in f:
        return
    info = json.loads(f["info.dat"].decode("utf-8-sig"))
    seen = set()
    if "_difficultyBeatmapSets" in info:
        names = info.get("_environmentNames") or []
        for s in info["_difficultyBeatmapSets"]:
            rot = s["_beatmapCharacteristicName"] in ("90Degree", "360Degree")
            for d in s["_difficultyBeatmaps"]:
                idx = d.get("_environmentNameIdx")
                e = names[idx] if idx is not None and idx < len(names) else (
                    info.get("_allDirectionsEnvironmentName") if rot else info.get("_environmentName"))
                fn = d["_beatmapFilename"].lower()
                if e == env and fn in f and fn not in seen:
                    seen.add(fn)
                    yield json.loads(f[fn].decode("utf-8-sig")), None
    else:
        names = info.get("environmentNames") or []
        for d in info.get("difficultyBeatmaps", []):
            idx = d.get("environmentNameIdx", 0)
            if idx >= len(names) or names[idx] != env:
                continue
            fn, ln = d.get("beatmapDataFilename", "").lower(), d.get("lightshowDataFilename", "").lower()
            if (fn, ln) in seen or fn not in f:
                continue
            seen.add((fn, ln))
            yield json.loads(f[fn].decode("utf-8-sig")), json.loads(f[ln].decode("utf-8-sig")) if ln in f else None


class Stats:
    def __init__(self):
        self.maps = 0
        self.basic = collections.Counter()           # event type -> maps
        self.basic_values = collections.defaultdict(collections.Counter)
        self.groups = collections.defaultdict(lambda: {
            "color": 0, "rotation": 0, "translation": 0, "fx": 0,
            "rot_axes": collections.Counter(), "tr_axes": collections.Counter(),
            "rot_max": [], "tr_max": [], "lights": collections.Counter(), "colors": collections.Counter(), "events": 0,
            "rot_vals": collections.defaultdict(list), "tr_vals": collections.defaultdict(list),
            "rot_axis_maps": collections.Counter(), "rot_turn_axes": collections.Counter(),
            "rot_held": collections.defaultdict(collections.Counter), "rot_held_maps": collections.Counter(),
            "tr_first": collections.defaultdict(list),
        })
        # (group, other group) -> [events of the group with an event of the other at the same beat, identical ones]
        self.pairs = collections.defaultdict(lambda: [0, 0])
        self.ease = {"rotation": [], "translation": []}  # beats each eased move takes
        self.pair_maps = collections.defaultdict(set)
        self.on_rates = collections.defaultdict(list)    # group -> per map: beats it is switched on / song beats
        self.turn_rates = collections.defaultdict(list)  # group -> per map turning it: beats it turns to a pose / song beats
        self.move_rates = collections.defaultdict(list)  # group -> per map moving it: beats it moves to a position / song beats
        # basic light type -> per map events / song beats; value classes; identical-event partners
        self.basic_rates = collections.defaultdict(list)
        self.basic_kinds = collections.defaultdict(collections.Counter)
        self.basic_all = collections.defaultdict(collections.Counter)  # type 8-12 -> every value it takes
        self.basic_pairs = collections.defaultdict(list)  # (type, other) -> per map [same-beat events, identical]
        self.steps = collections.defaultdict(list)       # type 8-13 -> per map (events / song beats, low, high)
        # per map: share of the playing time each group / basic light type is lit (maps leaving it dark count 0)
        self.lit_maps = 0
        self.lit_groups = collections.defaultdict(list)
        self.lit_basic = collections.defaultdict(list)

    def add_lit(self, shares):
        if shares is None:
            return
        groups, types = shares
        self.lit_maps += 1
        for g, s in groups.items():
            self.lit_groups[g].append(s)
        for t, s in types.items():
            self.lit_basic[t].append(s)

    def lit(self, values):
        """Median lit share over the maps measured, the ones that never light it counting as dark."""
        return round(statistics.median(values + [0.0] * (self.lit_maps - len(values))), 2) if self.lit_maps else None

    def add(self, basic, color, rotation, translation, fx):
        """basic: [(type, value, float, beat)]; color: [(group, filter, [color], beat, switched on)];
        rotation/translation: [(group, filter, axis, [magnitude], beat, [(beat, easing, previous)])]; fx: [group]."""
        self.maps += 1
        for t in {b[0] for b in basic}:
            self.basic[t] += 1
        for t, v, fl, _ in basic:
            if t in (8, 9, 12, 13) or t >= 14:
                self.basic_values[t][v] += 1
        span = max([b[3] for b in basic] + [c[3] for c in color] + [r[4] for r in rotation + translation] + [1])
        on = collections.defaultdict(set)
        for g, _, _, b, lit in color:
            if lit:
                on[g].add(round(b, 3))
        for g, beats in on.items():
            self.on_rates[g].append(len(beats) / span)
        self.add_basic_lights(basic, span)
        self.add_steps(basic, span)
        used = collections.defaultdict(set)
        hints = collections.defaultdict(int)
        rot_axes = collections.defaultdict(set)
        for g, filt, colors, _, _ in color:
            s = self.groups[g]
            used[g].add("color")
            s["events"] += 1
            hints[g] = max(hints[g], lights_hint(filt))
            for c in colors:
                s["colors"][c] += 1
        for kind, rows in (("rotation", rotation), ("translation", translation)):
            for g, filt, axis, mags, _, keys in rows:
                s = self.groups[g]
                used[g].add(kind)
                hints[g] = max(hints[g], lights_hint(filt))
                s["rot_axes" if kind == "rotation" else "tr_axes"][axis] += 1
                # keyframes that only hold the previous value carry no pose
                mags = [m for m, (_, _, prev) in zip(mags, keys) if not prev]
                if mags:
                    if kind == "rotation":  # angles are 0..360; 330 is -30
                        mags = [(m + 180) % 360 - 180 for m in mags]
                        # only real turns: mappers' tools hold axes the group lacks
                        rot_axes[g].add(axis)
                        s["rot_turn_axes"][axis] += 1
                    s["rot_max" if kind == "rotation" else "tr_max"].append(max(abs(m) for m in mags))
                    s["rot_vals" if kind == "rotation" else "tr_vals"][axis].extend(mags)
        self.add_held(rotation, span)
        turns = collections.defaultdict(set)
        for g, _, _, mags, b, keys in rotation:
            turns[g].update(round(b + kb, 3) for kb, _, prev in keys if not prev)
        for g, beats in turns.items():
            self.turn_rates[g].append(len(beats) / span)
        self.add_first(translation)
        moves = collections.defaultdict(set)
        for g, _, _, mags, b, keys in translation:
            moves[g].update(round(b + kb, 3) for kb, _, prev in keys if not prev)
        for g, beats in moves.items():
            self.move_rates[g].append(len(beats) / span)
        self.add_pairs(color, rotation, translation)
        self.add_eases(rotation, translation)
        for g, h in hints.items():
            self.groups[g]["lights"][h] += 1
        for g, axes in rot_axes.items():
            for a in axes:
                self.groups[g]["rot_axis_maps"][a] += 1
        for g in fx:
            used[g].add("fx")
        for g, kinds in used.items():
            for k in kinds:
                self.groups[g][k] += 1


    def add_basic_lights(self, basic, span):
        """Basic light types (0-12; 8-12 only count as lights in summarize when their values say so): how often
        each fires, which values (off / on / flash / fade / transition, colour) and which other type gets the
        same events (mirrored sides)."""
        for t, v, _, _ in basic:
            if 8 <= t <= 12:
                self.basic_all[t][v] += 1
        lights = [(t, v, fl, b) for t, v, fl, b in basic if 0 <= t <= 12 and 0 <= v <= 12]
        per = collections.Counter(t for t, *_ in lights)
        for t, n in per.items():
            if n >= 0.9 * sum(1 for x in basic if x[0] == t):
                self.basic_rates[t].append(n / span)
        at, pairs = collections.defaultdict(dict), collections.defaultdict(lambda: [0, 0])
        for t, v, fl, b in lights:
            k = self.basic_kinds[t]
            k["off" if v == 0 else ("on", "flash", "fade", "transition")[(v - 1) % 4]] += 1
            if v:
                k["white" if v > 8 else "color"] += 1
            at[round(b, 3)][t] = (v, round(fl, 2))
        for evs in at.values():
            for t, e in evs.items():
                for u, e2 in evs.items():
                    if t != u:
                        pairs[(t, u)][0] += 1
                        pairs[(t, u)][1] += e == e2
        for k, p in pairs.items():
            self.basic_pairs[k].append(p)

    def add_steps(self, basic, span):
        """Types 8-13 read as steps: how often they change and the two values mappers pulse between (the
        most common pair of consecutive different values; else the 10th / 90th percentile)."""
        for t in range(8, 14):
            ev = sorted((b, v) for x, v, _, b in basic if x == t and 0 <= v <= 20)
            if len(ev) < 5:
                continue
            moves = collections.Counter(tuple(sorted((v1, v2))) for (_, v1), (_, v2) in zip(ev, ev[1:]) if v1 != v2)
            vals = [v for _, v in ev]
            low, high = moves.most_common(1)[0][0] if moves else (pct(vals, 0.1), pct(vals, 0.9))
            self.steps[t].append((len(ev) / span, low, high))

    def add_pairs(self, color, rotation, translation):
        """Mirror partners: groups that get the same event at the same beat (the game mirrors the right
        side itself, so mappers send both halves identical values)."""
        at = collections.defaultdict(lambda: collections.defaultdict(list))
        for g, filt, colors, b, _ in color:
            at[("c", round(b, 3))][g].append((json.dumps(filt, sort_keys=True), tuple(colors)))
        for kind, rows in (("r", rotation), ("t", translation)):
            for g, filt, axis, mags, b, _ in rows:
                at[(kind, round(b, 3))][g].append((axis, tuple(round(m, 3) for m in mags)))
        for groups in at.values():
            for g, ev in groups.items():
                for h, ev2 in groups.items():
                    if g != h:
                        p = self.pairs[(g, h)]
                        p[0] += 1
                        p[1] += sorted(ev) == sorted(ev2)
                        self.pair_maps[(g, h)].add(self.maps)

    def add_held(self, rotation, end):
        """How long each group rests at each angle (15° steps) per axis, as a share of the map's held time, so
        a group's home is where it rests, not where mappers flick it to and back (EDM's circles rest at 0°)."""
        keys = collections.defaultdict(list)
        for g, _, axis, mags, b, kk in rotation:
            for m, (kb, _, prev) in zip(mags, kk):
                if not prev:
                    keys[(g, axis)].append((b + kb, step15(m)))
        for (g, axis), ks in keys.items():
            ks.sort()
            held = collections.Counter()
            for (b, a), (nb, _) in zip(ks, ks[1:] + [(max(end, ks[-1][0]), None)]):
                held[a] += nb - b
            tot = sum(held.values())
            if tot > 0:
                self.groups[g]["rot_held_maps"][axis] += 1
                for a, n in held.items():
                    self.groups[g]["rot_held"][axis][a] += n / tot

    def add_first(self, translation):
        """Where each map starts a group's rail: the first keyframe's value when it comes in the first two bars
        (None when it comes later: the group starts where the environment puts it). The Weeknd's lamp poles and
        buildings start lowered and rise into place on the first big moment."""
        first = {}
        for g, _, axis, mags, b, keys in translation:
            for m, (kb, _, _) in zip(mags, keys):
                if (g, axis) not in first or b + kb < first[(g, axis)][0]:
                    first[(g, axis)] = (b + kb, m)
        for (g, axis), (b, m) in first.items():
            self.groups[g]["tr_first"][axis].append(m if b <= 8 else None)

    def add_eases(self, rotation, translation):
        """Length of every eased move: from a group axis' previous keyframe to a keyframe with an easing."""
        for kind, rows in (("rotation", rotation), ("translation", translation)):
            last = {}
            for g, _, axis, _, b, keys in sorted(rows, key=lambda r: r[4]):
                for kb, ease, prev in keys:
                    t = b + kb
                    if ease != -1 and not prev and (g, axis) in last and t > last[(g, axis)]:
                        self.ease[kind].append(t - last[(g, axis)])
                    last[(g, axis)] = t

    def mirrors(self):
        """group -> its mirror partner: the group it moves identically with most often (>= 70 % of shared
        beats, >= 30 times, in >= 3 maps), when that is mutual."""
        best = {}
        for (g, h), (n, same) in self.pairs.items():
            if n >= 30 and same >= 0.7 * n and len(self.pair_maps[(g, h)]) >= 3 and same > best.get(g, (None, 0))[1]:
                best[g] = (h, same)
        return {g: h for g, (h, _) in best.items() if best.get(h, (None,))[0] == g}


def light_values(values):
    """Whether a basic type 8-12 switches lights (Lizzo's peaches and letters, Britney's 8/9) rather than spinning
    rings or setting laser speeds: its values stay within the light values 0-12, take at least three different
    colours / kinds and are mostly off / on / transition (speed and ring steps spread over every number)."""
    tot = sum(values.values())
    return tot > 0 and sum(n for v, n in values.items() if 0 <= v <= 12) >= 0.95 * tot \
        and sum(1 for v in values if 1 <= v <= 12) >= 3 \
        and sum(n for v, n in values.items() if v in (0, 1, 4, 5, 8, 9, 12)) >= 0.8 * tot


def step15(a):
    """An angle rounded to 15° steps in -165..180."""
    r = int(round(((a + 180) % 360 - 180) / 15)) * 15
    return 180 if r == -180 else r


def lights_hint(f):
    """Lower bound on a group's light count from a filter: step-and-offset start index, or the
    division count when its last section is addressed."""
    if not f:
        return 0
    ftype, p, t = f.get("f", 1), f.get("p", 1), f.get("t", 0)
    if ftype == 2:
        return p + 1
    return p if p > 1 and t == p - 1 else 0


def parse_v2(j):
    basic = [(e.get("_type", 0), e.get("_value", 0), e.get("_floatValue", 1), e.get("_time", 0)) for e in j.get("_events", [])]
    return basic, [], [], [], []


def parse_v3(j):
    basic = [(e.get("et", 0), e.get("i", 0), e.get("f", 1), e.get("b", 0)) for e in j.get("basicBeatmapEvents", [])]
    color = [(g.get("g", 0), box.get("f", {}), [e.get("c", 0) for e in box.get("e", [])], g.get("b", 0),
              any(e.get("s", 1) > 0 for e in box.get("e", [])))
             for g in j.get("lightColorEventBoxGroups", []) for box in g.get("e", [])]
    rotation = [(g.get("g", 0), box.get("f", {}), box.get("a", 0), [e.get("r", 0) for e in box.get("l", [])], g.get("b", 0),
                 [(e.get("b", 0), e.get("e", -1), e.get("p", 0)) for e in box.get("l", [])])
                for g in j.get("lightRotationEventBoxGroups", []) for box in g.get("e", [])]
    translation = [(g.get("g", 0), box.get("f", {}), box.get("a", 0), [e.get("t", 0) for e in box.get("l", [])], g.get("b", 0),
                    [(e.get("b", 0), e.get("e", -1), e.get("p", 0)) for e in box.get("l", [])])
                   for g in j.get("lightTranslationEventBoxGroups", []) for box in g.get("e", [])]
    fx = [g.get("g", 0) for g in j.get("vfxEventBoxGroups", [])]
    return basic, color, rotation, translation, fx


def parse_v4(j, ls):
    src = ls or j
    data = src.get("basicEventsData", [])
    basic = [(data[e["i"]].get("t", 0), data[e["i"]].get("i", 0), data[e["i"]].get("f", 1), e.get("b", 0))
             for e in src.get("basicEvents", []) if e.get("i", 0) < len(data)]
    filters = src.get("indexFilters", [])
    cboxes, rboxes, tboxes = src.get("lightColorEventBoxes", []), src.get("lightRotationEventBoxes", []), src.get("lightTranslationEventBoxes", [])
    cev, rev, tev = src.get("lightColorEvents", []), src.get("lightRotationEvents", []), src.get("lightTranslationEvents", [])
    color, rotation, translation, fx = [], [], [], []
    for g in src.get("eventBoxGroups", []):
        gid, typ = g.get("g", 0), g.get("t", 1)
        for box in g.get("e", []):
            filt = filters[box.get("f", 0)] if box.get("f", 0) < len(filters) else {}
            evs = [x.get("i", 0) for x in box.get("l", [])]
            bi = box.get("e", 0)
            if typ == 1 and bi < len(cboxes):
                color.append((gid, filt, [cev[i].get("c", 0) for i in evs if i < len(cev)], g.get("b", 0),
                              any(cev[i].get("b", 1) > 0 for i in evs if i < len(cev))))
            elif typ == 2 and bi < len(rboxes):
                rotation.append((gid, filt, rboxes[bi].get("a", 0), [rev[i].get("r", 0) for i in evs if i < len(rev)], g.get("b", 0),
                                 [(x.get("b", 0), rev[x.get("i", 0)].get("e", -1), rev[x.get("i", 0)].get("p", 0)) for x in box.get("l", []) if x.get("i", 0) < len(rev)]))
            elif typ == 3 and bi < len(tboxes):
                translation.append((gid, filt, tboxes[bi].get("a", 0), [tev[i].get("t", 0) for i in evs if i < len(tev)], g.get("b", 0),
                                    [(x.get("b", 0), tev[x.get("i", 0)].get("e", -1), tev[x.get("i", 0)].get("p", 0)) for x in box.get("l", []) if x.get("i", 0) < len(tev)]))
            elif typ == 4:
                fx.append(gid)
    return basic, color, rotation, translation, fx


LIT = 0.05  # brightness above which a light counts as on


def note_span(j, ls=None):
    """First and last note beat of a difficulty (any format)."""
    beats = [n.get("_time", 0) for n in j.get("_notes", []) if n.get("_type", 0) != 3]
    beats += [n.get("b", 0) for n in j.get("colorNotes", [])]
    return (min(beats), max(beats)) if beats else None


def color_brightness(j, ls):
    """{group: [(beat, brightness, transition)]} from v3 / v4 colour event boxes (filters and strobes ignored)."""
    out = collections.defaultdict(list)
    v = str(j.get("version") or j.get("_version") or "2")
    if v.startswith("3"):
        for g in j.get("lightColorEventBoxGroups", []):
            for box in g.get("e", []):
                for e in box.get("e", []):
                    out[g.get("g", 0)].append((g.get("b", 0) + e.get("b", 0), e.get("s", 1), e.get("i", 0)))
    elif not v.startswith("2"):
        src = ls or j
        cev = src.get("lightColorEvents", [])
        for g in src.get("eventBoxGroups", []):
            if g.get("t", 1) != 1:
                continue
            for box in g.get("e", []):
                for x in box.get("l", []):
                    if x.get("i", 0) < len(cev):
                        e = cev[x.get("i", 0)]
                        out[g.get("g", 0)].append((g.get("b", 0) + x.get("b", 0), e.get("b", 1), e.get("p", 0)))
    return out


def lit_share(steps, span, dt=0.25):
    """Share of the span a [(beat, level)] step timeline is above LIT."""
    steps = sorted(steps)
    n = max(1, int((span[1] - span[0]) / dt))
    k, level, lit = 0, 0.0, 0
    for i in range(n):
        t = span[0] + i * dt
        while k < len(steps) and steps[k][0] <= t:
            level = steps[k][1]
            k += 1
        lit += level > LIT
    return lit / n


def lit_shares(j, ls, basic, bpm):
    """How much of the playing time (first to last note) each light group and basic light type 0-12 is lit:
    ({group: share}, {type: share}). Basic on / flash hold their brightness, fades last 1.5 s, transitions
    fading up from dark count from the previous event; group colour events hold their brightness (transitions
    likewise, "extend" keeps the previous one)."""
    span = note_span(j, ls)
    if not span or span[1] - span[0] < 32:
        return None
    groups = {}
    for g, evs in color_brightness(j, ls).items():
        steps = []
        for b, s, trans in sorted(evs):
            if trans == 2 and steps:
                s = steps[-1][1]
            if trans == 1 and steps and steps[-1][1] <= LIT < s:
                steps[-1] = (steps[-1][0], s)
            steps.append((b, s))
        groups[g] = lit_share(steps, span)
    fade = 1.5 * bpm / 60
    types = {}
    for t in {e[0] for e in basic if 0 <= e[0] <= 12}:
        steps = []
        for _, v, f, b in sorted((e for e in basic if e[0] == t), key=lambda e: e[3]):
            f = 1 if f is None else f
            if v == 0:
                steps.append((b, 0.0))
            elif v in (3, 7, 11):
                steps += [(b, f), (b + fade, 0.0)]
            elif v in (4, 8, 12):
                if steps and steps[-1][1] <= LIT < f:
                    steps[-1] = (steps[-1][0], f)
                steps.append((b, f))
            elif 1 <= v <= 12:
                steps.append((b, f))
        types[t] = lit_share(steps, span)
    return groups, types


def bpm_of(files):
    info = json.loads(ci(files)["info.dat"].decode("utf-8-sig"))
    return info.get("_beatsPerMinute") or (info.get("audio") or {}).get("bpm") or 120


def parse(j, ls):
    v = str(j.get("version") or j.get("_version") or "2")
    if v.startswith("2"):
        return parse_v2(j)
    if v.startswith("3"):
        return parse_v3(j)
    return parse_v4(j, ls)


def pct(values, q):
    if not values:
        return 0
    s = sorted(values)
    return s[min(len(s) - 1, int(q * len(s)))]


def poses(angles, held=None):
    """Angles (-180..180, 15° steps) mappers turn a group to (>= 4 % of keyframes): first the home, where
    the group rests longest (`held`: angle -> held time), then the others most used first."""
    merged = collections.Counter(step15(a) for a in angles)
    tot = sum(merged.values())
    out = [a for a, n in merged.most_common() if n >= 0.04 * tot] if tot else []
    if held and out:
        home = max(out, key=lambda a: held.get(a, 0))
        out = [home] + [a for a in out if a != home]
    return out


def summarize(env, st):
    out = {"maps": st.maps}
    if st.maps == 0:
        return out
    for kind in ("rotation", "translation"):
        if len(st.ease[kind]) >= 100:
            out[kind + "Ease"] = round(statistics.median(st.ease[kind]), 2)
    out["basic"] = {str(t): round(n / st.maps, 2) for t, n in sorted(st.basic.items()) if n / st.maps >= 0.15}
    vals = {}
    for t, c in sorted(st.basic_values.items()):
        if str(t) in out["basic"]:
            vals[str(t)] = [min(c), max(c), max(c, key=c.get)]
    if vals:
        out["basicValues"] = vals
    lights = {}
    for t, rates in sorted(st.basic_rates.items()):
        # only next to light groups (classic environments light with these types anyway)
        if len(rates) < 0.3 * st.maps or not any(g["color"] for g in st.groups.values()):
            continue
        if t >= 8 and not light_values(st.basic_all[t]):
            continue
        k = st.basic_kinds[t]
        tot, lit = sum(k[c] for c in ("off", "on", "flash", "fade", "transition")), k["white"] + k["color"]
        row = {"share": round(len(rates) / st.maps, 2), "rate": round(statistics.median(rates), 3)}
        row.update({c: round(k[c] / tot, 2) for c in ("off", "on", "flash", "fade", "transition")})
        row["white"] = round(k["white"] / lit, 2) if lit else 0
        # the mirrored type: identical events (>= 70 % of shared beats) in >= 60 % of the maps (>= 3) firing both
        votes = {u: [n >= 5 and same >= 0.7 * n for n, same in p if n >= 5] for (x, u), p in st.basic_pairs.items() if x == t}
        mirror = [u for u, v in votes.items() if len(v) >= 3 and sum(v) >= 0.6 * len(v)]
        if mirror:
            row["mirror"] = max(mirror, key=lambda u: sum(votes[u]))
        if st.lit_maps:
            row["lit"] = st.lit(st.lit_basic[t])
        lights[str(t)] = row
    for row in lights.values():  # partners are mutual
        if "mirror" in row and lights.get(str(row["mirror"]), {}).get("mirror") != int(next(k for k, r in lights.items() if r is row)):
            del row["mirror"]
    if lights:
        out["basicLights"] = lights
    steps = {}
    for t, (lo, hi) in STEP_TYPES.get(env, {}).items():
        rows = st.steps[t]
        if len(rows) >= 0.3 * st.maps:
            steps[str(t)] = {"share": round(len(rows) / st.maps, 2), "rate": round(statistics.median(r[0] for r in rows), 3),
                             "low": min(hi, max(lo, int(statistics.median(r[1] for r in rows)))),
                             "high": min(hi, max(lo, int(statistics.median(r[2] for r in rows))))}
    if steps:
        out["steps"] = steps
    groups = {}
    mirrors = st.mirrors()
    for g, s in sorted(st.groups.items()):
        share = {k: round(s[k] / st.maps, 2) for k in ("color", "rotation", "translation", "fx")}
        if max(share.values()) < 0.2:
            continue
        row = {k: v for k, v in share.items() if v >= 0.2}
        # the largest light count at least three maps' filters imply (a map or two copy filters between groups)
        lights = s["lights"]
        row["lights"] = max((h for h in lights if sum(n for k, n in lights.items() if k >= h) >= min(3, sum(lights.values()))), default=0)
        if st.on_rates[g]:
            row["rate"] = round(statistics.median(st.on_rates[g]), 3)
        if st.lit_maps and share["color"] >= 0.2:
            # how much of the playing time it is lit: runways and floors mappers keep on vs lights they flash
            row["lit"] = st.lit(st.lit_groups[g])
        if s["rot_axes"] and share["rotation"] >= 0.2:
            # an axis counts when it carries 15 % of the real turns (from two maps or more), or a third of the maps
            # that turn the group really turn it about that axis with 5 % of the turns (Weave's y, Lizzo's rainbow
            # ends' y); turns about axes a group lacks are rare or one map's (Rock Mixtape's screens about x / z,
            # Hip Hop's hoops about z), and keyframes that only hold a value, as mappers' tools send them, don't count
            turned, total = s["rot_turn_axes"], sum(s["rot_turn_axes"].values())
            row["rotationAxes"] = [a for a, _ in s["rot_axes"].most_common()
                                   if turned[a] >= 0.15 * total and s["rot_axis_maps"][a] >= 2
                                   or turned[a] >= 0.05 * total and s["rot_axis_maps"][a] >= s["rotation"] / 3] \
                or [s["rot_axes"].most_common(1)[0][0]]
            row["rotationRange"] = round(pct(s["rot_max"], 0.9), 1)
            main = row["rotationAxes"][0]
            if st.turn_rates[g]:
                # how often it turns (Lizzo's rainbow and its end lasers: every few bars; its top lasers: every bar)
                row["rotationRate"] = round(statistics.median(st.turn_rates[g]), 3)
            row["rotationPoses"] = poses(s["rot_vals"][main], s["rot_held"][main])
            if row["rotationPoses"] and s["rot_held_maps"][main]:
                # the share of the time it rests at home (EDM's circle rotations: most of it, flicking out and back)
                row["homeShare"] = round(s["rot_held"][main][row["rotationPoses"][0]] / s["rot_held_maps"][main], 2)
            if len(row["rotationAxes"]) > 1:
                row["axisPoses"] = {str(a): poses(s["rot_vals"][a], s["rot_held"][a]) for a in row["rotationAxes"][1:]}
        if s["tr_axes"] and share["translation"] >= 0.2:
            row["translationAxes"] = [a for a, n in s["tr_axes"].most_common() if n >= 0.15 * sum(s["tr_axes"].values())]
            row["translationRange"] = round(pct(s["tr_max"], 0.9), 2)
            if st.move_rates[g]:
                # how often it moves (Rock Mixtape's eyes: every few beats; its back lasers: every few bars)
                row["translationRate"] = round(statistics.median(st.move_rates[g]), 3)
            row["translationValues"] = {str(a): [round(pct(s["tr_vals"][a], q), 2) for q in (0.1, 0.5, 0.9)]
                                        for a in row["translationAxes"] if s["tr_vals"][a]}
            # an axis mappers only ever set to one value carries no motion (a stray x on The Weeknd's ring)
            moving = [a for a in row["translationAxes"] if s["tr_vals"][a] and min(s["tr_vals"][a]) != max(s["tr_vals"][a])]
            if moving and len(moving) < len(row["translationAxes"]):
                row["translationAxes"] = moving
                row["translationValues"] = {str(a): row["translationValues"][str(a)] for a in moving}
            # the intro pose: >= 40 % of the maps moving the group start it beyond the distances it later moves
            # between (outside the 10th-90th percentile; median of those starts), e.g. The Weeknd's lamp poles
            # start lowered
            intro = {}
            for a in row["translationAxes"]:
                lo, _, hi = row["translationValues"].get(str(a), (0, 0, 0))
                firsts = s["tr_first"][a]
                away = [m for m in firsts if m is not None and (m < lo or m > hi)]
                if firsts and len(away) >= 0.4 * len(firsts) and len(away) >= 3:
                    intro[str(a)] = round(statistics.median(away), 2)
            if intro:
                row["translationIntro"] = intro
        if g in mirrors:
            row["mirror"] = mirrors[g]
        if s["colors"]:
            tot = sum(s["colors"].values())
            row["white"] = round(s["colors"][2] / tot, 2)
        groups[str(g)] = row
    # mirror partners hold the same lights; a map's filters may only reveal part of one side
    for g, row in groups.items():
        if str(row.get("mirror")) in groups:
            row["lights"] = max(row["lights"], groups[str(row["mirror"])]["lights"])
    if groups:
        out["groups"] = groups
    return out


def richest(files, env):
    """(events, parsed, difficulty, lightshow) of the difficulty with the richest lightshow (>= 50 events)."""
    best = None
    for j, ls in env_diffs(files, env):
        try:
            p = parse(j, ls)
        except Exception:
            continue
        n = sum(len(x) for x in p)
        if best is None or n > best[0]:
            best = (n, p, j, ls)
    return best if best and best[0] >= 50 else None


def lit_only():
    """Add the lit shares to the existing prior from the cached maps only (every cached map declaring the
    environment), leaving every other learned value as it is."""
    with open(OUT) as f:
        prior = json.load(f)
    folders = [os.path.join(CACHE, d) for d in sorted(os.listdir(CACHE)) if os.path.isdir(os.path.join(CACHE, d))]
    cached = [{n: open(os.path.join(d, n), "rb").read() for n in os.listdir(d)} for d in folders]
    for env in ENVIRONMENTS:
        out = prior.get(env)
        if not out or not (out.get("groups") or out.get("basicLights")):
            continue
        st = Stats()
        for files in cached:
            if "info.dat" not in ci(files):
                continue
            best = richest(files, env)
            if best:
                st.add_lit(lit_shares(best[2], best[3], best[1][0], bpm_of(files)))
        for g, row in out.get("groups", {}).items():
            if row.get("color", 0) >= 0.2:
                row["lit"] = st.lit(st.lit_groups[int(g)])
        for t, row in out.get("basicLights", {}).items():
            row["lit"] = st.lit(st.lit_basic[int(t)])
        print(f"{env}: {st.lit_maps} maps")
    with open(OUT, "w") as f:
        json.dump(prior, f, indent=1)
    print(f"wrote {OUT}")


def main():
    if sys.argv[1:2] == ["--lit-only"]:
        return lit_only()
    per_env = int(sys.argv[1]) if len(sys.argv) > 1 else 14
    os.makedirs(CACHE, exist_ok=True)
    prior = {}
    for env in ENVIRONMENTS:
        cands = candidates(env, per_env * 2)
        # prefer maps without Chroma (its environment tricks change how groups are used)
        cands.sort(key=lambda c: c[2])
        st, used = Stats(), 0
        for map_id, url, _ in cands:
            if used >= per_env:
                break
            print(f"  {env} {map_id}", file=sys.stderr, flush=True)
            try:
                files = fetch_dats(map_id, url)
            except Exception as e:
                print(f"  {map_id}: {e}", file=sys.stderr)
                continue
            # one difficulty per map (the richest lightshow) so long maps with many difficulties don't dominate
            best = richest(files, env)
            if best:
                st.add(*best[1])
                st.add_lit(lit_shares(best[2], best[3], best[1][0], bpm_of(files)))
                used += 1
        prior[env] = summarize(env, st)
        g = prior[env].get("groups", {})
        print(f"{env}: {st.maps} maps, {len(g)} groups, basic {list(prior[env].get('basic', {}))}")
    with open(OUT, "w") as f:
        json.dump(prior, f, indent=1)
    print(f"wrote {OUT}")


if __name__ == "__main__":
    main()
