#!/usr/bin/env python3
"""Learn ABeat's style prior from human maps (`abeat fetch-maps` output: map folders and/or zips).

Writes src/Abeat.Core/Generation/style-prior.json: per difficulty, the distribution of cut
directions and of grid cells per hand, the figure vocabulary (hand + cell + cut direction that
mappers actually use) and the vocabulary of double shapes, plus density / doubles / dots rates
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


def main(root):
    acc = {k: {"dir": collections.Counter(), "cell": [collections.Counter(), collections.Counter()],
               "fig": [collections.Counter(), collections.Counter()], "figMaps": [collections.Counter(), collections.Counter()],
               "dbl": collections.Counter(), "dblMaps": collections.Counter(),
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
        "difficulties": {},
    }
    for k, a in acc.items():
        if a["maps"] == 0:
            continue
        figures = [[f for f in range(108) if a["figMaps"][h][f] >= FIGURE_MAPS * a["maps"]] for h in (0, 1)]
        doubles = sorted(s for s, n in a["dblMaps"].items() if n >= DOUBLE_MAPS * a["maps"])
        out["difficulties"][k] = {
            "maps": a["maps"],
            "nps": mean(a["nps"]),
            "doubles": mean(a["doubles"]),
            "dots": mean(a["dots"]),
            "directions": dist(a["dir"], 9),
            "cells": [dist(a["cell"][0], 12), dist(a["cell"][1], 12)],
            "figures": figures,
            "doubleShapes": [list(s) for s in doubles],
        }
        cover = sum(a["fig"][h][f] for h in (0, 1) for f in figures[h]) / sum(sum(a["fig"][h].values()) for h in (0, 1))
        dcover = sum(a["dbl"][s] for s in doubles) / max(1, sum(a["dbl"].values()))
        top = max((f // 36 for f in figures[1]), default=0)
        print(f"{k:10} maps {a['maps']:3}  nps {mean(a['nps']):.2f}  doubles {mean(a['doubles']):.0%}  dots {mean(a['dots']):.1%}  "
              f"figures {len(figures[1]):3} ({cover:.1%} of notes, top row {sum(1 for f in figures[1] if f // 36 == 2)})  "
              f"double shapes {len(doubles):3} ({dcover:.1%})")
    path = os.path.join(os.path.dirname(__file__), "..", "src", "Abeat.Core", "Generation", "style-prior.json")
    json.dump(out, open(path, "w"), indent=1)
    print("wrote", os.path.normpath(path))


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "work/beatsaver-prior")
