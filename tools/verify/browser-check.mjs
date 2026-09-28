// 真实浏览器交互检查（用 Chrome DevTools Protocol 驱动）。
//
// 为什么需要它：Blazor 的渲染模式问题在纯服务端检查里看不出来 ——
// 布局如果被留在静态 SSR 里，HTML 看起来完全正常，只是 @onclick 不会绑定、
// 布局里的 JS 互操作会被拒绝。之前「提示的关闭按钮点不动」就是这个原因，
// 只有真的点一次才能发现。
//
// 用法（需要本机有 Chrome 与 Node 18+，不需要安装任何 npm 包）：
//   1) 启动应用：  dotnet run -- --port 5385
//   2) 启动 Chrome： "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome" \
//        --headless=new --remote-debugging-port=9222 --user-data-dir=/tmp/chrome-prof about:blank
//   3) 运行检查：  APP_URL=http://127.0.0.1:5385 node tools/verify/browser-check.mjs
//
// 检查内容：交互式渲染是否生效、提示能否弹出、关闭按钮是否真的能关掉、
// 提示是否会到时自动消失、布局里的 JS 互操作（主题切换）是否可用。

// 用 Chrome DevTools Protocol 真的点一次「提示的关闭按钮」。
// 只依赖 Node 内置的 WebSocket 与 fetch，不需要装任何包。
const APP = process.env.APP_URL || 'http://127.0.0.1:5385';
// 可选：给一个真实视频文件，用于检查「选模板 → 命令预览」这条链路
const SAMPLE = process.env.SAMPLE || '';
const CDP = process.env.CDP_URL || 'http://127.0.0.1:9222';

const sleep = ms => new Promise(r => setTimeout(r, ms));

async function findPageTarget() {
  for (let i = 0; i < 40; i++) {
    try {
      const list = await (await fetch(`${CDP}/json/list`)).json();
      const page = list.find(t => t.type === 'page' && t.webSocketDebuggerUrl);
      if (page) return page;
    } catch { /* Chrome 还没起来 */ }
    await sleep(500);
  }
  throw new Error('找不到可调试的页面目标');
}

class Session {
  constructor(ws) { this.ws = ws; this.id = 0; this.pending = new Map(); }
  static async open(url) {
    const ws = new WebSocket(url);
    await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });
    const s = new Session(ws);
    ws.onmessage = ev => {
      const msg = JSON.parse(ev.data);
      if (msg.id && s.pending.has(msg.id)) {
        const { resolve, reject } = s.pending.get(msg.id);
        s.pending.delete(msg.id);
        msg.error ? reject(new Error(JSON.stringify(msg.error))) : resolve(msg.result);
      }
    };
    return s;
  }
  send(method, params = {}) {
    const id = ++this.id;
    return new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      this.ws.send(JSON.stringify({ id, method, params }));
    });
  }
  async eval(expression) {
    const r = await this.send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
    if (r.exceptionDetails) throw new Error(r.exceptionDetails.text + ' ' + (r.exceptionDetails.exception?.description ?? ''));
    return r.result.value;
  }
  async waitFor(expression, label, timeoutMs = 15000) {
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
      if (await this.eval(expression)) return true;
      await sleep(200);
    }
    throw new Error(`等待超时: ${label}`);
  }
}

const results = [];
const check = (name, ok, detail = '') => {
  results.push({ name, ok, detail });
  console.log(`${ok ? '✅' : '❌'} ${name}${detail ? ' — ' + detail : ''}`);
};

const target = await findPageTarget();
const s = await Session.open(target.webSocketDebuggerUrl);
await s.send('Page.enable');
await s.send('Runtime.enable');

// 1) 打开设置页
await s.send('Page.navigate', { url: `${APP}/settings` });
await s.waitFor(`document.readyState === 'complete'`, '页面加载完成');

// 2) 等 Blazor 电路连上（这是判断「组件真的可交互」的依据）
await s.waitFor(`typeof window.Blazor !== 'undefined'`, 'blazor.web.js 已加载');
await sleep(2500);
const connected = await s.eval(`!!(window.Blazor && document.querySelector('.ffui-panel'))`);
check('页面已进入交互式渲染', connected);

