#!/usr/bin/env python3
"""Learn ABeat's style prior from human maps (`abeat fetch-maps` output: map folders and/or zips).

Writes src/Abeat.Core/Generation/style-prior.json: per difficulty, the distribution of cut
directions and of grid cells per hand, the figure vocabulary (hand + cell + cut direction that
mappers actually use), the move vocabulary (one hand's figure -> its next figure), the streak
vocabulary (runs of 3-16 consecutive figures of one hand, and 4 note moments of both hands) and the vocabulary of double shapes, plus density / doubles / dots rates
for reference. Hands are pooled with their mirror image so both vocabularies stay symmetric.

    python3 scripts/style_prior.py work/beatsaver-curated
    # plus the official maps imported into the app, by folder (regex on "Parent/Folder"):
    python3 scripts/style_prior.py work/beatsaver-curated --app-data src/Abeat.Web/data \
        --folders "Original Soundtrack Vol\\. [4-8]|Coldplay|Imagine Dragons|Billie Eilish|Lady Gaga|Electronic Mixtape|Shock Drops|The Weeknd"
"""
import collections
import glob
import json
import os
import re
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
# streaks: runs of one hand's consecutive figures; exact runs past 6-8 notes rarely recur between maps, so
# a streak counts when it is used MIN_USES times in at least STREAK_MIN_MAPS maps
STREAK_LENGTHS = [3, 4, 6, 8, 12, 16]
STREAK_MIN_MAPS = 2
# a pause longer than this (seconds) between two notes of a hand ends its streak (MovementAnalyzer.MaxGapSec)
STREAK_GAP_SEC = 1.5
# two-hand (pair) streaks: 4 consecutive note moments of both hands, each a token: a left note (figure), a
# right note (108 + figure) or a double (216 + left figure * 108 + right figure); a window counts only when
# both hands play in it (one hand alone is a per-hand streak). Longer joint runs almost never recur.
PAIR_LENGTH = 4


def pair_token(moment):
    """Token of one note moment (notes sorted, a stack's lowest note per hand) and of its mirror image."""
    first = {}
    for n in moment:
        first.setdefault(n[1], n)
    if len(first) == 2:
        l, r = first[0], first[1]
        ml, mr = mirror(*r[1:]), mirror(*l[1:])  # mirror image: the right note becomes the left one
        return 216 + figure(*l[2:]) * 108 + figure(*r[2:]), 216 + figure(*ml[1:]) * 108 + figure(*mr[1:])
    (c, n), = first.items()
    mc, mx, my, md = mirror(*n[1:])
    return c * 108 + figure(*n[2:]), mc * 108 + figure(mx, my, md)


