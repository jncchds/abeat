// abeat web UI: song list + upload, timeline, front view, reports and generator settings.
const $ = (id) => document.getElementById(id);
const audio = $('audio');

const COLORS = { red: '#ff4d6d', blue: '#3fa7ff', line: '#2a2f40', muted: '#8a90a6', text: '#e6e8ef', accent: '#b18cff', warn: '#ffc857', bad: '#ff6b6b', bg: '#171a23' };
const SECTION_COLORS = ['#3b4a7a', '#6a3b7a', '#7a5a3b', '#3b7a5e', '#7a3b4a', '#3b6f7a', '#5e7a3b', '#7a3b6f'];
const DIR_VEC = [[0, 1], [0, -1], [-1, 0], [1, 0], [-0.707, 0.707], [0.707, 0.707], [-0.707, -0.707], [0.707, -0.707], [0, 0]];
const ISSUE_COLOR = { Reset: COLORS.bad, VisionBlock: COLORS.warn, Crossover: '#ff9f43', HighCost: COLORS.muted, WallClash: '#ff3df5', BombHit: '#ff3df5' };

const state = {
  songs: [],
  id: null,
  meta: null,
  analysis: null,
  map: null,          // { difficulties: [...] }
  diff: null,         // selected difficulty name
  defaults: null,
  settings: null,
  view: { pxPerSec: 60, start: 0 },
  generating: false,
};

// ---------- API ----------
async function api(path, opts = {}) {
  const res = await fetch(`/api${path}`, opts);
  if (!res.ok) throw new Error(`${res.status} ${await res.text()}`);
  const type = res.headers.get('content-type') || '';
  return type.includes('json') ? res.json() : res.text();
}

// ---------- songs ----------
async function refreshSongs() {
  state.songs = await api('/songs');
  renderSongList();
  const busy = state.songs.some((s) => ['Queued', 'Analyzing', 'Generating'].includes(s.status));
  clearTimeout(refreshSongs.timer);
  refreshSongs.timer = setTimeout(refreshSongs, busy ? 1500 : 10000);
  if (state.id) {
    const m = state.songs.find((s) => s.id === state.id);
    if (m && state.meta && m.status !== state.meta.status) await selectSong(state.id, true);
    else if (m && m.status !== 'Ready') await updateProgress();
  }
}

function renderSongList() {
  const ul = $('songList');
  ul.innerHTML = '';
  for (const s of state.songs) {
    const li = document.createElement('li');
    li.className = s.id === state.id ? 'active' : '';
    li.innerHTML = `<span class="name"></span><span class="sub"><span class="badge ${s.status}">${s.status}</span><span class="artist"></span></span>`;
    li.querySelector('.name').textContent = s.title || s.fileName;
    li.querySelector('.artist').textContent = [s.artist, s.bpm ? `${+s.bpm.toFixed(2)} BPM` : ''].filter(Boolean).join(' · ');
    li.onclick = () => selectSong(s.id);
    ul.appendChild(li);
  }
}

async function upload(file) {
  const fd = new FormData();
  fd.append('file', file);
  fd.append('beats', $('beats').value);
  fd.append('stems', $('stems').checked ? 'true' : 'false');
  const meta = await api('/songs', { method: 'POST', body: fd });
  await refreshSongs();
  await selectSong(meta.id);
}

async function selectSong(id, keepView = false) {
  state.id = id;
  const { meta } = await api(`/songs/${id}`);
  state.meta = meta;
  renderSongList();
  $('empty').hidden = true;
  $('song').hidden = false;
  $('title').textContent = meta.title || meta.fileName;
  $('artist').textContent = meta.artist || '';
  const ready = meta.status === 'Ready';
  $('editor').hidden = !ready;
  $('progress').hidden = ready;
  for (const b of ['download', 'arcviewer']) $(b).toggleAttribute('disabled', !ready);
  if (!ready) {
    $('cover').src = '';
    $('facts').innerHTML = '';
    await updateProgress();
    return;
  }
  const [analysis, map, settings] = await Promise.all([
    api(`/songs/${id}/analysis`), api(`/songs/${id}/map`), api(`/songs/${id}/settings`),
  ]);
  state.analysis = analysis;
  state.map = map;
  state.settings = settings;
  if (!state.map.difficulties.some((d) => d.name === state.diff)) state.diff = state.map.difficulties.at(-1)?.name;
  $('cover').src = `/api/songs/${id}/cover?${Date.now()}`;
  const zipUrl = `/api/songs/${id}/map.zip`;
  $('download').href = zipUrl;
  $('arcviewer').href = `https://allpoland.github.io/ArcViewer/?url=${encodeURIComponent(location.origin + zipUrl)}`;
  if (!keepView || !audio.src.includes(id)) {
    audio.src = `/api/songs/${id}/audio`;
    state.view.start = 0;
  }
  renderFacts();
  renderSettings();
  renderMap();
}

