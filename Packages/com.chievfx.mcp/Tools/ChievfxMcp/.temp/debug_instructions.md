# ChievFX MCP debug instructions

Generated at (UTC): 2026-06-07T07:37:36Z
Project root: C:\Users\chiev\AppData\Local\Temp\tmpb7umespt
Trigger: tool-selection-save

## Selection snapshot

- Enabled tools: 50
- Enabled resources: 5
- Enabled resource templates: 19
- Enabled prompts: 0
- Tool selection: `C:\Users\chiev\AppData\Local\Temp\tmpb7umespt\UserSettings\ChievfxMcpToolSelection.json`
- Resource selection: `C:\_code_\chievfx-mcp\Packages\com.chievfx.mcp\Tools\ChievfxMcp\UserSettings\ChievfxMcpResourceSelection.json`
- Prompt selection: `C:\_code_\chievfx-mcp\Packages\com.chievfx.mcp\Tools\ChievfxMcp\UserSettings\ChievfxMcpPromptSelection.json`

## initialize.instructions

Exact payload returned from MCP `initialize.instructions`.

```text
ChievFX Unity MCP is project-local. Prefer enabled ChievFX MCP tools/resources when they provide live Unity evidence.
  Before calling a ChievFX MCP tool, inspect its descriptor/schema from Cursor's MCP tool folder and use exact tool names.
bridge-get-status: inspect Unity bridge heartbeat, compile/import busy state, recent operations, and event-wait liveness before longer orchestration.
events-check-since: recover after waits/timeouts using sinceEventId and sinceTimestampUtc from prior wait results.
events-wait: wait for specific Unity events or markers; timeout is a normal branch, not failure.
chievfx://editor/context: compact current Unity editor, play mode, active scene, prefab stage, and selection context.
chievfx://resources/guide: URI guide for ChievFX resources, drill-down links, and encoding rules.
Enabled ChievFX MCP descriptors (compact instruction form):
Tools:
- asset-create: Creates Unity object assets under Assets/: prefab, or a ScriptableObject asset from a non-abstract ScriptableObject inheritor type name. Scripts, shaders, uxml, uss, json, and other text files can be created by the agent directly without using this tool. args=(path:str, type:str)
- asset-delete: Deletes one or more Unity project assets or folders by path. args=(path?:str, paths?:str[])
- assets-refresh: Imports non-script Unity assets by path, folder/type, or path substring. Use recompile for C# scripts. args=(path?:str, folder?:str, pathContains?:str, type?:str, extensions?:str|str[])
- bridge-get-operation: Returns the full bridge operation record for a specific opId. args=(opId?:str, operationId?:str)
- bridge-get-status: Slim ChievFX bridge health snapshot. Pass verbose=true for full diagnostics.
- console-clear-logs: Clears ChievFX MCP log cache and Unity developer console.
- console-get-logs: Recent Unity Console entries (first-line only, no time). Each row has an id; duplicates collapse via stack=true. args=(maxEntries?:int, levels?:Error|Assert|Warning|Log|Exception[], contains?:str)
- console-get-logs-single: Fetches one Unity Console entry by id (from console-get-logs) with full message and stack trace. args=(id:str)
- editor-window-focus: Focuses/selects an existing Unity EditorWindow tab. args=(instanceId?:int, typeName?:str, titleContains?:str, focused?:bool, mouseOver?:bool)
- editor-window-list: Lists open Unity EditorWindow instances and docked tabs. args=(typeName?:str, titleContains?:str, maxResults?:int)
- editor-window-open: Opens a Unity EditorWindow by typeName or menuPath. args=(typeName?:str, menuPath?:str, focus?:bool, title?:str)
- events-check-since: Checks bridge events since a wait. args=(sinceEventId:int, sinceTimestampUtc:str, source?:log|bridge|editor|structured, contains?:str, marker?:str)
- events-wait: Long-polls bridge events. Subagents must be write-capable. args=(sinceEventId?:int, timeoutMs?:int, source?:log|bridge|editor|structured, contains?:str)
- folder-ensure: Creates missing Unity project folders for a path starting with Assets/. args=(path:str)
- gameobject-component-get: Returns one component on a GameObject with inspector-visible serialized state by default. args=(path?:str, instanceId?:int, componentType:str, componentIndex?:int, includeSerializedData?:bool, isDebug?:bool)
- gameobject-component-update-or-create: Updates a component by serialized property path, creating it when absent by default. args=(path?:str, instanceId?:int, componentType:str, componentIndex?:int, isCreateIfNone?:bool, writeNonSerialized?:bool, properties?:obj, json?:any)
- gameobject-create: Creates one GameObject at a hierarchy path in the active scene or current prefab stage. args=(path:str)
- gameobject-duplicate: Duplicates a GameObject hierarchy/subtree. By default the duplicate is a sibling of the source; provide parentPath/parentInstanceId to clone the branch under another parent. args=(path?:str, instanceId?:int, newName?:str, parentPath?:str|null, parentInstanceId?:int|null, includeChildren?:bool, count?:int, position?:any, positionOffset?:any, rotationEuler?:any, rotationEulerOffset?:any, euler?:any, eulerOffset?:any, scale?:any, scaleOffset?:any)
- gameobject-find: Finds GameObjects by filters, or returns detail data when includeDetails/includeComponents is set. args=(path?:str, name?:str, namePattern?:str, componentType?:str, instanceId?:int, includeInactive?:bool, includeDetails?:bool, includeComponents?:bool, maxResults?:int)
- gameobject-hierarchy: Returns a compact GameObject hierarchy for the active scene or current prefab stage. args=(path?:str, maxDepth?:int, includeComponents?:bool, maxResults?:int)
- gameobject-set-parent: Moves one GameObject under another GameObject, or unparents it to scene root. args=(path?:str, instanceId?:int, parentPath?:str|null, parentInstanceId?:int|null, worldPositionStays?:bool)
- gameobject-transform-get: Returns one GameObject Transform in local or world space. args=(path?:str, instanceId?:int, isWorld?:bool)
- gameobject-transform-update: Updates one GameObject Transform in local or world space. args=(path?:str, instanceId?:int, position?:obj, rotationEuler?:obj, scale?:obj, isWorld?:bool)
- gameobject-update: Updates GameObject name, tag, layer, active/static state, static flags, or light bake flags. args=(path?:str, instanceId?:int, newName?:str, tag?:str, layer?:int|str, isStatic?:bool, activeSelf?:bool, enabled?:bool, staticFlags?:any, lightBakeFlags?:obj)
- prefab-close: Closes the current prefab stage, optionally saving dirty contents first. args=(saveBeforeClose?:bool)
- prefab-create: Creates or overwrites a prefab asset from one scene or prefab-stage GameObject. By default only the asset is saved; set connectGameObjectToPrefab=true to keep the source scene object linked as a prefab instance. args=(sourcePath?:str, sourceInstanceId?:int, prefabPath:str, overwrite?:bool, connectGameObjectToPrefab?:bool)
- prefab-instantiate: Instantiates a prefab asset into current scene or prefab stage. args=(prefabPath:str, parentPath?:str, parentInstanceId?:int, name?:str, position?:obj, rotationEuler?:obj, scale?:obj)
- prefab-open: Opens a prefab asset in Unity Prefab Mode, refusing to discard dirty prefab stages. args=(prefabPath:str)
- prefab-save: Saves the currently open prefab stage.
- recompile: Refreshes scripts, requests Unity script compilation, and returns only after Unity is idle again. args=(timeoutMs?:int)
- reflection-method-call: Calls one loaded C# method. Instance calls need targetObject.value. args=(filter:obj, targetObject?:{value}, timeoutMs?:int)
- reflection-method-find: Finds loaded C# methods. Exact matching is default; use match:'contains' for fuzzy discovery. args=(filter:obj, match?:exact|contains, maxResults?:int, page?:int)
- reflection-method-find-single: Returns full info for one reflection-method-find result selected by page-local index. args=(filter:obj, match?:exact|contains, maxResults?:int, page?:int, index:int)
- scene-create: Creates an empty Unity scene asset at path without leaving it open. args=(path:str)
- scene-list-available: Finds scene assets available in the Unity project. args=(filter?:str, searchInFolders?:str[], maxResults?:int)
- scene-list-opened: Lists scenes currently open in the Unity editor.
- scene-open: Opens a Unity scene asset, preserving dirty scenes unless explicitly saved first. args=(scenePath:str, mode?:Single|Additive, saveDirtyScenes?:bool)
- scene-save: Saves an opened Unity scene, defaulting to the active scene. args=(openedSceneName?:str, path?:str)
- screenshot-camera: Captures a screenshot from a Unity camera. args=(cameraPath?:str, cameraInstanceId?:int, path?:str, instanceId?:int, cameraName?:str, width?:int, height?:int)
- screenshot-editor-window: Captures Unity EditorWindow. args=(target?:focused|mouseOver|{instanceId,typeName,titleContains,menuPath}, openIfMissing?:bool)
- screenshot-game-view: Captures a screenshot from the Unity Editor Game View. args=(maxDimension?:int)
- script-execute: Compiles and runs caller-provided C# without writing project files. args=(csharpCode:str, className?:str, methodName?:str, timeoutMs?:int)
- tests-run: Runs Unity tests with focused filters. args=(testMode?:EditMode|PlayMode, testAssembly?:str, testNamespace?:str, testClass?:str, testMethod?:str, timeoutMs?:int)
- tool-batch: Runs one enabled tool for many item argument objects. One tool only, no mixed operations. args=(tool:str, items:obj[], stopOnError?:bool)
- tools-get-role: Lists tools enabled by a ChievFX MCP role index, grouped by category. args=(roleIndex:int)
- tools-get-roles: Lists ChievFX MCP role titles and descriptions.
- tools-list-categories: Lists tool categories with enabled/total counts and short descriptions. args=(includeDisabled?:bool)
- tools-list-category: Lists tools in one category with argument names and short descriptions. args=(category:str, includeDisabled?:bool)
- tools-set-enabled-state: Enables/disables optional ChievFX MCP tools or categories. After success, call reload_cursor_mcp for this server before using changed tool descriptors. args=(enabled:bool, category?:str, categories?:str[], tool?:str, tools?:str[])
- tools-set-role: Applies a built-in/custom ChievFX MCP tool role preset. After success, call reload_cursor_mcp for this server before using changed tool descriptors. args=(role?:str, roleId?:str, customAssetPath?:str, roleIndex?:int)
Resources:
- chievfx://editor/context: Compact Unity editor, play mode, active scene, prefab stage, and selection context.
- chievfx://resources/guide: Static guide for ChievFX resource URIs, drill-down links, and encoding rules.
- chievfx://scene/current/material-profile/summary: Read-only material profile for the current prefab stage or active scene with exact shader/material counts and profiler memory estimates.
- chievfx://scene/current/usage/counts: Asset usage totals for the current prefab stage or active scene, grouped by common asset type.
- chievfx://scene/opened: Opened Unity scenes and their load/dirty/build state.
Resource templates:
- chievfx://asset/{guid}: Load one persisted main asset by GUID with AssetImporter metadata and subasset drill-down hints.
- chievfx://asset/{guid}/id/{localId}: Load one persisted subasset by GUID and long local file identifier.
- chievfx://assets/filter/{filterSpec}: Find persisted assets with encoded name/type/label/area/folder/limit/subassets filter text.
- chievfx://assets/label/{label}: Find persisted project assets by AssetDatabase label. Defaults to area=assets.
- chievfx://assets/name-contains/{text}: Find persisted project assets whose names match percent-encoded AssetDatabase name text. Defaults to area=assets.
- chievfx://assets/type/{assetType}: Find persisted project assets by AssetDatabase type or supported alias such as material, texture, prefab, scene, or mesh.
- chievfx://scene/current/go/component/{componentType}: Find current hierarchy GameObjects by simple or full component type name.
- chievfx://scene/current/go/filter/{filterSpec}: Find current hierarchy GameObjects with encoded name/component/inactive/case/limit filter text.
- chievfx://scene/current/go/name-contains/{text}: Find current hierarchy GameObjects whose names contain literal percent-encoded text.
- chievfx://scene/current/go/name-pattern/{pattern}: Find current hierarchy GameObjects by anchored * and ? wildcard pattern in one encoded segment.
- chievfx://scene/current/go/{goPath}: Compact GameObject summary in the current prefab stage or active scene.
- chievfx://scene/current/go/{goPath}/component/{componentKey}: Serialized values for one component in the current prefab stage or active scene.
- chievfx://scene/current/material-profile/material/{materialKey}: Drill into one material key from the current material profile summary, including locations and texture links.
- chievfx://scene/current/material-profile/shader/{shaderKey}: Drill into materials using one shader key from the current material profile summary.
- chievfx://scene/current/usage/asset/{guid}: Drill into current scene or prefab-stage GameObject/component references to an asset GUID.
- chievfx://scene/current/usage/asset/{guid}/id/{localId}: Drill into current scene or prefab-stage references to one subasset local file identifier.
- chievfx://scene/current/usage/assets/{assetType}: Summarize current scene or prefab-stage references for material, mesh, texture, renderTexture, or all assets.
- chievfx://scene/{scenePath}/go/{goPath}: Compact GameObject summary. Percent-encode scene path and hierarchy path as full URI segments.
- chievfx://scene/{scenePath}/go/{goPath}/component/{componentKey}: Serialized values for one component. Percent-encode every dynamic value as a full URI segment.
```

