#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Chievfx.Mcp.Editor;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using static Chievfx.Mcp.Extensions.Ugui.UguiDesignTools;
using static Chievfx.Mcp.Extensions.Ugui.UguiElementHelpers;
using static Chievfx.Mcp.Extensions.Ugui.UguiLayoutHelpers;
using static Chievfx.Mcp.Extensions.Ugui.UguiResourcesAndRows;
using static Chievfx.Mcp.Extensions.Ugui.UguiRuntimeControlHelpers;
using static Chievfx.Mcp.Extensions.Ugui.UguiRuntimeHelpers;
using static Chievfx.Mcp.Extensions.Ugui.UguiRuntimeTools;
using static Chievfx.Mcp.Extensions.Ugui.UguiSchemas;
using static Chievfx.Mcp.Extensions.Ugui.UguiSharedHelpers;

namespace Chievfx.Mcp.Extensions.Ugui
{
    [InitializeOnLoad]
    internal static class ChievfxMcpUguiExtension
    {
        internal const string ExtensionId = "chievfx.ugui";
        internal const string DesignCategory = "ugui-design";
        internal const string RuntimeControlCategory = "ugui-runtime-control";
        internal const string UriPrefix = "chievfx://extensions/chievfx.ugui/";
        internal const string StatusUri = UriPrefix + "status";
        internal const string CanvasesUri = UriPrefix + "canvases";
        internal const string CanvasDetailPrefix = UriPrefix + "canvas/";
        internal const string SpriteReadinessPrefix = UriPrefix + "sprite/";
        internal const string RuntimeStatusUri = UriPrefix + "runtime/status";
        internal const string RuntimeCanvasesUri = UriPrefix + "runtime/canvases";
        internal const string RuntimeVisibleTreeUri = UriPrefix + "runtime/visible-tree";
        internal const string RuntimeInteractablesUri = UriPrefix + "runtime/interactables";
        internal static bool? preferInputSystemUiModuleOverrideForTests;
        internal static bool? runtimeReadAllowedOverrideForTests;

#if CHIEVFX_MCP_HAS_UGUI
        internal const bool UguiVersionDefineActive = true;
#else
        internal const bool UguiVersionDefineActive = false;
#endif

        static ChievfxMcpUguiExtension()
        {
            ChievfxMcpExtensionRegistry.RegisterExtension(CreateDescriptor());
            if (GetDependencyStatus().Available)
            {
                ChievfxMcpRuntimeUiAdapterRegistry.Register(new UguiRuntimeUiAdapter());
            }
        }

        internal static object? RunToolForTests(string toolName, JToken args)
        {
            return RunTool(toolName, args);
        }

        public static object? RunToolForTests(string toolName, string argsJson)
        {
            return RunTool(toolName, string.IsNullOrWhiteSpace(argsJson) ? new JObject() : JObject.Parse(argsJson));
        }

        public static object? ReadResourceForTests(string uri)
        {
            return ReadResource(uri);
        }

        public static void SetPreferInputSystemUiModuleOverrideForTests(bool? preferInputSystemUiModule)
        {
            preferInputSystemUiModuleOverrideForTests = preferInputSystemUiModule;
        }

        public static void SetRuntimeReadAllowedOverrideForTests(bool? runtimeReadAllowed)
        {
            runtimeReadAllowedOverrideForTests = runtimeReadAllowed;
        }

        public static object ResolveTextBackendForTests(string requestedBackend, bool tmpConfigured)
        {
            var warnings = new List<string>();
            var backend = ResolveTextBackend(requestedBackend, tmpConfigured, warnings);
            return new Dictionary<string, object?>
            {
                ["textBackend"] = backend,
                ["warnings"] = warnings.ToArray(),
            };
        }

