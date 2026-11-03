// Real-browser QA using the repository's dependency-free Edge/CDP approach.
// node tests/power-browser.mjs <demo port> <pairing code> <new output directory>
// Optional fourth argument: a loopback tutorial-preview URL to capture the HTML companion too.
import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {mkdirSync, writeFileSync, existsSync} from 'node:fs';
import {resolve, join} from 'node:path';
import {createServer} from 'node:net';

const [port, code, directory, tutorialUrl] = process.argv.slice(2);
if (tutorialUrl) assert(/^http:\/\/127\.0\.0\.1:\d+\/?$/.test(tutorialUrl),'Tutorial preview must be on a loopback HTTP port.');
assert(/^\d{1,5}$/.test(port) && /^\d{6}$/.test(code) && directory, 'Provide demo port, six-digit code and output directory.');
const out = resolve(directory);
assert(!existsSync(out), 'Use a new output directory to preserve previous captures.');
mkdirSync(out, {recursive:true});
const probe = createServer(); await new Promise(r=>probe.listen(0,'127.0.0.1',r));
const debugPort = probe.address().port; await new Promise(r=>probe.close(r));
const edge = spawn('C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', ['--headless=new', '--disable-gpu', '--disable-extensions', '--no-proxy-server', '--remote-debugging-port='+debugPort, '--no-first-run', '--no-default-browser-check', '--user-data-dir='+join(out,'profile'), '--hide-scrollbars', 'about:blank'], {stdio:'ignore', windowsHide:true});
const sleep = ms=>new Promise(r=>setTimeout(r,ms));
let socket;
try {
  let target;
  for (let i=0;i<50&&!target;i++) { try { target=(await(await fetch(`http://127.0.0.1:${debugPort}/json`)).json()).find(t=>t.type==='page'); } catch { await sleep(100); } }
  assert(target,'Edge did not expose a page.');
  socket=new WebSocket(target.webSocketDebuggerUrl); await new Promise(r=>socket.onopen=r);
  let nextId=0, allowDialogs=false; const pending=new Map(), errors=[], confirmations=[];
  socket.onmessage=event=>{
    const message=JSON.parse(event.data);
    if(message.id&&pending.has(message.id)) { pending.get(message.id)(message); pending.delete(message.id); }
    if(message.method==='Runtime.exceptionThrown') errors.push(message.params.exceptionDetails.exception?.description||message.params.exceptionDetails.text);
    if(message.method==='Page.javascriptDialogOpening') {
      confirmations.push(message.params.message);
      void send('Page.handleJavaScriptDialog',{accept:allowDialogs&&message.params.type==='confirm'});
    }
  };
  const send=async(method,params={})=>{
    let timeout;
    const id=++nextId;
    try {
      const result=await new Promise((resolve,reject)=>{
        timeout=setTimeout(()=>reject(Error('CDP timeout: '+method+' '+(params.url||''))),20000);
        pending.set(id,resolve);socket.send(JSON.stringify({id,method,params}));
      });
      if(result.error) throw Error(JSON.stringify(result.error)); return result.result;
    } finally { clearTimeout(timeout);pending.delete(id); }
  };
  const js=async expression=>{const result=await send('Runtime.evaluate',{expression,awaitPromise:true,returnByValue:true});if(result.exceptionDetails)throw Error(result.exceptionDetails.text);return result.result.value;};
  const wait=async expression=>{for(let i=0;i<100;i++){if(await js(expression))return;await sleep(150);}throw Error('Condition failed: '+expression);};
  const capture=async name=>{const shot=await send('Page.captureScreenshot',{format:'png'});writeFileSync(join(out,name+'.png'),Buffer.from(shot.data,'base64'));};
  await send('Page.enable'); await send('Runtime.enable');
  await send('Page.navigate',{url:`http://127.0.0.1:${port}/`});
  await wait("!!window.monitorApp");
  assert.equal(await js(`(async()=>{const response=await fetch('/api/pair',{method:'POST',headers:{'Content-Type':'application/json','X-Monitor-Request':'1'},body:JSON.stringify({code:${JSON.stringify(code)}})});return response.status;})()`),200);
  await send('Page.navigate',{url:`http://127.0.0.1:${port}/`});
  await wait("!!window.monitorApp?.connected && !!window.powerUI");
  // Mandatory barrier: this script is incapable of creating tasks against a non-demo server.
  assert.equal(await js("(async()=>{const status=await(await fetch('/api/status')).json();const power=await(await fetch('/api/pc-power')).json();return status.simulated&&power.simulated&&power.allowPower;})()"),true,'Require MONITOR_DEMO=1, isolated storage and --allow-pc-power.');
  allowDialogs=true;
  await js("document.getElementById('tab-power').click()");
  await wait("!document.getElementById('pc-power-submit').disabled");
  const metrics=[];
  for(const [width,height,scheme,reduced] of [[390,844,'light','no-preference'],[375,667,'dark','reduce'],[844,390,'light','reduce']]) {
    await send('Emulation.setDeviceMetricsOverride',{width,height,deviceScaleFactor:1,mobile:true});
    await send('Emulation.setEmulatedMedia',{features:[{name:'prefers-color-scheme',value:scheme},{name:'prefers-reduced-motion',value:reduced}]});
    await js("document.getElementById('pc-power-mode').value='at';document.getElementById('pc-power-mode').dispatchEvent(new Event('change'));scrollTo(0,0)");
    await sleep(350);
    const metric=await js("({width:innerWidth,height:innerHeight,scrollWidth:document.documentElement.scrollWidth,tabCount:document.querySelectorAll('.tabbar .tab').length,background:getComputedStyle(document.body).backgroundColor,countdownLive:document.getElementById('pc-power-countdown').getAttribute('aria-live')})");
    assert(metric.scrollWidth<=width);assert.equal(metric.tabCount,4);assert.equal(metric.countdownLive,'off');metrics.push({...metric,scheme,reduced});
    await capture(`${width}-${height}-${scheme}-date`);
  }
  await send('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
  await js(`(async()=>{
    const power=await(await fetch('/api/pc-power')).json();
    const match=power.pcLocalTime.match(/([+-])(\\d\\d):(\\d\\d)$/);
    const offset=match?(Number(match[2])*60+Number(match[3]))*60000*(match[1]==='-'?-1:1):0;
    document.getElementById('pc-power-at').value=new Date(Date.parse(power.serverNowUtc)+120000+offset).toISOString().slice(0,16);
    document.getElementById('pc-power-action').value='sleep';document.getElementById('pc-power-action').dispatchEvent(new Event('change'));
    document.getElementById('pc-power-mode').value='at';document.getElementById('pc-power-mode').dispatchEvent(new Event('change'));
    document.getElementById('pc-power-form').requestSubmit();
  })()`);
  await wait("!!window.powerUI.pending");
  assert.equal(await js("(async()=>{const power=await(await fetch('/api/pc-power')).json();return power.current.action==='sleep'&&power.current.mode==='at';})()"),true);
  await capture('scheduled-sleep');await js("document.getElementById('pc-power-cancel').click()");await wait("!window.powerUI.pending");await wait("!document.getElementById('pc-power-submit').disabled");
  await js("document.getElementById('pc-power-action').value='shutdown';document.getElementById('pc-power-action').dispatchEvent(new Event('change'));document.getElementById('pc-power-mode').value='delay';document.getElementById('pc-power-mode').dispatchEvent(new Event('change'));document.getElementById('pc-power-form').requestSubmit()");
  await wait("!!window.powerUI.pending"); await capture('pending-timer');
  await js("document.getElementById('pc-power-cancel').click()"); await wait("!window.powerUI.pending");
  await wait("!document.getElementById('pc-power-submit').disabled");
  await js("document.getElementById('pc-power-mode').value='now';document.getElementById('pc-power-mode').dispatchEvent(new Event('change'));document.getElementById('pc-power-action').value='restart';document.getElementById('pc-power-action').dispatchEvent(new Event('change'));document.getElementById('pc-power-form').requestSubmit()");
  await wait("!!window.powerUI.pending");
  await wait("!window.powerUI.pending && document.getElementById('pc-power-result').textContent.includes('Simulated:')");
  await capture('simulated-restart');
  if(tutorialUrl) {
    await send('Emulation.setDeviceMetricsOverride',{width:900,height:1000,deviceScaleFactor:1,mobile:false});
    await send('Page.navigate',{url:tutorialUrl}); await wait("document.readyState==='complete'");
    const size=await js("({width:document.documentElement.scrollWidth,height:document.documentElement.scrollHeight})");
    assert(size.width<=900,'Tutorial has horizontal overflow.');
    const shot=await send('Page.captureScreenshot',{format:'png',captureBeyondViewport:true,clip:{x:0,y:0,width:900,height:size.height,scale:1}});
    writeFileSync(join(out,'tutorial.png'),Buffer.from(shot.data,'base64'));
  }
  assert.deepEqual(errors,[],'No JavaScript runtime errors.');
  assert.equal(confirmations.length,3); assert(confirmations.every(text=>text.includes('Unsaved work')));
  writeFileSync(join(out,'results.json'),JSON.stringify({metrics,errors,confirmations,flows:['pairing','date/time sleep scheduling and cancel','timer confirmation and cancel','immediate simulated restart'],nativePower:false},null,2));
  console.log('PASS: Edge portrait, narrow dark/reduced-motion and landscape; timer/cancel and simulated restart; zero overflow/runtime errors.');
} finally { socket?.close(); edge.kill(); }