## chievfx://resources/guide

Exact body returned from `resources/read` for the guide resource.

```text
ChievFX MCP resources v2
Guide covers enabled v2 GameObject, AssetDatabase, and scene-usage resources for this project.
Static resource and template lists match resources/list and resources/templates/list for the current selection.

Static resources:
- chievfx://editor/context: Compact Unity editor, play mode, active scene, prefab stage, and selection context.
- chievfx://resources/guide: Static guide for ChievFX resource URIs, drill-down links, and encoding rules.
- chievfx://scene/current/material-profile/summary: Read-only material profile for the current prefab stage or active scene with exact shader/material counts and profiler memory estimates.
- chievfx://scene/current/usage/counts: Asset usage totals for the current prefab stage or active scene, grouped by common asset type.
- chievfx://scene/opened: Opened Unity scenes and their load/dirty/build state.

Templates:
- chievfx://asset/{guid}: Load one persisted main asset by GUID with AssetImporter metadata and subasset drill-down hints.
- chievfx://asset/{guid}/id/{localId}: Load one persisted subasset by GUID and long local file identifier.
- chievfx://assets/filter/{filterSpec}: Find persisted assets with encoded name/type/label/area/folder/limit/subassets filter text.
- chievfx://assets/label/{label}: Find persisted project assets by AssetDatabase label. Defaults to area=assets.
- chievfx://assets/name-contains/{text}: Find persisted project assets whose names match percent-encoded AssetDatabase name text. Defaults to area=assets.
- chievfx://assets/type/{assetType}: Find persisted project assets by AssetDatabase type or supported alias such as material, texture, prefab, scene, or mesh.
- chievfx://scene/current/go/component/{componentType}: Find current hierarchy GameObjects by simple or full component type name.
- chievfx://scene/current/go/filter/{filterSpec}: Find current hierarchy GameObjects with encoded name/component/inactive/case/limit filter text.
- chievfx://scene/current/go/name-contains/{text}: Find current hierarchy GameObjects whose names contain literal percent-encoded text.
- chievfx://scene/current/go/name-pattern/{pattern}: Find current hierarchy GameObjects by anchored * and ? wildcard pattern in one encoded segment.
- chievfx://scene/current/go/{goPath}: Compact GameObject summary in the current prefab stage or active scene.
- chievfx://scene/current/go/{goPath}/component/{componentKey}: Serialized values for one component in the current prefab stage or active scene.
- chievfx://scene/current/material-profile/material/{materialKey}: Drill into one material key from the current material profile summary, including locations and texture links.
- chievfx://scene/current/material-profile/shader/{shaderKey}: Drill into materials using one shader key from the current material profile summary.
- chievfx://scene/current/usage/asset/{guid}: Drill into current scene or prefab-stage GameObject/component references to an asset GUID.
- chievfx://scene/current/usage/asset/{guid}/id/{localId}: Drill into current scene or prefab-stage references to one subasset local file identifier.
- chievfx://scene/current/usage/assets/{assetType}: Summarize current scene or prefab-stage references for material, mesh, texture, renderTexture, or all assets.
- chievfx://scene/{scenePath}/go/{goPath}: Compact GameObject summary. Percent-encode scene path and hierarchy path as full URI segments.
- chievfx://scene/{scenePath}/go/{goPath}/component/{componentKey}: Serialized values for one component. Percent-encode every dynamic value as a full URI segment.

Encode every scene path, GameObject hierarchy path, component key, and asset filterSpec as one URI segment.
Use percent-encoding with no safe slash: quote(value, safe='').
GameObject paths keep ChievFX grammar: / separator, \/ literal slash, \\ literal backslash, [n] duplicate suffix.
Component keys use simple class names. Duplicate simple names are suffixed 1-based, e.g. BoxCollider.1.
Asset filterSpec uses semicolon key=value clauses: name, type, label, area, folder, limit, subassets.
Asset resources cover persisted AssetDatabase project/package assets, not runtime-only objects.
Current usage resources cover loaded current scene or prefab stage references; runtime-only and built-in objects have no asset GUID.
Material profile resources report exact material/reference counts separately from optional Profiler.GetRuntimeMemorySizeLong estimates.

Outputs are compact text/plain TOON with readAt metadata, drill-down URIs, truncation flags, and hard caps.
```
