(() => {
  const $ = id => document.getElementById(id), base = window.audioUI;
  const supported = !!(window.AudioContext || window.webkitAudioContext);
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
    schedule(buffer);
  }
  function schedule(buffer) {
    const now = context.currentTime;
    // Re-anchor after an underrun, and drop audio that would put playback too far behind the PC.
    if (nextTime < now + 0.02) nextTime = now + JITTER;
    if (nextTime - now > MAX_AHEAD) return;
    const source = context.createBufferSource();
    source.buffer = buffer; source.connect(analyser || context.destination); source.start(nextTime); nextTime += buffer.duration;
  }
  async function start() {
    if (!supported) { note('This browser cannot play PC sound. Try another browser.'); return; }
    let started;
    try {
      // Creating and resuming the AudioContext inside the tap is what lets iOS play sound.
      context = new (window.AudioContext || window.webkitAudioContext)({ sampleRate: 48000, latencyHint: 'interactive' });
      started = context;
      const resumed = context.resume();
      analyser = context.createAnalyser(); analyser.fftSize = 256; analyser.connect(context.destination);
      nextTime = 0; listening = true; button(); note('Connecting…');
      await resumed;
      if (context !== started) return;
      if (context.state !== 'running') throw new Error('Playback was blocked. Tap Listen again to allow sound.');
      // HTTP gives actionable permission/session errors that a rejected WebSocket handshake hides.
      const { capabilities } = await window.monitorApp.api('status');
      if (context !== started) return;
      if (!capabilities?.allowSound) throw new Error('Enable PC sound streaming in the PC app, then try again.');
      if ($('listen-mute')?.checked && !capabilities.allowAudio) throw new Error('To mute PC speakers, enable audio volume control in the PC app or turn off "Mute PC speakers while listening".');
      const config = { codec: 'opus', sampleRate: 48000, numberOfChannels: 2 };
      let opus = false;
      if (typeof AudioDecoder === 'function' && typeof EncodedAudioChunk === 'function') {
        try { opus = (await AudioDecoder.isConfigSupported(config)).supported; } catch { /* use PCM instead */ }
      }
      if (context !== started) return;
      if (opus) {
        decoder = new AudioDecoder({ output: play, error: () => { if (context === started) stop('Sound decoding failed. Try listening again.'); } });
        decoder.configure(config);
      }
      const url = new URL('/api/audio/stream', location.href); if ($('listen-mute')?.checked) url.searchParams.set('mutePc', '1'); url.protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
      if (!opus) url.searchParams.set('format', 'pcm16');
      socket = new WebSocket(url); socket.binaryType = 'arraybuffer';
      let first = true;
      socket.onmessage = event => {
        if (typeof event.data === 'string') { try { stop(JSON.parse(event.data).error || 'Sound stream ended.'); } catch { stop('Sound stream ended.'); } return; }
        const view = new DataView(event.data);
        if (view.byteLength <= 9 || view.getUint8(0) !== (opus ? 1 : 2)) return;
        if (first) { first = false; note($('listen-mute')?.checked ? 'Listening. PC speakers are muted until you stop.' : 'Listening. PC speakers keep playing too.'); }
        const timestamp = view.getUint32(5, true) / 48000 * 1e6;
        try {
          if (opus) decoder.decode(new EncodedAudioChunk({ type: 'key', timestamp, data: new Uint8Array(event.data, 9) }));
          else {
            if (view.byteLength !== 9 + 960 * 2 * 2) throw new Error('Invalid PCM frame');
            const buffer = context.createBuffer(2, 960, 48000);
            for (let channel = 0; channel < 2; channel++) {
              const data = buffer.getChannelData(channel);
              for (let i = 0; i < 960; i++) data[i] = view.getInt16(9 + (i * 2 + channel) * 2, true) / 32768;
            }
            schedule(buffer);
          }
        } catch { stop('Sound playback failed. Try listening again.'); }
      };
      socket.onclose = () => { if (listening) stop('Sound stream closed. Enable "Allow PC sound streaming" in the PC app, then try again.'); };
      socket.onerror = () => { if (listening) stop('Could not connect to PC sound. Check the PC connection and sound permission. If muting PC speakers is selected, also allow audio volume control or turn that option off.'); };
    } catch (error) {
      if (!started || context === started) stop(error.message || 'Could not start sound playback. Try listening again.');
    }
  }
  try { if ($('listen-mute') && localStorage.getItem('listen-mute') === '1') $('listen-mute').checked = true; } catch { /* private mode */ }
  $('listen-mute')?.addEventListener('change', () => { try { localStorage.setItem('listen-mute', $('listen-mute').checked ? '1' : '0'); } catch { /* private mode */ } });
  $('listen-toggle').onclick = () => { if (listening) stop(); else start(); };
  window.audioUI = {
    connection(state) { base.connection(state); if (state === 'offline' || state === 'unpaired') stop(); },
    tab(active) { base.tab(active); }
  };
  button();
  if (!supported) note('This browser cannot play PC sound. Try another browser.');
})();
