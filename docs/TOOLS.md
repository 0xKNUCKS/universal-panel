# Tools and actions

The Tools tab is a launcher for your tools, and each tool can have **actions**: commands that
run it on something. Right-click a process (or a module in its details) and pick
**Open with ›** to run an action on it, e.g. *Attach x64dbg* or *strings (8+ chars)*.

There are two ways to change what's offered:

- **In the panel** (for your own setup): Tools tab, right-click a tool, **Edit…**, then
  **Actions**. Your changes are saved in your settings and override the defaults for that tool.
  **Reset to defaults** goes back to the shipped ones.
- **In the repository** (to improve the defaults for everyone): edit
  [`src/UniversalPanel/Catalog/tools.json`](../src/UniversalPanel/Catalog/tools.json). No C#
  needed. It's JSON with `//` comments allowed.

## Actions

| Field | Values | Meaning |
|---|---|---|
| `name` | text | What the menu shows, e.g. `Open in IDA`. |
| `target` | `process`, `file`, `none` | When it's offered ("Runs on" in the panel). `process`: when you right-click a process; it gets `{pid}`, and `{path}` is the process's exe. `file`: for a process's exe *and* for modules (DLLs); it gets `{path}`, `{dir}`, `{name}` but no `{pid}`, because a DLL has none. `none`: never offered, just starts the tool. |
| `run` | `window` (default), `console` | `console` keeps a window open with the tool's output. Use it for command-line tools. |
| `args` | text | The arguments, with placeholders. |
| `quote` | `true` (default), `false` | `false` passes the arguments exactly as typed, unquoted, for tools that read their whole command line as one path (CFF Explorer). Window actions only. |

Placeholders:

| Placeholder | Becomes |
|---|---|
| `{pid}` | The process ID. |
| `{path}` | The full path of the file (or the process's image). For WSL tools it's converted automatically, e.g. `/mnt/c/...`. |
| `{dir}` | The folder that file is in (converted for WSL too). |
| `{name}` | Just the file name, e.g. `notepad.exe`. |

How arguments are built, so you don't have to think about quoting:

- `args` is split into arguments first: spaces separate them, and `"double quotes"` group
  words. Then the placeholders are filled in, inside each argument. A path with spaces, `&`,
  `%` or `$(…)` stays exactly one argument. `{path}` and `"{path}"` mean the same thing.
- Nothing goes through a shell. Windows tools get a properly quoted command line, and WSL tools
  run as `wsl.exe -d <distro> --exec <command> …`, which skips the Linux shell.
- `.bat` / `.cmd` tools are run by cmd.exe, which re-reads its command line. An argument
  containing `% ! ^ & | < >` or a quote is refused for them rather than escaped.
- Type placeholders exactly as shown, braces included: `-p {pid}`. The **Insert** buttons
  under Arguments type them for you.
- An unknown placeholder (a typo like `{file}`) or one the action can't get (`{pid}` in a
  `file` action) is an error, shown in the preview and when saving, instead of reaching the
  tool as literal text. Other braces (`{0}`, JSON) are left alone.
- The Edit page shows a live preview of the exact command for each action, using the sample
  values that kind of action really gets.

Examples:

```jsonc
{ "id": "attach", "name": "Attach x64dbg",        "target": "process", "args": "-p {pid}" }
{ "id": "open",   "name": "Open in IDA",          "target": "file",    "args": "{path}" }
{ "id": "dump",   "name": "ProcDump full dump",   "target": "process", "run": "console", "args": "-ma {pid}" }
{ "id": "yara",   "name": "yara (my rules)",      "target": "file",    "run": "console", "args": "/home/me/rules.yar {path}" }
```

## Adding a tool to the catalog

```jsonc
{
  "id": "pe-bear",                       // unique, lowercase
  "name": "PE-bear",                     // shown in the panel
  "categories": ["Binary analysis"],     // sections it's listed in (it can be in several)
  "match": { "ProductName": "PE-bear" }, // how the panel recognizes the file
  "signer": null,                        // part of the signer's name, if the publisher signs it
  "files": ["PE-bear.exe"],              // names "Find installed tools" looks for
  "actions": [
    { "id": "open", "name": "Open in PE-bear", "target": "file", "args": "{path}" }
  ]
}
```

- **`match`**: version-info fields as Windows shows them (file **Properties > Details**):
  `CompanyName`, `ProductName`, `FileDescription`, `OriginalFilename`, `InternalName`,
  `LegalCopyright`. All of them must match. `*` is a wildcard, and matching ignores case.
  To see a file's values: `(Get-Item tool.exe).VersionInfo | Format-List`.
- **`signer`**: if the publisher signs the tool, a valid signature from them makes it
  **Verified**. Without one, a match is only **Identified**, because version info can be faked.
  To find the signer: `(Get-AuthenticodeSignature tool.exe).SignerCertificate.Subject`.
- **`files`**: preferred first. When an install has several entry points (x64dbg's `x96dbg.exe`
  picks 32 or 64 bit), list the one to use first.
- **WSL tools**: use `"wsl": "<command>"` instead of `match` / `files`. "Find installed tools"
  asks each distro whether it has the command. WSL tools always run in a console.
- Optional: `"nameFrom": "FileDescription"` shows the file's own description as the name (used
  for the generic Sysinternals entry), and `"version": "buildDate"` shows the link date
  instead of the version (x64dbg snapshots all report 0.0.2.5).
- Order matters: the first entry that matches wins, so put specific entries before generic ones.

Before opening a pull request, run the panel and check the tool shows up with the right name,
badge and actions, and that the preview on its Edit page is the command you expect.

## Your own tools

Any program, script or WSL command can be added (**Add > Add a program…**) and given actions on
its Edit page. It's offered under **Open with** like a catalog tool. Tools whose signature is
broken, or signed by someone other than expected, ask before running.

Running as administrator is always a per-click choice (Shift+click in **Open with**, or the
menu on the tile), never saved with the tool. See [SECURITY.md](SECURITY.md).
