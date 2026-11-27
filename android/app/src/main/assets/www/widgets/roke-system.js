import { API, D, LOCALE, PANEL_HEADERS, api, fmtDur, gpusOf, h, haptic, t, toast } from '../main.js';
import { W } from '../registry.js';
import { every } from './helpers.js';

// ---------------------------------------------------------------- RokeBox: the PC's spec sheet, health check, data
// usage and power timer (agent: RokeSystem.cs). The agent reads only while such a widget is on an enabled page; what
// changes the computer (emptying the Recycle Bin, the power timer) is opt-in and checked by the agent per widget.

const num = (v, digits = 1) => (v == null || !isFinite(v) ? '—'
  : Number(v).toLocaleString(LOCALE, { minimumFractionDigits: digits, maximumFractionDigits: digits }));
/** Gigabytes with one decimal under 100, none for a whole number ("64", "15,7", "512"). */
const gb = (v) => num(v, v == null || v >= 100 || Math.round(v * 10) % 10 === 0 ? 0 : 1);
/** Bytes as "820 MB" or "12,4 GB". */
const bytes = (b) => (b == null ? '—' : b >= 1e9 ? `${num(b / 1e9, b >= 1e10 ? 0 : 1)} GB` : `${num(b / 1e6, b >= 1e7 ? 0 : 1)} MB`);
const dateText = (ms) => new Date(ms).toLocaleDateString(LOCALE, { day: 'numeric', month: 'short', year: 'numeric' });
const widgetQuery = (page, w) => 'widget=' + encodeURIComponent(`${page.id}/${w.id}`);
/** A row of the spec sheet or health list: an optional status dot, a title and a detail. */
const row = (title, detail, level) => h('div', 'rk-row' + (level ? ' rk-' + level : ''),
  level ? h('i', 'rk-dot') : null, h('span', 'rk-k', title), h('span', 'rk-v', detail));

async function post(path) {
  const r = await fetch(API + path, { method: 'POST', cache: 'no-store', headers: PANEL_HEADERS });
  let body = null;
  try { body = await r.json(); } catch (_) { /* no body */ }
  if (!r.ok) throw Object.assign(new Error((body && body.error) || 'HTTP ' + r.status), { code: body && body.error });
  return body;
}

// ---------------------------------------------------------------- PC spec sheet

