import type { CSSProperties } from 'react'
import type { Difficulty } from '../api'
import { diffLabel } from '../utils/format'

interface Props {
  difficulties: Difficulty[]
  selected?: string
  onSelect: (name: string) => void
  /** Per difficulty: note-timing F1 against the human map. */
  vsHuman?: Record<string, number> | null
  title?: string
  /** Version colour (A / B) for the title and the selected card. */
  color?: string
}

/** One card per difficulty with flow score and the counts that matter for playability. */
export default function ReportCards({ difficulties, selected, onSelect, vsHuman, title, color }: Props) {
  return (
    <div className="report-grid" style={color ? ({ '--side': color } as CSSProperties) : undefined}>
      {title && <div className="report-grid-title" style={{ color }}>{title}</div>}
      {difficulties.map(d => {
        const r = d.report
        const tone = r.flowScore > 85 ? 'var(--success)' : r.flowScore > 70 ? 'var(--warning)' : 'var(--danger)'
        const dots = d.notes.filter(n => n.d === 8).length
        return (
          <button key={d.name} className={`card report-card${d.name === selected ? ' active' : ''}${color ? ' sided' : ''}`} onClick={() => onSelect(d.name)}>
            <div className="report-head">
              <span>{diffLabel(d.name)}</span>
              <span className="muted">{r.notes} notes</span>
            </div>
            <div className="report-score" style={{ color: tone }} title="Flow score: 100 = perfectly smooth">{r.flowScore.toFixed(1)}</div>
            <dl>
              <dt>NPS</dt><dd>{r.nps.toFixed(2)} (peak {r.peakNps.toFixed(2)})</dd>
              <dt>resets</dt><dd>{r.resets}{r.bombResets ? ` (+${r.bombResets} bomb)` : ''}</dd>
              <dt>vision blocks</dt><dd>{r.visionBlocks}</dd>
              <dt>crossovers / clashes</dt><dd>{r.crossovers} / {r.handClashes}</dd>
              <dt>left / right</dt><dd>{Math.round(r.leftShare * 100)} / {Math.round((1 - r.leftShare) * 100)}</dd>
              <dt>NJS / JD</dt><dd>{d.njs} / {d.jumpDistance.toFixed(1)}</dd>
              <dt>walls</dt><dd>{d.walls.length}{r.wallClashes ? ` (${r.wallClashes} clash)` : ''}</dd>
              <dt>bombs / dots</dt><dd>{d.bombs.length}{r.bombHits ? ` (${r.bombHits} hit)` : ''} / {dots}</dd>
              <dt>lights</dt><dd>{r.lights}</dd>
            </dl>
            {vsHuman?.[d.name] != null && (
              <div className="compare-line" title="Note timing F1 against the human map (±50 ms)"><span>vs human</span><b>F1 {vsHuman[d.name].toFixed(2)}</b></div>
            )}
          </button>
        )
      })}
    </div>
  )
}
