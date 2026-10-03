#!/usr/bin/env python3
"""Learn ABeat's style prior from human maps (e.g. `abeat fetch-maps` output in work/beatsaver).

Writes src/Abeat.Core/Generation/style-prior.json: per difficulty, the distribution of cut
directions and of grid cells per hand, plus density / doubles / dots rates for reference.

    python3 scripts/style_prior.py work/beatsaver
"""
import collections
import glob
import json
import os
import sys

ORDER = ["Easy", "Normal", "Hard", "Expert", "ExpertPlus"]


def difficulties(map_dir):
    info_path = (glob.glob(os.path.join(map_dir, "[Ii]nfo.dat")) or [None])[0]
    if not info_path:
        return
    info = json.load(open(info_path, encoding="utf-8-sig"))
    bpm = info.get("_beatsPerMinute")
    for s in info.get("_difficultyBeatmapSets", []):
        if s["_beatmapCharacteristicName"] != "Standard":
            continue
        for d in s["_difficultyBeatmaps"]:
            f = os.path.join(map_dir, d["_beatmapFilename"])
            if not os.path.exists(f):
                continue
            j = json.load(open(f, encoding="utf-8-sig"))
            if "colorNotes" in j:
                notes = [(n.get("b", 0), n.get("c", 0), n.get("x", 0), n.get("y", 0), n.get("d", 0)) for n in j["colorNotes"]]
            elif "_notes" in j:
                notes = [(n["_time"], n["_type"], n["_lineIndex"], n["_lineLayer"], n["_cutDirection"])
                         for n in j["_notes"] if n["_type"] in (0, 1)]
            else:
                continue
            if len(notes) >= 50:
                yield d["_difficulty"], bpm, sorted(notes)


def main(root):
    acc = {k: {"dir": collections.Counter(), "cell": [collections.Counter(), collections.Counter()],
               "nps": [], "doubles": [], "dots": [], "maps": 0} for k in ORDER}
    for map_dir in sorted(glob.glob(os.path.join(root, "*/"))):
        for name, bpm, notes in difficulties(map_dir):
            if name not in acc:
                continue
            a = acc[name]
            a["maps"] += 1
            spb = 60 / bpm
            a["nps"].append(len(notes) / max(1e-6, (notes[-1][0] - notes[0][0]) * spb))
            beats = collections.defaultdict(set)
            for n in notes:
                beats[round(n[0], 3)].add(n[1])
            a["doubles"].append(sum(1 for v in beats.values() if len(v) == 2) / len(beats))
            a["dots"].append(sum(1 for n in notes if n[4] == 8) / len(notes))
            for _, c, x, y, d in notes:
                if 0 <= d <= 8:
                    a["dir"][d] += 1
                if c in (0, 1) and 0 <= x <= 3 and 0 <= y <= 2:
                    a["cell"][c][y * 4 + x] += 1

    def dist(counter, n):
        total = sum(counter.values()) or 1
        return [round(counter[i] / total, 5) for i in range(n)]

    mean = lambda xs: round(sum(xs) / len(xs), 4) if xs else 0
    out = {
        "source": f"{len(glob.glob(os.path.join(root, '*/')))} maps from {root}",
        "difficulties": {
            k: {
                "maps": a["maps"],
                "nps": mean(a["nps"]),
                "doubles": mean(a["doubles"]),
                "dots": mean(a["dots"]),
                "directions": dist(a["dir"], 9),
                "cells": [dist(a["cell"][0], 12), dist(a["cell"][1], 12)],
            }
            for k, a in acc.items() if a["maps"] > 0
        },
    }
    path = os.path.join(os.path.dirname(__file__), "..", "src", "Abeat.Core", "Generation", "style-prior.json")
    json.dump(out, open(path, "w"), indent=1)
    for k, v in out["difficulties"].items():
        print(f"{k:10} maps {v['maps']:2}  nps {v['nps']:.2f}  doubles {v['doubles']:.0%}  dots {v['dots']:.1%}")
    print("wrote", os.path.normpath(path))


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "work/beatsaver")
