# ChievFX MCP Extension Guide

This guide is for Unity editor assemblies that add ChievFX MCP capabilities
without changing the core bridge source. A minimal extension only needs an
Editor-only asmdef, a static initializer, and a descriptor registered with
`ChievfxMcpExtensionRegistry`.

## Capability Choice

Use a resource when the agent needs read-only evidence: status, indexes,
summaries, serialized data, or drill-down URIs. Resources are discoverable
through `resources/list`, read with `resources/read`, and can be disabled in
the ChievFX MCP Resources window unless marked required.

Use a resource template when the URI has one path variable and many possible
instances, such as `chievfx://extensions/vendor.package/item/{id}`. Keep
template results bounded and include `maxResults`, `truncated`, and follow-up
URIs for deeper reads.

Use a prompt when you want to give the model a reusable workflow or instruction
bundle. Prompts are best for "how to inspect this domain" guidance, not for
fetching live editor data. Static prompts can include scalar arguments with
`{argumentName}` placeholders.

Use a tool only for an explicit action that is not just reading data. Tools need
the strongest security review because they can mutate projects, execute code,
or touch the network. In the current extension registry slice, external tool
descriptors are advertised for metadata and selection preview, but non-core
tool calls return a metadata-only error. Ship custom executable tools only when
you also add runtime dispatch in the MCP server/core bridge.

## Tool Roles

MCP tool roles are presets on top of per-category and per-tool selection. Built-in
presets are defined in `Tools/ChievfxMcp/chievfx_mcp_role_presets.json` and
custom presets are `ChievfxMcpToolRoleAsset` project assets. A role lists
category names plus optional explicit tool IDs; applying it enables matching
tools, disables other optional tools, and never disables required tools.

Create custom role assets from `Window > ChievFX > MCP Tools`, then edit
`enabledCategoryIds` and `enabledToolIds` in Inspector or save the current
selection into the asset from the same window. Agents can inspect/apply roles
with `tools-get-roles` and `tools-set-role`. After switching roles or editing
enabled tools, call `reload_cursor_mcp` for `unity-mcp-chievfx` (or its full
runtime identifier) before relying on changed Cursor tool descriptors.

## Folder Layout

First-party extensions live beside core editor code:

```text
Assets/
  Editor/
    ChievfxMcp/
      Chievfx.Mcp.Editor.asmdef
    ChievfxMcpExtensions/
      Ecs/
        Chievfx.Mcp.Extensions.Ecs.asmdef
        ChievfxMcpEcsExtension.cs
      SampleReadOnly/
        Chievfx.Mcp.Extensions.SampleReadOnly.asmdef
        ChievfxMcpSampleReadOnlyExtension.cs
```

User-created extensions can live under the project or a UPM package:

```text
Assets/
  YourCompany/
    Editor/
      ChievfxMcpExtensions/
        YourFeature/
          YourCompany.Mcp.YourFeature.asmdef
          YourFeatureMcpExtension.cs
```

```text
Packages/
  com.your-company.chievfx-mcp-your-feature/
    package.json
    Editor/
      YourCompany.Mcp.YourFeature.asmdef
      YourFeatureMcpExtension.cs
    Documentation~/
      ChievfxMcpYourFeature.md
```

Keep extension code Editor-only. Do not put MCP registration code in runtime
assemblies or player builds.

## Assembly Definition Setup

Minimal asmdef:

```json
{
  "name": "YourCompany.Mcp.YourFeature",
  "rootNamespace": "YourCompany.Mcp.YourFeature",
  "references": [
    "Chievfx.Mcp.Editor",
    "Unity.Newtonsoft.Json"
  ],
  "includePlatforms": [
    "Editor"
  ],
  "autoReferenced": true,
  "defineConstraints": [],
  "versionDefines": []
}
```

Required references:

- `Chievfx.Mcp.Editor`: core extension API, including descriptors and registry.
- `Unity.Newtonsoft.Json`: needed when you declare prompt arguments or JSON
  schemas with `JArray`/`JObject`. Omit it only if your extension does not use
  Newtonsoft types.

Optional package dependency patterns:

