import { useState } from 'react'
import type { DifficultyProfile, EnvironmentEntry, EnvironmentSuggestions, GeneratorSettings, ProfileOverride } from '../api'
import { DIFFICULTIES, diffLabel, fmtNum } from '../utils/format'
import ToggleField from './ToggleField'

interface Props {
  settings: GeneratorSettings
  defaults?: GeneratorSettings
  /** Built-in limits per difficulty, shown where the song doesn't override them. */
  profiles?: Record<string, DifficultyProfile>
  environments?: EnvironmentEntry[]
  /** What "Auto" picks for this song and the best-suited environments. */
  envSuggestions?: EnvironmentSuggestions
  layers: string[]
  layerSource: string
  onChange: (s: GeneratorSettings) => void
}

const MODES: [string, string][] = [['OneSaber', 'One Saber'], ['90Degree', '90°'], ['360Degree', '360°']]

const LIMITS: [keyof DifficultyProfile, string, string, number, number, number][] = [
  ['baseNps', 'notes/s', 'Notes per second at mid energy (scaled by section energy)', 0.5, 12, 0.1],
  ['maxNps', 'max notes/s', 'Cap on notes per second in the loudest parts', 1, 16, 0.1],
  ['minGapSec', 'min gap s', 'Shortest time between two notes of any hand', 0.05, 0.6, 0.01],
  ['minSameHandGapSec', 'hand gap s', 'Shortest time between two notes of the same hand', 0.08, 0.6, 0.01],
  ['burstGapSec', 'burst gap s', 'Quick back-and-forth flicks of one hand (up to 4 notes, 6 on Expert+) may be this close; set it to the hand gap for no bursts', 0.05, 0.6, 0.01],
]

const WEIGHT_MAX: Record<string, number> = { reset: 100, slowReset: 10, tooFast: 60, crossover: 10, visionBlock: 8 }

/** Generator settings; every committed change produces a new settings object for the parent. */
export default function SettingsPanel({ settings: s, defaults, profiles, environments = [], envSuggestions, layers, layerSource, onChange }: Props) {
  const set = (patch: Partial<GeneratorSettings>) => onChange({ ...s, ...patch })
  const setLimits = (d: string, o: ProfileOverride | undefined) => {
    const next = { ...(s.profileOverrides ?? {}) }
    if (o) next[d] = o
    else delete next[d]
    set({ profileOverrides: next })
  }

  return (
    <div className="settings-form">
      <fieldset>
        <legend>Difficulties</legend>
        <div className="diff-checks">
          {DIFFICULTIES.map(d => (
            <label key={d} className="inline-check">
              <input
                type="checkbox"
                checked={s.difficulties.includes(d)}
                onChange={e => set({ difficulties: DIFFICULTIES.filter(x => (x === d ? e.target.checked : s.difficulties.includes(x))) })}
              />
              {diffLabel(d)}
            </label>
          ))}
        </div>
      </fieldset>

      <fieldset>
        <legend>Extra modes</legend>
        <div className="diff-checks">
          {MODES.map(([m, label]) => (
            <label key={m} className="inline-check" title="Written next to Standard for the same difficulties (in the zip and ArcViewer)">
              <input
                type="checkbox"
                checked={(s.modes ?? []).includes(m)}
                onChange={e => set({ modes: MODES.map(([x]) => x).filter(x => (x === m ? e.target.checked : (s.modes ?? []).includes(x))) })}
              />
              {label}
            </label>
          ))}
        </div>
        <EnvironmentPicker value={s.environment} environments={environments} suggestions={envSuggestions} onChange={v => set({ environment: v })} />
      </fieldset>

      <fieldset>
        <legend>General</legend>
        <Slider name="density" value={s.density} min={0.3} max={2} step={0.05} onCommit={v => set({ density: v })} />
        <Slider name="seed" value={s.seed} min={1} max={100} step={1} onCommit={v => set({ seed: v })} />
        <Slider name="beam width" value={s.beamWidth} min={8} max={256} step={8} onCommit={v => set({ beamWidth: v })} />
        <div className="toggle-grid">
          <ToggleField label="Lights" checked={s.lights} onChange={v => set({ lights: v })} />
          <ToggleField label="Bombs" checked={s.bombs} onChange={v => set({ bombs: v })} />
          <ToggleField label="Walls" checked={s.walls} onChange={v => set({ walls: v })} />
          <ToggleField label="Dodge walls" checked={s.dodgeWalls} onChange={v => set({ dodgeWalls: v })} disabled={!s.walls} />
          <ToggleField label="Crouch walls" checked={s.crouchWalls} onChange={v => set({ crouchWalls: v })} disabled={!s.walls} />
          <ToggleField label="Rhythm walls" checked={s.rhythmWalls ?? true} onChange={v => set({ rhythmWalls: v })} disabled={!s.walls} />
          <ToggleField label="Arcs on held notes" checked={s.arcs ?? true} onChange={v => set({ arcs: v })} />
          <ToggleField label="Chains on rolls" checked={s.chains ?? true} onChange={v => set({ chains: v })} />
          <ToggleField label="Angles follow melody" checked={s.angleOffsets ?? true} onChange={v => set({ angleOffsets: v })} />
          <ToggleField label="Pause before drops" checked={s.dropPause ?? true} onChange={v => set({ dropPause: v })} />
        </div>
      </fieldset>

      {profiles && s.difficulties.length > 0 && (
        <fieldset>
          <legend>Speed and density</legend>
          {s.difficulties.filter(d => profiles[d]).map(d => {
            const o = s.profileOverrides?.[d] ?? {}
            return (
              <div key={d} className="limit-group">
                <div className="limit-head">
                  {diffLabel(d)}
                  {Object.keys(o).length > 0 && <button className="link-btn" onClick={() => setLimits(d, undefined)}>reset</button>}
                </div>
                {LIMITS.map(([k, name, title, min, max, step]) => (
                  <div key={k} title={title}>
                    <Slider name={name} value={o[k] ?? profiles[d][k]} min={min} max={max} step={step}
                      onCommit={v => setLimits(d, { ...o, [k]: v })} />
                  </div>
                ))}
              </div>
            )
          })}
        </fieldset>
      )}

      <fieldset>
        <legend>Flow weights</legend>
        {Object.entries(s.weights).map(([k, v]) => {
          const max = WEIGHT_MAX[k] ?? Math.max(3, Math.ceil((defaults?.weights[k] ?? v) * 3))
          return <Slider key={k} name={k} value={v} min={0} max={max} step={max > 20 ? 1 : 0.05}
            onCommit={x => set({ weights: { ...s.weights, [k]: x } })} />
        })}
      </fieldset>

      <fieldset>
        <legend>Onset layers ({layerSource})</legend>
        {layers.map(k => (
          <Slider key={k} name={k} value={s.layerWeights[k] ?? 0} min={0} max={2} step={0.05}
            onCommit={x => set({ layerWeights: { ...s.layerWeights, [k]: x } })} />
        ))}
      </fieldset>
    </div>
  )
}

