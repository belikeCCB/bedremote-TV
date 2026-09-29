/*
 * gpharness.js — 把 phone.html 里「手柄」那一块 JS 抠出来，在一个假 DOM 上跑，
 * 用合成的 pointerdown/move/up 事件验证：摇杆/十字键→按键集合、死区、疾跑阈值、
 * 点按/按住/切换/连点、组合键、视角累加、帧合批发出去的字符串长什么样。
 * 为什么不用真浏览器：这些映射是纯数学 + 状态机，浏览器只会慢和吵；渲染和属性面板另测。
 *
 * 两个刻意的"不方便"：
 *  - 画布原点故意设成非零（_ox/_oy）：摇杆要是忘了减掉它，这里就会测出方向错掉（真浏览器抓到过）。
 *  - movementX 故意留 0：让"用 movementX 判拖动"这类 bug 不会被测试顺手掩盖掉。
 */
const fs = require('fs');
const vm = require('vm');

const A = '/* ---------- 手柄';
const B = '/* ---------- 打字';

function sliceBlock(html) {
  const a = html.indexOf(A), b = html.indexOf(B);
  if (a < 0 || b < 0 || b <= a) throw new Error('找不到手柄代码块');
  return html.slice(a, b);
}

function mkNode(tag, cls, txt) {
  const n = {
    tagName: (tag || 'div').toUpperCase(),
    dataset: {}, style: {}, children: [], textContent: txt == null ? '' : txt,
    _cls: String(cls || '').split(/\s+/).filter(Boolean),
  };
  Object.defineProperty(n, 'className', {
    get() { return this._cls.join(' ') },
    set(v) { this._cls = String(v).split(/\s+/).filter(Boolean) },
  });
  n.classList = {
    add(c) { if (n._cls.indexOf(c) < 0) n._cls.push(c) },
    remove(c) { n._cls = n._cls.filter(x => x !== c) },
    toggle(c, on) { if (on === undefined) on = n._cls.indexOf(c) < 0; on ? this.add(c) : this.remove(c); return on },
    contains(c) { return n._cls.indexOf(c) >= 0 },
  };
  n.appendChild = function (c) { this.children.push(c); c.parent = this; return c };
  Object.defineProperty(n, 'innerHTML', { get() { return '' }, set(v) { if (v === '') this.children = [] } });
  n.matches = function (sel) {
    if (sel[0] === '.') return this._cls.indexOf(sel.slice(1)) >= 0;
    if (sel[0] === '[') {
      const m = /^\[data-([a-z]+)="([^"]*)"\]$/i.exec(sel);
      return !!m && this.dataset[m[1]] === m[2];
    }
    return this.tagName === sel.toUpperCase();
  };
  n.querySelectorAll = function (sel) {
    const out = [];
    (function walk(node) { node.children.forEach(c => { if (c.matches(sel)) out.push(c); walk(c) }) })(this);
    return out;
  };
  n.querySelector = function (sel) { return this.querySelectorAll(sel)[0] || null };
  n.closest = function (sel) { let c = this; while (c) { if (c.matches(sel)) return c; c = c.parent } return null };
  n.setPointerCapture = function () { };
  n.clientWidth = 360; n.clientHeight = 620;
  n.clientLeft = 1; n.clientTop = 1;
  n._ox = 0; n._oy = 0;
  n.getBoundingClientRect = function () {
    return { left: this._ox, top: this._oy, width: this.clientWidth + this.clientLeft * 2, height: this.clientHeight + this.clientTop * 2 };
  };
  return n;
}

