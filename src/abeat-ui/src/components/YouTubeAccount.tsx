import { useEffect, useState } from 'react'
import { getConfig } from '../api'
import { desktop } from '../desktop'

const load = () => Promise.all([desktop?.youtubeStatus(), getConfig().then(r => r.data.youtubeCookies).catch(() => undefined)])

/** The YouTube sign-in as the manual shows it: what it is for, whether it is set up, and (in the desktop
 *  app) the buttons to sign in and out. */
export default function YouTubeAccount() {
  const [signedIn, setSignedIn] = useState<boolean | null>(null)
  const [cookies, setCookies] = useState<boolean | null>(null)
  const [busy, setBusy] = useState(false)

  const apply = ([s, c]: Awaited<ReturnType<typeof load>>) => {
    setSignedIn(s?.signedIn ?? null)
    setCookies(c ?? null)
  }
  const refresh = () => load().then(apply)
  useEffect(() => { load().then(apply) }, [])

  const act = async (f: () => Promise<unknown>) => {
    setBusy(true)
    try { await f() } finally {
      setBusy(false)
      await refresh()
    }
  }

  return (
    <div className="card youtube-signin">
      <strong>YouTube sign-in for downloads</strong>
      <p>
        Lets ABeat download YouTube links as you, for networks where YouTube refuses anonymous downloads.
        You sign in on YouTube's own page; ABeat keeps only the sign-in cookies, in <code>data/cookies.txt</code>.
      </p>
      {desktop ? (
        <>
          <p>
            Status: {signedIn == null ? '…' : signedIn ? <b>signed in</b> : 'not signed in'}
            {signedIn === false && cookies && ' (a cookies.txt you added yourself is in use)'}
          </p>
          <div className="youtube-signin-actions">
            <button onClick={() => act(() => desktop!.youtubeSignIn())} disabled={busy}>
              {busy ? 'Waiting for the YouTube window…' : signedIn ? 'Sign in again' : 'Sign in to YouTube'}
            </button>
            {signedIn && <button className="btn-secondary" onClick={() => act(() => desktop!.youtubeSignOut())} disabled={busy}>Sign out of YouTube</button>}
          </div>
        </>
      ) : (
        <p>
          Status: {cookies == null ? '…' : cookies ? <b>cookies.txt found</b> : 'no cookies.txt'}.
          {' '}The sign-in button is in the desktop app; here, add the file by hand as described below.
        </p>
      )}
    </div>
  )
}