W.sysinfo = {
  needs: ['tick', 'state'],
  create(box) {
    let data = null, failed = false, key = '';
    const load = every(30_000, async () => {
      try { data = await api('/rk/sysinfo', 'GET', 8000); failed = false; return true; }
      catch (_) { failed = true; return false; }
    }, 10_000);
    const head = h('div', 'rk-head'), body = h('div', 'rk-scroll');
    box.append(head, body);

    function section(icon, title, rows) {
      const kids = rows.filter(Boolean);
      return kids.length ? h('section', 'rk-sec', h('div', 'rk-sec-t', h('span', 'rk-ic', icon), title), kids) : null;
    }

    function render() {
      if (!data || !data.enabled) {
        head.replaceChildren(h('div', 'rk-title', t('w.loading')));
        body.replaceChildren(failed ? h('div', 'rk-empty', t('w.gaming.offline')) : null);
        return;
      }
      const os = data.os || {}, cpu = data.cpu || {}, ram = data.ram || {}, board = data.board || {};
      const next = JSON.stringify([data, Math.floor(Date.now() / 60_000)]);
      if (next === key) return; // nothing to redraw: the uptime moves by the minute
      key = next;
      head.replaceChildren(
        h('div', 'rk-hero', '🖥️'),
        h('div', 'rk-titles',
          h('div', 'rk-title', board.system || os.computer || 'PC'),
          h('div', 'rk-sub', [os.name, os.version, os.build ? 'build ' + os.build : null].filter(Boolean).join(' · '))),
        os.bootedAt ? h('span', 'rk-chip', t('w.si.uptime', { dur: fmtDur(Date.now() - os.bootedAt) })) : null);

      const modules = ram.modules || [];
      const sameSize = modules.length > 1 && modules.every((m) => m.sizeGb === modules[0].sizeGb);
      // the drivers' names (the registry); the panel's live list names some GPUs differently, so it only fills a gap
      const gpus = (data.gpus || []).length ? data.gpus : gpusOf(D.state).map((g) => ({ name: g.name, driver: g.driver, vramGb: g.vramTotal }));
      body.replaceChildren(
        section('🧠', t('w.si.cpu'), [
          row(cpu.name || '—', ''),
          row(t('w.si.cores', { c: cpu.cores, t: cpu.threads }), cpu.baseMhz ? `${num(cpu.baseMhz / 1000, 1)} GHz` : ''),
        ]),
        section('💾', t('w.si.ram'), [
          row(`${gb(ram.totalGb)} GB${ram.type ? ' ' + ram.type : ''}`, ram.speedMts ? `${ram.speedMts} MT/s` : ''),
          modules.length ? row(sameSize ? t('w.si.modules', { n: modules.length, gb: gb(modules[0].sizeGb) }) : modules.map((m) => gb(m.sizeGb) + ' GB').join(' + '),
            [modules[0].maker, modules[0].part].filter(Boolean).join(' · ')) : null,
        ]),
        section('🎮', t('w.si.gpu'), gpus.map((g) => row(g.name, [g.vramGb ? `${gb(g.vramGb)} GB` : null, g.driver ? t('w.si.driver', { v: g.driver }) : null].filter(Boolean).join(' · ')))),
        section('📺', t('w.si.displays'), (data.displays || []).map((d) => row(`${d.width} × ${d.height}`, `${d.hz} Hz${d.primary ? ' · ' + t('w.si.primary') : ''}`))),
        section('🗄️', t('w.si.storage'), [
          ...(data.disks || []).map((d) => row(d.model, [d.bus, d.ssd === true ? 'SSD' : d.ssd === false ? 'HDD' : null, d.sizeGb ? `${num(d.sizeGb, 0)} GB` : null].filter(Boolean).join(' · '))),
          ...(data.volumes || []).map((v) => {
            const used = v.totalGb ? 1 - v.freeGb / v.totalGb : 0;
            const bar = h('div', 'rk-bar' + (used > 0.95 ? ' bad' : used > 0.85 ? ' warn' : ''), h('i'));
            bar.firstChild.style.width = `${Math.round(used * 100)}%`;
            return h('div', 'rk-vol', row(`${v.name}${v.label ? ' ' + v.label : ''}`, t('w.si.free', { free: gb(v.freeGb), total: gb(v.totalGb) })), bar);
          }),
        ]),
        section('🧩', t('w.si.board'), [
          board.model || board.maker ? row([board.maker, board.model].filter(Boolean).join(' '), '') : null,
          board.bios ? row('BIOS ' + board.bios, [board.biosVendor, board.biosDate].filter(Boolean).join(' · ')) : null,
        ]),
        section('🌐', t('w.si.network'), (data.network || []).filter((n) => n.ipv4).map((n) =>
          row(`${n.name}${n.speedMbps ? ' · ' + (n.speedMbps >= 1000 ? num(n.speedMbps / 1000, n.speedMbps % 1000 ? 1 : 0) + ' Gbps' : n.speedMbps + ' Mbps') : ''}`,
            [n.ipv4, n.gateway ? t('w.si.gateway', { ip: n.gateway }) : null].filter(Boolean).join(' · ')))),
        section('🪟', 'Windows', [
          row([os.name, os.version].filter(Boolean).join(' '), os.edition || ''),
          os.installedAt ? row(t('w.si.installed', { date: dateText(os.installedAt) }), os.build ? 'build ' + os.build : '') : null,
        ]));
    }

    return { update(kind) { if (kind === 'tick' || !data) load(); render(); } };
  },
};

// ---------------------------------------------------------------- PC health

const RANK = { ok: 0, warn: 1, bad: 2 };

