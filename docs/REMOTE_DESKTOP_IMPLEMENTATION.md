# Remote desktop viewing, touch control and PC audio implementation plan

Date: 2026-10-03

Status: Implemented and packaged after the user approved proceeding. Automated and synthetic browser checks pass; real PC/iPhone acceptance remains pending. This document began as the requested proposal and retains the design and manual acceptance criteria.

## 1. Goal and scope

Extend Monitor Remote so an iPhone can show the Windows PC's live screen, move and click the mouse using touch, type into the PC using an on-screen keyboard, and control PC audio volume across its Windows audio devices.

The first release will use the existing browser controller and pairing flow on a reachable trusted home network. The PC can use Ethernet while the phone uses Wi-Fi. Here, "from afar" means controlling the PC without sitting beside it; access while away from home is a separate extension described in section 10.

Included in the first release:

- Live view of one selected Windows display, including laptop displays even when their brightness cannot be controlled through DDC/CI.
- A display selector for multiple-monitor setups.
- Touchpad mode and direct touch mode, with left click, double click, right click, dragging and scrolling.
- An on-screen keyboard, special keys and common shortcuts.
- A phone text entry field that opens the native iPhone keyboard and sends committed text to the PC.
- PC master volume and mute/unmute, an explicit all-playback-devices action, and individual volume/mute controls for every audio endpoint Windows exposes, including microphone/input levels where supported.
- Explicit viewing/control switches, a visible sharing indicator on the PC, and Stop remote session actions on both devices.
- Connection recovery that never replays mouse clicks or text.

The PC must be awake, the app running, and the normal Windows desktop available for screen viewing and mouse/keyboard control. Audio volume control works independently of desktop sharing. Streaming PC sound to the phone, file transfer, clipboard synchronization, recording, gaming-quality video, Windows login/unlock, Ctrl+Alt+Delete and unattended public internet access are outside this first release.

## 2. Existing project and integration points

The current implementation contains:

| Area | Current behavior | Planned integration |
| --- | --- | --- |
| `Program.cs` | ASP.NET Core .NET 10 server on port 8765; private IPv4 Host checks; protected JSON endpoints | Add remote/audio endpoints, authenticated WebSockets and remote-session lifecycle |
| `PairingState.cs` | Six-digit pairing, 12-hour cookie sessions, renewal, logout and revocation | Notify remote sessions of logout/revocation and enforce expiry during open connections |
| `Hardware.cs` | Isolated DDC/CI workers and all-display off/wake | Retain isolation; keep capture/input outside the brightness-operation gate |
| `LocalManagement.cs`, `ManagementProtocol.cs` | Authenticated current-user management pipe | Add local remote/audio permissions, status and stop commands |
| `desktop/MainForm.cs`, `desktop/ServerController.cs` | Windows launcher, tray, server ownership and management client | Add permissions, active-session status and local Stop action |
| `wwwroot/index.html`, `wwwroot/app.js`, `wwwroot/style.css` | iPhone controller, pairing, brightness, power and reconnect states | Add Desktop and Audio tabs and load their dedicated UI logic |
| `package.ps1` | Self-contained Windows launcher/server package | Include new capture dependencies and assets in a fresh package |
| `tests/smoke.mjs`, `tests/frontend.mjs`, `desktop/LauncherChecks.cs` | Demo API, frontend behavior and launcher lifecycle checks | Extend relevant checks and add focused remote-session checks |

Capture display IDs must be separate from the current enumeration-based physical-monitor brightness IDs. A display with unsupported brightness must still be eligible for viewing.

Audio endpoints also need their own device IDs; never associate audio volume with a monitor's brightness ID or a device's position in an enumeration list.

## 3. User experience

### On the PC

Add three local switches: **Allow screen viewing**, **Allow mouse and keyboard control** and **Allow audio volume control**. All start disabled on each server launch; mouse/keyboard control requires viewing to be enabled. Audio permission is independent, so a paired phone can adjust volume without starting screen sharing. Pairing alone does not enable these new capabilities.

When a remote session is active, the window and tray show **Screen sharing active** or **Remote control active**, the selected display and a **Stop remote session** action. Stopping ends that session and releases held inputs. Disabling a permission also stops the affected activity. Existing Disconnect all phones remains a full revocation action.

These permissions can authorize subsequent phone starts while enabled, so using the phone does not require returning to the PC for every session. Console users get equivalent explicit launch options, disabled by default.

