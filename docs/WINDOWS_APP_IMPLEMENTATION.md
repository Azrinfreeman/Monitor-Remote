# Windows launcher implementation plan

Status: Implemented. Automated results and remaining manual trials are recorded in VALIDATION.md.

## Goal

Launch and manage Monitor Remote from a Windows desktop app without opening a terminal. Display the connection URL and pairing code so an iPhone on the same Wi-Fi can open the existing web controller easily.

## Reviewed baseline

- `MonitorRemote.csproj` targets .NET 10 and uses the ASP.NET Core Web SDK.
- `Program.cs` hosts the web UI/API, prints IPv4 URLs and a six-digit pairing code, and starts isolated hardware workers through `--worker`.
- Pairing expires after ten minutes; restarting generates a new code and revokes sessions.
- The mobile UI already controls brightness and requests display off/wake. Preserve its existing behavior and security checks.
- The current project sits at the workspace root, so adding nested C# projects requires explicit exclusions from its default source globs.

## Recommended approach

Add a Windows Forms desktop launcher targeting `net10.0-windows`, with a separate server process. Keeping the server separate preserves worker isolation and lets the launcher recover from server failures without freezing its UI.

Publish a self-contained Windows folder containing the launcher, server and web assets. The user double-clicks `MonitorRemote.Desktop.exe`; a .NET SDK and terminal are not required on the target PC. Start with Windows x64; offer an ARM64 package only if needed.

Keep the existing `dotnet run` workflow for development and diagnostics. Avoid renaming or moving existing source files unless necessary.

## Desktop experience

The main window will contain:

- Server status: Stopped, Starting, Running, Stopping or Error.
- Start, Stop and Restart controls, disabled appropriately while an operation is pending.
- Phone connection URL, Copy URL, Open local controller and a QR code containing only the selected URL.
- A selector for usable private-LAN IPv4 addresses, with interface labels and Refresh addresses. Multiple adapters may exist; the launcher cannot guarantee which address the phone can reach.
- The current pairing code and its remaining validity, plus New pairing code and Disconnect all phones actions.
- A short connection guide: same Wi-Fi, scan QR with iPhone Camera, open Safari, enter the displayed code. Scanning does not pair automatically.
- Connection troubleshooting for wrong adapter, guest Wi-Fi isolation, VPNs, a sleeping PC and Private-network firewall access.
- A bounded diagnostic log with Copy diagnostics; exclude codes, session tokens and other secrets.

Closing the window hides it to the tray after an explanatory first-use message. The tray menu offers Show window, Start/Stop server, Open controller and Exit. Exit stops the server owned by this launcher and removes the tray icon. Stopping or restarting visibly explains that phone sessions will be disconnected.

Start the server automatically when the launcher opens, with a saved setting to disable that behavior. Windows sign-in startup is a separate opt-in setting, disabled by default, with a working disable action. Do not require administrator rights for ordinary launch or startup registration.

## Local management and pairing

Extract pairing/session state into a small server-owned service so local management can renew the code and revoke sessions without restarting the HTTP listener.

- New pairing code renews the ten-minute window and resets pairing attempt counters; existing paired phones remain connected.
- Disconnect all phones clears sessions, invalidates the current code and issues a new one, preventing immediate reuse of the old code.
- Restart also invalidates all sessions, matching current behavior.
- The desktop displays authoritative server state; it does not parse console output for codes or readiness.

Use a per-launch Windows named pipe for management, restricted to the current Windows user, with a random per-launch secret passed through the child process environment. Do not add unauthenticated LAN management endpoints. Do not include credentials in URLs, QR codes or diagnostics. Clear the pairing display when the server stops.

Use versioned, bounded messages for readiness/status, pairing renewal, session revocation and graceful shutdown. Readiness is reported only after the HTTP listener has started. Authenticate the connection and validate each command; add timeouts and handle disconnects without blocking the desktop thread.

## Server lifecycle and packaging

