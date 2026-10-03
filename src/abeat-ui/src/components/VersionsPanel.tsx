import type { Version } from '../api'
import { SIDE_A, SIDE_B } from '../utils/draw'

export interface Side { v: string; d: string }

interface Props {
  versions: Version[]
  labels: Record<string, string>
  a: Side
  b: Side | null
  onPick: (side: 'a' | 'b', v: string) => void
  onDelete: (v: string) => void
  onPrune: () => void
  onLoadSettings: (v: string) => void
}

/** Generation history (newest first) plus the human map: pick A / B, load settings, delete. */
export default function VersionsPanel({ versions, labels, a, b, onPick, onDelete, onPrune, onLoadSettings }: Props) {
  const generations = versions.filter(v => v.kind === 'abeat')
  const prunable = generations.filter(v => v.id !== a.v && v.id !== b?.v).length
  return (
    <div className="card versions-card">
      <div className="versions-head">
        <h3>Versions <span className="muted">({generations.length})</span></h3>
        <button className="btn-secondary" disabled={!prunable} onClick={onPrune}
          title="Delete every generation except the ones shown as A and B">
          Keep only A &amp; B
        </button>
      </div>
      <ul className="version-list">
        {versions.map(v => {
          const isA = v.id === a.v, isB = v.id === b?.v
          const f1 = v.kind === 'abeat' ? v.vsHuman?.[a.d] : undefined
          return (
            <li key={v.id} className={`version-row${isA ? ' is-a' : ''}${isB ? ' is-b' : ''}`}>
              <button className={`side-pick${isA ? ' on' : ''}`} style={{ '--side': SIDE_A } as React.CSSProperties}
                title="Show as A" onClick={() => onPick('a', v.id)}>A</button>
              <button className={`side-pick${isB ? ' on' : ''}`} style={{ '--side': SIDE_B } as React.CSSProperties}
                title="Show as B (click again to hide B)" onClick={() => onPick('b', v.id)}>B</button>
              <span className="version-label">
                {labels[v.id]}
                {v.kind === 'abeat' && v.draft && <span className="draft-tag" title="Auto-regenerate replaces this version">draft</span>}
              </span>
              <span className="muted version-diffs">{v.difficulties.length} diff{v.difficulties.length === 1 ? '' : 's'}</span>
              {f1 != null && <span className="version-f1" title={`${a.d}: note timing F1 vs the human map`}>F1 {f1.toFixed(2)}</span>}
              {v.kind === 'abeat' && (
                <>
                  <button className="icon-btn" title="Load this version's generator settings" onClick={() => onLoadSettings(v.id)}>⚙</button>
                  <button className="icon-btn danger" title="Delete this version" onClick={() => onDelete(v.id)}>✕</button>
                </>
              )}
            </li>
          )
        })}
      </ul>
    </div>
  )
}
