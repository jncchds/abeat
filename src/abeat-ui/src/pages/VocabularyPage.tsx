import { useEffect, useMemo, useState } from 'react'
import { errorText, getVocabulary, type VocabularyDifficulty } from '../api'
import { BLUE, RED } from '../utils/draw'

const DIFFS = ['Easy', 'Normal', 'Hard', 'Expert', 'ExpertPlus']
const DIFF_LABEL: Record<string, string> = { ExpertPlus: 'Expert+' }
type Kind = 'moves' | 'stacks' | 'doubles' | 'figures'
const KINDS: { id: Kind; label: string; hint: string }[] = [
  { id: 'moves', label: 'Moves', hint: "One hand's note and its next note (faded = from, solid = to). The planner pays for a move outside this list, so crossings happen only where a known move leads." },
  { id: 'stacks', label: 'Stacks', hint: 'One swing through notes lined up along the cut; the swing meets the faded-edged head first and leaves from the last note.' },
  { id: 'doubles', label: 'Doubles', hint: 'Both hands at once.' },
  { id: 'figures', label: 'Figures', hint: 'Single notes: hand, cell and cut direction.' },
]
const DIR_VEC: [number, number][] = [[0, 1], [0, -1], [-1, 0], [1, 0], [-0.707, 0.707], [0.707, 0.707], [-0.707, -0.707], [0.707, -0.707], [0, 0]]
const DIR_NAME = ['↑', '↓', '←', '→', '↖', '↗', '↙', '↘', '•']
const PAGE = 240

interface Fig { x: number; y: number; d: number }
const fig = (f: number): Fig => ({ y: Math.floor(f / 36), x: Math.floor(f / 9) % 4, d: f % 9 })
const figText = (f: Fig) => `${DIR_NAME[f.d]} ${f.x},${f.y}`
/** Notes of the left hand on the right half (lanes 2-3) or the right hand on the left half. */
const crosses = (hand: number, f: Fig) => hand === 0 ? f.x >= 2 : f.x <= 1
const step = (d: number) => [Math.sign(Math.round(DIR_VEC[d][0] * 1000)), Math.sign(Math.round(DIR_VEC[d][1] * 1000))]

interface Entry { key: string; use: number; label: string; crossing: boolean; notes: { f: Fig; c: number; faded?: boolean }[]; arrow?: [Fig, Fig]; path?: Fig[] }

function entries(v: VocabularyDifficulty, kind: Kind, hand: number): Entry[] {
  switch (kind) {
    case 'moves':
      return v.moves[hand].map((m, i) => {
        const a = fig(Math.floor(m / 108)), b = fig(m % 108)
        return { key: String(m), use: v.moveUse[hand][i], label: `${figText(a)} → ${figText(b)}`, crossing: crosses(hand, b),
          notes: [{ f: a, c: hand, faded: true }, { f: b, c: hand }], arrow: [a, b] }
      })
    case 'stacks':
      return v.stacks[hand].map((k, i) => {
        const head = fig(Math.floor(k / 4)), n = k % 4, [sx, sy] = step(head.d)
        const cells = Array.from({ length: n }, (_, j) => ({ ...head, x: head.x + j * sx, y: head.y + j * sy }))
        return { key: String(k), use: v.stackUse[hand][i], label: `${n} × ${figText(head)}`, crossing: cells.some(f => crosses(hand, f)),
          notes: cells.map((f, j) => ({ f, c: hand, faded: j === 0 })), path: cells }
      })
    case 'doubles':
      return v.doubleShapes.map(([l, r], i) => {
        const a = fig(l), b = fig(r)
        return { key: `${l}-${r}`, use: v.doubleUse[i], label: `${figText(a)} + ${figText(b)}`, crossing: a.x > b.x,
          notes: [{ f: a, c: 0 }, { f: b, c: 1 }] }
      })
    default:
      return v.figures[hand].map((f, i) => {
        const a = fig(f)
        return { key: String(f), use: v.figureUse[hand][i], label: figText(a), crossing: crosses(hand, a), notes: [{ f: a, c: hand }] }
      })
  }
}

