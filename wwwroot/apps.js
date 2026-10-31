(() => {
  const $ = id => document.getElementById(id), app = window.monitorApp, base = window.audioUI;
  const cards = new Map();
  let revision = null, visible = false, generation = 0, reading = false, timer = null;
  const busy = () => [...cards.values()].some(c => c.running || c.pending !== null);
  function controls() {
    for (const card of cards.values()) card.slider.disabled = card.mute.disabled = !app.connected || card.running;
  }
  function schedule() { clearTimeout(timer); if (visible && !document.hidden && app.connected) timer = setTimeout(read, 2000); }
  function clear() {
    generation++; clearTimeout(timer); reading = false;
    for (const card of cards.values()) { clearTimeout(card.timer); card.element.remove(); }
    cards.clear(); revision = null; $('apps-heading').hidden = true;
  }
  function create(session) {
    const element = document.createElement('article'), heading = document.createElement('div'), title = document.createElement('h3'), output = document.createElement('output');
    const badge = document.createElement('p'), slider = document.createElement('input'), mute = document.createElement('button'), note = document.createElement('p');
    element.className = 'audio-card'; heading.className = 'monitor-heading'; badge.className = 'audio-badges'; note.className = 'monitor-note'; note.setAttribute('role', 'status');
    slider.type = 'range'; slider.min = 0; slider.max = 100; slider.step = 1; mute.className = 'secondary';
    slider.id = 'app-volume-' + session.id; output.setAttribute('for', slider.id);
    heading.append(title, output); element.append(heading, badge, slider, mute, note);
    const card = { element, title, output, badge, slider, mute, note, session, pending: null, timer: null, running: false, notice: null };
    slider.addEventListener('input', () => {
      if (slider.disabled) return;
      card.pending = Number(slider.value); output.textContent = card.pending + '%'; note.textContent = 'Requested ' + card.pending + '%';
      clearTimeout(card.timer); card.timer = setTimeout(() => write(card), 180);
    });
    mute.onclick = () => { if (!mute.disabled) write(card, !card.session.muted); };
    return card;
  }
  function render(card, session) {
    card.session = session;
    card.title.textContent = session.name;
    card.badge.textContent = [session.deviceName, session.active ? 'Playing' : 'Silent'].join(' · ');
    card.slider.setAttribute('aria-label', session.name + ' volume on ' + session.deviceName);
    card.element.dataset.muted = String(session.muted === true);
    if (!card.running && card.pending === null) {
      card.slider.value = Math.round(session.volume ?? 0); card.output.textContent = (session.volume ?? 0) + '%';
      card.note.textContent = card.notice || (session.muted ? 'Muted. Changing volume keeps it muted.' : 'Windows reports ' + session.volume + '%.');
    }
    card.mute.textContent = session.muted ? 'Unmute' : 'Mute';
  }
  function reconcile(result) {
    revision = result.revision;
    const keep = new Set();
    for (const session of result.sessions) {
      keep.add(session.id);
      let card = cards.get(session.id);
      if (!card) { card = create(session); cards.set(session.id, card); $('apps-list').append(card.element); }
      render(card, session);
    }
    for (const [id, card] of cards) if (!keep.has(id)) { clearTimeout(card.timer); card.element.remove(); cards.delete(id); }
    $('apps-heading').hidden = false;
    $('apps-note').textContent = result.sessions.length ? (result.simulated ? 'Demo applications — real app volumes are unchanged.' : 'Each application has its own volume on each playback device.') : 'No applications have an audio session. Start playing sound, then refresh.';
    controls();
  }
  async function read() {
    if (reading || busy() || !app.connected || document.hidden) { schedule(); return; }
    reading = true; const epoch = generation;
    try { const result = await app.api('audio/sessions'); if (epoch === generation && app.connected) reconcile(result); }
    catch (error) { if (epoch === generation) { $('apps-note').textContent = error.message; } }
    finally { if (epoch === generation) { reading = false; schedule(); } }
  }
  async function write(card, muted) {
    if (card.running || !revision || !app.connected || muted === undefined && card.pending === null) return;
    const epoch = generation, value = card.pending;
    clearTimeout(card.timer); card.pending = null; card.running = true; card.notice = null; controls();
    card.note.textContent = muted === undefined ? 'Applying ' + value + '%…' : muted ? 'Muting…' : 'Unmuting…';
    try {
      const result = await app.api('audio/sessions/' + encodeURIComponent(card.session.id) + (muted === undefined ? '/volume' : '/mute'), { revision, ...(muted === undefined ? { value } : { muted }) });
      if (epoch !== generation || !cards.has(card.session.id)) return;
      if (result.session) { card.running = false; render(card, result.session); card.running = true; }
      card.note.textContent = result.confirmed ? (result.simulated ? 'Applied to the demo application.' : 'Confirmed by Windows.') : result.error || 'Windows did not confirm the setting.';
      if (!result.confirmed) card.notice = card.note.textContent;
    } catch (error) {
      if (epoch === generation) { card.notice = error.message + ' Refresh to check before retrying.'; card.note.textContent = card.notice; }
    } finally {
      card.running = false;
      if (epoch === generation) { controls(); if (card.pending !== null && app.connected) write(card); else schedule(); }
    }
  }
  $('apps-refresh').onclick = () => { for (const card of cards.values()) card.notice = null; read(); };
  window.audioUI = {
    connection(state) { base.connection(state); if (state === 'offline' || state === 'unpaired') clear(); if (state === 'connected' && visible) read(); controls(); },
    tab(active) { base.tab(active); visible = active; if (active) read(); else clearTimeout(timer); }
  };
  document.addEventListener('visibilitychange', () => { if (!document.hidden && visible && app.connected) read(); });
})();