// 3) 触发一个提示：依次尝试几个一定会弹提示的按钮，直到提示出现
const candidates = ['立即清理历史', '重新检测', '测试 ffprobe', '清空全部历史'];
let triggered = null;
for (const label of candidates) {
  const clicked = await s.eval(`
    (() => {
      const btns = [...document.querySelectorAll('button')];
      const b = btns.find(x => x.textContent.trim() === ${JSON.stringify(label)} && !x.disabled);
      if (!b) return false;
      b.click();
      return true;
    })()
  `);
  if (!clicked) continue;

  // 最多等 20 秒（硬件检测会比较久）
  const deadline = Date.now() + 20000;
  let appeared = false;
  while (Date.now() < deadline) {
    if (await s.eval(`document.querySelectorAll('.ffui-toast').length > 0`)) { appeared = true; break; }
    await sleep(250);
    // 清空历史会弹确认框，顺手确认掉
    await s.eval(`
      (() => {
        const dlg = document.querySelector('.ffui-modal-footer');
        if (!dlg) return false;
        const b = [...dlg.querySelectorAll('button')].find(x => /清空|确定|恢复默认|立即/.test(x.textContent));
        if (b) { b.click(); return true; }
        return false;
      })()
    `);
  }
  if (appeared) { triggered = label; break; }
}
check('点击按钮后出现提示', triggered !== null, triggered ? `通过「${triggered}」触发` : '所有候选按钮都没弹出提示');

// 4) 提示内容与关闭按钮结构
const toastCount = await s.eval(`document.querySelectorAll('.ffui-toast').length`);
const toastText = await s.eval(`document.querySelector('.ffui-toast')?.textContent?.trim() ?? ''`);
check('提示内容非空', toastText.length > 0, `${toastCount} 条: ${toastText.slice(0, 50)}`);

// 5) 点关闭按钮 —— 这正是之前点不动的地方
const clickInfo = await s.eval(`
  (() => {
    const btn = document.querySelector('.ffui-toast-close');
    if (!btn) return { found: false };
    const r = btn.getBoundingClientRect();
    return { found: true, tag: btn.tagName, disabled: btn.disabled, w: Math.round(r.width), h: Math.round(r.height), label: btn.getAttribute('aria-label') };
  })()
`);
check('关闭按钮存在且可点击', clickInfo.found && clickInfo.w > 0 && clickInfo.h > 0, JSON.stringify(clickInfo));

const before = await s.eval(`document.querySelectorAll('.ffui-toast').length`);
await s.eval(`document.querySelector('.ffui-toast-close').click()`);
await sleep(1200);
const after = await s.eval(`document.querySelectorAll('.ffui-toast').length`);

check('点击关闭后提示消失（本次修复的目标）', after < before, `${before} → ${after}`);

// 6) 再验证一次：新提示应当自动消失，不需要手动关
async function triggerToast(label) {
  await s.eval(`
    (() => {
      const b = [...document.querySelectorAll('button')].find(x => x.textContent.trim() === ${JSON.stringify('PLACEHOLDER')} && !x.disabled);
      if (!b) return false;
      b.click(); return true;
    })()`.replace('PLACEHOLDER', label));
}
await triggerToast(triggered);
await s.waitFor(`document.querySelectorAll('.ffui-toast').length > 0`, '再次出现提示');
console.log('   （等待自动消失，最长 15 秒…）');
let autoGone = false;
for (let i = 0; i < 75; i++) {
  if (await s.eval(`document.querySelectorAll('.ffui-toast').length === 0`)) { autoGone = true; break; }
  await sleep(200);
}
check('提示到时自动消失（不需要手动关）', autoGone);

// 7) 布局里的其它交互也应当恢复：主题切换是 MainLayout/设置页里的 JS 互操作
const themeBefore = await s.eval(`document.documentElement.getAttribute('data-theme')`);
await s.eval(`
  (() => {
    const sel = document.querySelector('#theme') || [...document.querySelectorAll('select')].find(x => [...x.options].some(o => o.value === 'Dark'));
    if (!sel) return false;
    sel.value = 'Dark';
    sel.dispatchEvent(new Event('change', { bubbles: true }));
    return true;
  })()
`);
await sleep(1500);
const themeAfter = await s.eval(`document.documentElement.getAttribute('data-theme')`);
check('主题切换即时生效（布局 JS 互操作恢复）', themeAfter === 'Dark', `${themeBefore} → ${themeAfter}`);

