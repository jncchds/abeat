import { useState } from 'react'
import type { GeneratorSettings } from '../api'
import { DIFFICULTIES, diffLabel, fmtNum } from '../utils/format'
import ToggleField from './ToggleField'

interface Props {
  settings: GeneratorSettings
  defaults?: GeneratorSettings
  layers: string[]
  layerSource: string
  onChange: (s: GeneratorSettings) => void
}

const MODES: [string, string][] = [['OneSaber', 'One Saber'], ['90Degree', '90°'], ['360Degree', '360°']]

const WEIGHT_MAX: Record<string, number> = { reset: 100, slowReset: 10, tooFast: 60, crossover: 10, visionBlock: 8 }

/** Generator settings; every committed change produces a new settings object for the parent. */
export default function SettingsPanel({ settings: s, defaults, layers, layerSource, onChange }: Props) {
  const set = (patch: Partial<GeneratorSettings>) => onChange({ ...s, ...patch })

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
        <label className="inline-check" title="Pyro: PyroEnvironment with a v3 group lightshow (kicks, snares, hats, melody and notes each light their own group pair)">
          Environment
          <select value={s.environment ?? 'Default'} onChange={e => set({ environment: e.target.value })}>
            <option value="Default">Default (classic lights)</option>
            <option value="Pyro">Pyro (v3 group lights)</option>
          </select>
        </label>
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
          <ToggleField label="Arcs on held notes" checked={s.arcs ?? true} onChange={v => set({ arcs: v })} />
          <ToggleField label="Chains on rolls" checked={s.chains ?? true} onChange={v => set({ chains: v })} />
          <ToggleField label="Angles follow melody" checked={s.angleOffsets ?? true} onChange={v => set({ angleOffsets: v })} />
          <ToggleField label="Pause before drops" checked={s.dropPause ?? true} onChange={v => set({ dropPause: v })} />
        </div>
      </fieldset>

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
