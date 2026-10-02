import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import vm from 'node:vm';

// A minimal DOM and controllable transport exercise the real frontend script.
// These checks cover request behavior; they do not verify Safari rendering.
class Element {
  constructor(tag = 'div') { this.tagName = tag; this.children = []; this.dataset = {}; this.listeners = {}; this.hidden = false; this.disabled = false; this.value = ''; this.textContent = ''; }
  append(...children) { for (const child of children) { child.parent = this; this.children.push(child); } }
  remove() { this.parent.children = this.parent.children.filter(child => child !== this); }
  setAttribute(name, value) { this[name] = value; }
  addEventListener(name, fn) { this.listeners[name] = fn; }
}
const tick = async () => { for (let i = 0; i < 20; i++) await Promise.resolve(); };
const monitor = {id:'0', name:'Desk monitor', supported:true, brightness:50};
function setup() {
  const ids = ['status','pair','controls','refresh','logout','off','wake','connect','retry-connection','code','monitors','empty','power-status'];
  const elements = Object.fromEntries(ids.map(id => [id, new Element()]));
  const document = {hidden:false, listeners:{}, getElementById:id => elements[id], createElement:tag => new Element(tag), addEventListener(name, fn) { this.listeners[name] = fn; }};
  const window = {listeners:{}, addEventListener(name, fn) { this.listeners[name] = fn; }};
  let timerId = 0;
  const timers = new Map(), calls = [];
  const context = vm.createContext({document, window, AbortController, TypeError, console, confirm:()=>true,
    setTimeout(fn, ms) { const id = ++timerId; timers.set(id, {fn, ms}); return id; }, clearTimeout:id => timers.delete(id),
    fetch(path, options) {
      return new Promise((resolve, reject) => {
        const call = {path, options, done:false, reply(body, status = 200) { this.done = true; resolve({status, ok:status >= 200 && status < 300, json:async()=>body}); }, fail(error) { this.done = true; reject(error); }};
        options.signal.addEventListener('abort', () => { const error = Error('Aborted'); error.name = 'AbortError'; call.fail(error); });
        calls.push(call);
      });
    }
  });
  vm.runInContext(readFileSync(new URL('../wwwroot/app.js', import.meta.url), 'utf8'), context);
  return {context, elements, document, window, timers, calls,
    evaluate:code => vm.runInContext(code, context),
    get card() { return vm.runInContext("cards.get('0')", context); },
    async connect(list = [monitor]) { calls.at(-1).reply({simulated:false}); await tick(); calls.at(-1).reply(list); await tick(); },
    fireTimers(ms) { for (const [id, timer] of [...timers]) if (timer.ms === ms) { timers.delete(id); timer.fn(); } }
  };
}

