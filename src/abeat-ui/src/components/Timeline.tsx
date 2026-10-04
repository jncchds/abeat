import { useEffect, useLayoutEffect, useRef } from 'react'
import type { Analysis } from '../api'
import { drawTimeline, timelineHeight, type TapRow, type Track, type View } from '../utils/draw'

interface Props {
  analysis: Analysis
  /** One map, or two (A on top, B below in every lane). */
  tracks: Track[]
  /** Legend per track, e.g. "A · v0.1.1 12:03 · Expert". */
  labels?: string[]
  audio: HTMLAudioElement | null
  follow: boolean
  /** Tap-along runs shown above the lanes. */
  taps?: TapRow[]
}

/** Zoomable song timeline: sections, energy, onset layers, the 12 note lanes and flow issues.
 * Redraws itself every frame while playing; wheel zooms, drag scrolls, click seeks. */
export default function Timeline({ analysis, tracks, labels, audio, follow, taps = [] }: Props) {
  const canvas = useRef<HTMLCanvasElement>(null)
  const view = useRef<View>({ pxPerSec: 60, start: 0 })
  const props = useRef({ analysis, tracks, audio, follow, taps })
  useLayoutEffect(() => { props.current = { analysis, tracks, audio, follow, taps } })

  useEffect(() => {
    view.current.start = 0
  }, [analysis])

  useEffect(() => {
    let raf = 0
    const frame = () => {
      const c = canvas.current
      const { analysis: a, tracks: tr, audio: au, follow: f, taps: tp } = props.current
      if (c && a) {
        const now = au?.currentTime ?? 0
        const width = c.clientWidth / view.current.pxPerSec
        if (f && au && !au.paused && (now > view.current.start + width * 0.85 || now < view.current.start))
          view.current.start = Math.max(0, now - width * 0.15)
        drawTimeline(c, a, tr, view.current, now, tp)
      }
      raf = requestAnimationFrame(frame)
    }
    raf = requestAnimationFrame(frame)
    return () => cancelAnimationFrame(raf)
  }, [])

  useEffect(() => {
    const c = canvas.current
    if (!c) return
    let drag: { x: number; start: number; moved: boolean } | null = null
    // touch: two pointers pinch-zoom around their midpoint
    const pointers = new Map<number, number>()
    let pinch: { dist: number; pxPerSec: number } | null = null
    const down = (e: PointerEvent) => {
      pointers.set(e.pointerId, e.clientX)
      c.setPointerCapture(e.pointerId)
      if (pointers.size === 2) {
        const [x1, x2] = [...pointers.values()]
        pinch = { dist: Math.max(10, Math.abs(x1 - x2)), pxPerSec: view.current.pxPerSec }
        drag = null
      } else if (pointers.size === 1) drag = { x: e.clientX, start: view.current.start, moved: false }
    }
    const move = (e: PointerEvent) => {
      if (!pointers.has(e.pointerId)) return
      pointers.set(e.pointerId, e.clientX)
      if (pinch && pointers.size === 2) {
        const [x1, x2] = [...pointers.values()]
        const rect = c.getBoundingClientRect()
        const mid = (x1 + x2) / 2 - rect.left
        zoomAt(view.current, mid, (pinch.pxPerSec * Math.max(10, Math.abs(x1 - x2))) / pinch.dist / view.current.pxPerSec)
        return
      }
      if (!drag) return
      const dx = e.clientX - drag.x
      if (Math.abs(dx) > 3) drag.moved = true
      if (drag.moved) view.current.start = Math.max(0, drag.start - dx / view.current.pxPerSec)
    }
    const up = (e: PointerEvent) => {
      pointers.delete(e.pointerId)
      const au = props.current.audio
      if (drag && !drag.moved && !pinch && au) {
        const rect = c.getBoundingClientRect()
        au.currentTime = Math.max(0, view.current.start + (e.clientX - rect.left) / view.current.pxPerSec)
      }
      drag = null
      if (pointers.size === 0) pinch = null
    }
    const wheel = (e: WheelEvent) => {
      e.preventDefault()
      const v = view.current
      const rect = c.getBoundingClientRect()
      if (e.shiftKey || Math.abs(e.deltaX) > Math.abs(e.deltaY)) v.start = Math.max(0, v.start + (e.deltaX || e.deltaY) / v.pxPerSec)
      else zoomAt(v, e.clientX - rect.left, e.deltaY < 0 ? 1.2 : 1 / 1.2)
    }
    c.addEventListener('pointerdown', down)
    c.addEventListener('pointermove', move)
    c.addEventListener('pointerup', up)
    c.addEventListener('pointercancel', up)
    c.addEventListener('wheel', wheel, { passive: false })
    return () => {
      c.removeEventListener('pointerdown', down)
      c.removeEventListener('pointermove', move)
      c.removeEventListener('pointerup', up)
      c.removeEventListener('pointercancel', up)
      c.removeEventListener('wheel', wheel)
    }
  }, [])

  const zoomCenter = (factor: number) => zoomAt(view.current, (canvas.current?.clientWidth ?? 0) / 2, factor)

  return (
    <div className="card timeline-card">
      {tracks.length > 1 && labels && (
        <div className="timeline-legend">
          {tracks.map((t, k) => (
            <span key={k} className="legend-chip" style={{ borderColor: t.color, color: t.color }}>
              <span className="legend-swatch" style={{ background: t.color }} />
              {k === 0 ? 'top of each lane' : 'bottom of each lane'}: <b>{labels[k]}</b>
              {t.unmatched && <span className="muted"> · {t.unmatched.size} only here (ringed)</span>}
            </span>
          ))}
        </div>
      )}
      <canvas ref={canvas} className="timeline" style={{ height: timelineHeight(Object.keys(analysis.layers).length, tracks.length > 1, !!analysis.lyrics?.words.length, taps.length) }} />
      <div className="timeline-foot">
        <div className="hint">wheel / pinch: zoom · drag: scroll · click: seek · lanes: top row first, columns left → right</div>
        <div className="zoom-btns">
          <button className="icon-btn" title="Zoom out" onClick={() => zoomCenter(1 / 1.5)}>−</button>
          <button className="icon-btn" title="Zoom in" onClick={() => zoomCenter(1.5)}>+</button>
        </div>
      </div>
    </div>
  )
}

/** Zoom by `factor` keeping the time under canvas x `px` in place. */
function zoomAt(v: View, px: number, factor: number) {
  const at = v.start + px / v.pxPerSec
  v.pxPerSec = Math.min(1200, Math.max(4, v.pxPerSec * factor))
  v.start = Math.max(0, at - px / v.pxPerSec)
}
