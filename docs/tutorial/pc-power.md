# Control PC power from your phone

Choose shutdown, restart or sleep from the phone's PC power tab. Run an action after a short confirmation countdown, after a timer, or at a specific date and time. Monitor Remote stores and executes the task on the PC; your phone can disconnect afterward.

## Set up and use it

Use the current Windows package, keep the PC awake and leave its server running. Pair your phone using the existing QR/code workflow. In the Windows app, turn on **PC shutdown, restart and sleep** under Phone access. This permission starts off after every server restart.

1. Open **PC power** on the phone and choose an action.
2. Choose **Now**, **After a timer**, or **Date and time**. A timer accepts hours/minutes from one minute to 365 days. Date/time uses the PC time zone shown beside the field, even when the phone is elsewhere.
3. Review the confirmation and accept. Now gives you ten seconds to cancel; scheduled actions use their selected deadline without another delay.
4. Read the saved task and countdown. Use **Cancel power task** on the phone, PC window or tray before dispatch. One PC-wide task can be pending at a time.

Sleep/shutdown ends phone access. Wake the PC manually to reconnect; display wake does not wake a sleeping PC. Shutdown/restart requests do not force programs closed, so unsaved work or Windows policy can block them. A requested or disconnected state does not prove the PC completed the action.

## Understand a saved task

Phone logout, expired pairing cookies and network loss retain accepted tasks. Explicitly revoking the submitting phone on the PC, disconnecting all phones, disabling PC power permission or cancelling the task prevents a pending action.

Closing the PC window to its tray keeps scheduling alive. Stopping the server or exiting the app stops execution. On next launch, a future saved task is paused: enable PC power permission and choose **Resume power task** locally to re-arm it. Enabling permission alone does not resume it. An already overdue saved task is marked missed.

No Windows scheduled task, wake timer or background service is installed. If the running PC independently sleeps, scheduling waits for resume. A deadline up to 60 seconds late may still run; anything later is missed. Timers follow the PC UTC clock, so clock corrections can shorten or extend them. A date/time schedule resolves to a fixed UTC instant; changing the time zone later only changes its display. Daylight-saving times that are skipped or repeated must be replaced with an unambiguous time.

## Locate the editable sources

- `Power/PcPowerService.cs` owns permission, scheduling, cancellation, revocation ordering and recovery. Its named constants define the ten-second warning, 60-second lateness allowance and duration bounds.
- `Power/PcPowerProtocol.cs` defines requests and task states; `Power/PcPowerEndpoints.cs` maps authenticated HTTP routes. `ManagementProtocol.cs` is the compact contract shared with the launcher.
- `Power/PcPowerStore.cs` persists the last tasks at `%LocalAppData%\MonitorRemote\pc-power.json`. It uses a separate lock and atomic replacement. Never edit this file while a server owns it.
- `Power/WindowsPcPower.cs` runs whitelisted native power calls in an owned worker. `Infrastructure/OwnedJob.cs` supplies shared child-process ownership. Demo mode exits before native power or privilege changes.
- `Remote/RemoteSessionManager.cs`, `Remote/NativeInput.cs` and `Audio/SoundStream.cs` release held input and restore temporary speaker mute before dispatch. Failed or timed-out cleanup prevents the action.
- `wwwroot/index.html`, `wwwroot/power.js`, `wwwroot/shell.css` and existing theme tokens own the phone controls. `desktop/MainWindow.xaml` and its code-behind own the native permission/status controls. Edit these sources, not generated package files.
- `tests/PowerChecks.cs`, `tests/power.mjs`, `tests/power-browser.mjs` and `desktop/LauncherChecks.cs` cover the feature. The implementation contract is in [the plan](../PC_POWER_IMPLEMENTATION.md).

## Make a representative change

To allow 15 seconds for immediate cancellation, change `WarningSeconds` from `10` to `15` in `Power/PcPowerService.cs`. This changes the immediate deadline and the final warning window. Update the corresponding immediate-deadline assertion in `tests/PowerChecks.cs`, the immediate timing/confirmation text in `wwwroot/power.js`, and the tray warning comparison in `desktop/MainWindow.xaml.cs` from ten to 15 seconds. Keep these presentation values consistent with the server policy.

