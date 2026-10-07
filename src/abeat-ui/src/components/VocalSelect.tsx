const VOCAL_OPTIONS = [
  { value: 'flux', label: 'Spectral flux (fast)' },
  { value: 'notes', label: 'Sung notes (pitch; no lyrics needed)' },
  { value: 'lyrics', label: 'Lyric syllables (Whisper transcription; slowest)' },
]

/** How vocal-stem onsets are found (needs stem separation). */
export default function VocalSelect({ value, onChange }: { value: string; onChange: (v: string) => void }) {
  return (
    <label>
      Vocal onsets
      <select value={value} onChange={e => onChange(e.target.value)}
        title="How vocal syllables are found: energy changes, sung notes (pitch), or transcribed lyrics aligned to the audio">
        {VOCAL_OPTIONS.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
      </select>
    </label>
  )
}
