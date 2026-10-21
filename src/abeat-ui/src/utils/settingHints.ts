/** Hover texts for the generator settings: what each one does and what its value means. */

export const GENERAL_HINTS: Record<string, string> = {
  density: 'Multiplies the notes per second of every difficulty. 1 = the difficulty\'s usual density, 2 = twice as many notes '
    + '(still capped by its max notes/s and minimum gaps), 0.5 = half.',
  seed: 'Variation: each seed gives a different but equally valid map; the same seed always gives the same map.',
  beamWidth: 'How many partial maps the planner keeps while it places notes. Higher = smoother flow but slower generation; '
    + '64 is plenty, 8-16 is a quick draft.',
  tapPriority: 'How strongly your tap-along taps claim notes. 1 = every tap gets its note before any untapped sound of its bar, '
    + 'and bars with more taps than usual get extra notes (up to the difficulty\'s max notes/s). Between 0 and 1 strong music can win '
    + 'over a tap; above 1 even taps a little off the grid win. 0 = taps ignored. No effect on songs without taps.',
}

export const TOGGLE_HINTS: Record<string, string> = {
  lights: 'Write a lightshow (classic events, or the environment\'s light groups) that follows sections, beats and the melody.',
  bombs: 'Reset bombs where a hand would otherwise swing the same way twice (Normal and up) and accent bombs on strong single hits (Hard and up).',
  walls: 'Any walls at all: side walls in calm sections, dodge walls in gaps, rhythm walls; off removes every wall.',
  dodgeWalls: 'Single-lane walls in the centre during note-free gaps, so you lean aside (Normal and up).',
  crouchWalls: 'Full-width overhead walls right before drops, so you duck (Hard and up). Off by default: few curated maps use them.',
  rhythmWalls: 'Short walls in the outer lanes on the strongest kicks and snares of loud sections; a saber resting there buzzes on each hit.',
  arcs: 'Arcs (sliders) from a held melody note to the same hand\'s next note.',
  chains: 'Chains on notes followed by a roll or stutter too fast for single notes (Hard and up).',
  angleOffsets: 'Cut angles lean a little (15°) with the melody\'s pitch direction (Normal and up).',
  dropPause: 'A short note-free breath (about 1-2 beats) before each drop, then a double on the drop.',
}

/** Flow weights are costs the planner adds up while it places notes: higher = it avoids that more, 0 = ignored. */
export const FLOW_HINTS: Record<string, string> = {
  reset: 'Swinging the same way twice in a row without time to reset the arm. High so it only happens when nothing else fits.',
  slowReset: 'Same-way swing after a long pause (allowed, mildly discouraged).',
  angle: 'How far a swing departs from a clean reversal of the hand\'s previous one.',
  travel: 'Distance the saber has to move from where it ended to where the next cut starts.',
  visionBlock: 'Notes in the middle-row centre, which hide the notes behind them.',
  crossover: 'A hand reaching past where the other hand just cut (arms crossing).',
  handClash: 'One hand cutting the cell the other hand cut a moment ago.',
  tooFast: 'The same hand twice in a row faster than the difficulty\'s hand gap allows.',
  burst: 'Quick one-hand flicks below the hand gap (down to the burst gap). 0 = left to the effort and strain priors; higher = fewer.',
  horizontal: 'Extra cost for sideways cuts, on top of how often human maps use them.',
  styleCell: 'Grid cells human maps rarely use (cost = how unlikely the cell is in human maps of this difficulty).',
  styleDirection: 'Cut directions human maps rarely use at this cell.',
  figure: 'A note (hand + cell + direction) or double shape that human maps of this difficulty never use.',
  move: 'A move from the hand\'s previous note to this one that human maps of this difficulty never make.',
  streak: 'How closely each hand follows streaks from human maps (runs of 4-16 of its notes): how often it continues a known streak is matched to human maps of this difficulty, with a little extra for 6+ note streaks. 0 = ignore streaks.',
  pairStreak: 'How closely both hands together follow two-hand streaks from human maps (4 notes in a row across both hands, like left-right-left-right, doubles included): how often the map plays a known one is matched to human maps of this difficulty. 0 = ignore them.',
  stackAccent: 'Pulls stacks (one swing through 2-3 lined-up notes) onto strong hits, where curated maps put them.',
  emphasis: 'Strong accents prefer big vertical swings.',
  noise: 'Random jitter for variety; this is what the seed changes. 0 = the same choice whenever costs tie.',
  repeat: 'Repeating the exact same note (hand, position, direction) as last time.',
  stagnation: 'A hand hitting the same cell as its previous notes.',
  target: 'Pull towards the cell each phrase aims at; moves play around the grid and repeated sections reuse patterns.',
  handRole: 'One hand leads the drops and the other the rest, the support hand takes bar downbeats: the cost of a single note on the "wrong" hand.',
  dynamics: 'Swing size follows loudness: loud hits pull to the outer columns and big moves, soft ones stay small and central.',
  movementStyle: 'Match how far human maps turn and travel between swings; lower = smaller, more static movement.',
  effort: 'Match how strenuous human maps are (swings per second per hand), mostly by choosing which hand takes a note.',
  strain: 'Moves more strenuous than the difficulty\'s human ceiling.',
  repetition: 'Returning sections (same label) bring back the patterns of their first time: the bonus for matching them. 0 = off.',
}

export const FLOW_INTRO = 'Cost weight: higher = the planner avoids this more, 0 = not considered.'

export const LAYER_HINTS: Record<string, string> = {
  vocals: 'Syllables of the separated vocals.',
  drums: 'Kicks, snares and hats of the separated drums (hats count 0.6 of that).',
  other: 'The separated "other" stem: synths, guitars, strings, piano.',
  bass: 'The separated bass.',
  mix: 'Onsets of the full mix; fills gaps the stems miss.',
  melody: 'The one lead melody line across the vocals and other stems (whoever carries it); also gives notes the melody\'s pitch and held length.',
  full: 'Onsets of the full mix (no stem separation).',
  low: 'Low band: kicks and bass (no stem separation).',
  mid: 'Mid band: mostly vocals and leads (no stem separation).',
  high: 'High band: hats and cymbals (no stem separation).',
}

export const LAYER_INTRO = 'How much this onset layer counts when choosing which sounds become notes. 0 = ignored; '
  + 'each hit counts its rank within its layer (0.25-1) times this weight, so at 1.5 vs 0.75 a hit of this layer counts twice as much as an equally ranked hit of the other.'

/** A hover text from a hint plus the default value. */
export function hintText(hint: string | undefined, def?: number | boolean, intro?: string) {
  const parts = [intro, hint].filter(Boolean)
  if (def !== undefined) parts.push(`Default: ${typeof def === 'boolean' ? (def ? 'on' : 'off') : def}.`)
  return parts.join('\n')
}
