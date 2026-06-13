# This file is loaded by chievfx_mcp_server.py into its module namespace.
# Keep this part focused and below 1000 lines.

RESOURCES: list[dict[str, Any]] = [
    {
        "id": "editor-context",
        "uri": "chievfx://editor/context",
        "name": "Unity editor context",
        "description": "Compact Unity editor, play mode, active scene, prefab stage, and selection context.",
        "mimeType": RESOURCE_MIME_TYPE,
    },
    {
        "id": "scenes-opened",
        "uri": "chievfx://scene/opened",
        "name": "Opened scenes",
        "description": "Opened Unity scenes and their load/dirty/build state.",
        "mimeType": RESOURCE_MIME_TYPE,
    },
    {
        "id": "scene-current-usage-counts",
        "uri": "chievfx://scene/current/usage/counts",
        "name": "Current scene asset usage counts",
        "description": "Asset usage totals for the current prefab stage or active scene, grouped by common asset type.",
        "mimeType": RESOURCE_MIME_TYPE,
    },
    {
        "id": "scene-current-material-profile-summary",
        "uri": "chievfx://scene/current/material-profile/summary",
        "name": "Current scene material profile",
        "description": "Read-only material profile for the current prefab stage or active scene with exact shader/material counts and profiler memory estimates.",
        "mimeType": RESOURCE_MIME_TYPE,
    },
]


