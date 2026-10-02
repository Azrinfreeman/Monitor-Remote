# Monitor Remote

Use iPhone Safari to view a Windows PC screen, control its mouse with touch, type with an on-screen keyboard, and adjust PC and individual audio device volume on your trusted home network. Brightness and display off/wake controls are also available.

## Run the Windows app

Open the new package folder under `artifacts` and double-click `MonitorRemote.Desktop.exe`. If this README is inside a package, use the executable beside it. Keep the whole folder together, including its DLLs and `server` folder. The self-contained Windows x64 package requires no SDK or terminal. The existing workspace `App` folder contains the earlier brightness/power release.

The server starts automatically. Scan the PC app's QR with iPhone Camera and enter the six-digit pairing code in Safari within ten minutes. Keep the PC awake and the app running; Ethernet on the PC and Wi-Fi on the phone work on the same reachable network.

Enable the features you want in the PC app:

- **Allow screen viewing:** the phone's Desktop tab can list and view a selected display.
- **Allow mouse and keyboard control:** permits touch and typing after screen viewing is enabled. The phone separately selects Enable control.
- **Allow audio volume control:** enables Audio, independently of screen sharing.

These permissions start disabled after each server start/restart. The PC app shows sharing/control status. Stop remote session in the window or tray ends sharing and releases its held inputs. Removing a permission takes effect during existing connections.

Start, Stop and Restart manage the server. New pairing code preserves connected phones and local permissions; Disconnect all phones revokes their sessions. Closing the window hides it to the tray; Exit stops the app and its server/worker. Windows sign-in startup is opt-in. Disable it before moving/removing the folder; if the folder is gone, disable Monitor Remote under Windows Settings > Apps > Startup.

## Desktop and typing

Choose Desktop, refresh displays if needed, select a screen and quality, then Start viewing. Laptop displays can be viewed even when brightness is unsupported. Select Enable control to use touch and keyboard input. Stop viewing before switching displays or turning them off.

Touchpad mode: slide to move, tap to click, long press for right click, and use two fingers or the scroll strip to scroll. Hold Drag while moving on the touchpad. Left click, Double click and Right click buttons are available. Direct touch mode maps the viewed screen to the PC pointer; enable Drag, then touch and move. Pinch the screen to zoom, or use zoom/Fit screen controls.

Open Keyboard for letters, special keys and shortcuts. Click the intended PC text field first. The phone text field opens the native phone keyboard; Send text sends committed text once, including multiline/Unicode text, up to 1024 UTF-16 characters. Ctrl, Shift and Alt apply to the next applicable on-screen key. Check the target application for the result.

Input is disabled when the displayed frame is stale. Leaving Desktop, backgrounding/locking the phone, stopping, losing the connection or revoking pairing ends sharing. Start viewing/control again explicitly. Clicks and text are never replayed. If an acknowledgement is lost, input may already have reached Windows; inspect the target field before typing it again.

Windows must be unlocked on its normal desktop. Lock screens, UAC secure desktops and disconnected Windows sessions end sharing. Higher-privilege apps may reject input from this normally launched app. Protected video and some accelerated/HDR content may not capture correctly. This release targets ordinary desktop interaction; it does not stream PC sound, unlock Windows or wake a sleeping PC.

## PC and device volume

Audio shows **PC volume** for the default playback endpoint, plus individual cards for every Windows playback/input endpoint. Volume and mute are independent: adjusting volume preserves mute. Disabled, unplugged or unsupported devices are listed with unavailable controls.

**Apply to all**, **Mute all** and **Unmute all** target the currently listed active playback endpoints. Different output levels show Mixed; each operation reports individual success/failure. Microphones retain their settings. Change microphone/input level and mute on their own cards where Windows supports them. This controls endpoint master volume, including application sound sent to that endpoint; it does not provide a per-application mixer or channel-by-channel controls.

Windows/hardware volume changes refresh while Audio is visible. Refresh after device/default changes or an uncertain operation. Writes are tied to endpoint IDs and the current catalog; a default change cannot silently redirect a PC-volume write. Queued changes are discarded on failure/disconnection and never replayed on reconnect.

## Existing display controls

Enable DDC/CI in the monitor menu for brightness. Laptop internal screens and some docks/adapters may not support it. Brightness cards distinguish requested settings from readback; failures restore the last confirmed reading and offer explicit retry. Rapid edits settle on the latest selection. Monitor IDs follow the current physical enumeration; refresh after cabling changes.

Off/wake affects all displays, not PC power. Turning displays off is disabled while remote viewing is active. Wake resets the Windows display idle timer; keyboard/mouse recovery may still be needed. It does not unlock Windows or dismiss a screen saver. Requests time out after 12 seconds and may already have reached the PC; reconnect and inspect before retrying. Power state remains unconfirmed.

## Network and packaging

If Windows asks for firewall access, allow Private networks only. An administrator can add a narrowly scoped rule if needed; the app does not change the firewall:

```powershell
New-NetFirewallRule -DisplayName 'Monitor Remote Home LAN' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 8765 -Profile Private -RemoteAddress LocalSubnet
```

Use trusted home Wi-Fi. HTTP/WebSocket traffic, including screen images, typed text and pairing, is unencrypted. Do not forward the port on your router. Access from mobile data or another location requires a separate encrypted/private-network extension and is not included. Guest Wi-Fi isolation, VPNs and sleeping PCs can prevent connection. A router DHCP reservation keeps the PC URL stable. Set `MONITOR_PORT` before launch to change the port and adjust the rule accordingly.

Build a fresh portable package with:

```powershell
powershell -ExecutionPolicy Bypass -File .\package.ps1
```

Output is a new folder under `artifacts`; existing packages are preserved. Executables are unsigned. `-Runtime win-arm64` supports ARM64 packaging but has not been validated. `-FrameworkDependent` requires matching .NET 10 ASP.NET Core and Windows Desktop runtimes.

For development, install the .NET 10 SDK and run `dotnet run`. Feature permissions are disabled by default; to opt in for a console session:

```powershell
dotnet run -- --allow-viewing --allow-input --allow-audio
```

`desktop`, `Remote`, `Audio`, `wwwroot`, root C# files and `tests` are source. `docs` holds the implementation plan, guide and validation history. Build folders can be regenerated.

## Checks and real-device acceptance

```powershell
dotnet build tests/MonitorRemote.Checks.csproj
node tests/smoke.mjs
node tests/frontend.mjs
node tests/features.mjs
dotnet tests/bin/Debug/net10.0/MonitorRemote.Checks.dll
```

Automated remote/audio checks use synthetic frames, input and devices. They do not capture the real desktop, inject real mouse/keyboard input or change real audio. `--read-audio` on the checks executable separately enumerates actual Windows audio devices read only. The packaged launcher `--check` uses demo mode; set `MONITOR_CHECK_REPORT` to a report path before running it.

See `docs/VALIDATION.md` in the source workspace for performed checks. Test actual iPhone Safari gestures, portrait/landscape, native keyboard resizing, Wi-Fi loss, lock/UAC, display/DPI changes, real typing and speaker/microphone readback on your setup. ARM64, real Safari/native-hardware acceptance and measured latency remain unverified.
