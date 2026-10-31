const $ = id => document.getElementById(id);
const cards = new Map();
let epoch = 0, paired = false, loading = false, pairing = false, powerBusy = false, loggingOut = false;
let connection = 'connecting', resumePending = false;
const activeRequests = new Set();
function setConnection(state, text) {
  connection = state;
  $('status').dataset.state = state;
  $('status').textContent = text;
  updateControls();
  window.monitorFeatures?.connection(state);
}
function stopCards() {
  for (const card of cards.values()) {
    clearTimeout(card.timer); card.pending = null; card.revision++;
  }
}
function invalidateSession(text) {
  epoch++; paired = false; resumePending = false; stopCards();
  if (powerBusy) $('power-status').textContent = 'Connection changed. Visible display state is unconfirmed.';
  for (const controller of activeRequests) controller.abort();
  $('pair').hidden = false; $('controls').hidden = true;
  setConnection('unpaired', text);
}
function isBusy() {
  return loading || powerBusy || [...cards.values()].some(c => c.running || c.pending !== null);
}
function updateControls() {
  const unavailable = !paired || connection !== 'connected';
  $('refresh').disabled = unavailable || isBusy();
  $('logout').disabled = !paired || loading || powerBusy;
  for (const id of ['off', 'wake']) $(id).disabled = unavailable || isBusy() || id === 'off' && !!window.monitorFeatures?.remoteActive;
  $('connect').disabled = pairing || loading || loggingOut;
  $('retry-connection').hidden = connection !== 'offline';
  $('retry-connection').disabled = loading || pairing || loggingOut;
  for (const card of cards.values()) {
    const disabled = unavailable || loading || powerBusy || !card.supported;
    card.slider.disabled = disabled;
    for (const button of card.buttons) button.disabled = disabled;
    card.retry.disabled = disabled || card.running;
  }
}
async function api(path, data) {
  const controller = new AbortController(), requestEpoch = epoch;
  activeRequests.add(controller);
  const timer = setTimeout(() => controller.abort(), 12000);
  try {
    const response = await fetch('/api/' + path, {
      signal: controller.signal, cache: 'no-store',
      ...(data === undefined ? {} : { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Monitor-Request': '1' }, body: JSON.stringify(data) })
    });
    if (requestEpoch !== epoch) throw Error('Session changed. Connect again.');
    if (response.status === 401) {
      const text = path === 'pair' ? 'Code incorrect or expired. Check the PC console; restart the app for a new code.' : 'Enter the pairing code from your PC to connect again.';
      invalidateSession(text); throw Error(text);
    }
    if (!response.ok) {
      const body = await response.json().catch(() => ({}));
      throw Error(body.error || (response.status === 429 ? 'Too many attempts. Wait 10 minutes.' : 'Request failed (' + response.status + ').'));
    }
    const result = await response.json().catch(() => ({}));
    if (requestEpoch !== epoch) throw Error('Session changed. Connect again.');
    return result;
  } catch (error) {
    if (requestEpoch === epoch && (error.name === 'AbortError' || error instanceof TypeError)) {
      stopCards();
      setConnection('offline', 'PC unreachable. Check Wi-Fi and that the app is running, then reconnect.');
      throw Error('No response from the PC. The request may have reached it; reconnect to check before retrying.');
    }
    throw error;
  } finally { clearTimeout(timer); activeRequests.delete(controller); }
}
function report(error) {
  if (connection !== 'offline' && connection !== 'unpaired') $('status').textContent = error.message;
}
function idle() {
  updateControls();
  if (resumePending && !isBusy() && !document.hidden) {
    resumePending = false; load();
  }
}
function createCard(monitor) {
  const element = document.createElement('article'), heading = document.createElement('div');
  heading.className = 'monitor-heading';
  const title = document.createElement('h2'), output = document.createElement('output');
  const slider = document.createElement('input'), presets = document.createElement('div');
  const note = document.createElement('p'), retry = document.createElement('button');
  slider.type = 'range'; slider.min = 0; slider.max = 100; slider.step = 1;
  slider.id = 'brightness-' + monitor.id; output.setAttribute('for', slider.id);
  note.className = 'monitor-note'; note.setAttribute('role', 'status');
  note.id = 'note-' + monitor.id; slider.setAttribute('aria-describedby', note.id);
  presets.className = 'presets';
  retry.textContent = 'Retry brightness'; retry.className = 'secondary'; retry.hidden = true;
  const card = { element, title, output, slider, note, retry, buttons: [], id: monitor.id, pending: null, running: false, revision: 0, confirmed: null, supported: false, desired: null, timer: null };
  function choose(value, debounce = false) {
    if (slider.disabled) return;
    card.revision++; card.desired = value; card.pending = value;
    slider.value = value; output.textContent = value + '%';
    note.textContent = 'Requested ' + value + '%'; retry.hidden = true;
    clearTimeout(card.timer);
    if (debounce) card.timer = setTimeout(() => sendBrightness(card), 180);
    else sendBrightness(card);
    updateControls();
  }
  slider.addEventListener('input', () => choose(Number(slider.value), true));
  for (const value of [25, 50, 75, 100]) {
    const button = document.createElement('button'); button.textContent = value + '%';
    button.onclick = () => choose(value); presets.append(button); card.buttons.push(button);
  }
  retry.onclick = () => choose(card.desired);
  heading.append(title, output); element.append(heading, slider, presets, note, retry);
  return card;
}
function reconcile(monitors) {
  const ids = new Set(monitors.map(m => m.id));
  for (const [id, card] of cards) if (!ids.has(id)) {
    clearTimeout(card.timer); card.pending = null; card.revision++;
    card.element.remove(); cards.delete(id);
  }
  $('empty').hidden = monitors.length > 0;
  for (const monitor of monitors) {
    let card = cards.get(monitor.id);
    if (!card) { card = createCard(monitor); cards.set(monitor.id, card); $('monitors').append(card.element); }
    card.supported = monitor.supported;
    card.title.textContent = monitor.name || 'Monitor ' + monitor.id;
    card.slider.setAttribute('aria-label', card.title.textContent + ' brightness');
    card.confirmed = Number.isFinite(monitor.brightness) ? monitor.brightness : null;
    card.desired = card.confirmed; card.slider.value = card.confirmed ?? 50;
    card.output.textContent = card.confirmed === null ? 'Unavailable' : card.confirmed + '%';
    card.retry.hidden = true;
    card.note.textContent = !card.supported ? 'Brightness unavailable. Enable DDC/CI in the monitor menu; this connection may not support it.' : card.confirmed === null ? 'Brightness readback unavailable.' : 'Monitor reports ' + card.confirmed + '%';
  }
}
async function sendBrightness(card) {
  if (card.running || card.pending === null || connection !== 'connected' || !paired || cards.get(card.id) !== card) return;
  clearTimeout(card.timer);
  const value = card.pending, revision = card.revision, requestEpoch = epoch;
  card.pending = null; card.running = true;
  card.note.textContent = 'Applying ' + value + '%…'; updateControls();
  try {
    const result = await api('monitors/' + encodeURIComponent(card.id) + '/brightness', { value });
    if (requestEpoch !== epoch || cards.get(card.id) !== card) return;
    const actual = Number.isFinite(result.brightness) ? result.brightness : result.confirmed ? value : null;
    if (actual !== null) card.confirmed = actual;
    if (revision === card.revision) {
      if (actual !== null) {
        card.slider.value = actual; card.output.textContent = actual + '%';
        card.note.textContent = result.confirmed ? 'Confirmed by monitor: ' + actual + '%' : 'Monitor reports ' + actual + '%; requested ' + value + '%.';
      } else card.note.textContent = 'Requested ' + value + '%; readback unavailable. Value is unconfirmed.';
    }
  } catch (error) {
    if (requestEpoch === epoch && cards.get(card.id) === card) {
      card.pending = null; clearTimeout(card.timer);
      card.slider.value = card.confirmed ?? value;
      card.output.textContent = card.confirmed === null ? 'Unconfirmed' : card.confirmed + '%';
      card.note.textContent = error.message + (card.confirmed === null ? '' : ' Last confirmed: ' + card.confirmed + '%.');
      card.retry.hidden = false;
    }
  } finally {
    card.running = false;
    if (card.pending !== null && requestEpoch === epoch && connection === 'connected') sendBrightness(card);
    idle();
  }
}
async function load() {
  if (loading || pairing || loggingOut) return;
  if (isBusy()) { resumePending = true; return; }
  loading = true;
  const requestEpoch = epoch;
  setConnection(paired ? 'reconnecting' : 'connecting', paired ? 'Reconnecting to your PC…' : 'Checking connection…');
  try {
    const health = await api('status'), monitors = await api('monitors');
    if (requestEpoch !== epoch) return;
    reconcile(monitors); paired = true; $('pair').hidden = true; $('controls').hidden = false;
    window.monitorFeatures?.health(health);
    setConnection('connected', health.simulated ? 'Connected · Demo mode — no hardware commands' : 'Connected to your PC');
  } catch (error) {
    if (requestEpoch === epoch && connection !== 'offline') setConnection('offline', error.message + ' Reconnect to try again.');
  } finally { loading = false; idle(); }
}
$('pair').onsubmit = async event => {
  event.preventDefault(); if (pairing || loading || loggingOut) return;
  pairing = true; updateControls(); let success = false;
  try {
    const first = await api('pair', { code: $('code').value }); $('code').value = '';
    if (first.pending) {
      $('status').textContent = 'Waiting for approval on your PC…';
      let result = first;
      for (let i = 0; i < 80 && result.pending; i++) {
        await new Promise(resolve => setTimeout(resolve, 1500));
        try { result = await api('pair/poll', { id: first.id }); }
        catch (error) { throw /\(40[34]\)/.test(error.message) ? Error('The PC did not approve this connection, or the request expired.') : error; }
      }
      if (result.pending) throw Error('No approval on the PC. Try again.');
    }
    success = true;
  }
  catch (error) { report(error); }
  finally { pairing = false; updateControls(); }
  if (success) await load();
};
$('refresh').onclick = () => { if (!$('refresh').disabled) load(); };
$('retry-connection').onclick = () => load();
$('logout').onclick = async () => {
  if ($('logout').disabled) return;
  loggingOut = true;
  invalidateSession('Disconnecting…');
  try { await api('logout', {}); setConnection('unpaired', 'Disconnected. Enter your PC pairing code to connect.'); }
  catch (error) { report(error); }
  finally { loggingOut = false; updateControls(); }
};
for (const state of ['off', 'wake']) $(state).onclick = async () => {
  if ($(state).disabled || powerBusy) return;
  if (state === 'off' && !confirm('Turn off all PC displays? The PC must stay awake for phone access.')) return;
  const requestEpoch = epoch; powerBusy = true; updateControls();
  $('power-status').textContent = state === 'off' ? 'Requesting displays off…' : 'Requesting wake…';
  try {
    await api('displays/power', { state });
    if (requestEpoch === epoch) $('power-status').textContent = 'Request sent. Visible display state is unconfirmed.';
  } catch (error) { if (requestEpoch === epoch) $('power-status').textContent = error.message; }
  finally { powerBusy = false; idle(); }
};
function resume() {
  if (document.hidden) return;
  resumePending = false; load();
}
document.addEventListener('visibilitychange', resume);
window.addEventListener('pageshow', event => { if (event.persisted) resume(); });
window.addEventListener('online', resume);
window.addEventListener('offline', () => {
  epoch++; resumePending = false; stopCards();
  for (const controller of activeRequests) controller.abort();
  if (powerBusy) $('power-status').textContent = 'Connection lost. The request may have reached the PC; visible state is unconfirmed.';
  setConnection('offline', 'Wi-Fi appears offline. Reconnect when your PC is reachable.');
});
window.monitorApp = { api, report, updateControls, get connected() { return paired && connection === 'connected'; } };
load();
