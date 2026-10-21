import { useEffect, useRef, useState } from 'react'
import type { TapInfo, TapRun } from '../api'
import { compareBoth, compareHand, HANDS, TAP_COLORS, viewRuns, type TapRef } from '../utils/taps'


interface Props {
  audio: HTMLAudioElement | null
  runs: TapRun[]
  refs: TapRef[]
  /** How the server aligned the runs for the generator (null: no taps). */
  aligned?: TapInfo | null
  onChange: (runs: TapRun[]) => void
  onClose: () => void
  /** Difficulties of the imported human map (none: no human map). */
  humanDifficulties?: string[]
  /** Replaces both runs with the human map's notes of this difficulty. */
  onFromHuman?: (difficulty: string) => void
}

const DIFF_LABEL: Record<string, string> = { ExpertPlus: 'Expert+' }

const pct = (x: number) => `${Math.round(x * 100)}%`
const ms = (s: number) => `${s >= 0 ? '+' : ''}${Math.round(s * 1000)} ms`

/** Records one tap-along run per hand (right first): the song plays from the start and every key press or pad tap is a note. */
export default function TapPanel({ audio, runs, refs, aligned, onChange, onClose, humanDifficulties = [], onFromHuman }: Props) {
  const [recording, setRecording] = useState<number | null>(null)
  const [humanDiff, setHumanDiff] = useState('')
  const fromDiff = humanDifficulties.includes(humanDiff) ? humanDiff : humanDifficulties[humanDifficulties.length - 1]
  const [count, setCount] = useState(0)
  const taps = useRef<number[]>([])
  // appending: the new taps become a batch of the run, aligned on its own (another session, another latency)
  const [appending, setAppending] = useState(false)
  const appendingRef = useRef(false)
  const pad = useRef<HTMLButtonElement>(null)
  const stopRef = useRef<(() => void) | null>(null)
  const latest = useRef({ runs, onChange })
  useEffect(() => { latest.current = { runs, onChange } })

  useEffect(() => {
    if (recording === null || !audio) return
    const tap = (stamp: number) => {
      // the event waited in the queue for a moment: take the audio time of the press itself
      const lag = Math.max(0, performance.now() - stamp) / 1000
      taps.current.push(Math.max(0, audio.currentTime - lag * audio.playbackRate))
      setCount(taps.current.length)
      pad.current?.animate([{ opacity: 1 }, { opacity: 0.55 }], { duration: 90 })
    }
    const stop = () => {
      audio.pause()
      setRecording(null)
      if (!taps.current.length) return
      const next = [...latest.current.runs]
      const now = new Date().toISOString(), old = next[recording]
      next[recording] = appendingRef.current && old
        ? { ...old, appended: [...(old.appended ?? []), { recordedUtc: now, taps: taps.current }] }
        : { recordedUtc: now, taps: taps.current }
      latest.current.onChange(next.filter(Boolean))
    }
    const key = (e: KeyboardEvent) => {
      if (['Shift', 'Control', 'Alt', 'Meta', 'CapsLock', 'Tab'].includes(e.key)) return
      e.preventDefault()
      e.stopImmediatePropagation()
      if (e.key === 'Escape') stop()
      else if (!e.repeat) tap(e.timeStamp)
    }
    // Space / Enter on a focused button would click it on key up
    const keyUp = (e: KeyboardEvent) => { e.preventDefault(); e.stopImmediatePropagation() }
    const pointer = (e: PointerEvent) => { e.preventDefault(); tap(e.timeStamp) }
    const p = pad.current
    window.addEventListener('keydown', key, true)
    window.addEventListener('keyup', keyUp, true)
    p?.addEventListener('pointerdown', pointer)
    audio.addEventListener('ended', stop)
    stopRef.current = stop
    return () => {
      window.removeEventListener('keydown', key, true)
      window.removeEventListener('keyup', keyUp, true)
      p?.removeEventListener('pointerdown', pointer)
      audio.removeEventListener('ended', stop)
      stopRef.current = null
    }
  }, [recording, audio])
  /** Records run k from the start, or with `append` from the playhead on, adding to all of the run's taps. */
  const start = (k: number, append = false) => {
    if (!audio) return
    ;(document.activeElement as HTMLElement | null)?.blur()
    appendingRef.current = append
    setAppending(append)
    taps.current = []
    setCount(0)
    playFrom(audio, append ? audio.currentTime : 0)
    setRecording(k)
  }

  // appended batches on their run's latency, for counts and comparisons
  const view = viewRuns(runs, aligned)
  const both = view.length === 2 ? refs.map(r => ({ r, c: compareBoth(r, view) })) : []

  return (
    <div className="card tap-card">
      <div className="settings-head">
        <h3>Tap along</h3>
        <button className="icon-btn" title="Close" onClick={onClose} disabled={recording !== null}>✕</button>
      </div>
      <p className="muted tap-hint">
        One run per hand, right hand first: the song plays from the start, press any key (or tap the pad) on every note that hand
        would cut. Esc stops early; Append from playhead adds taps to a run from where the player is. Each run is shifted by its median offset, then matched (±70 ms) to the notes of the same
        hand; "any" also counts notes of the other hand. Both runs together are compared with all notes and doubles.
      </p>
      <p className="muted tap-hint">
        The generator gives each tap a note (see tap priority in the settings); the runs only mark moments, not hands, and
        taps of both runs together ask for a double.
        {aligned && aligned.count > 0 && <> Aligned to the song: latency {aligned.offsetsSec.map((o, k) =>
          `${HANDS[k]?.toLowerCase() ?? `run ${k + 1}`} ${aligned.confident[k] ? ms(o) : 'unclear, unshifted'}`).join(', ')}; {aligned.count} taps,
          {' '}{pct(aligned.snapped / aligned.count)} on an onset of the song, {aligned.doubles.length} doubles.</>}
      </p>

      {recording !== null
        ? (
          <div className="tap-rec">
            <button ref={pad} className="tap-pad" style={{ borderColor: TAP_COLORS[recording], color: TAP_COLORS[recording] }}>
              {HANDS[recording]} · {count} {appending ? 'new ' : ''}taps
              <span>any key / tap here</span>
            </button>
            <button className="btn-secondary" onClick={() => stopRef.current?.()}>Stop</button>
          </div>
        )
        : (
          <div className="tap-runs">
            {[0, 1].map(k => (
              <div key={k} className="tap-run" style={{ borderColor: TAP_COLORS[k] }}>
                <b style={{ color: TAP_COLORS[k] }}>{HANDS[k]}</b>
                <span className="muted">
                  {runs[k] ? `${view[k].taps.length} taps · ${runs[k].exact ? 'from the human map' : new Date(runs[k].recordedUtc).toLocaleString()}`
                    + (runs[k].appended?.length ? ` + ${runs[k].appended!.length} appended` + (aligned?.appendedOffsetsSec?.[k]?.length
                      ? ` (${aligned.appendedOffsetsSec[k].map(ms).join(', ')})` : '') : '') : 'not recorded'}
                </span>
                <button className={runs[k] || k > runs.length ? 'btn-secondary' : ''} disabled={!audio || k > runs.length}
                  title={runs[k] ? 'Replace this run: records again from the start of the song' : 'Records from the start of the song'}
                  onClick={() => start(k)}>{runs[k] ? 'Record again' : 'Record'}</button>
                {runs[k] && (
                  <button className="btn-secondary" disabled={!audio} onClick={() => start(k, true)}
                    title="Records from the playhead (seek on the timeline first) and adds the taps to this run as a batch with its own latency (found from the song, or the run's); a tap on one already there counts once, a human map note winning">
                    Append from playhead
                  </button>
                )}
              </div>
            ))}
            {runs.length > 0 && (
              <button className="btn-secondary" title="Deletes both runs (and their appended taps); the generator then ignores taps"
                onClick={() => confirm('Delete both tap runs?') && onChange([])}>Clear taps</button>
            )}
          </div>
        )}

      {recording === null && onFromHuman && fromDiff && (
        <div className="tap-human">
          <span className="muted">From the human map:</span>
          <select value={fromDiff} onChange={e => setHumanDiff(e.target.value)} title="Which difficulty of the human map becomes the taps">
            {humanDifficulties.map(d => <option key={d} value={d}>{DIFF_LABEL[d] ?? d}</option>)}
          </select>
          <button className="btn-secondary"
            title="Turns this difficulty's notes into the two runs (right hand, left hand; notes of both hands at once become doubles), with the mapper's exact timing. Generate then remakes the map, or builds easier difficulties on its rhythm. Replaces the recorded runs."
            onClick={() => (!runs.length || confirm('Replace both tap runs with the human map\'s notes?')) && onFromHuman(fromDiff)}>
            Use as taps
          </button>
        </div>
      )}

      {runs.length > 0 && recording === null && (
        <table className="tap-table">
          <thead>
            <tr><th /><th>taps</th>{refs.map(r => <th key={r.name} colSpan={5} style={{ color: r.color }}>vs {r.name} ({r.all.length} notes)</th>)}</tr>
            <tr><th /><th />{refs.map(r => ['offset', 'F1', 'P', 'R', 'any'].map(h => <th key={r.name + h} className="muted">{h}</th>))}</tr>
          </thead>
          <tbody>
            {view.map((run, k) => (
              <tr key={k}>
                <td style={{ color: TAP_COLORS[k] }}>{HANDS[k]}</td>
                <td>{run.taps.length}</td>
                {refs.map(r => {
                  const c = compareHand(r, k, run.taps)
                  return [
                    <td key={r.name + 'o'}>{ms(c.offset)}</td>,
                    <td key={r.name + 'f'}><b>{c.f1.toFixed(2)}</b></td>,
                    <td key={r.name + 'p'} title="Taps that have a note of this hand">{pct(c.precision)}</td>,
                    <td key={r.name + 'r'} title="Notes of this hand that have a tap">{pct(c.recall)}</td>,
                    <td key={r.name + 'a'} title="Taps that have a note of either hand">{pct(c.any.precision)}</td>,
                  ]
                })}
              </tr>
            ))}
            {both.length > 0 && (
              <tr className="tap-both">
                <td>Both</td>
                <td>{both[0].c.taps}</td>
                {both.map(({ r, c }) => [
                  <td key={r.name + 'o'} />,
                  <td key={r.name + 'f'}><b>{c.f1.toFixed(2)}</b></td>,
                  <td key={r.name + 'p'} title="Merged taps that have a note">{pct(c.precision)}</td>,
                  <td key={r.name + 'r'} title="Note times that have a tap">{pct(c.recall)}</td>,
                  <td key={r.name + 'a'} />,
                ])}
              </tr>
            )}
          </tbody>
        </table>
      )}
      {recording === null && both.map(({ r, c }) => (
        <div key={r.name} className="compare-line">
          <span style={{ color: r.color }}>Doubles vs {r.name}</span>
          <span>you <b>{c.doubles}</b> · map <b>{c.refDoubles}</b> · at the same time <b>{c.doubleMatch.matched}</b></span>
        </div>
      ))}
    </div>
  )
}

// media element mutations live outside components (React treats props as immutable)
function playFrom(audio: HTMLAudioElement, t: number) {
  audio.pause()
  audio.currentTime = t
  audio.play()
}
