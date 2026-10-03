import { createContext, useCallback, useContext, useEffect, useState } from 'react'
import { getPlaylists, type PlaylistSummary } from '../api'

interface PlaylistsState {
  playlists: PlaylistSummary[]
  refresh: () => Promise<void>
}

const PlaylistsContext = createContext<PlaylistsState>({ playlists: [], refresh: async () => {} })

/** Playlist list shared by the sidebar, the song page and the playlist page. */
export function PlaylistsProvider({ children }: { children: React.ReactNode }) {
  const [playlists, setPlaylists] = useState<PlaylistSummary[]>([])
  const refresh = useCallback(async () => {
    const r = await getPlaylists()
    setPlaylists(r.data)
  }, [])
  useEffect(() => { getPlaylists().then(r => setPlaylists(r.data)).catch(() => {}) }, [])
  return <PlaylistsContext.Provider value={{ playlists, refresh }}>{children}</PlaylistsContext.Provider>
}

// eslint-disable-next-line react-refresh/only-export-components
export const usePlaylists = () => useContext(PlaylistsContext)