def both_hands(window):
    return any(t >= 216 for t in window) or len({t // 108 for t in window}) == 2


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
    if str(info.get("version", "")).startswith("4"):
        yield from difficulties_v4(read, names, info)
        return
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


def difficulties_v4(read, names, info):
    """Official (v4) maps: notes are indices into colorNotesData, fields left out are 0."""
    bpm = info["audio"]["bpm"]
    for d in info.get("difficultyBeatmaps", []):
        if d.get("characteristic") != "Standard" or d["beatmapDataFilename"].lower() not in names:
            continue
        j = json.loads(read(d["beatmapDataFilename"]).decode("utf-8-sig"))
        data = j.get("colorNotesData", [])
        notes = []
        for n in j.get("colorNotes", []):
            nd = data[n.get("i", 0)] if n.get("i", 0) < len(data) else {}
            notes.append((n.get("b", 0), nd.get("c", 0), nd.get("x", 0), nd.get("y", 0), nd.get("d", 0)))
        notes = [n for n in notes if n[1] in (0, 1) and 0 <= n[2] <= 3 and 0 <= n[3] <= 2 and 0 <= n[4] <= 8]
        if len(notes) >= 50:
            yield d["difficulty"], bpm, sorted(notes)


def app_maps(data_dir, pattern):
    """Reference maps of the app's songs whose folder path ("Parent/Folder") matches the regex."""
    folders = {f["id"]: f for f in json.load(open(os.path.join(data_dir, "folders.json")))}

    def path(i):
        parts = []
        while i:
            parts.append(folders[i]["name"])
            i = folders[i]["parentId"]
        return "/".join(reversed(parts))

    out = []
    for meta in sorted(glob.glob(os.path.join(data_dir, "songs", "*", "meta.json"))):
        fid = json.load(open(meta)).get("folderId")
        ref = os.path.join(os.path.dirname(meta), "reference")
        if fid in folders and re.search(pattern, path(fid)) and os.path.isdir(ref):
            out.append(ref)
    return out


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


def main(root, extra=(), streaks_only=False):
    """extra: more map folders (official maps, whose difficulties are strict where BeatSaver's labels are
    not); with streaks_only they join just the streak vocabulary."""
    streak_only = set(extra) if streaks_only else set()
    acc = {k: {"dir": collections.Counter(), "cell": [collections.Counter(), collections.Counter()],
               "fig": [collections.Counter(), collections.Counter()], "figMaps": [collections.Counter(), collections.Counter()],
               "dbl": collections.Counter(), "dblMaps": collections.Counter(),
               "mov": [collections.Counter(), collections.Counter()], "movMaps": [collections.Counter(), collections.Counter()],
               "str": {L: [collections.Counter(), collections.Counter()] for L in STREAK_LENGTHS},
               "strMaps": {L: [collections.Counter(), collections.Counter()] for L in STREAK_LENGTHS},
               "str4ByMap": [], "streakOnly": 0,
               "pair": collections.Counter(), "pairMaps": collections.Counter(), "pairByMap": [],
               "stk": [collections.Counter(), collections.Counter()], "stkMaps": [collections.Counter(), collections.Counter()], "stacked": [],
               "nps": [], "doubles": [], "dots": [], "maps": 0} for k in ORDER}
    paths = sorted(set(glob.glob(os.path.join(root, "*/"))) | set(glob.glob(os.path.join(root, "*.zip")))) + list(extra)
    seen = set()
    for path in paths:
        # app references are all called "reference": key them by song id
        base = os.path.basename(os.path.normpath(path))
        key = os.path.basename(os.path.dirname(os.path.normpath(path))) if base == "reference" else os.path.splitext(base)[0]
        if key in seen:  # folder extracted next to its zip
            continue
        seen.add(key)
        for name, bpm, notes in difficulties(path):
            if name not in acc:
                continue
            a = acc[name]
            spb = 60 / bpm
            streaks = {L: [collections.Counter(), collections.Counter()] for L in STREAK_LENGTHS}
            for c in (0, 1):
                run, mrun, last = [], [], None
                for t in sorted({round(n[0], 3) for n in notes if n[1] == c}) + [None]:
                    if t is None or (last is not None and (t - last) * spb > STREAK_GAP_SEC):
                        mc = 1 - c
                        for L in STREAK_LENGTHS:
                            for i in range(len(run) - L + 1):
                                streaks[L][c][tuple(run[i:i + L])] += 1
                                streaks[L][mc][tuple(mrun[i:i + L])] += 1
                        run, mrun = [], []
                    if t is None:
                        break
                    cur = next(n for n in notes if n[1] == c and round(n[0], 3) == t)
                    run.append(figure(*cur[2:]))
                    _, mx, my, md = mirror(c, *cur[2:])
                    mrun.append(figure(mx, my, md))
                    last = t
            a["str4ByMap"].append(streaks[4])
            for L in STREAK_LENGTHS:
                for h in (0, 1):
                    a["str"][L][h].update(streaks[L][h])
                    a["strMaps"][L][h].update(k for k, n in streaks[L][h].items() if n >= MIN_USES)
            # two-hand streaks: every 4 note moments in a row (a pause ends the run), with their mirror image;
            # a window that is its own mirror image counts once
            pairs = collections.Counter()
            moments = collections.defaultdict(list)
            for n in notes:
                moments[round(n[0], 3)].append(n)
            run, last = [], None
            for t in sorted(moments) + [None]:
                if t is None or (last is not None and (t - last) * spb > STREAK_GAP_SEC):
                    for i in range(len(run) - PAIR_LENGTH + 1):
                        w = tuple(x[0] for x in run[i:i + PAIR_LENGTH])
                        if both_hands(w):
                            mw = tuple(x[1] for x in run[i:i + PAIR_LENGTH])
                            pairs[w] += 1
                            if mw != w:
                                pairs[mw] += 1
                    run = []
                if t is None:
                    break
                run.append(pair_token(moments[t]))
                last = t
            a["pairByMap"].append(pairs)
            a["pair"].update(pairs)
            a["pairMaps"].update(k for k, n in pairs.items() if n >= MIN_USES)
            if path in streak_only:
                a["streakOnly"] += 1
                continue
            a["maps"] += 1
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
        "source": f"{len(seen) - len(extra)} maps from {root}" + (f" + {len(extra)} official maps" + (" (streaks only)" if streaks_only else "") if extra else ""),
        "figureMaps": FIGURE_MAPS,
        "doubleMaps": DOUBLE_MAPS,
        "moveMaps": MOVE_MAPS,
        "stackMaps": STACK_MAPS,
        "streakMinMaps": STREAK_MIN_MAPS,
        "streakGapSec": STREAK_GAP_SEC,
        "difficulties": {},
    }
    for k, a in acc.items():
        if a["maps"] == 0:
            continue
        streak_maps = a["maps"] + a["streakOnly"]
        figures = [[f for f in range(108) if a["figMaps"][h][f] >= FIGURE_MAPS * a["maps"]] for h in (0, 1)]
        doubles = sorted(s for s, n in a["dblMaps"].items() if n >= DOUBLE_MAPS * a["maps"])
        stacks = [sorted(k for k, n in a["stkMaps"][h].items() if n >= STACK_MAPS * a["maps"]) for h in (0, 1)]
        moves = [sorted(m for m, n in a["movMaps"][h].items() if n >= MOVE_MAPS * a["maps"]) for h in (0, 1)]
        streaks = {L: [sorted(k for k, n in a["strMaps"][L][h].items() if n >= STREAK_MIN_MAPS) for h in (0, 1)] for L in STREAK_LENGTHS}
        # how much of a map is made of 4-note streaks other maps use (leave-one-out, so the map's own runs
        # don't count): the share the planner aims for
        shares = []
        for own in a["str4ByMap"]:
            occ = sum(sum(own[h].values()) for h in (0, 1))
            if occ:
                known = sum(n for h in (0, 1) for k, n in own[h].items()
                            if a["strMaps"][4][h][k] - (1 if n >= MIN_USES else 0) >= STREAK_MIN_MAPS)
                shares.append(known / occ)
        pair_streaks = sorted(k for k, n in a["pairMaps"].items() if n >= STREAK_MIN_MAPS)
        pair_shares = []
        for own in a["pairByMap"]:
            occ = sum(own.values())
            if occ:
                known = sum(n for k, n in own.items() if a["pairMaps"][k] - (1 if n >= MIN_USES else 0) >= STREAK_MIN_MAPS)
                pair_shares.append(known / occ)
        out["difficulties"][k] = {
            "maps": a["maps"],
            "streakMaps": a["maps"] + a["streakOnly"],
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
            # per length, per hand: figures of the run joined by "." (first note first), and maps using each
            "streaks": {str(L): [[".".join(map(str, k)) for k in streaks[L][h]] for h in (0, 1)] for L in STREAK_LENGTHS},
            "streakShare": mean(shares),
            "streakUse": {str(L): [[a["strMaps"][L][h][k] for k in streaks[L][h]] for h in (0, 1)] for L in STREAK_LENGTHS},
            # two-hand streaks: 4 note moments of both hands as tokens joined by "." (see pair_token), maps using
            # each, and the leave-one-out share of two-hand 4-windows that are known ones
            "pairStreaks": [".".join(map(str, k)) for k in pair_streaks],
            "pairStreakUse": [a["pairMaps"][k] for k in pair_streaks],
            "pairStreakShare": mean(pair_shares),
        }
        mcover = sum(a["mov"][h][m] for h in (0, 1) for m in moves[h]) / max(1, sum(sum(a["mov"][h].values()) for h in (0, 1)))
        cover = sum(a["fig"][h][f] for h in (0, 1) for f in figures[h]) / sum(sum(a["fig"][h].values()) for h in (0, 1))
        dcover = sum(a["dbl"][s] for s in doubles) / max(1, sum(a["dbl"].values()))
        scover = {L: sum(a["str"][L][h][k] for h in (0, 1) for k in streaks[L][h]) / max(1, sum(sum(a["str"][L][h].values()) for h in (0, 1)))
                  for L in STREAK_LENGTHS}
        top = max((f // 36 for f in figures[1]), default=0)
        print(f"{k:10} maps {a['maps']:3}  nps {mean(a['nps']):.2f}  doubles {mean(a['doubles']):.0%}  dots {mean(a['dots']):.1%}  "
              f"figures {len(figures[1]):3} ({cover:.1%} of notes, top row {sum(1 for f in figures[1] if f // 36 == 2)})  "
              f"double shapes {len(doubles):3} ({dcover:.1%})  moves {len(moves[1]):3} ({mcover:.1%})  "
              f"stacked {mean(a['stacked']):.1%} in {len(stacks[1])} shapes")
        pcover = sum(a["pair"][k] for k in pair_streaks) / max(1, sum(a["pair"].values()))
        print(f"           pair streaks {len(pair_streaks)} ({pcover:.1%})  pair share {mean(pair_shares):.1%}")
        print(f"           streak share {mean(shares):.1%}  streaks " + "  ".join(f"{L}: {len(streaks[L][1])} ({scover[L]:.1%})" for L in STREAK_LENGTHS))
    path = os.path.join(os.path.dirname(__file__), "..", "src", "Abeat.Core", "Generation", "style-prior.json")
    # indented, with each innermost array on one line (as diff-friendly as it is small)
    text = json.dumps(out, indent=1)
    text = re.sub(r"\[\s*([^\[\]{}]*?)\s*\]", lambda m: "[" + re.sub(r",\s+", ", ", m.group(1)) + "]", text)
    open(path, "w").write(text)
    print("wrote", os.path.normpath(path))


if __name__ == "__main__":
    import argparse
    ap = argparse.ArgumentParser()
    ap.add_argument("root", nargs="?", default="work/beatsaver-curated")
    ap.add_argument("--app-data", help="the web app's data folder, for --folders")
    ap.add_argument("--folders", help="regex on app folder paths whose songs' reference maps join the corpus")
    ap.add_argument("--streaks-only", action="store_true", help="the --folders maps join only the streak vocabulary")
    args = ap.parse_args()
    main(args.root, app_maps(args.app_data, args.folders) if args.app_data and args.folders else (), args.streaks_only)
