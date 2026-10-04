import type { Analysis, Difficulty } from '../api'
import { cssVar } from './format'
import { tempoOf, type Tempo } from './tempo'

// default Beat Saber saber colours (left red, right blue)
export const RED = '#ff1f4b'
export const BLUE = '#00a3ff'
const DIR_VEC: [number, number][] = [[0, 1], [0, -1], [-1, 0], [1, 0], [-0.707, 0.707], [0.707, 0.707], [-0.707, -0.707], [0.707, -0.707], [0, 0]]
const SECTION_COLORS = ['#00a3ff', '#ff1f4b', '#b14dff', '#00e5c7', '#ff7a1a', '#ff3dbb', '#4d6bff', '#c6ff1a']

export const ISSUE_COLOR: Record<string, string> = {
  Reset: '#ff1f4b', VisionBlock: '#ffb020', Crossover: '#ff7a1a', HighCost: '#8a8fb5', WallClash: '#ff3dbb', BombHit: '#ff3dbb', HandClash: '#ff3dbb', Strain: '#b36bff',
}

export interface View { pxPerSec: number; start: number }

/** Identity colours of the two compared versions (distinct from the red/blue sabers). */
export const SIDE_A = '#ffc23d'
export const SIDE_B = '#b46bff'

/** One map drawn in the lanes. With two tracks every lane is split: track 0 on top, track 1 below. */
export interface Track {
  d: Difficulty
  color: string
  /** Beats of notes with no note of the other track within the timing tolerance. */
  unmatched?: Set<number>
}

export const LAYOUT = { layersTop: 64, layerH: 9, lyricsH: 14, laneH: 15, splitLaneH: 26, diffH: 18, issuesH: 14 }

const laneHeight = (split: boolean) => (split ? LAYOUT.splitLaneH : LAYOUT.laneH)

export function timelineHeight(layerCount: number, split = false, lyrics = false) {
  return LAYOUT.layersTop + layerCount * LAYOUT.layerH + (lyrics ? LAYOUT.lyricsH : 0) + 8 + 12 * laneHeight(split)
    + (split ? LAYOUT.diffH : 0) + LAYOUT.issuesH + 4
}

/** Beats of notes in `a` without a note in `b` within `tolBeats` (any lane). */
export function unmatchedBeats(a: Difficulty, b: Difficulty, tolBeats: number): Set<number> {
  const bb = [...new Set(b.notes.map(n => n.b))].sort((x, y) => x - y)
  const out = new Set<number>()
  let j = 0
  for (const t of [...new Set(a.notes.map(n => n.b))].sort((x, y) => x - y)) {
    while (j < bb.length && bb[j] < t - tolBeats) j++
    if (j >= bb.length || bb[j] > t + tolBeats) out.add(t)
  }
  return out
}

/** Saber-coloured note with a cut arrow (white) or dot; `angle` rotates the arrow counter-clockwise (degrees). */
export function drawNote(g: CanvasRenderingContext2D, cx: number, cy: number, size: number, color: number, dir: number, angle = 0) {
  g.fillStyle = color === 0 ? RED : BLUE
  const r = size / 2
  g.beginPath()
  g.roundRect(cx - r, cy - r, size, size, 2)
  g.fill()
  g.fillStyle = '#fff'
  g.strokeStyle = '#fff'
  if (dir === 8) {
    g.beginPath()
    g.arc(cx, cy, Math.max(1.2, r * 0.3), 0, 7)
    g.fill()
    return
  }
  const [dx, dy] = DIR_VEC[dir]
  const rad = (angle * Math.PI) / 180
  const vx = dx * Math.cos(rad) - dy * Math.sin(rad), vy = dx * Math.sin(rad) + dy * Math.cos(rad)
  g.lineWidth = Math.max(1.2, size / 7)
  g.beginPath()
  g.moveTo(cx - vx * r * 0.7, cy + vy * r * 0.7)
  g.lineTo(cx + vx * r * 0.7, cy - vy * r * 0.7)
  g.stroke()
  const px = -vy, py = vx
  g.beginPath()
  g.moveTo(cx + vx * r * 0.95, cy - vy * r * 0.95)
  g.lineTo(cx + vx * r * 0.2 + px * r * 0.5, cy - (vy * r * 0.2 + py * r * 0.5))
  g.lineTo(cx + vx * r * 0.2 - px * r * 0.5, cy - (vy * r * 0.2 - py * r * 0.5))
  g.fill()
}

/** Ring in the version's colour around a note the other version does not have. */
function drawRing(g: CanvasRenderingContext2D, cx: number, cy: number, size: number, color: string) {
  g.strokeStyle = color
  g.lineWidth = 2
  const r = size / 2 + 2.5
  g.beginPath()
  g.roundRect(cx - r, cy - r, r * 2, r * 2, 4)
  g.stroke()
}

