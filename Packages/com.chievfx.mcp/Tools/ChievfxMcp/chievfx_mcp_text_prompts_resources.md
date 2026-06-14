## ChievFX MCP text catalogs
This file provides lazy-loaded catalog data for:
1) core resources + resource templates (id/uri/name/description/mimeType)
2) core prompts (name/title/description/category/arguments + messages[0].role/text)

Rules:
- This is a simple line-oriented format parsed by `chievfx_mcp_server.py`.
- Each record is delimited with `---`.
- Within a record, each field is `key: value`.
- For multi-line prompt message text, use fenced blocks:
  - `text: |` then following indented (or raw) lines until the next `---` delimiter.

If you add new prompt/resource records, keep `id`/`name` unique.

### RESOURCE
--- 
type: resource
id: editor-context
uri: chievfx://editor/context
name: Unity editor context
description: Compact Unity editor, play mode, active scene, prefab stage, and selection context.
mimeType: text/plain
---

--- 
type: resource
id: scenes-opened
uri: chievfx://scene/opened
name: Opened scenes
description: Opened Unity scenes and their load/dirty/build state.
mimeType: text/plain
---

--- 
type: resource
id: scene-all-usage-counts
uri: chievfx://scene/all/usage/counts
name: Asset usage counts across loaded scenes
description: Asset usage totals across all loaded scenes, grouped by common asset type.
mimeType: text/plain
---

--- 
type: resource
id: scene-all-material-profile-summary
uri: chievfx://scene/all/material-profile/summary
name: Material profile across loaded scenes
description: Read-only material profile across all loaded scenes with exact shader/material counts and profiler memory estimates.
mimeType: text/plain
---

### RESOURCE TEMPLATE
---
type: resourceTemplate
id: scene-go
uriTemplate: chievfx://scene/{scenePath}/go/{goPath}
name: GameObject summary by scene and path
description: Compact GameObject summary. Percent-encode scene path and hierarchy path as full URI segments.
mimeType: text/plain
---

---
type: resourceTemplate
id: scene-component
uriTemplate: chievfx://scene/{scenePath}/go/{goPath}/component/{componentKey}
name: Component serialized values by scene, GameObject path, and component key
description: Serialized values for one component. Percent-encode every dynamic value as a full URI segment.
mimeType: text/plain
---

---
type: resourceTemplate
id: scene-all-go
uriTemplate: chievfx://scene/all/go/{goPath}
name: GameObject summary across loaded scenes
description: Compact GameObject summary across all loaded scenes. Prefer this default when scene scope is unknown.
mimeType: text/plain
---

---
type: resourceTemplate
id: scene-all-component
uriTemplate: chievfx://scene/all/go/{goPath}/component/{componentKey}
name: Component serialized values across loaded scenes
description: Serialized values for one component across all loaded scenes. Prefer this default when scene scope is unknown.
mimeType: text/plain
---

---
type: resourceTemplate
id: asset-detail
uriTemplate: chievfx://asset/{guid}
name: Asset detail by GUID
description: Load one persisted main asset by GUID with AssetImporter metadata and subasset drill-down hints.
mimeType: text/plain
---

---
type: resourceTemplate
id: asset-subasset-detail
uriTemplate: chievfx://asset/{guid}/id/{localId}
name: Subasset detail by GUID and long local file identifier
description: Load one persisted subasset by GUID and long local file identifier.
mimeType: text/plain
---

---
type: resourceTemplate
id: scene-all-material-profile-shader
uriTemplate: chievfx://scene/all/material-profile/shader/{shaderKey}
name: Material profile by shader across loaded scenes
description: Drill into materials using one shader key from the material profile summary across loaded scenes.
mimeType: text/plain
---

---
type: resourceTemplate
id: scene-all-material-profile-material
uriTemplate: chievfx://scene/all/material-profile/material/{materialKey}
name: Material profile material detail across loaded scenes
description: Drill into one material key from the material profile summary across loaded scenes, including locations and texture links.
mimeType: text/plain
---

---
type: resourceTemplate
id: scene-all-usage-assets
uriTemplate: chievfx://scene/all/usage/assets/{assetType}
name: Asset usage by type across loaded scenes
description: Summarize loaded-scene references for material, mesh, texture, renderTexture, or all assets.
mimeType: text/plain
---

---
type: resourceTemplate
id: scene-all-usage-asset
uriTemplate: chievfx://scene/all/usage/asset/{guid}
name: Asset usage by GUID across loaded scenes
description: Drill into loaded-scene GameObject/component references to an asset GUID.
mimeType: text/plain
---

---
type: resourceTemplate
id: scene-all-usage-subasset
uriTemplate: chievfx://scene/all/usage/asset/{guid}/id/{localId}
name: Subasset usage by GUID and local file identifier across loaded scenes
description: Drill into loaded-scene references to one subasset local file identifier.
mimeType: text/plain
---

### PROMPT (core static only)
---
type: prompt
name: unity-scene-review
title: Review current Unity scene work
description: Static prompt for reviewing a Unity scene or prefab against a requested goal.
category: Scene
arguments: [{"name":"goal","required":true},{"name":"focus","required":false}]
messageRole: user
text: |
  Review current Unity scene or prefab work for this goal:
  {goal}

  Focus area: {focus}

  Use available ChievFX MCP resources/tools for evidence. Check hierarchy, selected objects, asset references, console warnings, and likely missing validation. Return concise findings with exact Unity objects/assets when possible.
---

---
type: prompt
name: unity-shader-built-in-draft
title: Draft Built-in Render Pipeline shader code
description: Static prompt for drafting Unity Built-in Render Pipeline shader code from project context.
category: Shader
arguments: [{"name":"goal","required":true},{"name":"shaderName","required":false},{"name":"context","required":false}]
messageRole: user
text: |
  Draft Unity Built-in Render Pipeline shader code for this goal:
  {goal}

  Shader name or target asset: {shaderName}
  Extra context: {context}

  Before drafting, read relevant ChievFX MCP resources: chievfx://editor/context, matching material/shader/texture asset searches, and asset-detail resources for referenced GUIDs. Confirm the project really targets the Built-in Render Pipeline; if URP or HDRP packages/render pipeline assets are active, stop and recommend the matching prompt. Account for Unity version, graphics API/platform, shader model target, lighting path, instancing, SRP Batcher irrelevance for Built-in, keywords, passes, fallbacks, transparency, shadows, and feature support. Prefer compact ShaderLab/HLSL that compiles in Built-in. Call out assumptions, required material properties, unsupported features, and validation steps.
---

