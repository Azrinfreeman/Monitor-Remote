(() => {
  const $ = id => document.getElementById(id), base = window.audioUI;
  const supported = typeof AudioDecoder === 'function' && typeof EncodedAudioChunk === 'function' && (window.AudioContext || window.webkitAudioContext);
  const JITTER = 0.1, MAX_AHEAD = 0.5;
  let socket = null, context = null, decoder = null, nextTime = 0, listening = false, analyser = null;
  const samples = new Uint8Array(128);
  // Output level for the on-screen meter (0 to 1).
  window.listenLevel = () => {
    if (!analyser) return 0;
    analyser.getByteTimeDomainData(samples);
    let sum = 0; for (const v of samples) { const x = (v - 128) / 128; sum += x * x; }
    return Math.sqrt(sum / samples.length);
  };
  function note(text) { $('listen-note').textContent = text; }
  function button() { if ($('listen-mute')) $('listen-mute').disabled = listening; const card = $('listen'); if (card) card.dataset.live = String(listening); $('listen-toggle').textContent = listening ? 'Stop listening' : 'Listen to PC sound'; $('listen-toggle').disabled = !supported; }
  function stop(message) {
    const wasListening = listening; listening = false;
    if (socket) { socket.onclose = socket.onerror = socket.onmessage = null; try { socket.close(); } catch { /* already closed */ } socket = null; }
    if (decoder) { try { decoder.close(); } catch { /* already closed */ } decoder = null; }
    if (context) { context.close().catch(() => {}); context = null; analyser = null; }
    button();
    if (message) note(message); else if (wasListening) note('Stopped listening.');
  }
  function play(audioData) {
    if (!context) { audioData.close(); return; }
    const buffer = context.createBuffer(audioData.numberOfChannels, audioData.numberOfFrames, audioData.sampleRate);
    for (let channel = 0; channel < audioData.numberOfChannels; channel++) {
      const data = new Float32Array(audioData.numberOfFrames);
      audioData.copyTo(data, { planeIndex: channel, format: 'f32-planar' });
      buffer.copyToChannel(data, channel);
    }
    audioData.close();
    const now = context.currentTime;
    // Re-anchor after an underrun, and drop audio that would put playback too far behind the PC.
    if (nextTime < now + 0.02) nextTime = now + JITTER;
    if (nextTime - now > MAX_AHEAD) return;
    const source = context.createBufferSource();
    source.buffer = buffer; source.connect(analyser || context.destination); source.start(nextTime); nextTime += buffer.duration;
  }
  function start() {
    if (!supported) { note('This browser cannot decode the sound stream. Update iOS to 26 or later.'); return; }
    // Creating and resuming the AudioContext inside the tap is what lets iOS play sound.
    context = new (window.AudioContext || window.webkitAudioContext)({ sampleRate: 48000, latencyHint: 'interactive' });
    context.resume().catch(() => {});
    analyser = context.createAnalyser(); analyser.fftSize = 256; analyser.connect(context.destination);
    decoder = new AudioDecoder({ output: play, error: () => stop('Sound decoding failed. Try listening again.') });
    decoder.configure({ codec: 'opus', sampleRate: 48000, numberOfChannels: 2 });
    nextTime = 0; listening = true; button(); note('Connecting…');
    const url = new URL('/api/audio/stream', location.href); if ($('listen-mute')?.checked) url.searchParams.set('mutePc', '1'); url.protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
    socket = new WebSocket(url); socket.binaryType = 'arraybuffer';
    let first = true;
    socket.onmessage = event => {
      if (typeof event.data === 'string') { try { stop(JSON.parse(event.data).error || 'Sound stream ended.'); } catch { stop('Sound stream ended.'); } return; }
      const view = new DataView(event.data);
      if (view.byteLength <= 9 || view.getUint8(0) !== 1) return;
      if (first) { first = false; note($('listen-mute')?.checked ? 'Listening. PC speakers are muted until you stop.' : 'Listening. PC speakers keep playing too.'); }
      const timestamp = view.getUint32(5, true) / 48000 * 1e6;
      try { decoder.decode(new EncodedAudioChunk({ type: 'key', timestamp, data: new Uint8Array(event.data, 9) })); } catch { stop('Sound decoding failed. Try listening again.'); }
    };
    socket.onclose = () => { if (listening) stop('Sound stream closed. Enable "Allow PC sound streaming" in the PC app, then try again.'); };
    socket.onerror = () => { if (listening) stop('Could not start the sound stream. Enable "Allow PC sound streaming" in the PC app.'); };
  }
  try { if ($('listen-mute') && localStorage.getItem('listen-mute') === '1') $('listen-mute').checked = true; } catch { /* private mode */ }
  $('listen-mute')?.addEventListener('change', () => { try { localStorage.setItem('listen-mute', $('listen-mute').checked ? '1' : '0'); } catch { /* private mode */ } });
  $('listen-toggle').onclick = () => { if (listening) stop(); else start(); };
  window.audioUI = {
    connection(state) { base.connection(state); if (state === 'offline' || state === 'unpaired') stop(); },
    tab(active) { base.tab(active); }
  };
  button();
  if (!supported) note('This browser cannot decode the sound stream. It needs iOS 26 or a current desktop browser.');
})();
