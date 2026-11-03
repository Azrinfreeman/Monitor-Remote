# Validation results — 2026-10-01

## PC power implementation — 2026-11-03

Implemented PC shutdown/restart/sleep with immediate, duration and PC-local date/time modes. New controls reuse the phone theme and native WinUI launcher patterns. All power execution during validation used fake/demo behavior; no real shutdown, restart, sleep, privilege elevation, firewall change or sign-in registration was performed.

- Source/checks builds and fresh Windows x64 self-contained packaging passed with zero compiler warnings/errors. The WinUI build uses `-r win-x64`; an architecture-free self-contained build is unsupported. A temporary QA server initially held a Debug assembly open; it was stopped before the successful final build.
- C# checks passed: 94 PC power checks plus 79 existing remote/audio checks. Power coverage includes all nine action/mode combinations, exact deadlines, duration bounds, DST gaps/overlaps, PC-zone interpretation, cookie expiry/logout retention, targeted/all revocation, creation/cancellation ordering, clock jumps during cleanup, stale revisions, startup pause/resume, missed tasks, idempotency, storage locking/corruption/write failure and ambiguous dispatch without replay. Report: `tests/pc-power-check-results.txt`.
- Harmless blocking-worker probes passed for normal stop and forced parent termination. Failed held-input release and blocked audio recovery prevent dispatch and release the transition guard. A read-only temporary mute marker causes only the task to fail; an HTTP integration check confirms continued server availability. Every server fixture isolates its power and mute-marker paths; an inherited stand-in personal task store stays unchanged.
- `tests/power.mjs`, `tests/frontend.mjs`, `tests/features.mjs`, `tests/smoke.mjs` and 58 `tests/design-checks.mjs` assertions passed. Frontend checks cover structured conflicts, retained error messages, UUID generation on HTTP, lost acknowledgement reconciliation, no automatic replay, cancellation and paused/dispatching controls. Syntax checks passed for the new JavaScript.
- Packaged launcher checks passed 29 assertions, including default-disabled power permission, ordinary command conflicts preserving management, a pending task appearing over the local pipe, saved-task restoration paused, explicit local resume and cancellation. Report: `tests/pc-power-launcher-results.txt`. This check bypasses the GUI.
- Edge/CDP visual acceptance passed at 390 × 844 light, 375 × 667 dark with reduced motion, and 844 × 390 landscape. Four tabs fit without horizontal overflow. The real page creates/cancels a date/time sleep task and a shutdown timer, then completes an immediate simulated restart. Native confirmations are checked, countdown live announcements are disabled, and no JavaScript runtime errors remain. A skipped view-transition rejection during resizing was fixed. Final captures/results: `artifacts/pc-power-browser-ready/`.
- The editable tutorial is `docs/tutorial/pc-power.md`; `node docs/tutorial/build-tutorial.mjs pc-power` produces its HTML companion. The continuous HTML export was visually inspected for typography, wrapping and footer clearance. Browser Print-to-PDF and the documented 15-second customization were not exercised. PDF page layout is not claimed.

Final portable package: `artifacts/MonitorRemote-PCPower-20261103-ready/MonitorRemote.Desktop.exe`. Source web-asset hashes match the package. Its server and launcher DLL hashes match the binaries used for the 29 packaged checks. Existing packages were preserved. No installer or OS scheduler/service was added.

Remaining manual acceptance: physical Windows shutdown/restart/sleep and resume, policy denial, unsaved-app veto, actual capture/input/audio cleanup during transition, iPhone Safari date-picker/gestures/VoiceOver, native launcher/tray visual rendering and high-contrast behavior. WinUI analyzer checks were not configured or run. ARM64 and a PC without installed development runtimes remain unvalidated. Existing historical results below retain their original scope and date.


Wake fix validation:
- Replaced the wake broadcast with `SetThreadExecutionState(ES_DISPLAY_REQUIRED)` to reset the Windows display idle timer. Off still uses the existing broadcast. No continuous keep-awake request is held.
- `dotnet build`, `node --check wwwroot/app.js`, and `node tests/smoke.mjs`: passed. Smoke checks cover both power actions and their unconfirmed simulated responses.
- Native wake worker: returned `requested: true, confirmed: false` successfully. This verifies the native call succeeded, not visible recovery from an off display.
- Still requires physical retest: turn the display off from the phone, wait, request wake, and verify it stays on until the configured Windows display timeout. Repeat several times. Screen saver and lock-screen behavior are separate from the display idle timer.

