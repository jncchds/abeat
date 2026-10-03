import { useEffect, useLayoutEffect, useRef } from 'react'
import type { Difficulty } from '../api'
import { drawFront } from '../utils/draw'

/** Player's-eye 4x3 grid synced to the audio. */
export default function FrontView({ difficulty, bpm, audio }: { difficulty?: Difficulty; bpm: number; audio: HTMLAudioElement | null }) {
  const canvas = useRef<HTMLCanvasElement>(null)
  const props = useRef({ difficulty, bpm, audio })
  useLayoutEffect(() => { props.current = { difficulty, bpm, audio } })

  useEffect(() => {
    let raf = 0
    const frame = () => {
      const { difficulty: d, bpm: b, audio: a } = props.current
      if (canvas.current) drawFront(canvas.current, d, b, a?.currentTime ?? 0)
      raf = requestAnimationFrame(frame)
    }
    raf = requestAnimationFrame(frame)
    return () => cancelAnimationFrame(raf)
  }, [])

  return (
    <div className="card front-card">
      <canvas ref={canvas} width={240} height={190} />
      <div className="hint">player view · next 2 beats</div>
    </div>
  )
}
