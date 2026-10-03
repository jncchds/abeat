import { useEffect, useLayoutEffect, useRef } from 'react'
import type { Analysis } from '../api'
import { drawTimeline, timelineHeight, type Track, type View } from '../utils/draw'

interface Props {
  analysis: Analysis
  /** One map, or two (A on top, B below in every lane). */
  tracks: Track[]
  /** Legend per track, e.g. "A · v0.1.1 12:03 · Expert". */
  labels?: string[]
  audio: HTMLAudioElement | null
  follow: boolean
}

/** Zoomable song timeline: sections, energy, onset layers, the 12 note lanes and flow issues.
 * Redraws itself every frame while playing; wheel zooms, drag scrolls, click seeks. */
export default function Timeline({ analysis, tracks, labels, audio, follow }: Props) {
  const canvas = useRef<HTMLCanvasElement>(null)
  const view = useRef<View>({ pxPerSec: 60, start: 0 })
  const props = useRef({ analysis, tracks, audio, follow })
  useLayoutEffect(() => { props.current = { analysis, tracks, audio, follow } })

  useEffect(() => {
    view.current.start = 0
  }, [analysis])

  useEffect(() => {
    let raf = 0
    const frame = () => {
      const c = canvas.current
      const { analysis: a, tracks: tr, audio: au, follow: f } = props.current
      if (c && a) {
        const now = au?.currentTime ?? 0
        const width = c.clientWidth / view.current.pxPerSec
        if (f && au && !au.paused && (now > view.current.start + width * 0.85 || now < view.current.start))
          view.current.start = Math.max(0, now - width * 0.15)
        drawTimeline(c, a, tr, view.current, now)
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
    const down = (e: PointerEvent) => {
      drag = { x: e.clientX, start: view.current.start, moved: false }
      c.setPointerCapture(e.pointerId)
    }
    const move = (e: PointerEvent) => {
      if (!drag) return
      const dx = e.clientX - drag.x
      if (Math.abs(dx) > 3) drag.moved = true
      if (drag.moved) view.current.start = Math.max(0, drag.start - dx / view.current.pxPerSec)
    }
    const up = (e: PointerEvent) => {
      const au = props.current.audio
      if (drag && !drag.moved && au) {
        const rect = c.getBoundingClientRect()
        au.currentTime = Math.max(0, view.current.start + (e.clientX - rect.left) / view.current.pxPerSec)
      }
      drag = null
    }
    const wheel = (e: WheelEvent) => {
      e.preventDefault()
      const v = view.current
      const rect = c.getBoundingClientRect()
      if (e.shiftKey || Math.abs(e.deltaX) > Math.abs(e.deltaY)) {
        v.start = Math.max(0, v.start + (e.deltaX || e.deltaY) / v.pxPerSec)
      } else {
        const at = v.start + (e.clientX - rect.left) / v.pxPerSec
        v.pxPerSec = Math.min(1200, Math.max(4, v.pxPerSec * (e.deltaY < 0 ? 1.2 : 1 / 1.2)))
        v.start = Math.max(0, at - (e.clientX - rect.left) / v.pxPerSec)
      }
    }
    c.addEventListener('pointerdown', down)
    c.addEventListener('pointermove', move)
    c.addEventListener('pointerup', up)
    c.addEventListener('wheel', wheel, { passive: false })
    return () => {
      c.removeEventListener('pointerdown', down)
      c.removeEventListener('pointermove', move)
      c.removeEventListener('pointerup', up)
      c.removeEventListener('wheel', wheel)
    }
  }, [])

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
      <canvas ref={canvas} className="timeline" style={{ height: timelineHeight(Object.keys(analysis.layers).length, tracks.length > 1) }} />
      <div className="hint">wheel: zoom · drag / shift+wheel: scroll · click: seek · lanes: top row first, columns left → right</div>
    </div>
  )
}
