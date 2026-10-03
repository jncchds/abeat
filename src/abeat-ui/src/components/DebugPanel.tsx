import { useEffect, useState } from 'react'
import { getStems, stemUrl, type StemFile } from '../api'

/** Debug-only downloads: separated stems and the raw analysis. */
export default function DebugPanel({ id, onPreview }: { id: string; onPreview?: (url: string | null) => void }) {
  const [stems, setStems] = useState<StemFile[] | null>(null)

  useEffect(() => {
    getStems(id).then(r => setStems(r.data)).catch(() => setStems([]))
  }, [id])

  return (
    <div className="card debug-card">
      <h3>Debug <span className="muted">Ctrl+Shift+D to hide</span></h3>
      <div className="debug-row">
        {stems === null && <span className="muted">loading…</span>}
        {stems?.length === 0 && (
          <span className="muted">No stems kept for this song. Re-analyze with stems on to separate and keep them.</span>
        )}
        {stems?.map(s => (
          <span key={s.file} className="stem-chip">
            <a className="btn btn-secondary" href={stemUrl(id, s.file)} download title={`${(s.bytes / 1e6).toFixed(1)} MB`}>⬇ {s.name}</a>
            {onPreview && <button className="btn-secondary" title="Listen in the player" onClick={() => onPreview(stemUrl(id, s.file))}>▶</button>}
          </span>
        ))}
        <a className="btn btn-secondary" href={`/api/songs/${id}/analysis.json`} download>⬇ analysis.json</a>
        {onPreview && <button className="btn-secondary" onClick={() => onPreview(null)}>♫ full mix</button>}
      </div>
    </div>
  )
}
