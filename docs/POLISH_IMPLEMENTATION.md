# Monitor Remote polish implementation plan

Status: Phase 1 implemented after the user's instruction to proceed. Automated checks passed; target iPhone review is pending. Phases 2 and 3 remain future increments.

## Objective

Make the working iPhone controller feel responsive, clear and dependable during everyday use on the home Wi-Fi. Keep the existing Windows brightness controller, pairing protection and working off/wake behavior.

## Current baseline

Reviewed `wwwroot/index.html`, `wwwroot/style.css`, `wwwroot/app.js`, `Program.cs`, `README.md`, `IMPLEMENTATION.md` and `VALIDATION.md`.

The app already provides a dark mobile layout, per-monitor sliders, four brightness presets, pairing, refresh, logout and all-display off/wake requests. Hardware operations run in isolated workers with a timeout. The user reports that the app works; historical validation still lists some device checks as pending.

The UI currently shows requested brightness without reconciling it with readback, has no fetch timeout or foreground reconnection flow, and allows overlapping refresh and power actions. These are the first areas to polish.

## Phase 1 — Everyday control experience (recommended first)

### Visual layout and accessibility

- Refine spacing, typography and contrast while retaining the compact dark design.
- Give each monitor a clear card with its name, brightness percentage, slider, presets and local operation feedback.
- Make Wake and Turn displays off easy to distinguish, keeping the existing off confirmation.
- Keep touch targets at least 44 CSS pixels, visible keyboard focus, accessible slider labels and polite status announcements.
- Account for iPhone safe areas, narrow screens, landscape and larger text. Avoid motion unless it respects reduced-motion preferences.
- Separate unsupported controls from controls temporarily busy sending a request.

### Honest brightness feedback

- Distinguish requested, applying, confirmed and failed values.
- Inspect the worker response contract before implementation; use returned brightness when available and add readback fields only if necessary.
- Preserve the latest slider intent while coalescing rapid changes. An older response must not overwrite a newer requested value.
- On failure, retain the last confirmed value and offer retry. If readback is unavailable, label the result as unconfirmed.
- Preserve slider focus and in-progress edits when updating status; avoid rebuilding all monitor cards unnecessarily.

### Connection and request handling

- Add a bounded fetch timeout and plain-language feedback for an unreachable PC, an expired session, a busy controller and a hardware timeout.
- Show connecting, connected, reconnecting and offline states without treating an unpaired session as a network failure.
- Recheck the session when the page becomes visible again; refresh monitor readings once connected. Use online/offline events as hints, then verify with the server.
- Prevent stale refresh responses from replacing newer state and stop queued brightness requests for removed cards or expired sessions.
- Disable duplicate pairing/refresh submissions and serialize both power buttons while a power request is pending.
- Do not automatically replay brightness or power commands after reconnection. Keep power state explicitly unconfirmed.
- Keep retry activity bounded and pause it when the page is hidden.

Files expected: `wwwroot/index.html`, `wwwroot/style.css`, `wwwroot/app.js`; `Hardware.cs` or `Program.cs` only if the existing response contract needs refinement.

## Phase 2 — Personal convenience

- Add editable monitor labels and custom brightness presets stored locally in the browser.
- Verify monitor identity before persisting per-monitor preferences. Current IDs follow enumeration and can change after reconnecting cables; do not attach saved settings to an index alone.
- Provide a reset option for saved preferences. Never store pairing codes or session tokens in browser storage.
- Add a purpose-made home-screen icon and improve the existing Add to Home Screen instructions. Test launch and session behavior on the actual iPhone.
- Do not promise offline control or add a service worker to cache authenticated API responses. The PC must remain reachable.

Files expected: frontend files, local icon assets and `README.md`; monitor identity support if needed.

## Phase 3 — Windows launch and pairing convenience

- Add a tray host with the current LAN URL, pairing code, regenerate-code action, revoke-sessions action and quit control.
- Generate a QR code locally containing only the app URL; keep code entry as the pairing step.
- Allow pairing-code renewal without restarting the controller, accessible only from the local PC controls.
- Offer explicit opt-in startup at Windows sign-in with a visible disable option and document how to stop the app.
- Evaluate packaging and dependency choices before implementation. Keep the existing console launch available for troubleshooting.
- Preserve private-network guidance and avoid automatic router or firewall changes.

This is a separate implementation increment because it changes the app's Windows lifecycle and packaging.

## Deferred scope

Per-monitor power, waking a sleeping PC, remote internet access, HTTPS provisioning, scheduled brightness and automatic brightness changes are outside this polish pass. They introduce separate behavior and setup requirements.

## Implementation order

1. Confirm existing API/readback fields and request behavior; capture the current demo UI as the comparison baseline.
2. Implement Phase 1 request state and reconnection handling.
3. Apply the visual and accessibility refinements to those states.
4. Run the checks below and update setup/validation notes with actual results.
5. Review Phase 1 on the target iPhone before beginning optional Phases 2 and 3.

## Validation and acceptance

During implementation, run `dotnet build`, `node --check wwwroot/app.js` and the existing `node tests/smoke.mjs` demo checks. Extend checks only for changed contracts and meaningful behavior: stale responses, request timeout, session expiry, latest slider intent and duplicate power requests. Add a focused browser-level check if the current tooling cannot exercise these UI behaviors.

Manually verify on iPhone Safari:

- Pairing, logout and session expiry show clear next steps.
- Rapid slider changes settle on the latest requested value and show accurate confirmation.
- Failed brightness commands never appear confirmed.
- Refresh and power controls cannot issue conflicting duplicate operations.
- Returning from the background or restoring Wi-Fi recovers the UI without replaying hardware commands.
- Unsupported monitors, an unplugged monitor and worker timeout have understandable feedback.
- Narrow/landscape screens, larger text and VoiceOver remain usable.
- Existing brightness presets and repeated off/wake still work on the target monitor.

Completion requires passing applicable automated checks and recording which device checks were actually performed. Demo responses do not establish physical monitor behavior.

## Review checkpoint

Phase 1 is implemented. Review it on the target iPhone before beginning optional Phases 2 and 3. See `VALIDATION.md` for performed checks and remaining device checks.
