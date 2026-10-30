---
name: commit
description: Commit work in universal-panel as clean, one-feature-per-commit history in Conventional Commits format. Use whenever committing, pushing, "save my work", finishing a feature, or when the working tree holds changes from more than one feature that need splitting.
---

# Commit workflow for universal-panel

The history must read like a changelog: **one logical change per commit**, each commit builds on
its own, and each message follows the format below exactly. Never bundle unrelated work into one
commit and then list it all in the message body.

## 1. The format

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
  period, at most 60 characters for the whole header line ideally, 72 hard max.

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
| `chore` | Repo housekeeping (.gitignore, editor config, tooling, .claude/) |
| `style` | Formatting only |

| Scope | Covers |
|---|---|
| `shell` | `Shell/`, `App.xaml(.cs)`: panel window, animation, hotkey, tray, single instance |
| `diagnostics` | `Diagnostics/`: Log, ErrorReporter, Guard |
| `native` | `Native/`, `NativeMethods.txt` |
| `modules` | `Modules/IPanelModule.cs` and shared module plumbing |
| `processes` | Process monitor module |
| `launcher` | Tools launcher module |
| `python` | Python console / script runner module |
| `network` | Network monitor module |
| `docs` | `docs/`, `README.md` (when the commit is docs-only) |
| `build` | Build configuration |
| `repo` | Repo-level files (.gitignore, .claude/) |

New modules add their own scope here in the same commit that creates the module.

**Body** (required unless the header says everything, e.g. a typo fix)
- Blank line after the header. Wrap at 72 characters.
- Explain **what and why**, not a file list. The diff already shows which files changed.
- At most ~6 short lines or bullets. If you need more, the commit is probably too big.
- If code was ported or mirrored from a reference repo, add a line:
  `Mirrors: <repo> <path> @ <short-sha>` (same entry as in `docs/REFERENCES.md`).

**Footer**
- `BREAKING CHANGE: <description>` when behavior/config/contract changes incompatibly
  (e.g. `IPanelModule` signature, default hotkey).
- `Refs: #<issue>` when there is an issue.
- The co-author trailer required by the session's attribution instructions goes last.

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

Bad (never do this):

```
update stuff
feat: add panel, logging, docs, security and fix hotkey
feat(shell): Added the panel.
```

## 2. What counts as one commit

One commit = one change that can be **reviewed, reverted and described in one header** without
the word "and".

- A feature's code, its `docs/REFERENCES.md` row, its `docs/SECURITY.md` / `PITFALLS.md` notes and
  its `NativeMethods.txt` entries belong in **the same commit** as the feature. Docs describing
  code ship with that code.
- Unrelated doc edits, dependency bumps, refactors, and fixes found along the way are **separate
  commits**.
- A refactor needed by a feature goes in its own commit *before* the feature.
- Build/config changes needed by a feature (new package) go with the feature, unless they are
  general (SDK bump) — then separate.
- Generated or build output (`bin/`, `obj/`, logs, scratch files) is never committed.

## 3. Procedure

Run every step. Use the Bash tool (Git Bash); interactive git (`-i`, `-p`) is not available.

### 3.1 Survey

```bash
git status --short
git diff --stat
git diff
git log --oneline -10
```

Read the full diff, including untracked files (`git status` shows them; read them).

### 3.2 Plan the commits

Group every changed hunk into logical units per section 2. Order them so each commit builds:
dependencies first (native helpers → diagnostics → shell → modules → docs-only).

Write the plan out in your message as a short table: commit header → files (and which hunks for
files split across commits). If the user asked to commit, proceed; if a grouping is genuinely
ambiguous (a hunk could belong to either feature and it changes what gets reverted together),
ask the user before committing.

### 3.3 Stage one commit at a time

Whole files:

```bash
git add -- path/one.cs path/two.cs
```

A file whose hunks belong to different commits — stage only the right hunks via a patch,
never `git add -p`:

```bash
P="<session scratchpad dir>/stage.patch"   # absolute path; never inside the repo
git diff -- path/file.cs > "$P"
# edit $P: delete the hunks that belong to other commits (keep the file header lines),
# keep hunk headers intact for the hunks you keep
git apply --cached --recount "$P"
git diff --cached -- path/file.cs     # confirm only the intended hunks are staged
```

New file that should only be partly committed: write the partial content as the first commit's
version, commit, then restore the full content. Rare — prefer splitting by file.

Never use `git add -A`, `git add .` or `git commit -a` unless the survey showed every change
belongs to the single commit you are making.

### 3.4 Verify the staged commit builds on its own

When the tree still has other uncommitted changes, build only what is staged:

```bash
git stash push --keep-index --include-untracked -m "commit-skill: unstaged"
dotnet build -c Release --nologo -v q
git stash pop
```

The build must have 0 errors and introduce no new warnings. If it fails, fix the grouping (usually
a dependency belongs in an earlier commit), don't commit a broken state. If `stash pop` reports
a conflict, stop and tell the user; don't resolve it by discarding anything.

When the staged set is the entire remaining change, a normal `dotnet build -c Release` is enough.

Docs-only and `.claude/` commits skip the build.

### 3.5 Commit

Pass the message through a heredoc so formatting survives:

```bash
git commit -F - <<'EOF'
feat(shell): add slide-in panel docked to monitor work area

Panel opens on the monitor under the cursor, full height, sized in
physical pixels so mixed-DPI setups get the exact rect.

Mirrors: EarTrumpet UI/Views/FlyoutWindow.xaml.cs @ f492b8f

Co-Authored-By: <trailer from the session's attribution instructions>
EOF
```

Then check: `git show --stat HEAD`. Repeat 3.3–3.5 for the next unit.

### 3.6 Finish

```bash
git status --short          # must be empty, or only what the user chose to leave out
git log --oneline origin/main..HEAD
```

Report the list of commits created (short sha + header).

### 3.7 Push

Push only when the user asked to push in this request. `git push origin <branch>`; never force
push. If the remote rejected the push (diverged), stop and ask; don't rebase or force on your own.

## 4. Never

- Never amend, rebase, squash or reword commits that are already pushed, unless the user
  explicitly asks; that requires a force push, which also needs their explicit OK.
- Never `--no-verify`, never skip signing.
- Never commit secrets, local paths to the user's personal files, logs, or `bin/`/`obj/`.
- Never write a body that is a list of files or a list of unrelated changes.
- Never commit a state that doesn't build (except docs-only commits).
