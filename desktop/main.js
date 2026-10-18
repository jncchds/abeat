// ABeat desktop app: a window around the same server the Docker image runs. It starts the bundled,
// self-contained ASP.NET Core server (resources/server) on a free localhost port and, in the background,
// docker/provision.py (resources/provision), which installs Python + the analysis worker with uv
// (resources/uv). The UI shows the runtime's progress, as in the container.
//
// Everything the app writes lives in three folders next to it (the Windows .exe, the Linux AppImage, the
// macOS ABeat.app):
//   data/     songs, analyses, generated maps, playlists, settings
//   models/   ML models, downloaded on first use
//   runtime/  Python, the worker environment, the worker source, ArcViewer, logs
// When that folder is not writable (a mounted .dmg, /Applications, macOS App Translocation) they go to the
// per-user app data folder instead; ABEAT_HOME overrides both.
const { app, BrowserWindow, Menu, dialog, ipcMain, shell } = require('electron')
const { spawn, spawnSync } = require('node:child_process')
const fs = require('node:fs')
const net = require('node:net')
const path = require('node:path')
const youtube = require('./youtube')

const isWin = process.platform === 'win32'
const isMac = process.platform === 'darwin'

// Ubuntu 23.10+ blocks the unprivileged user namespaces Chromium's sandbox needs for AppImages (the app's
// windows show its own localhost server, and youtube.com / Google's sign-in in the YouTube window)
if (process.env.APPIMAGE) app.commandLine.appendSwitch('no-sandbox')

const resources = app.isPackaged ? process.resourcesPath : path.join(__dirname, 'stage')
const children = []
let mainWindow = null
let origin = null

function writable(dir) {
  try {
    fs.mkdirSync(dir, { recursive: true })
    fs.accessSync(dir, fs.constants.W_OK)
    return true
  } catch {
    return false
  }
}

function homeDir() {
  if (process.env.ABEAT_HOME) return path.resolve(process.env.ABEAT_HOME)
  if (!app.isPackaged) return path.join(__dirname, 'home')
  const exe = app.getPath('exe')
  const dir = process.env.PORTABLE_EXECUTABLE_DIR  // Windows portable .exe (the real one, not its temp copy)
    ?? (process.env.APPIMAGE && path.dirname(process.env.APPIMAGE))
    ?? (isMac ? path.dirname(path.resolve(exe, '../../..')) : path.dirname(exe))
  const appFolders = ['/Applications', path.join(app.getPath('home'), 'Applications')]
  const unsuitable = dir.includes('/AppTranslocation/') || dir.startsWith('/Volumes/') || appFolders.includes(dir)
  return !unsuitable && writable(dir) ? dir : app.getPath('userData')
}

const home = homeDir()
const dirs = {
  data: path.join(home, 'data'),
  models: path.join(home, 'models'),
  runtime: path.join(home, 'runtime'),
}
const logs = path.join(dirs.runtime, 'logs')
const cookiesFile = path.join(dirs.data, 'cookies.txt')

function freePort() {
  return new Promise((resolve, reject) => {
    const srv = net.createServer()
    srv.unref()
    srv.on('error', reject)
    srv.listen(0, '127.0.0.1', () => {
      const { port } = srv.address()
      srv.close(() => resolve(port))
    })
  })
}

function copyDir(from, to) {
  fs.rmSync(to, { recursive: true, force: true })
  fs.cpSync(from, to, { recursive: true, filter: src => !src.includes('__pycache__') })
}

/** The worker source goes into runtime/analysis (uv installs it editable, and the app bundle itself is
 *  read-only: a mounted AppImage, a signed .app), refreshed when the app version changes. */