        private static ChievfxMcpExtensionDescriptor CreateDescriptor()
        {
            var status = GetDependencyStatus();
            var descriptor = new ChievfxMcpExtensionDescriptor
            {
                Id = ExtensionId,
                DisplayName = "ChievFX MCP uGUI Authoring",
                Version = "0.1.0",
                Description = status.Available
                    ? "First-party editor-time uGUI authoring helpers for Canvas, RectTransform, and common controls."
                    : "First-party uGUI authoring helpers unavailable until com.unity.ugui is installed and loaded.",
                ToolRunner = RunTool,
                ResourceReader = ReadResource,
            };

            descriptor.Resources.Add(new ChievfxMcpResourceDescriptor
            {
                Id = "ugui-status",
                Uri = StatusUri,
                Name = "uGUI extension status",
                Description = "Compact uGUI availability, TMP readiness, current hierarchy counts, and editor/runtime drill-down hints.",
                MimeType = "application/json",
                Category = DesignCategory,
            });
            descriptor.Resources.Add(new ChievfxMcpResourceDescriptor
            {
                Id = "ugui-canvases-summary",
                Uri = CanvasesUri,
                Name = "uGUI canvas summary",
                Description = "Compact summary of active-scene Canvas roots and their common uGUI authoring components.",
                MimeType = "application/json",
                Category = DesignCategory,
            });
            descriptor.Resources.Add(new ChievfxMcpResourceDescriptor
            {
                Id = "ugui-runtime-status",
                Uri = RuntimeStatusUri,
                Name = "Runtime uGUI status",
                Description = "Read-only Play Mode status for EventSystem, selected object, screen coordinates, canvases, and runtime warnings.",
                MimeType = "application/json",
                Category = RuntimeControlCategory,
            });
            descriptor.Resources.Add(new ChievfxMcpResourceDescriptor
            {
                Id = "ugui-runtime-canvases",
                Uri = RuntimeCanvasesUri,
                Name = "Runtime uGUI canvases",
                Description = "Read-only Play Mode Canvas, GraphicRaycaster, and sorting/camera state for runtime uGUI.",
                MimeType = "application/json",
                Category = RuntimeControlCategory,
            });
            descriptor.Resources.Add(new ChievfxMcpResourceDescriptor
            {
                Id = "ugui-runtime-visible-tree",
                Uri = RuntimeVisibleTreeUri,
                Name = "Runtime uGUI visible tree",
                Description = "Read-only Play Mode visible RectTransform tree for active uGUI canvases.",
                MimeType = "application/json",
                Category = RuntimeControlCategory,
            });
            descriptor.Resources.Add(new ChievfxMcpResourceDescriptor
            {
                Id = "ugui-runtime-interactables",
                Uri = RuntimeInteractablesUri,
                Name = "Runtime uGUI interactables",
                Description = "Read-only Play Mode list of loaded Button, Toggle, Slider, Scrollbar, Dropdown, TMP_Dropdown, and InputField controls.",
                MimeType = "application/json",
                Category = RuntimeControlCategory,
            });
            descriptor.ResourceTemplates.Add(new ChievfxMcpResourceTemplateDescriptor
            {
                Id = "ugui-canvas-detail",
                UriTemplate = CanvasDetailPrefix + "{pathOrInstanceId}",
                Name = "uGUI canvas detail",
                Description = "Compact detail for one Canvas by transform path or instance id.",
                MimeType = "application/json",
                Category = DesignCategory,
            });
            descriptor.ResourceTemplates.Add(new ChievfxMcpResourceTemplateDescriptor
            {
                Id = "ugui-sprite-readiness",
                UriTemplate = SpriteReadinessPrefix + "{guidOrPath}",
                Name = "uGUI sprite readiness",
                Description = "Checks a sprite/texture asset for UI Image and 9-slice readiness.",
                MimeType = "application/json",
                Category = DesignCategory,
            });
            descriptor.Prompts.Add(new ChievfxMcpPromptDescriptor
            {
                Name = "ugui-authoring-review",
                Title = "Review uGUI authoring changes",
                Description = "Guidance for editor-time uGUI creation plus visual screenshot follow-up.",
                Category = DesignCategory,
                Arguments = new JArray
                {
                    new JObject
                    {
                        ["name"] = "goal",
                        ["description"] = "Optional UI authoring goal.",
                        ["required"] = false,
                    },
                },
                StaticText = "Use editor-time uGUI tools only. Prefer semantic rect presets, inspect output, then capture Game view or Scene view screenshot for visual review. Goal: {goal}",
            });

            if (!status.Available)
            {
                return descriptor;
            }

            descriptor.Tools.Add(CreateTool("ugui-canvas-ensure", "Create or normalize uGUI Canvas/EventSystem.", CanvasEnsureSchema()));
            descriptor.Tools.Add(CreateTool("ugui-rect-get", "Returns RectTransform data for one or more uGUI elements.", RectGetSchema()));
            descriptor.Tools.Add(CreateTool("ugui-rect-update", "Set uGUI RectTransform preset/size/anchors.", RectUpdateSchema()));
            descriptor.Tools.Add(CreateTool("ugui-layout-group-set", "Set uGUI Vertical/Horizontal/Grid LayoutGroup.", LayoutGroupSetSchema()));
            descriptor.Tools.Add(CreateTool("ugui-layout-element-set", "Set uGUI LayoutElement sizing.", LayoutElementSetSchema()));
            descriptor.Tools.Add(CreateTool("ugui-layout-rebuild", "Force immediate uGUI layout rebuilds for one or more RectTransforms.", LayoutRebuildSchema()));
            descriptor.Tools.Add(CreateTool("ugui-scrollrect-create", "Create wired uGUI ScrollRect: root, viewport+mask, content layout/fitter.", ScrollRectCreateSchema()));
            descriptor.Tools.Add(CreateTool("ugui-grid-create", "Create GridLayoutGroup with N uGUI cells.", GridCreateSchema()));
            descriptor.Tools.Add(CreateTool("ugui-sibling-draworder-set", "Sets exact uGUI sibling draw order for one or more RectTransforms.", SiblingDrawOrderSetSchema()));
            descriptor.Tools.Add(CreateTool("ugui-create-simple", "Create a uGUI RectTransform, optionally with an Image.", SimpleCreateSchema()));
            descriptor.Tools.Add(CreateTool("ugui-create-control", "Create uGUI controls such as button, slider, or progressbar.", ControlCreateSchema()));
            descriptor.Tools.Add(CreateTool("ugui-textmeshpro-hierarchy", "Groups TextMeshProUGUI by style.", TextMeshProHierarchySchema()));
            descriptor.Tools.Add(CreateTool("ugui-textmeshpro-get", "Gets TextMeshProUGUI style.", TextMeshProGetSchema()));
            descriptor.Tools.Add(CreateTool("ugui-textmeshpro-set-or-create", "Sets TextMeshProUGUI style, optionally creating missing components.", TextMeshProSetOrCreateSchema()));
            descriptor.Tools.Add(CreateTool("ugui-image-set", "Set uGUI Image sprite, tint, raycast, aspect, type.", ImageSetSchema()));
            descriptor.Tools.Add(CreateTool("ugui-image-primitive-create", "Create generated Sprite primitive uGUI Image.", ImagePrimitiveCreateSchema()));
            descriptor.Tools.Add(CreateTool("ugui-sprite-configure", "Configure texture as uGUI Sprite/9-slice.", SpriteConfigureSchema()));
            descriptor.Tools.Add(CreateTool("ugui-ui-hierarchy", "Returns a compact uGUI RectTransform hierarchy for active-scene Canvases or a target root.", UiHierarchySchema()));
            descriptor.Tools.Add(CreateTool("ugui-ui-find", "Finds uGUI elements by path, name, component type, or instance id, with optional detail data.", UiFindSchema()));
            descriptor.Tools.Add(CreateTool("ugui-runtime-probe-screen-position", "Probe Play Mode uGUI hit stack at screen position. Requires Play Mode.", RuntimeProbeSchema(), RuntimeControlCategory));
            descriptor.Tools.Add(CreateTool("ugui-runtime-click", "Click runtime uGUI target. Use dryRun or allowStateMutation.", RuntimeClickSchema(), RuntimeControlCategory));
            descriptor.Tools.Add(CreateTool("ugui-runtime-drag", "Drag runtime uGUI target. Use dryRun or allowStateMutation.", RuntimeDragSchema(), RuntimeControlCategory));
            descriptor.Tools.Add(CreateTool("ugui-runtime-select", "Select, focus, or clear a runtime uGUI GameObject through EventSystem.", RuntimeSelectSchema(), RuntimeControlCategory));
            descriptor.Tools.Add(CreateTool("ugui-runtime-set-control-value", "Set runtime uGUI Slider, Scrollbar, Toggle, Dropdown, TMP_Dropdown, or InputField values with callback policy.", RuntimeSetControlValueSchema(), RuntimeControlCategory));

            return descriptor;
        }

