import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { cancelJob,
  audioUrl, compareVersions, coverUrl, deleteSong, deleteVersion, errorText, generate, getAnalysis, getConfig, getColors, getDefaults, getEnvironments, getSettings, getSong,
  getTaps, getVersion, getVersions, getVersionSettings, putTaps, reanalyze, zipUrl,
  type Analysis, type Comparison, type Difficulty, type DifficultyProfile, type EnvironmentEntry, type EnvironmentSuggestions, type GeneratorSettings, type SongColors, type SongMeta, type TapRun, type Version,
} from '../api'
import DebugPanel from '../components/DebugPanel'
import FrontView from '../components/FrontView'
import LyricsPanel from '../components/LyricsPanel'
import ReportCards from '../components/ReportCards'
import TapPanel from '../components/TapPanel'
import SettingsPanel from '../components/SettingsPanel'
import Timeline from '../components/Timeline'
import ToggleField from '../components/ToggleField'
import AddToPlaylist from '../components/AddToPlaylist'
import VersionsPanel, { type MapLinks, type Side } from '../components/VersionsPanel'
import AnalysisPanel, { type AnalysisChoice } from '../components/AnalysisPanel'
import { useDebug } from '../hooks/useDebug'
import { useSongs } from '../hooks/useSongs'
import { ISSUE_COLOR, SIDE_A, SIDE_B, unmatchedBeats, type Track } from '../utils/draw'
import { diffLabel, fmtTime } from '../utils/format'
import { tempoOf, type Tempo } from '../utils/tempo'
import { tapRef, tapRows, type TapRef } from '../utils/taps'

type Mode = 'a' | 'b' | 'both' | 'stack'

const MODES: [Mode, string, string][] = [
  ['a', 'A', 'Only A'],
  ['b', 'B', 'Only B'],
  ['both', 'A + B', 'Both in the same lanes: A in the top half of each lane, B in the bottom half'],
  ['stack', 'A / B', 'All of A\'s lanes above all of B\'s lanes'],
]

