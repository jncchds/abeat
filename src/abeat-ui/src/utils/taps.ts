import type { Difficulty, TapRun } from '../api'
import { BLUE, RED, type TapRow } from './draw'
import type { Tempo } from './tempo'

/** Run 0 is the right hand, run 1 the left: names, saber colours and note colour (`c`) per run. */
export const HANDS = ['Right hand', 'Left hand']
export const TAP_COLORS = [BLUE, RED]
const RUN_NOTE_COLOR = [1, 0]
/** Matching window for a tap and a note, after removing the tapper's median offset. */
export const TAP_TOL = 0.07
/** Taps of both hands this close together are one double. */
const DOUBLE_TOL = 0.05
/** An appended tap this close to one already in the run is the same tap. */
const SAME_TAP_TOL = 0.04

/** The run's taps plus the new ones, sorted; new taps within SAME_TAP_TOL of an existing one are dropped. */
export function mergeTaps(existing: number[], added: number[]): number[] {
  const old = [...existing].sort((a, b) => a - b)
  const fresh = added.filter(t => {
    let lo = 0, hi = old.length
    while (lo < hi) { const m = (lo + hi) >> 1; if (old[m] < t) lo = m + 1; else hi = m }
    return !(Math.abs((old[lo] ?? Infinity) - t) <= SAME_TAP_TOL || Math.abs((old[lo - 1] ?? Infinity) - t) <= SAME_TAP_TOL)
  })
  return [...old, ...fresh].sort((a, b) => a - b)
}

/** Note times (seconds) of a shown map: all, per note colour (0 left, 1 right), and the times both hands hit. */
export interface TapRef { name: string; color: string; all: number[]; hand: [number[], number[]]; doubles: number[] }

export function tapRef(name: string, color: string, d: Difficulty, tempo: Tempo): TapRef {
  const times = (c?: number) => [...new Set(d.notes.filter(n => c === undefined || n.c === c).map(n => n.b))].sort((a, b) => a - b)
  const left = new Set(times(0))
  const right = times(1)
  return {
    name, color,
    all: times().map(tempo.toSec),
    hand: [[...left].map(tempo.toSec), right.map(tempo.toSec)],
    doubles: right.filter(b => left.has(b)).map(tempo.toSec),
  }
}

/** Median of tap minus nearest reference time, over taps within 150 ms of one: the tapper's latency. */
export function medianOffset(ref: number[], taps: number[]): number {
  const diffs: number[] = []
  let j = 0
  for (const t of taps) {
    while (j + 1 < ref.length && ref[j + 1] <= t) j++
    const near = [ref[j], ref[j + 1]].filter(x => x !== undefined)
    if (!near.length) continue
    const d = near.reduce((best, x) => (Math.abs(t - x) < Math.abs(best) ? t - x : best), Infinity)
    if (Math.abs(d) < 0.15) diffs.push(d)
  }
  if (!diffs.length) return 0
  diffs.sort((a, b) => a - b)
  return diffs[Math.floor(diffs.length / 2)]
}

/** One-to-one matches in time order within `tol`; returns the indices of `test` without a partner. */
export function matchTimes(ref: number[], test: number[], tol = TAP_TOL) {
  const unmatched: number[] = []
  let j = 0, matched = 0
  for (let i = 0; i < test.length; i++) {
    while (j < ref.length && ref[j] < test[i] - tol) j++
    if (j < ref.length && ref[j] <= test[i] + tol) { matched++; j++ }
    else unmatched.push(i)
  }
  const precision = test.length ? matched / test.length : 0
  const recall = ref.length ? matched / ref.length : 0
  const f1 = precision + recall ? (2 * precision * recall) / (precision + recall) : 0
  return { matched, precision, recall, f1, unmatched }
}

/** Both hands as one stream: taps of the two hands within DOUBLE_TOL merge into one time, counted as a double. */
export function mergeHands(first: number[], second: number[]) {
  const all = [...first.map(t => ({ t, h: 0 })), ...second.map(t => ({ t, h: 1 }))].sort((a, b) => a.t - b.t)
  const times: number[] = [], doubles: number[] = []
  for (let i = 0; i < all.length; i++) {
    const next = all[i + 1]
    if (next && next.h !== all[i].h && next.t - all[i].t <= DOUBLE_TOL) {
      const t = (all[i].t + next.t) / 2
      times.push(t)
      doubles.push(t)
      i++
    } else times.push(all[i].t)
  }
  return { times, doubles }
}

/** One hand's run (by run index) against a map: offset against all notes, then matched to that hand's notes and to any note.
 * Only the time both cover counts, so a run stopped early is not charged for the rest of the song. */
export function compareHand(ref: TapRef, run: number, taps: number[]) {
  const offset = medianOffset(ref.all, taps)
  const shifted = taps.map(t => t - offset)
  const end = Math.min(ref.all.at(-1) ?? 0, shifted.at(-1) ?? 0) + TAP_TOL
  const any = matchTimes(ref.all.filter(t => t <= end), shifted)
  return { offset, shifted, end, ...matchTimes(ref.hand[RUN_NOTE_COLOR[run]].filter(t => t <= end), shifted), any }
}

/** Both runs merged (each shifted by its own offset) against all notes of the map. */
export function compareBoth(ref: TapRef, runs: TapRun[]) {
  const [a, b] = runs.map((run, k) => compareHand(ref, k, run.taps))
  const end = Math.min(a.end, b.end)
  const merged = mergeHands(a.shifted.filter(t => t <= end), b.shifted.filter(t => t <= end))
  return {
    ...matchTimes(ref.all.filter(t => t <= end), merged.times),
    taps: merged.times.length,
    doubles: merged.doubles.length,
    refDoubles: ref.doubles.filter(t => t <= end).length,
    doubleMatch: matchTimes(ref.doubles.filter(t => t <= end), merged.doubles),
  }
}

/** Timeline rows of the runs, shifted onto `ref`; taps without a note of either hand nearby are flagged. */
export function tapRows(runs: TapRun[], ref: TapRef | null): TapRow[] {
  return runs.map((run, k) => {
    const label = k === 0 ? 'tap R' : 'tap L'
    if (!ref?.all.length) return { label, color: TAP_COLORS[k], times: run.taps }
    const c = compareHand(ref, k, run.taps)
    return { label, color: TAP_COLORS[k], times: c.shifted, unmatched: new Set(c.any.unmatched) }
  })
}
