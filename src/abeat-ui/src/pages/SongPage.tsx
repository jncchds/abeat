import { useCallback, useEffect, useRef, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import {
  audioUrl, coverUrl, deleteSong, errorText, generate, getAnalysis, getDefaults, getMap, getSettings, getSong, reanalyze, zipUrl,
  type Analysis, type GeneratorSettings, type MapData, type SongMeta,
} from '../api'
import FrontView from '../components/FrontView'
import ReportCards from '../components/ReportCards'
import SettingsPanel from '../components/SettingsPanel'
import Timeline from '../components/Timeline'
import ToggleField from '../components/ToggleField'
import { useSongs } from '../hooks/useSongs'
import { ISSUE_COLOR } from '../utils/draw'
import { diffLabel, fmtTime } from '../utils/format'

export default function SongPage() {
  const { id = '' } = useParams()
  const navigate = useNavigate()
  const { songs, refresh } = useSongs()
  const [meta, setMeta] = useState<SongMeta | null>(null)
  const [log, setLog] = useState<string[]>([])
  // everything loaded for a ready song, tagged with its id so stale data is never shown
  const [loaded, setLoaded] = useState<{ id: string; analysis: Analysis; map: MapData; settings: GeneratorSettings } | null>(null)
  const [defaults, setDefaults] = useState<GeneratorSettings>()
  const [diff, setDiff] = useState<string>()
  const [autoRegen, setAutoRegen] = useState(true)
  const [generating, setGenerating] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [follow, setFollow] = useState(true)
  const [audio, setAudio] = useState<HTMLAudioElement | null>(null)

  const status = songs.find(s => s.id === id)?.status ?? meta?.status
  const ready = status === 'Ready'

  useEffect(() => { getDefaults().then(r => setDefaults(r.data.settings)) }, [])

  // song meta + log (polls while processing via the songs context changing status)
  useEffect(() => {
    let cancelled = false
    const load = async () => {
      const r = await getSong(id)
      if (cancelled) return
      setMeta(r.data.meta)
      setLog(r.data.log)
    }
    load().catch(e => setError(errorText(e)))
    if (ready) return () => { cancelled = true }
    const t = window.setInterval(() => load().catch(() => {}), 1500)
    return () => { cancelled = true; window.clearInterval(t) }
  }, [id, ready])

  useEffect(() => {
    if (!ready) return
    Promise.all([getAnalysis(id), getMap(id), getSettings(id)])
      .then(([a, m, s]) => {
        setLoaded({ id, analysis: a.data, map: m.data, settings: s.data })
        setDiff(d => (m.data.difficulties.some(x => x.name === d) ? d : m.data.difficulties.at(-1)?.name))
      })
      .catch(e => setError(errorText(e)))
  }, [id, ready])

  const data = ready && loaded?.id === id ? loaded : null
  const analysis = data?.analysis ?? null
  const map = data?.map ?? null
  const settings = data?.settings ?? null

  const pending = useRef<GeneratorSettings | null>(null)
  const runGenerate = useCallback(async (s: GeneratorSettings) => {
    if (generating) { pending.current = s; return }
    setGenerating(true)
    setError(null)
    try {
      const r = await generate(id, s)
      setLoaded(l => (l && l.id === id ? { ...l, map: r.data } : l))
      setDiff(d => (r.data.difficulties.some(x => x.name === d) ? d : r.data.difficulties.at(-1)?.name))
    } catch (e) {
      setError(errorText(e))
    } finally {
      setGenerating(false)
    }
  }, [id, generating])

  useEffect(() => {
    if (!generating && pending.current) {
      const s = pending.current
      pending.current = null
      runGenerate(s)
    }
  }, [generating, runGenerate])

  const onSettings = (s: GeneratorSettings) => {
    setLoaded(l => (l && l.id === id ? { ...l, settings: s } : l))
    if (autoRegen) runGenerate(s)
  }

  const onDelete = async () => {
    if (!confirm('Delete this song and its map?')) return
    await deleteSong(id)
    await refresh()
    navigate('/')
  }

  const onReanalyze = async () => {
    if (!meta) return
    await reanalyze(id, meta.analysis)
    await refresh()
  }

  const current = map?.difficulties.find(d => d.name === diff)
  const title = meta?.title || meta?.fileName || meta?.sourceUrl || '…'

  return (
    <div className="page">
      <div className="song-header">
        {ready ? <img className="cover" src={`${coverUrl(id)}?v=${meta?.bpm ?? ''}`} alt="" /> : <div className="cover cover-placeholder">🎵</div>}
        <div className="song-title">
          <h2>{title}</h2>
          <div className="muted">{meta?.artist}</div>
          {analysis && <Facts a={analysis} />}
        </div>
        <div className="song-actions">
          {ready && <a className="btn" href={zipUrl(id)}>⬇ Download map</a>}
          {ready && (
            <a className="btn btn-secondary" target="_blank" rel="noopener noreferrer"
              title="Works when this server is reachable from your browser; otherwise download the zip and drop it into ArcViewer."
              href={`https://allpoland.github.io/ArcViewer/?url=${encodeURIComponent(location.origin + zipUrl(id))}`}>
              ArcViewer
            </a>
          )}
          <button className="btn-secondary" onClick={onReanalyze} disabled={!ready && status !== 'Failed'}>Re-analyze</button>
          <button className="delete-btn" onClick={onDelete}>Delete</button>
        </div>
      </div>

      {error && <p className="error-text">{error}</p>}

      {!ready && (
        <div className="card progress-card">
          <div className="progress-status">
            {status === 'Failed' ? `Failed: ${meta?.error ?? ''}` : <><span className="spinner" /> {status ?? 'Loading'}…</>}
          </div>
          <pre className="log">{log.join('\n')}</pre>
        </div>
      )}

      {ready && analysis && map && settings && (
        <>
          <audio ref={setAudio} src={audioUrl(id)} preload="auto" />
          <Player audio={audio} bpm={analysis.tempo.bpm} follow={follow} setFollow={setFollow}>
            <div className="diff-tabs">
              {map.difficulties.map(d => (
                <button key={d.name} className={d.name === diff ? 'active' : ''} onClick={() => setDiff(d.name)}>{diffLabel(d.name)}</button>
              ))}
            </div>
          </Player>

          <div className="views">
            <Timeline analysis={analysis} difficulty={current} audio={audio} follow={follow} />
            <FrontView difficulty={current} bpm={analysis.tempo.bpm} audio={audio} />
          </div>

          <div className="bottom-grid">
            <div>
              <ReportCards difficulties={map.difficulties} selected={diff} onSelect={setDiff} />
              <div className="card issues-card">
                <h3>Flow issues <span className="muted">{current?.report.issues.length ? `(${current.report.issues.length})` : '— none'}</span></h3>
                <ul className="issue-list">
                  {current?.report.issues.slice(0, 300).map((i, k) => (
                    <li key={k} style={{ borderLeftColor: ISSUE_COLOR[i.kind] }}
                      onClick={() => seek(audio, (i.b * 60) / analysis.tempo.bpm - 1)}>
                      {fmtTime((i.b * 60) / analysis.tempo.bpm)} {i.hand === 0 ? 'L' : 'R'} {i.kind}
                    </li>
                  ))}
                </ul>
              </div>
            </div>
            <div className="card settings-card">
              <div className="settings-head">
                <h3>Generator settings</h3>
                <ToggleField label="Auto" checked={autoRegen} onChange={setAutoRegen} />
                <button onClick={() => runGenerate(settings)} disabled={generating}>{generating ? 'Generating…' : 'Generate'}</button>
                <button className="btn-secondary" disabled={!defaults} onClick={() => defaults && onSettings(structuredClone(defaults))}>Defaults</button>
              </div>
              <SettingsPanel settings={settings} defaults={defaults} layers={Object.keys(analysis.layers)}
                layerSource={analysis.layerSource} onChange={onSettings} />
            </div>
          </div>
        </>
      )}
    </div>
  )
}

// media element mutations live outside components (React treats props/state as immutable)
function seek(audio: HTMLAudioElement | null, t: number) {
  if (audio) audio.currentTime = Math.max(0, t)
}

function setRate(audio: HTMLAudioElement | null, rate: number) {
  if (audio) audio.playbackRate = rate
}

function Facts({ a }: { a: Analysis }) {
  const t = a.tempo
  return (
    <div className="facts">
      <span><b>{+t.bpm.toFixed(2)}</b> BPM</span>
      <span><b>{fmtTime(a.audio.durationSec)}</b></span>
      <span>beats: <b>{t.backend}</b></span>
      <span>onsets: <b>{a.layerSource}</b></span>
      <span>{a.sections.length} sections</span>
      {a.source.url && <a href={a.source.url} target="_blank" rel="noopener noreferrer">source ↗</a>}
      {!t.stable && <span className="warn" title="Detected beats deviate from a constant tempo; notes may drift">⚠ tempo varies ({t.maxDevMs.toFixed(0)} ms)</span>}
    </div>
  )
}

interface PlayerProps {
  audio: HTMLAudioElement | null
  bpm: number
  follow: boolean
  setFollow: (v: boolean) => void
  children: React.ReactNode
}

function Player({ audio, bpm, follow, setFollow, children }: PlayerProps) {
  const [playing, setPlaying] = useState(false)
  const timeRef = useRef<HTMLSpanElement>(null)

  useEffect(() => {
    if (!audio) return
    const onPlay = () => setPlaying(true)
    const onPause = () => setPlaying(false)
    audio.addEventListener('play', onPlay)
    audio.addEventListener('pause', onPause)
    let raf = 0
    const frame = () => {
      if (timeRef.current) timeRef.current.textContent = `${fmtTime(audio.currentTime)}  ·  beat ${((audio.currentTime * bpm) / 60).toFixed(2)}`
      raf = requestAnimationFrame(frame)
    }
    raf = requestAnimationFrame(frame)
    const key = (e: KeyboardEvent) => {
      const tag = (document.activeElement as HTMLElement | null)?.tagName
      if (e.code === 'Space' && !['INPUT', 'SELECT', 'TEXTAREA', 'BUTTON'].includes(tag ?? '')) {
        e.preventDefault()
        if (audio.paused) audio.play()
        else audio.pause()
      }
    }
    document.addEventListener('keydown', key)
    return () => {
      audio.removeEventListener('play', onPlay)
      audio.removeEventListener('pause', onPause)
      document.removeEventListener('keydown', key)
      cancelAnimationFrame(raf)
    }
  }, [audio, bpm])

  return (
    <div className="player">
      <button className="play-btn" title="Play / pause (space)" onClick={() => (audio?.paused ? audio.play() : audio?.pause())}>
        {playing ? '❚❚' : '▶'}
      </button>
      <span ref={timeRef} className="mono time" />
      <label className="inline-select">
        speed
        <select defaultValue="1" onChange={e => setRate(audio, +e.target.value)}>
          <option>0.5</option><option>0.75</option><option>1</option>
        </select>
      </label>
      <label className="inline-check"><input type="checkbox" checked={follow} onChange={e => setFollow(e.target.checked)} /> follow</label>
      <span className="spacer" />
      {children}
    </div>
  )
}
