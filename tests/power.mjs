import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';
import {webcrypto} from 'node:crypto';

// Minimal DOM/transport fixture: runs the owned script, without a browser or native power.
function setup() {
  const elements = new Map();
  for (const [, id] of readFileSync(new URL('../wwwroot/index.html', import.meta.url), 'utf8').matchAll(/id="([^"]+)"/g)) {
    elements.set(id, {value:'', textContent:'', hidden:false, disabled:false, listeners:{}, addEventListener(name, fn) { this.listeners[name] = fn; }, querySelector() { return null; }});
  }
  const get = id => elements.get(id);
  get('pc-power-action').value = 'shutdown'; get('pc-power-mode').value = 'now'; get('pc-power-hours').value = '0'; get('pc-power-minutes').value = '30';
  const requests = [], timers = [], confirmations = [], storage = new Map(), listeners = {};
  const app = {connected:true, api(path, data) { return new Promise((resolve, reject) => requests.push({path, data, resolve, reject})); }};
  const document = {hidden:false, getElementById:get, addEventListener(name, fn) { listeners[name] = fn; }};
  // getRandomValues path also works on trusted-LAN HTTP, where randomUUID may be unavailable.
  const window = {monitorApp:app, crypto:{getRandomValues:array=>webcrypto.getRandomValues(array)}};
  const context = vm.createContext({document, window, Date, console, Uint8Array, Number, Math,
    sessionStorage:{getItem:key=>storage.get(key),setItem:(key,value)=>storage.set(key,value),removeItem:key=>storage.delete(key)},
    confirm:text=>{confirmations.push(text); return true;}, setInterval(fn, ms) { timers.push({fn,ms}); }});
  vm.runInContext(readFileSync(new URL('../wwwroot/power.js', import.meta.url), 'utf8'), context);
  return {get, requests, timers, confirmations, storage, app, document, window, listeners};
}
const tick = async () => { for (let i = 0; i < 20; i++) await Promise.resolve(); };
const snapshot = () => ({available:true, allowPower:true, sleepSupported:true, pcName:'Test PC', serverNowUtc:new Date().toISOString(), pcLocalTime:new Date().toISOString(), timeZoneId:'UTC', timeZoneName:'UTC', current:null, history:[], simulated:true});
const task = (requestId='previous') => ({id:'task-id', requestId, action:'shutdown', mode:'now', state:'pending', revision:1, dueAtUtc:new Date(Date.now()+60000).toISOString(), dueAtLocal:'2026-11-04T23:30:00+08:00'});
async function load(ui, state=snapshot()) { ui.window.powerUI.tab(true); ui.requests.at(-1).resolve(state); await tick(); }

