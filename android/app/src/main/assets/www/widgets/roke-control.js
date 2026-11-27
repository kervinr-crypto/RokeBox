import { API, PANEL_HEADERS, api, h, haptic, t, toast } from '../main.js';
import { W } from '../registry.js';
import { every } from './helpers.js';

// ---------------------------------------------------------------- RokeBox: control of the computer (agent: RokeControl.cs)
// Windows, Keyboard, Computer screen and Quick actions. The agent acts only for the widget the phone names, on an
// enabled page, with that widget's own switch on (off by default, turned on in the settings on the computer): the
// panel only shows what the agent answers and says where to turn a switch on.

const widgetQuery = (page, w) => 'widget=' + encodeURIComponent(`${page.id}/${w.id}`);
const ARM_MS = 4000;

/** A POST to the agent, parameters in the query (the phone's transport sends no request body); throws its error text. */
export async function rkPost(path, timeout = 8000) {
  const ctl = new AbortController();
  const timer = setTimeout(() => ctl.abort(), timeout);
  try {
    const r = await fetch(API + path, { method: 'POST', cache: 'no-store', headers: PANEL_HEADERS, signal: ctl.signal });
    let body = null;
    try { body = await r.json(); } catch (_) { /* no body */ }
    if (!r.ok) throw Object.assign(new Error((body && body.error) || 'HTTP ' + r.status), { status: r.status });
    return body;
  } finally { clearTimeout(timer); }
}

const SVG_NS = 'http://www.w3.org/2000/svg';
/** A line icon (Lucide's style) from path data; several paths separated by "|". */
function icon(d) {
  const s = document.createElementNS(SVG_NS, 'svg');
  s.setAttribute('viewBox', '0 0 24 24');
  s.setAttribute('class', 'ico');
  s.setAttribute('aria-hidden', 'true');
  for (const [k, v] of [['fill', 'none'], ['stroke', 'currentColor'], ['stroke-width', '2'], ['stroke-linecap', 'round'], ['stroke-linejoin', 'round']]) s.setAttribute(k, v);
  for (const part of d.split('|')) {
    const p = document.createElementNS(SVG_NS, 'path');
    p.setAttribute('d', part);
    s.append(p);
  }
  return s;
}
const I = {
  min: 'M5 12h14',
  max: 'M5 5h14v14H5z',
  restore: 'M8 8h11v11H8z|M5 16V5h11',
  close: 'M18 6 6 18|M6 6l12 12',
  refresh: 'M21 12a9 9 0 1 1-2.6-6.4L21 8|M21 3v5h-5',
  send: 'M22 2 11 13|M22 2l-7 20-4-9-9-4z',
};

/** The line that says which switch to turn on, and where. */
const offNote = (text) => h('div', 'rc-note', h('b', null, t('w.rc.offTitle')), ' ', text);

// ---------------------------------------------------------------- Windows

