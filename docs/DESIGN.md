# Design principles

universal-panel is an **open source** Windows utility for reverse engineers and power users. Every
design choice serves three goals, in this order when they conflict:

1. **Secure.** The panel can run elevated and handles hostile input (malware samples, other
   processes' memory, user-writable config). It must never become a way for less-trusted code to
   gain rights, data or control.
2. **User-friendly.** It works out of the box, explains itself, and never silently loses or
   breaks what the user set up.
3. **Open source quality.** Strangers will read, build, audit and extend it. Code, data formats
   and decisions must make sense without asking the author.

Security never wins by removing functionality. Find the design that keeps both, and if a real
trade-off remains, write it down in [SECURITY.md](SECURITY.md).

[SECURITY.md](SECURITY.md) and [PITFALLS.md](PITFALLS.md) hold the concrete, binding rules. This
file is the reasoning behind them and the checks for anything they don't cover yet.

## Making a design decision

For anything non-trivial:

1. Read the parts of SECURITY.md and PITFALLS.md that apply.
2. Name the realistic options (usually two or three) and pick one using the principles below.
3. State the choice in the commit body or PR, with the main alternative you rejected and why.
4. If the choice adds hardening, a trade-off or a trap, update SECURITY.md / PITFALLS.md in the
   same change.

Prefer the simplest design that meets all three goals. Don't add options, abstractions or
generality nobody has asked for.

## Secure

- **Assume the panel is elevated.** Every code path must be safe when it is.
- **Anything a normal user process can write is untrusted:** `settings.json` and other config,
  files under `%LOCALAPPDATA%`, environment variables, window titles, other processes' memory
  and command lines, program output, files being scanned. Validate on load, clamp, cap counts
  and lengths, and fall back to defaults.
- **No privilege leaks.** Things the panel starts (tools, shells, interpreters) run unelevated by
  default. Elevation is an explicit, per-action user choice, never a stored flag. Never add a
  config value that an unelevated process could edit to get something run elevated.
- **No new command channels into the panel.** The single-instance pipe only shows the panel.
  Don't add IPC an unelevated process could use to make the elevated panel act.
- **Paths:** fully qualified, no `"`, no network paths (`\\…`, `//…`). The panel must not reach
  the network on its own (SMB access leaks the user's NTLM hash).
- **Files the panel owns** (copied icons, caches) live in its own folder and are referenced by a
  strict name pattern (e.g. a content hash), never by a stored path. The panel never reads or
  deletes anything outside that folder based on config contents.
- **Parsing untrusted files** happens out of process with less than the user's rights, the way
  `DieScanner` does: restricted token, job object, pinned and hash-checked binaries.
- **Launching processes:** absolute paths, `ArgumentList`, no `cmd /c`, no command-line strings.
  Each long-lived child goes in a job object with kill-on-close.
- **Other processes and threads:** open with the minimum access right, and re-check the creation
  time before acting so a reused PID can't redirect the action.
- **Avoid techniques that look like malware** (keyboard hooks, parent-process spoofing, code
  injection) when a normal API does the job. Antivirus false positives are a user-friendliness
  problem too.
- **Logs:** no secrets, environment blocks or other processes' full command lines at Info level.

## User-friendly

- **Works on first run.** Sensible defaults, no required setup, and a missing or broken config
  file means defaults plus a notice, never a crash.
- **Never lose or silently break user setup.** If something the user configured can't be used
  (missing file, rejected path), tell them through the notice bar and fall back visibly. Prefer
  designs where it can't break at all, e.g. copy a picked icon into the panel's folder instead
  of pointing at the user's Downloads.
- **Keep user content apart from UI state.** Things the user built by hand (tool list, profiles)
  go in their own file. Window widths, sort orders and collapsed sections go in `settings.json`.
  Resetting the UI must never cost the user their content.
- **Discoverable.** Actions are reachable from a button or the right-click menu, with keyboard
  shortcuts on top, not instead. Data the user may want to find has an "Open … folder" button.
- **Errors say what to do,** not just what failed. Use `ErrorReporter` and `IModuleHost.ShowNotice`.
- **Destructive actions confirm** through `IModuleHost.ConfirmAsync`, with Cancel as the default.
  Critical-process guardrails warn but don't block.
- **Honest indicators.** A badge or label never claims more than the code proves (Verified vs
  Identified in Tools is the model).
- **Cheap when hidden.** Modules stop all background work in `OnDeactivated`. Use
  `SuppressAutoHide` whenever focus legitimately leaves the panel.
- **Feels instant.** Slow work goes off the UI thread. Big live lists use virtualized,
  self-drawing rows (see PITFALLS.md, General).

## Open source quality

- **No machine-specific assumptions.** No hard-coded user paths, drive letters or locally
  installed tools; discover them or let the user pick.
- **Readable formats.** Config is small, human-readable JSON with stable property names, safe to
  hand-edit (and safe if hand-edited badly).
- **Config compatibility.** Until the first public release, config changes don't need migration
  code. After it, a change that would drop or misread existing user data needs one.
- **Third-party code and binaries** are pinned by version and hash, with license and source
  recorded in `THIRD_PARTY_NOTICES.md` and [REFERENCES.md](REFERENCES.md). The build still works
  offline.
- **No telemetry, no network calls at runtime** unless a feature is explicitly about the network
  and the user started it.
- **Match the surrounding code**: error boundaries via `Guard`, P/Invoke via CsWin32, the
  existing naming and comment density. Update `README.md` when user-visible behavior changes.

## Storage

| Data | Where |
|---|---|
| Preferences and UI state | `%LOCALAPPDATA%\UniversalPanel\settings.json` |
| User-built content (tools, future profiles) | its own file in the same folder, e.g. `tools.json` |
| Assets the panel copies in (icons) | a subfolder, files named by content hash |
| Logs | `%LOCALAPPDATA%\UniversalPanel\logs` |

Every file is loaded as untrusted input (validate, cap, fall back) and saved write-then-rename.
Local, not Roaming: tool paths only make sense on one machine. Not Documents, not next to the exe.

## Feature scope

Before building a feature, ask: would people genuinely use this weekly, or does it only look
good in a demo? Prefer features that use what the panel uniquely has (the process sampler, the
verified tool catalog, the sandboxed scanner, control over how children are started) over
generic ones other tools already do well.

## Checklist

- [ ] Safe when the panel is elevated; no path from user-writable data to elevated action.
- [ ] All loaded data validated; no network paths; owned files referenced by name pattern.
- [ ] Works with no config; failures are visible and explained; user content can't be lost.
- [ ] Background work stops when hidden; UI thread stays free.
- [ ] No machine-specific assumptions; third-party code pinned and credited.
- [ ] SECURITY.md, PITFALLS.md and README.md updated where the change touches them.
- [ ] The choice and its main rejected alternative stated in the commit body or PR.
