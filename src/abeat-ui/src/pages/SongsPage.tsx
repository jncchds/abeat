import { useState } from 'react'
import { useNavigate, useSearchParams } from 'react-router-dom'
import { createFolder, deleteFolder, errorText, moveFolder, renameFolder, setSongFolder, type Folder, type SongMeta } from '../api'
import { byActivity, folderPath, useSongs } from '../hooks/useSongs'

const when = (iso?: string | null) => iso ? new Date(iso).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' }) : ''

const SONG = 'application/x-abeat-song'
const FOLDER = 'application/x-abeat-folder'

/** Songs by folder: the open folder's sub-folders first, then its songs, the most recently generated first.
 * The filter searches every folder. Songs (and folders) can be dragged onto a folder or a breadcrumb. */
export default function SongsPage() {
  const navigate = useNavigate()
  const { songs, folders, refresh } = useSongs()
  const [params, setParams] = useSearchParams()
  const [query, setQuery] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [dropTarget, setDropTarget] = useState<string | null>(null)

  const byId = new Map(folders.map(f => [f.id, f]))
  const current = byId.get(params.get('folder') ?? '') ?? null
  const parent = current?.parentId ? byId.get(current.parentId) ?? null : null
  const depth = current ? (parent ? 2 : 1) : 0
  // a song or folder whose folder is gone shows at the top level
  const inFolder = (id?: string | null) => (id && byId.has(id) ? id : null) === (current?.id ?? null)

  const open = (f: Folder | null) => setParams(f ? { folder: f.id } : {})
  const run = async (action: () => Promise<unknown>) => {
    setError(null)
    try { await action(); await refresh() } catch (e) { setError(errorText(e)) }
  }

  const q = query.trim().toLowerCase()
  const shown = byActivity(songs).filter(s => q
    ? [s.title, s.artist, s.fileName, s.sourceUrl].some(v => v?.toLowerCase().includes(q))
    : inFolder(s.folderId))
  const subfolders = q ? [] : folders.filter(f => inFolder(f.parentId))

  const count = (f: Folder) => {
    const ids = new Set([f.id, ...folders.filter(x => x.parentId === f.id).map(x => x.id)])
    const n = songs.filter(s => s.folderId && ids.has(s.folderId)).length
    const subs = ids.size - 1
    return [`${n} song${n === 1 ? '' : 's'}`, subs ? `${subs} folder${subs === 1 ? '' : 's'}` : ''].filter(Boolean).join(' · ')
  }

  const newFolder = () => {
    const name = prompt('Name of the new folder', 'New folder')
    if (name) run(() => createFolder(name, current?.id ?? null))
  }
  const rename = () => {
    const name = current && prompt('Folder name', current.name)
    if (current && name) run(() => renameFolder(current.id, name))
  }
  const remove = () => {
    if (!current || !confirm(`Delete the folder “${current.name}”? Its songs and folders move up to ${parent ? `“${parent.name}”` : 'the top level'}.`)) return
    run(async () => { await deleteFolder(current.id); open(parent) })
  }

  // drag and drop: a target id of "" is the top level
  const dropProps = (target: string) => ({
    onDragOver: (e: React.DragEvent) => {
      if (![SONG, FOLDER].some(t => e.dataTransfer.types.includes(t))) return
      e.preventDefault()
      setDropTarget(target)
    },
    onDragLeave: () => setDropTarget(t => t === target ? null : t),
    onDrop: (e: React.DragEvent) => {
      e.preventDefault()
      setDropTarget(null)
      const song = e.dataTransfer.getData(SONG)
      const folder = e.dataTransfer.getData(FOLDER)
      if (song) run(() => setSongFolder(song, target || null))
      else if (folder && folder !== target) run(() => moveFolder(folder, target || null))
    },
  })
  const dropClass = (target: string) => dropTarget === target ? ' drop-target' : ''

  const crumbs: [string, string, Folder | null][] = [['', 'Songs', null], ...(parent ? [[parent.id, parent.name, parent] as [string, string, Folder]] : []),
    ...(current ? [[current.id, current.name, current] as [string, string, Folder]] : [])]

  return (
    <div className="page">
      <div className="page-header">
        <h2 className="breadcrumbs">
          {crumbs.map(([key, name, f], i) => (
            <span key={key || 'root'}>
              {i > 0 && <span className="muted crumb-sep">›</span>}
              {i < crumbs.length - 1
                ? <button className={`link-btn crumb${dropClass(key)}`} onClick={() => open(f)} {...dropProps(key)}>{name}</button>
                : <span className="crumb">{name}</span>}
            </span>
          ))}
        </h2>
        <span className="muted">{current ? count(current) : songs.length}</span>
        <span className="spacer" />
        <input className="songs-filter" type="search" placeholder="Filter all songs" value={query} onChange={e => setQuery(e.target.value)} aria-label="Filter songs" />
        {depth < 2 && <button className="btn-secondary" onClick={newFolder} title={current ? 'New folder inside this one' : 'New folder'}>📁 New folder</button>}
        {current && <button className="btn-secondary" onClick={rename}>Rename</button>}
        {current && (
          <select className="folder-move" value="" aria-label="Move this folder" title="Move this folder"
            onChange={e => run(() => moveFolder(current.id, e.target.value === '-' ? null : e.target.value))}>
            <option value="" disabled>Move to…</option>
            {current.parentId && <option value="-">Top level</option>}
            {!folders.some(f => f.parentId === current.id) && folders
              .filter(f => !f.parentId && f.id !== current.id && f.id !== current.parentId)
              .map(f => <option key={f.id} value={f.id}>{f.name}</option>)}
          </select>
        )}
        {current && <button className="delete-btn" onClick={remove} title="Delete this folder (keeps its songs)">Delete folder</button>}
        <button className="btn" onClick={() => navigate('/')}>➕ Add song</button>
      </div>

      {error && <p className="error-text">{error}</p>}

      <div className="song-list">
        {subfolders.map(f => (
          <button key={f.id} className={`card song-row folder-row${dropClass(f.id)}`} onClick={() => open(f)}
            draggable={!folders.some(x => x.parentId === f.id)}
            onDragStart={e => e.dataTransfer.setData(FOLDER, f.id)} {...dropProps(f.id)}>
            <div className="cover-placeholder">📁</div>
            <span className="song-row-text">
              <span className="song-row-title">{f.name}</span>
              <span className="muted">{count(f)}</span>
            </span>
          </button>
        ))}
        {shown.map(s => <SongRow key={s.id} s={s} path={q ? folderPath(folders, s.folderId) : ''} onOpen={() => navigate(`/songs/${s.id}`)} />)}
        {songs.length === 0 && folders.length === 0 && <p className="muted">No songs yet.</p>}
        {!q && current && subfolders.length === 0 && shown.length === 0 && <p className="muted">This folder is empty: drag songs here, or pick it on a song's page.</p>}
        {q && shown.length === 0 && <p className="muted">No song matches “{query}”.</p>}
      </div>
    </div>
  )
}

function SongRow({ s, path, onOpen }: { s: SongMeta; path: string; onOpen: () => void }) {
  return (
    <button className="card song-row" onClick={onOpen} draggable onDragStart={e => e.dataTransfer.setData(SONG, s.id)}>
      {s.hasAnalysis || s.status === 'Ready' ? <img src={`/api/songs/${s.id}/cover?r=${s.analysisRevision ?? 0}`} alt="" draggable={false} /> : <div className="cover-placeholder">🎵</div>}
      <span className="song-row-text">
        <span className="song-row-title">{s.title || s.fileName || s.sourceUrl}</span>
        <span className="muted">{[s.artist, s.bpm ? `${+s.bpm.toFixed(2)} BPM` : '', path && `📁 ${path}`].filter(Boolean).join(' · ')}</span>
        <span className="muted song-row-when">{s.lastGeneratedUtc ? `generated ${when(s.lastGeneratedUtc)}` : `added ${when(s.createdUtc)}`}</span>
      </span>
      <span className={`status status-${s.status.toLowerCase()}`}>{s.status}</span>
    </button>
  )
}