W.windows = {
  needs: ['tick'],
  create(box, { page, w, o }) {
    const q = widgetQuery(page, w);
    let data = null, failed = false, busy = false, armedId = 0, armedUntil = 0;
    const rows = new Map(); // window id -> its row, kept between refreshes so the icons are not fetched again

    async function refresh() {
      try { data = await api('/rk/windows?' + q, 'GET', 6000); failed = false; return true; }
      catch (_) { failed = true; return false; }
      finally { render(); }
    }
    const load = every(3000, refresh, 5000);

    const count = h('span', 'rk-chip');
    const head = h('div', 'rk-head', h('div', 'rk-titles', h('div', 'rk-title', t('w.rc.win.title')), h('div', 'rk-sub', t('w.rc.win.sub'))), count);
    const note = h('div');
    const list = h('div', 'rk-scroll rc-wins');
    box.append(head, note, list);

    async function act(win, action) {
      if (busy) return;
      if (!data || !data.control) { toast(t('w.rc.win.off')); return; }
      if (action === 'close' && o.confirmClose !== false && !(armedId === win.id && armedUntil > Date.now())) {
        armedId = win.id; armedUntil = Date.now() + ARM_MS; haptic(); render();
        setTimeout(render, ARM_MS + 50);
        return;
      }
      busy = true; armedId = 0; haptic();
      try { await rkPost(`/rk/windows/act?${q}&id=${encodeURIComponent(win.id)}&action=${action}`); }
      catch (e) { toast(e.message, 'err'); }
      finally { busy = false; setTimeout(refresh, 250); }
    }

    function rowFor(win) {
      let r = rows.get(win.id);
      if (!r) {
        // the program's icon, as the Processes widget shows it; a window without a known program gets a plain one
        let ic = h('span', 'rc-ic rc-ic-ph', '🪟');
        if (win.process) {
          ic = h('img', 'rc-ic');
          ic.alt = '';
          ic.decoding = 'async';
          ic.addEventListener('error', () => ic.replaceWith(h('span', 'rc-ic rc-ic-ph', '🪟')), { once: true });
          ic.src = `${API}/procs/icon?name=${encodeURIComponent(win.process)}`;
        }
        const title = h('b'), sub = h('small');
        const main = h('button', 'rc-win-main', ic, h('span', 'rc-win-t', title, sub));
        main.type = 'button';
        const key = (path, label, action) => {
          const b = h('button', 'rc-mini', icon(path));
          b.type = 'button';
          b.setAttribute('aria-label', label);
          b.addEventListener('click', (e) => { e.stopPropagation(); act(r.win, action); });
          return b;
        };
        const minB = key(I.min, t('w.rc.win.minimize'), 'minimize');
        const maxB = key(I.max, t('w.rc.win.maximize'), 'maximize');
        const closeB = key(I.close, t('w.rc.win.close'), 'close');
        closeB.classList.add('rc-x');
        main.addEventListener('click', () => act(r.win, 'focus'));
        r = { el: h('div', 'rc-win', main, minB, maxB, closeB), title, sub, maxB, closeB, win, sig: '' };
        rows.set(win.id, r);
      }
      r.win = win;
      return r;
    }

    function render() {
      if (!data) {
        count.textContent = '';
        note.replaceChildren();
        list.replaceChildren(h('div', 'rk-empty', failed ? t('w.gaming.offline') : t('w.loading')));
        return;
      }
      if (data.supported === false) { note.replaceChildren(); list.replaceChildren(h('div', 'rk-empty', t('w.rc.windowsOnly'))); count.textContent = ''; return; }
      const wins = data.windows || [];
      count.textContent = String(wins.length);
      note.replaceChildren(data.control ? '' : offNote(t('w.rc.win.offHint')));
      const seen = new Set();
      const now = Date.now();
      const els = wins.map((win) => {
        seen.add(win.id);
        const r = rowFor(win);
        const armed = armedId === win.id && armedUntil > now;
        const sig = JSON.stringify([win.title, win.app, win.minimized, win.maximized, win.active, armed, data.control]);
        if (r.sig !== sig) {
          r.sig = sig;
          r.title.textContent = win.title;
          r.sub.textContent = win.minimized ? `${win.app} · ${t('w.rc.win.minimized')}` : win.app;
          r.el.className = 'rc-win' + (win.active ? ' on' : '') + (win.minimized ? ' min' : '') + (data.control ? '' : ' off');
          r.maxB.replaceChildren(icon(win.maximized ? I.restore : I.max));
          r.maxB.setAttribute('aria-label', t(win.maximized ? 'w.rc.win.restore' : 'w.rc.win.maximize'));
          r.closeB.classList.toggle('armed', armed);
          r.closeB.replaceChildren(armed ? t('w.rc.win.closeConfirm') : icon(I.close));
        }
        return r.el;
      });
      for (const id of [...rows.keys()]) if (!seen.has(id)) rows.delete(id);
      if (!els.length) { list.replaceChildren(h('div', 'rk-empty', t('w.rc.win.none'))); return; }
      // move rows only when the order changed: re-appending would restart the icons' decoding
      const same = els.length === list.children.length && els.every((el, i) => list.children[i] === el);
      if (!same) list.replaceChildren(...els);
    }

    return { update(kind) { if (kind === 'tick' || !data) load(); } };
  },
};

