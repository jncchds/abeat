// In-app YouTube sign-in for downloads that YouTube answers with "Sign in to confirm you're not a bot".
// The sign-in window uses its own persistent browser session; when it closes, that session's youtube.com
// cookies are written to data/cookies.txt, which the worker hands to yt-dlp (analysis/.../fetch.py).
// yt-dlp keeps the file up to date as YouTube rotates the cookies, so the next sign-in window first loads
// them back from the file instead of resuming with stale ones.
const { BrowserWindow, session } = require('electron')
const fs = require('node:fs')

const PARTITION = 'persist:youtube'
const MARK = '# written by ABeat from its YouTube sign-in window'
// cookies that only exist for a signed-in account
const SIGNED_IN = new Set(['SAPISID', '__Secure-3PAPISID', '__Secure-1PSID', 'LOGIN_INFO'])
const isYouTube = domain => /(^|\.)youtube\.com$/i.test(domain.replace(/^\./, ''))

function ses() {
  const s = session.fromPartition(PARTITION)
  // Google refuses sign-ins from browsers that announce themselves as embedded (Electron), so this
  // session looks like the Chrome it is built on
  const platform = process.platform === 'win32' ? 'Windows NT 10.0; Win64; x64'
    : process.platform === 'darwin' ? 'Macintosh; Intel Mac OS X 10_15_7' : 'X11; Linux x86_64'
  s.setUserAgent(`Mozilla/5.0 (${platform}) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/${process.versions.chrome} Safari/537.36`)
  return s
}

async function youtubeCookies() {
  return (await ses().cookies.get({})).filter(c => isYouTube(c.domain ?? ''))
}

async function status() {
  return { signedIn: (await youtubeCookies()).some(c => SIGNED_IN.has(c.name)) }
}

/** Netscape cookies.txt, the format yt-dlp reads with --cookies. */
function toNetscape(cookies) {
  const lines = ['# Netscape HTTP Cookie File', MARK, '']
  for (const c of cookies) {
    const domain = c.hostOnly ? c.domain.replace(/^\./, '') : (c.domain.startsWith('.') ? c.domain : '.' + c.domain)
    lines.push([domain, c.hostOnly ? 'FALSE' : 'TRUE', c.path || '/', c.secure ? 'TRUE' : 'FALSE',
      Math.floor(c.expirationDate ?? 0), c.name, c.value].join('\t'))
  }
  return lines.join('\n') + '\n'
}

function fromNetscape(text) {
  const out = []
  for (const raw of text.split(/\r?\n/)) {
    // curl and yt-dlp mark HttpOnly cookies with this prefix on an otherwise commented line
    const line = raw.startsWith('#HttpOnly_') ? raw.slice('#HttpOnly_'.length) : raw
    if (!line.trim() || line.startsWith('#')) continue
    const [domain, , path, secure, expires, name, ...value] = line.split('\t')
    if (!name || !isYouTube(domain)) continue
    out.push({ domain, path, secure: secure === 'TRUE', expires: Number(expires), name, value: value.join('\t'),
      httpOnly: raw.startsWith('#HttpOnly_') })
  }
  return out
}

/** Loads the cookies yt-dlp has kept current into the session, so the window resumes that sign-in. */
async function importFile(file) {
  let text
  try { text = fs.readFileSync(file, 'utf8') } catch { return }
  if (!text.includes(MARK)) return  // a file the user put there: leave the session alone
  const s = ses()
  for (const c of fromNetscape(text)) {
    const host = c.domain.replace(/^\./, '')
    try {
      await s.cookies.set({
        url: `https://${host}${c.path || '/'}`, name: c.name, value: c.value, path: c.path || '/',
        secure: c.secure, httpOnly: c.httpOnly, ...(c.domain.startsWith('.') ? { domain: c.domain } : {}),
        ...(c.expires > 0 ? { expirationDate: c.expires } : {}),
      })
    } catch { /* a cookie Chromium rejects (expired, __Host- rules): skip it */ }
  }
}

async function exportFile(file) {
  const cookies = await youtubeCookies()
  if (!cookies.some(c => SIGNED_IN.has(c.name))) return false
  fs.writeFileSync(file, toNetscape(cookies))
  return true
}

let open = null

/** Opens the sign-in window (or focuses it); resolves with the status once it is closed. */
async function signIn(parent, file) {
  if (open) {
    open.window.focus()
    return open.done
  }
  await importFile(file)
  const window = new BrowserWindow({
    parent, width: 1100, height: 820, title: 'Sign in to YouTube – close this window when you are signed in',
    autoHideMenuBar: true,
    webPreferences: { partition: PARTITION, contextIsolation: true, sandbox: true },
  })
  window.on('page-title-updated', e => e.preventDefault())
  // sign-in popups stay in this session; nothing else opens from here
  window.webContents.setWindowOpenHandler(({ url }) => {
    if (/^https:\/\/([a-z0-9-]+\.)*(google|youtube)\.com\//i.test(url)) window.loadURL(url)
    return { action: 'deny' }
  })
  window.loadURL('https://www.youtube.com/')
  const done = new Promise(resolve => window.on('closed', async () => {
    open = null
    try { await exportFile(file) } catch { /* the status below says whether it worked */ }
    resolve(await status())
  }))
  open = { window, done }
  return done
}

/** Forgets the sign-in: clears the session and removes cookies.txt when this app wrote it. */
async function signOut(file) {
  open?.window.close()
  await ses().clearStorageData()
  try {
    if (fs.readFileSync(file, 'utf8').includes(MARK)) fs.rmSync(file)
  } catch { /* no file */ }
  return status()
}

module.exports = { status, signIn, signOut }
