// 生成 README 用的界面截图（真实浏览器 + DevTools Protocol）。
//
// 为什么要脚本化：界面会不断改，手动截图很快就会过期，
// 而且空页面（没有文件、没有任务）截图出来很难看，
// 所以这里会先把界面驱动到有内容的状态，再截图。
//
// 用法（需要本机有 Chrome 与 Node 18+，不需要安装任何 npm 包）：
//   1) 启动应用：  dotnet run -- --port 5385
//   2) 启动 Chrome： "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome" \
//        --headless=new --remote-debugging-port=9222 --user-data-dir=/tmp/chrome-prof about:blank
//   3) 生成截图：  APP_URL=http://127.0.0.1:5385 SAMPLE=/path/to/sample.mp4 \
//        node tools/screenshots/capture.mjs
//
// 产物写入 readmefiles/。

import { writeFileSync, mkdirSync } from 'node:fs';

const APP = process.env.APP_URL || 'http://127.0.0.1:5385';
const CDP = process.env.CDP_URL || 'http://127.0.0.1:9222';
const SAMPLE = process.env.SAMPLE || '';
// 输出目录：默认写到临时目录，避免生成截图时把文件写到用户的影片文件夹里
const OUTDIR = process.env.OUTDIR || '/tmp/ffmpegwebui-demo-out';
const OUT_DIR = process.env.OUT_DIR || 'readmefiles';
const WIDTH = Number(process.env.WIDTH || 1400);
const HEIGHT = Number(process.env.HEIGHT || 880);
const SCALE = Number(process.env.SCALE || 2);

const sleep = ms => new Promise(r => setTimeout(r, ms));

class Cdp {
  constructor(ws) { this.ws = ws; this.id = 0; this.pending = new Map(); }
  static async connect(url) {
    const ws = new WebSocket(url);
    await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });
    const c = new Cdp(ws);
    ws.onmessage = ev => {
      const msg = JSON.parse(ev.data);
      const entry = c.pending.get(msg.id);
      if (!entry) return;
      c.pending.delete(msg.id);
      msg.error ? entry.reject(new Error(JSON.stringify(msg.error))) : entry.resolve(msg.result);
    };
    return c;
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
    if (r.exceptionDetails) throw new Error(r.exceptionDetails.text);
    return r.result.value;
  }
  async waitFor(expression, label, timeoutMs = 20000) {
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
      try { if (await this.eval(expression)) return true; } catch { /* 导航中 */ }
      await sleep(200);
    }
    console.log(`   ⚠️  等待超时: ${label}`);
    return false;
  }
  async goto(path) {
    await this.send('Page.navigate', { url: APP + path });
    await this.waitFor(`document.readyState === 'complete' && typeof window.Blazor !== 'undefined'`, '页面就绪');
    // 等首屏的异步加载（环境检测、硬件扫描、模板列表）稳定下来
    await sleep(3500);
  }
  async shot(name) {
    const { data } = await this.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
    const file = `${OUT_DIR}/${name}.png`;
    mkdirSync(OUT_DIR, { recursive: true });
    writeFileSync(file, Buffer.from(data, 'base64'));
    const size = Buffer.from(data, 'base64').length;
    console.log(`   📸 ${file} (${(size / 1024).toFixed(0)} KB)`);
  }
}

// 把值写进受 Blazor 控制的输入框，并触发绑定
const setInput = (selector, value) => `
  (() => {
    const el = document.querySelector(${JSON.stringify(selector)});
    if (!el) return false;
    const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;
    setter.call(el, ${JSON.stringify(value)});
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
    el.blur();
    return true;
  })()`;

const clickByText = (text, tag = 'button') => `
  (() => {
    const el = [...document.querySelectorAll(${JSON.stringify(tag)})]
      .find(x => x.textContent.trim().includes(${JSON.stringify(text)}) && !x.disabled);
    if (!el) return false;
    el.click();
    return true;
  })()`;

const list = await (await fetch(`${CDP}/json/list`)).json();
const target = list.find(t => t.type === 'page' && t.webSocketDebuggerUrl);
if (!target) throw new Error('找不到可调试页面，Chrome 起来了吗？');

const cdp = await Cdp.connect(target.webSocketDebuggerUrl);
await cdp.send('Page.enable');
await cdp.send('Runtime.enable');
await cdp.send('Emulation.setDeviceMetricsOverride', {
  width: WIDTH, height: HEIGHT, deviceScaleFactor: SCALE, mobile: false
});

console.log(`分辨率 ${WIDTH}×${HEIGHT} @${SCALE}x  →  ${WIDTH * SCALE}×${HEIGHT * SCALE}px`);

// ── 1. 首页 ─────────────────────────────────────────────────────────
console.log('首页');
await cdp.goto('/');
await cdp.waitFor(`document.querySelectorAll('[class*=encoder]').length > 0`, '编码器卡片');
await cdp.shot('index');