async function updateProgress() {
  const { meta, log } = await api(`/songs/${state.id}`);
  $('statusText').textContent = meta.status === 'Failed' ? `Failed: ${meta.error}` : `${meta.status}…`;
  const pre = $('log');
  pre.textContent = log.join('\n');
  pre.scrollTop = pre.scrollHeight;
}

function renderFacts() {
  const a = state.analysis;
  const t = a.tempo;
  const facts = [
    `<span><b>${+t.bpm.toFixed(2)}</b> BPM</span>`,
    `<span><b>${fmtTime(a.audio.durationSec)}</b></span>`,
    `<span>beats: <b>${t.backend}</b></span>`,
    `<span>onsets: <b>${a.layerSource}</b></span>`,
    `<span>${a.sections.length} sections</span>`,
  ];
  if (!t.stable) facts.push(`<span class="warn" title="Detected beats deviate from a constant tempo; notes may drift">⚠ tempo varies (${t.maxDevMs.toFixed(0)} ms)</span>`);
  $('facts').innerHTML = facts.join('');
}

// ---------- settings ----------
const WEIGHT_MAX = { reset: 100, slowReset: 10, tooFast: 60, crossover: 10, visionBlock: 8 };

function renderSettings() {
  const s = state.settings;
  const form = $('settingsForm');
  form.innerHTML = '';
  const all = ['Easy', 'Normal', 'Hard', 'Expert', 'ExpertPlus'];

  const fsD = fieldset('Difficulties');
  const diffs = document.createElement('div');
  diffs.className = 'diffs';
  for (const d of all) {
    const l = document.createElement('label');
    l.className = 'check';
    l.innerHTML = `<input type="checkbox" ${s.difficulties.includes(d) ? 'checked' : ''}> ${d === 'ExpertPlus' ? 'Expert+' : d}`;
    l.querySelector('input').onchange = (e) => {
      s.difficulties = all.filter((x) => (x === d ? e.target.checked : s.difficulties.includes(x)));
      changed();
    };
    diffs.appendChild(l);
  }
  fsD.appendChild(diffs);
  form.appendChild(fsD);

  const fsG = fieldset('General');
  fsG.appendChild(slider('density', s.density, 0.3, 2, 0.05, (v) => (s.density = v)));
  fsG.appendChild(slider('seed', s.seed, 1, 100, 1, (v) => (s.seed = v)));
  fsG.appendChild(slider('beam width', s.beamWidth, 8, 256, 8, (v) => (s.beamWidth = v)));
  fsG.appendChild(toggle('lights', s.lights, (v) => (s.lights = v)));
  fsG.appendChild(toggle('walls', s.walls, (v) => (s.walls = v)));
  fsG.appendChild(toggle('dodge walls', s.dodgeWalls, (v) => (s.dodgeWalls = v)));
  fsG.appendChild(toggle('crouch walls', s.crouchWalls, (v) => (s.crouchWalls = v)));
  fsG.appendChild(toggle('bombs', s.bombs, (v) => (s.bombs = v)));
  form.appendChild(fsG);

  const fsW = fieldset('Flow weights');
  for (const [k, v] of Object.entries(s.weights)) {
    const max = WEIGHT_MAX[k] ?? Math.max(3, Math.ceil((state.defaults?.settings.weights[k] ?? v) * 3));
    fsW.appendChild(slider(k, v, 0, max, max > 20 ? 1 : 0.05, (x) => (s.weights[k] = x)));
  }
  form.appendChild(fsW);

  const fsL = fieldset(`Onset layers (${state.analysis.layerSource})`);
  for (const k of Object.keys(state.analysis.layers)) {
    fsL.appendChild(slider(k, s.layerWeights[k] ?? 0, 0, 2, 0.05, (x) => (s.layerWeights[k] = x)));
  }
  form.appendChild(fsL);
}

