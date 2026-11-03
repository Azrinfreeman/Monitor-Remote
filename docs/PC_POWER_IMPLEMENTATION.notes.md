# PC power implementation notes

## Deviations

- The launcher needs `-r win-x64` for its self-contained WinUI build. The tutorial and current README commands include the architecture; the initial architecture-free desktop command fails before compilation.
- A sleep worker can remain inside Windows until resume. Its durable task stays `dispatching` and its job remains owned while waiting. The worker acknowledges startup separately; only a positive native return becomes `requested`. Stop/crash leaves an unconfirmed outcome with no replay.
- Power availability is an additive top-level `pcPower` flag in `/api/status`; full permission, task and sleep capability details come from `/api/pc-power`. Existing remote capability fields are preserved.
- The shared job implementation moved to `Infrastructure/OwnedJob.cs`; the desktop project links this file. There is no duplicate job implementation or new service dependency.
- The phone offers an explicit dismissal for an unconfirmed submission after the user checks the PC. Status refresh and reconnection never resend it. Definitive conflict/validation errors remain visible while state is refreshed.
- A review found incomplete held-input verification, unbounded mute recovery, command conflicts being treated as launcher disconnection, and implicit live announcements on countdown output. Those paths now check held inputs, honor cancellation, preserve management connectivity and disable per-second announcements. Focused regression checks cover cleanup and expected management errors.
- A second review found that denied mute-marker access could escape the hosted scheduler. It now fails the task and preserves the marker; an HTTP integration check with an isolated read-only marker confirms the server remains available and no simulated dispatch occurs.
- Collaborative browser portrait checks succeeded, but two landscape snapshot attempts timed out. Remaining visual acceptance uses the repository's Edge/CDP workflow in `tests/power-browser.mjs`. A resize-related optional view-transition rejection found there is handled without changing pane behavior.
- The tutorial has editable Markdown and a generated print-ready HTML companion. PDF export is a documented browser Print workflow; an automated PDF export was not run, and no PDF rendering is claimed.

## How the run ended

Source implementation, simulation checks, phone-browser visual acceptance, documentation and Windows x64 portable packaging are complete. The C# suite passes 94 power checks plus 79 remote/audio checks; the launcher passes 29 packaged checks. See `docs/VALIDATION.md` for exact evidence and remaining manual Windows/iPhone acceptance. No real shutdown, restart or sleep was requested during implementation.

Use `artifacts/MonitorRemote-PCPower-20261103-ready/MonitorRemote.Desktop.exe` for the new package. The checked-in editable sources and `docs/tutorial/pc-power.md` remain authoritative for maintenance; artifacts are generated outputs.