// ── 2. 转换页（带输入文件、模板、命令预览）────────────────────────
console.log('转换');
await cdp.goto('/convert');
if (SAMPLE) {
  await cdp.eval(setInput('input[placeholder*="输入视频"]', SAMPLE));
  await sleep(2500);                           // 等 ffprobe 读取媒体信息
  await cdp.eval(setInput('input[placeholder*="保存目录"]', OUTDIR));
  await sleep(600);
  await cdp.eval(setInput('#output-name', '演示输出'));
  await sleep(600);
  // 选一个会自动挑编码器的模板，命令预览里能直接看到用了哪个编码器
  await cdp.eval(clickByText('MP4 · H.264'));
  await sleep(2000);
}
await cdp.shot('cv');

// 再拍一张滚到「模板参数 + 命令预览」的图：这是最能体现本工具价值的一屏
if (SAMPLE) {
  await cdp.eval(`
    (() => {
      const h = [...document.querySelectorAll('h2')].find(x => x.textContent.includes('确认并开始'));
      if (h) h.scrollIntoView({ block: 'start' });
      return true;
    })()`);
  await sleep(1200);
  await cdp.shot('cv-preview');
}

// ── 3. 真正跑一次转换，让任务列表有内容 ─────────────────────────────
if (SAMPLE) {
  console.log('跑一次真实转换（给任务列表准备数据）');
  await cdp.eval(clickByText('开始转换'));
  await cdp.waitFor(
    `[...document.querySelectorAll('.ffui-toast')].some(t => /完成|失败/.test(t.textContent))`,
    '转换结束', 120000);
  await sleep(1500);
  await cdp.shot('cv-running');
}

// ── 4. 模板页 ───────────────────────────────────────────────────────
console.log('模板');
await cdp.goto('/templates');
await cdp.waitFor(`document.querySelectorAll('.template-card, [class*=card]').length > 3`, '模板卡片');
await cdp.shot('tpl');

// ── 5. 批量处理（先加几个文件）──────────────────────────────────────
console.log('批量处理');
await cdp.goto('/batch');
if (SAMPLE) {
  await cdp.eval(setInput('input[placeholder*="粘贴文件"]', SAMPLE));
  await sleep(400);
  await cdp.eval(clickByText('添加'));
  await sleep(1800);
  await cdp.eval(setInput('input[placeholder*="保存目录"]', OUTDIR));
  await sleep(600);
  await cdp.eval(clickByText('MP4 · H.264', 'button'));
  await sleep(1500);
}
await cdp.shot('batch');

// ── 6. 任务列表 ─────────────────────────────────────────────────────
console.log('任务列表');
await cdp.goto('/tasks');
await sleep(1200);
await cdp.eval(`
  (() => {
    const b = [...document.querySelectorAll('button')].find(x => x.textContent.trim() === '详情' || x.textContent.trim() === '日志');
    if (b) b.click();
    return true;
  })()`);
await sleep(1200);
await cdp.shot('tasks');

// ── 7. 设置页（展示硬件能力与各项开关）──────────────────────────────
console.log('设置');
await cdp.goto('/settings');
await cdp.waitFor(`document.querySelectorAll('[class*=encoder]').length > 0`, '硬件编码器列表');
await cdp.shot('setting');

// ── 8. 深色主题 ─────────────────────────────────────────────────────
// 主题选择器只在「设置」页上，所以必须先在那里切主题（顺便写入 cookie），
// 再整页重新加载其它页面 —— 服务端会从 cookie 读出主题，首屏就是深色。
console.log('深色主题');
await cdp.goto('/settings');
const themeSet = await cdp.eval(`
  (() => {
    const sel = document.querySelector('#theme-select');
    if (!sel) return 'no-select';
    const setter = Object.getOwnPropertyDescriptor(window.HTMLSelectElement.prototype, 'value').set;
    setter.call(sel, 'Dark');
    sel.dispatchEvent(new Event('change', { bubbles: true }));
    return 'ok';
  })()`);
if (themeSet !== 'ok') console.log(`   ⚠️  没有找到主题选择器: ${themeSet}`);
await sleep(1500);

const themeNow = await cdp.eval(`document.documentElement.getAttribute('data-theme')`);
if (themeNow !== 'Dark') console.log(`   ⚠️  主题切换没生效，当前 data-theme=${themeNow}`);

await cdp.goto('/');
const afterReload = await cdp.eval(`document.documentElement.getAttribute('data-theme')`);
if (afterReload !== 'Dark') console.log(`   ⚠️  刷新后主题没有保持住: ${afterReload}`);
await cdp.shot('dark-index');

await cdp.goto('/convert');
if (SAMPLE) {
  await cdp.eval(setInput('input[placeholder*="输入视频"]', SAMPLE));
  await sleep(2200);
  await cdp.eval(setInput('input[placeholder*="保存目录"]', OUTDIR));
  await sleep(600);
  await cdp.eval(clickByText('MP4 · H.265'));
  await sleep(1800);
}
await cdp.shot('dark-convert');

// 换回浅色
await cdp.goto('/settings');
await cdp.eval(`
  (() => {
    const sel = document.querySelector('#theme-select');
    if (!sel) return false;
    const setter = Object.getOwnPropertyDescriptor(window.HTMLSelectElement.prototype, 'value').set;
    setter.call(sel, 'Light');
    sel.dispatchEvent(new Event('change', { bubbles: true }));
    return true;
  })()`);
await sleep(1200);

console.log('\n完成。');
process.exit(0);