const CELL = 26, PAD = 4, W = 4 * CELL + 2 * PAD, H = 3 * CELL + 2 * PAD
const cx = (x: number) => PAD + x * CELL + CELL / 2
const cy = (y: number) => PAD + (2 - y) * CELL + CELL / 2

function Note({ f, c, faded }: { f: Fig; c: number; faded?: boolean }) {
  const s = CELL * 0.74, r = s / 2, [dx, dy] = DIR_VEC[f.d], x = cx(f.x), y = cy(f.y)
  return (
    <g opacity={faded ? 0.45 : 1}>
      <rect x={x - r} y={y - r} width={s} height={s} rx={3} fill={c === 0 ? RED : BLUE} />
      {f.d === 8
        ? <circle cx={x} cy={y} r={r * 0.3} fill="#fff" />
        : <polygon fill="#fff" points={[
            [x + dx * r * 0.85, y - dy * r * 0.85],
            [x - dx * r * 0.1 - dy * r * 0.6, y + dy * r * 0.1 - dx * r * 0.6],
            [x - dx * r * 0.1 + dy * r * 0.6, y + dy * r * 0.1 + dx * r * 0.6],
          ].map(p => p.join(',')).join(' ')} />}
    </g>
  )
}

function Diagram({ e }: { e: Entry }) {
  const same = e.arrow && e.arrow[0].x === e.arrow[1].x && e.arrow[0].y === e.arrow[1].y
  return (
    <svg viewBox={`0 0 ${W} ${H}`} width={W} height={H} role="img" aria-label={e.label}>
      <defs>
        <marker id="vocab-arrow" viewBox="0 0 10 10" refX="8" refY="5" markerWidth="5" markerHeight="5" orient="auto-start-reverse">
          <path d="M0,0 L10,5 L0,10 z" fill="var(--text)" />
        </marker>
      </defs>
      <rect x={PAD} y={PAD} width={4 * CELL} height={3 * CELL} rx={4} fill="var(--bg)" stroke="var(--border)" />
      {[1, 2, 3].map(i => <line key={`v${i}`} x1={PAD + i * CELL} x2={PAD + i * CELL} y1={PAD} y2={PAD + 3 * CELL} stroke="var(--border)" strokeWidth={i === 2 ? 1.5 : 0.5} />)}
      {[1, 2].map(i => <line key={`h${i}`} y1={PAD + i * CELL} y2={PAD + i * CELL} x1={PAD} x2={PAD + 4 * CELL} stroke="var(--border)" strokeWidth={0.5} />)}
      {e.path && <polyline points={e.path.map(f => `${cx(f.x)},${cy(f.y)}`).join(' ')} fill="none" stroke="var(--text-muted)" strokeWidth={CELL * 0.85} strokeLinecap="round" opacity={0.25} />}
      {e.notes.map((n, i) => <Note key={i} {...n} />)}
      {e.arrow && !same && (() => {
        const [a, b] = e.arrow, x1 = cx(a.x), y1 = cy(a.y), x2 = cx(b.x), y2 = cy(b.y)
        const len = Math.hypot(x2 - x1, y2 - y1), ux = (x2 - x1) / len, uy = (y2 - y1) / len, cut = CELL * 0.42
        // bow the arrow so it doesn't hide behind notes it passes over
        const mx = (x1 + x2) / 2 - uy * CELL * 0.35, my = (y1 + y2) / 2 + ux * CELL * 0.35
        return <path d={`M${x1 + ux * cut},${y1 + uy * cut} Q${mx},${my} ${x2 - ux * cut},${y2 - uy * cut}`} fill="none" stroke="var(--text)" strokeWidth={1.6} markerEnd="url(#vocab-arrow)" />
      })()}
      {same && <circle cx={cx(e.arrow![1].x)} cy={cy(e.arrow![1].y)} r={CELL * 0.48} fill="none" stroke="var(--text)" strokeWidth={1.2} strokeDasharray="2 2" />}
    </svg>
  )
}