// 8) 主题必须能记住：整页重新加载后仍然是深色。
//    这里曾经有过一个真 bug —— 切换只调用 applyTheme 而不持久化，
//    于是 MainLayout 在下次加载时用「已保存的设置」把主题和 Cookie 一起覆盖回去，
//    表现是「选了深色、刷新一下又变回浅色」。
await s.send('Page.navigate', { url: `${APP}/settings` });
await s.waitFor(`document.readyState === 'complete' && typeof window.Blazor !== 'undefined'`, '设置页就绪');
await sleep(3000);
const themeAfterReload = await s.eval(`document.documentElement.getAttribute('data-theme')`);
check('整页重新加载后主题仍然保持深色', themeAfterReload === 'Dark', `data-theme=${themeAfterReload}`);
const cookieValue = await s.eval(`document.cookie`);
check('主题写进了 Cookie（服务端首屏据此渲染）', /ffui-theme=Dark/.test(cookieValue), cookieValue);

// 换回浅色，避免影响其它检查
await s.eval(`
  (() => {
    const sel = document.querySelector('#theme-select') || [...document.querySelectorAll('select')].find(x => [...x.options].some(o => o.value === 'Light'));
    if (!sel) return false;
    const setter = Object.getOwnPropertyDescriptor(window.HTMLSelectElement.prototype, 'value').set;
    setter.call(sel, 'Light');
    sel.dispatchEvent(new Event('change', { bubbles: true }));
    return true;
  })()
`);
await sleep(1000);

// 9) 转换页：选出模板后必须立刻有命令预览，且换模板后要跟着更新。
//    这里曾经有两个真 bug —— 选完模板预览是空的；换模板后预览还是上一个模板的命令
//    （@bind 只给字段赋值，不会触发 OnParametersSetAsync，所以重建逻辑从未执行）。
if (SAMPLE) {
  await s.send('Page.navigate', { url: `${APP}/convert` });
  await s.waitFor(`document.readyState === 'complete' && typeof window.Blazor !== 'undefined'`, '转换页就绪');
  await sleep(3500);

  const setVal = (selector, value) => s.eval(`
    (() => {
      const el = document.querySelector(${JSON.stringify(selector)});
      if (!el) return false;
      const proto = el.tagName === 'SELECT' ? window.HTMLSelectElement.prototype : window.HTMLInputElement.prototype;
      Object.getOwnPropertyDescriptor(proto, 'value').set.call(el, ${JSON.stringify(value)});
      el.dispatchEvent(new Event('input', { bubbles: true }));
      el.dispatchEvent(new Event('change', { bubbles: true }));
      el.blur();
      return true;
    })()`);

  await setVal('input[placeholder*="输入视频"]', SAMPLE);
  await sleep(2500);

  await s.eval(`(() => {
    const b = [...document.querySelectorAll('button')].find(x => x.textContent.includes('MP4 · H.264') && !x.disabled);
    if (b) b.click();
    return !!b;
  })()`);
  await sleep(2500);

  const cmd1 = await s.eval(`document.querySelector('.command-code')?.textContent?.trim() ?? ''`);
  check('选完模板后立即出现命令预览', cmd1.length > 20, cmd1.slice(0, 60));
  check('命令里包含解析出的编码器（不是空占位符）',
    !cmd1.includes('{') && /-c:v\s+\S+/.test(cmd1), cmd1.slice(0, 80));

  const out1 = await s.eval(`[...document.querySelectorAll('.preview-row')][1]?.textContent ?? ''`);
  check('输出路径已确定（不是「尚未确定」）', !out1.includes('尚未确定'), out1.trim().slice(0, 60));

  // 换一个模板，命令必须跟着变
  await s.eval(`(() => {
    const b = [...document.querySelectorAll('button')].find(x => x.textContent.includes('MP4 · H.265') && !x.disabled);
    if (b) b.click();
    return !!b;
  })()`);
  await sleep(2500);
  const cmd2 = await s.eval(`document.querySelector('.command-code')?.textContent?.trim() ?? ''`);
  check('换模板后命令随之更新（不会残留上一个模板的命令）', cmd2.length > 20 && cmd2 !== cmd1,
    cmd2.slice(0, 80));
  check('新命令确实是新模板的编码器', /hevc|265/i.test(cmd2), cmd2.slice(0, 80));

  // 不该出现「只有按钮、没有文字」的空提示框
  const emptyAlerts = await s.eval(`
    [...document.querySelectorAll('.ffui-alert')].filter(a => a.textContent.replace(/\\s+/g,'').length <= 2).length`);
  check('没有空白的提示框', emptyAlerts === 0, `空提示框数量=${emptyAlerts}`);
} else {
  console.log('⏭️  未提供 SAMPLE，跳过命令预览相关检查');
}

console.log('');
const failed = results.filter(r => !r.ok);
console.log(`结果：通过 ${results.length - failed.length} / 失败 ${failed.length}`);
process.exit(failed.length === 0 ? 0 : 1);