// ---------------------------------------------------------------- Keyboard

const REPEAT = new Set(['backspace', 'delete', 'left', 'right', 'up', 'down']);

W.keyboard = {
  needs: [],
  create(box, { page, w, o }) {
    const q = widgetQuery(page, w);
    let busy = false, armedUntil = 0, off = o.control !== true;

    const input = h('input', 'rc-input');
    input.type = 'text';
    input.maxLength = 1000;
    input.autocomplete = 'off';
    input.spellcheck = false;
    input.setAttribute('enterkeyhint', 'send');
    input.placeholder = t('w.rc.kb.placeholder');
    input.setAttribute('aria-label', t('w.rc.kb.placeholder'));
    const send = h('button', 'rc-key rc-send', icon(I.send), h('span', null, t('w.rc.kb.send')));
    send.type = 'submit';
    const form = h('form', 'rc-type', input, send);
    const note = h('div');
    const grid = h('div', 'rc-keys');
    box.append(form, note, grid);

    function showOff(why) { off = true; note.replaceChildren(offNote(why || t('w.rc.kb.offHint'))); }
    if (off) showOff();

    async function press(key) {
      try { await rkPost(`/rk/keys?${q}&key=${key}`, 5000); note.replaceChildren(); off = false; }
      catch (e) { if (e.status === 403) showOff(); else toast(e.message, 'err'); throw e; }
    }

    form.addEventListener('submit', async (e) => {
      e.preventDefault();
      const text = input.value;
      if (!text.trim() || busy) return;
      busy = true; send.disabled = true; haptic();
      try {
        await rkPost(`/rk/keys?${q}&text=${encodeURIComponent(text)}`, 25000);
        if (o.enter === true) await press('enter');
        input.value = '';
        note.replaceChildren();
        off = false;
      } catch (err) { if (err.status === 403) showOff(); else if (err.status) toast(err.message, 'err'); }
      finally { busy = false; send.disabled = false; }
    });

    const KEYS = [
      ['esc', 'Esc'], ['tab', 'Tab ⇥'], ['backspace', '⌫', 'w.rc.kb.backspace'], ['delete', t('w.rc.kb.delete')], ['enter', 'Enter ↵', null, 'wide'],
      ['left', '←', 'w.rc.kb.left'], ['up', '↑', 'w.rc.kb.up'], ['down', '↓', 'w.rc.kb.down'], ['right', '→', 'w.rc.kb.right'],
      ['home', t('w.rc.kb.home')], ['end', t('w.rc.kb.end')], ['pageup', '⇞', 'w.rc.kb.pageup'], ['pagedown', '⇟', 'w.rc.kb.pagedown'],
      ['copy', t('w.rc.kb.copy')], ['paste', t('w.rc.kb.paste')], ['cut', t('w.rc.kb.cut')], ['undo', t('w.rc.kb.undo')],
      ['redo', t('w.rc.kb.redo')], ['selectall', t('w.rc.kb.selectall')], ['save', t('w.rc.kb.save')], ['find', t('w.rc.kb.find')],
      ['alttab', 'Alt+Tab'], ['win', '⊞ Win'], ['menu', t('w.rc.kb.menu'), 'w.rc.kb.menuHint'], ['reload', 'F5'], ['fullscreen', 'F11'],
      ['newtab', t('w.rc.kb.newtab')], ['closetab', t('w.rc.kb.closetab')], ['closewindow', 'Alt+F4', 'w.rc.kb.closewindow', 'danger'],
      ['playpause', '⏯', 'w.rc.kb.playpause'], ['mute', '🔇', 'w.rc.kb.mute'], ['volumedown', '🔉', 'w.rc.kb.volumedown'], ['volumeup', '🔊', 'w.rc.kb.volumeup'],
    ];

    for (const [key, label, aria, cls] of KEYS) {
      const b = h('button', 'rc-key' + (cls ? ' ' + cls : '') + ([...label].length <= 2 && !/[A-Za-z0-9]/.test(label) ? ' sym' : ''), label);
      b.type = 'button';
      if (aria) b.setAttribute('aria-label', t(aria));
      if (key === 'closewindow') {
        b.addEventListener('click', () => {
          if (armedUntil < Date.now()) {
            armedUntil = Date.now() + ARM_MS; haptic();
            b.classList.add('armed'); b.textContent = t('w.rc.kb.again');
            setTimeout(() => { if (armedUntil <= Date.now()) { b.classList.remove('armed'); b.textContent = label; } }, ARM_MS + 50);
            return;
          }
          armedUntil = 0; b.classList.remove('armed'); b.textContent = label; haptic();
          press(key).catch(() => {});
        });
      } else if (REPEAT.has(key)) {
        // held down: the key repeats like on a keyboard, one request at a time
        let timer = 0, inflight = false;
        const once = () => { if (inflight) return; inflight = true; press(key).catch(() => stop()).then(() => { inflight = false; }, () => { inflight = false; }); };
        const stop = () => { clearTimeout(timer); clearInterval(timer); timer = 0; b.classList.remove('down'); };
        b.addEventListener('pointerdown', (e) => {
          e.preventDefault();
          haptic(); b.classList.add('down'); once();
          timer = setTimeout(() => { timer = setInterval(once, 110); }, 420);
        });
        for (const ev of ['pointerup', 'pointercancel', 'pointerleave']) b.addEventListener(ev, stop);
        b.addEventListener('keydown', (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); once(); } });
      } else {
        b.addEventListener('click', () => { haptic(); press(key).catch(() => {}); });
      }
      grid.append(b);
    }

    return { update() {} };
  },
};