{
  const ui = setup(); await load(ui, {...snapshot(),allowPower:false}); assert(ui.get('pc-power-submit').disabled);
  ui.get('pc-power-refresh').onclick(); ui.requests.at(-1).resolve(snapshot()); await tick();
  assert(!ui.get('pc-power-submit').disabled);
  ui.get('pc-power-mode').value='delay'; ui.get('pc-power-mode').onchange(); assert.equal(ui.get('pc-power-delay').hidden,false);
  ui.get('pc-power-hours').value='1'; ui.get('pc-power-minutes').value='15';
  const sending=ui.get('pc-power-form').listeners.submit({preventDefault(){}});
  const request=ui.requests.at(-1); assert.equal(request.path,'pc-power/tasks'); assert.equal(request.data.delaySeconds,4500);
  assert.match(request.data.requestId,/^[0-9a-f-]{36}$/); assert.match(ui.confirmations.at(-1),/Test PC/); assert.match(ui.confirmations.at(-1),/Unsaved work/);
  const accepted=task(request.data.requestId); request.resolve({task:accepted,serverNowUtc:new Date().toISOString()}); await tick();
  assert.equal(ui.requests.at(-1).path,'pc-power'); ui.requests.at(-1).resolve({...snapshot(),current:accepted}); await sending;
  assert.equal(ui.window.powerUI.pending,true); assert(ui.get('pc-power-submit').disabled); assert.match(ui.get('pc-power-due').textContent,/2026-11-04 23:30/);
  const cancelling=ui.get('pc-power-cancel').onclick(); assert.equal(ui.requests.at(-1).data.revision,1);
  ui.requests.at(-1).resolve({task:{...accepted,state:'cancelled'}}); await tick();
  ui.requests.at(-1).resolve({...snapshot(),history:[{...accepted,state:'cancelled'}]}); await cancelling;
  assert.equal(ui.window.powerUI.pending,false); assert.match(ui.get('pc-power-result').textContent,/Cancelled/);
  console.log('PASS: power permission, timer confirmation, HTTP-compatible UUID, PC-local deadline and cancellation');
}
{
  const ui=setup(); await load(ui);
  ui.get('pc-power-mode').value='at'; ui.get('pc-power-mode').onchange(); ui.get('pc-power-at').value='2026-12-04T20:00';
  const sending=ui.get('pc-power-form').listeners.submit({preventDefault(){}}); const request=ui.requests.at(-1);
  assert.equal(request.data.localDateTime,'2026-12-04T20:00'); assert.equal(request.data.timeZoneId,'UTC'); assert(!Object.hasOwn(request.data,'delaySeconds'));
  const error=Error('Another task exists'); error.status=409; error.details={code:'task_conflict',current:task()}; request.reject(error); await tick();
  assert.equal(ui.requests.at(-1).path,'pc-power','conflict refreshes instead of automatically retrying');
  ui.requests.at(-1).resolve({...snapshot(),current:task()}); await sending; assert(ui.get('pc-power-submit').disabled);
  assert.equal(ui.get('pc-power-result').textContent,'Another task exists','status reconciliation preserves the actionable error');
  console.log('PASS: date/time stays in PC zone and conflicts reconcile through a read');
}
{
  const ui=setup(); await load(ui);
  const sending=ui.get('pc-power-form').listeners.submit({preventDefault(){}}); const request=ui.requests.at(-1); const requestId=request.data.requestId;
  request.reject(Error('Outcome unknown')); await tick(); ui.requests.at(-1).resolve(snapshot()); await sending;
  assert(ui.get('pc-power-submit').disabled); assert.equal(ui.storage.get('monitor-pc-power-request'),requestId);
  ui.app.connected=false; ui.window.powerUI.connection('offline'); ui.app.connected=true; ui.window.powerUI.connection('connected');
  assert.equal(ui.requests.at(-1).path,'pc-power'); ui.requests.at(-1).resolve({...snapshot(),history:[{...task(requestId),state:'requested'}]}); await tick();
  assert.equal(ui.requests.filter(r=>r.data).length,1,'no reconnect replay'); assert.equal(ui.storage.size,0); assert.match(ui.get('pc-power-result').textContent,/unconfirmed/);
  const writes=ui.requests.filter(r=>r.data).length;
  for (const timer of ui.timers) if(timer.ms===1000) timer.fn();
  assert.equal(ui.requests.filter(r=>r.data).length,writes,'countdown cannot invoke a mutation');
  console.log('PASS: lost acknowledgement persists ID, reconciles after reconnect and never replays');
}
{
  const ui=setup(); await load(ui,{...snapshot(),current:{...task(),state:'paused'}});
  assert.match(ui.get('pc-power-task-state').textContent,/Resume it in the PC app/); assert(!ui.get('pc-power-cancel').disabled); assert(ui.get('pc-power-submit').disabled);
  ui.get('pc-power-refresh').onclick(); ui.requests.at(-1).resolve({...snapshot(),current:{...task(),state:'dispatching'}}); await tick();
  assert(ui.get('pc-power-cancel').disabled);
  console.log('PASS: restored task stays paused and dispatched task cannot promise cancellation');
}
console.log('PASS: PC power frontend checks. Synthetic DOM and transport; no native power.');
