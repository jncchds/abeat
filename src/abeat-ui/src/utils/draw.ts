import type { Analysis, Difficulty } from '../api'
import { cssVar } from './format'

// default Beat Saber saber colours (left red, right blue)
export const RED = '#ff1f4b'
export const BLUE = '#00a3ff'
const DIR_VEC: [number, number][] = [[0, 1], [0, -1], [-1, 0], [1, 0], [-0.707, 0.707], [0.707, 0.707], [-0.707, -0.707], [0.707, -0.707], [0, 0]]
const SECTION_COLORS = ['#00a3ff', '#ff1f4b', '#b14dff', '#00e5c7', '#ff7a1a', '#ff3dbb', '#4d6bff', '#c6ff1a']

export const ISSUE_COLOR: Record<string, string> = {
  Reset: '#ff1f4b', VisionBlock: '#ffb020', Crossover: '#ff7a1a', HighCost: '#8a8fb5', WallClash: '#ff3dbb', BombHit: '#ff3dbb', HandClash: '#ff3dbb',
}

export interface View { pxPerSec: number; start: number }

export const LAYOUT = { layersTop: 64, layerH: 9, laneH: 15, issuesH: 14 }

export function timelineHeight(layerCount: number) {
  return LAYOUT.layersTop + layerCount * LAYOUT.layerH + 8 + 12 * LAYOUT.laneH + LAYOUT.issuesH + 4
}

