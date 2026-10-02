# Project state

Saved: 2026-10-03 (Asia/Kuala_Lumpur)
Project: Monitor Remote
Repository: https://github.com/Azrinfreeman/Monitor-Remote
Visibility: private
Default branch: main

## Current milestone

The remote desktop and PC audio feature is implemented and packaged. Source, documentation, test fixtures and validation records are ready for version control. This workspace began without a Git repository; it now uses a local `main` branch. Git itself holds the current commit, remote and publication status.

## Implemented behavior

- Existing external-monitor brightness and all-display off/wake controls.
- Live JPEG viewing of one selected Windows display, including displays without brightness support.
- Touchpad/direct touch mouse movement, clicks, dragging, scrolling, zoom and fit controls.
- On-screen keys, shortcuts and committed native-phone-keyboard text entry with Unicode support.
- Default PC output volume/mute, explicit all-playback-device actions, and individual output/microphone endpoint controls where Windows supports them.
- Independent local viewing, input and audio permissions, disabled on each server launch/restart. Input requires viewing permission; audio does not require screen sharing.
- Authenticated frame/control WebSockets, single session ownership, frame freshness/revision validation, bounded input, heartbeat lease, revocation and held-input cleanup.
- Windows launcher/tray sharing status and Stop remote session controls.
- Failure/readback feedback and no automatic replay of mouse, text, volume, brightness or power commands after uncertain outcomes/reconnection.

This release uses reachable trusted-LAN HTTP/WebSockets. Access while away from home, encrypted gateways, PC sound streaming, login/unlock and per-application audio mixing are outside this milestone.

## Source and dependencies

- `Program.cs`, `PairingState.cs`, `LocalManagement.cs`, `ManagementProtocol.cs`: hosting, pairing, permissions and local management.
- `Hardware.cs`: existing isolated brightness/power workers.
- `Remote/`: display catalog/DPI, isolated persistent capture worker, input validation and session/stream lifecycle.
- `Audio/`: Windows Core Audio enumeration/interop, bounded MTA service, endpoint readback and protected audio routes.
- `desktop/`: Windows Forms launcher, tray, server/job ownership, QR and preferences.
- `wwwroot/`: plain HTML/CSS/JavaScript mobile controller with Displays, Desktop and Audio tabs.
- `tests/`: demo API, frontend behavior, remote/audio integration and packaged launcher checks.

Build: .NET 10 SDK, Windows x64. Last validated SDK: 10.0.400; runtimes: 10.0.11. JavaScript checks used Node.js 24.19.0. Packages: System.Drawing.Common 10.0.0, Microsoft.Win32.SystemEvents 10.0.0 and launcher QRCoder 1.8.0; notices are in THIRD_PARTY_NOTICES.md.

## Last validation

- Source/checks build and Windows x64 self-contained publish: passed, zero compiler warnings/errors.
- 42 remote/audio checks: passed against source and packaged server.
- 21 packaged launcher checks: passed; report in `tests/remote-launcher-check-results.txt`.
- `tests/smoke.mjs`, `tests/frontend.mjs` and `tests/features.mjs`: passed.
- Synthetic browser QA: portrait 390 × 844 and landscape 844 × 390; viewing, simulated click/key/text, navigation/Stop, audio readback and partial bulk results verified. No horizontal overflow or captured JavaScript errors.
- Read-only actual Windows audio discovery: 51 endpoints, 25 outputs, 26 inputs; 19 supported volume readback. No real volume/mute setting changed.
- All eight packaged web assets matched source hashes.

Automated tests used synthetic capture/input/audio. They did not capture the real desktop or inject real mouse/keyboard input. Temporary demo servers/tabs were closed and their ports released. No firewall/router or sign-in startup changes were made.

## Local package and preserved files

Latest feature package: `artifacts/MonitorRemote-win-x64-20261003-021911-a87ae824/MonitorRemote.Desktop.exe`.

The original `App` folder remains the older brightness/power package. Use the new artifact for these features. `.gitignore` excludes `App`, `artifacts`, `bin`, `obj` and `publish`; Git stores the source and records, while these generated packages remain available locally. Recreate a self-contained package with:

```powershell
powershell -ExecutionPolicy Bypass -File .\package.ps1
```

## Resume and verification

Read `docs/REMOTE_DESKTOP_IMPLEMENTATION.md` for the implemented design, `docs/VALIDATION.md` for detailed results/remaining acceptance, and README.md for usage. `docs/DEVELOPER_GUIDE.md` is updated for the new features. The previously generated developer-guide PDF predates this extension; use the Markdown guide as the current reference.

```powershell
dotnet build tests/MonitorRemote.Checks.csproj
node tests/smoke.mjs
node tests/frontend.mjs
node tests/features.mjs
dotnet tests/bin/Debug/net10.0/MonitorRemote.Checks.dll
```

For packaged checks, pass the absolute `server/MonitorRemote.exe` path as the remote/audio suite's argument. Set `MONITOR_CHECK_REPORT` before launching `MonitorRemote.Desktop.exe --check` for the launcher report.

Remaining manual acceptance: actual screen capture, iPhone Safari touch/native keyboard/accessibility, real mouse/typing behavior, mixed-DPI/hotplug, lock/UAC, Wi-Fi latency/recovery, real speaker/microphone level/mute changes, Windows external-volume/default changes, long-session resource use, GUI/tray rendering and forced launcher termination during capture. ARM64 and a PC without .NET installed are unvalidated. Executables are unsigned.
