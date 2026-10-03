export function fmtTime(t: number): string {
  const m = Math.floor(t / 60)
  return `${m}:${(t - m * 60).toFixed(1).padStart(4, '0')}`
}

export const fmtNum = (v: number) => (Number.isInteger(v) ? String(v) : v.toFixed(2))

export const diffLabel = (name: string) => (name === 'ExpertPlus' ? 'Expert+' : name)

export const DIFFICULTIES = ['Easy', 'Normal', 'Hard', 'Expert', 'ExpertPlus']

/** Read a CSS custom property so canvases follow the light/dark theme. */
export function cssVar(name: string, fallback: string): string {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim() || fallback
}
