// Presentation layer: reads the state that the feature scripts already publish (hidden attributes, data-state, aria-pressed)
// and drives visuals only. It never calls the API and never changes feature behavior.
(() => {
  const $ = id => document.getElementById(id), body = document.body;
  const reduceMotion = () => matchMedia('(prefers-reduced-motion: reduce)').matches;
  const observe = (target, options, callback) => { if (target) new MutationObserver(callback).observe(target, options); };
  const store = {
    get: key => { try { return localStorage.getItem(key); } catch { return null; } },
    set: (key, value) => { try { localStorage.setItem(key, value); } catch { /* private mode */ } }
  };

  // ---- header: collapse the large title into the glass bar after scrolling
  const onScroll = () => { body.dataset.scrolled = String(scrollY > 40); };
  addEventListener('scroll', onScroll, { passive: true }); onScroll();

  // ---- paired state
  const syncPaired = () => { body.dataset.paired = String(!$('controls').hidden); };
  observe($('controls'), { attributes: true, attributeFilter: ['hidden'] }, () => {
    const was = body.dataset.paired === 'true'; syncPaired();
    if (!was && body.dataset.paired === 'true') celebrate();
  });
  syncPaired();

  // ---- a scanned QR code opens /#code=123456: fill the box and connect (the fragment never reaches the server)
  let scannedCode = null;
  const scanned = location.hash.match(/^#code=(\d{6})$/);
  if (scanned) { scannedCode = scanned[1]; history.replaceState(null, '', location.pathname + location.search); }
  function applyScannedCode(attempt = 0) {
    if (!scannedCode) return;
    if ($('pair').hidden) { if (attempt < 40) setTimeout(() => applyScannedCode(attempt + 1), 250); else scannedCode = null; return; }
    const value = scannedCode; $('code').value = value; $('code').dispatchEvent(new Event('input'));
    $('pair-hint').hidden = false;
    // Short pause so the digits are seen filling in, then connect once the page is idle.
    const submit = tries => { if ($('connect').disabled && tries < 20) { setTimeout(() => submit(tries + 1), 150); return; } scannedCode = null; $('pair').requestSubmit(); };
    setTimeout(() => submit(0), 450);
  }
  // ---- pairing screen: digit cells, waiting, error, success
  const pair = $('pair'), cells = [...document.querySelectorAll('.code-cells span')], code = $('code');
  let submitted = false;
  function syncCells() {
    const value = code.value.replace(/\D/g, '').slice(0, 6);
    cells.forEach((cell, index) => {
      const had = cell.textContent, next = value[index] ?? '';
      cell.textContent = next; cell.dataset.active = String(document.activeElement === code && index === Math.min(value.length, 5));
      if (next && next !== had) { cell.dataset.filled = 'true'; } else if (!next) delete cell.dataset.filled;
    });
  }
  code.addEventListener('input', syncCells); code.addEventListener('focus', syncCells); code.addEventListener('blur', syncCells);
  pair.addEventListener('submit', () => { submitted = true; });
  observe($('status'), { childList: true, characterData: true, subtree: true }, () => {
    const text = $('status').textContent;
    pair.dataset.waiting = String(text.startsWith('Waiting for approval'));
    if (submitted && !pair.hidden && text && !text.startsWith('Waiting') && !text.startsWith('Checking') && $('status').dataset.state !== 'connecting') {
      submitted = false; pair.dataset.error = 'true'; setTimeout(() => { delete pair.dataset.error; }, 400);
    }
    syncCells();
  });
  function celebrate() {
    submitted = false; pair.dataset.success = 'true'; setTimeout(() => { delete pair.dataset.success; }, 600);
  }
  setInterval(() => { if (!pair.hidden) syncCells(); }, 200);
  applyScannedCode();

  // ---- slider fill (--p) follows the value
  const rangeFill = input => {
    const min = Number(input.min || 0), max = Number(input.max || 100), p = max > min ? (Number(input.value) - min) / (max - min) * 100 : 0;
    const text = p.toFixed(1) + '%'; if (input.style.getPropertyValue('--p') !== text) input.style.setProperty('--p', text);
  };
  document.addEventListener('input', event => { if (event.target.matches?.('input[type=range]')) rangeFill(event.target); });
  setInterval(() => { if (!document.hidden) document.querySelectorAll('input[type=range]').forEach(rangeFill); }, 250);

  // ---- tab bar: sliding indicator and pane entrance
  const tabs = ['monitors', 'desktop', 'audio', 'power'], revealed = new Set(['monitors']);
  function placeIndicator() {
    const active = tabs.map(name => $('tab-' + name)).find(button => button.getAttribute('aria-pressed') === 'true'), indicator = document.querySelector('.tab-indicator');
    if (!active || !indicator || active.offsetWidth === 0) return;
    const x = active.offsetLeft + active.offsetWidth / 2 - indicator.offsetWidth / 2;
    indicator.style.transform = `translateX(${Math.round(x)}px)`; indicator.style.opacity = '1';
  }
  for (const name of tabs) observe($('tab-' + name), { attributes: true, attributeFilter: ['aria-pressed'] }, () => {
    if ($('tab-' + name).getAttribute('aria-pressed') !== 'true') return;
    placeIndicator();
    const pane = $('pane-' + name);
    if (!revealed.has(name)) {
      revealed.add(name);
      const items = [...pane.children].slice(0, 6); items.forEach((item, index) => item.style.setProperty('--i', index));
      pane.classList.add('reveal'); setTimeout(() => pane.classList.remove('reveal'), 700);
    } else if (typeof document.startViewTransition !== 'function') { pane.classList.add('pane-enter'); pane.addEventListener('animationend', () => pane.classList.remove('pane-enter'), { once: true }); }
    scrollTo({ top: 0, behavior: 'instant' });
  });
  observe($('feature-tabs'), { attributes: true, attributeFilter: ['hidden'] }, placeIndicator);
  addEventListener('resize', placeIndicator); addEventListener('orientationchange', placeIndicator);
  requestAnimationFrame(placeIndicator);

  // ---- sheets: connection options and the keyboard
  const scrim = $('scrim'), sheet = $('more-sheet'), keyboard = $('remote-keyboard');
  let opener = null;
  function showSheet(element, trigger) {
    opener = trigger || document.activeElement; scrim.hidden = false; element.hidden = false;
    document.querySelector('main').inert = true; $('feature-tabs').inert = true;
    (element.querySelector('button') || element).focus?.();
  }
  function hideSheet(element) {
    element.hidden = true; scrim.hidden = true; element.classList.remove('closing', 'dragging'); element.style.transform = '';
    document.querySelector('main').inert = false; $('feature-tabs').inert = false;
    if (opener?.isConnected) opener.focus(); opener = null;
  }
  function closeAnimated(element, done) {
    if (reduceMotion()) { done(); return; }
    element.classList.add('closing'); element.style.transform = 'translateY(100%)';
    setTimeout(done, 230);
  }
  $('more').addEventListener('click', event => showSheet(sheet, event.currentTarget));
  scrim.addEventListener('click', () => { if (!sheet.hidden) closeAnimated(sheet, () => hideSheet(sheet)); if (keyboard.open) closeKeyboard(); });
  sheet.addEventListener('click', event => { if (event.target.closest('#refresh, #logout')) hideSheet(sheet); });
  addEventListener('keydown', event => {
    if (event.key !== 'Escape') return;
    if (!sheet.hidden) hideSheet(sheet); else if (keyboard.open) closeKeyboard();
  });
  function closeKeyboard() { closeAnimated(keyboard, () => { keyboard.open = false; keyboard.classList.remove('closing'); keyboard.style.transform = ''; scrim.hidden = true; }); }
  keyboard.addEventListener('toggle', () => { scrim.hidden = !keyboard.open && sheet.hidden; if (keyboard.open) scrim.hidden = false; });
  // Drag a sheet down from its handle; release velocity decides whether it dismisses.
  function draggable(element, handle, close) {
    let startY = 0, lastY = 0, lastT = 0, velocity = 0, active = false;
    handle.addEventListener('pointerdown', event => { if (element.tagName === 'DETAILS' && !element.open) return; active = true; startY = lastY = event.clientY; lastT = event.timeStamp; velocity = 0; handle.setPointerCapture(event.pointerId); element.classList.add('dragging'); });
    handle.addEventListener('pointermove', event => {
      if (!active) return;
      const dy = Math.max(0, event.clientY - startY); velocity = (event.clientY - lastY) / Math.max(1, event.timeStamp - lastT); lastY = event.clientY; lastT = event.timeStamp;
      element.style.transform = `translateY(${dy}px)`;
    });
    const end = event => {
      if (!active) return; active = false; element.classList.remove('dragging');
      const dy = Math.max(0, event.clientY - startY);
      if (dy > 90 || velocity > .6) close(); else { element.classList.add('closing'); element.style.transform = ''; setTimeout(() => element.classList.remove('closing'), 230); }
    };
    handle.addEventListener('pointerup', end); handle.addEventListener('pointercancel', end);
  }
  draggable(sheet, sheet.querySelector('.sheet-handle'), () => closeAnimated(sheet, () => hideSheet(sheet)));
  // The keyboard's summary is its handle. A tap still toggles it; only a real drag is intercepted.
  draggable(keyboard, keyboard.querySelector('summary'), closeKeyboard);

  // ---- touch mode as a segmented control that drives the real select
  const mode = $('remote-mode');
  if (mode) {
    const control = document.createElement('div'); control.className = 'segmented'; control.setAttribute('role', 'group'); control.setAttribute('aria-label', 'Touch mode');
    control.style.setProperty('--count', String(mode.options.length));
    const thumb = document.createElement('span'); thumb.className = 'thumb'; control.append(thumb);
    [...mode.options].forEach((option, index) => {
      const button = document.createElement('button'); button.type = 'button'; button.textContent = option.textContent;
      button.setAttribute('aria-pressed', String(mode.selectedIndex === index));
      button.addEventListener('click', () => { mode.selectedIndex = index; mode.dispatchEvent(new Event('change')); sync(); });
      control.append(button);
    });
    const sync = () => { control.style.setProperty('--index', String(mode.selectedIndex)); [...control.querySelectorAll('button')].forEach((b, i) => b.setAttribute('aria-pressed', String(i === mode.selectedIndex))); };
    sync(); mode.after(control); mode.addEventListener('change', sync);
  }

  // ---- desktop: viewing layout, control ring, coach mark, immersive idle bar
  const stage = $('remote-stage'), desktop = $('pane-desktop');
  const syncStage = () => {
    desktop.dataset.viewing = String(!stage.hidden);
    if (!stage.hidden && store.get('coach-dismissed') !== '1') $('remote-coach').hidden = false;
  };
  observe(stage, { attributes: true, attributeFilter: ['hidden'] }, syncStage); syncStage();
  const syncControl = () => { stage.dataset.control = String($('remote-control').textContent.startsWith('Disable')); };
  observe($('remote-control'), { childList: true, characterData: true, subtree: true }, syncControl); syncControl();
  $('remote-coach-dismiss').addEventListener('click', () => { $('remote-coach').hidden = true; store.set('coach-dismissed', '1'); });
  const bar = document.querySelector('.viewer-bar'), wrap = document.querySelector('.viewer-wrap');
  let idleTimer = null;
  const wake = () => { bar.dataset.idle = 'false'; clearTimeout(idleTimer); if (body.dataset.expanded === 'true') idleTimer = setTimeout(() => { bar.dataset.idle = 'true'; }, 3000); };
  wrap.addEventListener('pointerdown', wake); wrap.addEventListener('touchstart', wake, { passive: true });
  observe(body, { attributes: true, attributeFilter: ['data-expanded'] }, () => { wake(); });

  // ---- listening level meter, driven by the real output level
  const live = $('listen'), bars = [...document.querySelectorAll('.meter i')];
  let frame = 0;
  function meter(time) {
    frame = 0; if (live.dataset.live !== 'true' || document.hidden) return;
    const level = Math.min(1, (window.listenLevel?.() ?? 0) * 5);
    bars.forEach((bar, index) => { const shape = .55 + .45 * Math.sin(time / 170 + index * 1.3); bar.style.transform = `scaleY(${Math.max(.12, level * shape).toFixed(3)})`; });
    frame = requestAnimationFrame(meter);
  }
  observe(live, { attributes: true, attributeFilter: ['data-live'] }, () => {
    if (live.dataset.live === 'true') { if (!frame) frame = requestAnimationFrame(meter); } else bars.forEach(bar => { bar.style.transform = ''; });
  });
  document.addEventListener('visibilitychange', () => { if (!document.hidden && live.dataset.live === 'true' && !frame) frame = requestAnimationFrame(meter); });

  // ---- rows that appear after the first load fade in; the first load does not animate each row
  for (const id of ['apps-list', 'audio-outputs', 'audio-inputs']) {
    const list = $(id); let primed = false;
    observe(list, { childList: true }, records => {
      if (primed) for (const record of records) for (const node of record.addedNodes) if (node.classList) { node.classList.add('enter'); node.addEventListener('animationend', () => node.classList.remove('enter'), { once: true }); }
      if (list.children.length) primed = true;
    });
  }
})();