function fieldset(title) {
  const f = document.createElement('fieldset');
  f.innerHTML = `<legend>${title}</legend>`;
  return f;
}

function slider(name, value, min, max, step, set) {
  const row = document.createElement('div');
  row.className = 'row';
  row.innerHTML = `<label title="${name}">${name}</label><input type="range" min="${min}" max="${max}" step="${step}" value="${value}"><output>${fmtNum(value)}</output>`;
  const input = row.querySelector('input');
  input.oninput = () => (row.querySelector('output').textContent = fmtNum(+input.value));
  input.onchange = () => { set(+input.value); changed(); };
  return row;
}

function toggle(name, value, set) {
  const row = document.createElement('div');
  row.className = 'row';
  row.innerHTML = `<label>${name}</label><input type="checkbox" ${value ? 'checked' : ''} style="justify-self:start">`;
  row.querySelector('input').onchange = (e) => { set(e.target.checked); changed(); };
  return row;
}

function changed() {
  if ($('autoRegen').checked) {
    clearTimeout(changed.timer);
    changed.timer = setTimeout(generate, 400);
  }
}

async function generate() {
  if (state.generating) { state.pending = true; return; }
  state.generating = true;
  const btn = $('regen');
  btn.textContent = 'Generating…';
  btn.disabled = true;
  try {
    state.map = await api(`/songs/${state.id}/generate`, {
      method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(state.settings),
    });
    if (!state.map.difficulties.some((d) => d.name === state.diff)) state.diff = state.map.difficulties.at(-1)?.name;
    renderMap();
  } catch (e) {
    alert(`Generation failed: ${e.message}`);
  } finally {
    state.generating = false;
    btn.textContent = 'Generate';
    btn.disabled = false;
    if (state.pending) { state.pending = false; generate(); }
  }
}

// ---------- map views ----------
function currentDiff() {
  return state.map?.difficulties.find((d) => d.name === state.diff);
}

function renderMap() {
  const tabs = $('diffTabs');
  tabs.innerHTML = '';
  const reports = $('reports');
  reports.innerHTML = '';
  for (const d of state.map.difficulties) {
    const b = document.createElement('button');
    b.textContent = label(d.name);
    b.className = d.name === state.diff ? 'active' : '';
    b.onclick = () => { state.diff = d.name; renderMap(); };
    tabs.appendChild(b);

    const r = d.report;
    const card = document.createElement('div');
    card.className = `report ${d.name === state.diff ? 'active' : ''}`;
    const scoreColor = r.flowScore > 85 ? 'var(--ok)' : r.flowScore > 70 ? 'var(--warn)' : 'var(--bad)';
    card.innerHTML = `
      <div class="rname"><span>${label(d.name)}</span><span class="muted">${r.notes} notes</span></div>
      <div class="score" style="color:${scoreColor}" title="Flow score: 100 = perfectly smooth">${r.flowScore.toFixed(1)}</div>
      <dl>
        <dt>NPS</dt><dd>${r.nps.toFixed(2)} (peak ${r.peakNps.toFixed(2)})</dd>
        <dt>resets</dt><dd>${r.resets}${r.bombResets ? ` (+${r.bombResets} bomb)` : ''}</dd>
        <dt>vision blocks</dt><dd>${r.visionBlocks}</dd>
        <dt>crossovers</dt><dd>${r.crossovers}</dd>
        <dt>left / right</dt><dd>${Math.round(r.leftShare * 100)} / ${Math.round((1 - r.leftShare) * 100)}</dd>
        <dt>NJS / JD</dt><dd>${d.njs} / ${d.jumpDistance.toFixed(1)}</dd>
        <dt>walls / lights</dt><dd>${d.walls.length}${r.wallClashes ? ` (${r.wallClashes} clash)` : ''} / ${r.lights}</dd>
        <dt>dots / bombs</dt><dd>${d.notes.filter((n) => n.d === 8).length} / ${d.bombs.length}${r.bombHits ? ` (${r.bombHits} hit)` : ''}</dd>
      </dl>`;
    card.onclick = () => { state.diff = d.name; renderMap(); };
    reports.appendChild(card);
  }
  renderIssues();
  draw();
}