W.pchealth = {
  needs: ['tick', 'state'],
  create(box, { page, w }) {
    let data = null, failed = false, armed = 0, busy = false, key = '';
    const wq = widgetQuery(page, w);
    const load = every(10_000, async () => {
      try { data = await api('/rk/health?' + wq, 'GET', 6000); failed = false; return true; }
      catch (_) { failed = true; return false; }
    }, 10_000);
    const head = h('div', 'rk-head'), list = h('div', 'rk-scroll rk-list');
    box.append(head, list);

    /** The agent's checks plus the temperatures the panel already has. */
    function checks() {
      const out = [];
      for (const c of (data && data.checks) || []) {
        if (c.kind === 'disk') out.push({ level: c.level, title: t('w.ph.disk', { name: c.name }), detail: t('w.ph.diskFree', { free: gb(c.a), pct: num(c.value, 0) }) });
        else if (c.kind === 'ram') out.push({ level: c.level, title: t('w.ph.ram'), detail: t('w.ph.ramUsed', { used: gb(c.a), total: gb(c.b), pct: num(c.value, 0) }) });
        else if (c.kind === 'uptime') {
          const dur = fmtDur(c.value * 86_400_000);
          out.push({ level: c.level, title: t('w.ph.uptime'), detail: c.level === 'ok' ? t('w.ph.uptimeOk', { dur }) : t('w.ph.uptimeLong', { dur }) });
        } else if (c.kind === 'update') out.push({ level: c.level, title: 'Windows Update', detail: c.level === 'ok' ? t('w.ph.updateOk') : t('w.ph.updatePending') });
        else if (c.kind === 'recycle') {
          out.push({ level: c.level, title: t('w.ph.recycle'), recycle: c.value > 0,
            detail: !c.value ? t('w.ph.recycleEmpty') : t(c.value === 1 ? 'w.ph.recycleOne' : 'w.ph.recycleSize', { size: bytes(c.a * 1e9), n: c.value }) });
        }
      }
      const pc = (D.state && D.state.pc) || {};
      const temp = (title, v) => { if (v != null) out.push({ level: v >= 95 ? 'bad' : v >= 85 ? 'warn' : 'ok', title, detail: `${Math.round(v)} °C` }); };
      temp(t('w.ph.cpuTemp'), pc.cpuTemp);
      const g = gpusOf(D.state).find((x) => x.temp != null);
      temp(t('w.ph.gpuTemp'), g && g.temp);
      return out.sort((a, b) => RANK[b.level] - RANK[a.level]);
    }

    async function clean() {
      if (busy) return;
      if (Date.now() > armed) { armed = Date.now() + 4000; haptic(); key = ''; render(); return; }
      busy = true; armed = 0;
      try { await post('/rk/health/clean?' + wq); toast(t('w.ph.cleaned')); data = null; load(); }
      catch (e) { toast(e.message); }
      finally { busy = false; key = ''; render(); }
    }

    function render() {
      if (!data) { head.replaceChildren(h('div', 'rk-title', failed ? t('w.gaming.offline') : t('w.loading'))); list.replaceChildren(); return; }
      const all = checks();
      const bad = all.filter((c) => c.level === 'bad').length, warn = all.filter((c) => c.level === 'warn').length;
      const next = JSON.stringify([all, armed > Date.now(), busy, data.canClean]);
      if (next === key) return;
      key = next;
      const state = bad ? 'bad' : warn ? 'warn' : 'ok';
      head.replaceChildren(
        h('div', 'rk-hero rk-state-' + state, bad ? '⚠️' : warn ? '🩺' : '✅'),
        h('div', 'rk-titles',
          h('div', 'rk-title', bad ? t('w.ph.problems', { n: bad }) : warn ? t('w.ph.warnings', { n: warn }) : t('w.ph.allGood')),
          h('div', 'rk-sub', t('w.ph.checked', { n: all.length }))));
      list.replaceChildren(...all.map((c) => {
        const r = row(c.title, c.detail, c.level);
        if (c.recycle && data.canClean) {
          const b = h('button', 'rk-btn' + (armed > Date.now() ? ' armed' : ''), armed > Date.now() ? t('w.ph.cleanConfirm') : t('w.ph.clean'));
          b.addEventListener('click', (e) => { e.stopPropagation(); clean(); });
          r.append(b);
        }
        return r;
      }));
    }

    return { update(kind) { if (kind === 'tick' || !data) load(); render(); } };
  },
};

// ---------------------------------------------------------------- data usage

const ymd = (d) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
const parseYmd = (s) => { const [y, m, d] = s.split('-').map(Number); return new Date(y, m - 1, d); };

/** The current monthly cycle that starts on `resetDay`: its first day and the first day of the next one. */
export function usageCycle(today, resetDay) {
  const day = Math.min(28, Math.max(1, Math.round(resetDay) || 1));
  const start = new Date(today.getFullYear(), today.getMonth() - (today.getDate() < day ? 1 : 0), day);
  return { start, next: new Date(start.getFullYear(), start.getMonth() + 1, day) };
}

