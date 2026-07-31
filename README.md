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

For copying the package into another Unity project (e.g. to vendor a local copy), use the drag-and-drop installer shipped inside the package at `Packages/com.chievfx.mcp/Install/`.

```bash
cd Packages/com.chievfx.mcp/Install
python3 -m venv .venv
source .venv/bin/activate          # macOS / Linux
# .venv\Scripts\activate           # Windows
pip install -r requirements.txt
python chievfx_mcp_installer.py
```

Or open **Window > ChievFX > MCP** → **Connection** → **Advanced details** → **Launch Python Installer**.

Then drag a source Unity project into **FROM** and one or more target Unity projects into **TO**, and click **Install**. See [`Packages/com.chievfx.mcp/Install/README.md`](Packages/com.chievfx.mcp/Install/README.md) for details.

## Getting started

After the package is installed in your Unity project:

1. Open the project and wait for compile + domain reload.
2. **Window > ChievFX > MCP** → **Start Bridge**.
3. Keep **Cursor** selected, or switch the client to **Claude Code**, **Codex**, **Kimi Code** or **JetBrains Rider**, then click **Write <client> Config**.
4. Reload your MCP client's tools (or restart it).

The MCP server appears as `unity-mcp-chievfx`.

## Documentation

These guides ship inside the package (`Packages/com.chievfx.mcp/Documentation~/`), so they travel with both the UPM and Python installs:

- [Extending ChievFX MCP](Packages/com.chievfx.mcp/Documentation~/ChievfxMcpExtensions.md) — author custom tools, resources, and prompts (including the `Custom` category and dynamic prompts).
- [Agent instructions internals](Packages/com.chievfx.mcp/Documentation~/ChievfxMcpAgentInstructions.md) — how `initialize.instructions` and category resources are assembled.

## License

[MIT](LICENSE.md) © Evgeniy Skvortsov