// Latest intent wins: a stale response may update readback but never the edited slider.
{
  const app = setup(); await app.connect(); const card = app.card;
  card.buttons[0].onclick();
  card.slider.value = 87; card.slider.listeners.input(); app.fireTimers(180);
  assert.equal(app.calls.length, 3);
  app.calls[2].reply({confirmed:true, brightness:25}); await tick();
  assert.equal(Number(card.slider.value), 87);
  assert.equal(JSON.parse(app.calls[3].options.body).value, 87);
  app.calls[3].reply({confirmed:false, brightness:86}); await tick();
  assert.equal(card.output.textContent, '86%'); assert.match(card.note.textContent, /requested 87%/);
  const slider = card.slider;
  app.elements.refresh.onclick(); await app.connect();
  assert.equal(app.card.slider, slider, 'refresh keeps slider nodes and focus');
}
// Failed operations restore the last known reading, discard queued writes and offer explicit retry.
{
  const app = setup(); await app.connect(); const card = app.card;
  card.buttons[0].onclick(); card.buttons[3].onclick();
  app.calls[2].reply({error:'Controller busy. Try again.'}, 503); await tick();
  assert.equal(app.calls.length, 3); assert.equal(card.output.textContent, '50%'); assert.equal(card.retry.hidden, false);
  card.retry.onclick(); assert.equal(JSON.parse(app.calls[3].options.body).value, 100);
  app.calls[3].reply({confirmed:false}); await tick();
  assert.match(card.note.textContent, /unconfirmed/); assert.equal(card.confirmed, 50);
}
// Timeout makes the app offline and does not replay brightness after reconnecting.
{
  const app = setup(); await app.connect(); const card = app.card;
  card.buttons[0].onclick(); card.buttons[3].onclick(); app.fireTimers(12000); await tick();
  assert.equal(app.elements.status.dataset.state, 'offline'); assert.equal(card.output.textContent, '50%');
  assert.equal(app.calls.length, 3); assert.equal(app.elements.off.disabled, true);
  app.elements['retry-connection'].onclick(); await app.connect();
  assert.equal(app.calls.length, 5); assert.equal(app.elements.status.dataset.state, 'connected');
}
// Session expiry cancels pending writes and distinguishes pairing from an offline PC.
{
  const app = setup(); await app.connect(); app.card.buttons[0].onclick(); app.card.buttons[3].onclick();
  app.calls[2].reply({}, 401); await tick();
  assert.equal(app.calls.length, 3); assert.equal(app.elements.pair.hidden, false);
  assert.equal(app.elements.controls.hidden, true); assert.equal(app.elements.status.dataset.state, 'unpaired');
}
// Both power buttons and refresh stay locked during any power request.
{
  const app = setup(); await app.connect(); app.elements.wake.onclick();
  app.elements.off.onclick(); app.elements.wake.onclick(); app.elements.refresh.onclick();
  assert.equal(app.calls.length, 3); assert.equal(app.elements.off.disabled, true);
  app.calls[2].reply({requested:true, confirmed:false}); await tick();
  assert.equal(app.elements.off.disabled, false); assert.match(app.elements['power-status'].textContent, /unconfirmed/);
}
// Backgrounding does not poll; foreground recovery reads state without issuing commands.
{
  const app = setup(); await app.connect();
  app.document.hidden = true; app.document.listeners.visibilitychange(); assert.equal(app.calls.length, 2);
  app.document.hidden = false; app.document.listeners.visibilitychange(); await app.connect([]);
  assert.equal(app.calls.length, 4); assert.equal(app.card, undefined); assert.equal(app.elements.empty.hidden, false);
  assert(app.calls.every(call => !call.options.method));
}
// An offline event invalidates an in-flight refresh; old readings cannot reconnect the UI.
{
  const app = setup(); await app.connect(); const card = app.card;
  app.elements.refresh.onclick(); app.calls[2].reply({simulated:false}); await tick();
  app.window.listeners.offline();
  app.calls[3].reply([{...monitor, brightness:99}]); await tick();
  assert.equal(app.elements.status.dataset.state, 'offline'); assert.equal(card.output.textContent, '50%');
  app.window.listeners.online(); await app.connect();
  assert.equal(app.elements.status.dataset.state, 'connected');
  assert(app.calls.every(call => !call.options.method));
}
// Logout aborts active writes, clears queued intent, and cannot be undone by old responses.
{
  const app = setup(); await app.connect(); app.card.buttons[0].onclick(); app.card.buttons[3].onclick();
  app.elements.logout.onclick(); await tick();
  app.elements.pair.onsubmit({preventDefault(){}});
  assert.equal(app.calls.length, 4, 'pairing waits for logout to finish');
  app.calls[2].reply({confirmed:true, brightness:25}); app.calls[3].reply({}); await tick();
  assert.equal(app.calls.length, 4); assert.equal(app.elements.controls.hidden, true);
  assert.equal(app.elements.status.dataset.state, 'unpaired');
}
// Initial authorization and unsupported monitors provide distinct feedback.
{
  const app = setup(); app.calls[0].reply({}, 401); await tick();
  assert.equal(app.elements.status.dataset.state, 'unpaired'); assert.equal(app.elements.pair.hidden, false);
  app.elements.code.value = '123456'; app.elements.pair.onsubmit({preventDefault(){}});
  app.elements.pair.onsubmit({preventDefault(){}}); assert.equal(app.calls.length, 2);
  app.calls[1].reply({paired:true}); await tick();
  await app.connect([{...monitor, supported:false, brightness:null}]);
  assert.equal(app.card.slider.disabled, true); assert.match(app.card.note.textContent, /unavailable/);
}
console.log('PASS: latest slider intent, readback, failure/retry, timeout, expiry, power serialization, foreground recovery, logout and pairing');