- `dotnet build`: passed, zero warnings and errors.
- `node --check wwwroot/app.js`: passed.
- `node tests/smoke.mjs`: passed. Verified served UI, pairing, unauthorized access rejection, cross-origin POST rejection, brightness and power input validation, isolated demo worker, and logout revocation.
- Read-only native discovery: one Generic PnP Monitor, brightness supported, reported 12%. No physical settings changed.

At the initial console milestone, visible brightness changes, off/wake recovery, target iPhone Safari, Wi-Fi/firewall configuration, disconnect/reconnect, actual native timeout behavior, and startup after restart remained pending. Desktop work below supersedes the earlier tray/QR/startup deferrals. HTTPS remains unimplemented.

## Phase 1 polish — 2026-10-01

Implemented the recommended Phase 1 scope from POLISH_IMPLEMENTATION.md:
- Refined monitor cards, power controls, safe-area spacing, keyboard focus and 48px touch controls. Status messages remain accessible; slider nodes survive refresh. No animations were added.
- Native brightness responses now include nullable brightness readback. The UI distinguishes requested, applying, confirmed, differing readback, unavailable readback and failure states. Failed operations restore the last known value and offer explicit retry.
- Rapid changes coalesce to the latest intent. Session changes cancel queued requests and reject stale replies. Refresh and power operations cannot overlap with brightness writes; both power buttons lock together.
- Added 12-second fetch timeout, clear pairing/offline states, manual reconnect, and session/readings refresh on foreground or online events. No automatic hardware-command replay or polling retries.
- Existing native off/wake behavior is unchanged. Phases 2 and 3 (preferences, icon, tray, QR pairing and startup) remain deferred.

Checks performed:
- dotnet build: passed with zero warnings and errors.
- node --check wwwroot/app.js: passed.
- node tests/smoke.mjs: passed; includes nullable demo brightness response and existing authentication, origin, validation, power and logout checks.
- node tests/frontend.mjs: passed. Uses the actual frontend with a minimal DOM and controlled fetch/timers to check latest intent, differing readback, unconfirmed responses, failure/retry, timeout without replay, expiry, power serialization, foreground recovery, node preservation, removal, logout and pairing.

Limits: no browser automation tool was available. The minimal DOM checks do not establish Safari rendering, focus behavior in a real browser, VoiceOver, layout or physical monitor behavior. No native hardware commands were run in this polish pass. The user previously reported the app works; this version still needs an iPhone trial: rapid slider/preset changes, off/wake, background/foreground, Wi-Fi loss/recovery, larger text, landscape and VoiceOver.

## Windows launcher — 2026-10-01

Implemented Windows Forms window, tray controls, private IPv4 address selection, offline QR generation, pairing renewal/revocation, saved preferences and opt-in sign-in startup. An authenticated current-user named pipe manages a separate server; a kill-on-close Windows job owns the server and its children. Portable self-contained x64 packaging includes the web assets and runtime.

Reviewed and corrected retry handling to preserve the running server's actual port, startup-checkbox rollback after registry failure, activation before the form handle exists, and a nullable cookie warning. Verified QRCoder's MIT notice against the installed package and corrected its copyright years.

Checks performed:

- Server Release build and desktop Release publish passed with zero warnings and errors.
- Self-contained win-x64 package created in `artifacts/MonitorRemote-win-x64-20261001-041920-be735378`.
- JavaScript syntax, demo API smoke and frontend behavior checks passed.
- Packaged executable `--check` passed all 16 assertions: readiness, static assets, unauthorized management rejection, pairing, renewal preserving sessions, old-code rejection, revocation, oversized message recovery, graceful stop, wildcard-port conflict cleanup, restart revocation and job disposal freeing the port. Results: `tests/launcher-check-results.txt`.
- The port-conflict fixture now reserves the same fixed wildcard address as the server with exclusive access; restarting a port-zero listener had selected a different port. Oversized writes tolerate the server closing the pipe at its size limit.

No physical monitor commands, firewall changes or startup registration were performed during this completion pass. The executable check bypasses the GUI. Manual trials remain for window rendering/accessibility, tray interaction, second-instance activation, forced launcher crash and worker inheritance, sign-in enable/disable, QR scanning and Safari over Wi-Fi, hardware brightness/off/wake, and launching on a PC without .NET installed. ARM64 packaging is supported by the script but untested. Executables are unsigned.