        private static ChievfxMcpToolDescriptor CreateTool(string name, string description, JObject schema, string category = DesignCategory)
        {
            return new ChievfxMcpToolDescriptor
            {
                Name = name,
                Description = description,
                Category = category,
                InputSchema = schema,
            };
        }

        private static object? RunTool(string toolName, JToken args)
        {
            var status = GetDependencyStatus();
            if (!status.Available)
            {
                return CreateUnavailable(StatusUri, status, $"Tool '{toolName}' requires com.unity.ugui.");
            }

            return toolName switch
            {
                "ugui-canvas-ensure" => EnsureCanvas(args, status),
                "ugui-rect-get" => GetRect(args, status),
                "ugui-rect-update" => UpdateRect(args, status),
                "ugui-layout-group-set" => SetLayoutGroup(args, status),
                "ugui-layout-element-set" => SetLayoutElement(args, status),
                "ugui-layout-rebuild" => RebuildLayout(args, status),
                "ugui-scrollrect-create" => CreateScrollRect(args, status),
                "ugui-grid-create" => CreateGrid(args, status),
                "ugui-sibling-draworder-set" => SetSiblingDrawOrder(args, status),
                "ugui-create-simple" => CreateSimple(args, status),
                "ugui-create-control" => CreateControl(args, status),
                "ugui-textmeshpro-hierarchy" => TextMeshProHierarchy(args, status),
                "ugui-textmeshpro-get" => TextMeshProGet(args, status),
                "ugui-textmeshpro-set-or-create" => TextMeshProSetOrCreate(args, status),
                "ugui-image-set" => SetImage(args, status),
                "ugui-image-primitive-create" => CreateImagePrimitive(args, status),
                "ugui-sprite-configure" => ConfigureSprite(args, status),
                "ugui-ui-hierarchy" => UiHierarchy(args, status),
                "ugui-ui-find" => UiFind(args, status),
                "ugui-runtime-probe-screen-position" => ProbeRuntimeScreenPosition(args, status),
                "ugui-runtime-click" => RuntimeClick(args, status),
                "ugui-runtime-drag" => RuntimeDrag(args, status),
                "ugui-runtime-select" => RuntimeSelect(args, status),
                "ugui-runtime-set-control-value" => RuntimeSetControlValue(args, status),
                _ => throw new InvalidOperationException($"Unknown uGUI extension tool '{toolName}'."),
            };
        }

