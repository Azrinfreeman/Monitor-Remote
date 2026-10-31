# Unlock from the phone: spike protocol (phase 5b)

Status: **not run.** The code for phases 1-4 and 5a is in place; no privileged component exists and none should be written until this spike ends in a "go".

## What 5a already does

The server reports `locked` in its capabilities (Windows session not active, or the input desktop is not `Default`). The Desktop tab then says the PC is locked and points to waking the display from the Displays tab. Nothing types into the lock screen.

## Why a spike

The lock screen and UAC prompts run on the secure Winlogon desktop. The user-level server cannot capture or inject input there. Doing so needs a Windows service running as SYSTEM plus a helper in the console session that attaches to the Winlogon desktop. This weakens physical security, needs admin install, and I have not verified that it works on current Windows 11 builds, so it is tested before it is designed.

## Run on a throwaway test machine or VM, never the daily PC

Use a local test account with a throwaway password. Do not use a Microsoft account or a Windows Hello PIN you rely on.

1. Create a minimal service (outside this repo's shipping projects) that starts a helper with `CreateProcessAsUser` in the active console session using a SYSTEM token duplicated from `winlogon.exe`.
2. In the helper, call `OpenInputDesktop` / `SetThreadDesktop` for `Winlogon` while the PC is locked.
3. Record for each case below: works / partly / fails, plus what error Windows returned.

| # | Question | Pass condition |
|---|----------|----------------|
| 1 | Capture the lock screen with GDI `BitBlt` or Desktop Duplication from the helper | Image shows the lock screen or sign-in box |
| 2 | `SendInput` a password into the sign-in box | Account signs in |
| 3 | Same for a Windows Hello PIN | Account signs in |
| 4 | Capture and click through a UAC prompt (secure desktop) | Prompt visible and answerable |
| 5 | Behavior with Secure Boot, VBS/HVCI and Smart App Control on | Notes only |
| 6 | Behavior after sleep/wake and fast user switching | Notes only |
| 7 | What signing, if any, is needed for the helper to start (unsigned build) | Notes only |

## Go / no-go

Go only if cases 1 and 2 work reliably and the unsigned helper runs on an up-to-date Windows 11 install. Otherwise stop at 5a; it is a complete, safe feature on its own.

## If go: requirements for 5c (from the plan)

Separate optional installer; off by default with an explicit warning; HTTPS only; a separate unlock PIN with lockout (3 failures, then 15 minutes); the Windows password is held in memory only while injected and never logged or stored; the helper accepts commands only from the authenticated server over a locally authorized pipe and only while the session is locked; every attempt goes to the audit log; a clean uninstall. A threat review (stolen paired phone, replay, local malware on the pipe, shoulder-surfing) comes before any code.

## Record the result

Write the outcome and the table above into `docs/VALIDATION.md` under a dated heading, then update the decision in `docs/BEYOND_LAN_IMPLEMENTATION.md`.
