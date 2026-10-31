# Third-party notices

Builds of universal-panel include files that are not part of this repository's source.

## Detect It Easy 3.21 (`die/bin`, downloaded at build time)

- **Detect It Easy** (`diec.exe`, `db/`): MIT License, Copyright (c) 2012-2026 hors.
  https://github.com/horsicq/DIE-engine. The license text ships as `die/LICENSE.txt` and is in
  `third_party/die/LICENSE`.
- **Qt 5** (`Qt5Core.dll`, `Qt5Script.dll`): The Qt Company Ltd and other contributors, used under
  the GNU LGPL v3 as shipped unmodified in the Detect It Easy release. Source:
  https://download.qt.io/archive/qt/5.15/. You may replace these libraries with your own build.
- **Microsoft Visual C++ runtime** (`msvcp140*.dll`, `vcruntime140*.dll`): redistributed as part of
  the Detect It Easy release under the Visual Studio redistributable terms.

Nothing from these is linked into the panel; `diec` runs as a separate, sandboxed process.
