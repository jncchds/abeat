import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import {
  audioUrl, compareVersions, coverUrl, deleteSong, deleteVersion, errorText, generate, getAnalysis, getConfig, getDefaults, getSettings, getSong,
  getVersion, getVersions, getVersionSettings, reanalyze, zipUrl,
  type Analysis, type Comparison, type Difficulty, type GeneratorSettings, type SongMeta, type Version,
} from '../api'
import DebugPanel from '../components/DebugPanel'
import FrontView from '../components/FrontView'
import LyricsPanel from '../components/LyricsPanel'
import ReportCards from '../components/ReportCards'
import SettingsPanel from '../components/SettingsPanel'
import Timeline from '../components/Timeline'
import ToggleField from '../components/ToggleField'
import VersionsPanel, { type Side } from '../components/VersionsPanel'
import VocalSelect from '../components/VocalSelect'
import { useDebug } from '../hooks/useDebug'
import { useSongs } from '../hooks/useSongs'
import { ISSUE_COLOR, SIDE_A, SIDE_B, unmatchedBeats, type Track } from '../utils/draw'
import { diffLabel, fmtTime } from '../utils/format'

type Mode = 'a' | 'b' | 'both'

/** "#3 · Oct 3, 12:03:10 · v0.1.1" for generations (numbered oldest first), the mapper for the human map. */
function versionLabels(versions: Version[]): Record<string, string> {
  const gens = versions.filter(v => v.kind === 'abeat').reverse()
  const out: Record<string, string> = {}
  for (const v of versions) {
    if (v.kind === 'human') { out[v.id] = v.label; continue }
    const when = new Date(v.createdUtc).toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit', second: '2-digit' })
    out[v.id] = `#${gens.indexOf(v) + 1} · ${when}${v.appVersion ? ` · ${/^\d/.test(v.appVersion) ? 'v' : ''}${v.appVersion}` : ''}`
  }
  return out
}

function resolveSides(list: Version[], p: { a?: Side; b?: Side | null }): { a: Side; b: Side | null } | null {
  if (!list.length) return null
  const find = (v?: string) => list.find(x => x.id === v)
  const gens = list.filter(v => v.kind === 'abeat')
  const av = find(p.a?.v) ?? gens[0] ?? list[0]
  const a = { v: av.id, d: pickDiff(av, p.a?.d) }
  if (p.b === null) return { a, b: null }
  const picked = find(p.b?.v)
  if (picked) return { a, b: { v: picked.id, d: pickDiff(picked, p.b?.d) } }
  if (p.b) return { a, b: null } // picked version was deleted
  const bv = list.find(v => v.kind === 'human') ?? gens.find(v => v.id !== av.id)
  return { a, b: bv ? { v: bv.id, d: pickDiff(bv, a.d) } : null }
}

/** Same difficulty if the version has it, else its hardest. */
const pickDiff = (v: Version | undefined, want?: string) =>
  v ? (want && v.difficulties.includes(want) ? want : v.difficulties.at(-1) ?? '') : ''

