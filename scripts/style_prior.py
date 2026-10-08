#!/usr/bin/env python3
"""Learn ABeat's style prior from human maps (`abeat fetch-maps` output: map folders and/or zips).

Writes src/Abeat.Core/Generation/style-prior.json: per difficulty, the distribution of cut
directions and of grid cells per hand, the figure vocabulary (hand + cell + cut direction that
mappers actually use), the move vocabulary (one hand's figure -> its next figure) and the vocabulary
of double shapes, plus density / doubles / dots rates
for reference. Hands are pooled with their mirror image so both vocabularies stay symmetric.

    python3 scripts/style_prior.py work/beatsaver-prior
"""
import collections
import glob
import json
import os
import sys
import zipfile

ORDER = ["Easy", "Normal", "Hard", "Expert", "ExpertPlus"]
# CutDirection mirrored left <-> right: Up Down Left Right UpLeft UpRight DownLeft DownRight Any
MIRROR_DIR = [0, 1, 3, 2, 5, 4, 7, 6, 8]
# a figure is in the vocabulary when this share of the difficulty's maps use it at least MIN_USES times
FIGURE_MAPS = 0.1
DOUBLE_MAPS = 0.05
MOVE_MAPS = 0.05
STACK_MAPS = 0.1
# grid step along each cut direction (y up); dots have none
DIR_STEP = [(0, 1), (0, -1), (-1, 0), (1, 0), (-1, 1), (1, 1), (-1, -1), (1, -1)]
MIN_USES = 2


def open_map(path):
    """(read(name) -> bytes, names) for a map folder or zip."""
    if path.endswith(".zip"):
        z = zipfile.ZipFile(path)
        names = {n.lower(): n for n in z.namelist() if "/" not in n}
        return (lambda n: z.read(names[n.lower()])), names
    names = {n.lower(): n for n in os.listdir(path)}
    return (lambda n: open(os.path.join(path, names[n.lower()]), "rb").read()), names


def difficulties(path):
    read, names = open_map(path)
    if "info.dat" not in names:
        return
    info = json.loads(read("info.dat").decode("utf-8-sig"))
    bpm = info.get("_beatsPerMinute")
    for s in info.get("_difficultyBeatmapSets", []):
        if s["_beatmapCharacteristicName"] != "Standard":
            continue
        for d in s["_difficultyBeatmaps"]:
            if d["_beatmapFilename"].lower() not in names:
                continue
            j = json.loads(read(d["_beatmapFilename"]).decode("utf-8-sig"))
            if "colorNotes" in j:
                notes = [(n.get("b", 0), n.get("c", 0), n.get("x", 0), n.get("y", 0), n.get("d", 0)) for n in j["colorNotes"]]
            elif "_notes" in j:
                notes = [(n["_time"], n["_type"], n["_lineIndex"], n["_lineLayer"], n["_cutDirection"])
                         for n in j["_notes"] if n["_type"] in (0, 1)]
            else:
                continue
            notes = [n for n in notes if n[1] in (0, 1) and 0 <= n[2] <= 3 and 0 <= n[3] <= 2 and 0 <= n[4] <= 8]
            if len(notes) >= 50:
                yield d["_difficulty"], bpm, sorted(notes)


def figure(x, y, d):
    return y * 36 + x * 9 + d


def mirror(c, x, y, d):
    return 1 - c, 3 - x, y, MIRROR_DIR[d]


def stack(group):
    """(head figure, length) when one hand's simultaneous notes form a line along their shared cut
    direction (the swing meets the head first), else None."""
    dirs = {n[4] for n in group}
    if len(dirs) != 1 or 8 in dirs:
        return None
    d = dirs.pop()
    dx, dy = DIR_STEP[d]
    cells = sorted(((n[2], n[3]) for n in group), key=lambda c: -(c[0] * dx + c[1] * dy))
    hx, hy = cells[-1]  # lowest projection on the swing: met first
    line = [(hx + k * dx, hy + k * dy) for k in range(len(group))]
    return (figure(hx, hy, d), len(group)) if sorted(line) == sorted(cells) else None


