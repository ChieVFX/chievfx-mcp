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
id: resources-guide
uri: chievfx://resources/guide
name: ChievFX MCP resource guide
description: Static guide for ChievFX resource URIs, drill-down links, and encoding rules.
mimeType: text/plain
--- 

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
id: scene-opened
uri: chievfx://scene/opened
name: Opened scenes
description: Opened Unity scenes and their load/dirty/build state.
mimeType: text/plain
---

--- 
type: resource
id: scene-current-usage-counts
uri: chievfx://scene/current/usage/counts
name: Current scene asset usage counts
description: Asset usage totals for the current prefab stage or active scene, grouped by common asset type.
mimeType: text/plain
---

--- 
type: resource
id: scene-current-material-profile-summary
uri: chievfx://scene/current/material-profile/summary
name: Current scene material profile
description: Read-only material profile for the current prefab stage or active scene with exact shader/material counts and profiler memory estimates.
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
id: scene-current-go
uriTemplate: chievfx://scene/current/go/{goPath}
name: Current GameObject summary
description: Compact GameObject summary in the current prefab stage or active scene.
mimeType: text/plain
---

---
type: resourceTemplate
id: scene-current-component
uriTemplate: chievfx://scene/current/go/{goPath}/component/{componentKey}
name: Current component serialized values
description: Serialized values for one component in the current prefab stage or active scene.
mimeType: text/plain
---

---
type: resourceTemplate
id: scene-current-go-name-contains
uriTemplate: chievfx://scene/current/go/name-contains/{text}
name: Current GameObjects by name substring
description: Find current hierarchy GameObjects whose names contain literal percent-encoded text.
mimeType: text/plain
---

---
type: resourceTemplate
id: scene-current-go-name-pattern
uriTemplate: chievfx://scene/current/go/name-pattern/{pattern}
name: Current GameObjects by name wildcard
description: Find current hierarchy GameObjects by anchored * and ? wildcard pattern in one encoded segment.
mimeType: text/plain
---

---
type: resourceTemplate
id: scene-current-go-component
uriTemplate: chievfx://scene/current/go/component/{componentType}
name: Current GameObjects by component type
description: Find current hierarchy GameObjects by simple or full component type name.
mimeType: text/plain
---

---
type: resourceTemplate
id: scene-current-go-filter
uriTemplate: chievfx://scene/current/go/filter/{filterSpec}
name: Current GameObjects by compact filter
description: Find current hierarchy GameObjects with encoded name/component/inactive/case/limit filter text.
mimeType: text/plain
---

---
type: resourceTemplate
id: assets-name-contains
uriTemplate: chievfx://assets/name-contains/{text}
name: Assets by name substring
description: Find persisted project assets whose names match percent-encoded AssetDatabase name text. Defaults to area=assets.
mimeType: text/plain
---

---
type: resourceTemplate
id: assets-type
uriTemplate: chievfx://assets/type/{assetType}
name: Assets by type
description: Find persisted project assets by AssetDatabase type or supported alias such as material, texture, prefab, scene, or mesh.
mimeType: text/plain
---

---
type: resourceTemplate
id: assets-label
uriTemplate: chievfx://assets/label/{label}
name: Assets by label
description: Find persisted project assets by AssetDatabase label. Defaults to area=assets.
mimeType: text/plain
---

---
type: resourceTemplate
id: assets-filter
uriTemplate: chievfx://assets/filter/{filterSpec}
name: Assets by compact AssetDatabase filter
description: Find persisted assets with encoded name/type/label/area/folder/limit/subassets filter text.
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
id: scene-current-material-profile-shader
uriTemplate: chievfx://scene/current/material-profile/shader/{shaderKey}
name: Current material profile by shader
description: Drill into materials using one shader key from the current material profile summary.
mimeType: text/plain
---

---
type: resourceTemplate
id: scene-current-material-profile-material
uriTemplate: chievfx://scene/current/material-profile/material/{materialKey}
name: Current material profile material detail
description: Drill into one material key from the current material profile summary, including locations and texture links.
mimeType: text/plain
---

---
type: resourceTemplate
id: scene-current-usage-assets
uriTemplate: chievfx://scene/current/usage/assets/{assetType}
name: Current scene asset usage by type
description: Summarize current scene or prefab-stage references for material, mesh, texture, renderTexture, or all assets.
mimeType: text/plain
---

---
type: resourceTemplate
id: scene-current-usage-asset
uriTemplate: chievfx://scene/current/usage/asset/{guid}
name: Current scene asset usage by GUID
description: Drill into current scene or prefab-stage GameObject/component references to an asset GUID.
mimeType: text/plain
---

---
type: resourceTemplate
id: scene-current-usage-subasset
uriTemplate: chievfx://scene/current/usage/asset/{guid}/id/{localId}
name: Current scene subasset usage by GUID and local file identifier
description: Drill into current scene or prefab-stage references to one subasset local file identifier.
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

  Before drafting, read relevant ChievFX MCP resources: chievfx://editor/context, chievfx://resources/guide, matching material/shader/texture asset searches, and asset-detail resources for referenced GUIDs. Confirm the project really targets the Built-in Render Pipeline; if URP or HDRP packages/render pipeline assets are active, stop and recommend the matching prompt. Account for Unity version, graphics API/platform, shader model target, lighting path, instancing, SRP Batcher irrelevance for Built-in, keywords, passes, fallbacks, transparency, shadows, and feature support. Prefer compact ShaderLab/HLSL that compiles in Built-in. Call out assumptions, required material properties, unsupported features, and validation steps.
---

