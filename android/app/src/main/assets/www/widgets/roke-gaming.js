import { API, D, LOCALE, api, fmtDur, fmtRate, h, haptic, t, toast } from '../main.js';
import { W } from '../registry.js';
import { every } from './helpers.js';
import { gameInitials } from './steam-library.js';
import { rkPost } from './roke-control.js';

// ---------------------------------------------------------------- RokeBox: Gaming
// The Steam game being played and what its own processes use (CPU, GPU, video memory, RAM, disk), its size on disk,
// and the computer's network while it runs (Windows cannot split network traffic by process without administrator
// rights, so that one is the whole PC's, with the data used since the game started). The agent reads all of it only
// while this widget is on an enabled page; the phone asks every 2 s while the widget is on screen.

const HISTORY = 60; // samples kept for the sparklines (2 min at one every 2 s)

/** "12,3" in the panel's language; "—" for nothing. */
const num = (v, digits = 1) => (v == null || !isFinite(v) ? '—'
  : Number(v).toLocaleString(LOCALE, { minimumFractionDigits: digits, maximumFractionDigits: digits }));

/** Megabytes as "4,7 MB", "820 MB" or "1,4 GB". */
const mbText = (mb) => (mb == null ? '—' : mb >= 1000 ? `${num(mb / 1000, 1)} GB` : `${num(mb, mb < 10 ? 1 : 0)} MB`);

/** An area sparkline from 0 to `max` (or the data's own peak), drawn crisp for the screen's density. */
function spark(canvas, data, color, max) {
  const w = canvas.clientWidth, hh = canvas.clientHeight;
  if (!w || !hh || data.length < 2) return;
  const dpr = window.devicePixelRatio || 1;
  if (canvas.width !== Math.round(w * dpr) || canvas.height !== Math.round(hh * dpr)) {
    canvas.width = Math.round(w * dpr);
    canvas.height = Math.round(hh * dpr);
  }
  const g = canvas.getContext('2d');
  const W2 = canvas.width, H2 = canvas.height;
  g.clearRect(0, 0, W2, H2);
  const top = Math.max(max || 0, ...data, 1e-9);
  // the line spans the whole width once 15 points (30 s) are in, and grows from the right until then
  const step = W2 / (Math.max(data.length, 15) - 1);
  const pts = data.map((v, i) => [W2 - (data.length - 1 - i) * step, H2 - (Math.max(0, v) / top) * (H2 - 3 * dpr) - dpr]);
  const fill = g.createLinearGradient(0, 0, 0, H2);
  fill.addColorStop(0, color.replace('rgb(', 'rgba(').replace(')', ', 0.35)'));
  fill.addColorStop(1, color.replace('rgb(', 'rgba(').replace(')', ', 0)'));
  g.beginPath();
  g.moveTo(pts[0][0], H2);
  pts.forEach(([x, y]) => g.lineTo(x, y));
  g.lineTo(pts[pts.length - 1][0], H2);
  g.closePath();
  g.fillStyle = fill;
  g.fill();
  g.beginPath();
  pts.forEach(([x, y], i) => (i ? g.lineTo(x, y) : g.moveTo(x, y)));
  g.strokeStyle = color;
  g.lineWidth = 1.6 * dpr;
  g.lineJoin = 'round';
  g.stroke();
}

