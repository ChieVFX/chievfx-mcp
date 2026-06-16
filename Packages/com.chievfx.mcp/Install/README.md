# ChievFX Unity MCP Installer

PyQt6 drag-and-drop tool that copies the `com.chievfx.mcp` Unity package from one
Unity project into another.

## What it copies

From a source Unity project (`FROM`) into one or more target Unity projects (`TO`),
at the same relative path:

```text
Packages/com.chievfx.mcp/         (entire package folder)
Packages/com.chievfx.mcp.meta     (when present in FROM)
```

Existing copies in `TO` are deleted first. `__pycache__/`, `tests/`, `*.pyc`, and
`*.pyo` are skipped during copy.

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

From a Unity project that already contains this package:

```bash
cd Packages/com.chievfx.mcp/Install
python3 -m venv .venv
source .venv/bin/activate          # macOS / Linux
# .venv\Scripts\activate            # Windows
pip install -r requirements.txt
```

## Run

```bash
python chievfx_mcp_installer.py
```

Or launch it from **Window > ChievFX > MCP** → **Connection** → **Advanced details**
→ **Launch Python Installer**.

1. **FROM** auto-fills by walking up from the installer folder until a Unity
   project root containing `Packages/com.chievfx.mcp/` is found. Override by
   dragging another project or clicking **Browse...**.
2. Drag one or more target Unity project roots onto **TO**, or click
   **Browse...**.
3. Both zones turn green when validation passes. The **Install** button enables.
4. Click **Install**, confirm.
5. If `com.unity.nuget.newtonsoft-json` is missing from the target manifest
   the installer asks whether to add it. Pick **Yes** unless you plan to add
   it yourself.
6. Watch the log.

## Remembered paths

FROM and TO are cached **per launcher Unity project**, not globally:

```text
~/.chievfx_mcp_installer/profiles/<hash>/settings.json
```

- Launch from Unity project **A** → remembers that project's last FROM/TO pair.
- Launch from Unity project **B** → separate profile, so A↔B bidirectional
  installs do not overwrite each other.
- Standalone launch (no `--launcher-project`) uses the host Unity project that
  contains the installer, or a `__default__` profile when none is found.

Legacy global TO-only cache from `~/.chievfx_mcp_installer.json` is imported
into the default profile on first use.

## Post-install steps in the target Unity project

1. Open the project, wait for compile and domain reload.
2. `Window > ChievFX > MCP` -> `Start Bridge`.
3. Keep `Cursor` selected, or switch the client to `Claude Code` or `Codex`, then click `Write <client> Config`.
4. Reload your MCP client's tools or restart it.

The MCP server should appear as `unity-mcp-chievfx`.

## Notes

- The installer never edits files in `FROM`. Only `TO` is modified.
- `.cursor/mcp.json`, `.mcp.json`, or `.codex/config.toml` in `TO` is written
  by the Unity editor button, not by this installer.
- Runtime artifacts under `Library/ChievfxMcpBridge/` and
  `UserSettings/ChievfxMcp*.json` are created by Unity at runtime; the
  installer never touches them.
- MCP unit tests are intentionally not installed into target projects. They are
  developed and run in the source project.