export function drawTimeline(canvas: HTMLCanvasElement, a: Analysis, tracks: Track[], view: View, now: number) {
  const dpr = window.devicePixelRatio || 1
  const w = canvas.clientWidth, h = canvas.clientHeight
  if (canvas.width !== Math.round(w * dpr) || canvas.height !== Math.round(h * dpr)) {
    canvas.width = Math.round(w * dpr)
    canvas.height = Math.round(h * dpr)
  }
  const g = canvas.getContext('2d')!
  g.setTransform(dpr, 0, 0, dpr, 0, 0)
  g.clearRect(0, 0, w, h)

  const text = cssVar('--text', '#e2e8f0')
  const muted = cssVar('--text-muted', '#94a3b8')
  const grid = cssVar('--border', '#334155')
  const accent = cssVar('--accent', '#6366f1')

  const { pxPerSec, start: t0 } = view
  const t1 = t0 + w / pxPerSec
  const X = (t: number) => (t - t0) * pxPerSec
  const layers = Object.keys(a.layers)
  const split = tracks.length > 1
  const laneH = laneHeight(split)
  const sub = laneH / Math.max(1, tracks.length)
  const lyricsTop = LAYOUT.layersTop + layers.length * LAYOUT.layerH
  const lanesTop = lyricsTop + (a.lyrics?.words.length ? LAYOUT.lyricsH : 0) + 8
  const diffTop = lanesTop + 12 * laneH
  const lanesBottom = diffTop + (split ? LAYOUT.diffH : 0)
  /** Centre of a grid cell's (sub-)lane for track k. */
  const laneY = (x: number, y: number, k: number) => lanesTop + ((2 - y) * 4 + x) * laneH + k * sub + sub / 2
  const tempo = tempoOf(a.tempo)
  const beatToSec = tempo.toSec

  // sections, coloured per label (repeats share a colour), opacity by energy
  const labels = [...new Set(a.sections.map(s => s.label))]
  g.font = '11px Inter, system-ui, sans-serif'
  for (const s of a.sections) {
    if (s.end < t0 || s.start > t1) continue
    g.fillStyle = SECTION_COLORS[labels.indexOf(s.label) % SECTION_COLORS.length]
    g.globalAlpha = 0.18 + 0.5 * s.energy
    g.fillRect(X(s.start), 0, (s.end - s.start) * pxPerSec - 1, 20)
    g.globalAlpha = 1
    g.fillStyle = text
    g.fillText(`${s.label} · ${s.energy.toFixed(2)}`, Math.max(X(s.start), 0) + 4, 14)
  }

  // energy curve
  const e = a.energy
  g.beginPath()
  g.moveTo(X(t0), 62)
  for (let t = t0; t <= t1; t += Math.max(e.hopSec, 1 / pxPerSec)) {
    const v = e.values[Math.min(e.values.length - 1, Math.max(0, Math.round(t / e.hopSec)))] ?? 0
    g.lineTo(X(t), 62 - v * 38)
  }
  g.lineTo(X(t1), 62)
  g.fillStyle = accent
  g.globalAlpha = 0.25
  g.fill()
  g.globalAlpha = 1

  // beat grid: bars strong, beats faint when zoomed in enough
  const downs = new Set(a.tempo.downbeats.map(x => Math.round(tempo.toBeat(x))))
  const showBeats = (60 / tempo.max) * pxPerSec > 8
  for (let b = Math.max(0, Math.floor(tempo.toBeat(t0))); beatToSec(b) <= t1; b++) {
    const bar = downs.has(b)
    if (!bar && !showBeats) continue
    g.fillStyle = grid
    g.globalAlpha = bar ? 1 : 0.45
    g.fillRect(Math.round(X(beatToSec(b))), 22, 1, lanesBottom - 22 + LAYOUT.issuesH)
  }
  g.globalAlpha = 1

  // onset layers
  g.font = '10px Inter, system-ui, sans-serif'
  layers.forEach((name, i) => {
    const y = LAYOUT.layersTop + i * LAYOUT.layerH
    g.fillStyle = muted
    g.fillText(name, 2, y + 8)
    g.fillStyle = text
    for (const o of a.layers[name]) {
      if (o.t < t0 || o.t > t1) continue
      g.globalAlpha = 0.12 + 0.8 * o.s
      g.fillRect(X(o.t), y + 1, 2, LAYOUT.layerH - 2)
    }
    g.globalAlpha = 1
  })

  // transcribed lyrics, one word per tick, skipping words that would overlap the previous one
  if (a.lyrics?.words.length) {
    g.fillStyle = muted
    g.fillText('lyrics', 2, lyricsTop + 10)
    g.font = '10px Inter, system-ui, sans-serif'
    let lastEnd = 40
    for (const wd of a.lyrics.words) {
      if (wd.e < t0 || wd.t > t1) continue
      const x = X(wd.t)
      if (x < lastEnd + 3) continue
      g.fillStyle = accent
      g.fillRect(x, lyricsTop + 2, 1, LAYOUT.lyricsH - 4)
      g.fillStyle = text
      g.fillText(wd.w, x + 3, lyricsTop + 10)
      lastEnd = x + 3 + g.measureText(wd.w).width
    }
  }

  // 12 note lanes: rows top layer first (y = 2..0), columns x = 0..3; split lanes are tinted per version
  if (split)
    for (let lane = 0; lane < 12; lane++)
      tracks.forEach((tr, k) => {
        g.fillStyle = tr.color
        g.globalAlpha = 0.11
        g.fillRect(0, lanesTop + lane * laneH + k * sub, w, sub)
        g.globalAlpha = 0.9
        g.fillRect(0, lanesTop + lane * laneH + k * sub + 1, 3, sub - 2)
      })
  g.globalAlpha = 1
  for (let r = 0; r <= 12; r++) {
    g.fillStyle = grid
    g.globalAlpha = r % 4 === 0 ? 0.9 : 0.35
    g.fillRect(0, lanesTop + r * laneH, w, 1)
  }
  g.globalAlpha = 1
  g.fillStyle = muted
  ;['top', 'mid', 'bot'].forEach((n, i) => g.fillText(n, 6, lanesTop + i * 4 * laneH + 11))

  const size = Math.min(sub - 3, Math.max(6, (60 / tempo.max) * pxPerSec * 0.35))
  tracks.forEach(({ d, color, unmatched }, k) => {
    for (const wl of d.walls) {
      const ts = beatToSec(wl.b), te = beatToSec(wl.b + wl.d)
      if (te < t0 || ts > t1) continue
      g.fillStyle = wl.y >= 2 ? 'rgba(255,176,32,.32)' : 'rgba(255,31,75,.28)' // crouch walls amber
      for (let x = wl.x; x < wl.x + wl.w; x++)
        for (let y = Math.max(0, wl.y); y < Math.min(3, wl.y + wl.h); y++)
          g.fillRect(X(ts), laneY(x, y, k) - sub / 2 + 1, (te - ts) * pxPerSec, sub - 1)
    }
    g.lineWidth = Math.max(1.5, size / 5)
    g.globalAlpha = 0.45
    for (const a of d.arcs ?? []) {
      const ts = beatToSec(a.b), te = beatToSec(a.tb)
      if (te < t0 - 1 || ts > t1 + 1) continue
      const y0 = laneY(a.x, a.y, k), y1 = laneY(a.tx, a.ty, k)
      g.strokeStyle = a.c === 0 ? RED : BLUE
      g.beginPath()
      g.moveTo(X(ts), y0)
      g.bezierCurveTo(X(ts) + (X(te) - X(ts)) / 3, y0 + sub, X(te) - (X(te) - X(ts)) / 3, y1 + sub, X(te), y1)
      g.stroke()
    }
    g.globalAlpha = 1
    for (const c of d.chains ?? []) {
      const ts = beatToSec(c.b), te = beatToSec(c.tb)
      if (te < t0 - 1 || ts > t1 + 1) continue
      const links = Math.max(1, c.sc - 1), y0 = laneY(c.x, c.y, k), y1 = laneY(c.tx, c.ty, k)
      g.fillStyle = c.c === 0 ? RED : BLUE
      for (let i = 1; i <= links; i++) {
        const f = i / links, s = size * 0.45
        g.fillRect(X(ts + (te - ts) * f) - s / 2, y0 + (y1 - y0) * f - s / 4, s, s / 2)
      }
    }
    for (const n of d.notes) {
      const t = beatToSec(n.b)
      if (t < t0 - 1 || t > t1 + 1) continue
      const cy = laneY(n.x, n.y, k)
      if (unmatched?.has(n.b)) drawRing(g, X(t), cy, size, color)
      drawNote(g, X(t), cy, size, n.c, n.d, n.a)
    }
    g.fillStyle = muted
    for (const b of d.bombs) {
      const t = beatToSec(b.b)
      if (t < t0 || t > t1) continue
      g.beginPath()
      g.arc(X(t), laneY(b.x, b.y, k), size / 2.5, 0, 7)
      g.fill()
    }
  })

  // difference strip: notes only in A (upper half) / only in B (lower half)
  if (split) {
    g.fillStyle = muted
    g.fillText('only', 6, diffTop + 12)
    tracks.forEach(({ unmatched, color }, k) => {
      if (!unmatched) return
      g.fillStyle = color
      const y = diffTop + 2 + k * (LAYOUT.diffH - 4) / 2
      for (const b of unmatched) {
        const t = beatToSec(b)
        if (t < t0 || t > t1) continue
        g.fillRect(X(t) - 1.5, y, 3, (LAYOUT.diffH - 4) / 2 - 1)
      }
    })
  }

  // flow issues of the first track
  if (tracks[0])
    for (const i of tracks[0].d.report.issues) {
      const t = beatToSec(i.b)
      if (t < t0 || t > t1) continue
      g.fillStyle = ISSUE_COLOR[i.kind] ?? muted
      const x = X(t)
      g.beginPath()
      g.moveTo(x, lanesBottom + 3)
      g.lineTo(x - 4, lanesBottom + 12)
      g.lineTo(x + 4, lanesBottom + 12)
      g.fill()
    }

  g.fillStyle = accent
  g.fillRect(X(now), 0, 2, h)
}