function syncWorkerSource(version) {
  const dest = path.join(dirs.runtime, 'analysis')
  const stamp = path.join(dest, '.app-version')
  if (fs.existsSync(stamp) && fs.readFileSync(stamp, 'utf8') === version) return dest
  fs.mkdirSync(dest, { recursive: true })
  for (const f of ['pyproject.toml', 'uv.lock', '.python-version'])
    fs.copyFileSync(path.join(resources, 'analysis', f), path.join(dest, f))
  copyDir(path.join(resources, 'analysis', 'src'), path.join(dest, 'src'))
  fs.writeFileSync(stamp, version)
  return dest
}

function childEnv(analysisDir) {
  const m = dirs.models, r = dirs.runtime
  const pathKey = Object.keys(process.env).find(k => k.toUpperCase() === 'PATH') ?? 'PATH'
  return {
    ...process.env,
    [pathKey]: path.join(resources, 'uv') + path.delimiter + (process.env[pathKey] ?? ''),
    ABEAT_DATA: dirs.data,
    ABEAT_RUNTIME: r,
    ABEAT_ANALYSIS_DIR: analysisDir,
    ABEAT_RUNTIME_STATUS: path.join(r, 'status.json'),
    ABEAT_ARCVIEWER_DIR: path.join(r, 'arcviewer'),
    ABEAT_PARENT_PID: String(process.pid),
    ABEAT_DESKTOP: '1',
    TORCH_HOME: path.join(m, 'torch'),
    HF_HOME: path.join(m, 'hf'),
    XDG_CACHE_HOME: path.join(m, 'cache'),
    ABEAT_SEPARATOR_MODELS: path.join(m, 'separator'),
    NUMBA_CACHE_DIR: path.join(r, 'numba'),
    UV_PYTHON_INSTALL_DIR: path.join(r, 'python'),
    UV_CACHE_DIR: path.join(r, 'uv-cache'),
    UV_PYTHON_PREFERENCE: 'only-managed',
    PYTHONUTF8: '1',
  }
}

function run(name, exe, args, opts) {
  const log = fs.createWriteStream(path.join(logs, `${name}.log`))
  // own process group on Linux/macOS, so stopping it also stops what it started (uv, Python, the worker)
  const child = spawn(exe, args, { ...opts, detached: !isWin, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] })
  child.stdout.pipe(log)
  child.stderr.pipe(log)
  child.on('error', e => log.write(`\n[abeat] failed to start ${exe}: ${e.message}\n`))
  children.push(child)
  return child
}

function stop(child) {
  if (child.exitCode !== null || child.signalCode !== null || !child.pid) return
  if (isWin) spawnSync('taskkill', ['/pid', String(child.pid), '/T', '/F'], { windowsHide: true })
  else try { process.kill(-child.pid, 'SIGTERM') } catch { /* already gone */ }
}

async function waitHealthy(url, server) {
  for (let i = 0; i < 600; i++) {
    if (server.exitCode !== null) throw new Error(`the server exited (code ${server.exitCode})`)
    try {
      if ((await fetch(url + '/healthz')).ok) return
    } catch { /* not listening yet */ }
    await new Promise(r => setTimeout(r, 100))
  }
  throw new Error('the server did not start within 60 s')
}

async function start() {
  for (const d of [...Object.values(dirs), logs]) fs.mkdirSync(d, { recursive: true })
  const version = fs.readFileSync(path.join(resources, 'VERSION'), 'utf8').trim()
  const analysisDir = syncWorkerSource(version)
  const env = childEnv(analysisDir)

  fs.rmSync(env.ABEAT_RUNTIME_STATUS, { force: true })
  const uv = path.join(resources, 'uv', isWin ? 'uv.exe' : 'uv')
  run('provision', uv, ['run', '--no-project', '--python', '3.12', path.join(resources, 'provision', 'provision.py')],
    { env, cwd: dirs.runtime })

  const port = await freePort()
  // "localhost", not 127.0.0.1: ArcViewer (Unity) only allows plain-http downloads from localhost, so the
  // bundled viewer works without the https listener and its certificate
  origin = `http://localhost:${port}`
  const serverDir = path.join(resources, 'server')
  const server = run('server', path.join(serverDir, isWin ? 'Abeat.Web.exe' : 'Abeat.Web'), [],
    { env: { ...env, ASPNETCORE_URLS: origin, ASPNETCORE_ENVIRONMENT: 'Production' }, cwd: serverDir })
  await waitHealthy(origin, server)
  return origin
}

