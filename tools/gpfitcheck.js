/*
 * gpfitcheck.js — 手柄布局的"摆得开、拖得回"回归测试（不弹浏览器、不注入任何东西）。
 *
 * 盯的是两类真出过的 bug：
 *  1) 默认布局只在竖屏手机上不重叠，一到横屏/大屏（1280x720 起）跳跃键就压进攻击键、还被夹出画布；
 *  2) 存的位置和画出来的位置各夹各的边界（模型夹到 0.98、画的时候夹到"装得下"），
 *     拖过边之后模型还在偷偷涨、画的却钉在角落 → "粘在右下角拉不动"。
 *
 * 所以它检查的是 gpPlace 真正写进 style 的像素，而不是模型里的比例数字：
 *  - 十种画布比例下：任何两个控件不重叠、任何控件不出界；
 *  - 不变量：模型 * 画布 == 画出来的位置（±1px，四舍五入）；也就是说只有一处在夹边界；
 *  - 拖出右下角再拖回来，第一下就得动。
 *
 * 跑法（这台机器没有 node/npm，两条路任选）：
 *   node-repl:  (await import('node:module')).createRequire('file:///…/tools/a.js')('<绝对路径>/gpfitcheck.js')
 *   或 ZCode 自带的无头 node：  ZCode.exe …\tools\gpfitcheck.js
 * 退出码非 0 = 有过不去的检查。
 */
const fs = require('fs');
const path = require('path');
const harness = require('./gpharness.js');

// 默认测本仓库的 www/phone.html；argv[2] 可以指到别一份（负对照就拿它测改之前的文件，
// 证明这些检查真的会红 —— 不会红的检查等于没检查）。
const HTML = (process.argv[2] && fs.existsSync(process.argv[2])) ? process.argv[2]
  : path.join(__dirname, '..', 'www', 'phone.html');

// 竖屏手机、横屏、平板、电视、超宽、方屏、小窗 —— 布局不许赌"手机就是竖的"
const STAGES = [
  ['手机 360x620', 360, 620], ['窄手机 320x560', 320, 560], ['小窗 280x480', 280, 480],
  ['横屏 1280x720', 1280, 720], ['PC 窗口 1280x620', 1280, 620], ['电视 1920x1080', 1920, 1080],
  ['超宽 2560x1080', 2560, 1080], ['矮窗 900x420', 900, 420], ['平板 768x1024', 768, 1024], ['方屏 600x600', 600, 600]
];

const fails = [];
function ok(cond, msg) { if (!cond) fails.push(msg) }
function px(v) { return Math.round(parseFloat(v) || 0) }
function boxes(h) {
  const root = h.gp;
  return root.querySelectorAll('.gpw').map(function (e) {
    return { id: e.dataset.w, x: px(e.style.left), y: px(e.style.top), w: px(e.style.width), h: px(e.style.height) };
  });
}

const H = harness.run(HTML);
const ctx = H.ctx;

// ---- 1. 十种画布：不重叠、不出界、且"画出来的"就是"存着的" ----
STAGES.forEach(function (st) {
  const name = st[0], W = st[1], Ht = st[2];
  H.gp.clientWidth = W; H.gp.clientHeight = Ht;
  ctx.gpPlace();
  const bs = boxes(H);
  ok(bs.length === ctx.GP.lay.widgets.length, name + '：画布上少了控件（' + bs.length + '/' + ctx.GP.lay.widgets.length + '）');
  bs.forEach(function (b) {
    ok(b.x >= 0 && b.y >= 0 && b.x + b.w <= W + 1 && b.y + b.h <= Ht + 1,
      name + '：' + b.id + ' 出界（x ' + b.x + '..' + (b.x + b.w) + ' / ' + W + '，y ' + b.y + '..' + (b.y + b.h) + ' / ' + Ht + '）');
  });
  ctx.GP.lay.widgets.forEach(function (w) {
    const b = bs.filter(function (x) { return x.id === w.id })[0]; if (!b) return;
    ok(Math.abs(b.x - w.x * W) <= 1 && Math.abs(b.y - w.y * Ht) <= 1,
      name + '：' + b.id + ' 存的位置和画出来的位置对不上（存 ' + w.x.toFixed(3) + ',' + w.y.toFixed(3) +
      ' → ' + (w.x * W).toFixed(0) + 'px，画在 ' + b.x + ',' + b.y + 'px）—— 夹边界的只该有一处');
  });
  for (let i = 0; i < bs.length; i++) for (let j = i + 1; j < bs.length; j++) {
    const A = bs[i], B = bs[j];
    const ox = Math.min(A.x + A.w, B.x + B.w) - Math.max(A.x, B.x);
    const oy = Math.min(A.y + A.h, B.y + B.h) - Math.max(A.y, B.y);
    ok(!(ox > 1 && oy > 1), name + '：' + A.id + ' 和 ' + B.id + ' 压在一起 ' + ox + 'x' + oy + 'px');
  }
});

