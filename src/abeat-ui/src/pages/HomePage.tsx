import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { addSongUrl, errorText, uploadSong } from '../api'
import ToggleField from '../components/ToggleField'
import { useSongs } from '../hooks/useSongs'

/** Add a song by upload or link; analysis and a first map then run automatically. */
export default function HomePage() {
  const navigate = useNavigate()
  const { songs, refresh } = useSongs()
  const [url, setUrl] = useState('')
  const [beats, setBeats] = useState('auto')
  const [stems, setStems] = useState(true)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [dragging, setDragging] = useState(false)

  const run = async (action: () => Promise<{ data: { id: string } }>) => {
    setBusy(true)
    setError(null)
    try {
      const r = await action()
      await refresh()
      navigate(`/songs/${r.data.id}`)
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  const onFile = (f?: File) => f && run(() => uploadSong(f, beats, stems))

  return (
    <div className="page">
      <div className="page-header">
        <h2>Add a song</h2>
      </div>

      <div className="add-grid">
        <div
          className={`card drop-zone${dragging ? ' dragging' : ''}`}
          onDragOver={e => { e.preventDefault(); setDragging(true) }}
          onDragLeave={() => setDragging(false)}
          onDrop={e => { e.preventDefault(); setDragging(false); onFile(e.dataTransfer.files[0]) }}
        >
          <div className="drop-icon">🎵</div>
          <p>Drop an audio file here</p>
          <label className="file-btn">
            Choose file
            <input type="file" hidden accept="audio/*,.mp3,.ogg,.flac,.wav,.m4a,.opus,.webm" disabled={busy}
              onChange={e => onFile(e.target.files?.[0])} />
          </label>
        </div>

        <form className="card" onSubmit={e => { e.preventDefault(); if (url.trim()) run(() => addSongUrl(url.trim(), beats, stems)) }}>
          <h3>From a link</h3>
          <label>
            YouTube or YouTube Music URL
            <input type="url" value={url} onChange={e => setUrl(e.target.value)} placeholder="https://music.youtube.com/watch?v=…" />
          </label>
          <button type="submit" disabled={busy || !url.trim()}>Add link</button>
        </form>

        <div className="card">
          <h3>Analysis options</h3>
          <label>
            Beat tracker
            <select value={beats} onChange={e => setBeats(e.target.value)}>
              <option value="auto">auto (beat_this if installed)</option>
              <option value="beat_this">beat_this (neural, accurate)</option>
              <option value="librosa">librosa (fast, simpler)</option>
            </select>
          </label>
          <ToggleField label="Separate stems (Demucs)" hint="Isolates vocals so their rhythm leads the map; adds about one song-length of CPU time" checked={stems} onChange={setStems} />
        </div>
      </div>

      {error && <p className="error-text">{error}</p>}
      <p className="hint">Only use audio you have the rights to. Analysis and a first map run automatically after adding.</p>

      {songs.length > 0 && (
        <>
          <h3 className="section-title">Songs</h3>
          <div className="song-list">
            {songs.map(s => (
              <button key={s.id} className="card song-row" onClick={() => navigate(`/songs/${s.id}`)}>
                {s.status === 'Ready' ? <img src={`/api/songs/${s.id}/cover`} alt="" /> : <div className="cover-placeholder">🎵</div>}
                <span className="song-row-text">
                  <span className="song-row-title">{s.title || s.fileName || s.sourceUrl}</span>
                  <span className="muted">{[s.artist, s.bpm ? `${+s.bpm.toFixed(2)} BPM` : ''].filter(Boolean).join(' · ')}</span>
                </span>
                <span className={`status status-${s.status.toLowerCase()}`}>{s.status}</span>
              </button>
            ))}
          </div>
        </>
      )}
    </div>
  )
}
