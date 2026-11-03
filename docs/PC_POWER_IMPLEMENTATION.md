# PC shutdown, restart and sleep implementation plan

Add a separate PC power feature with immediate actions, a countdown timer, and a one-time date/time schedule. The PC server owns execution so an accepted task continues when the phone locks or disconnects. Existing display off/wake controls keep their current meaning.

Status: implementation authorized and delivered on 3 November 2026; simulated acceptance and packaging are recorded in [the notes](PC_POWER_IMPLEMENTATION.notes.md). Physical power transitions and iPhone/native-window acceptance remain manual. This file is the authoritative design contract; checkboxes below distinguish implemented work from remaining acceptance.

## Contents

- [Scope and proposed defaults](#scope-and-proposed-defaults)
- [Current code and integration points](#current-code-and-integration-points)
- [Phone and PC workflows](#phone-and-pc-workflows)
- [Time, lifecycle and cancellation rules](#time-lifecycle-and-cancellation-rules)
- [Implementation contracts](#implementation-contracts)
- [Delivery steps](#delivery-steps)
- [Verification and acceptance](#verification-and-acceptance)
- [Recovery and handoff](#recovery-and-handoff)

## Scope and proposed defaults

The user requested shutdown, restart and sleep from a phone, either immediately, after a duration, or at a specific date and time. Interpret “window” as the Windows PC. Closing an individual application window is outside this plan.

The following implementation defaults were proposed with this plan and retained when the user instructed implementation to proceed.

| Decision | Proposed behavior | Reason / when to reconsider |
| --- | --- | --- |
| Supported actions | Shut down PC, restart PC, sleep PC | These are the requested actions. Hibernate, sign out, recurrence and Wake-on-LAN need separate scope. |
| Active task count | One pending PC power task per PC, shared across paired phones | Prevent conflicting actions. Reconsider if a queue of multiple tasks is required. |
| Immediate action | Confirmation, then a cancellable 10-second countdown | Gives phone and PC users a chance to cancel. |
| Duration | Integer seconds, from 60 seconds to 365 days; phone offers hours/minutes | Bound validation without imposing a short session-lifetime limit. |
| Date and time | Future instant, at least 60 seconds and at most 365 days ahead, interpreted in the PC time zone | Prevent phone/PC time-zone differences from moving the task. |
| Scheduling engine | In-process ASP.NET Core background service with a small local JSON store | Reuses the current app lifecycle; no service installation, database or Windows Task Scheduler dependency. |
| App availability | PC must be awake and server running at execution time | Running after app exit, while logged out, or waking the PC would require a different scheduler and permission model. |
| Force-close programs | Disabled | Unsaved work can block shutdown/restart; report a request rather than promising completion. |

An ordinary sleep request depends on Windows and hardware support. Do not silently substitute hibernation or shutdown. The native API and supported sleep-state behavior must be checked during implementation on the target Windows versions.

## Current code and integration points

These paths were inspected in this checkout. Older documentation is historical where it conflicts with source: the launcher currently uses WinUI 3, not Windows Forms.

| Existing source | Relevant behavior and planned change |
| --- | --- |
| [`../Program.cs`](../Program.cs) | .NET 10 Minimal APIs, pairing middleware, same-origin/custom-header protection, worker dispatch, `/api/status`, application lifetime. Register PC power services and routes here. |
| [`../Hardware.cs`](../Hardware.cs) and `/api/displays/power` in `Program.cs` | Existing display operations. Keep them separate from system power and preserve their behavior. |
| [`../PairingState.cs`](../PairingState.cs) | Cookie sessions, session IDs, explicit revocation and logout. Add a narrow way to obtain a validated session's public ID and a separate PC-revocation notification by public ID. Existing `Revoked` also fires for logout, so using it unchanged would contradict the retention rules below. |
| [`../Remote/RemoteSessionManager.cs`](../Remote/RemoteSessionManager.cs) | Local permission switches, capabilities, held-input cleanup and `remote:stop`. Coordinate cleanup before native power dispatch; avoid putting the scheduler in this class. |
| [`../Audio/SoundStream.cs`](../Audio/SoundStream.cs) | Sound streaming and temporary speaker-mute restoration. Await bounded stream/mute cleanup before dispatch so a failed/aborted power request does not strand the mute state. |
| [`../ManagementProtocol.cs`](../ManagementProtocol.cs), [`../LocalManagement.cs`](../LocalManagement.cs) | Authenticated, current-user-only named pipe. Extend replies with power permission and a compact task summary; add local cancellation/resume commands. Keep pipe `shutdown` meaning “stop server.” |
| [`../desktop/MainWindow.xaml`](../desktop/MainWindow.xaml), [`../desktop/MainWindow.xaml.cs`](../desktop/MainWindow.xaml.cs), [`../desktop/Tray.cs`](../desktop/Tray.cs) | WinUI permission toggles and native tray controls. Add a separate permission, current-task summary and cancellation. |
| [`../desktop/ServerController.cs`](../desktop/ServerController.cs), [`../Infrastructure/OwnedJob.cs`](../Infrastructure/OwnedJob.cs) | Launcher owns and stops the server and its children. Closing to tray keeps scheduling alive; Exit or Stop ends execution availability. Shared job source moved out of desktop during implementation. |
| [`../wwwroot/index.html`](../wwwroot/index.html), [`../wwwroot/features.js`](../wwwroot/features.js), [`../wwwroot/ui.js`](../wwwroot/ui.js), [`../wwwroot/app.js`](../wwwroot/app.js) | Three phone tabs, shared API/connection hooks and presentation layer. Add a fourth “PC power” tab and wire every tab list/indicator/connection hook. Shared `monitorApp.api` currently supports GET and POST. |
| [`../wwwroot/tokens.css`](../wwwroot/tokens.css), [`../wwwroot/components.css`](../wwwroot/components.css), [`../wwwroot/shell.css`](../wwwroot/shell.css) | Existing theme, grouped controls and bottom navigation. Reuse these styles; verify four tabs fit narrow phones. |
| [`../tests/RemoteChecks.cs`](../tests/RemoteChecks.cs), [`../tests/features.mjs`](../tests/features.mjs), [`../desktop/LauncherChecks.cs`](../desktop/LauncherChecks.cs) | Existing dependency-light C# and Node demo checks. Extend these conventions with fake clocks and a fake native power executor. |

Added files are `Power/PcPowerProtocol.cs`, `Power/PcPowerService.cs`, `Power/PcPowerStore.cs`, `Power/WindowsPcPower.cs`, `Power/PcPowerEndpoints.cs`, `wwwroot/power.js`, `tests/PowerChecks.cs`, `tests/power.mjs` and `tests/power-browser.mjs`. The reusable job source moved from `desktop/OwnedJob.cs` to `Infrastructure/OwnedJob.cs`, linked by `desktop/MonitorRemote.Desktop.csproj`. The server project includes nested C# files and excludes desktop/tests sources. The test project remains one executable; its entry point calls the added power checks.

## Phone and PC workflows

### Phone

The PC power tab shows whether the PC permits the feature, followed by an action selector and timing choices: “Now,” “After a timer,” or “At a date and time.” Use native inputs with explicit labels. The date/time field is labeled with the PC time zone; do not parse its value using the phone's local time zone.

Before submission, a confirmation identifies the PC, selected action, effective execution time and time zone. It explains that phone access ends when the PC sleeps or shuts down, that unsaved work may block shutdown/restart, and that the app must remain running. For a timer, the server calculates the final due time when it accepts the request; render that returned time rather than promising an exact client-side estimate.

After acceptance, show the active action, due date/time, remaining time, state and “Cancel task.” The browser countdown is a display only. Refresh status while the tab is visible and on reconnect/foreground; never use a browser timeout to trigger the PC action. Do not announce every second to screen readers; announce acceptance, the final warning, cancellation and errors.

A second task receives a conflict response with the existing summary. Tell the user to cancel the current task before creating another. Version one does not include editing or automatic replacement.

### PC launcher and tray

Add “Allow PC shutdown, restart and sleep,” disabled at every server start. It is independent of screen viewing, input and audio. Add a compact active-task summary and “Cancel power task” in the window and tray. Keep “Stop remote session” scoped to viewing/input/sound; the power cancellation control is explicit.

Restored tasks show “Paused after server restart” with their original due time. A separate local “Resume power task” action requires power permission to be on and the due time still in the future. Turning the permission on alone does not silently resume a saved task.

Use the existing tray notification facility for the final 10-second warning when the launcher is connected. Cancellation must remain available in the window even if Windows suppresses the notification. Console mode prints the warning and exposes the same phone cancellation API.

## Time, lifecycle and cancellation rules

### The PC clock determines execution

For a timer, calculate `dueAtUtc = serverNowUtc + delaySeconds`. For a date/time schedule, the server interprets the submitted local date/time in `TimeZoneInfo.Local`, rejecting nonexistent daylight-saving times and ambiguous times rather than choosing an offset silently. Return UTC due time, PC-local display time, UTC offset, time-zone ID and current server time. Include the time-zone ID in creation requests; reject with `409` if the PC time zone changed since the form loaded.

Store the resolved UTC instant. Later time-zone changes affect display only. This first version uses the PC's UTC wall clock for both modes: manual/system clock changes can shorten or extend a timer. State this limitation in help text and test clock jumps; a duration that must ignore clock adjustments would need a separately validated elapsed-time clock design.

Check the next deadline at least once per second with an injectable `TimeProvider`. The final warning begins at `dueAtUtc - 10 seconds`; dispatch remains at the requested due time. If created or resumed within the warning window, warn immediately for the remaining time. Never add another 10 seconds after a scheduled deadline.

Dispatch when due only if lateness is at most 60 seconds. If the PC resumes or the service wakes more than 60 seconds late, mark the task `missed` and require a new task. Within that allowance, run once and report actual dispatch time. This is best-effort scheduling, not a real-time guarantee. Backward clock changes defer the deadline; a forward jump beyond the allowance produces `missed` rather than an unexpected late shutdown.

### Lifecycle matrix

| Event | Pending task behavior |
| --- | --- |
| Phone screen locks, tab closes, Wi-Fi drops or phone logs out | Task remains accepted on the PC. Warn on explicit logout if a task is pending. |
| Pairing cookie naturally expires | Task remains; accepted scheduling does not require a phone heartbeat or a still-valid original cookie. Re-pair to view/cancel it. |
| Pairing code renewed without revocation | Task remains. |
| PC explicitly revokes the submitting phone | Cancel its pending task, using its stored public session ID. Do not depend on the token still being valid. |
| PC disconnects all phones | Cancel any pending task. |
| PC power permission turned off | Atomically cancel the pending task, including a restored paused task. |
| Launcher window closes to tray | Task continues while server and PC remain available. |
| Server stops/restarts, launcher exits or crashes | No execution while stopped. Persist the task; on next launch restore it paused, with power permission off. |
| Server starts after the original deadline | Mark the saved task missed; never execute it on startup. |
| PC sleeps independently while server remains alive | No wake timer is installed. On resume apply the 60-second lateness rule. |
| A PC power action was already dispatched before a crash/restart | Restore its outcome as unconfirmed; never dispatch it again automatically. |

Any paired phone may inspect and cancel the PC-wide task; the trusted-LAN app already treats paired phones as controllers of this PC. PC-local cancellation works regardless of power permission. Cancellation and dispatch share one serialization boundary: if cancellation wins, native execution cannot start; if dispatch wins, return `409` with “Already sent to Windows; cancellation is no longer available.” Do not promise to reverse a Windows power transition.

Task admission, explicit PC revocation and final dispatch must also share that boundary. Introduce one shared coordinator gate, supplied to `PairingState` and `PcPowerService`. Creation validates the cookie and resolves its public session ID inside this gate before persisting the task. `RevokeSession` and revoke-all acquire it before changing pairing state and publish their new public-ID notification before releasing it. Invoke notifications outside the internal pairing lock; always acquire the coordinator before that lock, and never await cleanup while holding either. Record explicit revoked IDs in the power service for the current server lifetime and cancel any matching task under the gate. Thus revocation either invalidates admission first or cancels the admitted task before dispatch. Natural expiry and logout do not add to this explicit-revocation set, preserving committed tasks. Do not substitute a cookie-validity check at execution time.

## Implementation contracts

### Services, state and persistence

`PcPowerService` owns permission, one current task, idempotency and all task transitions. Run its scheduler using ASP.NET Core hosted-service lifetime. HTTP handlers and the named pipe call this same service; they must not create independent timers. Use a narrow executor boundary so fake execution cannot reach Windows.

`PcPowerProtocol.cs` defines the action whitelist (`shutdown`, `restart`, `sleep`), modes (`now`, `delay`, `at`), validated request/response types and task states. Keep time values, bounds and warning/lateness constants named and documented.

Task fields: server-generated task ID, client request ID, original public session ID, action, mode, creation time, resolved UTC deadline, submitted local date/time and zone where relevant, revision, state, dispatch time and sanitized last error. No session cookies, pairing codes or pipe secrets enter task storage or audit messages.

Main transitions are `pending -> dispatching -> requested | failed | unconfirmed`, `pending -> cancelled | missed`, and restored `pending -> paused -> pending | cancelled | missed`. Terminal tasks do not execute again. `requested` means the API accepted a request, not that the PC verifiably reached the intended state. Demo execution returns an explicit simulated outcome.

`PcPowerStore` writes schema-versioned JSON at `%LocalAppData%\MonitorRemote\pc-power.json`, outside the installation folder. Allow `MONITOR_PC_POWER_FILE` to override the path for isolated test fixtures. Use atomic replace in the same directory and bounded history of the last 20 terminal tasks. Acknowledging create/cancel requires successful persistence. Persist `dispatching` before invoking Windows; a crash in that gap may skip a task but cannot cause automatic duplicate execution. A persistence failure prevents dispatch and surfaces an actionable error. Unknown schema, corrupt data or a competing server using the same store disables power scheduling until resolved; preserve the file for diagnosis and do not auto-reset it. Hold a per-store exclusive lock for the service lifetime.

### HTTP and local management

All new routes inherit existing pairing, host/network and same-origin protections. No mutation uses GET, no endpoint accepts shell text, executable paths or arbitrary command arguments. Recheck permission and task state at the dispatch boundary. Apply power permission to creation/dispatch, while authenticated status and cancellation remain available when it is off. Add bounded mutation throttling per session; cancellation must remain usable during create-request throttling. Console mode may opt in with a new `--allow-pc-power` flag, stripped from host configuration just like the existing permission flags; it never implicitly resumes a restored task.

| Route | Contract |
| --- | --- |
| `GET /api/pc-power` | Capability/permission, PC name/time/zone, current task and bounded terminal history. Does not expose the original session token or other private pairing metadata. |
| `POST /api/pc-power/tasks` | Accept exactly one mode-specific payload; return `202` with persisted task and server clock. Require a UUID `requestId`. |
| `POST /api/pc-power/tasks/{id}/cancel` | Cancel the matching task with its expected revision. Return the final state; repeated cancellation of the same cancelled task is harmless. |

Creation payloads:

```json
{"requestId":"<client-generated UUID>","action":"shutdown","mode":"now"}
{"requestId":"<client-generated UUID>","action":"restart","mode":"delay","delaySeconds":1800}
{"requestId":"<client-generated UUID>","action":"sleep","mode":"at","localDateTime":"2026-11-04T23:30:00","timeZoneId":"<PC time-zone ID returned by GET>"}
```

These are separate payload examples, not one JSON document. Reject unexpected/mixed timing fields, unknown actions/modes, malformed UUIDs/dates, out-of-range durations and past dates. Return `400` for invalid values, `401` for pairing failure, `403` for disabled power permission, `409` for conflicts/stale revisions/time-zone changes, `429` for throttling and `503` for unavailable storage/executor. Capability responses distinguish unsupported sleep from disabled permission.

Within the current task and retained history, repeating a request ID with the same normalized payload returns its existing outcome; a different payload returns `409`. Scope IDs to the PC so concurrent phones cannot duplicate a submission. Retain a pending submission's request ID in session storage before POST; if acknowledgement is lost, refresh status and reconcile that ID. Never automatically resend or mint a new request on reconnect. After history retention ends, that ID is no longer a deduplication guarantee; the UI does not retry old submissions.

Extend `monitorApp.api` compatibly: failures remain `Error` objects with the existing `.message`, and gain `.status` and a parsed `.details` object when JSON is available. Power errors include a stable `code` and optional current-task summary, using codes such as `task_conflict`, `stale_revision` and `time_zone_changed`. `power.js` reads those fields and performs a fresh GET after a conflict to reconcile current state; malformed/non-JSON errors retain the existing generic message. Test both the structured power path and unchanged error handling in older features.

Extend `/api/status` capabilities with PC power availability/permission. Extend `ManagementReply` with optional fields for permission and a compact current-task summary. Keep protocol version 1 only for additive compatible fields. Add distinct commands `pc-power:on`, `pc-power:off`, `pc-power:cancel:<id>:<revision>` and `pc-power:resume:<id>:<revision>`. Compose status in `LocalManagement` without moving scheduler ownership into `RemoteSessionManager`; keep serialized messages within the existing 4096-byte read limit.

### Windows execution

`WindowsPcPower` uses a dedicated `--pc-power-worker` branch in `Program.cs`, following the current owned-child-process pattern. It accepts only validated action values. It must honor `MONITOR_DEMO=1` before privilege adjustment or native calls, with an injected fake used by checks as a second barrier.

For shutdown/restart, use documented Windows power APIs such as `ExitWindowsEx` with shutdown privilege enabled in the worker token and the appropriate shutdown/reboot flag. Do not set force-close flags. Check privilege assignment and Win32 errors explicitly. For sleep, validate support and use `SetSuspendState` with hibernation disabled and wake events left enabled; verify actual Windows behavior rather than using the unreliable `rundll32` shortcut. No elevation prompt or persistent administrator service is introduced. Denied policy/privilege produces a specific failure.

Before dispatch, block new input/viewing/sound session starts, end existing remote activity, release held input and finish bounded mute restoration. Add an awaitable stop-and-cleanup operation to `SoundStreamService`: its existing `Stop()` only requests cancellation and `Unmute()` swallows failures, so neither alone establishes cleanup success. If cleanup cannot complete safely, fail the task without executing it. Recheck cancellation/permission immediately before committing `dispatching` and starting the worker. Release the transition guard when the worker completes, or on bounded dispatch failure/return from sleep; do not leave unrelated controls permanently blocked. A paused task or still-running power worker also occupies the single active slot.

After asynchronous cleanup, reacquire the coordinator gate and read the clock again alongside permission, explicit-revocation and task-revision checks. If the clock moved before the deadline, leave the task pending, release the transition guard and re-arm the deadline. If lateness now exceeds 60 seconds, persist `missed` and release the guard. Only a still-due eligible task may commit `dispatching`. Include clock changes and suspend/resume while cleanup is deliberately blocked in fake-clock checks.

Sleep calls may not return until resume, and shutdown may disconnect the phone before any acknowledgement. Do not reuse the monitor worker's eight-second timeout as proof of power failure. Bound the dispatch handshake separately, store an unconfirmed outcome on ambiguous worker exit/timeout, and never retry. Keep cancellation cutoff at the durable dispatch transition. A worker left running through sleep must be reaped after resume or app exit, under the existing Windows job ownership. Surface `requested` only after a positive native return; otherwise use `unconfirmed` until a late positive/negative result can refine it. No state claims to confirm physical shutdown or sleep.

The existing job belongs to the launcher and is absent during direct console startup. Give console-mode power workers a server-owned Windows job with kill-on-close semantics. Extract only the reusable native job-handle code from `desktop/OwnedJob.cs` into shared source linked by the desktop project; preserve the launcher's existing behavior. Hold the worker behind a release gate until job assignment succeeds, as `ServerController` already does for the server. On graceful stop cancel/reap outstanding workers; on server crash closing its job handle kills them. If a suitable job cannot be assigned, reject dispatch. Verify normal stop and forced server termination with a harmless blocking worker in both console and launcher modes; none of these ownership checks invokes native power.

## Delivery steps

Implementation is authorized. Completed source and automated work is checked below; manual acceptance remains unchecked. Deviations and validation are in `PC_POWER_IMPLEMENTATION.notes.md` beside this file.

### 1. Prove one immediate shutdown through the real app path in demo mode

- [x] Add the power protocol/service/executor boundary, default-off permission, create/status/cancel routes and additive local management fields in the files above.
- [x] Add the phone PC power tab, confirmation, pending countdown and cancel; add the WinUI permission toggle and local cancellation. Use fake execution for automated acceptance.
- [x] Include task persistence and request-ID reconciliation so an acknowledged task is durable and duplicate submissions reconcile.
- [x] Verify pairing, permission, confirmation, acceptance, cancellation and simulated dispatch. Confirm unpaired/permission-off requests never reach the executor and existing display off/wake is unchanged.

### 2. Add restart, sleep and both scheduling modes

- [x] Extend the same path to all three actions and timing modes in `Power/` and `wwwroot/power.js`. Add native labeled duration/date/time inputs and server-returned PC time-zone summaries.
- [x] Implement scheduler, final warning, shared-task conflicts, missed deadlines, explicit-revocation cancellation and restart pause/resume according to the lifecycle matrix.
- [x] Add fake-clock coverage for due boundaries, clock jumps, DST, reconnection, concurrency and storage recovery. A saved task runs once without any connected browser; cancelled/missed/paused tasks never dispatch.

### 3. Connect Windows power execution and complete launcher behavior

- [x] Implement the native worker, privilege/error reporting, sleep capability detection, bounded cleanup and transition guard. Demo execution bypasses native power.
- [x] Complete WinUI task status/resume, tray warning/cancel and server-stop/exit messaging. Launcher checks cover default-off and restored-paused behavior.
- [x] Verify worker ownership, ambiguous outcomes and cleanup in simulation, including blocked/denied mute recovery and failed input release.
- [ ] Validate real shutdown/restart/sleep during a separately authorized manual session with saved work; record requested versus observed results separately.

### 4. Document, package and verify human maintenance

- [x] Update README, developer guide, validation and project-state documentation while retaining historical records.
- [x] Add `docs/tutorial/pc-power.md` and its generated HTML companion. Inspect the export and distinguish documented customization from changes actually applied.
- [x] Build a fresh portable package and verify included web assets and isolated clean-store startup with power permission off. Installer/service registrations are unchanged.
- [x] Review editable source ownership and verify tutorial paths and build commands against the implementation. The documented 15-second customization was not applied or manually exercised.
- [ ] Complete physical iPhone/Safari/VoiceOver and native Windows-window visual acceptance; these remain separate from synthetic browser and launcher checks.

## Verification and acceptance

These are the acceptance commands; dated results are in `docs/VALIDATION.md`. No real power action belongs in automated checks.

```powershell
dotnet build MonitorRemote.csproj
dotnet build desktop/MonitorRemote.Desktop.csproj -r win-x64
dotnet build tests/MonitorRemote.Checks.csproj
dotnet tests/bin/Debug/net10.0/MonitorRemote.Checks.dll
node --check wwwroot/power.js
node tests/power.mjs
node tests/smoke.mjs
node tests/frontend.mjs
node tests/features.mjs
node tests/design-checks.mjs
```

The existing C# entry point sets `MONITOR_DEMO=1`; preserve this and ensure all spawned fixtures inherit it. Launcher `--check` uses its existing packaged demo workflow with `MONITOR_CHECK_REPORT` pointing to a writable report file. Every server-launching fixture, including existing `tests/RemoteChecks.cs`, `tests/smoke.mjs`, `desktop/LauncherChecks.cs` and any other fixture found during implementation, must set `MONITOR_PC_POWER_FILE` to its own temporary directory before server startup. New power checks do the same. Demo mode alone is insufficient isolation because startup may restore or mark a task missed. Clean up only fixture-owned directories. In an isolation test, use a stand-in personal-store sentinel under a separate temporary local-data root and assert it remains byte-for-byte unchanged; never create a sentinel in the actual user's store.

| Acceptance area | Required evidence / expected result |
| --- | --- |
| Actions and timing | Each of three actions works in each mode with a fake executor; exactly one dispatch at the deadline, within the allowed scheduling tolerance. |
| Security | Missing pairing, wrong origin/header, disabled permission and invalid actions cannot create or execute a task. Deterministically pause creation before admission, revoke, then resume: no task is accepted. Reverse the ordering: the admitted task is cancelled before dispatch. Explicit revoke/off wins any pre-dispatch race. No arbitrary process arguments. |
| Time validation | Past/out-of-range/mixed fields rejected; PC and phone in different zones agree on UTC; DST gap/overlap rejected; PC-zone change requires refresh. |
| Background behavior | Lock/close phone and disconnect network; PC executes accepted task without a browser timer. Reconnect reads state without mutation replay. |
| Idempotency and conflicts | Duplicate ID/same payload yields one task; changed payload/stale revision/second pending task conflicts. Lost POST acknowledgement reconciles by ID. |
| Cancellation races | Phone/local cancellation before dispatch prevents every native call; after dispatch returns an honest conflict. Throttling cannot prevent cancellation. |
| Recovery | Restart restores future pending task paused; local resume required; expired tasks missed. Corrupt/locked/unwritable store prevents acceptance/dispatch. Crash after dispatch commit never replays. |
| Sleep and clocks | Resume within/beyond 60 seconds follows the matrix; date/time warning and dispatch are not delayed by a second grace period. Move the clock backward/forward while cleanup is blocked: defer or mark missed at the final gate, with no native call. |
| Session lifetime | Task beyond cookie expiry still runs; logout warns but retains; targeted PC revocation cancels only the submitting session's task; revoke-all cancels. |
| Integration | Existing brightness/display power/audio/viewing checks remain green and all server fixtures use isolated power storage. Held inputs and temporary audio mute are cleaned before dispatch; start attempts cannot race cleanup. Structured conflict errors preserve existing `.message` handling. Console and launcher stop/crash leave no harmless test worker running. |
| UI | Real phone-browser preview at narrow portrait and landscape sizes: four tabs, usable date/time picker, confirmation, pending/cancel and errors; keyboard focus, VoiceOver labels, light/dark and reduced motion. Record simulator versus physical iPhone checks honestly. |
| Windows/manual | With explicit later authorization, observe real sleep/resume, shutdown and restart; save work first. Exercise policy denial, unsupported sleep and an unsaved app that may veto shutdown. Phone disconnect is never reported as proof of success. |

Windows APIs, privilege behavior, sleep support, real-device UI and actual power transitions remain unverified at the planning stage. Check current Microsoft API documentation and run the manual acceptance on supported target Windows versions during implementation.

## Recovery and handoff

Roll back by disabling power permission and cancelling the pending task before replacing the app. Preserve the JSON file for diagnosis, but older builds have no PC power reader and must not execute it. Reinstalling/upgrading does not arm saved tasks. No OS scheduled jobs, wake timers or service registrations need removal.

An executor should stop and report if satisfying scheduling requires execution after app exit, wake-from-sleep, force-closing applications, automatic elevation or a persistent Windows service: those reverse this plan's scope decisions. Ordinary code adjustments stay focused and are recorded under `## Deviations` in the implementation notes. Finish those notes with `## How the run ended`, naming completed acceptance evidence or the specific remaining blocker.

The feature is complete only when automated simulated paths pass, phone/local configuration and maintenance instructions match the source, and the real Windows/phone acceptance above has either passed or is clearly reported as pending. Writing this plan does not complete any implementation step.
