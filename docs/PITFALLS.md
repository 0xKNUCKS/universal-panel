# Pitfalls checklist

Known traps for this kind of app. Check the relevant section before implementing a module.

## Elevation / UAC

- Don't prompt UAC at every logon. To start elevated, use a Task Scheduler task with "Run with
  highest privileges" at logon (the `Run` registry key can't elevate). Alternative: unelevated UI +
  small elevated helper over a named pipe.
- Anything an elevated panel launches runs elevated too. To launch unelevated from an elevated
  process, go through Explorer (`IShellDispatch2::ShellExecute` via the shell window) or
  `CreateProcessWithTokenW` with Explorer's token.
- UIPI blocks drag-and-drop from unelevated Explorer into an elevated window.
- Enable `SeDebugPrivilege` explicitly. Protected processes (PPL) still refuse access: open with
  `PROCESS_QUERY_LIMITED_INFORMATION` and degrade gracefully.

## Process module

- Never poll via WMI or `PerformanceCounter`. Avoid `System.Diagnostics.Process` at scale. One
  `NtQuerySystemInformation(SystemProcessInformation)` call per tick.
- `NtQueryObject` can hang on some synchronous pipe handles: query handle names on a worker thread
  with a timeout (System Informer does this).
- Build x64 only. For WOW64 processes use `EnumProcessModulesEx(LIST_MODULES_ALL)`.
- Guardrails: killing `csrss`/`wininit` bugchecks; suspending `dwm`/`explorer`/`csrss` freezes the
  desktop. Keep a denylist with confirmation.

## Network module

- Connection lists (IP Helper) don't need admin; per-process bandwidth (ETW) does.
- Stop ETW sessions on module deactivate and on crash. Orphaned real-time sessions linger and the
  system-wide session count is limited.

## Antivirus false positives

- OpenProcess on everything, suspend, token manipulation, global hotkeys and spawning interpreters
  all look suspicious to heuristics.
- Code-sign builds, avoid packers/obfuscators, prefer `RegisterHotKey` over low-level keyboard hooks.

## Focus / hotkeys

- Receiving `WM_HOTKEY` grants the right to `SetForegroundWindow`. Showing from other triggers
  (tray, timer, IPC) may only flash the taskbar.
- Hide-on-deactivate must be suppressed while launching a tool, during a UAC prompt, or while a
  file dialog is open.
- `RegisterHotKey` fails if another app owns the combo; some Win+ combos are reserved. Surface it.
- `WS_EX_TOOLWINDOW` keeps the panel out of Alt+Tab and the taskbar.

## DPI / monitors / animation

- Per-Monitor V2 in the manifest. WPF works in DIPs; `SetWindowPos` uses physical pixels.
- Use the monitor work area (`rcWork`), on the monitor under the cursor. Recompute on display/DPI
  change.
- Never recreate the window; keep it alive and hidden/cloaked, pre-warmed at startup.
- Avoid `AllowsTransparency=True` (layered window, worse rendering). Use DWM system backdrop.
- Respect `SPI_GETCLIENTAREAANIMATION` (user disabled animations).

## Python console (out-of-process)

- Piped stdio = no TTY: Python block-buffers and prints no prompts. Use `-u`, and preferably a small
  driver script built on `code.InteractiveConsole` speaking a line protocol (prompts, multi-line
  input, completion).
- Ctrl+C doesn't travel over pipes. Note `CTRL_BREAK_EVENT` raises SIGBREAK, which *terminates*
  Python by default rather than raising `KeyboardInterrupt`, so implement interrupt through the
  driver protocol (e.g. `_thread.interrupt_main()` from a control thread).
- Put each Python process in a Job Object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` so child
  processes die with it.
- Set `PYTHONUTF8=1`. Discover interpreters with `py -0p`; a venv has a `pyvenv.cfg`.

## General

- Single instance via named mutex; forward "show" to the running instance.
- Icon extraction is COM: do it on an STA thread and cache.
- Leave GC defaults alone; don't call `EmptyWorkingSet` to fake low RAM.
- H.NotifyIcon's `ForceCreate()` defaults to enabling Windows 11 Efficiency Mode for the whole
  process. Call `ForceCreate(false)` or the panel and its monitors get throttled.
- WPF-UI implicit styles: an App.xaml style with `BasedOn="{StaticResource {x:Type ToolTip}}"`
  (same key as itself) resolves lazily, finds itself instead of WPF-UI's style and silently falls
  back to the default light control. Extend WPF-UI styles in code from the found `Style` object
  (see `App.ExtendToolTipStyle`), and check against the real compiled App, not `XamlReader.Parse`.
- WPF `DataGrid` rows are expensive: ~4-8 ms each to build here, even with one bare text column
  and no styling, so anything that re-shows ~30 rows (a tree collapse, a refresh) costs ~250 ms.
  For big live lists use a virtualized `ListBox` with rows that draw themselves (`ProcessRowView`).
- Pausing `IsLiveSorting` while live-filtered properties change also stops the view from telling
  the control: the view drops the rows, the list keeps showing them. Check the control's own
  `Items.Count`, not only the view's.
