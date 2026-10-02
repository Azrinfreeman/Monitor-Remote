(() => {
  const $ = id => document.getElementById(id), app = window.monitorApp;
  const cards = new Map();
  let snapshot = null, visible = false, generation = 0, reading = false, bulk = false, timer = null, suspended = false;
  const busy = () => bulk || [...cards.values()].some(c => c.running || c.pending !== null);
  function controls() {
    $('audio-refresh').disabled = !app.connected || reading || busy();
    const outputs = snapshot?.devices.filter(d => d.direction === 'output' && d.state === 'active') || [];
    for (const id of ['audio-all-apply', 'audio-all-mute', 'audio-all-unmute']) $(id).disabled = !app.connected || !outputs.length || busy() || reading || suspended;
    for (const card of cards.values()) {
      const unavailable = !app.connected || suspended || bulk || !card.device.supported || card.device.state !== 'active';
      const siblingBusy = [...cards.values()].some(other => other !== card && other.device.id === card.device.id && (other.running || other.pending !== null));
      card.slider.disabled = unavailable || siblingBusy || !Number.isFinite(card.device.volume);
      card.mute.disabled = unavailable || siblingBusy || card.running || card.pending !== null || typeof card.device.muted !== 'boolean';
    }
  }
  function schedule() { clearTimeout(timer); if (visible && !document.hidden && app.connected && !suspended) timer = setTimeout(read, 1000); }
  function stop(clear = false) {
    generation++; clearTimeout(timer); reading = false; suspended = true;
    for (const card of cards.values()) { clearTimeout(card.timer); card.pending = null; card.revision++; }
    if (clear) {
      for (const card of cards.values()) card.element.remove(); cards.clear(); snapshot = null;
      $('audio-all').hidden = $('audio-output-heading').hidden = $('audio-input-heading').hidden = true;
    }
    controls();
  }
  function create(device, pc) {
    const element = document.createElement('article'), heading = document.createElement('div'), title = document.createElement('h2'), output = document.createElement('output');
    const badges = document.createElement('p'), slider = document.createElement('input'), mute = document.createElement('button'), note = document.createElement('p');
    element.className = 'audio-card'; heading.className = 'monitor-heading'; badges.className = 'audio-badges'; note.className = 'monitor-note'; note.setAttribute('role', 'status');
    slider.type = 'range'; slider.min = 0; slider.max = 100; slider.step = 1; mute.className = 'secondary';
    const key = (pc ? 'pc-' : 'device-') + device.id;
    slider.id = 'volume-' + key; output.setAttribute('for', slider.id); note.id = 'volume-note-' + key; slider.setAttribute('aria-describedby', note.id);
    heading.append(title, output); element.append(heading, badges, slider, mute, note);
    const card = { element, title, output, badges, slider, mute, note, device, pc, pending: null, timer: null, revision: 0, running: false, notice: null };
    slider.addEventListener('input', () => {
      if (slider.disabled) return;
      card.revision++; card.pending = Number(slider.value); card.output.textContent = card.pending + '%'; card.note.textContent = 'Requested ' + card.pending + '%';
      clearTimeout(card.timer); card.timer = setTimeout(() => write(card), 180); controls();
    });
    mute.onclick = () => { if (!mute.disabled) write(card, !card.device.muted); };
    return card;
  }
  function renderCard(card, device) {
    card.device = device;
    card.title.textContent = card.pc ? 'PC volume' : device.name;
    card.badges.textContent = [card.pc ? device.name : device.direction === 'input' ? 'Microphone / input' : 'Playback output', device.default ? 'Default' : '', device.communications ? 'Communications' : '', device.state !== 'active' ? device.state : ''].filter(Boolean).join(' · ');
    card.slider.setAttribute('aria-label', (card.pc ? 'PC volume · ' : '') + device.name + (device.direction === 'input' ? ' input level' : ' volume'));
    card.element.dataset.muted = String(device.muted === true); card.element.dataset.unavailable = String(!device.supported || device.state !== 'active');
    if (!card.running && card.pending === null) {
      card.slider.value = Number.isFinite(device.volume) ? Math.round(device.volume) : 0;
      card.output.textContent = Number.isFinite(device.volume) ? device.volume + '%' : 'Unavailable';
      card.note.textContent = card.notice || device.error || (!device.supported || device.state !== 'active' ? 'Volume control unavailable for this device.' : device.muted ? 'Muted. Changing volume keeps the device muted.' : 'Windows reports ' + device.volume + '%.');
    }
    card.mute.textContent = device.muted ? 'Unmute' : 'Mute';
  }
  function reconcile(next) {
    snapshot = next;
    const desired = new Map();
    for (const device of next.devices) {
      desired.set('device-' + device.id, { device, pc: false, parent: device.direction === 'output' ? 'audio-outputs' : 'audio-inputs' });
      if (device.default && device.direction === 'output' && device.state === 'active') desired.set('pc-' + device.id, { device, pc: true, parent: 'audio-pc' });
    }
    for (const [key, card] of cards) if (!desired.has(key)) { clearTimeout(card.timer); card.pending = null; card.revision++; card.element.remove(); cards.delete(key); }
    for (const [key, item] of desired) {
      let card = cards.get(key);
      if (!card) { card = create(item.device, item.pc); cards.set(key, card); $(item.parent).append(card.element); }
      renderCard(card, item.device);
    }
    const outputs = next.devices.filter(d => d.direction === 'output' && d.state === 'active');
    $('audio-all').hidden = !outputs.length; $('audio-output-heading').hidden = !next.devices.some(d => d.direction === 'output'); $('audio-input-heading').hidden = !next.devices.some(d => d.direction === 'input');
    const values = outputs.map(d => d.volume);
    $('audio-all-current').textContent = values.length && values.every(v => Number.isFinite(v) && v === values[0]) ? 'All outputs · ' + values[0] + '%' : 'All outputs · Mixed / unavailable';
    controls();
  }
  async function read() {
    if (reading || busy() || !app.connected || document.hidden) { schedule(); return; }
    reading = true; const epoch = generation; controls();
    try {
      const result = await app.api('audio/devices');
      if (epoch !== generation || !app.connected) return;
      suspended = false; reconcile(result);
      $('audio-note').textContent = result.devices.length ? (result.simulated ? 'Demo audio devices — real PC volume is unchanged.' : 'Control PC sound and each audio device. Windows changes refresh here automatically.') : 'No audio devices found. Connect a device and refresh.';
    } catch (error) {
      if (epoch === generation) { $('audio-note').textContent = error.message; stop(false); }
    } finally { if (epoch === generation) { reading = false; controls(); schedule(); } }
  }
  async function write(card, muted) {
    if (card.running || bulk || !snapshot || !app.connected || suspended || muted === undefined && card.pending === null) return;
    const epoch = generation, revision = card.revision, value = card.pending;
    clearTimeout(card.timer); card.pending = null; card.running = true; card.notice = null;
    card.note.textContent = muted === undefined ? 'Applying ' + value + '%…' : muted ? 'Muting…' : 'Unmuting…'; controls();
    try {
      const result = await app.api('audio/devices/' + encodeURIComponent(card.device.id) + (muted === undefined ? '/volume' : '/mute'), {
        revision: snapshot.revision, requireDefault: card.pc, ...(muted === undefined ? { value } : { muted })
      });
      if (epoch !== generation || ![...cards.values()].includes(card)) return;
      const actual = result.results[0];
      if (actual.device) { card.device = actual.device; if (revision === card.revision) { card.running = false; renderCard(card, actual.device); card.running = true; } }
      if (revision === card.revision) card.note.textContent = actual.confirmed ? (result.simulated ? 'Applied to the demo device.' : 'Confirmed by Windows.') : actual.error || 'Windows did not confirm the setting.';
      if (!actual.confirmed) { card.pending = null; clearTimeout(card.timer); card.notice = actual.error || 'Windows did not confirm the setting. Refresh before retrying.'; }
    } catch (error) {
      if (epoch === generation) {
        card.pending = null; clearTimeout(card.timer); card.running = false; renderCard(card, card.device); card.running = true; card.notice = error.message + ' Refresh to check before retrying.'; card.note.textContent = card.notice;
      }
    } finally {
      card.running = false;
      if (epoch === generation) { controls(); if (card.pending !== null && app.connected && !suspended) write(card); else schedule(); }
    }
  }
  async function all(muted) {
    if (busy() || reading || !snapshot || !app.connected || suspended) return;
    const ids = snapshot.devices.filter(d => d.direction === 'output' && d.state === 'active').map(d => d.id);
    if (!ids.length) return;
    const epoch = generation; bulk = true; controls(); $('audio-all-note').textContent = 'Applying to playback devices…';
    try {
      const result = await app.api('audio/outputs/' + (muted === undefined ? 'volume' : 'mute'), {
        revision: snapshot.revision, deviceIds: ids, ...(muted === undefined ? { value: Number($('audio-all-value').value) } : { muted })
      });
      if (epoch !== generation) return;
      for (const entry of result.results) for (const card of cards.values()) if (card.device.id === entry.id) {
        if (entry.device) renderCard(card, entry.device);
        card.notice = entry.confirmed ? null : entry.error || 'Setting unconfirmed.';
        card.note.textContent = entry.confirmed ? (result.simulated ? 'Applied to the demo device.' : 'Confirmed by Windows.') : card.notice;
      }
      const failed = result.results.filter(entry => !entry.confirmed);
      $('audio-all-note').textContent = failed.length ? `${result.results.length - failed.length} of ${result.results.length} devices confirmed. Check the device cards for failures.` : 'All selected playback devices confirmed. Microphone settings are unchanged.';
    } catch (error) { if (epoch === generation) $('audio-all-note').textContent = error.message + ' Refresh to check before retrying.'; }
    finally { bulk = false; if (epoch === generation) { controls(); schedule(); } }
  }
  $('audio-refresh').onclick = () => { for (const card of cards.values()) card.notice = null; suspended = false; read(); };
  $('audio-all-value').oninput = () => { $('audio-all-requested').textContent = 'Apply ' + $('audio-all-value').value + '%'; };
  $('audio-all-apply').onclick = () => all(); $('audio-all-mute').onclick = () => all(true); $('audio-all-unmute').onclick = () => all(false);
  window.audioUI = {
    connection(state) {
      if (state === 'offline' || state === 'unpaired') { stop(state === 'unpaired'); $('audio-note').textContent = 'Connect to the PC before controlling audio.'; }
      if (state === 'connected' && visible) { suspended = false; read(); } controls();
    },
    tab(active) { visible = active; if (active) { suspended = false; read(); } else { clearTimeout(timer); for (const card of cards.values()) { clearTimeout(card.timer); card.pending = null; } } }
  };
  document.addEventListener('visibilitychange', () => {
    if (document.hidden) stop(false); else if (visible && app.connected) { suspended = false; read(); }
  });
  controls();
})();
