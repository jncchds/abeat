import { createContext, useCallback, useContext, useEffect, useRef, useState } from 'react'
import { getFolders, getSongs, type Folder, type SongMeta } from '../api'

interface SongsState {
  songs: SongMeta[]
  folders: Folder[]
  refresh: () => Promise<void>
}

const SongsContext = createContext<SongsState>({ songs: [], folders: [], refresh: async () => {} })

const BUSY = new Set(['Queued', 'Analyzing', 'Generating'])

/** Song list (and its folders) shared by the sidebar and pages; polls quickly while anything is being processed. */
export function SongsProvider({ children }: { children: React.ReactNode }) {
  const [songs, setSongs] = useState<SongMeta[]>([])
  const [folders, setFolders] = useState<Folder[]>([])
  const busy = useRef(false)

  const refresh = useCallback(async () => {
    const [r, f] = await Promise.all([getSongs(), getFolders()])
    busy.current = r.data.some(s => BUSY.has(s.status))
    setSongs(r.data)
    setFolders(f.data)
  }, [])

  useEffect(() => {
    let timer = 0
    let stopped = false
    const tick = async () => {
      try { await refresh() } catch { /* server restarting; retry */ }
      if (!stopped) timer = window.setTimeout(tick, busy.current ? 1500 : 10000)
    }
    tick()
    return () => { stopped = true; window.clearTimeout(timer) }
  }, [refresh])

  return <SongsContext.Provider value={{ songs, folders, refresh }}>{children}</SongsContext.Provider>
}

// eslint-disable-next-line react-refresh/only-export-components
export const useSongs = () => useContext(SongsContext)

/** Songs by their latest generated version, newest first; songs never generated count from when they were added. */
// eslint-disable-next-line react-refresh/only-export-components
export const byActivity = (songs: SongMeta[]) => {
  const at = (s: SongMeta) => Date.parse(s.lastGeneratedUtc ?? s.createdUtc) || 0
  return [...songs].sort((a, b) => at(b) - at(a))
}

/** Folders as select options: each top-level folder followed by its sub-folders, indented. */
// eslint-disable-next-line react-refresh/only-export-components
export const folderOptions = (folders: Folder[]) =>
  folders.filter(f => !f.parentId || !folders.some(p => p.id === f.parentId)).flatMap(top => [
    { id: top.id, label: top.name, depth: 1 },
    ...folders.filter(f => f.parentId === top.id).map(f => ({ id: f.id, label: `\u00a0\u00a0\u00a0${f.name}`, depth: 2 })),
  ])

/** "Parent / Folder" for a folder id, "" for none. */
// eslint-disable-next-line react-refresh/only-export-components
export const folderPath = (folders: Folder[], id?: string | null) => {
  const f = folders.find(x => x.id === id)
  if (!f) return ''
  const parent = folders.find(x => x.id === f.parentId)
  return parent ? `${parent.name} / ${f.name}` : f.name
}
