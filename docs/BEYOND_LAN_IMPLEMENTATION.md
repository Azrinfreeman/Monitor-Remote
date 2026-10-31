# Beyond-LAN implementation plan

Status: **phases 1-4 and 5a implemented on branch `feat/beyond-lan`; 5b spike not run; 5c not started.** See "Implementation status" at the end.
Baseline: `main` at `eb5743d`. Related: `REMOTE_DESKTOP_IMPLEMENTATION.md`, `VALIDATION.md`.

This plan covers the five items listed as out of scope in `PROJECT_STATE.md`:

1. Encrypted connections
2. Access from outside the home network
3. Streaming PC sound to the phone
4. Per-application audio mixing
5. Login and unlock from the phone

## Recommended order

| Phase | Item | Why here |
|-------|------|----------|
| 1 | Encrypted connections | Everything else sends secrets or media. Remote access and unlock must not ship without it. |
| 2 | Remote access | Depends on phase 1 (TLS, host and origin handling). |
| 3 | Per-app audio mixing | Small, self-contained, extends the existing audio service. Can run in parallel with phases 1-2. |
| 4 | PC sound streaming | New capture pipeline. Benefits from TLS but works on LAN. |
| 5 | Login and unlock | Riskiest. Starts with a feasibility spike and a go/no-go decision. Needs phases 1-2 first. |

Each phase ships independently, off by default, with its own permission toggle and its own entry in `docs/VALIDATION.md`.

## Constraints from the current code

These are what the plan has to change or work around.

- `Program.cs:20` binds `http://0.0.0.0:{port}` only. There is no HTTPS endpoint.
- `Program.cs:36` rejects any `Host` that is not loopback or a private IPv4 address. A tunnel or MagicDNS hostname gets a 403.
- `Program.cs:37,39,40` hardcode `http://` and `ws://` in the CSP and Origin checks.
- `Program.cs:49` sets the session cookie without `Secure`.
- `PairingState.cs:26` rate-limits pairing by `RemoteIpAddress`. Behind a proxy or tunnel every client may share one address, so the limiter would either lock everyone out or stop limiting.
- Pairing uses a 6-digit code valid for 10 minutes with 10 attempts per address. That is acceptable on a LAN. It is weak for an internet-reachable endpoint.
- `Audio/AudioControlService.cs` uses a single MTA thread and a bounded queue. That suits short control calls, not a continuous capture loop.
- `NativeInput.Apply` refuses to act when `DesktopDisplayCatalog.Interactive()` is false (locked or no normal desktop). The lock screen is therefore deliberately unsupported today.

## Phase 1 - Encrypted connections

### Goal

Serve the controller over HTTPS and WSS with a certificate the phone trusts without installing profiles or tapping through warnings.

### Options considered

| Option | Verdict |
|--------|---------|
| Self-signed cert on a LAN IP | Rejected as the default. iOS Safari shows a warning, and WebSockets and some APIs are unreliable until the cert is trusted. Installing a profile is poor UX. |
| Own domain plus Let's Encrypt (DNS-01) | Works, but needs a domain, DNS credentials and renewal handling. Keep as an advanced option. |
| **Tailscale HTTPS certificates (`tailscale cert`) for the `*.ts.net` name** | **Recommended.** Real, publicly trusted cert, works on the LAN and remotely, no domain needed. Pairs with phase 2. |
| Cloudflare Tunnel (TLS terminated at Cloudflare) | Viable for phase 2, but traffic is decrypted at a third party. See phase 2. |

### Design