### On the phone

Keep the existing monitor controls and add a **Desktop** tab:

1. Pair with the PC using the existing code.
2. Open Desktop, select a display, and tap **Start viewing**.
3. Show the live screen, connection state, display name, quality setting and Stop button.
4. Tap **Enable control** when the PC permits it. Viewing starts without mouse/keyboard input enabled.
5. Select **Touchpad** or **Direct touch**, and open **Keyboard** when needed.

Use a compact toolbar and a collapsible keyboard drawer. In landscape, let the viewer occupy most of the page. Provide Fit screen and zoom/pan controls; expanded viewing must work even if Safari fullscreen is unavailable. Account for safe areas and native keyboard resizing. Keep Stop reachable and controls at least 44 CSS pixels.

Input remains disabled until a fresh frame is visible. A frozen/disconnected viewer shows a clear paused state; ending or revoking sharing clears the screen image from the page.

### Touch behavior

| Gesture or control | Touchpad mode | Direct touch mode |
| --- | --- | --- |
| One-finger movement | Move the cursor relative to its current position | Position the cursor at the touched image location |
| Tap / double tap | Left click / double click | Move to target, then left click / double click |
| Long press | Right click at cursor | Right click at touched target |
| Two-finger movement | Vertical/horizontal wheel scrolling | Pan the locally zoomed preview |
| Pinch | Zoom the preview locally | Zoom the preview locally |
| Drag control | Hold the explicit Drag button while moving on the pad | Activate Drag, then press/move/release on the image |
| Scroll control | Optional dedicated scroll strip | Dedicated scroll strip to avoid conflict with preview panning |

Provide visible Left click, Right click and Drag controls as alternatives to gestures. Distinguish taps from motion with movement/time thresholds and suppress the follow-up click after a long press. Releasing or cancelling a drag releases the mouse button.