function renderIssues() {
  const d = currentDiff();
  const ul = $('issueList');
  ul.innerHTML = '';
  const issues = d?.report.issues ?? [];
  $('issueCount').textContent = issues.length ? `(${issues.length})` : '— none';
  for (const i of issues.slice(0, 300)) {
    const li = document.createElement('li');
    li.style.borderLeft = `3px solid ${ISSUE_COLOR[i.kind]}`;
    li.textContent = `${fmtTime(beatToSec(i.b))} ${i.hand === 0 ? 'L' : 'R'} ${i.kind}`;
    li.onclick = () => seek(beatToSec(i.b) - 1);
    ul.appendChild(li);
  }
}

const beatToSec = (b) => (b * 60) / state.analysis.tempo.bpm;
const secToBeat = (t) => (t * state.analysis.tempo.bpm) / 60;

// ---------- timeline ----------
const tl = $('timeline');
const LAYOUT = { sections: [0, 20], energy: [22, 62], layersTop: 64, layerH: 9, lanesTop: 110, laneH: 15, issuesH: 14 };

function draw() {
  if (!state.analysis) return;
  const dpr = window.devicePixelRatio || 1;
  const w = tl.clientWidth, h = tl.clientHeight;
  if (tl.width !== w * dpr || tl.height !== h * dpr) { tl.width = w * dpr; tl.height = h * dpr; }
  const g = tl.getContext('2d');
  g.setTransform(dpr, 0, 0, dpr, 0, 0);
  g.clearRect(0, 0, w, h);

  const a = state.analysis;
  const { pxPerSec } = state.view;
  const t0 = state.view.start, t1 = t0 + w / pxPerSec;
  const X = (t) => (t - t0) * pxPerSec;
  const layers = Object.keys(a.layers);
  const lanesTop = LAYOUT.layersTop + layers.length * LAYOUT.layerH + 8;
  const lanesBottom = lanesTop + 12 * LAYOUT.laneH;

  // sections
  const labels = [...new Set(a.sections.map((s) => s.label))];
  for (const s of a.sections) {
    if (s.end < t0 || s.start > t1) continue;
    g.fillStyle = SECTION_COLORS[labels.indexOf(s.label) % SECTION_COLORS.length];
    g.globalAlpha = 0.35 + 0.65 * s.energy;
    g.fillRect(X(s.start), 0, (s.end - s.start) * pxPerSec - 1, 20);
    g.globalAlpha = 1;
    g.fillStyle = COLORS.text;
    g.font = '11px system-ui';
    g.fillText(`${s.label} · ${s.energy.toFixed(2)}`, Math.max(X(s.start), 0) + 4, 14);
  }

  // energy curve
  const e = a.energy;
  g.beginPath();
  g.moveTo(X(t0), 62);
  for (let t = t0; t <= t1; t += Math.max(e.hopSec, 1 / pxPerSec)) {
    const v = e.values[Math.min(e.values.length - 1, Math.max(0, Math.round(t / e.hopSec)))] ?? 0;
    g.lineTo(X(t), 62 - v * 38);
  }
  g.lineTo(X(t1), 62);
  g.fillStyle = 'rgba(177,140,255,.25)';
  g.fill();

  // beat grid
  const bpm = a.tempo.bpm;
  const spb = 60 / bpm;
  const showBeats = spb * pxPerSec > 8;
  const downs = new Set(a.tempo.downbeats.map((d) => Math.round(d / spb)));
  for (let b = Math.floor(t0 / spb); b * spb <= t1; b++) {
    const isBar = downs.has(b);
    if (!isBar && !showBeats) continue;
    g.fillStyle = isBar ? 'rgba(255,255,255,.14)' : 'rgba(255,255,255,.05)';
    g.fillRect(Math.round(X(b * spb)), 22, 1, lanesBottom - 22 + LAYOUT.issuesH);
  }

  // onset layers
  g.font = '10px system-ui';
  layers.forEach((name, i) => {
    const y = LAYOUT.layersTop + i * LAYOUT.layerH;
    g.fillStyle = COLORS.muted;
    g.fillText(name, 2, y + 8);
    for (const o of a.layers[name]) {
      if (o.t < t0 || o.t > t1) continue;
      g.fillStyle = `rgba(230,232,239,${0.15 + 0.85 * o.s})`;
      g.fillRect(X(o.t), y + 1, 2, LAYOUT.layerH - 2);
    }
  });

  // note lanes: rows top layer first (y=2..0), columns x=0..3
  for (let r = 0; r < 12; r++) {
    const y = lanesTop + r * LAYOUT.laneH;
    g.fillStyle = r % 4 === 0 ? 'rgba(255,255,255,.06)' : 'rgba(255,255,255,.02)';
    g.fillRect(0, y, w, 1);
  }
  g.fillStyle = 'rgba(255,255,255,.06)';
  g.fillRect(0, lanesBottom, w, 1);
  g.fillStyle = COLORS.muted;
  ['top', 'mid', 'bot'].forEach((n, i) => g.fillText(n, 2, lanesTop + i * 4 * LAYOUT.laneH + 11));

  const d = currentDiff();
  if (d) {
    for (const wl of d.walls) {
      const ts = beatToSec(wl.b), te = beatToSec(wl.b + wl.d);
      if (te < t0 || ts > t1) continue;
      g.fillStyle = wl.y >= 2 ? 'rgba(255,200,87,.28)' : 'rgba(255,77,109,.22)'; // crouch walls in yellow
      for (let x = wl.x; x < wl.x + wl.w; x++)
        for (let y = Math.max(0, wl.y); y < Math.min(3, wl.y + wl.h); y++)
          g.fillRect(X(ts), lanesTop + ((2 - y) * 4 + x) * LAYOUT.laneH + 1, (te - ts) * pxPerSec, LAYOUT.laneH - 1);
    }
    const size = Math.min(LAYOUT.laneH - 3, Math.max(6, spb * pxPerSec * 0.35));
    for (const n of d.notes) {
      const t = beatToSec(n.b);
      if (t < t0 - 1 || t > t1 + 1) continue;
      const cx = X(t), cy = lanesTop + ((2 - n.y) * 4 + n.x) * LAYOUT.laneH + LAYOUT.laneH / 2;
      drawNote(g, cx, cy, size, n.c, n.d);
    }
    for (const b of d.bombs) {
      const t = beatToSec(b.b);
      if (t < t0 || t > t1) continue;
      g.fillStyle = '#888';
      g.beginPath();
      g.arc(X(t), lanesTop + ((2 - b.y) * 4 + b.x) * LAYOUT.laneH + LAYOUT.laneH / 2, size / 2.5, 0, 7);
      g.fill();
    }
    for (const i of d.report.issues) {
      const t = beatToSec(i.b);
      if (t < t0 || t > t1) continue;
      g.fillStyle = ISSUE_COLOR[i.kind];
      const x = X(t);
      g.beginPath();
      g.moveTo(x, lanesBottom + 3);
      g.lineTo(x - 4, lanesBottom + 12);
      g.lineTo(x + 4, lanesBottom + 12);
      g.fill();
    }
  }

  // playhead
  const ph = X(audio.currentTime || 0);
  g.fillStyle = COLORS.accent;
  g.fillRect(ph, 0, 2, h);
  drawFront();
}