W.datausage = {
  needs: ['tick'],
  create(box, { o }) {
    let data = null, failed = false, key = '';
    const load = every(15_000, async () => {
      try { data = await api('/rk/datausage', 'GET', 6000); failed = false; return true; }
      catch (_) { failed = true; return false; }
    }, 15_000);
    const stats = h('div', 'du-stats'), chart = h('div', 'du-chart'), foot = h('div', 'du-foot');
    box.append(stats, chart, foot);
    const limit = Math.max(0, Number(o.limitGb) || 0) * 1e9;
    const span = Math.min(31, Math.max(7, Math.round(Number(o.days) || 14)));

    function stat(label, down, up, extra) {
      return h('div', 'du-stat', h('div', 'rk-label', label), h('div', 'du-big', bytes(down + up)),
        h('div', 'du-sub', `↓ ${bytes(down)}  ↑ ${bytes(up)}`), extra || null);
    }

    function render() {
      if (!data) { stats.replaceChildren(h('div', 'rk-title', failed ? t('w.gaming.offline') : t('w.loading'))); chart.replaceChildren(); foot.replaceChildren(); return; }
      const today = data.today ? parseYmd(data.today) : new Date();
      const byDate = new Map((data.days || []).map((d) => [d.date, d]));
      const sum = (from, to) => { let down = 0, up = 0; for (const d of data.days || []) { const x = parseYmd(d.date); if (x >= from && x < to) { down += d.down; up += d.up; } } return [down, up]; };
      const tomorrow = new Date(today.getFullYear(), today.getMonth(), today.getDate() + 1);
      const t0 = byDate.get(ymd(today)) || { down: 0, up: 0 };
      const [wDown, wUp] = sum(new Date(today.getFullYear(), today.getMonth(), today.getDate() - 6), tomorrow);
      const { start, next } = usageCycle(today, o.resetDay);
      const [cDown, cUp] = sum(start, tomorrow);
      const used = cDown + cUp;
      const daysLeft = Math.round((next - today) / 86_400_000);
      const nextKey = JSON.stringify([data, limit, span, o.resetDay]);
      if (nextKey === key) return;
      key = nextKey;

      let extra = h('div', 'du-sub', t('w.du.renews', { n: daysLeft }));
      if (limit) {
        const pct = used / limit;
        const bar = h('div', 'rk-bar' + (pct >= 1 ? ' bad' : pct >= 0.8 ? ' warn' : ''), h('i'));
        bar.firstChild.style.width = `${Math.min(100, Math.round(pct * 100))}%`;
        extra = h('div', null, bar, h('div', 'du-sub' + (pct >= 1 ? ' rk-badtext' : ''),
          pct >= 1 ? t('w.du.over') : `${t('w.du.ofLimit', { gb: num(limit / 1e9, 0) })} · ${t('w.du.renews', { n: daysLeft })}`));
      }
      stats.replaceChildren(stat(t('w.du.today'), t0.down, t0.up), stat(t('w.du.week'), wDown, wUp), stat(t('w.du.cycle'), cDown, cUp, extra));

      // the last `span` days as stacked columns: download below, upload on top
      const days = [];
      for (let i = span - 1; i >= 0; i--) {
        const d = new Date(today.getFullYear(), today.getMonth(), today.getDate() - i);
        const v = byDate.get(ymd(d)) || { down: 0, up: 0 };
        days.push({ d, down: v.down, up: v.up });
      }
      const max = Math.max(1, ...days.map((x) => x.down + x.up));
      chart.replaceChildren(...days.map((x, i) => {
        const col = h('div', 'du-col' + (i === days.length - 1 ? ' today' : ''),
          h('div', 'du-stack', h('i', 'du-up'), h('i', 'du-down')),
          h('span', 'du-day', String(x.d.getDate())));
        const total = (x.down + x.up) / max;
        col.firstChild.style.height = `${Math.max(total > 0 ? 3 : 0, Math.round(total * 100))}%`;
        col.firstChild.firstChild.style.flexGrow = String(x.up || 0);
        col.firstChild.lastChild.style.flexGrow = String(x.down || 0);
        col.title = `${x.d.toLocaleDateString(LOCALE)}: ↓ ${bytes(x.down)} ↑ ${bytes(x.up)}`;
        return col;
      }));
      foot.replaceChildren(data.since ? t('w.du.since', { date: parseYmd(data.since).toLocaleDateString(LOCALE, { day: 'numeric', month: 'short' }) }) : t('w.du.none'));
    }

    return { update() { load(); render(); } };
  },
};

// ---------------------------------------------------------------- power timer

const PRESETS = [15, 30, 45, 60, 90, 120];
const ACTION_KEYS = { sleep: 'w.pt.sleep', shutdown: 'w.pt.shutdown', restart: 'w.pt.restart' };
const IN_KEYS = { sleep: 'w.pt.sleepIn', shutdown: 'w.pt.shutdownIn', restart: 'w.pt.restartIn' };

