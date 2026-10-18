// Builds the ABeat desktop app for the platform it runs on (CI builds each on its own runner):
//   1. the React UI (src/abeat-ui -> src/Abeat.Web/wwwroot)
//   2. the self-contained server (dotnet publish -> stage/server)
//   3. the worker source, docker/provision.py and fetch-arcviewer.sh (-> stage/analysis, stage/provision)
//   4. uv for the target (-> stage/uv, downloaded once into .cache)
//   5. electron-builder: Windows portable .exe, Linux AppImage, macOS .dmg (x64; macOS arm64, as PyTorch
//      ships no Intel-Mac builds)
// Usage: node scripts/build.mjs [--stage-only] [--skip-ui]
import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const UV_VERSION = '0.12.22'
const desktop = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const root = path.resolve(desktop, '..')
const stage = path.join(desktop, 'stage')
const args = new Set(process.argv.slice(2))
const isWin = process.platform === 'win32'

const targets = {
  win32: { rid: 'win-x64', uv: 'x86_64-pc-windows-msvc.zip', builder: ['--win', '--x64'] },
  linux: { rid: 'linux-x64', uv: 'x86_64-unknown-linux-gnu.tar.gz', builder: ['--linux', '--x64'] },
  darwin: { rid: 'osx-arm64', uv: 'aarch64-apple-darwin.tar.gz', builder: ['--mac', '--arm64'] },
}
const target = targets[process.platform]
if (!target) throw new Error(`unsupported platform ${process.platform}`)
const version = fs.readFileSync(path.join(root, 'VERSION'), 'utf8').trim()

function sh(cmd, argv, cwd = root) {
  console.log(`> ${cmd} ${argv.join(' ')}`)
  // npm/npx are .cmd scripts on Windows, which need a shell
  execFileSync(cmd, argv, { cwd, stdio: 'inherit', shell: isWin && /^(npm|npx)$/.test(cmd) })
}

function copy(from, to) {
  fs.cpSync(path.join(root, from), path.join(stage, to), {
    recursive: true,
    filter: src => !/[\\/](__pycache__|\.venv|\.pytest_cache)([\\/]|$)/.test(src),
  })
}

function findFile(dir, name) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name)
    if (e.isFile() && e.name === name) return p
    if (e.isDirectory()) {
      const found = findFile(p, name)
      if (found) return found
    }
  }
  return null
}

async function fetchUv() {
  const cache = path.join(desktop, '.cache', `uv-${UV_VERSION}-${target.uv}`)
  if (!fs.existsSync(cache)) {
    const url = `https://github.com/astral-sh/uv/releases/download/${UV_VERSION}/uv-${target.uv}`
    console.log(`> download ${url}`)
    const res = await fetch(url)
    if (!res.ok) throw new Error(`${url}: HTTP ${res.status}`)
    fs.mkdirSync(path.dirname(cache), { recursive: true })
    fs.writeFileSync(cache, Buffer.from(await res.arrayBuffer()))
  }
  const tmp = path.join(desktop, '.cache', 'uv-unpack')
  fs.rmSync(tmp, { recursive: true, force: true })
  fs.mkdirSync(tmp, { recursive: true })
  sh('tar', ['-xf', cache, '-C', tmp])  // bsdtar on Windows also reads .zip
  const exe = isWin ? 'uv.exe' : 'uv'
  fs.mkdirSync(path.join(stage, 'uv'), { recursive: true })
  fs.copyFileSync(findFile(tmp, exe), path.join(stage, 'uv', exe))
  if (!isWin) fs.chmodSync(path.join(stage, 'uv', exe), 0o755)
  fs.rmSync(tmp, { recursive: true, force: true })
}

/** Licence texts of what the app bundles besides Electron (whose LICENSE and LICENSES.chromium.html
 *  electron-builder adds): the .NET runtime of the self-contained server and uv. Credits: docs/MANUAL.md. */
async function licenses() {
  const dir = path.join(stage, 'licenses')
  fs.mkdirSync(dir, { recursive: true })
  const runtimes = execFileSync('dotnet', ['--list-runtimes'], { encoding: 'utf8' })
  const shared = /Microsoft\.NETCore\.App \S+ \[(.+)\]/.exec(runtimes)?.[1]
  const dotnetRoot = shared && path.resolve(shared, '..', '..')
  for (const [from, to] of [['LICENSE.txt', 'dotnet-LICENSE.txt'], ['ThirdPartyNotices.txt', 'dotnet-ThirdPartyNotices.txt']]) {
    const f = dotnetRoot && path.join(dotnetRoot, from)
    if (f && fs.existsSync(f)) fs.copyFileSync(f, path.join(dir, to))
    else console.warn(`warning: .NET ${from} not found next to ${shared}; not bundled`)
  }
  for (const f of ['LICENSE-MIT', 'LICENSE-APACHE']) {
    const url = `https://raw.githubusercontent.com/astral-sh/uv/${UV_VERSION}/${f}`
    const res = await fetch(url)
    if (!res.ok) throw new Error(`${url}: HTTP ${res.status}`)
    fs.writeFileSync(path.join(dir, `uv-${f}.txt`), await res.text())
  }
}

fs.rmSync(stage, { recursive: true, force: true })
fs.mkdirSync(stage, { recursive: true })

if (!args.has('--skip-ui')) sh('npm', ['run', 'build'], path.join(root, 'src', 'abeat-ui'))
sh('dotnet', ['publish', 'src/Abeat.Web/Abeat.Web.csproj', '-c', 'Release', '-r', target.rid, '--self-contained',
  '-p:DebugType=none', '-p:GenerateDocumentationFile=false', '-o', path.join(stage, 'server')])
if (!fs.existsSync(path.join(stage, 'server', 'wwwroot', 'index.html')))
  throw new Error('the published server has no wwwroot/index.html (build the UI first)')
// a machine-local viewer in src/abeat-ui/public/bsviewer holds game files that must never be redistributed
fs.rmSync(path.join(stage, 'server', 'wwwroot', 'bsviewer'), { recursive: true, force: true })

for (const f of ['pyproject.toml', 'uv.lock', '.python-version', 'src']) copy(`analysis/${f}`, `analysis/${f}`)
copy('docker/provision.py', 'provision/provision.py')
copy('scripts/fetch-arcviewer.sh', 'provision/fetch-arcviewer.sh')
fs.writeFileSync(path.join(stage, 'VERSION'), version + '\n')
await fetchUv()
await licenses()

if (args.has('--stage-only')) {
  console.log(`staged in ${stage}; run "npm start" to try it`)
} else {
  sh('npx', ['electron-builder', ...target.builder, '--publish', 'never', `-c.extraMetadata.version=${version}`], desktop)
}