- Add required packages to `Packages/manifest.json` or your UPM `package.json`
  dependencies when the extension cannot compile without them.
- Add optional package asmdef references only when the referenced asmdef exists
  for every project that imports your package.
- Prefer `versionDefines` for optional packages. Example: define
  `YOUR_FEATURE_HAS_ENTITIES` when `com.unity.entities` is present, then either
  compile guarded code or expose a status resource that reports unavailable
  capabilities.
- Use `defineConstraints` only when the whole extension assembly must disappear
  unless a symbol is present. This is safer than leaving broken optional
  references in projects that do not install your dependency.
- Reflection is acceptable for optional Unity packages when it keeps the asmdef
  compiling everywhere. See the ECS extension pattern for status resources plus
  reflection-based reads.

## Registration

Register once from an Editor assembly:

```csharp
using Chievfx.Mcp.Editor;
using UnityEditor;

namespace YourCompany.Mcp.YourFeature
{
    [InitializeOnLoad]
    public static class YourFeatureMcpExtension
    {
        static YourFeatureMcpExtension()
        {
            ChievfxMcpExtensionRegistry.RegisterExtension(CreateDescriptor());
        }

        private static ChievfxMcpExtensionDescriptor CreateDescriptor()
        {
            return new ChievfxMcpExtensionDescriptor
            {
                Id = "your-company.your-feature",
                DisplayName = "Your Feature MCP",
                Version = "0.1.0",
                Description = "Read-only Your Feature inspection resources.",
            };
        }
    }
}
```

Add capabilities to the descriptor:

- `Tools`: `ChievfxMcpToolDescriptor` with `Name`, `Description`, `Category`,
  and `InputSchema`.
- `Resources`: `ChievfxMcpResourceDescriptor` with `Id`, `Uri`, `Name`,
  `Description`, `MimeType`, `Category`, optional `Required`, and optional
  `StaticText`.
- `ResourceTemplates`: `ChievfxMcpResourceTemplateDescriptor` with `Id`,
  `UriTemplate`, `Name`, `Description`, `MimeType`, `Category`, and optional
  `Required`.
- `Prompts`: `ChievfxMcpPromptDescriptor` with `Name`, `Title`, `Description`,
  `Category`, `Arguments`, optional `Required`, and optional `StaticText`.
- `ResourceReader`: `Func<string, object?>` used for dynamic extension
  resources and templates. Return strings, dictionaries, arrays, or other JSON
  serializable values.

Static resources and prompts are served directly from the MCP server manifest.
Dynamic resources call back into Unity through the bridge and then into
`ResourceReader`.

## Manifest Fields

At domain reload, Unity writes a generated runtime manifest to
`Library/ChievfxMcpBridge/extension-capabilities.json`. The MCP server reads
that file and merges extension capabilities into `tools/list`,
`resources/list`, `resources/templates/list`, and `prompts/list`.

Generated manifest fields:

- `schemaVersion`: extension manifest schema version. Current value is `1`.
- `extensionUriPrefix`: currently `chievfx://extensions/`.
- `id`: stable extension id. Use lowercase reverse-DNS style, for example
  `vendor.package` or `vendor.package.feature`.
- `displayName`: human-readable name shown in metadata.
- `version`: extension version. Use SemVer when possible.
- `description`: short summary for metadata/debugging.
- `sourceAssembly`: Unity assembly that registered the extension.
- `tools`, `resources`, `resourceTemplates`, `prompts`: generated descriptor
  arrays.

Recommended package-level fields to document in your README or package
manifest, even when the current generated manifest does not consume them yet:

- `minCoreVersion`: oldest ChievFX MCP core version you test against.
- `dependencies`: required and optional Unity packages, assembly references,
  symbols, and external services.
- `capabilities`: tool/resource/prompt ids plus short purpose text.
- `risk` or `permissions`: read-only, write, destructive, execute, or network.
- `categories`: categories used for selection windows and metadata grouping.
- `tokenEstimates`: expected descriptor and response size, including caps.
- `defaultEnablement`: whether capabilities should be enabled by default and
  which ones are required/policy-locked.

## URI And Naming Rules

Extension resource URIs must start with:

```text
chievfx://extensions/
```

