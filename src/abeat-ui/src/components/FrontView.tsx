import { useEffect, useLayoutEffect, useRef } from 'react'
import type { Difficulty } from '../api'
import { drawFront } from '../utils/draw'
import type { Tempo } from '../utils/tempo'

/** Player's-eye 4x3 grid synced to the audio. */
interface Props {
  difficulty?: Difficulty
  tempo: Tempo
  audio: HTMLAudioElement | null
  /** Version label and colour when two versions are compared. */
  label?: string
  color?: string
}

export default function FrontView({ difficulty, tempo, audio, label, color }: Props) {
  const canvas = useRef<HTMLCanvasElement>(null)
  const props = useRef({ difficulty, tempo, audio })
  useLayoutEffect(() => { props.current = { difficulty, tempo, audio } })

  useEffect(() => {
    let raf = 0
    const frame = () => {
      const { difficulty: d, tempo: t, audio: a } = props.current
      if (canvas.current) drawFront(canvas.current, d, t, a?.currentTime ?? 0)
      raf = requestAnimationFrame(frame)
    }
    raf = requestAnimationFrame(frame)
    return () => cancelAnimationFrame(raf)
  }, [])

  return (
    <div className="card front-card" style={color ? { borderColor: color } : undefined}>
      {label && <div className="front-label" style={{ color }}>{label}</div>}
      <canvas ref={canvas} width={240} height={190} />
      <div className="hint">player view · next 2 beats</div>
    </div>
  )
}
