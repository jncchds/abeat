import { createContext, useCallback, useContext, useEffect, useRef, useState } from 'react'
import { getSongs, type SongMeta } from '../api'

interface SongsState {
  songs: SongMeta[]
  refresh: () => Promise<void>
}

const SongsContext = createContext<SongsState>({ songs: [], refresh: async () => {} })

const BUSY = new Set(['Queued', 'Analyzing', 'Generating'])

/** Song list shared by the sidebar and pages; polls quickly while anything is being processed. */
export function SongsProvider({ children }: { children: React.ReactNode }) {
  const [songs, setSongs] = useState<SongMeta[]>([])
  const busy = useRef(false)

  const refresh = useCallback(async () => {
    const r = await getSongs()
    busy.current = r.data.some(s => BUSY.has(s.status))
    setSongs(r.data)
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

  return <SongsContext.Provider value={{ songs, refresh }}>{children}</SongsContext.Provider>
}

// eslint-disable-next-line react-refresh/only-export-components
export const useSongs = () => useContext(SongsContext)

/** Songs by their latest generated version, newest first; songs never generated count from when they were added. */
// eslint-disable-next-line react-refresh/only-export-components
export const byActivity = (songs: SongMeta[]) => {
  const at = (s: SongMeta) => Date.parse(s.lastGeneratedUtc ?? s.createdUtc) || 0
  return [...songs].sort((a, b) => at(b) - at(a))
}