def main(root):
    acc = {k: {"dir": collections.Counter(), "cell": [collections.Counter(), collections.Counter()],
               "fig": [collections.Counter(), collections.Counter()], "figMaps": [collections.Counter(), collections.Counter()],
               "dbl": collections.Counter(), "dblMaps": collections.Counter(),
               "mov": [collections.Counter(), collections.Counter()], "movMaps": [collections.Counter(), collections.Counter()],
               "stk": [collections.Counter(), collections.Counter()], "stkMaps": [collections.Counter(), collections.Counter()], "stacked": [],
               "nps": [], "doubles": [], "dots": [], "maps": 0} for k in ORDER}
    paths = sorted(set(glob.glob(os.path.join(root, "*/"))) | set(glob.glob(os.path.join(root, "*.zip"))))
    seen = set()
    for path in paths:
        key = os.path.splitext(os.path.basename(os.path.normpath(path)))[0]
        if key in seen:  # folder extracted next to its zip
            continue
        seen.add(key)
        for name, bpm, notes in difficulties(path):
            if name not in acc:
                continue
            a = acc[name]
            a["maps"] += 1
            spb = 60 / bpm
            a["nps"].append(len(notes) / max(1e-6, (notes[-1][0] - notes[0][0]) * spb))
            beats = collections.defaultdict(list)
            for n in notes:
                beats[round(n[0], 3)].append(n)
            a["doubles"].append(sum(1 for v in beats.values() if len({n[1] for n in v}) == 2) / len(beats))
            a["dots"].append(sum(1 for n in notes if n[4] == 8) / len(notes))
            figs = [collections.Counter(), collections.Counter()]
            for _, c, x, y, d in notes:
                a["dir"][d] += 1
                a["cell"][c][y * 4 + x] += 1
                for mc, mx, my, md in ((c, x, y, d), mirror(c, x, y, d)):
                    figs[mc][figure(mx, my, md)] += 1
            for h in (0, 1):
                a["fig"][h].update(figs[h])
                a["figMaps"][h].update(f for f, n in figs[h].items() if n >= 2 * MIN_USES)  # mirrored: counted twice
            moves = [collections.Counter(), collections.Counter()]
            for c in (0, 1):
                prev = None
                for t in sorted({round(n[0], 3) for n in notes if n[1] == c}):
                    cur = next(n for n in notes if n[1] == c and round(n[0], 3) == t)  # a stack's first note
                    if prev is not None:
                        _, px, py, pd = prev[1:]
                        x, y, d = cur[2:]
                        moves[c][(figure(px, py, pd), figure(x, y, d))] += 1
                        mc, mpx, mpy, mpd = mirror(c, px, py, pd)
                        _, mx, my, md = mirror(c, x, y, d)
                        moves[mc][(figure(mpx, mpy, mpd), figure(mx, my, md))] += 1
                    prev = cur
            stacks = [collections.Counter(), collections.Counter()]
            stacked = 0
            for c in (0, 1):
                groups = collections.defaultdict(list)
                for n in notes:
                    if n[1] == c:
                        groups[round(n[0], 3)].append(n)
                for g in groups.values():
                    if len(g) < 2 or (st := stack(g)) is None:
                        continue
                    stacked += len(g)
                    stacks[c][st] += 1
                    mg = [(n[0], *mirror(*n[1:])) for n in g]
                    stacks[1 - c][stack(mg)] += 1
            a["stacked"].append(stacked / len(notes))
            for h in (0, 1):
                a["stk"][h].update(stacks[h])
                a["stkMaps"][h].update(k for k, n in stacks[h].items() if n >= MIN_USES)
            for h in (0, 1):
                a["mov"][h].update(moves[h])
                a["movMaps"][h].update(m for m, n in moves[h].items() if n >= MIN_USES)
            dbls = collections.Counter()
            for g in beats.values():
                left = [n for n in g if n[1] == 0]
                right = [n for n in g if n[1] == 1]
                if len(left) == 1 and len(right) == 1:
                    l, r = left[0], right[0]
                    dbls[(figure(*l[2:]), figure(*r[2:]))] += 1
                    ml, mr = mirror(*r[1:]), mirror(*l[1:])  # mirror image: the right note becomes the left one
                    dbls[(figure(*ml[1:]), figure(*mr[1:]))] += 1
            a["dbl"].update(dbls)
            a["dblMaps"].update(s for s, n in dbls.items() if n >= MIN_USES)

    def dist(counter, n):
        total = sum(counter.values()) or 1
        return [round(counter[i] / total, 5) for i in range(n)]

    mean = lambda xs: round(sum(xs) / len(xs), 4) if xs else 0
    out = {
        "source": f"{len(seen)} maps from {root}",
        "figureMaps": FIGURE_MAPS,
        "doubleMaps": DOUBLE_MAPS,
        "moveMaps": MOVE_MAPS,
        "stackMaps": STACK_MAPS,
        "difficulties": {},
    }
    for k, a in acc.items():
        if a["maps"] == 0:
            continue
        figures = [[f for f in range(108) if a["figMaps"][h][f] >= FIGURE_MAPS * a["maps"]] for h in (0, 1)]
        doubles = sorted(s for s, n in a["dblMaps"].items() if n >= DOUBLE_MAPS * a["maps"])
        stacks = [sorted(k for k, n in a["stkMaps"][h].items() if n >= STACK_MAPS * a["maps"]) for h in (0, 1)]
        moves = [sorted(m for m, n in a["movMaps"][h].items() if n >= MOVE_MAPS * a["maps"]) for h in (0, 1)]
        out["difficulties"][k] = {
            "maps": a["maps"],
            "nps": mean(a["nps"]),
            "doubles": mean(a["doubles"]),
            "dots": mean(a["dots"]),
            "directions": dist(a["dir"], 9),
            "cells": [dist(a["cell"][0], 12), dist(a["cell"][1], 12)],
            "figures": figures,
            "figureUse": [[a["figMaps"][h][f] for f in figures[h]] for h in (0, 1)],  # curated maps using each
            "doubleShapes": [list(s) for s in doubles],
            "doubleUse": [a["dblMaps"][s] for s in doubles],
            "stacked": mean(a["stacked"]),
            "stacks": [[f * 4 + n for f, n in stacks[h]] for h in (0, 1)],  # head figure * 4 + notes in the stack
            "stackUse": [[a["stkMaps"][h][k] for k in stacks[h]] for h in (0, 1)],
            "moves": [[f * 108 + t for f, t in moves[h]] for h in (0, 1)],  # from figure * 108 + to figure
            "moveUse": [[a["movMaps"][h][m] for m in moves[h]] for h in (0, 1)],
        }
        mcover = sum(a["mov"][h][m] for h in (0, 1) for m in moves[h]) / max(1, sum(sum(a["mov"][h].values()) for h in (0, 1)))
        cover = sum(a["fig"][h][f] for h in (0, 1) for f in figures[h]) / sum(sum(a["fig"][h].values()) for h in (0, 1))
        dcover = sum(a["dbl"][s] for s in doubles) / max(1, sum(a["dbl"].values()))
        top = max((f // 36 for f in figures[1]), default=0)
        print(f"{k:10} maps {a['maps']:3}  nps {mean(a['nps']):.2f}  doubles {mean(a['doubles']):.0%}  dots {mean(a['dots']):.1%}  "
              f"figures {len(figures[1]):3} ({cover:.1%} of notes, top row {sum(1 for f in figures[1] if f // 36 == 2)})  "
              f"double shapes {len(doubles):3} ({dcover:.1%})  moves {len(moves[1]):3} ({mcover:.1%})  "
              f"stacked {mean(a['stacked']):.1%} in {len(stacks[1])} shapes")
    path = os.path.join(os.path.dirname(__file__), "..", "src", "Abeat.Core", "Generation", "style-prior.json")
    json.dump(out, open(path, "w"), indent=1)
    print("wrote", os.path.normpath(path))


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "work/beatsaver-prior")
