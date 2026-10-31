(() => {
  const $ = id => document.getElementById(id), app = window.monitorApp;
  const canvas = $('remote-canvas'), context = canvas.getContext('2d');
  let catalog = null, id = null, starting = false, generation = 0, frames = null, control = null;
  let metadata = null, image = null, renderedAt = 0, enabled = false, permitted = false, sequence = 0;
  let pendingFrame = null, decoding = false, movement = null, movementTimer = null, heartbeat = null, freshness = null;
  let zoom = 1, panX = 0, panY = 0, imageRect = null, drag = false, sendingText = false;
  const pending = new Map(), modifiers = new Set();
  const gestureResets = []; let scrollPoint = null;
  function ready() { return !!id && enabled && app.connected && control?.readyState === WebSocket.OPEN && metadata && Date.now() - renderedAt < 2000; }
  function update() {
    const fresh = !!metadata && Date.now() - renderedAt < 2000;
    $('remote-start').disabled = starting || !!id || !app.connected || !catalog?.snapshot.displays.length;
    $('remote-refresh').disabled = starting || !!id || !app.connected;
    $('remote-display').disabled = $('remote-quality').disabled = starting || !!id;
    $('remote-stop').disabled = !id && !starting;
    $('remote-control').disabled = !id || !fresh || !permitted || !app.connected;
    $('remote-control').textContent = enabled ? 'Disable control' : 'Enable control';
    for (const button of document.querySelectorAll('[data-remote-input]')) button.disabled = !ready();
    $('remote-text').disabled = !ready(); $('remote-send-text').disabled = !ready() || sendingText;
    $('remote-drag').setAttribute('aria-pressed', String(drag));
    if (id) $('remote-frame-state').textContent = !fresh ? 'Screen paused — input is disabled until a fresh frame arrives.' : `${enabled ? 'Control active' : 'Viewing only'} · ${metadata.nativeWidth} × ${metadata.nativeHeight}`;
  }
  function clearImage() {
    image?.close?.(); image = null; metadata = null; pendingFrame = null; renderedAt = 0; imageRect = null;
    context.clearRect(0, 0, canvas.width, canvas.height);
  }
  function stop(message = 'Viewing stopped.', notify = true) {
    const previous = id; generation++; id = null; starting = false; enabled = false; drag = false;
    clearTimeout(movementTimer); movementTimer = null; movement = null;
    for (const reset of gestureResets) reset(); scrollPoint = null;
    clearInterval(heartbeat); clearInterval(freshness);
    for (const request of pending.values()) { clearTimeout(request.timer); request.reject(Error('Connection ended. Input may already have reached Windows; it will not be resent.')); }
    pending.clear(); sendingText = false;
    const oldFrames = frames, oldControl = control; frames = control = null;
    oldFrames?.close(); oldControl?.close(); clearImage();
    modifiers.clear(); updateModifiers(); $('remote-text').value = '';
    $('remote-stage').hidden = true; $('remote-note').textContent = message;
    window.monitorFeatures.active(false); update();
    if (previous && notify && app.connected) app.api('remote/session/stop', { remoteSessionId: previous }).catch(() => {});
  }
  async function refresh() {
    if (!app.connected || id || starting) return;
    const epoch = generation;
    try {
      const result = await app.api('remote/displays');
      if (epoch !== generation || !app.connected) return;
      catalog = result; permitted = result.capabilities.allowInput;
      const select = $('remote-display'), selected = select.value; select.replaceChildren();
      for (const display of result.snapshot.displays) { const option = document.createElement('option'); option.value = display.id; option.textContent = `${display.name} · ${display.width} × ${display.height}`; select.append(option); }
      if (result.snapshot.displays.some(d => d.id === selected)) select.value = selected;
      $('remote-note').textContent = result.capabilities.locked ? 'The PC is locked or showing a secure prompt. Wake the display from the Displays tab and unlock it at the PC; viewing and control work again afterwards.' : result.capabilities.allowViewing ? 'Select a display and start viewing. Mouse and keyboard control starts separately.' : 'Enable Allow screen viewing in the PC app, then refresh displays.';
    } catch (error) { $('remote-note').textContent = error.message; }
    update();
  }
  function socket(path, epoch) {
    const url = new URL('/api/remote/' + path, location.href); url.protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const connection = new WebSocket(url); connection.binaryType = 'arraybuffer';
    connection.onopen = () => {
      if (epoch !== generation || !id) { connection.close(); return; }
      connection.send(JSON.stringify({ type: 'bind', remoteSessionId: id }));
      if (path === 'control') pulse();
    };
    connection.onclose = connection.onerror = () => { if (epoch === generation && id) stop('Remote connection ended. Start viewing again when the PC is reachable.', false); };
    connection.onmessage = event => {
      if (epoch !== generation || !id) return;
      if (typeof event.data === 'string') {
        let message; try { message = JSON.parse(event.data); } catch { stop('Invalid remote response.', false); return; }
        if (message.type === 'ended') { stop(message.error || 'Viewing ended.', false); return; }
        if (message.type === 'heartbeat') { enabled = message.controlEnabled; permitted = message.allowInput; if (!enabled) { drag = false; movement = null; } update(); }
        if (message.type === 'ack') {
          const request = pending.get(message.sequence);
          if (request) { clearTimeout(request.timer); pending.delete(message.sequence); message.inserted ? request.resolve(message) : request.reject(Error(message.error || 'Windows did not accept the input.')); }
        }
      } else if (path === 'frames') { pendingFrame = event.data; decode(epoch); }
    };
    return connection;
  }
  function pulse() {
    if (id && control?.readyState === WebSocket.OPEN && !document.hidden)
      control.send(JSON.stringify({ type: 'heartbeat', frameSequence: metadata?.sequence || 0 }));
  }
  async function start() {
    if ($('remote-start').disabled) return;
    starting = true; const epoch = ++generation; update();
    $('remote-note').textContent = 'Starting screen viewing…';
    try {
      const result = await app.api('remote/session', { displayId: $('remote-display').value, revision: catalog.snapshot.revision, quality: $('remote-quality').value });
      if (epoch !== generation || document.hidden || !app.connected) { app.api('remote/session/stop', { remoteSessionId: result.remoteSessionId }).catch(() => {}); return; }
      id = result.remoteSessionId; sequence = 0; zoom = 1; panX = panY = 0;
      $('remote-stage').hidden = false; $('remote-note').textContent = result.display.name + ' · Viewing only';
      frames = socket('frames', epoch); control = socket('control', epoch);
      heartbeat = setInterval(pulse, 1000); freshness = setInterval(update, 200);
      window.monitorFeatures.active(true);
    } catch (error) { if (epoch === generation) $('remote-note').textContent = error.message; }
    finally { starting = false; update(); }
  }
  async function decode(epoch) {
    if (decoding) return; decoding = true;
    try {
      while (pendingFrame && epoch === generation && id) {
        const packet = pendingFrame; pendingFrame = null;
        if (packet.byteLength < 5 || packet.byteLength > 2 * 1024 * 1024 + 4100) throw Error('Invalid screen frame.');
        const size = new DataView(packet).getInt32(0, true);
        if (size < 1 || size > 4096 || size + 4 >= packet.byteLength) throw Error('Invalid screen metadata.');
        const header = JSON.parse(new TextDecoder().decode(new Uint8Array(packet, 4, size)));
        if (header.version !== 1 || header.revision !== catalog.snapshot.revision || header.width < 1 || header.height < 1 || header.width > 1600 || header.height > 1600) throw Error('Display layout changed. Refresh displays.');
        const bitmap = await createImageBitmap(new Blob([new Uint8Array(packet, 4 + size)], { type: 'image/jpeg' }));
        if (epoch !== generation || !id) { bitmap.close(); return; }
        if (bitmap.width !== header.width || bitmap.height !== header.height) { bitmap.close(); throw Error('Invalid screen dimensions.'); }
        image?.close?.(); image = bitmap; metadata = header; renderedAt = Date.now(); paint(); update();
      }
    } catch (error) { if (epoch === generation) stop(error.message, true); }
    finally { decoding = false; if (pendingFrame && id) decode(generation); }
  }
  function paint() {
    if (!image) return;
    const bounds = canvas.getBoundingClientRect(), ratio = Math.min(2, window.devicePixelRatio || 1);
    canvas.width = Math.max(1, Math.round(bounds.width * ratio)); canvas.height = Math.max(1, Math.round(bounds.height * ratio));
    context.setTransform(ratio, 0, 0, ratio, 0, 0); context.fillStyle = '#080d16'; context.fillRect(0, 0, bounds.width, bounds.height);
    const fit = Math.min(bounds.width / image.width, bounds.height / image.height), width = image.width * fit * zoom, height = image.height * fit * zoom;
    panX = Math.max(-Math.max(width, bounds.width) / 2, Math.min(panX, Math.max(width, bounds.width) / 2));
    panY = Math.max(-Math.max(height, bounds.height) / 2, Math.min(panY, Math.max(height, bounds.height) / 2));
    imageRect = { x: (bounds.width - width) / 2 + panX, y: (bounds.height - height) / 2 + panY, width, height };
    context.drawImage(image, imageRect.x, imageRect.y, width, height);
    if (metadata.cursorVisible) {
      const x = imageRect.x + (metadata.cursorX - metadata.x) / metadata.nativeWidth * width;
      const y = imageRect.y + (metadata.cursorY - metadata.y) / metadata.nativeHeight * height;
      context.beginPath(); context.arc(x, y, 6, 0, Math.PI * 2); context.fillStyle = '#fff'; context.fill(); context.strokeStyle = '#101827'; context.lineWidth = 2; context.stroke();
    }
  }
  function point(event) {
    if (!imageRect) return null;
    const bounds = canvas.getBoundingClientRect(), x = (event.clientX - bounds.left - imageRect.x) / imageRect.width, y = (event.clientY - bounds.top - imageRect.y) / imageRect.height;
    return x >= 0 && x <= 1 && y >= 0 && y <= 1 ? { x, y } : null;
  }
  function send(operation, requireFresh = true) {
    if (requireFresh && !ready() || !id || control?.readyState !== WebSocket.OPEN) return Promise.reject(Error('Control is disabled or the displayed screen is stale.'));
    if (control.bufferedAmount > 8192 || pending.size >= 64) { stop('Remote connection is too slow. Input was stopped to avoid a queue.'); return Promise.reject(Error('Input stopped.')); }
    const number = ++sequence;
    const message = { ...operation, sequence: number, frameSequence: metadata?.sequence || 0, revision: metadata?.revision };
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => { pending.delete(number); reject(Error('Outcome unknown. Windows may have received this input; it will not be resent.')); stop('Input acknowledgement timed out. Start viewing again.'); }, 3000);
      pending.set(number, { resolve, reject, timer });
      try { control.send(JSON.stringify(message)); } catch { clearTimeout(timer); pending.delete(number); reject(Error('Remote connection ended.')); stop(); }
    });
  }
  function perform(operation, fresh = true) { send(operation, fresh).catch(error => { $('remote-text-note').textContent = error.message; }); }
  function flushMovement() { clearTimeout(movementTimer); movementTimer = null; const next = movement; movement = null; if (next && ready()) perform(next); }
  function move(operation) {
    if (!ready()) return;
    if (movement && movement.type !== operation.type) flushMovement();
    if (operation.type === 'delta' && movement?.type === 'delta') { movement.dx = Math.max(-2000, Math.min(2000, movement.dx + operation.dx)); movement.dy = Math.max(-2000, Math.min(2000, movement.dy + operation.dy)); }
    else if (operation.type === 'wheel') { operation.dx = Math.max(-10, Math.min(10, operation.dx + (movement?.dx || 0))); operation.dy = Math.max(-10, Math.min(10, operation.dy + (movement?.dy || 0))); movement = operation; }
    else movement = operation;
    if (!movementTimer) movementTimer = setTimeout(flushMovement, 34);
  }
  function click(button = 'left', twice = false) { flushMovement(); perform({ type: 'click', button, double: twice }); }
  function release() { movement = null; clearTimeout(movementTimer); movementTimer = null; drag = false; if (id) perform({ type: 'release' }, false); update(); }
  $('remote-start').onclick = start; $('remote-refresh').onclick = refresh; $('remote-stop').onclick = () => stop();
  $('remote-control').onclick = async () => {
    if ($('remote-control').disabled) return;
    const epoch = generation;
    try { const result = await app.api('remote/session/configure', { remoteSessionId: id, controlEnabled: !enabled }); if (epoch === generation) { enabled = result.controlEnabled; if (!enabled) release(); update(); } }
    catch (error) { $('remote-note').textContent = error.message; }
  };
  $('remote-left').onclick = () => click(); $('remote-right').onclick = () => click('right'); $('remote-double').onclick = () => click('left', true);
  $('remote-fit').onclick = () => { zoom = 1; panX = panY = 0; paint(); };
  for (const [button, factor] of [['remote-zoom-in', 1.25], ['remote-zoom-out', .8]]) $(button).onclick = () => { zoom = Math.max(1, Math.min(4, zoom * factor)); paint(); };
  $('remote-expand').onclick = () => { document.body.dataset.expanded = String(document.body.dataset.expanded !== 'true'); $('remote-expand').textContent = document.body.dataset.expanded === 'true' ? 'Compact view' : 'Expand view'; paint(); };
  $('remote-mode').onchange = () => { release(); $('remote-touchpad').hidden = $('remote-mode').value === 'direct'; };
  $('remote-drag').onclick = () => { if ($('remote-mode').value === 'direct' && ready()) { drag = !drag; update(); } };
  $('remote-drag').onpointerdown = event => {
    if ($('remote-mode').value !== 'touchpad' || !ready()) return;
    event.preventDefault(); $('remote-drag').setPointerCapture(event.pointerId); flushMovement(); drag = true; perform({ type: 'button', button: 'left', down: true }); update();
  };
  for (const name of ['pointerup', 'pointercancel', 'lostpointercapture']) $('remote-drag').addEventListener(name, () => { if ($('remote-mode').value === 'touchpad' && drag) release(); });
  function gestures(element) {
    const pointers = new Map(); let pinch = null;
    gestureResets.push(() => { for (const entry of pointers.values()) clearTimeout(entry.timer); pointers.clear(); pinch = null; });
    const middle = () => { const points = [...pointers.values()]; return { x: (points[0].x + points[1].x) / 2, y: (points[0].y + points[1].y) / 2, distance: Math.hypot(points[0].x - points[1].x, points[0].y - points[1].y) }; };
    element.addEventListener('pointerdown', event => {
      if (!id) return;
      element.setPointerCapture(event.pointerId);
      const entry = { x: event.clientX, y: event.clientY, startX: event.clientX, startY: event.clientY, moved: false, direct: element === canvas && $('remote-mode').value === 'direct', dragging: false, timer: null };
      pointers.set(event.pointerId, entry);
      if (pointers.size >= 2) { for (const pointer of pointers.values()) { pointer.moved = true; clearTimeout(pointer.timer); } pinch = { ...middle(), zoom }; return; }
      if (!ready()) return;
      if (entry.direct) { const target = point(event); if (!target) { entry.moved = true; return; } perform({ type: 'move', ...target }); if (drag) { entry.dragging = true; entry.moved = true; perform({ type: 'button', button: 'left', down: true }); } }
      if (!entry.dragging) entry.timer = setTimeout(() => { if (!entry.moved && pointers.size === 1 && ready()) { entry.moved = true; click('right'); } }, 500);
    });
    element.addEventListener('pointermove', event => {
      const entry = pointers.get(event.pointerId); if (!entry) return;
      const dx = event.clientX - entry.x, dy = event.clientY - entry.y; entry.x = event.clientX; entry.y = event.clientY;
      if (Math.hypot(entry.x - entry.startX, entry.y - entry.startY) > 6) { entry.moved = true; clearTimeout(entry.timer); }
      if (pointers.size >= 2 && pinch) {
        const current = middle(), scale = current.distance / Math.max(1, pinch.distance);
        if (element === canvas) { zoom = Math.max(1, Math.min(4, pinch.zoom * scale)); panX += current.x - pinch.x; panY += current.y - pinch.y; paint(); }
        else if (ready()) move({ type: 'wheel', dx: (current.x - pinch.x) / 45, dy: -(current.y - pinch.y) / 45 });
        pinch.x = current.x; pinch.y = current.y; return;
      }
      if (!ready()) return;
      if (entry.direct) { const target = point(event); if (target) move({ type: 'move', ...target }); }
      else move({ type: 'delta', dx: dx * 1.6, dy: dy * 1.6 });
    });
    function up(event, cancelled = false) {
      const entry = pointers.get(event.pointerId); if (!entry) return;
      clearTimeout(entry.timer); pointers.delete(event.pointerId); if (pointers.size < 2) pinch = null;
      if (entry.dragging || cancelled) release();
      else if (!entry.moved && ready()) { if (entry.direct) { const target = point(event); if (target) perform({ type: 'move', ...target }); else return; } click(); }
    }
    element.addEventListener('pointerup', event => up(event)); element.addEventListener('pointercancel', event => up(event, true));
    element.addEventListener('contextmenu', event => event.preventDefault());
  }
  gestures(canvas); gestures($('remote-touchpad'));

  $('remote-scroll').onpointerdown = event => { if (!ready()) return; $('remote-scroll').setPointerCapture(event.pointerId); scrollPoint = { x: event.clientX, y: event.clientY }; };
  $('remote-scroll').onpointermove = event => { if (!scrollPoint || !ready()) return; move({ type: 'wheel', dx: (event.clientX - scrollPoint.x) / 45, dy: -(event.clientY - scrollPoint.y) / 45 }); scrollPoint = { x: event.clientX, y: event.clientY }; };
  $('remote-scroll').onpointerup = () => { flushMovement(); scrollPoint = null; };
  $('remote-scroll').onpointercancel = () => { movement = null; scrollPoint = null; };
  function updateModifiers() { for (const key of ['Shift', 'Ctrl', 'Alt']) $('key-' + key.toLowerCase()).setAttribute('aria-pressed', String(modifiers.has(key))); }
  for (const key of ['Shift', 'Ctrl', 'Alt']) $('key-' + key.toLowerCase()).onclick = () => { modifiers.has(key) ? modifiers.delete(key) : modifiers.add(key); updateModifiers(); };
  function keyButton(label, container, action) { const button = document.createElement('button'); button.textContent = label; button.setAttribute('data-remote-input', ''); button.disabled = true; button.onclick = action; $(container).append(button); }
  for (const character of 'qwertyuiopasdfghjklzxcvbnm1234567890.,?!@-_/:') keyButton(character, 'keyboard-keys', () => {
    if (!ready()) return;
    if ((modifiers.has('Ctrl') || modifiers.has('Alt')) && /^[a-z0-9]$/i.test(character)) perform({ type: 'key', key: character.toUpperCase(), modifiers: [...modifiers] });
    else perform({ type: 'text', text: modifiers.has('Shift') ? character.toUpperCase() : character });
    modifiers.clear(); updateModifiers();
  });
  for (const key of ['Space', 'Backspace', 'Enter', 'Tab', 'Escape', 'Delete', 'ArrowLeft', 'ArrowUp', 'ArrowDown', 'ArrowRight', 'Home', 'End', 'PageUp', 'PageDown']) keyButton(key.replace('ArrowLeft', '←').replace('ArrowRight', '→').replace('ArrowUp', '↑').replace('ArrowDown', '↓'), 'keyboard-special', () => { perform({ type: 'key', key, modifiers: [...modifiers] }); modifiers.clear(); updateModifiers(); });
  for (const key of ['C', 'V', 'A', 'Z', 'Y']) keyButton('Ctrl+' + key, 'keyboard-shortcuts', () => perform({ type: 'key', key, modifiers: ['Ctrl'] }));
  $('remote-send-text').onclick = async () => {
    if ($('remote-send-text').disabled || !$('remote-text').value) return;
    const text = $('remote-text').value, epoch = generation; sendingText = true; update(); $('remote-text-note').textContent = 'Sending text…';
    try { await send({ type: 'text', text }); if (epoch === generation) { $('remote-text-note').textContent = 'Sent to Windows. Check the PC field for the result.'; if ($('remote-text').value === text) $('remote-text').value = ''; } }
    catch (error) { if (epoch === generation) $('remote-text-note').textContent = error.message; }
    finally { if (epoch === generation) { sendingText = false; update(); } }
  };
  window.remoteUI = {
    stop,
    connection(state) { if (state === 'offline' || state === 'unpaired') stop('Connect to your PC before viewing.', false); update(); },
    tab(visible) { if (visible && !id) refresh(); }
  };
  document.addEventListener('visibilitychange', () => { if (document.hidden) stop('Viewing stopped while the phone was in the background.'); });
  window.addEventListener('pagehide', () => stop('Viewing stopped.', false));
  window.addEventListener('resize', paint); window.visualViewport?.addEventListener('resize', paint);
  window.addEventListener('blur', release);
  update();
})();
