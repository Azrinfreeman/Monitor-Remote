# Monitor Remote implementation

Approved implementation: Windows .NET 10 server, mobile Safari UI, DDC/CI brightness control and Windows all-display off/wake requests. No external packages required.

## Design

- Serve UI and API on port 8765 over trusted home LAN HTTP.
- Six-digit pairing code expires after ten minutes. HttpOnly, SameSite cookies expire after twelve hours; logout or restart revokes access.
- Rate-limit pairing; validate private IP Host headers, same-origin POSTs and a custom request header. Do not expose monitor details before pairing.
- Run native commands in a separate process with an eight-second timeout; serialize requests and coalesce slider changes in the UI. Release physical monitor handles after every operation.
- Enumerate connected monitors on every command. Brightness uses hardware range conversion and readback. Power requests report unconfirmed state.

## Scope and remaining milestones

The implemented version uses a console launcher. Tray integration, QR code, locally trusted HTTPS and an automatic startup installer remain future packaging work. Per-monitor power is deliberately deferred until recoverable hardware support is established. Laptop panel brightness is not implemented.

## Validation

Build, JavaScript syntax and automated demo-mode HTTP checks cover authentication, input validation, origin rejection, session revocation and UI serving. Demo mode never changes physical monitors. Actual brightness, repeated off/wake, cable/dock compatibility, iPhone Safari and restart behavior require manual tests on the target setup. Do not claim hardware compatibility from demo tests.

References: https://learn.microsoft.com/en-us/windows/win32/api/_monitor/ and https://learn.microsoft.com/en-us/windows/win32/menurc/wm-syscommand