RESOURCE_TEMPLATES: list[dict[str, Any]] = [
    {
        "id": "scene-go",
        "uriTemplate": "chievfx://scene/{scenePath}/go/{goPath}",
        "name": "GameObject summary by scene and path",
        "description": "Compact GameObject summary. Percent-encode scene path and hierarchy path as full URI segments.",
        "mimeType": RESOURCE_MIME_TYPE,
    },
    {
        "id": "scene-component",
        "uriTemplate": "chievfx://scene/{scenePath}/go/{goPath}/component/{componentKey}",
        "name": "Component serialized values by scene, GameObject path, and component key",
        "description": "Serialized values for one component. Percent-encode every dynamic value as a full URI segment.",
        "mimeType": RESOURCE_MIME_TYPE,
    },
    {
        "id": "scene-current-go",
        "uriTemplate": "chievfx://scene/current/go/{goPath}",
        "name": "Current GameObject summary",
        "description": "Compact GameObject summary in the current prefab stage or active scene.",
        "mimeType": RESOURCE_MIME_TYPE,
    },
    {
        "id": "scene-current-component",
        "uriTemplate": "chievfx://scene/current/go/{goPath}/component/{componentKey}",
        "name": "Current component serialized values",
        "description": "Serialized values for one component in the current prefab stage or active scene.",
        "mimeType": RESOURCE_MIME_TYPE,
    },
    {
        "id": "scene-current-go-name-contains",
        "uriTemplate": "chievfx://scene/current/go/name-contains/{text}",
        "name": "Current GameObjects by name substring",
        "description": "Find current hierarchy GameObjects whose names contain literal percent-encoded text.",
        "mimeType": RESOURCE_MIME_TYPE,
    },
    {
        "id": "scene-current-go-name-pattern",
        "uriTemplate": "chievfx://scene/current/go/name-pattern/{pattern}",
        "name": "Current GameObjects by name wildcard",
        "description": "Find current hierarchy GameObjects by anchored * and ? wildcard pattern in one encoded segment.",
        "mimeType": RESOURCE_MIME_TYPE,
    },
    {
        "id": "scene-current-go-component",
        "uriTemplate": "chievfx://scene/current/go/component/{componentType}",
        "name": "Current GameObjects by component type",
        "description": "Find current hierarchy GameObjects by simple or full component type name.",
        "mimeType": RESOURCE_MIME_TYPE,
    },
    {
        "id": "scene-current-go-filter",
        "uriTemplate": "chievfx://scene/current/go/filter/{filterSpec}",
        "name": "Current GameObjects by compact filter",
        "description": "Find current hierarchy GameObjects with encoded name/component/inactive/case/limit filter text.",
        "mimeType": RESOURCE_MIME_TYPE,
    },
    {
        "id": "scene-go-name-contains",
        "uriTemplate": "chievfx://scene/{scene}/go/name-contains/{text}",
        "name": "GameObjects by name substring across scenes",
        "description": "Find GameObjects whose names contain literal percent-encoded text. Scene segment accepts all, active, current, or an encoded scene name/path.",
        "mimeType": RESOURCE_MIME_TYPE,
    },
    {
        "id": "scene-go-name-pattern",
        "uriTemplate": "chievfx://scene/{scene}/go/name-pattern/{pattern}",
        "name": "GameObjects by name wildcard across scenes",
        "description": "Find GameObjects by anchored * and ? wildcard pattern. Scene segment accepts all, active, current, or an encoded scene name/path.",
        "mimeType": RESOURCE_MIME_TYPE,
    },
    {
        "id": "scene-go-component",
        "uriTemplate": "chievfx://scene/{scene}/go/component/{componentType}",
        "name": "GameObjects by component type across scenes",
        "description": "Find GameObjects by simple or full component type name. Scene segment accepts all, active, current, or an encoded scene name/path.",
        "mimeType": RESOURCE_MIME_TYPE,
    },
    {
        "id": "scene-go-filter",
        "uriTemplate": "chievfx://scene/{scene}/go/filter/{filterSpec}",
        "name": "GameObjects by compact filter across scenes",
        "description": "Find GameObjects with encoded name/component/inactive/case/limit filter text. Scene segment accepts all, active, current, or an encoded scene name/path.",
        "mimeType": RESOURCE_MIME_TYPE,
    },
    {
        "id": "assets-name-contains",
        "uriTemplate": "chievfx://assets/name-contains/{text}",
        "name": "Assets by name substring",
        "description": "Find persisted project assets whose names match percent-encoded AssetDatabase name text. Defaults to area=assets.",
        "mimeType": RESOURCE_MIME_TYPE,
    },
    {
        "id": "assets-type",
        "uriTemplate": "chievfx://assets/type/{assetType}",
        "name": "Assets by type",
        "description": "Find persisted project assets by AssetDatabase type or supported alias such as material, texture, prefab, scene, or mesh.",
        "mimeType": RESOURCE_MIME_TYPE,
    },
    {
        "id": "assets-label",
        "uriTemplate": "chievfx://assets/label/{label}",
        "name": "Assets by label",
        "description": "Find persisted project assets by AssetDatabase label. Defaults to area=assets.",
        "mimeType": RESOURCE_MIME_TYPE,
    },
    {
        "id": "assets-filter",
        "uriTemplate": "chievfx://assets/filter/{filterSpec}",
        "name": "Assets by compact AssetDatabase filter",
        "description": "Find persisted assets with encoded name/type/label/area/folder/limit/subassets filter text.",
        "mimeType": RESOURCE_MIME_TYPE,
    },
    {
        "id": "asset-detail",
        "uriTemplate": "chievfx://asset/{guid}",
        "name": "Asset detail by GUID",
        "description": "Load one persisted main asset by GUID with AssetImporter metadata and subasset drill-down hints.",
        "mimeType": RESOURCE_MIME_TYPE,
    },
    {
        "id": "asset-subasset-detail",
        "uriTemplate": "chievfx://asset/{guid}/id/{localId}",
        "name": "Subasset detail by GUID and local file identifier",
        "description": "Load one persisted subasset by GUID and long local file identifier.",
        "mimeType": RESOURCE_MIME_TYPE,
    },
    {
        "id": "scene-current-material-profile-shader",
        "uriTemplate": "chievfx://scene/current/material-profile/shader/{shaderKey}",
        "name": "Current material profile by shader",
        "description": "Drill into materials using one shader key from the current material profile summary.",
        "mimeType": RESOURCE_MIME_TYPE,
    },
    {
        "id": "scene-current-material-profile-material",
        "uriTemplate": "chievfx://scene/current/material-profile/material/{materialKey}",
        "name": "Current material profile material detail",
        "description": "Drill into one material key from the current material profile summary, including locations and texture links.",
        "mimeType": RESOURCE_MIME_TYPE,
    },
    {
        "id": "scene-current-usage-assets",
        "uriTemplate": "chievfx://scene/current/usage/assets/{assetType}",
        "name": "Current scene asset usage by type",
        "description": "Summarize current scene or prefab-stage references for material, mesh, texture, renderTexture, or all assets.",
        "mimeType": RESOURCE_MIME_TYPE,
    },
    {
        "id": "scene-current-usage-asset",
        "uriTemplate": "chievfx://scene/current/usage/asset/{guid}",
        "name": "Current scene asset usage by GUID",
        "description": "Drill into current scene or prefab-stage GameObject/component references to an asset GUID.",
        "mimeType": RESOURCE_MIME_TYPE,
    },
    {
        "id": "scene-current-usage-subasset",
        "uriTemplate": "chievfx://scene/current/usage/asset/{guid}/id/{localId}",
        "name": "Current scene subasset usage by GUID and local file identifier",
        "description": "Drill into current scene or prefab-stage references to one subasset local file identifier.",
        "mimeType": RESOURCE_MIME_TYPE,
    },
]


