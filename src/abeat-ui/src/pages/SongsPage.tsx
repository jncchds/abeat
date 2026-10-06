import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { byActivity, useSongs } from '../hooks/useSongs'

const when = (iso?: string | null) => iso ? new Date(iso).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' }) : ''

/** Every song, the most recently generated first, with a filter over title, artist and file name. */
export default function SongsPage() {
  const navigate = useNavigate()
  const { songs } = useSongs()
  const [query, setQuery] = useState('')

  const q = query.trim().toLowerCase()
  const shown = byActivity(songs).filter(s => !q || [s.title, s.artist, s.fileName, s.sourceUrl].some(v => v?.toLowerCase().includes(q)))

  return (
    <div className="page">
      <div className="page-header">
        <h2>Songs</h2>
        <span className="muted">{songs.length}</span>
        <span className="spacer" />
        <input className="songs-filter" type="search" placeholder="Filter songs" value={query} onChange={e => setQuery(e.target.value)} aria-label="Filter songs" />
        <button className="btn" onClick={() => navigate('/')}>➕ Add song</button>
      </div>

      <div className="song-list">
        {shown.map(s => (
          <button key={s.id} className="card song-row" onClick={() => navigate(`/songs/${s.id}`)}>
            {s.hasAnalysis || s.status === 'Ready' ? <img src={`/api/songs/${s.id}/cover?r=${s.analysisRevision ?? 0}`} alt="" /> : <div className="cover-placeholder">🎵</div>}
            <span className="song-row-text">
              <span className="song-row-title">{s.title || s.fileName || s.sourceUrl}</span>
              <span className="muted">{[s.artist, s.bpm ? `${+s.bpm.toFixed(2)} BPM` : ''].filter(Boolean).join(' · ')}</span>
              <span className="muted song-row-when">{s.lastGeneratedUtc ? `generated ${when(s.lastGeneratedUtc)}` : `added ${when(s.createdUtc)}`}</span>
            </span>
            <span className={`status status-${s.status.toLowerCase()}`}>{s.status}</span>
          </button>
        ))}
        {songs.length === 0 && <p className="muted">No songs yet.</p>}
        {songs.length > 0 && shown.length === 0 && <p className="muted">No song matches “{query}”.</p>}
      </div>
    </div>
  )
}