/** A CSS colour variable as "rgb(r, g, b)", which the sparkline fades. */
function cssRgb(el, name, fallback) {
  const probe = h('i');
  probe.style.color = `var(${name}, ${fallback})`;
  el.append(probe);
  const c = getComputedStyle(probe).color || fallback;
  probe.remove();
  return /^rgb\(/.test(c) ? c : fallback;
}

/** The game's cover from Steam's library cache on the computer, over its initials. */
function cover(id, name, cls) {
  const wrap = h('div', cls, h('span', 'gm-initials', gameInitials(name)));
  const img = h('img');
  img.alt = '';
  img.decoding = 'async';
  img.addEventListener('load', () => wrap.classList.add('has-art'), { once: true });
  img.addEventListener('error', () => img.remove(), { once: true });
  img.src = `${API}/steam/cover?id=${encodeURIComponent(id)}`;
  wrap.append(img);
  return wrap;
}

/** One metric tile: label, the game's value, the PC's value under it and a sparkline. */
function metric(cls, label) {
  const val = h('b'), unit = h('small'), sub = h('div', 'gm-sub'), canvas = h('canvas');
  const el = h('div', `gm-metric ${cls}`, h('div', 'gm-label', label), h('div', 'gm-val', val, unit), sub, canvas);
  return { el, val, unit, sub, canvas, hist: [] };
}

W.gaming = {
  needs: ['tick', 'state'],
  create(box, { page, w }) {
    let data = null, failed = false, shownId = null, colors = null;
    const load = every(2000, async () => {
      try { data = await api('/gaming', 'GET', 6000); failed = false; return true; }
      catch (_) { failed = true; return false; }
    });
    // Close the game (off unless this widget allows it in its settings): a first tap arms the key, a second asks the
    // game's windows to close; when it has none that can be asked (it hangs), the key offers Force close instead.
    const wq = 'widget=' + encodeURIComponent(`${page.id}/${w.id}`);
    let canClose = false, closeArmed = 0, closeBusy = false, force = false;
    const loadAllowed = every(60_000, async () => {
      try { canClose = !!(await api('/rk/gaming/allowed?' + wq, 'GET', 6000)).close; return true; }
      catch (_) { return false; }
    }, 15_000);
    async function closeGame() {
      if (closeBusy) return;
      if (closeArmed < Date.now()) { closeArmed = Date.now() + 4000; haptic(); render(); setTimeout(render, 4050); return; }
      closeArmed = 0; closeBusy = true; haptic();
      try {
        await rkPost(`/rk/gaming/close?${wq}${force ? '&force=true' : ''}`);
        toast(t(force ? 'w.rc.game.ended' : 'w.rc.game.closing'));
        force = false;
      } catch (e) {
        if (!force && e.status === 409 && /Force close/.test(e.message)) { force = true; toast(t('w.rc.game.noWindow'), 'warn'); }
        else toast(e.message, 'err');
      } finally { closeBusy = false; render(); }
    }

    // ---- playing: cover, title, chips, four metric tiles and a footer
    const art = h('div', 'gm-art');
    const name = h('div', 'gm-name');
    const status = h('div', 'gm-status');
    const chips = h('div', 'gm-chips');
    const m = {
      cpu: metric('gm-cpu', 'CPU'),
      gpu: metric('gm-gpu', 'GPU'),
      ram: metric('gm-ram', 'RAM'),
      net: metric('gm-net', t('w.gaming.net')),
    };
    const foot = h('div', 'gm-foot');
    const playing = h('div', 'gm-playing', art,
      h('div', 'gm-main', h('div', 'gm-head', name, status, chips), h('div', 'gm-metrics', m.cpu.el, m.gpu.el, m.ram.el, m.net.el), foot));

    // ---- idle: a message and the recently played games
    const idle = h('div', 'gm-idle');
    box.append(playing, idle);

    const chip = (text, cls) => h('span', 'gm-chip' + (cls ? ' ' + cls : ''), text);

    function renderIdle() {
      playing.hidden = true;
      idle.hidden = false;
      if (data && data.enabled && !data.found) {
        idle.replaceChildren(h('div', 'gm-empty', h('div', 'gm-empty-ic', '🎮'), h('b', null, t('w.gaming.noSteam')), h('span', null, data.error || '')));
        return;
      }
      const recent = (data && data.recent) || [];
      const tiles = recent.slice(0, 5).map((g) => h('div', 'gm-tile',
        cover(g.id, g.name, 'gm-tile-art'),
        h('div', 'gm-tile-name', g.name),
        h('div', 'gm-tile-sub', `${num(g.sizeGb, 1)} GB`)));
      idle.replaceChildren(
        h('div', 'gm-idle-head',
          h('div', 'gm-empty-ic', '🎮'),
          h('div', null, h('b', null, failed && !data ? t('w.gaming.offline') : t('w.gaming.idle')), h('span', null, t('w.gaming.idleHint')))),
        tiles.length ? h('div', 'gm-label gm-recent-label', t('w.gaming.recent')) : null,
        tiles.length ? h('div', 'gm-tiles', tiles) : null,
        data && data.installed ? h('div', 'gm-lib', t('w.gaming.library', { n: data.installed, gb: num(data.libraryGb, 0) })) : null);
    }

    function push(hist, v) {
      if (v == null) return; // not measured yet (a rate needs two readings): no dip to 0 in the line
      hist.push(v);
      if (hist.length > HISTORY) hist.shift();
    }

    function renderGame(g, sampled) {
      playing.hidden = false;
      idle.hidden = true;
      if (shownId !== g.id) {
        shownId = g.id;
        art.replaceChildren(cover(g.id, g.name, 'gm-cover'));
        name.textContent = g.name;
        Object.keys(m).forEach((k) => { m[k].hist = []; });
      }
      const pc = (D.state && D.state.pc) || {};
      status.replaceChildren(h('i', 'gm-live'), t('w.gaming.playing'),
        g.startedAt ? h('span', 'gm-dim', ' · ' + t('w.gaming.since', { dur: fmtDur(Date.now() - g.startedAt) })) : null);
      let closeKey = null;
      if (canClose) {
        const armed = closeArmed > Date.now();
        closeKey = h('button', 'gm-close' + (armed ? ' armed' : '') + (force ? ' force' : ''),
          armed ? t('w.rc.kb.again') : t(force ? 'w.rc.game.force' : 'w.rc.game.close'));
        closeKey.type = 'button';
        closeKey.addEventListener('click', closeGame);
      }
      chips.replaceChildren(
        chip('Steam', 'gm-chip-steam'),
        g.sizeGb != null ? chip(t('w.gaming.size', { gb: num(g.sizeGb, 1) })) : null,
        g.drive ? chip(g.driveFreeGb != null ? t('w.gaming.free', { drive: g.drive, gb: num(g.driveFreeGb, 0) }) : g.drive) : null,
        closeKey);

      // CPU: the game's share of the processor; the PC's under it
      m.cpu.val.textContent = num(g.cpu, 0);
      m.cpu.unit.textContent = '%';
      m.cpu.sub.textContent = t('w.gaming.pc', { v: pc.cpu != null ? `${num(pc.cpu, 0)}%` : '—' });
      // GPU: the game's load and its video memory
      m.gpu.val.textContent = num(g.gpu, 0);
      m.gpu.unit.textContent = '%';
      m.gpu.sub.textContent = g.vramGb != null ? t('w.gaming.vram', { gb: num(g.vramGb, 1) }) : t('w.gaming.pc', { v: '—' });
      // RAM: the game's working set; the PC's used / total under it
      m.ram.val.textContent = num(g.ramGb, 1);
      m.ram.unit.textContent = 'GB';
      m.ram.sub.textContent = t('w.gaming.pc', { v: pc.ramUsedGb != null ? `${num(pc.ramUsedGb, 1)} / ${num(pc.ramTotalGb, 0)} GB` : '—' });
      // Network: the PC's (see above), with the upload and the ping
      const down = g.netDownBps != null ? g.netDownBps : pc.netDownBps;
      const up = g.netUpBps != null ? g.netUpBps : pc.netUpBps;
      const [dv, du] = fmtRate(down);
      m.net.val.textContent = dv;
      m.net.unit.textContent = du;
      const ping = D.state && D.state.ping && D.state.ping.ms != null ? ` · ${Math.round(D.state.ping.ms)} ms` : '';
      m.net.sub.textContent = `↑ ${fmtRate(up).join(' ')}${ping}`;

      if (sampled) {
        push(m.cpu.hist, g.cpu);
        push(m.gpu.hist, g.gpu);
        push(m.ram.hist, g.ramGb);
        push(m.net.hist, down);
      }
      if (!colors) colors = { cpu: cssRgb(box, '--cpu', 'rgb(45, 212, 191)'), gpu: cssRgb(box, '--gpu', 'rgb(132, 204, 22)'), ram: cssRgb(box, '--ram', 'rgb(167, 139, 250)'), net: cssRgb(box, '--net', 'rgb(245, 158, 11)') };
      spark(m.cpu.canvas, m.cpu.hist, colors.cpu, 100);
      spark(m.gpu.canvas, m.gpu.hist, colors.gpu, 100);
      spark(m.ram.canvas, m.ram.hist, colors.ram, pc.ramTotalGb || 0);
      spark(m.net.canvas, m.net.hist, colors.net, 0);

      foot.replaceChildren(
        h('span', null, h('em', null, t('w.gaming.disk')), ` ↓ ${fmtRate(g.diskReadBps).join(' ')}  ↑ ${fmtRate(g.diskWriteBps).join(' ')}`),
        h('span', null, h('em', null, t('w.gaming.session')), ` ↓ ${mbText(g.sessionDownMb)}  ↑ ${mbText(g.sessionUpMb)}`),
        h('span', 'gm-dim', g.processes ? t('w.gaming.processes', { n: g.processes }) : t('w.gaming.measuring')));
    }

    let lastSample = null;
    function render() {
      const g = data && data.game;
      if (!g) { shownId = null; force = false; renderIdle(); return; }
      // the sparklines take one point per answer from the agent, not one per screen update
      const sampled = data !== lastSample;
      lastSample = data;
      renderGame(g, sampled);
    }

    return {
      update(kind) {
        if (kind === 'tick') { load(); loadAllowed(); }
        render();
      },
    };
  },
};
