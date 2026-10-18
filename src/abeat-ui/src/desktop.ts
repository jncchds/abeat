/** What the desktop app (desktop/preload.js) offers the page; undefined in a normal browser. */
interface AbeatDesktop {
  youtubeStatus(): Promise<{ signedIn: boolean }>
  youtubeSignIn(): Promise<{ signedIn: boolean }>
  youtubeSignOut(): Promise<{ signedIn: boolean }>
}

export const desktop: AbeatDesktop | undefined = (window as unknown as { abeatDesktop?: AbeatDesktop }).abeatDesktop

/** yt-dlp's answer when YouTube wants a signed-in browser (see analysis/.../fetch.py). */
export const isYouTubeBotCheck = (error?: string | null) => !!error && /not a bot|--cookies/i.test(error)
