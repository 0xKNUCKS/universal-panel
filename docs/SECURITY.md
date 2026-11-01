# Security notes

Low-cost hardening applied so far, plus rules for upcoming modules. Hardening must not reduce
functionality; where a trade-off exists it is written down here.

## Applied

| Where | What | Why |
|---|---|---|
| `App.HardenDllSearchPath` | `SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS)` at the top of `Main` | Removes the current directory and `PATH` from native DLL search order. Blocks DLL planting via a malicious DLL next to a file/folder we touch, or in a user-writable `PATH` entry. App dir and System32 are still searched. |
| `SingleInstance` | Pipe DACL grants only the current user's SID; mutex in `Local\` | Other users / sessions can't connect. Uses the user SID rather than `CurrentUserOnly` (which compares token owners, Administrators when elevated) so a normal launch can still reach an elevated panel. The server never reads from the pipe, and the client sends nothing, so a connection can only trigger "show panel". |
| `HotkeyService` | `RegisterHotKey` (via NHotkey), never a low-level keyboard hook | We never see keystrokes, and hooks are an AV/EDR red flag. |
| `App.OpenLogFolder` | Absolute path to `%WINDIR%\explorer.exe`, `UseShellExecute=false`, argument via `ArgumentList` | No `PATH` lookup, no shell parsing/quoting injection. |
| `Log` | Logs under `%LOCALAPPDATA%\UniversalPanel\logs` (user-only ACL by default), bounded queue, per-day size cap, 7-day retention | A crash loop can't fill the disk; logs aren't readable by other users. |
| `app.manifest` | `asInvoker`, x64 only | Least privilege by default. Elevation is opt-in: tray > Restart as administrator (UAC). |
| `Elevation` | SeDebugPrivilege is enabled only when already elevated. The elevated restart passes only `--replace <pid>`, and the new copy waits only if that PID is the panel's own executable | An argument can't make the panel wait on (or act on) an unrelated process. No logon task is created; starting elevated at logon is a separate, explicit opt-in. |
| `ProcessControl` | Each action opens the process with only the right it needs, then checks the creation time still matches the sampled row before acting | A PID reused between sampling and the click can't redirect terminate/suspend to a different process. |
| `ThreadControl` | Thread actions re-check the thread's creation time and that it still belongs to the process being shown | Same PID-reuse protection as processes, for TIDs. |
| `HandleSnapshot` | Other processes' handles are duplicated with zero access (names) or limited-query access (process/thread identity) and closed immediately; never `DUPLICATE_SAME_ACCESS`. Name queries that can hang run on a timed worker | Reading handle info never gives the panel real access to another process's objects, and a hostile pipe can't freeze the panel. Closing a remote handle always confirms and is refused for the panel itself. |
| `ProcessGuard` | Critical processes (System Informer's list, System PID, break-on-termination) get an explicit warning; the panel refuses to suspend itself | Prevents one-click bugchecks, logoffs or a panel that can never resume. Not a restriction: the action still runs after confirming. |
| `DieScanner` (Detect It Easy) | Build: the release zip is pinned by version + SHA-256 and the build fails (and deletes it) on a mismatch; only manifest-listed files are copied. Run: every file in `die\bin` is hashed against the manifest embedded in the panel and held open without write/delete sharing; unknown files (e.g. a planted `version.dll` or `qt.conf`) and links are refused. `diec` runs on a restricted copy of our token (low integrity, Administrators deny-only, no privileges) in a job (1 process, 1 GB, no clipboard/desktop/atoms, killed with the panel), inherits only its output pipe, gets a minimal environment, an explicit `-D` database path and `--` before the target | The panel may be elevated and `diec` parses untrusted files. Even if a file is swapped after the check, or a crafted target exploits `diec`, that code runs with *less* than the normal user. Exception to "never build a command line": `CreateProcessAsUser` needs one; its only variable part is a fully qualified, existing file path, which can't contain `"`. |
| `ToolLauncher` / Tools module | Opening a tool from an elevated panel goes through Explorer's own `ShellExecute` (desktop shell view), so the tool gets the user's normal token; if that fails the launch fails, never falls back to elevated. "Run as administrator" is per click, and when the panel is already elevated (no UAC prompt) it confirms with the path and signer first. The tool list in `settings.json` is just paths, with no "always elevated" flag, and is sanitized on load | `settings.json` is user-writable: an unelevated process editing it must not be able to get something started elevated without the user clicking "Run as administrator" on it. |
| `ToolEntry.IsSafePath` (Tools) | Tool paths and custom icons must be local: `\\…` and `//…` (UNC, `\\?\UNC\`, device paths) are refused on load (dropped from a hand-edited file), in Add / Edit / the icon picker (with a notice), and the finder skips network folders on PATH. Known limitation: mapped drive letters (Z:) aren't detected | Reading a tool's version, signature or icon from a share makes the panel, possibly elevated, connect over SMB at every start, which leaks the user's NTLM hash; the tool list is user-writable. |
| Tool actions "pass arguments as typed" | Opt-in per action (the catalog uses it only for CFF Explorer): the arguments are joined unquoted instead of quoted. Still no shell, window actions only | It only changes how that one program splits its own command line, so it can't add commands or rights. |
| `ToolCatalog` | "Verified" only when the file is validly signed by the publisher expected for that tool; tools whose publisher doesn't sign are labelled "Identified" (version info can be faked); a broken or foreign signature shows a warning and opening it asks first | The indicator never claims more than the file proves. |
| `ToolCommand` / `ConsoleRunner` (tool actions, "Open with") | Action arguments are split first and placeholders filled inside each argument, so a process or file path can't add arguments or commands. Nothing goes through a shell: Windows tools get an MSVC-quoted command line (round-trip checked with `CommandLineToArgvW`), WSL tools run with `wsl.exe --exec` (without it, bash would expand `$(...)` in a path), and `.bat`/`.cmd` tools refuse arguments with cmd's special characters. Console output uses the panel's own `--run-console` mode instead of `cmd /k`. Actions start unelevated like any tool launch; "run as administrator" is Shift+click, confirmed when the panel is elevated | Paths come from other processes and from disk, so they're untrusted input. The catalog is embedded in the panel; users' own actions live in their settings and run with their normal rights. |

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
