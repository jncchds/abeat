import { useEffect, useState } from 'react'
import { errorText, getLyrics, putLyrics, type Analysis } from '../api'

interface Props {
  id: string
  analysis: Analysis
  /** Whether the next re-analysis uses lyric syllables. */
  usesLyrics: boolean
  onClose?: () => void
}

/** Lyrics to align instead of a Whisper transcription; can start from the transcription to correct it. */
export default function LyricsPanel({ id, analysis, usesLyrics, onClose }: Props) {
  const [text, setText] = useState<{ id: string; value: string; saved: string } | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    getLyrics(id).then(r => setText({ id, value: r.data.text, saved: r.data.text })).catch(e => setError(errorText(e)))
  }, [id])

  const cur = text?.id === id ? text : null
  const transcript = analysis.lyrics && analysis.lyrics.source !== 'user' ? analysis.lyrics.words : null

  // one line per phrase: break where the singer pauses for more than ~0.8 s
  const fromTranscript = () => {
    if (!transcript || !cur) return
    const lines: string[][] = []
    transcript.forEach((w, i) => {
      if (i === 0 || w.t - transcript[i - 1].e > 0.8) lines.push([])
      lines[lines.length - 1].push(w.w)
    })
    setText({ ...cur, value: lines.map(l => l.join(' ')).join('\n') })
  }

  const save = async () => {
    if (!cur) return
    try {
      await putLyrics(id, cur.value)
      setText({ ...cur, saved: cur.value })
      setError(null)
    } catch (e) {
      setError(errorText(e))
    }
  }

  return (
    <div className="card lyrics-card">
      <div className="versions-head">
        <h3>Lyrics</h3>
        {transcript && <button className="btn-secondary" onClick={fromTranscript} title="Replace the text with Whisper's transcription to correct it">From transcription</button>}
        <button onClick={save} disabled={!cur || cur.value === cur.saved}>Save</button>
        {onClose && <button className="icon-btn" title="Close" onClick={onClose}>✕</button>}
      </div>
      <textarea rows={8} value={cur?.value ?? ''} disabled={!cur} placeholder="Paste the lyrics here, with repeated choruses written out"
        onChange={e => cur && setText({ ...cur, value: e.target.value })} />
      <p className="hint">
        {usesLyrics
          ? 'Aligned to the vocals instead of a transcription on the next re-analysis.'
          : 'Used when vocal onsets come from lyrics: pick "vocals: lyrics" next to Re-analyze, then Re-analyze.'}
        {' '}Write every line as sung, in order; extra ad-libs are tolerated.
      </p>
      {error && <p className="error-text">{error}</p>}
    </div>
  )
}
