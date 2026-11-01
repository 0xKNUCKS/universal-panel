# Contributing

Thanks for helping. Before changing code, read [docs/DESIGN.md](docs/DESIGN.md) (goals and design
principles), [docs/SECURITY.md](docs/SECURITY.md) and [docs/PITFALLS.md](docs/PITFALLS.md).

## Building

Requires the .NET 10 SDK. `dotnet build -c Release`; see the README for details. Close a running
`UniversalPanel.exe` first, or the build can't replace its files.

## Commits

The history should read like a changelog: **one logical change per commit**, each commit builds on
its own, and each message follows the format below.

### Format

[Conventional Commits 1.0.0](https://www.conventionalcommits.org/en/v1.0.0/) with the project's
fixed types and scopes:

```
<type>(<scope>): <subject>

<body>

<footer>
```

**Header** (required)
- `<type>`: one of the types below.
- `<scope>`: one of the scopes below. Exactly one. If a change truly needs two scopes, it's two commits.
- `<subject>`: imperative mood ("add", not "added"/"adds"), lowercase first letter, no trailing
  period. Keep the header around 60 characters, 72 at most.

| Type | Use for |
|---|---|
| `feat` | New user-visible capability |
| `fix` | Bug fix |
| `perf` | Performance improvement, no behavior change |
| `refactor` | Code restructure, no behavior change |
| `security` | Hardening with no feature change (DLL search order, ACLs, input validation) |
| `docs` | Documentation only |
| `test` | Tests only |
| `build` | csproj, packages, SDK, manifest, solution |
| `chore` | Repo housekeeping (.gitignore, editor config, tooling) |
| `style` | Formatting only |

| Scope | Covers |
|---|---|
| `shell` | `Shell/`, `App.xaml(.cs)`: panel window, animation, hotkey, tray, single instance |
| `diagnostics` | `Diagnostics/`: Log, ErrorReporter, Guard |
| `native` | `Native/`, `NativeMethods.txt` |
| `modules` | `Modules/IPanelModule.cs` and shared module plumbing |
| `processes` | Process monitor module |
| `launcher` | Tools launcher module |
| `terminal` | Terminal module (shells in tabs, terminal host) |
| `network` | Network monitor module |
| `settings` | Settings page (`Modules/Settings/`) and the persisted settings it edits |
| `docs` | `docs/`, `README.md`, `CONTRIBUTING.md` (when the commit is docs-only) |
| `build` | Build configuration |
| `repo` | Repo-level files (.gitignore and similar) |

A new module adds its own scope here in the same commit that creates the module.

**Body** (required unless the header says everything, e.g. a typo fix)
- Blank line after the header. Wrap at 72 characters.
- Explain **what and why**, not a file list. The diff already shows which files changed.
- At most ~6 short lines or bullets. If you need more, the commit is probably too big.
- If code was ported or mirrored from a reference repo, add a line:
  `Mirrors: <repo> <path> @ <short-sha>` (same entry as in `docs/REFERENCES.md`).

**Footer**
- `BREAKING CHANGE: <description>` when behavior, config or a contract changes incompatibly
  (e.g. the `IPanelModule` signature, the default hotkey).
- `Refs: #<issue>` when there is an issue.

### Examples

```
feat(shell): add slide-in panel docked to monitor work area

Panel opens on the monitor under the cursor, full height, sized in
physical pixels so mixed-DPI setups get the exact rect.

Mirrors: EarTrumpet UI/Views/FlyoutWindow.xaml.cs @ f492b8f
```

```
security(shell): restrict single-instance pipe to current user
```

```
fix(diagnostics): flush log before process termination

AppDomain.UnhandledException ends the process right after the
handler returns, so queued lines were lost on crash.
```

```
feat(modules)!: pass cancellation token to OnActivated

BREAKING CHANGE: IPanelModule.OnActivated now takes a
CancellationToken; modules must stop work when it is cancelled.
```

Not like this:

```
update stuff
feat: add panel, logging, docs, security and fix hotkey
feat(shell): Added the panel.
```

### What counts as one commit

One commit is one change that can be **reviewed, reverted and described in one header** without
the word "and".

- A feature's code, its `docs/REFERENCES.md` row, its `docs/SECURITY.md` / `PITFALLS.md` notes and
  its `NativeMethods.txt` entries go in **the same commit** as the feature. Docs describing code
  ship with that code.
- Unrelated doc edits, dependency bumps, refactors and fixes found along the way are **separate
  commits**.
- A refactor a feature needs goes in its own commit *before* the feature.
- Build or config changes a feature needs (a new package) go with the feature, unless they are
  general (an SDK bump); then they're separate.
- Every commit builds with 0 errors and no new warnings (docs-only commits excepted). When other
  changes are still uncommitted, check with
  `git stash push --keep-index --include-untracked` → build → `git stash pop`.
- Never commit build output (`bin/`, `obj/`), logs, secrets or paths to your personal files.

### Pushed history

Don't amend, rebase, squash or reword commits that are already pushed, and never force-push
`main`.