Rebuild and run the checks below. Expected result: an immediate task remains pending for 15 seconds, can be cancelled before dispatch, and executes once in simulation afterward. Date/time tasks still dispatch at their requested deadline. This customization example describes the source changes; the shipped ten-second policy remains unchanged, and this example was not applied as part of validation.

For colors and typography, edit the existing semantic tokens in `wwwroot/tokens.css`; the new form inherits them. Change phone field layout in `wwwroot/shell.css`, and use WinUI theme resources and native controls in the launcher XAML. No new visual framework is required.

## Build and verify safely

Use Windows, the .NET 10 SDK and Node.js. The WinUI launcher requires an explicit Windows runtime architecture for its self-contained build.

```powershell
dotnet build tests/MonitorRemote.Checks.csproj
dotnet build desktop/MonitorRemote.Desktop.csproj -r win-x64
dotnet tests/bin/Debug/net10.0/MonitorRemote.Checks.dll
node tests/power.mjs
node tests/frontend.mjs
node tests/features.mjs
node tests/design-checks.mjs
node tests/smoke.mjs
powershell -ExecutionPolicy Bypass -File .\package.ps1
```

C# checks run with fake execution/demo mode and isolated task storage. Harmless blocking workers prove stop/crash ownership without calling power APIs. Existing server-launching checks also isolate their stores. Never use personal task storage for test servers.

For a manual simulated browser session, open a fresh PowerShell and set these variables before starting the console server:

```powershell
$env:MONITOR_DEMO = '1'
$env:MONITOR_PC_POWER_FILE = Join-Path $env:TEMP 'MonitorRemote-demo-pc-power.json'
$env:MONITOR_MUTE_MARKER = Join-Path $env:TEMP 'MonitorRemote-demo-mute-marker.txt'
$env:MONITOR_PORT = '19175'
dotnet run -- --allow-pc-power
```

Use a fresh demo-store filename for each independent run; an existing future task intentionally restores paused. With that server running, open another terminal and pass its printed pairing code to the browser check. Replace the placeholder with the actual code; choose a new capture directory.

```powershell
node tests/power-browser.mjs 19175 <pairing-code> artifacts/power-browser-review
```

This script requires installed Microsoft Edge, refuses to create tasks unless the server reports simulation, and captures portrait, dark/reduced-motion and landscape layouts. It creates/cancels a timer and waits for an immediate simulated restart. It does not establish physical iPhone/Safari support or actual Windows power behavior.

## Recover from an error

If acknowledgement is lost, refresh status. The phone retains the request ID for reconciliation and never resends it automatically. If no matching task appears, check the PC before explicitly dismissing the uncertain result. A dispatched task cannot promise cancellation or automatic retry.

Storage errors disable scheduling. Close competing servers and check file permissions. Preserve corrupt or unsupported-version JSON for diagnosis; restore a valid backup while all servers are stopped, or move the damaged file aside before starting with an empty store. Recovered future tasks still need local resumption.

If input cleanup fails, return Windows to its normal unlocked desktop. If mute restoration fails, stop listening and restore PC speaker mute locally; the existing mute marker remains for recovery. Sleep support and shutdown privilege depend on Windows policy and hardware. The app never requests elevation or substitutes a different power action.

## Validation and exports

See [the dated validation record](../VALIDATION.md#pc-power-implementation--2026-11-03) for actual results and remaining manual checks. Automated simulation, source/package builds and browser checks cannot verify physical shutdown, restart, sleep/resume, an unsaved application's veto, VoiceOver or the native Windows window's rendering.

This Markdown is authoritative. Regenerate its readable HTML companion after edits:

```powershell
node docs/tutorial/build-tutorial.mjs pc-power
```

Open `docs/tutorial/pc-power.html` in a browser. To make a PDF companion, use Print > Save as PDF; the export includes print margins and page-break rules. Browser-to-PDF printing is a manual workflow and is not automated by the build script.
