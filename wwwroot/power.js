// The browser displays PC-owned deadlines. It never dispatches a power action on a timer.
(() => {
  const $ = id => document.getElementById(id);
  const labels = { shutdown: 'Shut down', restart: 'Restart', sleep: 'Sleep' };
  const actionNames = { shutdown: 'shutdown', restart: 'restart', sleep: 'sleep' };
  let snapshot = null, selected = false, busy = false, loading = false, epoch = 0, serverOffset = 0, uncertain = false, fresh = false, operationError = null;
  const terminal = { cancelled: 'Cancelled.', missed: 'The scheduled time was missed. Create a new task.', requested: 'Request accepted by Windows. The resulting PC state is unconfirmed.', unconfirmed: 'The power outcome is unconfirmed. It will not be retried.', failed: 'The power request failed.' };
  const storageKey = 'monitor-pc-power-request';
  const storedRequest = () => { try { return sessionStorage.getItem(storageKey); } catch { return null; } };
  const saveRequest = value => { try { if (value) sessionStorage.setItem(storageKey, value); else sessionStorage.removeItem(storageKey); } catch { /* Reconciliation still works for this tab's current task. */ } };
  const active = () => snapshot?.current;
  function statusText(id, text) { if ($(id).textContent !== text) $(id).textContent = text; }
  function controls() {
    const enabled = window.monitorApp.connected && fresh && !busy && !uncertain && !!snapshot?.available && snapshot.allowPower && !active();
    $('pc-power-submit').disabled = !enabled || ($('pc-power-action').value === 'sleep' && !snapshot?.sleepSupported);
    for (const id of ['pc-power-action', 'pc-power-mode', 'pc-power-hours', 'pc-power-minutes', 'pc-power-at']) $(id).disabled = busy || !!active();
    $('pc-power-cancel').disabled = !window.monitorApp.connected || busy || !active() || !['pending', 'paused'].includes(active().state);
    $('pc-power-refresh').disabled = !window.monitorApp.connected || loading || busy;
    $('pc-power-dismiss').hidden = !uncertain;
  }
  function timing() {
    const mode = $('pc-power-mode').value;
    $('pc-power-delay').hidden = mode !== 'delay'; $('pc-power-date').hidden = mode !== 'at';
    $('pc-power-timing').textContent = mode === 'now' ? 'Starts a 10-second countdown with time to cancel.' : mode === 'delay' ? 'From 1 minute to 365 days. The timer starts when the PC accepts it.' : 'Uses the PC time zone, even if your phone is in a different zone.';
    $('pc-power-submit').textContent = mode === 'now' ? labels[$('pc-power-action').value] + ' PC…' : 'Schedule ' + actionNames[$('pc-power-action').value] + '…';
    controls();
  }
  function countdown() {
    const task = active(); if (!task) return;
    const seconds = Math.max(0, Math.ceil((Date.parse(task.dueAtUtc) - Date.now() - serverOffset) / 1000));
    const days = Math.floor(seconds / 86400), hours = Math.floor(seconds % 86400 / 3600), minutes = Math.floor(seconds % 3600 / 60);
    $('pc-power-countdown').textContent = task.state === 'paused' ? 'Paused' : task.state === 'dispatching' || seconds === 0 ? 'Requesting…' : (days ? days + 'd ' : '') + [hours, minutes, seconds % 60].map(n => String(n).padStart(2, '0')).join(':');
    const message = task.state === 'paused' ? 'Paused after server restart. Resume it in the PC app, or cancel.' : task.state === 'dispatching' ? 'Sent for execution. Cancellation is no longer available.' : seconds <= 10 ? 'Power action is due shortly. Cancel now to stop it.' : 'Scheduled on the PC. This task continues when your phone disconnects.';
    if ($('pc-power-task-state').textContent !== message) $('pc-power-task-state').textContent = message;
  }
  function render() {
    if (!snapshot) { controls(); return; }
    statusText('pc-power-note', snapshot.error || (snapshot.allowPower ? 'Connected to ' + snapshot.pcName + (snapshot.simulated ? ' · Simulation' : '') : 'Enable PC shutdown, restart and sleep in the PC app.'));
    $('pc-power-zone').textContent = 'Date and time on the PC · ' + snapshot.timeZoneName;
    const sleepOption = $('pc-power-action').querySelector('option[value="sleep"]'); if (sleepOption) sleepOption.disabled = !snapshot.sleepSupported;
    const task = active(); $('pc-power-task').hidden = !task;
    if (task) {
      $('pc-power-task-title').textContent = labels[task.action] + ' ' + snapshot.pcName;
      // The server supplies the deadline's offset, which may differ from today's due to DST.
      const pcDate = (task.dueAtLocal || task.dueAtUtc).slice(0,16).replace('T', ' ');
      $('pc-power-due').textContent = pcDate + ' · ' + snapshot.timeZoneName + ' (phone: ' + new Date(task.dueAtUtc).toLocaleString() + ')';
      countdown();
    } else if (!uncertain && !operationError && snapshot.history?.length) {
      const last = snapshot.history[0]; statusText('pc-power-result', (last.simulated ? 'Simulated: ' : '') + (last.error || terminal[last.state] || last.state));
    }
    controls();
  }
  async function load() {
    if (!window.monitorApp.connected || loading || busy) return;
    const version = epoch; loading = true; controls();
    try {
      const result = await window.monitorApp.api('pc-power'); if (version !== epoch) return;
      snapshot = result; fresh = true; serverOffset = Date.parse(result.serverNowUtc) - Date.now();
      const requestId = storedRequest();
      if (requestId) {
        const match = [result.current, ...result.history].find(t => t?.requestId === requestId);
        if (match) { uncertain = false; operationError = null; saveRequest(null); }
        else { uncertain = true; $('pc-power-result').textContent = 'No matching task is visible yet. Refresh and check the PC; this request will not be resent.'; }
      }
      render();
    } catch (error) { if (version === epoch) $('pc-power-note').textContent = error.message; }
    finally { loading = false; controls(); if (version !== epoch && window.monitorApp.connected) load(); }
  }
  function newId() {
    if (window.crypto?.randomUUID) return window.crypto.randomUUID();
    const bytes = new Uint8Array(16); window.crypto.getRandomValues(bytes); bytes[6] = bytes[6] & 15 | 64; bytes[8] = bytes[8] & 63 | 128;
    const hex = [...bytes].map(b => b.toString(16).padStart(2, '0')).join(''); return [hex.slice(0,8),hex.slice(8,12),hex.slice(12,16),hex.slice(16,20),hex.slice(20)].join('-');
  }
  $('pc-power-form').addEventListener('submit', async event => {
    event.preventDefault(); if ($('pc-power-submit').disabled) return;
    const action = $('pc-power-action').value, mode = $('pc-power-mode').value;
    const request = { requestId: newId(), action, mode };
    let when = 'after a 10-second countdown';
    if (mode === 'delay') {
      const hours = Number($('pc-power-hours').value), minutes = Number($('pc-power-minutes').value);
      if (!Number.isInteger(hours) || hours < 0 || !Number.isInteger(minutes) || minutes < 0 || minutes > 59 || hours * 3600 + minutes * 60 < 60 || hours * 3600 + minutes * 60 > 31536000) { $('pc-power-result').textContent = 'Choose a timer from 1 minute to 365 days.'; return; }
      request.delaySeconds = hours * 3600 + minutes * 60; when = 'in ' + hours + ' hours and ' + minutes + ' minutes';
    } else if (mode === 'at') {
      request.localDateTime = $('pc-power-at').value; request.timeZoneId = snapshot.timeZoneId;
      if (!request.localDateTime) { $('pc-power-result').textContent = 'Choose a PC date and time.'; return; }
      when = 'at ' + request.localDateTime.replace('T', ' ') + ' (' + snapshot.timeZoneName + ')';
    }
    if (!confirm(labels[action] + ' ' + snapshot.pcName + ' ' + when + '?\n\nKeep Monitor Remote running. Phone access will end. Unsaved work may block shutdown or restart.')) return;
    const version = epoch; busy = true; operationError = null; saveRequest(request.requestId); controls();
    try {
      const result = await window.monitorApp.api('pc-power/tasks', request); if (version !== epoch) return;
      snapshot.current = result.task; serverOffset = Date.parse(result.serverNowUtc) - Date.now(); uncertain = false; saveRequest(null);
      $('pc-power-result').textContent = 'Task saved on the PC.'; render();
      $('pc-power-task').scrollIntoView?.({block:'nearest'});
    } catch (error) {
      if (error.status && storedRequest() === request.requestId) saveRequest(null);
      if (version !== epoch) return;
      if (error.status) { saveRequest(null); uncertain = false; } else uncertain = true;
      operationError = error.message; $('pc-power-result').textContent = operationError;
    } finally { busy = false; controls(); if (version === epoch) await load(); }
  });
  $('pc-power-cancel').onclick = async () => {
    const task = active(); if (!task || $('pc-power-cancel').disabled) return;
    const version = epoch; busy = true; operationError = null; controls();
    try {
      await window.monitorApp.api('pc-power/tasks/' + task.id + '/cancel', { revision: task.revision });
      if (version === epoch) { snapshot.current = null; $('pc-power-result').textContent = 'Power task cancelled.'; }
    } catch (error) { if (version === epoch) { operationError = error.message; $('pc-power-result').textContent = operationError; } }
    finally { busy = false; controls(); if (version === epoch) await load(); }
  };
  $('pc-power-refresh').onclick = load;
  $('pc-power-dismiss').onclick = () => {
    if (!confirm('Have you checked the PC and refreshed its power status? The previous request may have reached Windows. Dismissing this notice does not cancel it.')) return;
    saveRequest(null); uncertain = false; operationError = null; $('pc-power-result').textContent = 'Notice dismissed. No request was resent.'; controls();
  };
  $('pc-power-mode').onchange = $('pc-power-action').onchange = timing;
  window.powerUI = {
    tab(value) { selected = value; if (value) load(); },
    connection(state) { epoch++; fresh = false; if (state !== 'connected') { controls(); } else load(); },
    health() { if (!snapshot) load(); },
    get pending() { return !!active() || uncertain; }
  };
  document.addEventListener('visibilitychange', () => { if (!document.hidden) load(); });
  setInterval(() => { if (selected && !document.hidden) load(); }, 2000);
  setInterval(() => { if (selected && !document.hidden) countdown(); }, 1000);
  timing();
})();