const stored = (k: string) => { try { return localStorage.getItem(k) } catch { return null } }

/** Every move, stack, double shape and figure in the style prior's vocabularies, drawn on the 4x3 grid. */
export default function VocabularyPage() {
  const [data, setData] = useState<Record<string, VocabularyDifficulty> | null>(null)
  const [error, setError] = useState('')
  const [diff, setDiff] = useState(() => stored('vocabDiff') ?? 'ExpertPlus')
  const [kind, setKind] = useState<Kind>(() => (stored('vocabKind') as Kind | null) ?? 'moves')
  const [hand, setHand] = useState(1)
  const [crossingOnly, setCrossingOnly] = useState(false)
  // "show more" applies to the list it was pressed on
  const [more, setMore] = useState({ key: '', limit: PAGE })
  const listKey = `${diff}|${kind}|${hand}|${crossingOnly}`
  const limit = more.key === listKey ? more.limit : PAGE

  useEffect(() => { getVocabulary().then(r => setData(r.data.difficulties)).catch(e => setError(errorText(e))) }, [])
  useEffect(() => { try { localStorage.setItem('vocabDiff', diff); localStorage.setItem('vocabKind', kind) } catch { /* private mode */ } }, [diff, kind])

  const v = data?.[diff]
  const all = useMemo(() => v ? entries(v, kind, hand).sort((a, b) => b.use - a.use || a.key.localeCompare(b.key)) : [], [v, kind, hand])
  const shown = crossingOnly ? all.filter(e => e.crossing) : all
  const crossingCount = all.filter(e => e.crossing).length

  return (
    <div className="page">
      <div className="page-header">
        <h2>Vocabulary</h2>
        {v && <span className="muted">learned from {v.maps} curated maps</span>}
      </div>
      {error && <p className="error-text">{error}</p>}
      <div className="vocab-controls">
        <div className="diff-tabs">
          {KINDS.map(k => <button key={k.id} className={k.id === kind ? 'active' : ''} onClick={() => setKind(k.id)}>{k.label}</button>)}
        </div>
        <div className="diff-tabs">
          {DIFFS.filter(d => data?.[d]).map(d => <button key={d} className={d === diff ? 'active' : ''} onClick={() => setDiff(d)}>{DIFF_LABEL[d] ?? d}</button>)}
        </div>
        {kind !== 'doubles' && (
          <div className="diff-tabs">
            <button className={hand === 0 ? 'active' : ''} onClick={() => setHand(0)}>Left</button>
            <button className={hand === 1 ? 'active' : ''} onClick={() => setHand(1)}>Right</button>
          </div>
        )}
        <label className="vocab-check">
          <input type="checkbox" checked={crossingOnly} onChange={e => setCrossingOnly(e.target.checked)} />
          {kind === 'doubles' ? 'crossed hands only' : 'on the other side only'} ({crossingCount})
        </label>
      </div>
      <p className="muted vocab-hint">
        {KINDS.find(k => k.id === kind)!.hint} Sorted by how many curated maps use each (at least twice).
        {kind !== 'doubles' && ' Hands are pooled with their mirror image, so Left is Right mirrored.'}
      </p>
      {v && <p className="muted">{shown.length} {kind}{v.stacked && kind === 'stacks' ? ` · ${(v.stacked * 100).toFixed(1)} % of notes sit in stacks` : ''}</p>}
      <div className="vocab-grid">
        {shown.slice(0, limit).map(e => (
          <div key={e.key} className={`vocab-card${e.crossing ? ' crossing' : ''}`} title={e.label}>
            <Diagram e={e} />
            <span className="vocab-label">{e.label}</span>
            <span className="muted vocab-use">{e.use} / {v!.maps} maps{e.crossing ? ' · crosses' : ''}</span>
          </div>
        ))}
      </div>
      {shown.length > limit && <button className="btn btn-secondary vocab-more" onClick={() => setMore({ key: listKey, limit: limit + PAGE })}>Show {Math.min(PAGE, shown.length - limit)} more of {shown.length - limit}</button>}
    </div>
  )
}