/** Saber-coloured note with a cut arrow (white) or dot. */
export function drawNote(g: CanvasRenderingContext2D, cx: number, cy: number, size: number, color: number, dir: number) {
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
  const [vx, vy] = DIR_VEC[dir]
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

/** Outline-only note, used to overlay the human reference map. */
function drawGhost(g: CanvasRenderingContext2D, cx: number, cy: number, size: number, color: number) {
  g.strokeStyle = color === 0 ? RED : BLUE
  g.lineWidth = 1.5
  const r = size / 2 + 1.5
  g.beginPath()
  g.roundRect(cx - r, cy - r, r * 2, r * 2, 3)
  g.stroke()
}

export function drawTimeline(canvas: HTMLCanvasElement, a: Analysis, d: Difficulty | undefined, view: View, now: number, ghost?: Difficulty) {
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
  const lanesTop = LAYOUT.layersTop + layers.length * LAYOUT.layerH + 8
  const lanesBottom = lanesTop + 12 * LAYOUT.laneH
  const spb = 60 / a.tempo.bpm
  const beatToSec = (b: number) => b * spb

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
  const downs = new Set(a.tempo.downbeats.map(x => Math.round(x / spb)))
  const showBeats = spb * pxPerSec > 8
  for (let b = Math.floor(t0 / spb); b * spb <= t1; b++) {
    const bar = downs.has(b)
    if (!bar && !showBeats) continue
    g.fillStyle = grid
    g.globalAlpha = bar ? 1 : 0.45
    g.fillRect(Math.round(X(b * spb)), 22, 1, lanesBottom - 22 + LAYOUT.issuesH)
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

  // 12 note lanes: rows top layer first (y = 2..0), columns x = 0..3
  for (let r = 0; r <= 12; r++) {
    g.fillStyle = grid
    g.globalAlpha = r % 4 === 0 ? 0.9 : 0.35
    g.fillRect(0, lanesTop + r * LAYOUT.laneH, w, 1)
  }
  g.globalAlpha = 1
  g.fillStyle = muted
  ;['top', 'mid', 'bot'].forEach((n, i) => g.fillText(n, 2, lanesTop + i * 4 * LAYOUT.laneH + 11))

  if (d) {
    for (const wl of d.walls) {
      const ts = beatToSec(wl.b), te = beatToSec(wl.b + wl.d)
      if (te < t0 || ts > t1) continue
      g.fillStyle = wl.y >= 2 ? 'rgba(255,176,32,.32)' : 'rgba(255,31,75,.28)' // crouch walls amber
      for (let x = wl.x; x < wl.x + wl.w; x++)
        for (let y = Math.max(0, wl.y); y < Math.min(3, wl.y + wl.h); y++)
          g.fillRect(X(ts), lanesTop + ((2 - y) * 4 + x) * LAYOUT.laneH + 1, (te - ts) * pxPerSec, LAYOUT.laneH - 1)
    }
    const size = Math.min(LAYOUT.laneH - 3, Math.max(6, spb * pxPerSec * 0.35))
    if (ghost)
      for (const n of ghost.notes) {
        const t = beatToSec(n.b)
        if (t < t0 - 1 || t > t1 + 1) continue
        drawGhost(g, X(t), lanesTop + ((2 - n.y) * 4 + n.x) * LAYOUT.laneH + LAYOUT.laneH / 2, size, n.c)
      }
    for (const n of d.notes) {
      const t = beatToSec(n.b)
      if (t < t0 - 1 || t > t1 + 1) continue
      drawNote(g, X(t), lanesTop + ((2 - n.y) * 4 + n.x) * LAYOUT.laneH + LAYOUT.laneH / 2, size, n.c, n.d)
    }
    g.fillStyle = muted
    for (const b of d.bombs) {
      const t = beatToSec(b.b)
      if (t < t0 || t > t1) continue
      g.beginPath()
      g.arc(X(t), lanesTop + ((2 - b.y) * 4 + b.x) * LAYOUT.laneH + LAYOUT.laneH / 2, size / 2.5, 0, 7)
      g.fill()
    }
    for (const i of d.report.issues) {
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
  }

  g.fillStyle = accent
  g.fillRect(X(now), 0, 2, h)
}

/** What the player sees: the 4x3 grid with notes of the next `ahead` beats growing as they approach. */
export function drawFront(canvas: HTMLCanvasElement, d: Difficulty | undefined, bpm: number, now: number, ahead = 2) {
  const g = canvas.getContext('2d')!
  const W = canvas.width, H = canvas.height
  g.clearRect(0, 0, W, H)
  const cell = 52, ox = (W - cell * 4) / 2, oy = (H - cell * 3) / 2
  g.strokeStyle = cssVar('--border', '#334155')
  for (let x = 0; x < 4; x++) for (let y = 0; y < 3; y++) g.strokeRect(ox + x * cell + 0.5, oy + y * cell + 0.5, cell - 1, cell - 1)
  if (!d) return
  const beat = (now * bpm) / 60
  const k = (b: number) => Math.max(0, Math.min(1, 1 - (b - beat) / ahead))
  const inView = (b: number) => b >= beat - 0.15 && b <= beat + ahead
  g.fillStyle = cssVar('--text-muted', '#94a3b8')
  for (const b of d.bombs.filter(b => inView(b.b))) {
    g.globalAlpha = b.b < beat ? 0.25 : 0.25 + 0.75 * k(b.b)
    g.beginPath()
    g.arc(ox + b.x * cell + cell / 2, oy + (2 - b.y) * cell + cell / 2, cell * (0.12 + 0.2 * k(b.b)), 0, 7)
    g.fill()
  }
  for (const n of d.notes.filter(n => inView(n.b)).sort((p, q) => q.b - p.b)) {
    g.globalAlpha = n.b < beat ? 0.25 : 0.25 + 0.75 * k(n.b)
    drawNote(g, ox + n.x * cell + cell / 2, oy + (2 - n.y) * cell + cell / 2, cell * (0.35 + 0.5 * k(n.b)), n.c, n.d)
  }
  g.globalAlpha = 1
  g.fillStyle = cssVar('--text-muted', '#94a3b8')
  g.font = '11px Inter, system-ui, sans-serif'
  g.fillText(`beat ${beat.toFixed(2)}`, 6, H - 6)
}