W.powertimer = {
  needs: ['tick'],
  create(box, { page, w }) {
    let data = null, failed = false, action = 'shutdown', armed = null, busy = false;
    const wq = widgetQuery(page, w);
    const load = every(5000, async () => {
      try { data = await api('/rk/powertimer', 'GET', 5000); failed = false; return true; }
      catch (_) { failed = true; return false; }
    }, 5000);

    async function start(minutes) {
      if (busy) return;
      if (!armed || armed.minutes !== minutes || armed.action !== action || Date.now() > armed.until) {
        armed = { minutes, action, until: Date.now() + 4000 };
        haptic();
        render();
        return;
      }
      busy = true; armed = null;
      try {
        data = await post(`/rk/powertimer?action=${action}&minutes=${minutes}&${wq}`);
        toast(t('w.pt.started', { action: t(ACTION_KEYS[action]), min: minutes }));
      } catch (e) { toast(e.code === 'off' ? t('w.pt.off') : e.message); }
      finally { busy = false; render(); }
    }

    async function cancel() {
      if (busy) return;
      busy = true;
      try { data = await post('/rk/powertimer/cancel?' + wq); toast(t('w.pt.cancelled')); }
      catch (e) { toast(e.message); }
      finally { busy = false; render(); }
    }

    // the buttons are built only when what they show changes: a rebuild between a finger's down and up would lose the tap
    let key = '', ring = null, clock = null, startedLeft = 0;

    function tickClock() {
      if (!ring || !data || !data.endsAt) return;
      const left = Math.max(0, data.endsAt - Date.now());
      const s = Math.ceil(left / 1000);
      clock.textContent = s >= 3600 ? `${Math.floor(s / 3600)}:${String(Math.floor((s % 3600) / 60)).padStart(2, '0')}:${String(s % 60).padStart(2, '0')}`
        : `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
      ring.style.setProperty('--p', String(startedLeft ? Math.min(1, left / startedLeft) : 1));
    }

    function render() {
      const live = armed && Date.now() <= armed.until ? armed : null;
      const active = !!(data && data.action && data.endsAt > Date.now());
      const next = JSON.stringify([!!data, failed, data && data.allowed, active && data.action, active && data.endsAt, action, live, busy]);
      if (next === key) { tickClock(); return; }
      key = next;
      ring = null;
      if (!data) { box.replaceChildren(h('div', 'rk-title', failed ? t('w.gaming.offline') : t('w.loading'))); return; }
      if (!data.allowed) {
        box.replaceChildren(h('div', 'pt-off', h('div', 'rk-hero', '⏲️'), h('div', 'rk-title', t('w.pt.title')), h('div', 'rk-sub', t('w.pt.off'))));
        return;
      }
      if (active) {
        // the ring empties from what was left when it was first shown (the agent knows the end, not the start)
        startedLeft = Math.max(startedLeft, data.endsAt - Date.now());
        clock = h('b');
        ring = h('div', 'pt-ring pt-' + data.action, clock);
        const end = new Date(data.endsAt).toLocaleTimeString(LOCALE, { hour: '2-digit', minute: '2-digit' });
        const btn = h('button', 'rk-btn pt-cancel', t('w.pt.cancel'));
        btn.addEventListener('click', (e) => { e.stopPropagation(); cancel(); });
        box.replaceChildren(h('div', 'pt-active', h('div', 'rk-label', t(IN_KEYS[data.action])), ring, h('div', 'rk-sub', t('w.pt.at', { time: end })), btn));
        tickClock();
        return;
      }
      startedLeft = 0;
      const seg = h('div', 'pt-seg', Object.keys(ACTION_KEYS).map((a) => {
        const b = h('button', a === action ? 'on' : '', t(ACTION_KEYS[a]));
        b.addEventListener('click', (e) => { e.stopPropagation(); action = a; armed = null; render(); });
        return b;
      }));
      const grid = h('div', 'pt-grid', PRESETS.map((m) => {
        const b = h('button', 'pt-preset' + (live && live.minutes === m ? ' armed' : ''), h('b', null, String(m)), h('small', null, 'min'));
        b.addEventListener('click', (e) => { e.stopPropagation(); start(m); });
        return b;
      }));
      const hint = live ? t('w.pt.confirm', { action: t(ACTION_KEYS[action]), min: live.minutes })
        : action === 'sleep' ? t('w.pt.title') : t('w.pt.closes');
      box.replaceChildren(h('div', 'rk-label', t('w.pt.title')), seg, grid, h('div', 'rk-sub pt-hint' + (live ? ' armed' : ''), hint));
    }

    return { update() { load(); render(); } };
  },
};