1. Enforce one launcher instance per user; a second launch activates the existing window.
2. Start the bundled server using an absolute executable path and an explicit content root for bundled `wwwroot` assets. Working-directory changes must not break serving files.
3. Redirect and consume both output streams asynchronously with bounded retention. Create the server and workers without console windows.
4. Detect startup failures, port conflicts, unexpected exits and pipe timeouts. Show actionable feedback and an explicit retry; never kill an unrelated process using the port.
5. Track the owned server process and use a Windows job object with kill-on-close behavior to avoid orphaned server/worker processes after launcher failure. Verify worker inheritance and shutdown behavior.
6. Request graceful shutdown first; after a bounded timeout terminate only the owned process tree. Dispose process, pipe, tray and image resources.
7. Preserve `--worker`, hardware serialization/timeouts, demo mode and the existing wake timer reset.
8. Store nonsecret desktop preferences in the current user's local application data. Validate ports from 1–65535; apply a changed port on restart and refresh displayed URLs.

Use a maintained local QR encoder, selected and license-reviewed during implementation. Generate QR images offline; no remote QR service. Keep extra dependencies limited to QR generation if practical.

Publish initially as a portable folder, not a single file or installer. Include all server assets and a clear launch shortcut/instruction. A signed installer and update system can follow later. Record whether packaged executables are unsigned; do not promise they will avoid Windows reputation prompts.

Do not automatically modify firewall rules or router settings. Show the app path and selected port with Private-network setup guidance. Retain the trusted-LAN HTTP model and current pairing protections. The desktop launcher does not wake a sleeping PC or keep it permanently awake.

## Expected file changes

- Add `desktop/MonitorRemote.Desktop.csproj` and desktop window, tray, preferences, lifecycle and management-client code.
- Add server-side pairing state and local management pipe code; integrate these into `Program.cs`.
- Update `MonitorRemote.csproj` to exclude desktop sources/resources and nested build/publish outputs from server compilation and content discovery.
- Add a repeatable packaging script that builds both projects and assembles a fresh dedicated output folder without deleting unrelated user files.
- Add focused lifecycle/management checks; extend demo API checks for changed pairing behavior.
- Update `README.md` and `VALIDATION.md` after implementation with actual launch instructions and results.

## Implementation sequence

1. Introduce pairing state and authenticated local management while preserving console operation and existing API contracts.
2. Build the desktop status window and owned-process lifecycle, including graceful stop, startup failure and crash handling.
3. Add address selection, URL copying, QR display and pairing controls.
4. Add tray behavior, saved preferences and opt-in Windows sign-in startup.
5. Package a self-contained Windows release, run automated checks and record manual verification.

## Acceptance and validation

During implementation, run the existing build, JavaScript syntax, demo API and frontend checks. Add meaningful checks for management authorization, renewal/revocation, readiness failure, occupied ports, duplicate launcher instances and process cleanup. Use demo mode for automated lifecycle tests; no monitor commands are needed for those checks.

Manually verify:

- Double-clicking the packaged launcher works without a terminal or installed SDK, including from a path containing spaces and a different working directory.
- Start/Stop/Restart accurately reflect the real listener state; stopping frees the port and revokes sessions.
- A second launcher does not create a second server; Exit and a forced launcher crash leave no owned server or workers running.
- Copy URL, QR scanning and pairing work on the target iPhone over the same Wi-Fi. Code expiration, renewal and session revocation match the stated behavior.
- Tray restore/exit, unavailable adapters, a port conflict and server failure have understandable outcomes.
- Sign-in startup works when enabled and stops when disabled; deleting/moving a portable package has documented recovery steps.
- Packaged brightness and repeated off/wake still work on the actual monitors, with keyboard/mouse recovery available.
- Windows keyboard navigation, focus, text scaling and screen-reader labels are usable.

Completion requires recording checks actually performed. Demo tests cannot establish physical monitor behavior, firewall reachability or iPhone QR/Safari compatibility.

## Approval boundary

This document is the only planned change at this stage. Do not implement, install dependencies, build/run the app, change startup/firewall settings or issue hardware commands until the user approves implementation.
