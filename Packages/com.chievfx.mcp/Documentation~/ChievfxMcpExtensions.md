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
presets are defined in `Tools~/ChievfxMcp/chievfx_mcp_role_presets.json` and
custom presets are `ChievfxMcpToolRoleAsset` project assets. A role lists
category names plus optional explicit tool IDs; applying it enables matching
tools, disables other optional tools, and never disables required tools.

Create custom role assets with `Assets > Create > ChievFX > MCP Tool Role`, then edit
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

## Tool Result Conventions

An extension cannot ship a Python formatter, so a few reply fields carry the
shape core tools get from their own formatters. All are optional.

- `summary` (string): printed as the reply's first line, the way a core tool
  opens with its headline ("tapped Play at (0.5, 0.8)"); the other fields
  follow below it. In a `tool-batch` result each step starts with its tool's
  summary, so a batch reads as a log of what was done. `outputFormat:"json"`
  keeps it as an ordinary field.
- `waitFor` (string, or a list of strings: any matches): a log substring that
  marks the end of an action that finishes after the reply (a walk, a drive, a
  multi-step skill). The caller can pass `awaitMs` to the same call: the server
  takes the event cursor before calling Unity, waits for an event containing
  `waitFor`, and appends that event (or how to keep waiting) to the reply. The
  extension never sees `awaitMs`. Declare `awaitMs` (integer) in the tool's
  `InputSchema` so callers find it. `completionMarker` works the same way for
  marker beacons.
- `waitForFailed` (string, or a list of strings: any matches): a log substring
  that marks a failed end, e.g. `waitFor` + `"failed"`. When the awaited end
  event contains it, the call counts as failed although its reply said ok: a
  `tool-batch` step stops the batch there, as on a thrown error. So does a
  step whose end did not come within its `awaitMs`: the next step would act
  over an action that is still running.
- Without `awaitMs` the server adds `next` to the reply: the ready
  `events-wait` call for that end, printed as the reply's last line
  (`next: events-wait {"contains":...,"sinceEventId":...,"timeoutMs":...}`).
  Its cursor is the reply's `sinceEventId` when the extension sets one,
  otherwise the journal's id from just before `ToolRunner` ran, which the core
  puts in the reply as `eventCursorBefore`: an end logged while the tool was
  still replying stays inside the wait. An extension that needs the cursor at
  another moment reads `ChievfxMcpExtensionRegistry.CurrentEventId()`.
  `waitTimeoutMs` (integer) sets the wait's `timeoutMs` when the action
  lasts longer than the default 30 s (a drive across the map). An extension
  that builds its own `next` keeps it. A `watch` field (say, a shell line that
  waits on the extension's own append-only log in the background) passes
  through as is: the core journal is a rolling window that also holds older
  events with the same text, so the core does not build one.

Failures: throw from `ToolRunner` (the message is the error the caller sees),
or reply with `ok:false` (with `error` or `errors` saying why). Both stop a
`tool-batch`; any other reply counts as success.

Argument descriptions in `InputSchema` reach the client as written: the core
descriptors list only the argument names of an extension tool.

Before a call reaches any tool, the server renames arguments a caller guessed
in another form: the same name in another case or with separators
(`car_name`, `CarName` for `carName`) when it matches exactly one property,
and names a property lists under `x-aliases` in its schema
(`"grep": {"type": "string", "x-aliases": ["text", "query"]}`). A rename
never overwrites an argument the call already set, and the reply ends with
`Arguments taken under the tool's names: car_name→carName` so the caller
learns the right name. Declare the synonyms models reach for under
`x-aliases` instead of adding them as properties: the core descriptors list
only property names, so aliases cost nothing there. Conversions that change
the value (seconds to milliseconds, a point to x/y/z) stay with the extension;
it lists them in the reply as `argumentsRenamed` (`["position→x,y,z"]`), and
the server adds them to the same line instead of showing the field.

`additionalProperties: true` in a tool's `InputSchema` tells the core the
extension judges argument names itself (accepts aliases, or rejects unknown
ones with its own error), so the core does not add its "Unrecognized argument
... expect no effect" warning. Leave it out to get that warning for free.

## Shared Editor Services

Public static classes in `Chievfx.Mcp.Editor` an extension uses instead of
building its own copy:

- `ChievfxMcpFrameRecorder` - the Play Mode black box behind
  `screenshot-game-view storyboard:true`: a small Game view frame every 0.5 s,
  the last minute kept. `EnsureRunning()` starts it, `Mark(note)` captures a
  labelled frame at the next tick (labelled frames always reach the
  storyboard), `StateProvider` adds one line of state to every frame,
  `FrameCaptured` hands each frame to checks of the extension's own (read the
  texture inside the handler: it is reused), `AddNoteToLatestFrame(note)`
  labels the frame such a check looked at, `SetInterval(seconds)` changes the
  pace, `Save(reason)` writes the storyboard to `Logs/chievfx_frames` (a frame
  marked just before it is taken first) and returns its full path. Recording
  stops when Play Mode ends.
- `ChievfxMcpEditorHealth.Build()` - the editor health block
  `bridge-get-status` shows (reserved memory, Play fps, update subscribers and
  render textures left by scripts loaded from bytes), to fold into an
  extension's own state reply.
- `ChievfxMcpExtensionRegistry.CurrentEventId()` - the event journal's newest
  id, for a `sinceEventId` taken at another moment than the tool call.

## Manifest Fields

At runtime, Unity serves extension capabilities to the MCP server through the
bridge command `extension-capabilities-get`. The MCP server merges that payload
into `tools/list`, `resources/list`, `resources/templates/list`, and
`prompts/list`.

`ChievfxMcpExtensionRegistry.GetManifest()` is the in-editor source of truth.
`ExportManifest(path)` remains available for tests and manual inspection. Unity
selection windows refresh
`Library/ChievfxMcpBridge/extension-capabilities.snapshot.json` immediately
before spawning Python metadata CLIs so the editor never deadlocks on a bridge
round-trip.

Manifest fields:

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
`ChievfxMcpExtensionRegistry.GetManifest()` from trusted editor code, then
call `reload_cursor_mcp` for `unity-mcp-chievfx` so Cursor reads fresh MCP
descriptors.

## Testing Checklist

Before review:

- Unity compiles the extension asmdef with only required dependencies present.
- Domain reload registers the extension exactly once.
- `extension-capabilities-get` through the Unity bridge returns your
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