function drawNote(g, cx, cy, size, color, dir) {
  g.fillStyle = color === 0 ? COLORS.red : COLORS.blue;
  const r = size / 2;
  g.beginPath();
  g.roundRect(cx - r, cy - r, size, size, 2);
  g.fill();
  g.fillStyle = '#fff';
  g.strokeStyle = '#fff';
  if (dir === 8) {
    g.beginPath();
    g.arc(cx, cy, Math.max(1.2, r * 0.3), 0, 7);
    g.fill();
    return;
  }
  // a bar on the side the saber enters from + the cut direction, like in-game arrows
  const [vx, vy] = DIR_VEC[dir];
  g.lineWidth = Math.max(1.2, size / 7);
  g.beginPath();
  g.moveTo(cx - vx * r * 0.7, cy + vy * r * 0.7);
  g.lineTo(cx + vx * r * 0.7, cy - vy * r * 0.7);
  g.stroke();
  g.beginPath();
  const px = -vy, py = vx;
  g.moveTo(cx + vx * r * 0.95, cy - vy * r * 0.95);
  g.lineTo(cx + vx * r * 0.2 + px * r * 0.5, cy - (vy * r * 0.2 + py * r * 0.5));
  g.lineTo(cx + vx * r * 0.2 - px * r * 0.5, cy - (vy * r * 0.2 - py * r * 0.5));
  g.fill();
}

