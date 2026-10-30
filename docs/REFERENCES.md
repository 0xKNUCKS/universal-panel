# References

Every non-trivial piece of this project is grounded in an existing, maintained open-source
implementation. This file maps each piece of our code to the code it mirrors, so any part can be
audited against its source.

Rules:

- Prefer using a well-known library over re-implementing it.
- When we port logic, record the repo, commit, file and what was taken.
- Only port from licenses compatible with MIT (MIT, Apache-2.0, BSD). Keep attribution in the
  ported file's header comment.
- GPL/LGPL code may be *read* for API behavior, never copied. Check each repo's license at the
  commit we reference before porting.

## Libraries we depend on

| Package | Version | License | Why |
|---|---|---|---|
| [WPF-UI](https://github.com/lepoco/wpfui) | 4.3.0 | MIT | Fluent theme, controls, Mica/Acrylic backdrop (`FluentWindow`) |
| [NHotkey.Wpf](https://github.com/thomaslevesque/NHotkey) | 3.0.0 | Apache-2.0 | Global hotkey via `RegisterHotKey` (same package Flow Launcher uses) |
| [H.NotifyIcon.Wpf](https://github.com/HavenDV/H.NotifyIcon) | 2.4.1 | MIT | Tray icon |
| [Microsoft.Windows.CsWin32](https://github.com/microsoft/CsWin32) | 0.3.x | MIT | P/Invoke signatures generated from official Win32 metadata (no hand-written signatures) |

## Ported / mirrored code

Reference snapshots (shallow clones, read 2026-10-29):

| Repo | License | Commit |
|---|---|---|
| [File-New-Project/EarTrumpet](https://github.com/File-New-Project/EarTrumpet) | MIT (with named excluded entities) | `f492b8fa6459d9c5b442ad07ea28d70e9db7f493` |
| [Flow-Launcher/Flow.Launcher](https://github.com/Flow-Launcher/Flow.Launcher) | MIT | `440f38bd2addc49c1507ef32536edb9511477f82` |
| [lepoco/wpfui](https://github.com/lepoco/wpfui) | MIT | `ffebacd61058170cf63864b7d5aa730cffff848a` |
| [thomaslevesque/NHotkey](https://github.com/thomaslevesque/NHotkey) | Apache-2.0 | `0def9e82b71d67b97026901a2f543202af430d09` |
| [microsoft/PowerToys](https://github.com/microsoft/PowerToys) | MIT | `669a19ea9c50f6b42ecc8ee962e1323013c3656f` |
| [HavenDV/H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon) | MIT | `d590d46aa18d480782c8aaf5bf4fee2b4c12accf` |
| [winsiderss/systeminformer](https://github.com/winsiderss/systeminformer) | MIT | `c2f1489890f14de76b985c0142cbad154d92623d` |

### Shell

| Our file | Mirrors | What was taken |
|---|---|---|
| `src/UniversalPanel/Shell/SingleInstance.cs` | Flow Launcher `Flow.Launcher/Helper/SingleInstance.cs` | Per-user named mutex + named pipe that tells the first instance to show itself |
| `src/UniversalPanel/App.xaml.cs` (`Main`) | Flow Launcher `Flow.Launcher/App.xaml.cs` `Main()` | Custom `[STAThread] Main` that gates `App.Run()` on the single-instance check |
| `src/UniversalPanel/Shell/HotkeyService.cs` | Flow Launcher `Flow.Launcher/Helper/HotKeyMapper.cs` | `HotkeyManager.Current.AddOrReplace(...)`, catching `HotkeyAlreadyRegisteredException` and surfacing it |
| `src/UniversalPanel/Native/WindowExtensions.cs` | EarTrumpet `EarTrumpet/Extensions/WindowExtensions.cs` | `Cloak` (`DWMWA_CLOAK`), `ApplyExtendedWindowStyle(WS_EX_TOOLWINDOW)`, `SetWindowPos` with `SWP_NOZORDER \| SWP_NOACTIVATE` |
| `src/UniversalPanel/Shell/PanelWindow.xaml.cs` (show/hide) | EarTrumpet `EarTrumpet/UI/Views/FlyoutWindow.xaml.cs` | Pre-warm with `Show(); Hide();` + `WS_EX_TOOLWINDOW`; position from monitor working area in physical pixels; cloak while hidden |
| `src/UniversalPanel/Shell/PanelAnimator.cs` | EarTrumpet `EarTrumpet/UI/Helpers/WindowAnimationLibrary.cs` | Entrance/exit storyboard on `Window.Left` + opacity, `QuinticEase` ease-out, `FillBehavior.Stop` then commit final value; activate + `SetForegroundWindow` on completion |
| `src/UniversalPanel/Shell/PanelWindow.xaml` | WPF-UI `src/Wpf.Ui/Controls/FluentWindow/FluentWindow.cs`, `Controls/Window/WindowBackdrop.cs` | `FluentWindow` + `ExtendsContentIntoTitleBar` + `WindowBackdropType` (backdrop requires extended content) |

### Processes

| Our file | Mirrors | What was taken |
|---|---|---|
| `src/UniversalPanel/Native/ProcessSnapshot.cs`, `ThreadStates.cs` | System Informer `phnt/include/ntexapi.h` `SYSTEM_PROCESS_INFORMATION` + `SYSTEM_THREAD_INFORMATION`; `ntkeapi.h` `KTHREAD_STATE` / `KWAIT_REASON`; `phlib/native.c` `PhEnumProcessesEx` | Field layout (0x100 bytes on x64); one `NtQuerySystemInformation(SystemProcessInformation)` call, grow on `STATUS_INFO_LENGTH_MISMATCH` / `STATUS_BUFFER_TOO_SMALL`, keep the size for next time; `PagefileUsage` as private bytes |
| `src/UniversalPanel/Modules/Processes/ProcessSampler.cs` | System Informer `SystemInformer/procprv.c` (time-based CPU mode) | CPU % = process (kernel + user) delta / system (kernel incl. idle + user) delta; processes keyed by PID + create time so reused PIDs don't inherit history |
| `src/UniversalPanel/Native/ProcessControl.cs` | System Informer `SystemInformer/actions.c` `PhUiTerminateProcesses`, `PhUiSuspendProcesses`, `PhUiResumeProcesses`; `phnt/include/ntpsapi.h` | Least access per action (`PROCESS_TERMINATE`, `PROCESS_SUSPEND_RESUME`); exit status 1 on terminate; `NtSuspendProcess` / `NtResumeProcess`; `ProcessBreakOnTermination` query |
| `src/UniversalPanel/Native/ProcessControl.cs` (`SetPriority`) | System Informer `SystemInformer/actions.c` `PhUiSetPriorityClassProcesses` | `PROCESS_SET_INFORMATION`; refuse System (PID 4), which can bugcheck some Windows versions |
| `src/UniversalPanel/Modules/Processes/ProcessGuard.cs` | System Informer `SystemInformer/actions.c` `PhIsDangerousProcess`, `PhpShowContinueMessageProcesses` | Dangerous image-name list + System PID + break-on-termination = critical; terminate always confirms, suspend confirms only when dangerous |

### Error handling

| Our file | Mirrors | What was taken |
|---|---|---|
| `src/UniversalPanel/Diagnostics/ErrorReporter.cs` (global handlers) | Flow Launcher `Flow.Launcher/Helper/ErrorReporting.cs` | Dispatcher handler marks handled; AppDomain handler logs (process dies); unobserved task exceptions logged + `SetObserved()` |
| `src/UniversalPanel/Diagnostics/ErrorReporter.cs` (`IsRecoverableDwmCompositionException`) | PowerToys `src/modules/launcher/PowerLauncher/Helper/ExceptionHelper.cs`, as copied by Flow Launcher `Flow.Launcher/Helper/ExceptionHelper.cs` | Filter for harmless `WindowChrome` COM exceptions during DWM composition changes |
| `src/UniversalPanel/App.xaml.cs` (startup catch) | Flow Launcher `App.ShowErrorMsgBoxAndFailFast` | Startup failures are logged and shown, never silent |

Our own additions (no upstream equivalent, kept small on purpose): de-duplication of repeated
errors, the UI-exception circuit breaker, `Guard` boundaries, and the bounded background logger.

Deliberate deviations from upstream:

- `SingleInstance`: upstream fires the client connect as an un-awaited task and returns from
  `Main`, which can exit before the signal is sent; we connect synchronously with a 1 s timeout.
  Added `CurrentUserOnly` pipe options (see [SECURITY.md](SECURITY.md)).
- Animations: EarTrumpet fades `Window.Opacity`, which only works with `AllowsTransparency`. We fade
  the window's content instead.

Deviation from EarTrumpet's backdrop: EarTrumpet uses `AllowsTransparency="True"` plus the
undocumented `SetWindowCompositionAttribute` accent policy for acrylic. We use WPF-UI's
documented `DWMWA_SYSTEMBACKDROP_TYPE` path instead, which avoids the layered-window rendering cost
(see [PITFALLS.md](PITFALLS.md)).

## Planned references per module

| Module | Primary reference | Secondary |
|---|---|---|
| Process details (modules, handles, threads) | System Informer `SystemInformer/modprv.c`, `hndlprv.c`, `thrdprv.c` (list, process, threads done; see Processes above) | Vanara (`dahall/Vanara`) for typed ntdll/advapi32 wrappers |
| Tools launcher / icons | PowerToys Run `Microsoft.Plugin.Program` (`IShellItemImageFactory`, `.lnk` resolution) | Flow Launcher `Plugins/Flow.Launcher.Plugin.Program` |
| Python console | `code.InteractiveConsole` driver (CPython stdlib), CliWrap for piping | Job Objects with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` |
| Network monitor | System Informer network tab, `GetExtendedTcpTable` / `GetExtendedUdpTable` | TinyWall; TraceEvent for ETW bandwidth |

These entries become rows in the "Ported / mirrored code" table as each module is implemented.
