import { createPlaylist } from './api'
import { BrowserRouter, Outlet, Route, Routes, useLocation, useNavigate } from 'react-router-dom'
import Sidebar, { SidebarBtn, SidebarDivider, SidebarSection } from './components/Sidebar'
import { PlaylistsProvider, usePlaylists } from './hooks/usePlaylists'
import { byActivity, SongsProvider, useSongs } from './hooks/useSongs'
import HomePage from './pages/HomePage'
import PlaylistPage from './pages/PlaylistPage'
import RuntimeBanner from './components/RuntimeBanner'
import SongPage from './pages/SongPage'
import SongsPage from './pages/SongsPage'

const SIDEBAR_SONGS = 10

const STATUS_DOT: Record<string, string> = {
  Ready: 'var(--success)', Failed: 'var(--danger)', Queued: 'var(--warning)', Analyzing: 'var(--warning)', Generating: 'var(--warning)',
}

function Layout() {
  const navigate = useNavigate()
  const location = useLocation()
  const { songs } = useSongs()
  const { playlists, refresh: refreshPlaylists } = usePlaylists()

  const newPlaylist = async () => {
    const name = prompt('Name of the new playlist', 'ABeat playlist')
    if (!name) return
    const r = await createPlaylist(name)
    await refreshPlaylists()
    navigate(`/playlists/${r.data.id}`)
  }

  return (
    <div className="app-layout">
      <Sidebar bottomChildren={<div className="sidebar-credit hide-when-collapsed">
        ABeat by CHDS · built with Claude<br />
        <span className="arcviewer-credit">includes <a href="https://github.com/AllPoland/ArcViewer" target="_blank" rel="noopener noreferrer">ArcViewer</a> (GPL-3.0)</span>
      </div>}>
        <SidebarBtn icon="🎵" label="Songs" active={location.pathname === '/songs'} onClick={() => navigate('/songs')} />
        <SidebarBtn icon="➕" label="Add song" active={location.pathname === '/'} onClick={() => navigate('/')} />
        <SidebarDivider />
        <SidebarSection title="Playlists" />
        <div className="sidebar-book-list">
          {playlists.map(p => (
            <button key={p.id} className={`sidebar-book-entry${location.pathname === `/playlists/${p.id}` ? ' active' : ''}`}
              onClick={() => navigate(`/playlists/${p.id}`)} title={p.title}>
              <span className="s-be-icon">📃</span>
              <span className="s-be-content">
                <span className="s-be-title">{p.title}</span>
                <span className="s-be-premise">{p.count} version{p.count === 1 ? '' : 's'}</span>
              </span>
            </button>
          ))}
        </div>
        <SidebarBtn icon="📃" label="New playlist" onClick={newPlaylist} />
        <SidebarDivider />
        <SidebarSection title="Recent songs" />
        <div className="sidebar-book-list">
          {byActivity(songs).slice(0, SIDEBAR_SONGS).map(s => (
            <button
              key={s.id}
              className={`sidebar-book-entry${location.pathname === `/songs/${s.id}` ? ' active' : ''}`}
              onClick={() => navigate(`/songs/${s.id}`)}
              title={s.title}
            >
              <span className="s-be-icon">🎵</span>
              <span className="s-be-content">
                <span className="s-be-title">{s.title || s.fileName || s.sourceUrl}</span>
                <span className="s-be-premise">
                  <span className="status-dot" style={{ background: STATUS_DOT[s.status] }} />
                  {[s.artist, s.bpm ? `${+s.bpm.toFixed(1)} BPM` : s.status].filter(Boolean).join(' · ')}
                </span>
              </span>
            </button>
          ))}
          {songs.length === 0 && <p className="sidebar-empty">No songs yet</p>}
        </div>
        {songs.length > SIDEBAR_SONGS && <SidebarBtn icon="…" label={`All ${songs.length} songs`} onClick={() => navigate('/songs')} />}
      </Sidebar>
      <main className="app-main">
        <RuntimeBanner />
        <Outlet />
      </main>
    </div>
  )
}

export default function App() {
  return (
    <SongsProvider>
      <PlaylistsProvider>
      <BrowserRouter>
        <Routes>
          <Route element={<Layout />}>
            <Route path="/" element={<HomePage />} />
            <Route path="/songs" element={<SongsPage />} />
            <Route path="/songs/:id" element={<SongPage />} />
            <Route path="/playlists/:id" element={<PlaylistPage />} />
          </Route>
        </Routes>
      </BrowserRouter>
      </PlaylistsProvider>
    </SongsProvider>
  )
}