// ---- 2. 拖过边界再拖回来：第一下就得动（真出过的"粘在右下角拉不动"） ----
H.gp.clientWidth = 1280; H.gp.clientHeight = 720;
ctx.GP.edit = true;
ctx.gpPlace();
const stick = ctx.GP.lay.widgets.filter(function (w) { return w.kind === 'stick' })[0];
const sw = stick.w * Math.min(1280, 720);                       // 摇杆是正方形，边长按 min 算
const maxX = (1280 - sw) / 1280, maxY = (720 - sw) / 720;        // 贴右下角时该停在哪
H.down('stick1', 100, 450);
for (let i = 0; i < 20; i++) H.move('stick1', 100 + (i + 1) * 200, 450 + (i + 1) * 200);   // 一路甩出画布
H.up('stick1');
ok(Math.abs(stick.x - maxX) < 0.01 && Math.abs(stick.y - maxY) < 0.01,
  '拖出界后没贴在角上：' + stick.x.toFixed(3) + ',' + stick.y.toFixed(3) + ' 应为 ' + maxX.toFixed(3) + ',' + maxY.toFixed(3));
const atCorner = { x: stick.x, y: stick.y };
const drawn0 = boxes(H).filter(function (b) { return b.id === 'stick1' })[0];
H.down('stick1', atCorner.x * 1280 + 60, atCorner.y * 720 + 60);
H.move('stick1', atCorner.x * 1280 + 60 - 90, atCorner.y * 720 + 60 - 90);                 // 往回拖 90px
H.up('stick1');
const drawn1 = boxes(H).filter(function (b) { return b.id === 'stick1' })[0];
const moved = (drawn0.x - drawn1.x) + (drawn0.y - drawn1.y);
ok(moved > 80, '钉在角上拖不动：往回拖 90px，画面上只挪了 ' + moved + 'px（' +
  drawn0.x + ',' + drawn0.y + ' → ' + drawn1.x + ',' + drawn1.y + '）—— 以前模型夹到 0.98、画的夹到装得下，' +
  '两边各夹一份，往回拖得先把那段看不见的账还清才动');

// ---- 3. 旋钮归中：松手后偏移必须是 0，且居中不许靠"负 margin" ----
//   真 bug：CSS 用 margin:-23% 把旋钮摆回圆心，脚本推的时候又用像素值覆盖同一个 margin，
//   松手写回 0px → 圆心那套居中没了，旋钮永远歪在右下。现在居中交给 transform，margin 只当偏移用。
const knobCss = /\.gpstick \.knob\{([^}]*)\}/.exec(fs.readFileSync(HTML, 'utf8'));
ok(!!knobCss, '找不到 .gpstick .knob 这条 CSS');
if (knobCss) {
  ok(/translate\(\s*-50%\s*,\s*-50%\s*\)/.test(knobCss[1]), '旋钮没写成 transform 居中（靠负 margin 会被脚本覆盖）');
  ok(!/margin\s*:\s*-/.test(knobCss[1]), '旋钮 CSS 里还有负 margin');
}
ctx.GP.edit = false;
const kp = H.node('stick1').querySelector('.knob');
const kr = ctx.gpRects()['stick1'];
H.down('stick1', kr.cx + kr.r * 0.8, kr.cy + kr.r * 0.8);
ok(!!kp && parseFloat(kp.style.marginLeft) > 0, '推摇杆时旋钮没跟着动');
H.up('stick1');
ok(!!kp && kp.style.marginLeft === '0px' && kp.style.marginTop === '0px',
  '松手后旋钮没回圆心：margin ' + (kp ? kp.style.marginLeft + ' / ' + kp.style.marginTop : '没有旋钮') + ' → 看着就是歪在右下角');

