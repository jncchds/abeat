import { BrowserRouter, Outlet, Route, Routes, useLocation, useNavigate } from 'react-router-dom'
import Sidebar, { SidebarBtn, SidebarDivider, SidebarSection } from './components/Sidebar'
import { SongsProvider, useSongs } from './hooks/useSongs'
import HomePage from './pages/HomePage'
import SongPage from './pages/SongPage'

const STATUS_DOT: Record<string, string> = {
  Ready: 'var(--success)', Failed: 'var(--danger)', Queued: 'var(--warning)', Analyzing: 'var(--warning)', Generating: 'var(--warning)',
}

function Layout() {
  const navigate = useNavigate()
  const location = useLocation()
  const { songs } = useSongs()

  return (
    <div className="app-layout">
      <Sidebar bottomChildren={<div className="sidebar-credit hide-when-collapsed">ABeat by CHDS · built with Claude</div>}>
        <SidebarBtn icon="➕" label="Add song" active={location.pathname === '/'} onClick={() => navigate('/')} />
        <SidebarDivider />
        <SidebarSection title="Songs" />
        <div className="sidebar-book-list">
          {songs.map(s => (
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
      </Sidebar>
      <main className="app-main">
        <Outlet />
      </main>
    </div>
  )
}

export default function App() {
  return (
    <SongsProvider>
      <BrowserRouter>
        <Routes>
          <Route element={<Layout />}>
            <Route path="/" element={<HomePage />} />
            <Route path="/songs/:id" element={<SongPage />} />
          </Route>
        </Routes>
      </BrowserRouter>
    </SongsProvider>
  )
}