- Add configuration (`appsettings` or the launcher's `Preferences`): `Tls.CertificatePath`, `Tls.CertificatePassword` (or a Windows certificate store thumbprint), `Tls.Port` (default 8766), `AllowedHosts` (list of hostnames).
- Kestrel listens on HTTPS when a certificate is configured. Plain HTTP stays available on the LAN only when `Tls.AllowPlainHttp` is true (default true for backward compatibility; the launcher warns when it is on).
- Replace the private-IP host check with: loopback, private IPv4, or a hostname in `AllowedHosts`. Keep the check, since it still blocks DNS-rebinding.
- Make Origin and CSP scheme-aware: derive `https`/`wss` from `ctx.Request.Scheme`.
- Set cookies `Secure` when the request is HTTPS. Add `Strict-Transport-Security` on HTTPS responses.
- Certificate renewal: the launcher re-reads the certificate file on a timer and reloads Kestrel's certificate selector without restarting the server (so sessions survive).
- Launcher: show the HTTPS URL and QR code when TLS is configured, and a clear "unencrypted" indicator otherwise.

### Files likely touched

`Program.cs`, `desktop/Preferences.cs`, `desktop/ServerController.cs`, `desktop/MainForm.cs`, `wwwroot/*.js` (any hardcoded `ws://`), `tests/`.

### Verification

- Unit: scheme-aware Origin/CSP logic; `AllowedHosts` accepts listed names and rejects unlisted names and spoofed `Host` values.
- Integration: HTTPS pair, cookie has `Secure`, WSS frames and control connect, HTTP-to-WSS mixed use is rejected.
- Manual: iPhone Safari on LAN with a Tailscale cert shows no warning; certificate renewal does not drop an active session.

## Phase 2 - Access from outside the home network

### Goal

Reach the PC from mobile data or another Wi-Fi without opening a router port to the public internet.

### Recommendation

**Tailscale (WireGuard) as the supported path.** The PC and phone join the same tailnet, and the app is only reachable by the user's own devices. No port forwarding, and the phase 1 `*.ts.net` certificate gives HTTPS.

Document two alternatives but do not build them in this phase:

- Cloudflare Tunnel: easy, but Cloudflare sees decrypted traffic including the screen stream and typed text. Only suitable if the user accepts that.
- Router port forward: not supported. The README should say so, because the pairing code is not strong enough for the open internet.

### Design

- No tunnel code inside the app. The app only needs to be reachable and to accept the tailnet hostname (phase 1 `AllowedHosts`).
- Tailscale binds traffic as a local source, so the existing per-IP pairing limiter still sees distinct peer addresses. If a reverse proxy is ever used, add `ForwardedHeaders` handling with an explicit trusted-proxy list, never trusting `X-Forwarded-For` by default.
- Harden pairing for non-LAN clients:
  - Require the pairing code to be confirmed on the PC for a client that is not on a private LAN address (launcher shows "Approve device X?").
  - Lengthen the code to 8 digits or shorten its lifetime to 2 minutes, and lock out after 5 failures per window.
  - Shorter session lifetime for remote clients (for example 2 hours, renewable).
- Session visibility: launcher lists active sessions with address, device label and start time, and offers Revoke.
- Audit log: append-only local file of pair, revoke, permission change and remote-session start/stop events. No secrets, no typed text.
- Resilience on slow links: adaptive JPEG quality and frame rate based on measured WebSocket round trip. The frame protocol already has freshness and revision validation, so dropping frames is safe.

### Files likely touched

`PairingState.cs`, `Program.cs`, `Remote/RemoteSessionManager.cs`, `Remote/DesktopCaptureService.cs` (quality control), `LocalManagement.cs` and `ManagementProtocol.cs` (approval and session list over the existing management pipe), `desktop/MainForm.cs`.

### Verification

- Unit: clock-controlled tests for the new lockout and expiry rules, using the existing `TimeProvider` injection in `PairingState`.
- Integration: non-LAN pairing blocked until approved; revoke closes WebSockets and releases held input.
- Manual: phone on mobile data via Tailscale; throttled-network recovery; no router changes needed.

## Phase 3 - Per-application audio mixing

### Goal

Show each app that is playing sound (browser, Spotify, game) with its own volume slider and mute, per output device.

### Design

- Use the Core Audio session APIs: `IAudioSessionManager2` from each active render endpoint, `IAudioSessionEnumerator`, `IAudioSessionControl2` (process id, display name, session instance id) and `ISimpleAudioVolume` for volume and mute.
- Extend `Audio/CoreAudioInterop.cs` and `Audio/AudioDeviceCatalog.cs` with a `Sessions(deviceId)` snapshot. Reuse the same single MTA worker, since these are short calls.
- Identity: sessions come and go as apps start and stop. Key a session by `(deviceId, sessionInstanceId)`, and carry the process name and icon-less display name. Include a snapshot `Revision` exactly like devices do today, and reject changes with 409 when the revision is stale.
- Endpoints, mirroring the existing style:
  - `GET /api/audio/sessions`
  - `POST /api/audio/sessions/{id}/volume`
  - `POST /api/audio/sessions/{id}/mute`
- Keep the read-back confirmation behavior: set, read back within 1 point, report `confirmed` or the mismatch.
- Authorization: reuse the existing "Allow audio volume control" permission. No new toggle is needed because this is the same class of action.
- Frontend: new "Apps" section in the Audio tab (`wwwroot/audio.js`), grouped by output device, sorted by recent activity, hiding the system-sounds session unless it is active.
- Limits to document: some protected or elevated processes may refuse changes; the UI reports "rejected the change" per session, as it does for devices.

### Verification

- Demo provider (`AudioDemoProvider`) gains synthetic sessions so the existing synthetic suite covers the endpoints.
- Real discovery check, read-only first (like the earlier 51-endpoint discovery), then a manual volume change on a test app.

## Phase 4 - Streaming PC sound to the phone

### Goal

Listen to what the PC is playing on the phone, with latency good enough for video and music (target under about 300 ms, not for gaming or lip-sync critical use).

### Design

- **Capture:** WASAPI loopback of the default render endpoint (`AUDCLNT_STREAMFLAGS_LOOPBACK`). Run it on its own dedicated thread, not on the control worker in `AudioControlService`. Handle default-device changes by re-opening the stream.
- **Encode:** Opus (20 ms frames, around 64-96 kbps stereo, 48 kHz) using a managed Opus library so the existing self-contained publish has no native dependency. Fallback when Opus is unavailable: 16-bit PCM at 24 kHz mono for diagnosis only.
- **Transport:** a new authenticated WebSocket, `/api/audio/stream`, added to the existing WebSocket allow-list at `Program.cs:38`. Binary frames carry a sequence number and timestamp. One listener per session, bounded send queue, drop oldest on backlog so latency never grows.
- **Playback in the browser:** WebCodecs `AudioDecoder` into an `AudioContext`, with a small adaptive jitter buffer (start about 80 ms). iOS needs a user gesture to start audio, so playback begins from an explicit "Listen" button. If WebCodecs Opus is not available on the user's iOS version, fall back to a WASM Opus decoder; check this early, since it decides the client work.
- **Permission and privacy:** new local toggle "Allow PC sound streaming", off at every server start like the others. Loopback captures everything the PC plays, including notifications and calls, so the launcher must show a persistent "Streaming sound" indicator and the Stop remote session control must end it. Streaming does not require screen sharing, matching how audio volume works today.
- **PC audio while streaming:** loopback does not mute the PC speakers. Add an optional "mute PC speakers while listening" action, implemented through the existing endpoint mute call, and restore the previous state afterwards, including on crash or disconnect.
- **Limits:** one stream at a time; stream stops on session revoke, heartbeat loss, or permission revocation; no recording or storing of audio anywhere.

### Files likely touched

New `Audio/LoopbackCapture.cs`, `Audio/AudioStreamEndpoint.cs`; `Audio/CoreAudioInterop.cs`; `Remote/RemoteSessionManager.cs` (permission and lifecycle); `Program.cs`; `wwwroot/audio.js` and a new `wwwroot/listen.js`; `desktop/MainForm.cs` (indicator and toggle); `MonitorRemote.csproj` (Opus package) and `THIRD_PARTY_NOTICES.md`.

### Verification

- Synthetic: demo loopback provider emits a sine tone; check sequence, ordering, drop-oldest, and revoke/permission-off teardown.
- Manual: music and video on iPhone Safari, latency measured with a clap test, default device switch mid-stream, sleep and wake, mute-and-restore after a forced disconnect.

## Phase 5 - Login and unlock from the phone

### Goal

When the PC is locked, see the lock screen and enter the Windows PIN or password from the phone.

### Why this is different

The lock screen and UAC prompts run on the secure Winlogon desktop. The current server runs as the logged-in user and cannot capture or inject input there, which is why `NativeInput.Apply` refuses when locked. Supporting unlock means adding a component with much higher privilege, and it weakens the PC's physical-security posture: a paired phone plus whatever extra factor we require becomes a way into the machine.

I have not verified how well the approaches below work on current Windows 11 builds; that is the point of the spike.

### Approaches

| Approach | Notes |
|----------|-------|
| A. Wake only | Detect lock state, show "PC is locked", wake the display. No credential entry. Low risk. |
| B. SYSTEM helper that attaches to the Winlogon desktop | A Windows service launches a helper in the active console session with `SetThreadDesktop` to `Winlogon`, captures that desktop and injects keystrokes with `SendInput`. Needs installation with admin rights and ideally code signing. |
| C. Custom Credential Provider | A COM DLL loaded by LogonUI that accepts credentials from the app. Deeply integrated and fragile, and it is a high-value attack target. Not recommended. |

### Plan

1. **5a (ship first, low risk):** lock-state detection via session notifications, a "PC is locked" state in the Desktop tab instead of confusing errors, and display wake. Optionally Wake-on-LAN if the PC sleeps, with a clear note that it only works on the same LAN or via a tailnet subnet router.
2. **5b spike (time-boxed, no shipping):** prove approach B on a test machine. Questions to answer: can the helper capture the secure desktop reliably; does `SendInput` work for password and PIN entry; behavior with Windows Hello PIN, Microsoft accounts, and UAC prompts; effect of Secure Boot, virtualization-based security, and Smart App Control; what signing is needed. Write the result into `VALIDATION.md` and make a go/no-go decision before any production code.
3. **5c (only if the spike says go):**
   - Separate optional installer or component; the main portable app stays unprivileged and keeps working without it.
   - Off by default, enabled in the launcher with an explicit warning.
   - Requires HTTPS (phase 1) and a client that arrived over the tailnet or LAN; never plain HTTP.
   - Extra factor: a separate unlock PIN set on the PC, distinct from the pairing code, with strict lockout (for example 3 failures, then 15 minutes).
   - Secrets handling: the Windows password or PIN is sent only over TLS, held in memory only for the injection, never logged, never stored, never echoed back in diagnostics or error messages.
   - Helper accepts commands only from the authenticated server over a locally authorized pipe, and only while the session is locked.
   - Every unlock attempt is written to the audit log from phase 2.
   - Clear uninstall path that removes the service and helper.

### Verification

- Threat review before coding 5c: stolen paired phone, replay, local malware talking to the pipe, shoulder-surfing the PIN entry on the phone.
- Manual only for the real thing: lock, unlock with PIN, wrong-PIN lockout, UAC prompt, sleep and wake, fast user switching, forced helper termination. Synthetic tests cover only the pipe protocol and lockout rules.

## Cross-cutting work

- **Capability reporting:** extend `/api/status` capabilities (`remote.Capabilities()`) with `tls`, `remoteClient`, `soundStream`, `appMixer`, `unlock` so the frontend shows only what is available.
- **Permissions:** keep the "disabled on each server start" rule. New toggles: "Allow PC sound streaming" (phase 4) and "Allow unlock" (phase 5). Per-app mixing reuses the volume toggle.
- **Documentation:** update `README.md` (packaged builds copy it without `docs`), `docs/DEVELOPER_GUIDE.md`, `docs/VALIDATION.md`, `THIRD_PARTY_NOTICES.md` for any new package, and `PROJECT_STATE.md`. The developer-guide PDF remains stale; regenerate it or drop it.
- **Tests:** extend `tests/` suites for each phase; keep the rule that automated tests use synthetic capture, input and audio, and record manual acceptance separately.
- **Packaging:** `package.ps1` must include any new files; confirm the self-contained publish still has no native runtime dependency beyond what is documented.

## Risks

| Risk | Mitigation |
|------|------------|
| Exposing remote control to the internet | Tailscale-only support, no port-forward guidance, approval on the PC for non-LAN pairing, audit log. |
| Unlock weakens the PC | Separate component, off by default, extra PIN, TLS-only, lockout, spike with a go/no-go gate. |
| Sound streaming leaks private audio | Separate local permission, persistent indicator, one listener, nothing stored. |
| iOS Safari audio and codec support | Early spike for WebCodecs Opus; WASM decoder fallback. |
| Cert renewal drops sessions | Hot certificate reload without restarting Kestrel. |
| Pairing limiter fails behind a proxy | Trusted-proxy allow-list for forwarded headers, otherwise ignore them. |
| Unsigned executables get blocked | Signing is a separate decision; needed most for phase 5c. |

## Decisions (recorded 2026-10-31)

1. Tailscale is the supported remote path.
2. Plain HTTP on the LAN stays available when TLS is configured.
3. Phase 5 continues past 5a to the spike and a possible privileged component.
4. Minimum iOS for sound streaming: 26 (WebCodecs Opus; no WASM fallback needed).
5. No code signing available; assume unsigned binaries.

## Implementation status (2026-10-31)

| Phase | State | Differences from the plan |
|-------|-------|---------------------------|
| 1 TLS | Done | Configured by environment variables and `desktop.json`; no launcher settings screen. HSTS omitted because plain HTTP stays available. |
| 2 Remote access | Done, partly | PC approval, 2-hour remote sessions, 5-attempt lockout, session list and revoke-by-id (management commands), audit log. Not done: launcher screen listing sessions with per-session Revoke (only the approval prompt exists), adaptive JPEG quality, ForwardedHeaders handling (no proxy is supported). Pairing code length unchanged (6 digits) because approval gates remote clients. |
| 3 Per-app mixing | Done | Read-only discovery verified on the real PC; real volume writes not yet exercised. |
| 4 Sound streaming | Done, partly | Loopback + Opus + WebCodecs; real capture verified from a script (100 packets); not yet heard on an iPhone. Not done: "mute PC speakers while listening", WASM fallback (iOS 26 only, per decision 4). |
| 5a Lock state | Done | `locked` capability and a Desktop-tab message. |
| 5b Spike | Not run | Protocol in `docs/UNLOCK_SPIKE.md`; needs a throwaway machine. |
| 5c Unlock helper | Not started | Blocked on the spike. |