// ---- 4. 老存档读进来必须被重排，但用户自己改的东西要留着 ----
//   只改代码不改存档是没用的：手机 localStorage 和电脑 bedremote.json 里存的还是旧数值，
//   他打开看到的仍然是坏样子。下面这份就是 0.11.1 那套只在竖屏成立的坐标（没 ver 标记）。
const legacy = {
  widgets: [
    { id: 'stick1', kind: 'stick', x: 0.035, y: 0.545, w: 0.39, h: 0.39, keys: ['w', 'a', 's', 'd'], run: 'shift', runAt: 0.82, dead: 0.16 },
    { id: 'look1', kind: 'look', x: 0.44, y: 0.03, w: 0.54, h: 0.58, sens: 1.5, invX: 1, invY: 1 },
    { id: 'b1', x: 0.615, y: 0.7, w: 0.235, h: 0.235, round: 1, label: '攻击', act: 'mb', k: 'left', mode: 'hold' },
    { id: 'b2', x: 0.505, y: 0.885, w: 0.165, h: 0.165, round: 1, label: '交互', act: 'key', k: 'e', mode: 'tap' },
    { id: 'b3', x: 0.7, y: 0.885, w: 0.165, h: 0.165, round: 1, label: '跳跃', act: 'key', k: 'space', mode: 'tap' },
    { id: 'b4', x: 0.865, y: 0.76, w: 0.125, h: 0.125, round: 1, label: '1', act: 'key', k: '1', mode: 'tap' },
    { id: 'b5', x: 0.865, y: 0.615, w: 0.125, h: 0.125, round: 1, label: '2', act: 'key', k: '2', mode: 'tap' }
  ]
};
legacy.widgets[2].label = '开火'; legacy.widgets[2].k = 'f'; legacy.widgets[3].alpha = 0.4;   // 假装用户改过名字/键位/透明度
legacy.widgets.push({ id: 'wcustom1', kind: 'btn', x: 0.02, y: 0.02, w: 0.1, h: 0.1, label: '我加的', act: 'key', k: 'q', mode: 'tap' });
const mig = ctx.gpFix(legacy);
const byId = function (id) { return mig.widgets.filter(function (w) { return w.id === id })[0] };
const def1 = function (id) { return ctx.GP_DEF.widgets.filter(function (w) { return w.id === id })[0] };
// 先确认这个常量真的存在：不查的话，老代码两边都是 undefined，这条会"绿着放过"
ok(typeof ctx.GP_LAY_VER === 'number', '代码里没有 GP_LAY_VER（布局版本号）');
ok(mig.ver === ctx.GP_LAY_VER, 'gpFix 没给布局打版本号 → 以后每次打开都要重排一遍，用户摆好的位置会被反复冲掉');
ok(byId('b1').x === def1('b1').x && byId('b1').y === def1('b1').y, '老存档里的出厂控件没被重排');
ok(byId('b1').label === '开火' && byId('b1').k === 'f', '重排把用户改的名字/键位冲掉了');
ok(byId('b2').alpha === 0.4, '重排把用户改的透明度冲掉了');
ok(byId('wcustom1').x === 0.02 && byId('wcustom1').y === 0.02, '用户自己加的控件被动过了');
const again = ctx.gpFix({ widgets: JSON.parse(JSON.stringify(mig.widgets)), ver: 2, opt: mig.opt });
ok(JSON.stringify(again.widgets) === JSON.stringify(mig.widgets), '带了版本号的存档还是被改了 —— 重排必须只做一次');

console.log(fails.length ? 'GP FIT CHECK: ' + fails.length + ' 条没过' : 'GP FIT CHECK: 全过');
fails.forEach(function (f) { console.log('  ! ' + f) });
process.exitCode = fails.length ? 1 : 0;
module.exports = { fails: fails, stages: STAGES.length };   // 让 node-repl 之类的宿主也能拿到结果
