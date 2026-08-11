# ChievFX MCP

> Work in progress.

A Unity Editor bridge and [MCP (Model Context Protocol)](https://modelcontextprotocol.io/) server that lets AI agents drive the Unity Editor: read project context, query and edit assets and scenes, inspect the console, run editor operations, capture screenshots, run tests, and more.

## Why this exists

I tried the existing Unity MCP extensions and kept hitting the same walls:

- **Unstable for LLM agents.** Tool surfaces that were hard for an agent to use reliably — ambiguous arguments, noisy results, surprising failure modes.
- **Unstable connection.** The bridge between the editor and the MCP server would drop, hang, or need restarts.
- **Slow and bloated.** Heavy tool sets, large payloads, sluggish round-trips.

Things in that space have gotten better over time. But after building this out for my own workflows, in a lot of ways this extension still feels better to me: a leaner, more predictable tool surface designed around how agents actually call tools, a more resilient editor↔server bridge, and compact results that keep token usage and latency down.

Concrete comparison cases are planned — benchmarks and side-by-side examples will be added here as the project stabilizes.

## Installation

### Option A — Unity Package Manager (git URL)

1. In Unity, open **Window > Package Manager**.
2. Click **+ > Add package from git URL...**
3. Paste:

```
https://github.com/ChieVFX/chievfx-mcp.git?path=Packages/com.chievfx.mcp
```

To pin a specific revision, append `#<branch|tag|commit>`, e.g. `...com.chievfx.mcp#main`.

> Requires Unity `2022.3`+. Dependencies (`com.unity.nuget.newtonsoft-json`, `com.unity.test-framework`) are declared in the package and resolved automatically by Unity.

### Option B — Manual install (Python installer)

For copying the package into another Unity project (e.g. to vendor a local copy), use the drag-and-drop installer shipped inside the package at `Packages/com.chievfx.mcp/Install~/`.

Open **Window > ChievFX > MCP** → **Connection** → **Advanced details** → **Launch Python Installer**. Nothing to set up by hand: it runs on the single shared managed Python environment at `~/.chievfx-mcp/env` — the same interpreter that runs the MCP server — and installs the installer's requirements into it on first use (~25 s, once per machine). There is no per-project virtual environment.

To run it standalone instead:

```bash
~/.chievfx-mcp/env/bin/python3 -m pip install -r Packages/com.chievfx.mcp/Install~/requirements.txt
~/.chievfx-mcp/env/bin/python3 Packages/com.chievfx.mcp/Install~/chievfx_mcp_installer.py
```

On Windows the interpreter is `%USERPROFILE%\.chievfx-mcp\env\python.exe`.

Then drag a source Unity project into **FROM** and one or more target Unity projects into **TO**, and click **Install**. Tick **Install as tarball (.tgz)** to install as a manifest `file:` dependency instead of copied sources — the filename carries an auto-incrementing build suffix so target projects re-resolve the package without a manual version bump. See [`Packages/com.chievfx.mcp/Install~/README.md`](Packages/com.chievfx.mcp/Install~/README.md) for details.

## Getting started

After the package is installed in your Unity project:

1. Open the project and wait for compile + domain reload.
2. **Window > ChievFX > MCP** → **Start Bridge**.
3. Keep **Cursor** selected, or switch the client to **Claude Code**, **Codex**, **Kimi Code** or **JetBrains Rider**, then click **Write <client> Config**.
4. Reload your MCP client's tools (or restart it).

The MCP server appears as `unity-mcp-chievfx`.

**Codex only:** Codex ignores a project's `.codex/config.toml` — the only place this server is
declared — until the project is trusted in your user-level config (`CODEX_HOME` or
`~/.codex/config.toml`):

```toml
[projects.'/absolute/path/to/project']
trust_level = "trusted"
```

Normally Codex writes that itself when you answer its trust prompt, but a host that drives Codex
without showing the prompt (JetBrains AI's Codex agent) never gets the chance, so the config looks
correct while no Unity tools load. Auto-setup adds the entry when nothing covers the project yet;
turn it off with **Record Codex project trust** in the window's Automation section. Trust matching is
exact-path: only the launch directory or the repo root Codex resolves from it counts.

### No system Python needed

On first setup the package downloads a portable CPython (3.12.13, from
[astral-sh/python-build-standalone](https://github.com/astral-sh/python-build-standalone)) into
`~/.chievfx-mcp/env/` and points every client config at it. One environment per machine, shared by
all project copies, so a missing or too-old system Python is not a problem. The Welcome window
reports what the managed install did.

### Generated client skills

Opening the project also writes an `mcp-unity-chievfx` skill for each supported client —
`.cursor/`, `.claude/`, `.codex/`, `.kimi-code/`, at `skills/mcp-unity-chievfx/SKILL.md` — holding
the complete tool and resource reference with argument signatures. Most clients truncate MCP startup
instructions, so the same content is placed where the client loads skills from. These files are
generated (rewritten on open, content follows the local tool selection); gitignore them like the
`mcp.json` configs.

## What's new in 0.3.0

Everything since the initial public release (0.2.0). Highlights:

- **Client setup is automatic.** Configs for Cursor, Claude Code, Codex, Kimi Code and JetBrains
  Rider are written once per Unity session, idempotently — never rewritten when already correct,
  never clobbered on a mid-write race, and never touched while entering or in Play Mode. Configs
  point at a stable launcher path instead of the `PackageCache` hash, so they survive package
  re-resolves. A Welcome window shows setup status the first time and whenever something is wrong,
  and `Ctrl+Alt+M` opens the ChievFX MCP window.
- **Managed Python.** A portable CPython is downloaded into `~/.chievfx-mcp/env/` and reused by the
  server and the drag-and-drop installer alike. The installer no longer needs a hand-made venv.
- **Tarball install mode.** The Python installer can install the package as a manifest `file:` `.tgz`
  dependency with a per-version build suffix, cleaning up prior install forms first.
- **Agent instructions reworked.** Startup instructions lead with the imperative and the literal call
  convention, list only unlisted domains, and inline the essential tool signatures; a per-client
  capability skill carries the full reference. Tool descriptors were trimmed across the board (with a
  regression guard) to cut token cost.
- **Runtime UI and input.** A `ui-runtime-*` family (probe, click, drag, focus, type-text,
  set-control-value) drives uGUI and UI Toolkit in Play Mode, plus
  `input-control-keyboard-sequence` for batched keyboard input. Input injection is now the default,
  screenshot-space coordinates match what the screenshot shows, and HUD interactions are supported.
- **Play Mode edits.** GameObject, transform and particle edits work while playing;
  `editor-playmode-set` waits for a settled state and recovers from a domain reload interrupting the
  round-trip; `recompile` exits Play Mode as it should.
- **New tools.** `frame-debugger-pick-pixel` (which draw call wrote this pixel), `shader-status`
  (screenshots are flagged when taken mid shader compile), `console-get-logs` freshness cursors,
  dedupe toggle and `includeStack`, compile errors and warnings surfaced in `recompile` output, and
  `savePath` on screenshots.
- **Bridge performance.** The event journal no longer rewrites `events.json` on every log line, and
  heartbeats no longer re-read or double-parse it — journal flush dropped from 27% of wall clock to
  3%.
- **Fails loudly instead of lying.** Unrecognized tool arguments warn instead of being dropped
  silently, `frame-debugger-control` and input injection stop reporting success for work they never
  did, and `editor-window-focus` names the real problem when no target is supplied.
- **Tool availability is selectable.** All non-hidden tools and resources are exposed by default; turn
  on **Customize which tools & resources are exposed** in the Connection section to pick them
  yourself (the Tools and Resources panels stay read-only until you do).

## Documentation

These guides ship inside the package (`Packages/com.chievfx.mcp/Documentation~/`), so they travel with both the UPM and Python installs:

- [Extending ChievFX MCP](Packages/com.chievfx.mcp/Documentation~/ChievfxMcpExtensions.md) — author custom tools, resources, and prompts (including the `Custom` category and dynamic prompts).
- [Agent instructions internals](Packages/com.chievfx.mcp/Documentation~/ChievfxMcpAgentInstructions.md) — how `initialize.instructions` and category resources are assembled.
- [Python installer](Packages/com.chievfx.mcp/Install~/README.md) — copy and tarball install modes, requirements, remembered paths.

Two samples ship under `Packages/com.chievfx.mcp/Samples~/`:

- **Editor Tests** (`Samples~/Tests`) — the package's own editor tests and QA fixtures, in a test assembly that actually runs once imported. Declared in `package.json`, so it appears in the Package Manager's **Samples** section.
- **Custom Extension Example** (`Samples~/CustomExtensionExample`) — a minimal third-party extension registering its own tools. Copy it into your project manually.

## License

[MIT](LICENSE.md) © Evgeniy Skvortsov