// ---------- front (player) view ----------
const fv = $('frontView');
function drawFront() {
  const g = fv.getContext('2d');
  const W = fv.width, H = fv.height;
  g.clearRect(0, 0, W, H);
  const cell = 52, ox = (W - cell * 4) / 2, oy = (H - cell * 3) / 2;
  g.strokeStyle = COLORS.line;
  for (let x = 0; x < 4; x++) for (let y = 0; y < 3; y++) g.strokeRect(ox + x * cell + 0.5, oy + y * cell + 0.5, cell - 1, cell - 1);
  const d = currentDiff();
  if (!d || !state.analysis) return;
  const now = secToBeat(audio.currentTime || 0);
  const ahead = 2;
  const visible = d.notes.filter((n) => n.b >= now - 0.15 && n.b <= now + ahead).sort((p, q) => q.b - p.b);
  for (const b of d.bombs.filter((b) => b.b >= now - 0.15 && b.b <= now + ahead)) {
    const k = Math.max(0, Math.min(1, 1 - (b.b - now) / ahead));
    g.globalAlpha = b.b < now ? 0.25 : 0.25 + 0.75 * k;
    g.fillStyle = '#9aa0b5';
    g.beginPath();
    g.arc(ox + b.x * cell + cell / 2, oy + (2 - b.y) * cell + cell / 2, cell * (0.12 + 0.2 * k), 0, 7);
    g.fill();
  }
  for (const n of visible) {
    const k = Math.max(0, Math.min(1, 1 - (n.b - now) / ahead)); // 1 = now
    const size = cell * (0.35 + 0.5 * k);
    g.globalAlpha = n.b < now ? 0.25 : 0.25 + 0.75 * k;
    drawNote(g, ox + n.x * cell + cell / 2, oy + (2 - n.y) * cell + cell / 2, size, n.c, n.d);
  }
  g.globalAlpha = 1;
  g.fillStyle = COLORS.muted;
  g.font = '11px system-ui';
  g.fillText(`beat ${now.toFixed(2)}`, 6, H - 6);
}

// ---------- interaction ----------
let drag = null;
tl.addEventListener('pointerdown', (e) => { drag = { x: e.clientX, start: state.view.start, moved: false }; tl.setPointerCapture(e.pointerId); });
tl.addEventListener('pointermove', (e) => {
  if (!drag) return;
  const dx = e.clientX - drag.x;
  if (Math.abs(dx) > 3) drag.moved = true;
  if (drag.moved) { state.view.start = Math.max(0, drag.start - dx / state.view.pxPerSec); draw(); }
});
tl.addEventListener('pointerup', (e) => {
  if (drag && !drag.moved) {
    const rect = tl.getBoundingClientRect();
    seek(state.view.start + (e.clientX - rect.left) / state.view.pxPerSec);
  }
  drag = null;
});
tl.addEventListener('wheel', (e) => {
  e.preventDefault();
  const rect = tl.getBoundingClientRect();
  if (e.shiftKey || Math.abs(e.deltaX) > Math.abs(e.deltaY)) {
    state.view.start = Math.max(0, state.view.start + (e.deltaX || e.deltaY) / state.view.pxPerSec);
  } else {
    const at = state.view.start + (e.clientX - rect.left) / state.view.pxPerSec;
    state.view.pxPerSec = Math.min(1200, Math.max(4, state.view.pxPerSec * (e.deltaY < 0 ? 1.2 : 1 / 1.2)));
    state.view.start = Math.max(0, at - (e.clientX - rect.left) / state.view.pxPerSec);
  }
  draw();
}, { passive: false });