export default function SongPage() {
  const { id = '' } = useParams()
  const navigate = useNavigate()
  const { songs, refresh } = useSongs()
  const [meta, setMeta] = useState<SongMeta | null>(null)
  const [log, setLog] = useState<string[]>([])
  // everything loaded for a ready song, tagged with its id so stale data is never shown
  const [loaded, setLoaded] = useState<{ id: string; analysis: Analysis; settings: GeneratorSettings } | null>(null)
  const [versions, setVersions] = useState<{ id: string; list: Version[] } | null>(null)
  // difficulties per version, per song
  const [maps, setMaps] = useState<{ id: string; byVersion: Record<string, Difficulty[]> }>({ id: '', byVersion: {} })
  // what the user picked for A / B (b: undefined = default, null = hidden); validated against the list below
  const [picked, setPicked] = useState<{ id: string; a?: Side; b?: Side | null }>({ id: '' })
  const [mode, setMode] = useState<Mode>('both')
  const [comparison, setComparison] = useState<{ key: string; c: Comparison } | null>(null)
  const [defaults, setDefaults] = useState<GeneratorSettings>()
  const [autoRegen, setAutoRegen] = useState(true)
  const [generating, setGenerating] = useState(false)
  const [error, setError] = useState<string | null>(null)
  // vocal onset method for the next re-analysis (defaults to the song's current one)
  const [vocalPick, setVocalPick] = useState<{ id: string; v: string } | null>(null)
  const [follow, setFollow] = useState(true)
  const [audio, setAudio] = useState<HTMLAudioElement | null>(null)
  const debug = useDebug()
  // debug: play a single stem in the player instead of the mix (same timing as song.egg)
  const [stemSrc, setStemSrc] = useState<{ id: string; url: string } | null>(null)

  const status = songs.find(s => s.id === id)?.status ?? meta?.status
  const ready = status === 'Ready'

  useEffect(() => { getDefaults().then(r => setDefaults(r.data.settings)) }, [])
  const [httpsPort, setHttpsPort] = useState<number | null>(null)
  useEffect(() => { getConfig().then(r => setHttpsPort(r.data.httpsPort)).catch(() => {}) }, [])
  // ArcViewer is an https page: it can fetch the zip directly (noProxy) from https or from localhost,
  // never from a plain-http LAN address, and its CORS proxy cannot reach the LAN at all
  const zipOrigin = location.protocol === 'https:' || location.hostname === 'localhost' || !httpsPort
    ? location.origin : `https://${location.hostname}:${httpsPort}`

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

  const refreshVersions = useCallback(async () => {
    const r = await getVersions(id)
    setVersions({ id, list: r.data })
    return r.data
  }, [id])

  useEffect(() => {
    if (!ready) return
    Promise.all([getAnalysis(id), getSettings(id), getVersions(id)])
      .then(([a, s, v]) => {
        setLoaded({ id, analysis: a.data, settings: s.data })
        setVersions({ id, list: v.data })
      })
      .catch(e => setError(errorText(e)))
  }, [id, ready])

  const data = ready && loaded?.id === id ? loaded : null
  const analysis = data?.analysis ?? null
  const settings = data?.settings ?? null
  const list = useMemo(() => (versions?.id === id ? versions.list : []), [versions, id])
  const labels = useMemo(() => versionLabels(list), [list])
  const byVersion = useMemo(() => (maps.id === id ? maps.byVersion : {}), [maps, id])

  // A defaults to the newest generation, B to the human map (or the previous generation); picks of
  // deleted versions fall back
  const sides = useMemo(() => resolveSides(list, picked.id === id ? picked : {}), [list, picked, id])
  const sideA = sides?.a ?? null
  const sideB = sides?.b ?? null

  // load the difficulties of the shown versions
  useEffect(() => {
    for (const v of [sideA?.v, sideB?.v]) {
      if (!v || byVersion[v]) continue
      getVersion(id, v)
        .then(r => setMaps(m => ({ id, byVersion: { ...(m.id === id ? m.byVersion : {}), [v]: r.data.difficulties } })))
        .catch(e => setError(errorText(e)))
    }
  }, [id, sideA?.v, sideB?.v, byVersion])

  const diffA = sideA ? byVersion[sideA.v]?.find(d => d.name === sideA.d) : undefined
  const diffB = sideB ? byVersion[sideB.v]?.find(d => d.name === sideB.d) : undefined

  const compareKey = sideA && sideB ? `${id}|${sideA.v}|${sideA.d}|${sideB.v}|${sideB.d}` : ''
  useEffect(() => {
    if (!compareKey) return
    const [, av, ad, bv, bd] = compareKey.split('|')
    compareVersions(id, av, ad, bv, bd)
      .then(r => setComparison({ key: compareKey, c: r.data }))
      .catch(() => {})
  }, [id, compareKey])
  const cmp = comparison?.key === compareKey ? comparison.c : null

  const showB = mode !== 'a' && !!diffB
  const showA = mode !== 'b' || !diffB
  const tracks = useMemo<Track[]>(() => {
    if (!analysis) return []
    const tol = (0.05 * analysis.tempo.bpm) / 60
    const out: Track[] = []
    if (showA && diffA) out.push({ d: diffA, color: SIDE_A, unmatched: showB && diffB ? unmatchedBeats(diffA, diffB, tol) : undefined })
    if (showB && diffB) out.push({ d: diffB, color: SIDE_B, unmatched: showA && diffA ? unmatchedBeats(diffB, diffA, tol) : undefined })
    return out
  }, [analysis, diffA, diffB, showA, showB])
  const sideLabel = (s: Side, k: 'A' | 'B') => `${k} · ${labels[s.v] ?? ''} · ${diffLabel(s.d)}`
  const trackLabels = [showA && diffA && sideA ? sideLabel(sideA, 'A') : null, showB && sideB ? sideLabel(sideB, 'B') : null]
    .filter((x): x is string => !!x)

  const pending = useRef<{ s: GeneratorSettings; draft: boolean } | null>(null)
  const runGenerate = useCallback(async (s: GeneratorSettings, draft: boolean) => {
    if (generating) { pending.current = { s, draft }; return }
    setGenerating(true)
    setError(null)
    try {
      const r = await generate(id, s, draft)
      const v = r.data.version
      setMaps(m => ({ id, byVersion: { ...(m.id === id ? m.byVersion : {}), [v.id]: r.data.difficulties } }))
      const fresh = await refreshVersions()
      const a = { v: v.id, d: pickDiff(v, sideA?.d) }
      let b = sideB
      // the old A becomes B when nothing is compared yet (unless it was the draft just replaced)
      if (!b && sideA && sideA.v !== v.id && fresh.some(x => x.id === sideA.v)) b = sideA
      setPicked({ id, a, b })
    } catch (e) {
      setError(errorText(e))
    } finally {
      setGenerating(false)
    }
  }, [id, generating, refreshVersions, sideA, sideB])

  useEffect(() => {
    if (!generating && pending.current) {
      const p = pending.current
      pending.current = null
      runGenerate(p.s, p.draft)
    }
  }, [generating, runGenerate])

  const onSettings = (s: GeneratorSettings) => {
    setLoaded(l => (l && l.id === id ? { ...l, settings: s } : l))
    if (autoRegen) runGenerate(s, true)
  }

  const setSide = (side: 'a' | 'b', next: Partial<Side> | null) => {
    if (!sideA) return
    if (side === 'b' && next === null) { setPicked({ id, a: sideA, b: null }); return }
    const old = side === 'a' ? sideA : sideB
    const v = list.find(x => x.id === (next?.v ?? old?.v))
    if (!v) return
    const s = { v: v.id, d: pickDiff(v, next?.d ?? old?.d ?? sideA.d) }
    setPicked(side === 'a' ? { id, a: s, b: sideB } : { id, a: sideA, b: s })
  }

  // clicking B on the version already shown as B hides it
  const onPick = (side: 'a' | 'b', v: string) => setSide(side, side === 'b' && sideB?.v === v ? null : { v })

  const onAddB = () => {
    const v = list.find(x => x.id !== sideA?.v) ?? list[0]
    if (v) setSide('b', { v: v.id })
  }

  const onDeleteVersion = async (v: string) => {
    if (!confirm(`Delete version ${labels[v] ?? v}?`)) return
    await deleteVersion(id, v).catch(e => setError(errorText(e)))
    await refreshVersions()
  }

  const onPrune = async () => {
    const doomed = list.filter(v => v.kind === 'abeat' && v.id !== sideA?.v && v.id !== sideB?.v)
    if (!doomed.length || !confirm(`Delete ${doomed.length} version${doomed.length === 1 ? '' : 's'} (all except A and B)?`)) return
    for (const v of doomed) await deleteVersion(id, v.id).catch(e => setError(errorText(e)))
    await refreshVersions()
  }

  const onLoadSettings = async (v: string) => {
    try {
      const r = await getVersionSettings(id, v)
      setLoaded(l => (l && l.id === id ? { ...l, settings: r.data } : l))
    } catch (e) {
      setError(errorText(e))
    }
  }

  const onDelete = async () => {
    if (!confirm('Delete this song and all its versions?')) return
    await deleteSong(id)
    await refresh()
    navigate('/')
  }

  const vocals = vocalPick?.id === id ? vocalPick.v : meta?.analysis.vocalOnsets ?? 'flux'
  const onReanalyze = async () => {
    if (!meta) return
    // sung notes and lyric syllables work on the separated vocals, so they switch stems on
    await reanalyze(id, { ...meta.analysis, vocalOnsets: vocals, stems: meta.analysis.stems || vocals !== 'flux' })
    await refresh()
  }

  const current = showA ? diffA : diffB
  const versionA = list.find(v => v.id === sideA?.v)
  const versionB = list.find(v => v.id === sideB?.v)
  // download / ArcViewer: the generation shown as A (the newest one when A is the human map)
  const zipVersion = versionA?.kind === 'abeat' ? versionA.id : undefined
  const title = meta?.title || meta?.fileName || meta?.sourceUrl || '…'

  return (
    <div className="page">
      <div className="song-header">
        {ready ? <img className="cover" src={`${coverUrl(id)}?v=${meta?.bpm ?? ''}`} alt="" /> : <div className="cover cover-placeholder">🎵</div>}
        <div className="song-title">
          <h2>{title}</h2>
          <div className="muted">
            {meta?.artist}
            {meta?.referenceMapper && (
              <span className="ref-badge">
                human map by {meta.referenceUrl
                  ? <a href={meta.referenceUrl} target="_blank" rel="noopener noreferrer">{meta.referenceMapper}</a>
                  : meta.referenceMapper}
              </span>
            )}
          </div>
          {analysis && <Facts a={analysis} />}
        </div>
        <div className="song-actions">
          {ready && <a className="btn" href={zipUrl(id, zipVersion)} title="Downloads version A">⬇ Download map</a>}
          {ready && (
            <a className="btn btn-secondary" target="_blank" rel="noopener noreferrer"
              title={zipOrigin.startsWith('https:') && location.protocol !== 'https:'
                ? `First time on this device: open ${zipOrigin} once and accept the certificate warning`
                : 'Opens version A in ArcViewer'}
              href={`https://allpoland.github.io/ArcViewer/?url=${encodeURIComponent(zipOrigin + zipUrl(id, zipVersion))}&noProxy=true`}>
              ArcViewer
            </a>
          )}
          {meta && <VocalSelect compact value={vocals} onChange={v => setVocalPick({ id, v })} />}
          <button className="btn-secondary" onClick={onReanalyze} disabled={!ready && status !== 'Failed'}
            title="Runs the analysis again (separated stems are reused) and adds a new version">Re-analyze</button>
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

      {ready && analysis && settings && sideA && (
        <>
          <audio ref={setAudio} src={stemSrc?.id === id ? stemSrc.url : audioUrl(id)} preload="auto" />
          {debug && <DebugPanel id={id} onPreview={url => setStemSrc(url ? { id, url } : null)} />}
          <Player audio={audio} bpm={analysis.tempo.bpm} follow={follow} setFollow={setFollow}>
            {sideB && (
              <div className="view-switch" title="Which version the timeline and player view show">
                {(['a', 'b', 'both'] as const).map(m => (
                  <button key={m} className={mode === m ? 'active' : ''} onClick={() => setMode(m)}>
                    {m === 'a' ? 'A' : m === 'b' ? 'B' : 'A + B'}
                  </button>
                ))}
              </div>
            )}
          </Player>

          <div className="compare-bar">
            <SidePicker name="A" color={SIDE_A} side={sideA} versions={list} labels={labels} onChange={n => setSide('a', n)} />
            {sideB
              ? <SidePicker name="B" color={SIDE_B} side={sideB} versions={list} labels={labels} onChange={n => setSide('b', n)}
                  onClear={() => setSide('b', null)} />
              : <button className="btn-secondary add-b" style={{ borderColor: SIDE_B, color: SIDE_B }} onClick={onAddB}>+ Compare with B</button>}
            {cmp && (
              <div className="compare-line compare-ab"
                title="A against B: note timing F1 at ±50 ms (B as reference), median offset, direction / position distribution distance (0 = same)">
                <span>A vs B</span>
                <b>F1 {cmp.f1.toFixed(2)}</b>
                <span>P {cmp.precision.toFixed(2)} · R {cmp.recall.toFixed(2)}</span>
                <span>offset {cmp.offsetMs.toFixed(0)} ms</span>
                <span>nps {cmp.aNps.toFixed(1)} / {cmp.bNps.toFixed(1)}</span>
                <span>dirΔ {cmp.directionDistance.toFixed(2)}</span>
                <span>posΔ {cmp.positionDistance.toFixed(2)}</span>
              </div>
            )}
          </div>

          <div className="views">
            <Timeline analysis={analysis} tracks={tracks} labels={trackLabels} audio={audio} follow={follow} />
            <div className="front-stack">
              {tracks.length > 1
                ? tracks.map((t, k) => (
                  <FrontView key={k} difficulty={t.d} bpm={analysis.tempo.bpm} audio={audio} color={t.color} label={k === 0 ? 'A' : 'B'} />
                ))
                : <FrontView difficulty={current} bpm={analysis.tempo.bpm} audio={audio} />}
            </div>
          </div>

          <div className="bottom-grid">
            <div>
              <VersionsPanel versions={list} labels={labels} a={sideA} b={sideB} onPick={onPick}
                onDelete={onDeleteVersion} onPrune={onPrune} onLoadSettings={onLoadSettings} />
              {byVersion[sideA.v] && (
                <ReportCards difficulties={byVersion[sideA.v]} selected={sideA.d} onSelect={d => setSide('a', { d })}
                  vsHuman={versionA?.kind === 'abeat' ? versionA.vsHuman : null} title={`A · ${labels[sideA.v] ?? ''}`} color={SIDE_A} />
              )}
              {sideB && byVersion[sideB.v] && (
                <ReportCards difficulties={byVersion[sideB.v]} selected={sideB.d} onSelect={d => setSide('b', { d })}
                  vsHuman={versionB?.kind === 'abeat' ? versionB.vsHuman : null} title={`B · ${labels[sideB.v] ?? ''}`} color={SIDE_B} />
              )}
              <LyricsPanel id={id} analysis={analysis} usesLyrics={vocals === 'lyrics'} />
              <div className="card issues-card">
                <h3>
                  Flow issues {current && <span className="muted">{showA ? 'A' : 'B'}</span>}{' '}
                  <span className="muted">{current?.report.issues.length ? `(${current.report.issues.length})` : '— none'}</span>
                </h3>
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
                <button onClick={() => runGenerate(settings, false)} disabled={generating}
                  title="Adds a new version (auto-regenerate only updates a draft)">
                  {generating ? 'Generating…' : 'Generate new version'}
                </button>
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

interface SidePickerProps {
  name: 'A' | 'B'
  color: string
  side: Side
  versions: Version[]
  labels: Record<string, string>
  onChange: (next: Partial<Side>) => void
  onClear?: () => void
}

/** Version + difficulty selector for one side of the comparison. */
function SidePicker({ name, color, side, versions, labels, onChange, onClear }: SidePickerProps) {
  const v = versions.find(x => x.id === side.v)
  return (
    <div className="side-picker" style={{ borderColor: color }}>
      <span className="side-name" style={{ background: color }}>{name}</span>
      <select value={side.v} onChange={e => onChange({ v: e.target.value })} aria-label={`Version ${name}`}>
        {versions.map(x => <option key={x.id} value={x.id}>{labels[x.id]}{x.kind === 'abeat' && x.draft ? ' · draft' : ''}</option>)}
      </select>
      <div className="diff-tabs">
        {v?.difficulties.map(d => (
          <button key={d} className={d === side.d ? 'active' : ''} style={d === side.d ? { color, borderColor: color } : undefined}
            onClick={() => onChange({ d })}>{diffLabel(d)}</button>
        ))}
      </div>
      {onClear && <button className="icon-btn" title="Stop comparing" onClick={onClear}>✕</button>}
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
      {a.vocalSource && <span>vocals: <b>{a.vocalSource}</b>{a.lyrics ? ` (${a.lyrics.words.length} words${a.lyrics.language ? `, ${a.lyrics.language}` : ''})` : ''}</span>}
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
