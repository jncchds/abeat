#!/usr/bin/env python3
"""Survey how curated BeatSaver maps light each Beat Saber environment.

For every environment the game offers, reads the lightshows of up to N well-rated maps that use it
(only the .dat files are fetched from each zip, via HTTP range requests; cached under
work/env-survey) and writes src/Abeat.Core/Generation/environment-prior.json:

- classic environments: the share of maps using each basic event type, and value ranges of the
  ring / laser-speed types;
- group environments (v3/v4 lightshows): per light group, the share of maps lighting / rotating /
  translating it, the axes and magnitudes used, and a lower bound on its light count from filters.

    python3 scripts/environment_prior.py [maps per environment, default 14]
"""
import collections
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
            "rot_max": [], "tr_max": [], "lights": 0, "colors": collections.Counter(), "events": 0,
        })

    def add(self, basic, color, rotation, translation, fx):
        """basic: [(type, value, float)]; color: [(group, filter, [color])]; rotation/translation:
        [(group, filter, axis, [magnitude])]; fx: [group]."""
        self.maps += 1
        for t in {b[0] for b in basic}:
            self.basic[t] += 1
        for t, v, fl in basic:
            if t in (8, 9, 12, 13) or t >= 14:
                self.basic_values[t][v] += 1
        used = collections.defaultdict(set)
        for g, filt, colors in color:
            s = self.groups[g]
            used[g].add("color")
            s["events"] += 1
            s["lights"] = max(s["lights"], lights_hint(filt))
            for c in colors:
                s["colors"][c] += 1
        for kind, rows in (("rotation", rotation), ("translation", translation)):
            for g, filt, axis, mags in rows:
                s = self.groups[g]
                used[g].add(kind)
                s["lights"] = max(s["lights"], lights_hint(filt))
                s["rot_axes" if kind == "rotation" else "tr_axes"][axis] += 1
                if mags:
                    if kind == "rotation":  # angles are 0..360; 330 is -30
                        mags = [(m + 180) % 360 - 180 for m in mags]
                    s["rot_max" if kind == "rotation" else "tr_max"].append(max(abs(m) for m in mags))
        for g in fx:
            used[g].add("fx")
        for g, kinds in used.items():
            for k in kinds:
                self.groups[g][k] += 1


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
    basic = [(e.get("_type", 0), e.get("_value", 0), e.get("_floatValue", 1)) for e in j.get("_events", [])]
    return basic, [], [], [], []


def parse_v3(j):
    basic = [(e.get("et", 0), e.get("i", 0), e.get("f", 1)) for e in j.get("basicBeatmapEvents", [])]
    color = [(g.get("g", 0), box.get("f", {}), [e.get("c", 0) for e in box.get("e", [])])
             for g in j.get("lightColorEventBoxGroups", []) for box in g.get("e", [])]
    rotation = [(g.get("g", 0), box.get("f", {}), box.get("a", 0), [e.get("r", 0) for e in box.get("l", [])])
                for g in j.get("lightRotationEventBoxGroups", []) for box in g.get("e", [])]
    translation = [(g.get("g", 0), box.get("f", {}), box.get("a", 0), [e.get("t", 0) for e in box.get("l", [])])
                   for g in j.get("lightTranslationEventBoxGroups", []) for box in g.get("e", [])]
    fx = [g.get("g", 0) for g in j.get("vfxEventBoxGroups", [])]
    return basic, color, rotation, translation, fx


def parse_v4(j, ls):
    src = ls or j
    data = src.get("basicEventsData", [])
    basic = [(data[e["i"]].get("t", 0), data[e["i"]].get("i", 0), data[e["i"]].get("f", 1))
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
                color.append((gid, filt, [cev[i].get("c", 0) for i in evs if i < len(cev)]))
            elif typ == 2 and bi < len(rboxes):
                rotation.append((gid, filt, rboxes[bi].get("a", 0), [rev[i].get("r", 0) for i in evs if i < len(rev)]))
            elif typ == 3 and bi < len(tboxes):
                translation.append((gid, filt, tboxes[bi].get("a", 0), [tev[i].get("t", 0) for i in evs if i < len(tev)]))
            elif typ == 4:
                fx.append(gid)
    return basic, color, rotation, translation, fx


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


def summarize(env, st):
    out = {"maps": st.maps}
    if st.maps == 0:
        return out
    out["basic"] = {str(t): round(n / st.maps, 2) for t, n in sorted(st.basic.items()) if n / st.maps >= 0.15}
    vals = {}
    for t, c in sorted(st.basic_values.items()):
        if str(t) in out["basic"]:
            vals[str(t)] = [min(c), max(c), max(c, key=c.get)]
    if vals:
        out["basicValues"] = vals
    groups = {}
    for g, s in sorted(st.groups.items()):
        share = {k: round(s[k] / st.maps, 2) for k in ("color", "rotation", "translation", "fx")}
        if max(share.values()) < 0.2:
            continue
        row = {k: v for k, v in share.items() if v >= 0.2}
        row["lights"] = s["lights"]
        if s["rot_axes"] and share["rotation"] >= 0.2:
            row["rotationAxes"] = [a for a, n in s["rot_axes"].most_common() if n >= 0.15 * sum(s["rot_axes"].values())]
            row["rotationRange"] = round(pct(s["rot_max"], 0.9), 1)
        if s["tr_axes"] and share["translation"] >= 0.2:
            row["translationAxes"] = [a for a, n in s["tr_axes"].most_common() if n >= 0.15 * sum(s["tr_axes"].values())]
            row["translationRange"] = round(pct(s["tr_max"], 0.9), 2)
        if s["colors"]:
            tot = sum(s["colors"].values())
            row["white"] = round(s["colors"][2] / tot, 2)
        groups[str(g)] = row
    if groups:
        out["groups"] = groups
    return out


def main():
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
            best = None
            for j, ls in env_diffs(files, env):
                try:
                    p = parse(j, ls)
                except Exception:
                    continue
                n = sum(len(x) for x in p)
                if best is None or n > best[0]:
                    best = (n, p)
            if best and best[0] >= 50:
                st.add(*best[1])
                used += 1
        prior[env] = summarize(env, st)
        g = prior[env].get("groups", {})
        print(f"{env}: {st.maps} maps, {len(g)} groups, basic {list(prior[env].get('basic', {}))}")
    with open(OUT, "w") as f:
        json.dump(prior, f, indent=1)
    print(f"wrote {OUT}")


if __name__ == "__main__":
    main()
