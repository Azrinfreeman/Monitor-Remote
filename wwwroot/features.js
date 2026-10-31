(() => {
  const get = id => document.getElementById(id);
  let selected = 'monitors';
  const features = {
    remoteActive: false,
    connection(state) {
      for (const name of ['desktop', 'audio']) window[name === 'desktop' ? 'remoteUI' : 'audioUI']?.connection(state);
    },
    health(health) {
      const capabilities = health.capabilities;
      get('feature-tabs').hidden = !capabilities;
      if (!capabilities) choose('monitors');
    },
    get selected() { return selected; },
    active(active) { this.remoteActive = active; window.monitorApp.updateControls(); }
  };
  // Cross-fade between panes with the View Transitions API where it exists; elsewhere the pane just switches.
  function choose(name) {
    const reduced = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
    if (name !== selected && !reduced && typeof document.startViewTransition === 'function') document.startViewTransition(() => apply(name));
    else apply(name);
  }
  function apply(name) {
    if (selected === 'desktop' && name !== 'desktop') window.remoteUI?.stop('Viewing stopped.');
    selected = name;
    for (const pane of ['monitors', 'desktop', 'audio']) {
      get('pane-' + pane).hidden = pane !== name;
      get('tab-' + pane).setAttribute('aria-pressed', String(pane === name));
    }
    document.body.dataset.tab = name;
    window.remoteUI?.tab(name === 'desktop'); window.audioUI?.tab(name === 'audio');
  }
  for (const name of ['monitors', 'desktop', 'audio']) get('tab-' + name).onclick = () => choose(name);
  window.monitorFeatures = features;
})();