PROMPTS: list[dict[str, Any]] = [
    {
        "name": "unity-scene-review",
        "title": "Review current Unity scene work",
        "description": "Static prompt for reviewing a Unity scene or prefab against a requested goal.",
        "category": "Scene",
        "arguments": [
            {
                "name": "goal",
                "description": "Specific scene, prefab, or gameplay goal to review against.",
                "required": True,
            },
            {
                "name": "focus",
                "description": "Optional focus area such as lighting, UI, gameplay wiring, or performance.",
                "required": False,
            },
        ],
        "messages": [
            {
                "role": "user",
                "text": (
                    "Review current Unity scene or prefab work for this goal:\n"
                    "{goal}\n\n"
                    "Focus area: {focus}\n\n"
                    "Use available ChievFX MCP resources/tools for evidence. Check hierarchy, selected objects, "
                    "asset references, console warnings, and likely missing validation. Return concise findings "
                    "with exact Unity objects/assets when possible."
                ),
            }
        ],
    },
    {
        "name": "unity-shader-built-in-draft",
        "title": "Draft Built-in Render Pipeline shader code",
        "description": "Static prompt for drafting Unity Built-in Render Pipeline shader code from project context.",
        "category": "Shader",
        "arguments": [
            {
                "name": "goal",
                "description": "Visual effect, material behavior, or shader change to implement.",
                "required": True,
            },
            {
                "name": "shaderName",
                "description": "Optional shader name or target asset name.",
                "required": False,
            },
            {
                "name": "context",
                "description": "Optional material, texture, scene object, platform, or performance constraints.",
                "required": False,
            },
        ],
        "messages": [
            {
                "role": "user",
                "text": (
                    "Draft Unity Built-in Render Pipeline shader code for this goal:\n"
                    "{goal}\n\n"
                    "Shader name or target asset: {shaderName}\n"
                    "Extra context: {context}\n\n"
                    "Before drafting, read relevant ChievFX MCP resources: chievfx://editor/context, "
                    "matching material/shader/texture asset searches, and asset-detail "
                    "resources for referenced GUIDs. Confirm the project really targets the Built-in Render Pipeline; "
                    "if URP or HDRP packages/render pipeline assets are active, stop and recommend the matching prompt. "
                    "Account for Unity version, graphics API/platform, shader model target, lighting path, instancing, "
                    "SRP Batcher irrelevance for Built-in, keywords, passes, fallbacks, transparency, shadows, and "
                    "feature support. Prefer compact ShaderLab/HLSL that compiles in Built-in. Call out assumptions, "
                    "required material properties, unsupported features, and validation steps."
                ),
            }
        ],
    },
    {
        "name": "unity-shader-urp-draft",
        "title": "Draft URP shader code",
        "description": "Static prompt for drafting Unity Universal Render Pipeline shader code from project context.",
        "category": "Shader",
        "arguments": [
            {
                "name": "goal",
                "description": "Visual effect, material behavior, or shader change to implement.",
                "required": True,
            },
            {
                "name": "shaderName",
                "description": "Optional shader name or target asset name.",
                "required": False,
            },
            {
                "name": "context",
                "description": "Optional material, renderer feature, platform, or performance constraints.",
                "required": False,
            },
        ],
        "messages": [
            {
                "role": "user",
                "text": (
                    "Draft Unity URP shader code for this goal:\n"
                    "{goal}\n\n"
                    "Shader name or target asset: {shaderName}\n"
                    "Extra context: {context}\n\n"
                    "Before drafting, read relevant ChievFX MCP resources: chievfx://editor/context, "
                    "matching material/shader/texture asset searches, and asset-detail "
                    "resources for referenced GUIDs. Confirm URP is the target render pipeline and check the installed "
                    "URP package/version, Unity version, renderer asset settings, target platforms, shader model, "
                    "lighting/shadow needs, additional lights, depth/normal texture availability, GPU instancing, "
                    "SRP Batcher compatibility, keywords, render queue, transparency, and pass requirements. Use URP "
                    "ShaderLibrary includes and tags appropriate for the detected version. If the request belongs in "
                    "Shader Graph or a Renderer Feature instead of handwritten shader code, say so. Return code, "
                    "assumptions, material properties, unsupported features, and validation steps."
                ),
            }
        ],
    },
    {
        "name": "unity-shader-hdrp-draft",
        "title": "Draft HDRP shader code",
        "description": "Static prompt for drafting Unity High Definition Render Pipeline shader code from project context.",
        "category": "Shader",
        "arguments": [
            {
                "name": "goal",
                "description": "Visual effect, material behavior, or shader change to implement.",
                "required": True,
            },
            {
                "name": "shaderName",
                "description": "Optional shader name or target asset name.",
                "required": False,
            },
            {
                "name": "context",
                "description": "Optional material, volume/profile, platform, or performance constraints.",
                "required": False,
            },
        ],
        "messages": [
            {
                "role": "user",
                "text": (
                    "Draft Unity HDRP shader code for this goal:\n"
                    "{goal}\n\n"
                    "Shader name or target asset: {shaderName}\n"
                    "Extra context: {context}\n\n"
                    "Before drafting, read relevant ChievFX MCP resources: chievfx://editor/context, "
                    "matching material/shader/texture asset searches, and asset-detail "
                    "resources for referenced GUIDs. Confirm HDRP is the target render pipeline and check the installed "
                    "HDRP package/version, Unity version, HDRenderPipelineAsset settings, platform, shader model, "
                    "lighting model, ray tracing or path tracing requirements, decals, tessellation, transparency, "
                    "motion vectors, depth/normal buffers, keywords, and pass requirements. HDRP custom shader code is "
                    "version-sensitive; prefer HDRP Shader Graph/custom function guidance when safer. Return code only "
                    "when the version and feature surface are clear, plus assumptions, material properties, unsupported "
                    "features, and validation steps."
                ),
            }
        ],
    },
    {
        "name": "unity-shader-graph-plan",
        "title": "Plan Unity Shader Graph",
        "description": "Static prompt for planning Shader Graph properties, nodes, targets, and validation.",
        "category": "Shader",
        "arguments": [
            {
                "name": "goal",
                "description": "Visual effect or material behavior the Shader Graph should implement.",
                "required": True,
            },
            {
                "name": "pipeline",
                "description": "Optional target render pipeline such as URP or HDRP.",
                "required": False,
            },
            {
                "name": "context",
                "description": "Optional material, texture, platform, graph asset, or performance constraints.",
                "required": False,
            },
        ],
        "messages": [
            {
                "role": "user",
                "text": (
                    "Plan a Unity Shader Graph for this goal:\n"
                    "{goal}\n\n"
                    "Target render pipeline: {pipeline}\n"
                    "Extra context: {context}\n\n"
                    "Before planning, read relevant ChievFX MCP resources: chievfx://editor/context, "
                    "matching Shader Graph/material/texture asset searches, and asset-detail "
                    "resources for referenced GUIDs. Verify Unity version, installed Shader Graph package/version, "
                    "target pipeline and graph target, supported shader model/features, platform limits, precision, "
                    "keywords, instancing, SRP Batcher expectations, transparency, shadows, depth/normal texture needs, "
                    "and feature availability. Return graph type, Blackboard properties, subgraphs, node groups, "
                    "connections, keywords, material setup, and validation steps. Do not write .shadergraph JSON directly "
                    "unless the user supplies the exact existing graph file/schema/version and asks for a narrow surgical "
                    "edit; otherwise describe editor/API steps and warn that graph JSON is Unity-version-specific and "
                    "easy to corrupt."
                ),
            }
        ],
    },
    {
        "name": "unity-material-profile-review",
        "title": "Review Unity material and render profile setup",
        "description": "Static prompt for reviewing shader, material, render pipeline asset, and profile configuration.",
        "category": "Shader",
        "arguments": [
            {
                "name": "goal",
                "description": "Material, shader, or render profile outcome to review.",
                "required": True,
            },
            {
                "name": "assetHint",
                "description": "Optional material, shader, renderer asset, volume profile, or scene object hint.",
                "required": False,
            },
            {
                "name": "focus",
                "description": "Optional focus such as visuals, batching, lighting, mobile performance, or migration.",
                "required": False,
            },
        ],
        "messages": [
            {
                "role": "user",
                "text": (
                    "Review Unity material, shader, and render profile setup for this goal:\n"
                    "{goal}\n\n"
                    "Asset hint: {assetHint}\n"
                    "Focus: {focus}\n\n"
                    "Before reviewing, read relevant ChievFX MCP resources: chievfx://editor/context, "
                    "scene usage resources for materials/shaders, matching asset searches, "
                    "and asset-detail resources for referenced GUIDs. Identify active render pipeline, package/version "
                    "state, pipeline asset or quality-level overrides, material shader assignment, missing textures, "
                    "keywords, render queue, instancing, SRP Batcher compatibility, shader model/platform support, "
                    "lighting/shadow/depth feature requirements, Shader Graph limitations, and migration mismatches. "
                    "Do not draft shader or graph JSON unless requested after the review; return findings, risks, exact "
                    "assets or settings to inspect/change, and validation steps."
                ),
            }
        ],
    },
    {
        "name": "unity-editor-context",
        "title": "Summarize live Unity editor context",
        "description": "Dynamic prompt backed by Unity bridge context from current scene, selection, and editor state.",
        "category": "Editor",
        "arguments": [
            {
                "name": "focus",
                "description": "Optional question or work area to prioritize in the generated context prompt.",
                "required": False,
            }
        ],
        "dynamic": True,
        "bridgeCommand": "prompt-get",
    },
]
