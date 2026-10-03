import type { Comparison, Difficulty } from '../api'
import { diffLabel } from '../utils/format'

interface Props {
  difficulties: Difficulty[]
  selected?: string
  onSelect: (name: string) => void
  /** Per difficulty: how ABeat's map compares with the human reference. */
  comparisons?: Record<string, Comparison | null>
  title?: string
}

/** One card per difficulty with flow score and the counts that matter for playability. */
export default function ReportCards({ difficulties, selected, onSelect, comparisons, title }: Props) {
  return (
    <div className="report-grid">
      {title && <div className="report-grid-title">{title}</div>}
      {difficulties.map(d => {
        const r = d.report
        const tone = r.flowScore > 85 ? 'var(--success)' : r.flowScore > 70 ? 'var(--warning)' : 'var(--danger)'
        const dots = d.notes.filter(n => n.d === 8).length
        return (
          <button key={d.name} className={`card report-card${d.name === selected ? ' active' : ''}`} onClick={() => onSelect(d.name)}>
            <div className="report-head">
              <span>{diffLabel(d.name)}</span>
              <span className="muted">{r.notes} notes</span>
            </div>
            <div className="report-score" style={{ color: tone }} title="Flow score: 100 = perfectly smooth">{r.flowScore.toFixed(1)}</div>
            <dl>
              <dt>NPS</dt><dd>{r.nps.toFixed(2)} (peak {r.peakNps.toFixed(2)})</dd>
              <dt>resets</dt><dd>{r.resets}{r.bombResets ? ` (+${r.bombResets} bomb)` : ''}</dd>
              <dt>vision blocks</dt><dd>{r.visionBlocks}</dd>
              <dt>crossovers</dt><dd>{r.crossovers}</dd>
              <dt>left / right</dt><dd>{Math.round(r.leftShare * 100)} / {Math.round((1 - r.leftShare) * 100)}</dd>
              <dt>NJS / JD</dt><dd>{d.njs} / {d.jumpDistance.toFixed(1)}</dd>
              <dt>walls</dt><dd>{d.walls.length}{r.wallClashes ? ` (${r.wallClashes} clash)` : ''}</dd>
              <dt>bombs / dots</dt><dd>{d.bombs.length}{r.bombHits ? ` (${r.bombHits} hit)` : ''} / {dots}</dd>
              <dt>lights</dt><dd>{r.lights}</dd>
            </dl>
            {comparisons?.[d.name] && <CompareLine c={comparisons[d.name]!} />}
          </button>
        )
      })}
    </div>
  )
}

function CompareLine({ c }: { c: Comparison }) {
  return (
    <div className="compare-line" title="ABeat vs the human map: note timing F1 at ±50 ms, median offset, direction / position distribution distance (0 = same)">
      <span>vs human</span>
      <b>F1 {c.f1.toFixed(2)}</b>
      <span>{c.offsetMs.toFixed(0)} ms</span>
      <span>dirΔ {c.directionDistance.toFixed(2)}</span>
      <span>posΔ {c.positionDistance.toFixed(2)}</span>
    </div>
  )
}
