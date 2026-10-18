import { useState } from 'react'
import { Link } from 'react-router-dom'
import { desktop } from '../desktop'

/** Shown when a download failed on YouTube's "Sign in to confirm you're not a bot": in the desktop app a
 *  sign-in window (its cookies go to data/cookies.txt), elsewhere the manual's cookies.txt steps. */
export default function YouTubeSignIn({ onRetry }: { onRetry: () => Promise<void> }) {
  const [state, setState] = useState<'idle' | 'open' | 'signed-in' | 'not-signed-in'>('idle')
  const [busy, setBusy] = useState(false)

  const signIn = async () => {
    if (!desktop) return
    setState('open')
    const s = await desktop.youtubeSignIn()
    setState(s.signedIn ? 'signed-in' : 'not-signed-in')
  }
  const retry = async () => {
    setBusy(true)
    try { await onRetry() } finally { setBusy(false) }
  }

  return (
    <div className="card youtube-signin">
      <strong>YouTube wants you to sign in</strong>
      <p>
        From some networks YouTube only lets signed-in users download. ABeat can use your YouTube sign-in for
        downloads; it never sees your password. {' '}
        <Link to="/manual#youtube-asks-to-sign-in">How this works</Link>
      </p>
      {desktop ? (
        <>
          {state === 'idle' && <p>Click <b>Sign in to YouTube</b>, sign in in the window that opens, then close that window.</p>}
          {state === 'open' && <p><span className="spinner" /> Sign in in the YouTube window, then close it to continue.</p>}
          {state === 'signed-in' && <p>Signed in. Retry the download now.</p>}
          {state === 'not-signed-in' && <p>The window was closed before the sign-in finished. Try again; close the window only once YouTube shows your account picture.</p>}
          <div className="youtube-signin-actions">
            {state !== 'signed-in' && <button onClick={signIn} disabled={state === 'open'}>Sign in to YouTube</button>}
            <button className={state === 'signed-in' ? '' : 'btn-secondary'} onClick={retry} disabled={busy || state === 'open'}>Retry download</button>
          </div>
        </>
      ) : (
        <>
          <p>
            Save the cookies of a browser signed in to YouTube as <code>cookies.txt</code> in ABeat's data folder
            (Docker: <code>./docker-data/data/cookies.txt</code>), then retry. The <Link to="/manual#youtube-asks-to-sign-in">manual</Link> has
            the steps.
          </p>
          <div className="youtube-signin-actions">
            <button onClick={retry} disabled={busy}>Retry download</button>
          </div>
        </>
      )}
    </div>
  )
}