Use Pointer Events, pointer capture and cancellation handling. Restrict browser touch suppression to the viewer/touchpad so the rest of the page remains scrollable and accessible. See [Pointer Events](https://developer.mozilla.org/en-US/docs/Web/API/Pointer_events).

### PC and device volume controls

Add an **Audio** tab available after pairing when the PC permits audio control:

- **PC volume:** 0–100% master slider and mute/unmute for the current default multimedia playback device, with its name always visible.
- **All playback devices:** an explicit Apply volume to all action and Mute all / Unmute all controls for the currently available output devices. Show Mixed when their confirmed values differ instead of inventing a single current volume.
- **Playback devices:** a card for each speaker, headphone, HDMI/DisplayPort monitor audio, USB audio, Bluetooth audio or virtual output exposed by Windows, with its own volume, mute and availability.
- **Recording devices:** separate cards for microphones, headset microphones, USB microphones, line-in and virtual inputs, with input level and mute where supported. Playback-wide actions never change microphone settings.
- Mark default playback/communications devices, keep disconnected or disabled devices visible as unavailable, and provide Refresh devices. Distinguish identically named endpoints by device details.

Changing a slider must preserve mute state; unmuting requires the explicit Unmute action. Keep requested values separate from Windows readback, show partial failures per device, and update the page when volume changes through Windows or hardware buttons. Ending a remote session leaves intentionally applied volume settings in place. Per-application mixer controls and changing the default audio device are future additions.

## 4. Recommended technical design

### Capture and frame delivery

Use a persistent Windows capture worker that captures the selected display, scales the image and JPEG-encodes it. Start with `Graphics.CopyFromScreen`; it copies a screen rectangle and reports native capture failures. See [CopyFromScreen](https://learn.microsoft.com/en-us/dotnet/api/system.drawing.graphics.copyfromscreen?view=windowsdesktop-10.0).

Add a .NET 10-compatible, pinned `System.Drawing.Common` dependency during implementation and keep its use behind Windows-only boundaries. Retain the server's `net10.0` target and a non-native demo path. This library is Windows-specific; see [Microsoft's platform support documentation](https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/6.0/system-drawing-common-windows-only). Verify redistribution notices when packaging.

The capture worker is long-lived rather than launched once per frame. It communicates through a bounded, authenticated current-user pipe and inherits the launcher's existing owned job. The server also explicitly stops its worker when run from the console. A worker that loses its parent pipe exits; a capture stall triggers a bounded watchdog and terminates only that owned worker. Capture failure pauses sharing without taking down brightness control.

Initial tuning values, to be measured on the target PC and iPhone:

- Balanced: maximum image dimension 1280 pixels, JPEG quality about 70, target 8 frames/second.
- Clear: maximum dimension 1600 pixels, higher quality and a lower frame rate if necessary.
- Low bandwidth: maximum dimension 960 pixels and target 4 frames/second.
- At most one pending frame per stage; overwrite/drop obsolete frames instead of accumulating them.
- Stop capture when the remote session ends. Do not save frames or recording files.

Use two same-origin WebSockets: a binary frame connection and a small JSON control/status connection. Keeping image delivery separate prevents large image messages from blocking input acknowledgements. Use a single serialized sender on each socket and bounded writes/queues. Disable WebSocket compression initially; JPEG frames are already compressed.

Each binary frame has a versioned envelope containing header length, JSON metadata and JPEG bytes. Metadata identifies sequence, display, display-layout revision, capture time, native bounds, encoded dimensions and cursor position/visibility. Bound metadata and image sizes before allocation. The phone keeps only the latest frame for decoding/rendering and acknowledges the rendered sequence through the control connection.

Show a lightweight cursor overlay from the server's position updates. Do not imply every native cursor shape is reproduced in the first release. Keep capture and encoding behind an interface so Desktop Duplication or another video transport can be evaluated later if measured performance requires it.

### Display coordinates and scaling

Enumerate logical display bounds independently of brightness support using Windows display APIs, including [GetMonitorInfo](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getmonitorinfow).

Configure DPI awareness before display enumeration/capture/input initialization, including the capture worker, and use physical pixels throughout the native boundary. See [Windows DPI awareness initialization](https://learn.microsoft.com/en-us/windows/win32/hidpi/setting-the-default-dpi-awareness-for-a-process).

For direct input:

1. Convert the touch from page coordinates into the rendered image rectangle.
2. Remove letterboxing and reverse local zoom/pan; ignore touches outside the image.
3. Send normalized coordinates in the range 0 to 1 with the displayed frame/layout revision.
4. Map to native pixels using the selected display's bounds, including negative desktop origins.
5. Normalize against the entire virtual desktop for Windows absolute mouse injection.

Windows absolute mouse coordinates use 0 to 65535; `MOUSEEVENTF_VIRTUALDESK` selects the virtual desktop. See [MOUSEINPUT](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-mouseinput).

For touchpad motion, apply a bounded sensitivity to deltas, accumulate against the current cursor location, and clamp to the selected display. Display removal, rotation or layout changes invalidate the mapping, release dragging and require a fresh frame before further input.

### Native mouse and keyboard input

Use a dedicated Windows input adapter around `SendInput`. It supports pointer movement, button transitions, wheel input, special keys and atomic shortcut combinations. Check the returned inserted-event count and report partial/failing injection. A successful insertion means Windows accepted events; it does not prove the target application processed them. See [SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput).

Keep ordered clicks, text and key events; coalesce only superseded movement. Track the buttons/keys this app has injected and release its held state on cancellation or shutdown. Shortcut modifiers are pressed/released within each combination; a visual sticky modifier applies to the next key without leaving Windows keys held.

Input acts on the foreground application. The phone should tell the user to click the intended text field first. This is desktop control, not an application sandbox.

### Windows audio implementation

Use Windows Core Audio through a dedicated adapter/service, independently of capture, mouse/keyboard input and the DDC/CI worker gate:

- Enumerate both playback and recording endpoints, including unavailable states, using `IMMDeviceEnumerator.EnumAudioEndpoints`. Keep IDs tied to Windows endpoint identity rather than list order. See [audio endpoint enumeration](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immdeviceenumerator-enumaudioendpoints).
- Activate `IAudioEndpointVolume` on each available endpoint for master-level readback and mute; map its normalized scalar to the phone's 0–100% slider. See [endpoint volume controls](https://learn.microsoft.com/en-us/windows/win32/api/endpointvolume/nn-endpointvolume-iaudioendpointvolume) and [SetMasterVolumeLevelScalar](https://learn.microsoft.com/en-us/windows/win32/api/endpointvolume/nf-endpointvolume-iaudioendpointvolume-setmastervolumelevelscalar).
- Track endpoint changes/default-device changes using [IMMNotificationClient](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nn-mmdeviceapi-immnotificationclient), and level/mute changes using [IAudioEndpointVolumeCallback](https://learn.microsoft.com/en-us/windows/win32/api/endpointvolume/nn-endpointvolume-iaudioendpointvolumecallback).
- Own COM initialization, endpoint interfaces and callback lifetimes on a dedicated audio worker. Callbacks enqueue bounded state updates and return promptly; unregister callbacks and release interfaces on removal/shutdown. Prefer focused COM interop without an additional package unless implementation establishes a concrete need.
- Refresh state while the Audio tab is visible through a lightweight authenticated read, initially about once per second, and once on foreground/reconnect. Pause polling when hidden. Audio controls do not depend on the desktop WebSockets or a rendered frame.
- Serialize audio writes, coalesce rapid slider changes to the latest intent per endpoint and read back volume/mute after applying. Validate finite 0–100% values, explicit mute booleans and current device IDs. Never replay an audio command after a timeout or reconnect.

Windows master volume belongs to an endpoint. The all-playback-devices action therefore applies to a snapshot of active playback endpoints, with independent results for each; it is not an atomic system-wide transaction. Bind the request to the displayed endpoint IDs/catalog revision. A new device is not included automatically, and an endpoint disappearing mid-operation produces a partial result. If the default playback device changes before a PC volume request is applied, reject the stale target and refresh rather than changing a different device silently.

Enumerate every Windows audio endpoint, but expose write controls only where supported and available. A device-level Windows volume setting is not proof of audible output: exclusive-mode software bypass and separate physical amplifiers/knobs can affect results. See [Microsoft's endpoint volume behavior](https://learn.microsoft.com/en-us/windows/win32/coreaudio/endpoint-volume-controls). Record compatibility on the actual devices instead of promising control of hardware settings Windows does not expose.

## 5. On-screen keyboard

Offer two complementary ways to type:

- **App keyboard:** a compact QWERTY layout with letters, digits, punctuation, Space, Shift, Backspace, Enter and Tab. Include Esc, Delete, arrow keys, Home/End and a shortcut row such as Ctrl+C, Ctrl+V, Ctrl+A and Ctrl+Z. Ctrl+V uses the PC's existing clipboard; there is no phone-to-PC clipboard synchronization.
- **Phone keyboard:** focus a visible text field to open the native iPhone keyboard. Compose locally, then tap **Send text**. This makes autocorrect and composition predictable. Do not send intermediate IME text or infer PC edits from changes to the local field.

Transmit normal committed text as Unicode, with correctly ordered UTF-16 surrogate pairs for supplementary characters. Use explicit key operations for navigation, Backspace, Tab and Enter; normalize committed line breaks into Enter operations. The Windows Unicode input mechanism is described in [KEYBDINPUT](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-keybdinput).

Limit each text submission to 1024 UTF-16 code units, preserve valid surrogate boundaries, and reject oversized submissions with clear feedback. Keep text out of logs and browser storage. Disable duplicate sends while pending, then show Sent to Windows, Failed, or Outcome unknown. Preserve unsent local text only in memory. Never resend text automatically after an uncertain acknowledgement.

Unicode behavior varies by application; verify everyday editors and browser fields before claiming compatibility. Applications using raw/game input and elevated applications may reject these events.

## 6. API, authorization and session rules

Proposed endpoints, all under the existing protected `/api` prefix:

| Endpoint | Purpose |
| --- | --- |
| `GET /api/remote/displays` | Return capture-capable displays and the current remote permissions |
| `POST /api/remote/session` | Start a view-only session for the selected display |
| `POST /api/remote/session/configure` | Change display, viewing quality or control mode for the owned session |
| `POST /api/remote/session/stop` | End the owned remote session |
| WebSocket `/api/remote/frames` | Deliver frame envelopes and capture state |
| WebSocket `/api/remote/control` | Receive input, rendered-frame acknowledgements and heartbeats; return input/session status |
| `GET /api/audio/devices` | Return endpoint IDs, names, direction, availability, default roles, capabilities, volume/mute readback and catalog revision |
| `POST /api/audio/devices/{id}/volume` | Set one endpoint's master volume to an absolute 0–100% value |
| `POST /api/audio/devices/{id}/mute` | Set one endpoint's explicit mute/unmute state |
| `POST /api/audio/outputs/volume` | Apply an absolute volume to an explicit snapshot of active playback devices |
| `POST /api/audio/outputs/mute` | Apply mute/unmute to that playback-device snapshot |

All audio endpoints require a valid pairing session and the independent local audio permission; POSTs retain the existing Origin/custom-header protection. Use encoded endpoint identifiers or server-issued opaque IDs. Return confirmed Windows readback and per-device errors for bulk changes; never mark a partially successful operation as fully successful. Audio discovery/commands do not acquire the single remote-desktop-session lease. Logout, revocation or disabling audio permission rejects subsequent requests and cancels queued writes; it does not restore earlier volume settings.

Session creation returns an opaque remote-session ID bound to the existing pairing cookie. It is an identifier, not a replacement credential. Each socket must authenticate the cookie and bind to the matching session in its first message within a short deadline. Keep pairing codes, cookies and session IDs out of connection URLs and diagnostics.

Allow one active remote session per PC in the first release. A second browser/tab receives a busy response and cannot silently take over. Brightness-only paired phones can continue using their existing controls.

Security and lifecycle requirements:

- Keep Host, pairing, HttpOnly/SameSite cookies and JSON POST protections. Explicitly validate WebSocket Origin and cookie authentication before accepting the connection.
- Browser WebSockets cannot add the existing custom request header. Exempt only a verified WebSocket upgrade from that header requirement; validate its exact expected Origin separately. Do not weaken ordinary POST checks or treat Origin as authentication. See [ASP.NET Core WebSocket origin restrictions](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/websockets?view=aspnetcore-10.0#websocket-origin-restriction).
- Make CSP `connect-src` explicitly allow only the current HTTP and WebSocket origin, including the active port. Canvas rendering avoids requiring unrestricted image sources or inline scripts.
- Recheck pairing validity before input and frame delivery. Logout, Disconnect all phones and expiry cancel open sessions; add revocation notifications rather than relying only on the initial socket handshake. Pairing-code renewal continues preserving valid sessions.
- Bound complete received JSON messages, including fragmented messages, to 8 KiB; validate types, finite coordinates, key allowlists, protocol versions and display revisions. Apply per-session input rate and queue limits.
- Use monotonic command sequences and reject duplicates/outdated commands. Acknowledgements report insertion results without promising application-level success.
- Send an application heartbeat approximately every second; a three-second lease expiry releases held input and stops sharing. Socket failure and local revocation stop immediately. Returning from Safari's background requires an explicit new viewing/control start.
- Disable input when the visible frame is stale, initially after two seconds. The server also checks the acknowledged frame's age/revision so a connected input socket cannot control an obsolete display image.
- On permission changes, monitor switches, gesture cancellation, Stop, logout or app shutdown, release tracked inputs and discard queued commands. Never replay commands after reconnection.
- Reject Turn displays off while a remote session is active, on both the phone and server, with an instruction to stop sharing first. Brightness and explicit Wake remain available.

The existing HTTP transport is unencrypted. Adding screen and text traffic increases what is exposed on that network; document this in setup guidance and keep remote permissions explicit. Public port forwarding is outside the design.

## 7. Windows boundaries and failure handling

`SendInput` is limited by Windows integrity levels; a normally launched app cannot control higher-integrity applications. Do not automatically elevate the launcher. See [Microsoft's SendInput restrictions](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput#remarks).

Pause capture/input on Windows lock, secure desktop/UAC transitions, disconnected user sessions or an unavailable display. Check the active input desktop and Windows session state; [OpenInputDesktop](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-openinputdesktop) alone is insufficient to distinguish a disconnected session. Resume only through an explicit phone action after the normal desktop is available.

Protected video, HDR surfaces, some accelerated applications and displays that are powered off may render incorrectly or fail with the initial capture method. Show an unavailable/paused state rather than claiming the last frame is live. No Windows login, secure-desktop bypass or remote PC wake-from-sleep is planned.

Failure messages should distinguish permission disabled, another active session, expired pairing, stale frame, capture failure, unavailable/unsupported audio device, partial audio update and PC unreachable. Dispose images, native handles, COM interfaces, callback registrations, pipes and sockets on all exit paths. A hung capture worker must not block management, audio or ordinary monitor commands.

## 8. Implementation files

| File or area | Responsibility |
| --- | --- |
| `Remote/DesktopDisplayCatalog.cs` | Capture display identity, bounds, layout revisions and DPI handling |
| `Remote/DesktopCaptureWorker.cs` | Persistent Windows capture/encoding worker and bounded pipe protocol |
| `Remote/DesktopCaptureService.cs` | Worker ownership, frame scheduling and capture watchdog |
| `Remote/NativeInput.cs` | Validated mouse/keyboard injection and held-input cleanup |
| `Remote/RemoteSessionManager.cs` | Permissions, ownership, leases, freshness and revocation |
| `Remote/RemoteProtocol.cs`, `Remote/RemoteEndpoints.cs` | API records, bounded frame/input protocol and routes |
| Demo branches in `Remote/DesktopCaptureWorker.cs`, `Remote/NativeInput.cs`, `Remote/DesktopDisplayCatalog.cs` | Synthetic frames, display layout and simulated input without native calls |
| `Audio/AudioDeviceCatalog.cs`, `Audio/CoreAudioInterop.cs` | Windows endpoint discovery/identity, capability checks and Core Audio COM declarations |
| `Audio/AudioControlService.cs` (includes `AudioEndpoints`) | Audio-worker lifetime, notifications, bounded writes, readback and protected audio routes |
| `Audio/AudioDeviceCatalog.cs` (includes `AudioDemoProvider`) | Simulated output/input devices, volume/mute and device changes without touching PC audio |
| `Program.cs`, `PairingState.cs` | Service wiring, worker entry point, upgrade/CSP protection and revocation integration |
| `MonitorRemote.csproj` | Capture dependency and any resource/test-output exclusions |
| `ManagementProtocol.cs`, `LocalManagement.cs` | Remote/audio permissions and status, local permission changes and Stop commands |
| `desktop/MainForm.cs`, `desktop/ServerController.cs` | Three local permission switches, session indicator and tray Stop |
| `wwwroot/remote.js`, `wwwroot/remote.css` | Viewer, gestures, keyboard and remote-session state |
| `wwwroot/audio.js`, `wwwroot/audio.css` | PC/all-output/device volume cards, readback, partial-result handling and foreground refresh |
| Existing frontend files | Desktop/Audio tabs, script/style loading and connection/logout integration |
| Tests, `package.ps1`, notices and documentation | Meaningful regression coverage, fresh packaging and actual validation records |

Retain management protocol version 1 with optional additive reply fields and new command names. Older clients can ignore extra fields; the new phone capability response must handle an older server gracefully. New local permission commands use the existing authenticated pipe only.

The existing `App` folder is preserved. The new release is built into a fresh folder under `artifacts`.

## 9. Implementation sequence and acceptance

The approved implementation followed this sequence. Real PC/iPhone acceptance below remains to be performed; automated and browser checks use synthetic capture/input/audio.

1. Define display, frame, input, audio-device and session contracts; introduce injectable native/demo boundaries.
2. Add independent viewing, input and audio permissions, session ownership/revocation and visible PC status/Stop controls.
3. Implement display discovery, persistent capture worker and authenticated frame delivery.
4. Add the phone's view-only screen, display selection, quality and connection states.
5. Add mapped mouse input, touchpad/direct modes, click/scroll and reliable drag cleanup.
6. Add app/native keyboard entry, Unicode handling, special keys and shortcuts.
7. Integrate foreground/background recovery, stale-frame protection and display-power interactions.
8. Add Core Audio device discovery, PC/per-device volume and mute, all-output actions and Windows state synchronization.
9. Run relevant automated checks, test the real PC/iPhone, then produce a fresh Windows package and record results.

Automated verification during implementation:

- Existing server/desktop builds, frontend syntax, demo API/frontend checks and packaged launcher checks.
- Unauthorized and wrong-Origin WebSocket rejection; normal POST header checks remain enforced.
- Disabled permissions, single-owner conflicts, logout/revoke/expiry and renewal preserving sessions.
- Invalid/fragmented/oversized messages, queue limits, duplicate commands and stale display revisions.
- Coordinate mapping with letterboxing, zoom/pan, mixed display origins and DPI scaling.
- Input ordering, partial injection, surrogate pairs, cancelled drag and release on lease loss.
- Capture backpressure, worker timeout/crash, resource cleanup and no command replay.
- Audio permission/authentication, invalid values/IDs, output versus input separation, mute preservation, readback, latest slider intent and no write replay.
- Audio-device/default changes, unsupported controls, partial bulk results, stale catalog rejection, callback cleanup and independence from desktop sharing.

Demo mode must use synthetic display frames, a simulated input adapter and simulated audio devices/volume state. Automated tests must never capture the user's real desktop, move/type on it or change real speaker/microphone settings. Existing minimal DOM checks need extension or a focused browser harness for the new UI; they cannot establish actual Safari touch/keyboard behavior.

Manual acceptance on the intended Windows PC and iPhone Safari:

- Start viewing and see recognizable current screen content; switching displays shows the correct display.
- Tap/double-click/right-click, drag a window, scroll and position the pointer accurately in both modes.
- Test 100%, 125%, 150% and mixed monitor scaling where available, including a monitor left of the primary display and unplug/reconnect.
- Type letters, punctuation, multiline text and representative non-ASCII/emoji text into Notepad and browser fields; verify special keys and common shortcuts.
- Check portrait, landscape, native keyboard resizing, zoom, safe areas and accessible control labels.
- Switch apps/lock the phone, interrupt Wi-Fi, expire/revoke pairing and stop from the tray. Verify dragging is released, the image clears when sharing ends, and no input is resent.
- Verify Windows lock/UAC, capture failure and elevated applications show the documented limits.
- Adjust the default PC volume and every available playback endpoint separately; verify level/mute readback agrees with Windows and supported physical output.
- Apply volume/mute to all playback endpoints with different initial levels; verify Mixed state, per-device results and that microphone/input settings stay unchanged.
- Adjust supported microphone/input levels and mute individually; check USB/Bluetooth reconnect, disabled devices and default-device changes without assigning writes to a different endpoint.
- Change volume using Windows/hardware controls and verify the phone refreshes. Test rapid slider changes, loss of connectivity and partial failure without stale readback or replay.
- Confirm ordinary brightness/power flows still work and the packaged launcher cleans up its capture worker.
- Measure frame rate, capture-to-render delay, input responsiveness, bandwidth, CPU and memory during a representative session. Aim for ordinary desktop interaction with roughly 8 fps and typical visible feedback within 300 ms on the home network; these are tuning goals, not validated guarantees.

Completion requires recorded results for checks actually performed. A demo pass is not evidence of real capture quality, Safari gestures, Windows input compatibility, audio-device compatibility or network latency.

## 10. Access while away from home and later improvements

If "from afar" includes using mobile data or another location, add a separate network-access increment. Prefer an authenticated encrypted private-network connection or authenticated HTTPS/WSS gateway. Do not expose the existing HTTP listener directly through router port forwarding.

This increment must define reachable addresses, trust/authentication and encryption, then update the current private-IPv4 Host allowlist, exact-Origin logic, displayed URLs/QR, CSP and cookie transport handling as needed. Some private-network products use address ranges or hostnames the existing server rejects; installing one alone does not establish compatibility. No network accounts, tunnels, certificates, firewall or router settings will be configured as part of this proposal.

Later improvements can include hardware video capture/encoding, WebRTC transport, richer cursor shapes, multiple viewers or clipboard/file transfer. Choose these from measured needs after the basic viewing, touch and keyboard experience works.

## Implementation and verification record

The user approved implementation by saying "proceed" after requesting the plan and adding PC/device volume control. Screen viewing, touchpad/direct input, keyboard entry, independent audio controls, local permissions, Stop actions, authenticated streaming and cleanup are implemented.

- Server and checks build with zero warnings/errors.
- 42 remote/audio checks pass, including a sustained stream beyond WebSocket keepalive, fragmented binding, oversized-message rejection, permission/revocation/expiry, stale/duplicate input, Unicode and partial audio results.
- `tests/features.mjs` checks the real frontend scripts with a controlled DOM/transport: mapped touches, letterboxing, bounded scrolling, stale frames, uncertain text cleanup, latest volume intent, mute preservation, shared-device write serialization, failure notices and reconnect without replay.
- Phone-sized browser checks use synthetic frames and audio. Viewing, simulated clicks, Unicode text, navigation cleanup and partial all-output results were verified.
- A separate read-only Windows Core Audio check discovered 51 endpoints: 25 outputs, 26 inputs; 19 supported volume readback. No real volume or mute setting was changed.

The self-contained Windows x64 package is `artifacts/MonitorRemote-win-x64-20261003-021911-a87ae824`; its 21 launcher checks and 42 remote/audio checks pass. See `docs/VALIDATION.md` for results and remaining manual acceptance. No real screen capture or input injection, firewall/router change, public exposure or startup registration was performed during automated validation. Away-from-home access remains the separate extension in section 10.
