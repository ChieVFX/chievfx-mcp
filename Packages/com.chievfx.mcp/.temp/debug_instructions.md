# ChievFX MCP debug instructions

Generated at (UTC): 2026-06-10T06:01:31Z
Project root: C:\_code_\chievfx-mcp\Packages\com.chievfx.mcp
Trigger: remove-guide

## Selection snapshot

- Enabled tools: 50
- Enabled resources: 4
- Enabled resource templates: 19
- Enabled prompts: 0
- Tool selection: `C:\_code_\chievfx-mcp\Packages\com.chievfx.mcp\UserSettings\ChievfxMcpToolSelection.json`
- Resource selection: `C:\_code_\chievfx-mcp\Packages\com.chievfx.mcp\UserSettings\ChievfxMcpResourceSelection.json`
- Prompt selection: `C:\_code_\chievfx-mcp\Packages\com.chievfx.mcp\UserSettings\ChievfxMcpPromptSelection.json`

## initialize.instructions

Exact payload returned from MCP `initialize.instructions`.

```text
ChievFX Unity MCP is project-local. Prefer enabled ChievFX MCP tools/resources when they provide live Unity evidence.
  Before calling a ChievFX MCP tool, inspect its descriptor/schema from Cursor's MCP tool folder and use exact tool names.
bridge-get-status: inspect Unity bridge heartbeat, compile/import busy state, recent operations, and event-wait liveness before longer orchestration.
console-get-logs: filter console severity with levels, not contains. Default levels are Error, Exception, Assert, Warning. Exact contains tokens error, exception, warning, or issue are reinterpreted as severity filters so Assert rows like "Map must be contained in state" still match.
events-check-since: recover after waits/timeouts using sinceEventId and sinceTimestampUtc from prior wait results.
events-wait: wait for specific Unity events or markers; timeout is a normal branch, not failure. Default cursor is lastEventId (future-only), so boot/early logs that fire during the triggering op (Play-mode enter, recompile, script-execute) are skipped. For such logs capture sinceEventId from the trigger result (editor-playmode-set returns eventCursorBefore) or bridge-get-status BEFORE the trigger, or use includeRecentMs with no sinceEventId. Debug.Log lines are source:log; pass source:"log" for clarity. Prefer ASCII-only contains substrings (e.g. "Turn 1", "Player Turn") or a marker: filter over Unicode punctuation (em dash —, smart quotes) in log text, since encoding mismatches can break substring matches. On timeout, inspect result.diagnostic: matchBelowCursor means it fired below your cursor (retry from earlier cursor), nonAsciiContains means your filter had non-ASCII that may have been mangled (retry ASCII-only), possiblyTruncated means it was evicted (verify via console-get-logs contains).
chievfx://editor/context: compact current Unity editor, play mode, active scene, prefab stage, and selection context.
Enabled ChievFX MCP descriptors (compact instruction form):
Tools:
- asset-create: Creates Unity object assets under Assets/: prefab, or a ScriptableObject asset from a non-abstract ScriptableObject inheritor type name. Scripts, shaders, uxml, uss, json, and other text files can be created by the agent directly without using this tool. args=(path:str, type:str)
- asset-delete: Deletes one or more Unity project assets or folders by path. args=(path?:str, paths?:str[])
- assets-refresh: Imports non-script Unity assets by path, folder/type, or path substring. Use recompile for C# scripts. args=(path?:str, folder?:str, pathContains?:str, type?:str, extensions?:str|str[])
- bridge-get-operation: Returns the full bridge operation record for a specific opId. args=(opId?:str, operationId?:str)
- bridge-get-status: Slim ChievFX bridge health snapshot. Pass verbose=true for full diagnostics.
- console-clear-logs: Clears ChievFX MCP log cache and Unity developer console.
- console-get-logs: Recent Unity Console entries (first-line only, no time). Each row has an id; duplicates collapse via stack=true. Filter severity with levels (default: Error, Exception, Assert, Warning). Exact contains values error/exception/warning/issue are treated as severity filters, not message substring search. args=(maxEntries?:int, levels?:Error|Assert|Warning|Log|Exception|ConsoleErrors|ConsoleIssues[], contains?:str)
- console-get-logs-single: Fetches one Unity Console entry by id (from console-get-logs) with full message and stack trace. args=(id:str)
- editor-window-focus: Focuses/selects an existing Unity EditorWindow tab. args=(instanceId?:int, typeName?:str, titleContains?:str, focused?:bool, mouseOver?:bool)
- editor-window-list: Lists open Unity EditorWindow instances and docked tabs. args=(typeName?:str, titleContains?:str, maxResults?:int)
- editor-window-open: Opens a Unity EditorWindow by typeName or menuPath. args=(typeName?:str, menuPath?:str, focus?:bool, title?:str)
- events-check-since: Checks bridge events since a wait. args=(sinceEventId:int, sinceTimestampUtc:str, source?:log|bridge|editor|structured, contains?:str, marker?:str)
- events-wait: Long-polls bridge events. Subagents must be write-capable. args=(sinceEventId?:int, timeoutMs?:int, source?:log|bridge|editor|structured, contains?:str)
- folder-ensure: Creates missing Unity project folders for a path starting with Assets/. args=(path:str)
- recompile: Refreshes scripts, requests Unity script compilation, and returns only after Unity is idle again. args=(timeoutMs?:int)
- reflection-method-call: Calls one loaded C# method. Instance calls need targetObject.value. args=(filter:obj, targetObject?:{value}, timeoutMs?:int)
- reflection-method-find: Finds loaded C# methods. Exact matching is default; use match:'contains' for fuzzy discovery. args=(filter:obj, match?:exact|contains, maxResults?:int, page?:int)
- reflection-method-find-single: Returns full info for one reflection-method-find result selected by page-local index. args=(filter:obj, match?:exact|contains, maxResults?:int, page?:int, index:int)
- screenshot-camera: Captures a screenshot from a Unity camera. args=(cameraPath?:str, cameraInstanceId?:int, path?:str, instanceId?:int, cameraName?:str, width?:int, height?:int)
- screenshot-editor-window: Captures Unity EditorWindow. args=(target?:focused|mouseOver|{instanceId,typeName,titleContains,menuPath}, openIfMissing?:bool)
- screenshot-game-view: Captures a screenshot from the Unity Editor Game View. args=(maxDimension?:int)
- script-execute: Compiles and runs caller-provided C# without writing project files. args=(csharpCode:str, className?:str, methodName?:str, timeoutMs?:int)
- tests-run: Runs Unity tests with focused filters. args=(testMode?:EditMode|PlayMode, testAssembly?:str, testNamespace?:str, testClass?:str, testMethod?:str, timeoutMs?:int)
- tool-batch: Runs one enabled tool for many item argument objects. One tool only, no mixed operations. args=(tool:str, items:obj[], stopOnError?:bool)
Resources:
- chievfx://editor/context: Compact Unity editor, play mode, active scene, prefab stage, and selection context.
Extra API capabilities (batched by category to save tokens; read the linked chievfx://categories resource for full tool/resource details):
- Asset (13 resources): Persisted AssetDatabase search and asset drill-down resources. -> chievfx://categories/asset
- Autonomous (6 tools): Optional discovery and enablement helpers for agents to inspect and change optional MCP tool exposure. -> chievfx://categories/autonomous
- GameObject (10 tools, 8 resources): Optional GameObject hierarchy, lookup, creation, metadata/component mutation, transform, parenting, and duplication tools. -> chievfx://categories/gameobject
- Prefab (5 tools): Optional prefab-stage and prefab asset workflows. -> chievfx://categories/prefab
- Scene (5 tools, 1 resources): Optional scene inventory and open/save control. -> chievfx://categories/scene
```
