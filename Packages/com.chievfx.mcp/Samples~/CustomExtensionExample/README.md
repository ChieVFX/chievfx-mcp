# ChievFX MCP Custom Extension Example

A minimal, self-contained ChievFX MCP extension that a project can register
without editing the core package. It demonstrates the three extensible surfaces,
all grouped under the `Custom` category so they appear at the top of the optional
categories in the ChievFX MCP window:

- Executable tool `custom-example-echo` (runs through `ToolRunner`).
- Dynamic resource `chievfx://extensions/chievfx.example.custom/status`
  (served by `ResourceReader`).
- Dynamic prompt `custom-example-plan` (`Dynamic = true`, served by
  `PromptRunner`).

## How it works

`ChievfxMcpCustomExtensionExample` uses `[InitializeOnLoad]` to register a
`ChievfxMcpExtensionDescriptor` with `ChievfxMcpExtensionRegistry` on domain
reload. No core loader edit is needed: any Editor assembly that self-registers
is picked up by the manifest snapshot the selection windows export before
reading metadata.

## Using the sample

1. Import this sample from Package Manager: select the ChievFX MCP package, open
   the Samples tab, and import "Custom Extension Example". (Unity copies it into
   `Assets/Samples/...`, where the `[InitializeOnLoad]` ctor runs.)
2. Open `Window > ChievFX MCP`. In the Tools, Resources, and Prompts tabs the
   `Custom` category appears at the top of the optional categories.
3. Enable the capabilities you want, then reload MCP tools (or restart Cursor)
   so the client reads the updated descriptors.
4. Verify:
   - Call `custom-example-echo` with a `message` argument.
   - Read `chievfx://extensions/chievfx.example.custom/status`.
   - Get the `custom-example-plan` prompt with an optional `focus` argument; it
     is generated live in Unity through the bridge.

## Adapting it

Copy this folder out of `Samples~`, rename the assembly, change the extension
`Id` to your own reverse-DNS namespace, and replace the capabilities. Keep
extension code Editor-only and keep resource/template URIs under
`chievfx://extensions/<your-extension-id>/`.