function seek(t) {
  audio.currentTime = Math.max(0, t);
  draw();
}

$('play').onclick = () => (audio.paused ? audio.play() : audio.pause());
audio.onplay = () => { $('play').textContent = '❚❚'; tick(); };
audio.onpause = () => { $('play').textContent = '▶'; };
$('rate').onchange = (e) => (audio.playbackRate = +e.target.value);
document.addEventListener('keydown', (e) => {
  if (e.code === 'Space' && !['INPUT', 'SELECT', 'TEXTAREA'].includes(document.activeElement?.tagName) && !$('editor').hidden) {
    e.preventDefault();
    $('play').click();
  }
});

function tick() {
  const t = audio.currentTime;
  $('time').textContent = fmtTime(t);
  if (state.analysis) $('beatPos').textContent = `beat ${secToBeat(t).toFixed(2)}`;
  const w = tl.clientWidth / state.view.pxPerSec;
  if ($('follow').checked && (t > state.view.start + w * 0.85 || t < state.view.start)) state.view.start = Math.max(0, t - w * 0.15);
  draw();
  if (!audio.paused) requestAnimationFrame(tick);
}
audio.ontimeupdate = () => { if (audio.paused) tick(); };

$('regen').onclick = generate;
$('resetSettings').onclick = () => { state.settings = structuredClone(state.defaults.settings); renderSettings(); changed(); };
$('reanalyze').onclick = async () => {
  await api(`/songs/${state.id}/reanalyze`, {
    method: 'POST', headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ beatBackend: $('beats').value, stems: $('stems').checked }),
  });
  await refreshSongs();
  await selectSong(state.id);
};
$('delete').onclick = async () => {
  if (!confirm('Delete this song and its map?')) return;
  await api(`/songs/${state.id}`, { method: 'DELETE' });
  state.id = null;
  $('song').hidden = true;
  $('empty').hidden = false;
  audio.removeAttribute('src');
  await refreshSongs();
};
$('file').onchange = (e) => e.target.files[0] && upload(e.target.files[0]).catch((err) => alert(err.message));

// drag & drop anywhere
let dragDepth = 0;
window.addEventListener('dragenter', (e) => { if (e.dataTransfer?.types.includes('Files')) { dragDepth++; $('drop').hidden = false; } });
window.addEventListener('dragleave', () => { if (--dragDepth <= 0) { dragDepth = 0; $('drop').hidden = true; } });
window.addEventListener('dragover', (e) => e.preventDefault());
window.addEventListener('drop', (e) => {
  e.preventDefault();
  dragDepth = 0;
  $('drop').hidden = true;
  const f = e.dataTransfer?.files?.[0];
  if (f) upload(f).catch((err) => alert(err.message));
});
window.addEventListener('resize', draw);

// ---------- utils ----------
function fmtTime(t) {
  const m = Math.floor(t / 60);
  return `${m}:${(t - m * 60).toFixed(1).padStart(4, '0')}`;
}
function fmtNum(v) { return Number.isInteger(v) ? String(v) : v.toFixed(2); }
function label(name) { return name === 'ExpertPlus' ? 'Expert+' : name; }

// ---------- boot ----------
(async () => {
  state.defaults = await api('/defaults');
  await refreshSongs();
  const fromHash = location.hash.slice(1);
  const first = state.songs.find((s) => s.id === fromHash) ?? state.songs[0];
  if (first) await selectSong(first.id);
})();
window.addEventListener('hashchange', () => location.hash && selectSong(location.hash.slice(1)));
