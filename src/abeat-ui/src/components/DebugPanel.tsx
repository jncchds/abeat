import { useEffect, useRef, useState } from 'react'
import { getStems, stemUrl, type StemFile } from '../api'

/** Debug-only downloads (separated stems, the raw analysis) and a stem mixer for the player. */
export default function DebugPanel({ id, onPreview }: { id: string; onPreview?: (url: string | null) => void }) {
  const [stems, setStems] = useState<StemFile[] | null>(null)
  const [on, setOn] = useState<Set<string>>(new Set())
  const [mixing, setMixing] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const mixUrl = useRef<string | null>(null)

  useEffect(() => {
    getStems(id).then(r => { setStems(r.data); setOn(new Set(r.data.map(s => s.file))) }).catch(() => setStems([]))
  }, [id])
  useEffect(() => () => { if (mixUrl.current) URL.revokeObjectURL(mixUrl.current) }, [])

  const toggle = (file: string) => setOn(s => {
    const next = new Set(s)
    if (next.has(file)) next.delete(file)
    else next.add(file)
    return next
  })

  const play = async () => {
    if (!onPreview || !stems) return
    const files = stems.filter(s => on.has(s.file)).map(s => s.file)
    if (files.length === stems.length) { onPreview(null); return }
    if (files.length === 1) { onPreview(stemUrl(id, files[0])); return }
    setMixing(true)
    setError(null)
    try {
      const url = URL.createObjectURL(await mixStems(files.map(f => stemUrl(id, f))))
      if (mixUrl.current) URL.revokeObjectURL(mixUrl.current)
      mixUrl.current = url
      onPreview(url)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setMixing(false)
    }
  }

  const all = !!stems && on.size === stems.length
  return (
    <div className="card debug-card">
      <h3>Debug <span className="muted">Ctrl+Shift+D or tap the title 10 times to hide</span></h3>
      <div className="debug-row">
        {stems === null && <span className="muted">loading…</span>}
        {stems?.length === 0 && (
          <span className="muted">No stems kept for this song. Re-analyze with stems on to separate and keep them.</span>
        )}
        {stems?.map(s => (
          <span key={s.file} className="stem-chip">
            {onPreview && (
              <button className={`btn-secondary stem-toggle${on.has(s.file) ? ' active' : ''}`} aria-pressed={on.has(s.file)}
                title="Include in the player mix" onClick={() => toggle(s.file)}>{on.has(s.file) ? '✓' : '○'} {s.name}</button>
            )}
            <a className="btn btn-secondary" href={stemUrl(id, s.file)} download title={`Download ${s.name} (${(s.bytes / 1e6).toFixed(1)} MB)`}>
              ⬇{onPreview ? '' : ` ${s.name}`}
            </a>
          </span>
        ))}
        {onPreview && !!stems?.length && (
          <button onClick={play} disabled={mixing || on.size === 0}
            title="Plays the ticked stems in the player (same timing as the song); all ticked plays the original mix">
            {mixing ? 'Mixing…' : all ? '▶ Original mix' : '▶ Play ticked stems'}
          </button>
        )}
        <a className="btn btn-secondary" href={`/api/songs/${id}/analysis.json`} download>⬇ analysis.json</a>
      </div>
      {error && <p className="error-text">{error}</p>}
    </div>
  )
}

/** Sums the stems (decoded one after another to keep memory down) into one 16-bit stereo WAV. */
async function mixStems(urls: string[]): Promise<Blob> {
  const rate = 44100
  const ctx = new OfflineAudioContext(2, 1, rate)
  let out: Float32Array[] = []
  for (const url of urls) {
    const r = await fetch(url)
    if (!r.ok) throw new Error(`stem ${r.status}`)
    const buf = await ctx.decodeAudioData(await r.arrayBuffer())
    if (!out.length) out = [new Float32Array(buf.length), new Float32Array(buf.length)]
    for (let c = 0; c < 2; c++) {
      const src = buf.getChannelData(Math.min(c, buf.numberOfChannels - 1)), dst = out[c]
      for (let i = 0, n = Math.min(src.length, dst.length); i < n; i++) dst[i] += src[i]
    }
  }
  return wav(out, rate)
}

function wav(ch: Float32Array[], rate: number): Blob {
  const n = ch[0].length, bytes = n * 4
  const v = new DataView(new ArrayBuffer(44 + bytes))
  const str = (o: number, s: string) => { for (let i = 0; i < s.length; i++) v.setUint8(o + i, s.charCodeAt(i)) }
  str(0, 'RIFF'); v.setUint32(4, 36 + bytes, true); str(8, 'WAVE')
  str(12, 'fmt '); v.setUint32(16, 16, true); v.setUint16(20, 1, true); v.setUint16(22, 2, true)
  v.setUint32(24, rate, true); v.setUint32(28, rate * 4, true); v.setUint16(32, 4, true); v.setUint16(34, 16, true)
  str(36, 'data'); v.setUint32(40, bytes, true)
  for (let i = 0, o = 44; i < n; i++)
    for (let c = 0; c < 2; c++, o += 2) v.setInt16(o, Math.max(-1, Math.min(1, ch[c][i])) * 0x7fff, true)
  return new Blob([v], { type: 'audio/wav' })
}
