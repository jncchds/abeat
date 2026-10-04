import type { AnalysisOptions } from '../api'

type Extras = Pick<AnalysisOptions, 'tempo' | 'pitched' | 'separator'>

const FIELDS: { key: keyof Extras; label: string; title: string; options: [string, string][] }[] = [
  {
    key: 'tempo', label: 'tempo', title: 'Tempo map: BPM changes only when one BPM can\'t follow the song (live recordings), or force one',
    options: [['auto', 'tempo: auto'], ['constant', 'tempo: one BPM'], ['variable', 'tempo: variable']],
  },
  {
    key: 'pitched', label: 'other/bass', title: 'Other/bass onsets: spectral flux, or notes transcribed by basic-pitch (pitch drives rows, note lengths drive arcs)',
    options: [['flux', 'other/bass: flux'], ['notes', 'other/bass: notes']],
  },
  {
    key: 'separator', label: 'separator', title: 'Vocal stem from Demucs, or from BS-RoFormer (cleaner, slow without a GPU; worker extra "roformer")',
    options: [['demucs', 'vocals: Demucs'], ['roformer', 'vocals: RoFormer']],
  },
]

/** Compact selects for the less common analysis options (applied on Re-analyze). */
export default function AnalysisExtras({ value, onChange }: { value: Extras; onChange: (v: Extras) => void }) {
  return (
    <>
      {FIELDS.map(f => (
        <select key={f.key} aria-label={f.label} title={f.title} value={value[f.key] ?? f.options[0][0]}
          onChange={e => onChange({ ...value, [f.key]: e.target.value })}>
          {f.options.map(([v, l]) => <option key={v} value={v}>{l}</option>)}
        </select>
      ))}
    </>
  )
}