interface SliderProps { name: string; value: number; min: number; max: number; step: number; onCommit: (v: number) => void }

/** Range input that shows the live value while dragging but only commits on release. */
function Slider({ name, value, min, max, step, onCommit }: SliderProps) {
  const [draft, setDraft] = useState<number | null>(null)
  const shown = draft ?? value
  const commit = () => {
    if (draft !== null && draft !== value) onCommit(draft)
    setDraft(null)
  }
  return (
    <div className="slider-row">
      <span className="slider-name" title={name}>{name}</span>
      <input type="range" min={min} max={max} step={step} value={shown}
        onChange={e => setDraft(+e.target.value)} onPointerUp={commit} onKeyUp={commit} onBlur={commit} />
      <output>{fmtNum(shown)}</output>
    </div>
  )
}

/** Environment select: Auto (naming what it picks for this song), the song's best matches, then every
 * environment by lighting system. Older settings saved short names ("Default", "Pyro"). */
function EnvironmentPicker({ value, environments, suggestions, onChange }: {
  value?: string
  environments: EnvironmentEntry[]
  suggestions?: EnvironmentSuggestions
  onChange: (v: string) => void
}) {
  const byId = new Map(environments.map(e => [e.id.toLowerCase(), e]))
  const raw = value ?? 'Auto'
  const current = raw.toLowerCase() === 'auto' ? 'Auto'
    : (byId.get(raw.toLowerCase()) ?? byId.get(`${raw.toLowerCase()}environment`) ?? environments.find(e => e.name === raw))?.id ?? raw
  const autoName = suggestions ? byId.get(suggestions.auto.toLowerCase())?.name ?? suggestions.auto : null
  const c = suggestions?.character
  const title = 'Beat Saber environment the lights are made for. Group environments (Weave and later) get a v3 lightshow on their own light groups, '
    + 'with turning and moving lights; classic ones get classic events.'
    + (c ? `\nThis song: drive ${c.drive.toFixed(2)} (four-on-the-floor), intensity ${c.intensity.toFixed(2)}, darkness ${c.darkness.toFixed(2)}, ${Math.round(c.bpm)} BPM` : '')
  const option = (e: EnvironmentEntry) => <option key={e.id} value={e.id}>{e.name}</option>
  return (
    <label className="inline-check" title={title}>
      Environment
      <select value={current} onChange={e => onChange(e.target.value)}>
        <option value="Auto">{autoName ? `Auto (${autoName})` : 'Auto (suits the song)'}</option>
        {suggestions && (
          <optgroup label="Suits this song">
            {suggestions.ranked.slice(0, 5).map(r => byId.get(r.id.toLowerCase())).filter((e): e is EnvironmentEntry => !!e)
              .map(e => <option key={`s-${e.id}`} value={e.id}>{e.name}</option>)}
          </optgroup>
        )}
        <optgroup label="Group lights">{environments.filter(e => e.system === 'Groups').map(option)}</optgroup>
        <optgroup label="Classic lights">{environments.filter(e => e.system === 'Classic').map(option)}</optgroup>
        {current !== 'Auto' && !byId.has(current.toLowerCase()) && <option value={current}>{current}</option>}
      </select>
    </label>
  )
}
