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

### Shell

| Our file | Mirrors | What was taken |
|---|---|---|
| `src/UniversalPanel/Native/WindowExtensions.cs` | EarTrumpet `EarTrumpet/Extensions/WindowExtensions.cs` | `Cloak` (`DWMWA_CLOAK`), `ApplyExtendedWindowStyle(WS_EX_TOOLWINDOW)`, `SetWindowPos` with `SWP_NOZORDER \| SWP_NOACTIVATE` |

### Error handling

| Our file | Mirrors | What was taken |
|---|---|---|
| `src/UniversalPanel/Diagnostics/ErrorReporter.cs` (global handlers) | Flow Launcher `Flow.Launcher/Helper/ErrorReporting.cs` | Dispatcher handler marks handled; AppDomain handler logs (process dies); unobserved task exceptions logged + `SetObserved()` |
| `src/UniversalPanel/Diagnostics/ErrorReporter.cs` (`IsRecoverableDwmCompositionException`) | PowerToys `src/modules/launcher/PowerLauncher/Helper/ExceptionHelper.cs`, as copied by Flow Launcher `Flow.Launcher/Helper/ExceptionHelper.cs` | Filter for harmless `WindowChrome` COM exceptions during DWM composition changes |
| `src/UniversalPanel/App.xaml.cs` (startup catch) | Flow Launcher `App.ShowErrorMsgBoxAndFailFast` | Startup failures are logged and shown, never silent |

Our own additions (no upstream equivalent, kept small on purpose): de-duplication of repeated
errors, the UI-exception circuit breaker, `Guard` boundaries, and the bounded background logger.

## Planned references per module

| Module | Primary reference | Secondary |
|---|---|---|
| Process monitor | System Informer (`winsiderss/systeminformer`), `NtQuerySystemInformation(SystemProcessInformation)` | Vanara (`dahall/Vanara`) for typed ntdll/advapi32 wrappers |
| Tools launcher / icons | PowerToys Run `Microsoft.Plugin.Program` (`IShellItemImageFactory`, `.lnk` resolution) | Flow Launcher `Plugins/Flow.Launcher.Plugin.Program` |
| Python console | `code.InteractiveConsole` driver (CPython stdlib), CliWrap for piping | Job Objects with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` |
| Network monitor | System Informer network tab, `GetExtendedTcpTable` / `GetExtendedUdpTable` | TinyWall; TraceEvent for ETW bandwidth |

These entries become rows in the "Ported / mirrored code" table as each module is implemented.
