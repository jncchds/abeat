import { useEffect, useLayoutEffect, useRef } from 'react'
import type { Analysis, Difficulty } from '../api'
import { drawTimeline, timelineHeight, type View } from '../utils/draw'

interface Props {
  analysis: Analysis
  difficulty?: Difficulty
  audio: HTMLAudioElement | null
  follow: boolean
  /** Drawn as outlines underneath (human reference in overlay mode). */
  ghost?: Difficulty
}

/** Zoomable song timeline: sections, energy, onset layers, the 12 note lanes and flow issues.
 * Redraws itself every frame while playing; wheel zooms, drag scrolls, click seeks. */
export default function Timeline({ analysis, difficulty, audio, follow, ghost }: Props) {
  const canvas = useRef<HTMLCanvasElement>(null)
  const view = useRef<View>({ pxPerSec: 60, start: 0 })
  const props = useRef({ analysis, difficulty, audio, follow, ghost })
  useLayoutEffect(() => { props.current = { analysis, difficulty, audio, follow, ghost } })

  useEffect(() => {
    view.current.start = 0
  }, [analysis])

  useEffect(() => {
    let raf = 0
    const frame = () => {
      const c = canvas.current
      const { analysis: a, difficulty: d, audio: au, follow: f, ghost: gh } = props.current
      if (c && a) {
        const now = au?.currentTime ?? 0
        const width = c.clientWidth / view.current.pxPerSec
        if (f && au && !au.paused && (now > view.current.start + width * 0.85 || now < view.current.start))
          view.current.start = Math.max(0, now - width * 0.15)
        drawTimeline(c, a, d, view.current, now, gh)
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
      <canvas ref={canvas} className="timeline" style={{ height: timelineHeight(Object.keys(analysis.layers).length) }} />
      <div className="hint">wheel: zoom · drag / shift+wheel: scroll · click: seek · lanes: top row first, columns left → right</div>
    </div>
  )
}
