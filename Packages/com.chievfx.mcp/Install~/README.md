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

Before installing (in **either** mode), the installer removes other existing
forms of the package from `TO` so only the mode being installed remains:

- embedded/copied sources — `Packages/com.chievfx.mcp/` (and its `.meta`),
- any `com.chievfx.mcp-*.tgz` (and `.meta`) under `Assets/` or `Packages/`.

The `com.chievfx.mcp` entry in `Packages/manifest.json` is then handled by the
chosen mode: **copy** mode removes it (an embedded package needs no manifest
entry), while **tarball** mode rewrites it (see below). Either way any prior git
url, `file:`, or registry version is superseded.

`__pycache__/`, `tests/`, `*.pyc`, and `*.pyo` are skipped during copy.

### Install as tarball (.tgz)

Tick **Install as tarball (.tgz)** to install as a Unity tarball dependency instead
of copying sources. For each `TO` project the installer:

- removes the other existing installs of the package (see above),
- builds the tarball into the **Destination folder** (default `Assets/Editor`,
  editable in the UI), and
- sets `"com.chievfx.mcp": "file:<relative-path>.tgz"` in `Packages/manifest.json`
  — if a `com.chievfx.mcp` line already exists it is **substituted in place**
  (keeping its position), otherwise it is **inserted alphabetically** among the
  dependencies.

The tarball filename carries a per-version build suffix so updates propagate
without a manual `package.json` bump:

- the **first** tarball of a version has **no suffix**
  (`com.chievfx.mcp-<version>.tgz`),
- each subsequent rebuild of the **same** version increments `.f1`, `.f2`, … (the
  suffix = biggest existing index for that version in the target folder(s) + 1,
  shared across all targets in one run so it matches between projects),
- bumping the version in `package.json` starts fresh (no suffix again).

Because the filename — and therefore the manifest `file:` reference — changes on
every install, a project copy that pulls the new `.tgz` re-resolves the package
automatically. Prior tarballs are removed by the pre-install cleanup, so they
don't accumulate.

The `.tgz` includes every package file **and its `.meta`** (an immutable tarball
package drops any `.cs` missing its `.meta`), excluding `__pycache__/`, `tests/`,
and `*.pyc`/`*.pyo`. The choice and folder are remembered per launcher project.

## Requirements

- Python 3.9+ on the host machine.
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

Nothing to do. Launching from Unity (**Window > ChievFX > MCP** → **Connection** →
**Advanced details** → **Launch Python Installer**) uses the **single shared managed
environment** at `~/.chievfx-mcp/env` — the same interpreter that runs the MCP server —
and installs `requirements.txt` into it on first use (~25 s once per machine). Every
project copy reuses that one environment; there is no per-project virtual environment.

If the shared environment cannot be used, the launcher falls back to any interpreter that
already has PyQt6 (a hand-made `Install~/.venv`, or a system Python) and logs a warning
saying why.

To install the requirements by hand:

```bash
~/.chievfx-mcp/env/bin/python3 -m pip install -r requirements.txt
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
3. Keep `Cursor` selected, or switch the client to `Claude Code`, `Codex`, `Kimi Code` or `JetBrains Rider`, then click `Write <client> Config`.
4. Reload your MCP client's tools or restart it.

The MCP server should appear as `unity-mcp-chievfx`.

Opening the project also writes a `mcp-unity-chievfx` skill for each client —
`.cursor/`, `.claude/`, `.codex/`, `.kimi-code/` under `skills/mcp-unity-chievfx/SKILL.md` —
holding the complete tool/resource reference with argument signatures. MCP startup
instructions are truncated by most clients, so the same content is placed where the client
loads skills from. These files are generated (rewritten on open, content follows the local
tool selection); gitignore them like the `mcp.json` configs.

## Notes

- On macOS, Unity launches this as a bare Python process. The installer now
  forces itself frontmost before confirm/result dialogs so Install does not
  look like a no-op behind the Unity window.
- The installer never edits files in `FROM`. Only `TO` is modified.
- Installing into the same project that owns the running `Install~` package is
  blocked, because that would delete the live installer mid-run.
- `.cursor/mcp.json`, `.mcp.json`, or `.codex/config.toml` in `TO` is written
  by the Unity editor button, not by this installer.
- Runtime artifacts under `Library/ChievfxMcpBridge/` and
  `UserSettings/ChievfxMcp*.json` are created by Unity at runtime; the
  installer never touches them.
- MCP unit tests are intentionally not installed into target projects. They are
  developed and run in the source project.