// ---------------------------------------------------------------- Computer screen

W.screen = {
  needs: ['tick'],
  create(box, { page, w, o }) {
    const q = widgetQuery(page, w);
    const periodMs = Math.min(60, Math.max(1, Number(o.every) || 3)) * 1000;
    let info = null, infoFailed = false, mon = Math.max(0, (Math.round(Number(o.monitor)) || 1) - 1);
    let loading = false, lastAt = 0, shown = '', error = '', double = false;

    const img = h('img', 'rc-shot');
    img.alt = t('w.rc.scr.alt');
    img.draggable = false;
    const dot = h('i', 'rc-tap');
    const stage = h('div', 'rc-stage', img, dot);
    const msg = h('div', 'rc-msg');
    const bar = h('div', 'rc-bar');
    box.append(stage, msg, bar);

    const loadInfo = every(30_000, async () => {
      try { info = await api('/rk/screen/info?' + q, 'GET', 6000); infoFailed = false; render(); return true; }
      catch (_) { infoFailed = true; render(); return false; }
    }, 10_000);

    /** On screen right now: the page is the one shown (not a neighbour the pager keeps ready) and the app is in front. */
    function visible() {
      if (document.visibilityState === 'hidden') return false;
      const r = box.getBoundingClientRect();
      return r.width > 0 && r.right > 4 && r.left < window.innerWidth - 4;
    }

    async function shot() {
      if (loading || !info || !info.view) return;
      loading = true;
      const width = Math.min(1600, Math.max(480, Math.round(stage.clientWidth * (window.devicePixelRatio || 1))));
      const ctl = new AbortController();
      const timer = setTimeout(() => ctl.abort(), 9000);
      try {
        const r = await fetch(`${API}/rk/screen?${q}&monitor=${mon}&width=${width}`, { cache: 'no-store', headers: PANEL_HEADERS, signal: ctl.signal });
        if (r.status === 429) return; // another picture was just taken: the next tick gets one
        if (!r.ok) {
          let b = null;
          try { b = await r.json(); } catch (_) { /* not JSON */ }
          throw new Error((b && b.error) || 'HTTP ' + r.status);
        }
        const next = URL.createObjectURL(await r.blob());
        const prev = shown;
        img.addEventListener('load', () => { if (prev) URL.revokeObjectURL(prev); }, { once: true });
        img.src = next;
        shown = next;
        error = '';
      } catch (e) {
        error = e.name === 'AbortError' ? t('w.gaming.offline') : e.message;
      } finally {
        clearTimeout(timer);
        loading = false;
        lastAt = Date.now();
        render();
      }
    }

    // ---- taps: a click where the picture was tapped; held for a right-click; "2×" makes the next one a double-click
    let downAt = 0, downX = 0, downY = 0;
    stage.addEventListener('pointerdown', (e) => { downAt = Date.now(); downX = e.clientX; downY = e.clientY; });
    stage.addEventListener('pointerup', async (e) => {
      if (!info || !info.click || !img.naturalWidth || Math.hypot(e.clientX - downX, e.clientY - downY) > 14) return;
      const r = img.getBoundingClientRect();
      const scale = Math.min(r.width / img.naturalWidth, r.height / img.naturalHeight);
      const cw = img.naturalWidth * scale, ch = img.naturalHeight * scale;
      const ox = r.left + (r.width - cw) / 2, oy = r.top + (r.height - ch) / 2;
      const x = (e.clientX - ox) / cw, y = (e.clientY - oy) / ch;
      if (x < 0 || x > 1 || y < 0 || y > 1) return;
      const button = Date.now() - downAt > 550 ? 'right' : double ? 'double' : 'left';
      double = false;
      const sr = stage.getBoundingClientRect();
      dot.style.left = `${e.clientX - sr.left}px`;
      dot.style.top = `${e.clientY - sr.top}px`;
      dot.className = 'rc-tap on' + (button === 'right' ? ' right' : '');
      setTimeout(() => { dot.className = 'rc-tap'; }, 450);
      haptic();
      try { await rkPost(`/rk/screen/click?${q}&monitor=${mon}&x=${x.toFixed(4)}&y=${y.toFixed(4)}&button=${button}`, 5000); }
      catch (err) { toast(err.message, 'err'); }
      setTimeout(shot, 450);
      render();
    });
    stage.addEventListener('contextmenu', (e) => e.preventDefault());

    function key(label, aria, on, run) {
      const b = h('button', 'rc-key sm' + (on ? ' lit' : ''), label);
      b.type = 'button';
      if (aria) b.setAttribute('aria-label', aria);
      b.addEventListener('click', () => { haptic(); run(); });
      return b;
    }

    let sig = '';
    function render() {
      const next = JSON.stringify([info, infoFailed, error, mon, double, !!shown]);
      if (next === sig) return;
      sig = next;
      if (!info) {
        stage.hidden = true; bar.replaceChildren();
        msg.replaceChildren(h('div', 'rk-empty', infoFailed ? t('w.gaming.offline') : t('w.loading')));
        return;
      }
      if (info.supported === false) { stage.hidden = true; bar.replaceChildren(); msg.replaceChildren(h('div', 'rk-empty', t('w.rc.windowsOnly'))); return; }
      if (!info.view) {
        stage.hidden = true; bar.replaceChildren();
        msg.replaceChildren(h('div', 'rc-off', h('div', 'gm-empty-ic', '🖥️'), offNote(t('w.rc.scr.offHint'))));
        return;
      }
      stage.hidden = false;
      stage.classList.toggle('clickable', !!info.click);
      msg.replaceChildren(error ? h('div', 'rc-err', error) : (!shown ? h('div', 'rk-empty', t('w.loading')) : ''));
      const monitors = info.monitors || [];
      bar.replaceChildren(
        monitors.length > 1 ? h('div', 'rc-seg', monitors.map((m, i) => key(String(i + 1), t('w.rc.scr.monitor', { n: i + 1 }), i === mon, () => { mon = i; shot(); render(); }))) : null,
        h('span', 'rc-hint', info.click ? t('w.rc.scr.tapHint') : t('w.rc.scr.viewOnly')),
        info.click ? key('2×', t('w.rc.scr.double'), double, () => { double = !double; render(); }) : null,
        key(icon(I.refresh), t('w.rc.scr.refresh'), false, () => shot()));
    }

    render();
    return {
      update() {
        loadInfo();
        if (info && info.view && visible() && Date.now() - lastAt >= periodMs) shot();
      },
      destroy() { if (shown) URL.revokeObjectURL(shown); },
    };
  },
};

