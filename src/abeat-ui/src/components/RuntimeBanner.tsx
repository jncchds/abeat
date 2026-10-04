import { useEffect, useState } from 'react'
import { getConfig, type RuntimeState } from '../api'

/** Shown while the container installs the analysis runtime on first start (or when that failed). */
export default function RuntimeBanner() {
  const [rt, setRt] = useState<RuntimeState | null>(null)
  useEffect(() => {
    let timer = 0
    let stopped = false
    const tick = async () => {
      let next = 15000
      try {
        const r = await getConfig()
        setRt(r.data.runtime ?? null)
        if (r.data.runtime && r.data.runtime.status !== 'ready') next = 3000
      } catch { /* server restarting */ }
      if (!stopped) timer = window.setTimeout(tick, next)
    }
    tick()
    return () => { stopped = true; window.clearTimeout(timer) }
  }, [])
  if (!rt || rt.status === 'ready') return null
  return (
    <div className={`job-banner runtime-banner${rt.status === 'failed' ? ' failed' : ''}`}>
      <span>
        {rt.status === 'failed' ? '⚠' : <span className="spinner" />}
        {rt.message ?? 'Preparing the analysis runtime'}
        {rt.status !== 'failed' && ' — songs added now wait in the queue.'}
      </span>
    </div>
  )
}
