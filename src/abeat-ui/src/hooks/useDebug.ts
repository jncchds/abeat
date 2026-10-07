import { useCallback, useEffect, useState } from 'react'

const KEY = 'abeatDebug'

function initial(): boolean {
  const q = new URLSearchParams(location.search).get('debug')
  if (q !== null) {
    const on = q !== '0' && q !== 'false'
    localStorage.setItem(KEY, String(on))
    return on
  }
  return localStorage.getItem(KEY) === 'true'
}

/** Hidden debug mode: Ctrl+Shift+D, ?debug=1 (?debug=0 to turn off) or the returned toggle; remembered per browser. */
export function useDebug(): [boolean, () => void] {
  const [debug, setDebug] = useState(initial)
  const toggle = useCallback(() => setDebug(d => {
    localStorage.setItem(KEY, String(!d))
    return !d
  }), [])
  useEffect(() => {
    const key = (e: KeyboardEvent) => {
      if (e.ctrlKey && e.shiftKey && e.code === 'KeyD') {
        e.preventDefault()
        toggle()
      }
    }
    document.addEventListener('keydown', key)
    return () => document.removeEventListener('keydown', key)
  }, [toggle])
  return [debug, toggle]
}