/** "#3 · Oct 3, 12:03:10 · v0.1.1" for generations (numbered oldest first), the mapper for the human map. */
function versionLabels(versions: Version[]): Record<string, string> {
  const gens = versions.filter(v => v.kind === 'abeat').reverse()
  const out: Record<string, string> = {}
  for (const v of versions) {
    if (v.kind === 'human') { out[v.id] = v.label; continue }
    const when = new Date(v.createdUtc).toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit', second: '2-digit' })
    out[v.id] = `#${gens.indexOf(v) + 1} · ${when}${v.appVersion ? ` · ${/^\d/.test(v.appVersion) ? 'v' : ''}${v.appVersion}` : ''}${v.environment ? ` · ${v.environment}` : ''}`
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
  // never the same version as A by default: with a single version only A is shown
  const bv = list.find(v => v.kind === 'human' && v.id !== av.id) ?? gens.find(v => v.id !== av.id)
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
  const [profiles, setProfiles] = useState<Record<string, DifficultyProfile>>()
  const [environments, setEnvironments] = useState<EnvironmentEntry[]>([])
  const [envSuggestions, setEnvSuggestions] = useState<EnvironmentSuggestions>()
  const [colors, setColors] = useState<{ id: string; c: SongColors }>()
  const [autoRegen, setAutoRegen] = useState(false)
  const [generating, setGenerating] = useState(false)
  const [error, setError] = useState<string | null>(null)
  // options for the next re-analysis (default to the song's current ones)
  const [analysisPick, setAnalysisPick] = useState<{ id: string; v: AnalysisChoice } | null>(null)
  const [analysisOpen, setAnalysisOpen] = useState(false)
  const [lyricsOpen, setLyricsOpen] = useState(false)
  const [tapOpen, setTapOpen] = useState(false)
  const [taps, setTaps] = useState<{ id: string; runs: TapRun[] }>({ id: '', runs: [] })
  const [follow, setFollow] = useState(true)
  const [audio, setAudio] = useState<HTMLAudioElement | null>(null)
  const [debug, toggleDebug] = useDebug()
  // ten quick taps on the title toggle debug mode (phones have no Ctrl+Shift+D)
  const titleTaps = useRef<number[]>([])
  const onTitleTap = () => {
    const now = performance.now()
    titleTaps.current = [...titleTaps.current.filter(t => now - t < 4000), now]
    if (titleTaps.current.length >= 10) { titleTaps.current = []; toggleDebug() }
  }
  // debug: play a single stem in the player instead of the mix (same timing as song.egg)
  const [stemSrc, setStemSrc] = useState<{ id: string; url: string } | null>(null)

  const listed = songs.find(s => s.id === id)
  const status = listed?.status ?? meta?.status
  const busy = status === 'Queued' || status === 'Analyzing' || status === 'Generating'
  // usable whenever an analysis exists, also while a re-analysis runs or after one failed
  const ready = !!(listed?.hasAnalysis ?? meta?.hasAnalysis) || status === 'Ready'
  const revision = listed?.analysisRevision ?? meta?.analysisRevision ?? 0
  const [dismissed, setDismissed] = useState<string | null>(null)

  useEffect(() => { getDefaults().then(r => { setDefaults(r.data.settings); setProfiles(r.data.profiles); setEnvironments(r.data.environments ?? []) }) }, [])
  const [config, setConfig] = useState<{ httpsPort: number | null; arcViewer: boolean }>({ httpsPort: null, arcViewer: false })
  useEffect(() => { getConfig().then(r => setConfig(r.data)).catch(() => {}) }, [])
  const httpsPort = config.httpsPort
  // ArcViewer (Unity) refuses plain-http downloads except from localhost, so from other devices both the
  // bundled viewer and the zip go through the https listener (self-signed: accept it once per device).
  // The public viewer is an https page too: it can fetch the zip directly (noProxy) from https or localhost,
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
    if (!busy) return () => { cancelled = true }
    const t = window.setInterval(() => load().catch(() => {}), 1500)
    return () => { cancelled = true; window.clearInterval(t) }
  }, [id, ready, busy])

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
  }, [id, ready, revision])

  useEffect(() => {
    getTaps(id).then(r => setTaps({ id, runs: r.data.runs ?? [] })).catch(() => {})
  }, [id])
  const data = ready && loaded?.id === id ? loaded : null
  const pad = data?.analysis.audio.padSec
  // runs moved onto the current song.egg (a re-analysis can change its padding)
  const tapRuns = useMemo(() => (taps.id === id && pad !== undefined
    ? taps.runs.map(r => ({ ...r, taps: r.taps.map(t => t + pad - (r.padSec ?? pad)), padSec: pad }))
    : []), [taps, id, pad])
  const onTaps = (runs: TapRun[]) => {
    const stamped = runs.map(r => ({ ...r, padSec: r.padSec ?? pad }))
    setTaps({ id, runs: stamped })
    putTaps(id, stamped).catch(e => setError(errorText(e)))
  }

  const analysis = data?.analysis ?? null
  const settings = data?.settings ?? null
  const seed = settings?.seed
  const hasAnalysis = analysis != null
  useEffect(() => {
    if (!hasAnalysis || seed == null) return
    let live = true
    getEnvironments(id, seed).then(r => { if (live) setEnvSuggestions(r.data) }).catch(() => {})
    return () => { live = false }
  }, [id, seed, hasAnalysis])
  useEffect(() => {
    if (!hasAnalysis) return
    let live = true
    getColors(id).then(r => { if (live) setColors({ id, c: r.data }) }).catch(() => {})
    return () => { live = false }
  }, [id, hasAnalysis])
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
  const tapRefs = useMemo<TapRef[]>(() => {
    if (!analysis) return []
    const tempo = tempoOf(analysis.tempo)
    return [diffA && tapRef('A', SIDE_A, diffA, tempo), diffB && tapRef('B', SIDE_B, diffB, tempo)]
      .filter((r): r is TapRef => !!r)
  }, [analysis, diffA, diffB])
  // rows are aligned to the top track's notes
  const tapTimeline = useMemo(() => tapRows(tapRuns, (tracks[0]?.d === diffB ? tapRefs.find(r => r.name === 'B') : tapRefs[0]) ?? null),
    [tapRuns, tapRefs, tracks, diffB])
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

  const choice: AnalysisChoice = analysisPick?.id === id ? analysisPick.v : {
    vocals: meta?.analysis.vocalOnsets ?? 'flux', tempo: meta?.analysis.tempo, pitched: meta?.analysis.pitched, separator: meta?.analysis.separator,
  }
  const vocals = choice.vocals
  const onChoice = (v: AnalysisChoice) => {
    setAnalysisPick({ id, v })
    if (v.vocals === 'lyrics' && vocals !== 'lyrics') setLyricsOpen(true)
  }
  // the lyrics box opens with the Lyrics button, and when lyric syllables are picked
  const showLyrics = ready && lyricsOpen
  const onReanalyze = async () => {
    if (!meta) return
    // sung notes and lyric syllables work on the separated vocals, so they switch stems on
    const { vocals: vocalOnsets, ...extras } = choice
    const stems = meta.analysis.stems || vocalOnsets !== 'flux' || extras.pitched === 'notes' || extras.separator === 'roformer'
    await reanalyze(id, { ...meta.analysis, ...extras, vocalOnsets, stems })
    setAnalysisOpen(false)
    await refresh()
  }

  const current = showA ? diffA : diffB
  const versionA = list.find(v => v.id === sideA?.v)
  const versionB = list.find(v => v.id === sideB?.v)
  const linksOf = (v: Version | undefined): MapLinks | undefined => {
    if (!v || (v.kind === 'human' && !v.zip)) return undefined
    const zip = zipUrl(id, v.id)
    const viewerZip = encodeURIComponent(zipOrigin + zip)
    const firstTime = zipOrigin.startsWith('https:') && location.protocol !== 'https:'
    return config.arcViewer
      ? {
        zip, viewer: `${zipOrigin}/arcviewer/?url=${viewerZip}&noProxy=true`,
        viewerTitle: firstTime ? `Opens in ArcViewer over https. First time on this device: accept the certificate warning of ${zipOrigin}` : 'Opens in the bundled ArcViewer',
      }
      : {
        zip, viewer: `https://allpoland.github.io/ArcViewer/?url=${viewerZip}&noProxy=true`,
        viewerTitle: firstTime ? `First time on this device: open ${zipOrigin} once and accept the certificate warning`
          : 'Opens in ArcViewer (allpoland.github.io; run scripts/fetch-arcviewer.sh to bundle it)',
      }
  }
  const title = meta?.title || meta?.fileName || meta?.sourceUrl || '…'

  return (
    <div className="page">
      <div className="song-header">
        {ready ? <img className="cover" src={`${coverUrl(id)}?v=${meta?.bpm ?? ''}`} alt="" /> : <div className="cover cover-placeholder">🎵</div>}
        <div className="song-title">
          <h2 onClick={onTitleTap}>{title}</h2>
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
          {meta && (
            <button className={`btn-secondary${analysisOpen ? ' active' : ''}`} onClick={() => setAnalysisOpen(o => !o)}
              title="Change how the song is analysed (tempo, vocals, separation) and run the analysis again">Re-analyze…</button>
          )}
          {busy && <button className="btn-secondary" onClick={() => cancelJob(id).then(refresh).catch(e => setError(errorText(e)))}
            title="Stops the running analysis; the current analysis and versions stay">Cancel analysis</button>}
          <button className="delete-btn" onClick={onDelete} title="Delete this song and all its versions">Delete song</button>
        </div>
      </div>

      {error && <p className="error-text">{error}</p>}

      {meta && analysisOpen && (
        <AnalysisPanel value={choice} onChange={onChoice} busy={busy} lyricsOpen={showLyrics} onLyrics={() => setLyricsOpen(o => !o)}
          onRun={onReanalyze} onClose={() => setAnalysisOpen(false)} />
      )}

      {showLyrics && analysis && <LyricsPanel id={id} analysis={analysis} usesLyrics={vocals === 'lyrics'} onClose={() => setLyricsOpen(false)} />}

      {ready && (busy || (status === 'Failed' && dismissed !== `${id}:${meta?.error}`)) && (
        <div className={`job-banner${status === 'Failed' ? ' failed' : ''}`}>
          {status === 'Failed'
            ? <span>Re-analysis {meta?.error === 'cancelled' ? 'cancelled' : `failed: ${meta?.error ?? ''}`} — the previous analysis and all versions are kept.</span>
            : <span><span className="spinner" /> {status}… {log[log.length - 1]?.replace(/^\d\d:\d\d:\d\d /, '') ?? ''}</span>}
          {status === 'Failed' && <button className="link-btn" onClick={() => setDismissed(`${id}:${meta?.error}`)}>dismiss</button>}
        </div>
      )}

      {!ready && (
        <div className="card progress-card">
          <div className="progress-status">
            {status === 'Failed' ? `Failed: ${meta?.error ?? ''}` : <><span className="spinner" /> {status ?? 'Loading'}…</>}
          </div>
          <pre className="log">{log.join('\n')}</pre>
        </div>
      )}

      {ready && analysis && settings && (
        <>
          <audio ref={setAudio} src={stemSrc?.id === id ? stemSrc.url : audioUrl(id)} preload="auto" />
          {debug && <DebugPanel id={id} onPreview={url => setStemSrc(url ? { id, url } : null)} />}
          <Player audio={audio} tempo={tempoOf(analysis.tempo)} follow={follow} setFollow={setFollow}>
            <button className={`btn-secondary${tapOpen ? ' active' : ''}`} onClick={() => setTapOpen(o => !o)}
              title="Tap along with the song twice and compare your beats with the generated notes">
              Tap beats{tapRuns.length ? ` (${tapRuns.length})` : ''}
            </button>
            {sideB && (
              <div className="view-switch" title="Which version the timeline and player view show">
                {MODES.map(([m, label, hint]) => (
                  <button key={m} className={mode === m ? 'active' : ''} title={hint} onClick={() => setMode(m)}>{label}</button>
                ))}
              </div>
            )}
          </Player>

          {tapOpen && <TapPanel audio={audio} runs={tapRuns} refs={tapRefs} onChange={onTaps} onClose={() => setTapOpen(false)} />}

          {sideA ? <div className="compare-bar">
            <SidePicker name="A" color={SIDE_A} side={sideA} versions={list} labels={labels} onChange={n => setSide('a', n)} songId={id} />
            {sideB
              ? <SidePicker name="B" color={SIDE_B} side={sideB} versions={list} labels={labels} onChange={n => setSide('b', n)} songId={id}
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
          </div> : <p className="hint no-versions">No versions yet: press <b>Generate new version</b> under Generator settings to make the first map.</p>}

          <div className="views">
            <Timeline analysis={analysis} tracks={tracks} labels={trackLabels} audio={audio} follow={follow} taps={tapTimeline}
              stacked={mode === 'stack'} />
            <div className="front-stack">
              {tracks.length > 1
                ? tracks.map((t, k) => (
                  <FrontView key={k} difficulty={t.d} tempo={tempoOf(analysis.tempo)} audio={audio} color={t.color} label={k === 0 ? 'A' : 'B'} />
                ))
                : <FrontView difficulty={current} tempo={tempoOf(analysis.tempo)} audio={audio} />}
            </div>
          </div>

          <div className="bottom-grid">
            <div>
              {sideA && <VersionsPanel versions={list} labels={labels} a={sideA} b={sideB} onPick={onPick}
                onDelete={onDeleteVersion} onPrune={onPrune} onLoadSettings={onLoadSettings} linksOf={linksOf} />}
              {sideA && byVersion[sideA.v] && (
                <ReportCards difficulties={byVersion[sideA.v]} selected={sideA.d} onSelect={d => setSide('a', { d })}
                  vsHuman={versionA?.kind === 'abeat' ? versionA.vsHuman : null} title={`A · ${labels[sideA.v] ?? ''}`} color={SIDE_A} />
              )}
              {sideB && byVersion[sideB.v] && (
                <ReportCards difficulties={byVersion[sideB.v]} selected={sideB.d} onSelect={d => setSide('b', { d })}
                  vsHuman={versionB?.kind === 'abeat' ? versionB.vsHuman : null} title={`B · ${labels[sideB.v] ?? ''}`} color={SIDE_B} />
              )}
              <div className="card issues-card">
                <h3>
                  Flow issues {current && <span className="muted">{showA ? 'A' : 'B'}</span>}{' '}
                  <span className="muted">{current?.report.issues.length ? `(${current.report.issues.length})` : '— none'}</span>
                </h3>
                <ul className="issue-list">
                  {current?.report.issues.slice(0, 300).map((i, k) => (
                    <li key={k} style={{ borderLeftColor: ISSUE_COLOR[i.kind] }}
                      onClick={() => seek(audio, tempoOf(analysis.tempo).toSec(i.b) - 1)}>
                      {fmtTime(tempoOf(analysis.tempo).toSec(i.b))} {i.hand === 0 ? 'L' : 'R'} {i.kind}
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
              <SettingsPanel settings={settings} defaults={defaults} profiles={profiles} environments={environments} envSuggestions={envSuggestions}
                songColors={colors?.id === id ? colors.c : undefined}
                layers={Object.keys(analysis.layers)}
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
  songId: string
}

/** Version + difficulty selector for one side of the comparison. */
function SidePicker({ name, color, side, versions, labels, onChange, onClear, songId }: SidePickerProps) {
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
      {v?.kind === 'abeat' && <AddToPlaylist songId={songId} version={v.id} />}
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
  const tempo = tempoOf(t)
  return (
    <div className="facts">
      {tempo.varies
        ? <span title={`${(t.changes?.length ?? 1) - 1} tempo changes follow the band (live recording)`}><b>{+tempo.min.toFixed(1)}–{+tempo.max.toFixed(1)}</b> BPM (variable)</span>
        : <span><b>{+t.bpm.toFixed(2)}</b> BPM</span>}
      <span><b>{fmtTime(a.audio.durationSec)}</b></span>
      <span>beats: <b>{t.backend}</b></span>
      <span>onsets: <b>{a.layerSource}</b></span>
      {a.vocalSource && <span>vocals: <b>{a.vocalSource}</b>{a.lyrics ? ` (${a.lyrics.words.length} words${a.lyrics.language ? `, ${a.lyrics.language}` : ''})` : ''}</span>}
      <span>{a.sections.length} sections</span>
      {a.source.url && <a href={a.source.url} target="_blank" rel="noopener noreferrer">source ↗</a>}
      {!t.stable && !tempo.varies && <span className="warn" title="Detected beats deviate from a constant tempo; notes may drift">⚠ tempo varies ({t.maxDevMs.toFixed(0)} ms)</span>}
    </div>
  )
}

interface PlayerProps {
  audio: HTMLAudioElement | null
  tempo: Tempo
  follow: boolean
  setFollow: (v: boolean) => void
  children: React.ReactNode
}

function Player({ audio, tempo, follow, setFollow, children }: PlayerProps) {
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
      if (timeRef.current) timeRef.current.textContent = `${fmtTime(audio.currentTime)}  ·  beat ${tempo.toBeat(audio.currentTime).toFixed(2)}`
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
  }, [audio, tempo])

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
