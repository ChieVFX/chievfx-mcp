# ChievFX Unity MCP Installer

PyQt6 drag-and-drop tool that installs the ChievFX Unity MCP into another Unity
project.

## What it copies

From this repo (`FROM`) into a target Unity project (`TO`), at the same
relative paths:

```text
Tools/ChievfxMcp/                 (entire folder)
Assets/Editor/ChievfxMcp/         (entire folder)
Assets/Editor/ChievfxMcp.meta     (folder meta)
Assets/Editor/ChievfxMcpExtensions/      (entire folder)
Assets/Editor/ChievfxMcpExtensions.meta  (folder meta)
```

Existing copies in `TO` are deleted first. MCP test folders from earlier
installs are also removed. `__pycache__/`, `tests/`, `*.pyc`, and `*.pyo` are
skipped during copy.

## Requirements

- Python 3.10+ on the host machine.
- Unity Editor (for the target project).
- Target project's `Packages/manifest.json` must list:
  - `com.unity.nuget.newtonsoft-json` at `3.2.2` or newer — required by the
    bridge for JSON parsing. The installer detects whether this package is
    present and, if not, asks whether to add it for you (alphabetically
    inserted into `dependencies`).
  - `com.unity.test-framework` — required for the bridge `tests-run` tool.
    The installer only warns if it is missing; add it manually via Unity
    Package Manager if you need test execution.

## Setup

```bash
cd Install
python3 -m venv .venv
source .venv/bin/activate          # macOS / Linux
# .venv\Scripts\activate            # Windows
pip install -r requirements.txt
```

## Run

```bash
python chievfx_mcp_installer.py
```

1. The `FROM` zone auto-fills with this repo on launch (the installer's parent
   folder). Override by dragging another folder or clicking `Browse...`.
2. Drag the target Unity project root onto the `TO` zone, or click
   `Browse...`.
3. Both zones turn green when validation passes. The `Install` button enables.
4. Click `Install`, confirm.
5. If `com.unity.nuget.newtonsoft-json` is missing from the target manifest
   the installer asks whether to add it. Pick `Yes` unless you plan to add
   it yourself.
6. Watch the log.

## Post-install steps in the target Unity project

1. Open the project, wait for compile and domain reload.
2. `Window > ChievFX > MCP` -> `Start Bridge` -> `Write Cursor Config`.
3. Reload Cursor MCP tools or restart Cursor.

The MCP server should appear as `unity-mcp-chievfx`.

## Notes

- The installer never edits files in `FROM`. Only `TO` is modified.
- `.cursor/mcp.json` in `TO` is written by the Unity editor button, not by
  this installer.
- Runtime artifacts under `Library/ChievfxMcpBridge/` and
  `UserSettings/ChievfxMcp*.json` are created by Unity at runtime; the
  installer never touches them.
- MCP unit tests are intentionally not installed into target projects. They are
  developed and run in this repo.