To rerun lifecycle checks, set `MONITOR_CHECK_REPORT` to a writable report path, then run the packaged `MonitorRemote.Desktop.exe --check`. The check starts its own demo server and does not operate real monitors.

## Folder cleanup � 2026-10-01

Moved the latest icon-equipped self-contained Windows x64 package into `App` and kept its required runtime and server files together. Moved planning and validation documents into `docs`. Removed the obsolete package and generated root/desktop bin and obj directories after verification. Source, tests, assets and third-party notices were preserved. Excluded App and docs from server content so later publishing does not copy the portable package into itself.

Validation: relocated App executable --check passed all 16 assertions; server build passed with zero warnings/errors; JavaScript syntax, frontend and demo API smoke checks passed. Build outputs were removed after checks. Physical hardware and GUI were not exercised during cleanup. If sign-in startup was enabled for the former package path, enable it again from the relocated app to register its new path.

## Remote desktop and PC audio — 2026-10-03

Implemented the approved screen viewer, touchpad/direct mouse controls, keyboard/text entry, independent audio endpoint controls, local permission switches and session Stop controls. The existing App package is preserved.

Performed checks:

- Server/checks build and self-contained Windows x64 server/desktop publish passed with zero compiler warnings/errors. New package: `artifacts/MonitorRemote-win-x64-20261003-021911-a87ae824`.
- The new package's launcher `--check` passed 21 assertions. Added default-disabled permissions, local switches, packaged synthetic audio discovery, permission preservation on renewal and reset on restart. Report: `tests/remote-launcher-check-results.txt`.
- Remote/audio suite passed 42 checks against both the source build and packaged server. It covers permissions, negative display origins, frame age/revisions, duplicate commands, Unicode/surrogates, drag release, logout/expiry/revocation, pairing renewal, single session/socket ownership, fragmented binding, wrong-Origin upgrade, oversized input, ordinary POST protection, sustained JPEG streaming and heartbeat expiry. Audio covers auth/permissions, finite bounded values, explicit mute, catalog/default guards, readback preserving mute, per-endpoint partial bulk results, and microphone exclusion.
- Browser QA exposed a missing receive loop on the frame WebSocket: ping/pong handling timed out even while images were sent. Both frame/control sockets now receive; a six-second sustained-stream regression verifies survival past keepalive timeout. Fast scrolling is also coalesced and bounded.
- Original `tests/smoke.mjs` and `tests/frontend.mjs` passed. New `tests/features.mjs` passed: letterbox/direct-touch mapping, scrolling coalescing, stale-frame controls, uncertain text cleanup/no replay, latest audio slider intent, preserved mute, default/device-card serialization, persistent failure feedback, reconnect without replay, unsupported endpoints and output-only partial results.
- All eight packaged HTML/JavaScript/CSS assets match their validated source hashes.
- In-app browser demo QA at 390 × 844 portrait and 844 × 390 landscape: pairing, synthetic live screen, explicit Enable control, simulated click/on-screen key, committed multiline Unicode text, Stop/navigation cleanup, audio readback, partial Apply to all and unchanged microphone level. No horizontal overflow or captured JavaScript errors. Corrected unavailable-device notes and wrapped viewer buttons for the narrow layout. Preview: `docs/remote-desktop-demo.png`.
- A separate read-only native Windows Core Audio check discovered 51 endpoints (25 outputs, 26 inputs); 19 supported volume readback. This validates real enumeration/readback and COM activation/disposal on this PC, not setting compatibility. No real level/mute change was made.
- Demo browser tab, temporary server and viewport override were cleaned up. No firewall/router, startup registration or machine setting was changed.

Remaining manual acceptance: real screen capture quality, mouse/keyboard insertion into target apps, iPhone Safari gestures/native keyboard/VoiceOver, mixed-DPI and hotplug mappings, lock/UAC transitions, Wi-Fi latency/recovery, actual playback/microphone changes and readback, native volume notifications/default changes, long-session resource use, GUI/tray rendering, forced launcher crash with an active capture worker and a PC without .NET installed. ARM64 is not validated. Away-from-home networking remains outside this release. Executables are unsigned and transport is the existing trusted-LAN HTTP/WebSocket connection.
