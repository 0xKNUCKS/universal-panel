# Security notes

Low-cost hardening applied so far, plus rules for upcoming modules. Hardening must not reduce
functionality; where a trade-off exists it is written down here.

## Applied

| Where | What | Why |
|---|---|---|
| `App.HardenDllSearchPath` | `SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS)` at the top of `Main` | Removes the current directory and `PATH` from native DLL search order. Blocks DLL planting via a malicious DLL next to a file/folder we touch, or in a user-writable `PATH` entry. App dir and System32 are still searched. |
| `SingleInstance` | Pipe created with `PipeOptions.CurrentUserOnly` (server and client); mutex in `Local\` | Other users / sessions can't connect to or squat the pipe. The server never reads from the pipe, so a connection can only trigger "show panel". |
| `HotkeyService` | `RegisterHotKey` (via NHotkey), never a low-level keyboard hook | We never see keystrokes, and hooks are an AV/EDR red flag. |
| `App.OpenLogFolder` | Absolute path to `%WINDIR%\explorer.exe`, `UseShellExecute=false`, argument via `ArgumentList` | No `PATH` lookup, no shell parsing/quoting injection. |
| `Log` | Logs under `%LOCALAPPDATA%\UniversalPanel\logs` (user-only ACL by default), bounded queue, per-day size cap, 7-day retention | A crash loop can't fill the disk; logs aren't readable by other users. |
| `app.manifest` | `asInvoker` (no elevation yet), x64 only | Least privilege until a module actually needs admin. |
| `ProcessControl` | Each action opens the process with only the right it needs, then checks the creation time still matches the sampled row before acting | A PID reused between sampling and the click can't redirect terminate/suspend to a different process. |
| `ProcessGuard` | Critical processes (System Informer's list, System PID, break-on-termination) get an explicit warning; the panel refuses to suspend itself | Prevents one-click bugchecks, logoffs or a panel that can never resume. Not a restriction: the action still runs after confirming. |

## Rules for upcoming modules

- **Launching processes** (launcher, Python runner): always absolute executable paths and
  `ProcessStartInfo.ArgumentList`; never build a command line string, never go through `cmd /c`.
  `UseShellExecute=true` only for opening documents/folders the user picked.
- **Elevation**: when the panel runs elevated, launched tools must default to *unelevated* (see
  PITFALLS.md, Elevation). Running a tool as admin is an explicit per-tool choice.
- **Process actions**: kill/suspend on critical system processes require confirmation (denylist in
  PITFALLS.md). Open handles with the minimum access right needed, e.g.
  `PROCESS_QUERY_LIMITED_INFORMATION` for listing.
- **Python**: each run inside a Job Object (`KILL_ON_JOB_CLOSE`); the interpreter path comes from
  user config or `py -0p`, never from the script's directory.
- **Config files**: store under `%LOCALAPPDATA%`; treat as untrusted input on load (validate, fail
  to defaults, report through `ErrorReporter`).
- **Logging**: never log secrets, environment blocks or full command lines of *other* processes at
  Info level (they can contain tokens).