/** What the player sees: the 4x3 grid with notes of the next `ahead` beats growing as they approach. */
export function drawFront(canvas: HTMLCanvasElement, d: Difficulty | undefined, tempo: Tempo, now: number, ahead = 2) {
  const g = canvas.getContext('2d')!
  const W = canvas.width, H = canvas.height
  g.clearRect(0, 0, W, H)
  const cell = 52, ox = (W - cell * 4) / 2, oy = (H - cell * 3) / 2
  g.strokeStyle = cssVar('--border', '#334155')
  for (let x = 0; x < 4; x++) for (let y = 0; y < 3; y++) g.strokeRect(ox + x * cell + 0.5, oy + y * cell + 0.5, cell - 1, cell - 1)
  if (!d) return
  const beat = tempo.toBeat(now)
  const k = (b: number) => Math.max(0, Math.min(1, 1 - (b - beat) / ahead))
  const inView = (b: number) => b >= beat - 0.15 && b <= beat + ahead
  g.fillStyle = cssVar('--text-muted', '#94a3b8')
  for (const b of d.bombs.filter(b => inView(b.b))) {
    g.globalAlpha = b.b < beat ? 0.25 : 0.25 + 0.75 * k(b.b)
    g.beginPath()
    g.arc(ox + b.x * cell + cell / 2, oy + (2 - b.y) * cell + cell / 2, cell * (0.12 + 0.2 * k(b.b)), 0, 7)
    g.fill()
  }
  const cx = (x: number) => ox + x * cell + cell / 2, cy = (y: number) => oy + (2 - y) * cell + cell / 2
  g.lineWidth = 4
  for (const a of (d.arcs ?? []).filter(a => a.b <= beat + ahead && a.tb >= beat - 0.15)) {
    g.globalAlpha = 0.2 + 0.4 * k(a.b)
    g.strokeStyle = a.c === 0 ? RED : BLUE
    g.beginPath()
    g.moveTo(cx(a.x), cy(a.y))
    g.quadraticCurveTo((cx(a.x) + cx(a.tx)) / 2, Math.max(cy(a.y), cy(a.ty)) + cell * 0.8, cx(a.tx), cy(a.ty))
    g.stroke()
  }
  for (const c of (d.chains ?? []).filter(c => c.b <= beat + ahead && c.tb >= beat - 0.15)) {
    const links = Math.max(1, c.sc - 1)
    g.fillStyle = c.c === 0 ? RED : BLUE
    for (let i = 1; i <= links; i++) {
      const f = i / links, lb = c.b + (c.tb - c.b) * f
      g.globalAlpha = lb < beat ? 0.2 : 0.2 + 0.6 * k(lb)
      const s = cell * (0.15 + 0.25 * k(lb))
      g.fillRect(cx(c.x + (c.tx - c.x) * f) - s / 2, cy(c.y + (c.ty - c.y) * f) - s / 4, s, s / 2)
    }
  }
  for (const n of d.notes.filter(n => inView(n.b)).sort((p, q) => q.b - p.b)) {
    g.globalAlpha = n.b < beat ? 0.25 : 0.25 + 0.75 * k(n.b)
    drawNote(g, cx(n.x), cy(n.y), cell * (0.35 + 0.5 * k(n.b)), n.c, n.d, n.a)
  }
  g.globalAlpha = 1
  g.fillStyle = cssVar('--text-muted', '#94a3b8')
  g.font = '11px Inter, system-ui, sans-serif'
  g.fillText(`beat ${beat.toFixed(2)}`, 6, H - 6)
}