Own a namespace under your extension id:

```text
chievfx://extensions/vendor.package/status
chievfx://extensions/vendor.package/items/{id}
```

Rules:

- Extension ids match `^[a-z0-9][a-z0-9._-]{0,127}$`.
- Tool, resource, template, and prompt ids match
  `^[a-z0-9][a-z0-9_-]{0,127}$`.
- Capability ids must not collide with core ids or another extension.
- Resource URIs and URI templates must be unique.
- URI template variables use `{name}` and match one path segment.
- Put the extension id immediately after `chievfx://extensions/` so ownership
  is obvious.
- Use nouns for resources and prompts, verbs for tools.

## Security Expectations

Classify each capability before shipping:

- Read-only: inspect editor/project state and do not mutate anything.
- Write: create, modify, move, or delete assets, scene objects, settings, or
  packages.
- Destructive: delete data, overwrite files, remove packages, clear logs, or
  close user work.
- Execute: run scripts, reflection calls, tests, shell commands, or generated
  code.
- Network: send data outside the local Unity project or download remote data.

Security rules:

- Prefer resources for inspection and prompts for guidance. Do not use a tool
  for a read-only query.
- Keep write/destructive/execute/network capabilities disabled by default unless
  the user explicitly trusts the extension and task.
- Mark non-optional safety capabilities `Required = true` only when the user
  must not disable them. Required capabilities become policy locks in selection
  metadata.
- Use categories that reveal risk, such as `Package Manager`,
  `Script Execution / Tests`, or `Network`.
- For future custom executable tools, mirror standard MCP annotations in package
  docs and descriptor text: `readOnlyHint`, `destructiveHint`,
  `idempotentHint`, and `openWorldHint`. The current
  `ChievfxMcpToolDescriptor` does not expose these annotations directly.
- Treat every third-party extension as user-trusted local code. It runs inside
  the Unity Editor process.

## Token And Output Controls

Descriptor cost comes from the capability list sent to the client. Response
cost comes from actual tool/resource/prompt payloads.

Controls:

- Keep names and descriptions short but specific.
- Put large data behind resource templates and drill-down URIs.
- Cap result arrays and include `maxResults`, `totalCount`, and `truncated`.
- Prefer compact JSON-serializable dictionaries over full Unity object dumps.
- Avoid embedding large static guides in `StaticText`; link to a smaller status
  resource or prompt instead.
- Keep prompt arguments scalar. Prompt rendering rejects arrays and objects.
- The MCP server truncates resource text at its global resource cap, but
  extensions should still cap their own output before returning it.

Selection metadata includes descriptor previews, descriptor byte counts,
estimated tokens, read/get/call envelope previews, and response profiles. Use
the ChievFX MCP Tools, Resources, and Prompts windows to inspect those values.
If the extension manifest looks stale, trigger a Unity domain reload or call
`ChievfxMcpExtensionRegistry.ExportManifest()` from trusted editor code, then
call `reload_cursor_mcp` for `unity-mcp-chievfx` so Cursor reads fresh MCP
descriptors.

## Testing Checklist

Before review:

- Unity compiles the extension asmdef with only required dependencies present.
- Domain reload registers the extension exactly once.
- `Library/ChievfxMcpBridge/extension-capabilities.json` contains your
  extension id, source assembly, resource ids, template ids, and prompt names.
- `resources/list` and `prompts/list` include enabled capabilities after Cursor
  descriptor refresh.
- Disabled resources/templates/prompts fail cleanly and do not call into Unity.
- Required capabilities remain enabled after saving selection settings.
- Dynamic resources return bounded output and include cap/truncation metadata.
- Optional dependencies missing: status resource explains unavailable features
  and no compile errors occur.
- Optional dependencies installed: version define or reflection path exposes
  the additional resources.
- Cursor descriptor refresh or MCP server restart shows updated descriptor
  previews and token estimates.

## Minimal Sample

See `Assets/Editor/ChievfxMcpExtensions/SampleReadOnly/`. It compiles as
`Chievfx.Mcp.Extensions.SampleReadOnly`, registers one dynamic read-only JSON
resource, and registers one static prompt with a scalar argument.