function createWindow() {
  mainWindow = new BrowserWindow({
    width: 1440,
    height: 920,
    minWidth: 800,
    minHeight: 560,
    title: 'ABeat',
    backgroundColor: '#07070e',
    icon: path.join(__dirname, 'build-resources', 'icon.png'),
    autoHideMenuBar: !isMac,
    webPreferences: { contextIsolation: true, sandbox: true, preload: path.join(__dirname, 'preload.js') },
  })
  mainWindow.loadFile(path.join(__dirname, 'loading.html'))
  // the app's own pages (ArcViewer, previews) open in app windows; anything else in the default browser
  mainWindow.webContents.setWindowOpenHandler(({ url }) => {
    if (origin && url.startsWith(origin)) return { action: 'allow', overrideBrowserWindowOptions: { autoHideMenuBar: !isMac } }
    if (/^https?:/i.test(url)) shell.openExternal(url)
    return { action: 'deny' }
  })
  mainWindow.webContents.on('will-navigate', (e, url) => {
    if (origin && !url.startsWith(origin) && /^https?:/i.test(url)) {
      e.preventDefault()
      shell.openExternal(url)
    }
  })
  mainWindow.on('closed', () => { mainWindow = null })
}

// only the app's own pages may use the bridge (not YouTube, not ArcViewer content from elsewhere)
function fromApp(e) {
  return !!origin && (e.senderFrame?.url ?? '').startsWith(origin + '/')
}
ipcMain.handle('youtube:status', e => fromApp(e) ? youtube.status() : null)
ipcMain.handle('youtube:sign-in', e => fromApp(e) ? youtube.signIn(mainWindow, cookiesFile) : null)
ipcMain.handle('youtube:sign-out', e => fromApp(e) ? youtube.signOut(cookiesFile) : null)

function showManual(anchor = '') {
  if (origin) mainWindow?.loadURL(`${origin}/manual${anchor}`)
}

function buildMenu() {
  const openFolder = (label, dir) => ({ label, click: () => shell.openPath(dir) })
  const folders = [openFolder('Open Data Folder', dirs.data), openFolder('Open Models Folder', dirs.models),
    openFolder('Open Logs Folder', logs)]
  Menu.setApplicationMenu(Menu.buildFromTemplate([
    ...(isMac ? [{ role: 'appMenu' }] : []),
    { label: 'File', submenu: [...folders, { type: 'separator' }, isMac ? { role: 'close' } : { role: 'quit' }] },
    { role: 'editMenu' },
    { role: 'viewMenu' },
    { role: 'windowMenu' },
    {
      role: 'help',
      submenu: [
        { label: 'Manual', accelerator: 'F1', click: () => showManual() },
        { label: 'YouTube Sign-in…', click: () => showManual('#youtube-asks-to-sign-in') },
        { type: 'separator' },
        { label: 'ABeat on GitHub', click: () => shell.openExternal('https://github.com/jncchds/abeat') },
      ],
    },
  ]))
}

if (!app.requestSingleInstanceLock()) {
  app.quit()
} else {
  app.on('second-instance', () => {
    if (!mainWindow) return
    if (mainWindow.isMinimized()) mainWindow.restore()
    mainWindow.focus()
  })
  app.whenReady().then(async () => {
    buildMenu()
    createWindow()
    try {
      const url = await start()
      mainWindow?.loadURL(url)
    } catch (e) {
      dialog.showErrorBox('ABeat could not start', `${e.message}\n\nLogs: ${logs}`)
      app.quit()
    }
  })
  app.on('window-all-closed', () => app.quit())
  app.on('will-quit', () => children.forEach(stop))
}
