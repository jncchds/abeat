import { useEffect, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import {
  coverUrl, deletePlaylist, errorText, getPlaylist, playlistBplistUrl, playlistZipUrl, removeFromPlaylist, renamePlaylist,
  type PlaylistDetails,
} from '../api'
import { usePlaylists } from '../hooks/usePlaylists'
import { diffLabel } from '../utils/format'

/** One playlist: its versions, and the zip / .bplist downloads for BSManager. */
export default function PlaylistPage() {
  const { id = '' } = useParams()
  const navigate = useNavigate()
  const { refresh } = usePlaylists()
  const [data, setData] = useState<PlaylistDetails | null>(null)
  const [title, setTitle] = useState<{ id: string; value: string } | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = () => getPlaylist(id).then(r => setData(r.data)).catch(e => setError(errorText(e)))
  useEffect(() => {
    getPlaylist(id).then(r => setData(r.data)).catch(e => setError(errorText(e)))
  }, [id])

  const p = data?.id === id ? data : null
  const titleValue = title?.id === id ? title.value : p?.title ?? ''

  const saveTitle = async () => {
    if (!p || !titleValue.trim() || titleValue === p.title) return
    await renamePlaylist(id, titleValue).catch(e => setError(errorText(e)))
    await Promise.all([load(), refresh()])
  }

  const remove = async (index: number) => {
    await removeFromPlaylist(id, index).catch(e => setError(errorText(e)))
    await Promise.all([load(), refresh()])
  }

  const onDelete = async () => {
    if (!p || !confirm(`Delete playlist "${p.title}"? The songs and versions stay.`)) return
    await deletePlaylist(id)
    await refresh()
    navigate('/')
  }

  const available = p?.entries.filter(e => !e.missing).length ?? 0

  return (
    <div className="page">
      <div className="page-header playlist-header">
        <input className="playlist-title" value={titleValue} aria-label="Playlist name"
          onChange={e => setTitle({ id, value: e.target.value })} onBlur={saveTitle}
          onKeyDown={e => e.key === 'Enter' && (e.target as HTMLInputElement).blur()} />
        <span className="spacer" />
        <a className={`btn${available ? '' : ' disabled'}`} href={available ? playlistZipUrl(id) : undefined}
          title="All maps plus the .bplist, for BSManager's map import">⬇ Zip for BSManager</a>
        <a className={`btn btn-secondary${available ? '' : ' disabled'}`} href={available ? playlistBplistUrl(id) : undefined}>.bplist only</a>
        <button className="delete-btn" onClick={onDelete}>Delete</button>
      </div>

      {error && <p className="error-text">{error}</p>}

      <div className="card">
        <h3>Install with BSManager</h3>
        <ol className="install-steps">
          <li>Download the zip.</li>
          <li>BSManager → your Beat Saber version → <b>Maps</b> → <b>Import</b> → pick the zip (it installs every map in it).</li>
          <li>Optional, for the in-game playlist: <b>Playlists</b> → <b>Import</b> → pick the .bplist (it is also inside the zip).</li>
        </ol>
        <p className="hint">BSManager's one-click links only find maps published on BeatSaver, so generated maps are imported from the zip.
          In game each map shows its version as the subtitle (“ABeat #3”).</p>
      </div>

      <div className="song-list">
        {p?.entries.map(e => (
          <div key={`${e.index}-${e.version}`} className={`card song-row playlist-row${e.missing ? ' missing' : ''}`}>
            {!e.missing ? <img src={coverUrl(e.songId)} alt="" /> : <div className="cover-placeholder">✕</div>}
            <button className="song-row-text link-like" onClick={() => !e.missing && navigate(`/songs/${e.songId}`)} disabled={e.missing}>
              <span className="song-row-title">{e.artist ? `${e.artist} - ` : ''}{e.title}</span>
              <span className="muted">
                {e.missing
                  ? 'version deleted; skipped in downloads'
                  : `ABeat #${e.number}${e.appVersion ? ` · ${/^\d/.test(e.appVersion) ? 'v' : ''}${e.appVersion}` : ''} · ${e.difficulties.map(diffLabel).join(', ')}`}
              </span>
            </button>
            <button className="icon-btn danger" title="Remove from playlist" onClick={() => remove(e.index)}>✕</button>
          </div>
        ))}
        {p && p.entries.length === 0 && (
          <p className="empty">Empty. Add versions from a song page with “＋ playlist” next to the A / B version picker.</p>
        )}
      </div>
    </div>
  )
}