// ---------------------------------------------------------------- Quick actions

const QUICK = [
  { title: 'w.rc.qa.display', confirm: true, items: [['project-pc', '💻', 'w.rc.qa.pc'], ['project-clone', '⧉', 'w.rc.qa.clone'], ['project-extend', '⇔', 'w.rc.qa.extend'], ['project-second', '🖥️', 'w.rc.qa.second']] },
  { title: 'w.rc.qa.windows', items: [['desktop', '▭', 'w.rc.qa.desktop'], ['screenshot', '📸', 'w.rc.qa.screenshot'], ['snip', '✂️', 'w.rc.qa.snip'], ['explorer', '📁', 'w.rc.qa.explorer'], ['taskmgr', '📊', 'w.rc.qa.taskmgr'], ['settings', '⚙️', 'w.rc.qa.settings']] },
  { title: 'w.rc.qa.gamebar', items: [['clip', '⏺️', 'w.rc.qa.clip'], ['record', '🎬', 'w.rc.qa.record'], ['gamebar', '🎮', 'w.rc.qa.gamebarOpen']] },
];

W.quickactions = {
  needs: ['tick'],
  create(box, { page, w }) {
    const q = widgetQuery(page, w);
    let data = null, failed = false, busy = false, armed = '', armedUntil = 0;
    const refresh = async () => {
      try { data = await api('/rk/quick?' + q, 'GET', 6000); failed = false; return true; }
      catch (_) { failed = true; return false; }
      finally { render(); }
    };
    const load = every(20_000, refresh, 10_000);
    const note = h('div'), body = h('div', 'rk-scroll rc-qa');
    box.append(note, body);

    async function run(action, confirm) {
      if (busy) return;
      if (!data || !data.control) { toast(t('w.rc.qa.off')); return; }
      if (confirm && !(armed === action && armedUntil > Date.now())) {
        armed = action; armedUntil = Date.now() + ARM_MS; haptic(); render();
        setTimeout(render, ARM_MS + 50);
        return;
      }
      armed = ''; busy = true; haptic();
      try { await rkPost(`/rk/quick?${q}&action=${action}`); toast(t('w.rc.qa.done')); }
      catch (e) { toast(e.message, 'err'); }
      finally { busy = false; if (action.indexOf('theme') === 0) setTimeout(refresh, 600); render(); }
    }

    let sig = '';
    function render() {
      const now = Date.now();
      const next = JSON.stringify([data, failed, armedUntil > now ? armed : '']);
      if (next === sig) return;
      sig = next;
      if (!data) { note.replaceChildren(); body.replaceChildren(h('div', 'rk-empty', failed ? t('w.gaming.offline') : t('w.loading'))); return; }
      if (data.supported === false) { note.replaceChildren(); body.replaceChildren(h('div', 'rk-empty', t('w.rc.windowsOnly'))); return; }
      note.replaceChildren(data.control ? '' : offNote(t('w.rc.qa.offHint')));
      const tile = (action, emoji, label, confirm, lit) => {
        const isArmed = armed === action && armedUntil > now;
        const b = h('button', 'rc-qk' + (isArmed ? ' armed' : '') + (lit ? ' lit' : '') + (data.control ? '' : ' off'),
          h('span', 'rc-qk-ic', emoji), h('span', 'rc-qk-l', isArmed ? t('w.rc.kb.again') : t(label)));
        b.type = 'button';
        b.addEventListener('click', () => run(action, confirm));
        return b;
      };
      const dark = data.dark === true;
      body.replaceChildren(
        ...QUICK.map((g, i) => h('section', 'rc-qa-sec',
          h('div', 'rk-label', t(g.title)),
          h('div', 'rc-qa-grid',
            g.items.map(([action, emoji, label]) => tile(action, emoji, label, !!g.confirm)),
            // the theme switch sits with the display group: it shows what a tap would turn on
            i === 0 && data.dark != null ? tile(dark ? 'theme-light' : 'theme-dark', dark ? '☀️' : '🌙', dark ? 'w.rc.qa.light' : 'w.rc.qa.dark', false) : null))));
    }

    return { update(kind) { if (kind === 'tick' || !data) load(); render(); } };
  },
};
