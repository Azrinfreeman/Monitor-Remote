// Visual QA helper: drives headless Edge over CDP against a demo server and saves screenshots.
// Usage: node tests/screenshots.mjs <port> <pairing code> <output dir>
import {spawn} from 'node:child_process';
import {mkdirSync, writeFileSync} from 'node:fs';
const [port, code, out] = process.argv.slice(2);
mkdirSync(out, {recursive: true});
const edge = 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe';
const proc = spawn(edge, ['--headless=new', '--remote-debugging-port=9333', '--no-first-run', '--user-data-dir=' + out + '/profile', '--hide-scrollbars', 'about:blank'], {stdio: 'ignore'});
const sleep = ms => new Promise(r => setTimeout(r, ms));
let target;
for (let i = 0; i < 50 && !target; i++) { try { target = (await (await fetch('http://127.0.0.1:9333/json')).json()).find(t => t.type === 'page'); } catch { await sleep(200); } }
const ws = new WebSocket(target.webSocketDebuggerUrl); await new Promise(r => ws.onopen = r);
let id = 0; const waiting = new Map();
ws.onmessage = e => { const m = JSON.parse(e.data); if (m.id && waiting.has(m.id)) { waiting.get(m.id)(m); waiting.delete(m.id); } };
const send = (method, params = {}) => new Promise(r => { const n = ++id; waiting.set(n, r); ws.send(JSON.stringify({id: n, method, params})); });
const js = async expression => (await send('Runtime.evaluate', {expression, awaitPromise: true, returnByValue: true})).result?.result?.value;
const shot = async name => { const r = await send('Page.captureScreenshot', {format: 'png'}); writeFileSync(`${out}/${name}.png`, Buffer.from(r.result.data, 'base64')); };
const view = (width, height, scheme, reduced = 'no-preference') => Promise.all([
  send('Emulation.setDeviceMetricsOverride', {width, height, deviceScaleFactor: 2, mobile: true}),
  send('Emulation.setEmulatedMedia', {features: [{name: 'prefers-color-scheme', value: scheme}, {name: 'prefers-reduced-motion', value: reduced}]})]);
await send('Page.enable'); await send('Runtime.enable');
const errors = []; ws.addEventListener('message', e => { const m = JSON.parse(e.data); if (m.method === 'Runtime.exceptionThrown') errors.push(m.params.exceptionDetails.text + ' ' + (m.params.exceptionDetails.exception?.description ?? '')); });
await send('Page.navigate', {url: `http://localhost:${port}/`});
await sleep(1500);
for (const scheme of ['light', 'dark']) {
  await view(390, 844, scheme);
  await sleep(300); await shot(`${scheme}-1-pair`);
}
await js(`(async () => { const r = await fetch('/api/pair', {method:'POST', headers:{'Content-Type':'application/json','X-Monitor-Request':'1'}, body: JSON.stringify({code:'${code}'})}); return r.status; })()`);
await send('Page.navigate', {url: `http://localhost:${port}/`}); await sleep(2000);
for (const scheme of ['light', 'dark']) {
  await view(390, 844, scheme); await sleep(400);
  await shot(`${scheme}-2-displays`);
  for (const tab of ['desktop', 'audio']) {
    await js(`document.getElementById('tab-${tab}').click()`); await sleep(900);
    if (tab === 'desktop') {
      await js(`document.getElementById('remote-start').click()`); await sleep(2500);
    }
    if (tab === 'audio') { await js(`document.getElementById('audio-refresh').click()`); await sleep(1500); }
    await shot(`${scheme}-3-${tab}`);
    if (tab === 'desktop') { await js(`document.getElementById('remote-keyboard').open = true`); await sleep(600); await shot(`${scheme}-3-desktop-keyboard`); await js(`document.getElementById('remote-keyboard').open = false`); await js(`document.getElementById('remote-stop').click()`); await sleep(500); }
  }
  await js(`document.getElementById('tab-monitors').click()`); await sleep(500);
  await js(`document.getElementById('more').click()`); await sleep(700); await shot(`${scheme}-4-sheet`);
  await js(`document.getElementById('scrim').click()`); await sleep(500);
}
await view(844, 390, 'light'); await sleep(500); await shot('landscape-displays');
await view(375, 667, 'light'); await sleep(500); await shot('se-displays');
console.log('JS errors:', errors.length ? errors : 'none');
ws.close(); proc.kill();
process.exit(0);