function run(htmlPath) {
  const block = sliceBlock(fs.readFileSync(htmlPath, 'utf8'));
  const frames = [], beacons = [], saved = [], prompts = [], clicks = [];
  const store = {};
  const gp = mkNode('div'); gp.id = 'gp'; gp._ox = 12; gp._oy = 55;
  const tb = {};
  ['gpe', 'gpadd', 'gpadd2', 'gpadd3', 'gpadd4', 'gpsz', 'gpsize', 'gpcopy', 'gpdel',
    'gpsave', 'gpre', 'gpout', 'gpex', 'gpst', 'gphud', 'gptb', 'gpp',
    'gpnew', 'gprn', 'gprm', 'gpprof', 'gplag', 'gplagb'].forEach(id => { tb[id] = mkNode('button'); tb[id].id = id });
  const byId = id => (id === '#gp' ? gp : tb[id.slice(1)] || null);

  const ctx = {
    console, JSON, Math, Object, Array, String, Number, Date, encodeURIComponent, decodeURIComponent, RegExp, parseInt, parseFloat, isNaN,
    T: 'tk',
    // 手机页现在把所有请求都拼成一条 AUTH（t + 设备编号 d + 设备名 dn）。
    // 这个假 DOM 只喂「手柄」那一段代码，段外声明的变量在这儿必须也有，
    // 否则一跑就是 AUTH is not defined（gpfitcheck 就是这么抓到的）。
    AUTH: 't=tk&d=harnessdevice&dn=harness',
    $: byId,
    el: mkNode,
    buzz() { },
    prompt(q, d) { prompts.push(q); return d },
    addEventListener() { },
    localStorage: { getItem: k => (k in store ? store[k] : null), setItem: (k, v) => { store[k] = v } },
    navigator: { sendBeacon(u) { beacons.push(u); return true }, vibrate() { } },
    document: {
      hidden: false, addEventListener() { }, documentElement: {}, fullscreenElement: null,
      body: mkNode('body'), getElementById: () => null, exitFullscreen() { },
    },
    // 定时器一律不真跑：测试里直接调 ctx.gpTick() / ctx.gpLagGot()，免得被时序牵着走
    setInterval() { return 1 },
    clearInterval() { },
    api(p) { frames.push(String(p)); return Promise.resolve('ok') },
    setTimeout(fn) { return 0 }, clearTimeout() { },
    fetch(u, opt) {
      if (String(u).indexOf('/gamepad/save') >= 0) { saved.push(decodeURIComponent(String(opt.body).replace(/^j=/, ''))); return Promise.resolve({ ok: true }) }
      if (String(u).indexOf('/gamepad') >= 0) return Promise.resolve({ json: () => Promise.resolve(null) });
      return Promise.resolve({ ok: true });
    },
    send(p) { frames.push(String(p)); return Promise.resolve('held:0+0') },
  };
  ctx.globalThis = ctx;
  vm.createContext(ctx);
  vm.runInContext(block, ctx);
  ctx.gpStart();
  ctx.gpPanel = function () { };            // 属性面板要真 DOM，这里只测输入→发帧那条链
  ctx.GP.set = ctx.gpFixSet({ active: "默认", ts: 1, profiles: { "默认": JSON.parse(JSON.stringify(ctx.GP_DEF)) } });
  ctx.GP.lay = ctx.gpCur();
  ctx.gpBuild();

  function rect(id) { return ctx.gpRects()[id] }
  function node(id) { return gp.querySelectorAll('.gpw').filter(n => n.dataset.w === id)[0] }
  function fire(type, id, x, y, pid) {
    const n = node(id);
    if (!n) throw new Error('没有控件 ' + id);
    return { e: { type, target: n, pointerId: pid == null ? 1 : pid, clientX: x + gp._ox, clientY: y + gp._oy, movementX: 0, movementY: 0, preventDefault() { } }, n };
  }
  function down(id, x, y, pid) { const r = fire('pointerdown', id, x, y, pid); gp.onpointerdown(r.e) }
  function move(id, x, y, pid) { const r = fire('pointermove', id, x, y, pid); gp.onpointermove(r.e) }
  function up(id, x, y, pid) { const r = fire('pointerup', id, x, y, pid); gp.onpointerup(r.e) }
  function tick() { ctx.gpTick && ctx.gpTick() }
  function since(n) { return frames.slice(n) }

  return { ctx, gp, frames, beacons, saved, prompts, rect, node, down, move, up, tick, since, store };
}

module.exports = { run, sliceBlock };
