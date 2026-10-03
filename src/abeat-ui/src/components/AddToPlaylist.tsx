import { useState } from 'react'
import { addToPlaylist, createPlaylist, errorText } from '../api'
import { usePlaylists } from '../hooks/usePlaylists'

const NEW = '__new__'

/** "+ playlist" picker for one generated version; "New playlist…" asks for a name first. */
export default function AddToPlaylist({ songId, version }: { songId: string; version: string }) {
  const { playlists, refresh } = usePlaylists()
  const [note, setNote] = useState<string | null>(null)

  const add = async (target: string) => {
    try {
      let id = target
      let title = playlists.find(p => p.id === target)?.title ?? ''
      if (target === NEW) {
        const name = prompt('Name of the new playlist', 'ABeat playlist')
        if (!name) return
        const r = await createPlaylist(name)
        id = r.data.id
        title = r.data.title
      }
      await addToPlaylist(id, songId, version)
      await refresh()
      setNote(`added to ${title}`)
    } catch (e) {
      setNote(errorText(e))
    }
    window.setTimeout(() => setNote(null), 2500)
  }

  return (
    <span className="add-playlist">
      <select value="" onChange={e => add(e.target.value)} aria-label="Add this version to a playlist"
        title="Add this version to a playlist">
        <option value="" disabled>＋ playlist</option>
        {playlists.map(p => <option key={p.id} value={p.id}>{p.title} ({p.count})</option>)}
        <option value={NEW}>New playlist…</option>
      </select>
      {note && <span className="add-note">{note}</span>}
    </span>
  )
}
