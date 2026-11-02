import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const tick = async () => { for (let i = 0; i < 20; i++) await Promise.resolve(); };
function setup({ codecs = false, codecSupported = true, resumeError = false, pendingResume = false, allowSound = true, allowAudio = true, statusError = null } = {}) {
  const elements = Object.fromEntries(['listen', 'listen-toggle', 'listen-note', 'listen-mute'].map(id => [id, { dataset: {}, addEventListener() {} }]));
  const sockets = [], contexts = [], decoded = [];
  let release;
  class Context {
    constructor() { this.currentTime = 0; this.state = 'suspended'; this.played = []; contexts.push(this); }
    resume() {
      if (resumeError) return Promise.reject(new Error('Playback blocked'));
      if (pendingResume) return new Promise(resolve => { release = () => { this.state = 'running'; resolve(); }; });
      this.state = 'running'; return Promise.resolve();
    }
    close() { this.state = 'closed'; return Promise.resolve(); }
    createAnalyser() { return { connect() {} }; }
    createBuffer(channels, frames, rate) {
      const data = Array.from({ length: channels }, () => new Float32Array(frames));
      return { duration: frames / rate, getChannelData: channel => data[channel], copyToChannel: (values, channel) => data[channel].set(values) };
    }
    createBufferSource() {
      const source = { connect() {}, start: time => this.played.push({ time, buffer: source.buffer }) }; return source;
    }
  }
  class Socket {
    constructor(url) { this.url = new URL(url); sockets.push(this); }
    close() { this.closed = true; }
  }
  class Decoder {
    static async isConfigSupported() { return { supported: codecSupported }; }
    configure() {}
    decode(chunk) { decoded.push(chunk); }
    close() {}
  }
  const window = { AudioContext: Context, audioUI: { connection() {}, tab() {} }, monitorApp: {
    async api(path) { assert.equal(path, 'status'); if (statusError) throw new Error(statusError); return { capabilities: { allowSound, allowAudio } }; }
  } };
  vm.runInNewContext(readFileSync(new URL('../wwwroot/listen.js', import.meta.url), 'utf8'), {
    window, document: { getElementById: id => elements[id] }, URL, DataView,
    location: { href: 'http://192.168.1.2:8765/', protocol: 'http:' }, WebSocket: Socket,
    ...(codecs ? { AudioDecoder: Decoder, EncodedAudioChunk: class { constructor(init) { Object.assign(this, init); } } } : {})
  });
  return { elements, sockets, contexts, decoded, window, release: () => release(), tap: () => elements['listen-toggle'].onclick() };
}
function pcmPacket() {
  const data = new ArrayBuffer(9 + 960 * 4), view = new DataView(data);
  view.setUint8(0, 2);
  for (let i = 0; i < 960; i++) {
    view.setInt16(9 + i * 4, 16384, true); view.setInt16(11 + i * 4, -16384, true);
  }
  return data;
}
{
  const ui = setup();
  assert.equal(ui.elements['listen-toggle'].disabled, false, 'LAN HTTP without WebCodecs can listen');
  ui.tap(); await tick();
  assert.equal(ui.sockets[0].url.searchParams.get('format'), 'pcm16');
  ui.sockets[0].onmessage({ data: pcmPacket() });
  const played = ui.contexts[0].played[0];
  assert.equal(played.buffer.getChannelData(0)[0], .5);
  assert.equal(played.buffer.getChannelData(1)[0], -.5);
  assert.equal(played.time, .1);
  ui.tap(); assert.equal(ui.sockets[0].closed, true); assert.equal(ui.contexts[0].state, 'closed');
}
{
  const ui = setup({ codecs: true }); ui.tap(); await tick();
  assert.equal(ui.sockets[0].url.searchParams.has('format'), false, 'supported browsers retain Opus');
  const data = new ArrayBuffer(10); new DataView(data).setUint8(0, 1);
  ui.sockets[0].onmessage({ data }); assert.equal(ui.decoded.length, 1);
  ui.window.audioUI.connection('unpaired'); assert.equal(ui.sockets[0].closed, true);
}
{
  const ui = setup({ codecs: true, codecSupported: false }); ui.tap(); await tick();
  assert.equal(ui.sockets[0].url.searchParams.get('format'), 'pcm16', 'unsupported Opus falls back to PCM');
}
{
  const ui = setup({ resumeError: true }); ui.tap(); await tick();
  assert.equal(ui.sockets.length, 0); assert.equal(ui.contexts[0].state, 'closed');
  assert.match(ui.elements['listen-note'].textContent, /Playback blocked/);
}
{
  const ui = setup({ pendingResume: true }); ui.tap(); ui.tap(); ui.release(); await tick();
  assert.equal(ui.sockets.length, 0, 'stopping during startup never opens a late sound stream');
}
{
  const ui = setup(); ui.tap(); await tick();
  ui.sockets[0].onmessage({ data: '{"error":"Sound streaming was turned off."}' });
  assert.equal(ui.sockets[0].closed, true); assert.match(ui.elements['listen-note'].textContent, /turned off/);
}
console.log('PASS: sound playback over LAN HTTP, stereo PCM samples, Opus selection/fallback, blocked playback, startup cancellation and stream revocation');
{
  const ui = setup({ allowAudio: false }); ui.elements['listen-mute'].checked = true; ui.tap(); await tick();
  assert.equal(ui.sockets.length, 0); assert.match(ui.elements['listen-note'].textContent, /enable audio volume control/);
  assert.equal(ui.contexts[0].state, 'closed');
  ui.elements['listen-mute'].checked = false; ui.tap(); await tick();
  assert.equal(ui.sockets.length, 1, 'sound alone never requires volume-control permission');
}
for (const [options, message] of [
  [{ allowSound: false }, /Enable PC sound streaming/],
  [{ statusError: 'Enter the pairing code from your PC to connect again.' }, /pairing code/],
  [{ statusError: 'PC unreachable.' }, /PC unreachable/]
]) {
  const ui = setup(options); ui.tap(); await tick();
  assert.equal(ui.sockets.length, 0); assert.match(ui.elements['listen-note'].textContent, message);
}
console.log('PASS: sound permission, separate mute permission, expired pairing and connection errors are explained before opening a stream');
