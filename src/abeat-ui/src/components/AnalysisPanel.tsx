import type { AnalysisOptions } from '../api'

export type AnalysisChoice = Pick<AnalysisOptions, 'tempo' | 'pitched' | 'separator'> & { vocals: string }

type Field = { key: keyof AnalysisChoice; label: string; hint: string; options: [string, string][] }

const FIELDS: Field[] = [
  {
    key: 'tempo', label: 'Tempo', hint: 'Auto keeps one BPM unless the song drifts (live recordings); variable follows every tempo change',
    options: [['auto', 'Auto'], ['constant', 'One BPM'], ['variable', 'Variable']],
  },
  {
    key: 'vocals', label: 'Vocal rhythm', hint: 'How syllables are found in the separated vocals; sung notes need no lyrics, lyric syllables transcribe them (Whisper)',
    options: [['flux', 'Energy changes (fast)'], ['notes', 'Sung notes (pitch)'], ['lyrics', 'Lyric syllables (slowest)']],
  },
  {
    key: 'pitched', label: 'Bass and melody rhythm', hint: 'Transcribed notes also drive note rows (pitch) and arcs (held notes)',
    options: [['flux', 'Energy changes (fast)'], ['notes', 'Transcribed notes (basic-pitch)']],
  },
  {
    key: 'separator', label: 'Vocal separation', hint: 'BS-RoFormer is cleaner but needs a GPU in practice (~35 min per minute of audio on a CPU)',
    options: [['demucs', 'Demucs'], ['roformer', 'BS-RoFormer']],
  },
]

interface Props {
  value: AnalysisChoice
  onChange: (v: AnalysisChoice) => void
  busy: boolean
  lyricsOpen: boolean
  onLyrics: () => void
  onRun: () => void
  onClose: () => void
}

/** Options for the next analysis run; nothing changes until Re-analyze is pressed. */
export default function AnalysisPanel({ value, onChange, busy, lyricsOpen, onLyrics, onRun, onClose }: Props) {
  return (
    <div className="card analysis-card">
      <div className="settings-head">
        <h3>Re-analyze song</h3>
        <button className="icon-btn" title="Close" onClick={onClose}>✕</button>
      </div>
      <div className="analysis-grid">
        {FIELDS.map(f => (
          <label key={f.key} title={f.hint}>
            {f.label}
            <select value={value[f.key] ?? f.options[0][0]} onChange={e => onChange({ ...value, [f.key]: e.target.value })}>
              {f.options.map(([v, l]) => <option key={v} value={v}>{l}</option>)}
            </select>
            <span className="field-hint">{f.hint}</span>
          </label>
        ))}
      </div>
      <div className="analysis-foot">
        <button onClick={onRun} disabled={busy}>Re-analyze with these options</button>
        <button className={`btn-secondary${lyricsOpen ? ' active' : ''}`} onClick={onLyrics}
          title="Paste the lyrics so lyric syllables align to the right words">Lyrics…</button>
        <span className="hint">Separated stems are reused. Existing versions stay; their notes move onto the new beat grid.</span>
      </div>
    </div>
  )
}