        private static object? ReadResource(string uri)
        {
            var status = GetDependencyStatus();
            if (string.Equals(uri, StatusUri, StringComparison.Ordinal))
            {
                return ReadStatusResource(uri, status);
            }

            if (!status.Available)
            {
                return CreateUnavailable(uri, status);
            }

            if (string.Equals(uri, CanvasesUri, StringComparison.Ordinal))
            {
                return ReadCanvases(uri, status);
            }

            if (uri.StartsWith(CanvasDetailPrefix, StringComparison.Ordinal))
            {
                return ReadCanvasDetail(uri, DecodeSegment(uri.Substring(CanvasDetailPrefix.Length)), status);
            }

            if (uri.StartsWith(SpriteReadinessPrefix, StringComparison.Ordinal))
            {
                return ReadSpriteReadiness(uri, DecodeSegment(uri.Substring(SpriteReadinessPrefix.Length)), status);
            }

            if (string.Equals(uri, RuntimeStatusUri, StringComparison.Ordinal))
            {
                return ReadRuntimeStatus(uri, status);
            }

            if (string.Equals(uri, RuntimeCanvasesUri, StringComparison.Ordinal))
            {
                return ReadRuntimeCanvases(uri, status);
            }

            if (string.Equals(uri, RuntimeVisibleTreeUri, StringComparison.Ordinal))
            {
                return ReadRuntimeVisibleTree(uri, status);
            }

            if (string.Equals(uri, RuntimeInteractablesUri, StringComparison.Ordinal))
            {
                return ReadRuntimeInteractables(uri, status);
            }

            return CreateUnavailable(uri, status, "Unsupported uGUI extension resource URI.");
        }
    }
}
