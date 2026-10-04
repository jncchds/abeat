import type { Analysis } from '../api'

/** Beat/second conversion for a song: one BPM, or the tempo changes of a drifting song. */
export interface Tempo {
  /** Tempo at beat 0. */
  bpm: number
  varies: boolean
  min: number
  max: number
  toSec(beat: number): number
  toBeat(sec: number): number
}

interface Point { beat: number; time: number; bpm: number }

const cache = new WeakMap<Analysis['tempo'], Tempo>()

export function tempoOf(t: Analysis['tempo']): Tempo {
  const hit = cache.get(t)
  if (hit) return hit
  const changes = t.changes && t.changes.length > 1 ? t.changes : [{ beat: 0, bpm: t.bpm }]
  const pts: Point[] = []
  for (const c of changes) {
    const last = pts[pts.length - 1]
    pts.push(last ? { beat: c.beat, time: last.time + ((c.beat - last.beat) * 60) / last.bpm, bpm: c.bpm } : { beat: 0, time: 0, bpm: c.bpm })
  }
  const at = (key: 'beat' | 'time', v: number) => {
    let i = pts.length - 1
    while (i > 0 && pts[i][key] > v) i--
    return pts[i]
  }
  const bpms = pts.map(p => p.bpm)
  const tempo: Tempo = {
    bpm: pts[0].bpm,
    varies: pts.length > 1,
    min: Math.min(...bpms),
    max: Math.max(...bpms),
    toSec: b => { const p = at('beat', b); return p.time + ((b - p.beat) * 60) / p.bpm },
    toBeat: s => { const p = at('time', s); return p.beat + ((s - p.time) * p.bpm) / 60 },
  }
  cache.set(t, tempo)
  return tempo
}
