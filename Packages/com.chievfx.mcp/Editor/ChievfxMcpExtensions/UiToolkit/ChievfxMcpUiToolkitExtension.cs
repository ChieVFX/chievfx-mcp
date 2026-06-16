#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Chievfx.Mcp.Editor;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitRuntimeTools;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitResources;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitInteractions;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitPanelQueries;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitRows;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitShared;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitSchemas;

namespace Chievfx.Mcp.Extensions.UiToolkit
{
    [InitializeOnLoad]
    internal static class ChievfxMcpUiToolkitExtension
    {
        internal const string ExtensionId = "chievfx.uitoolkit";
        internal const string Category = "UI Toolkit";
        internal const string UriPrefix = "chievfx://extensions/chievfx.uitoolkit/";
        internal const string StatusUri = UriPrefix + "status";
        internal const string RuntimeStatusUri = UriPrefix + "runtime/status";
        internal const string RuntimePanelsUri = UriPrefix + "runtime/panels";
        internal const string RuntimeVisibleTreeUri = UriPrefix + "runtime/visible-tree";
        internal const int DefaultMaxRows = 256;

#if CHIEVFX_MCP_HAS_UITOOLKIT
        internal const bool UiToolkitVersionDefineActive = true;
#else
        internal const bool UiToolkitVersionDefineActive = false;
#endif

        static ChievfxMcpUiToolkitExtension()
        {
            ChievfxMcpExtensionRegistry.RegisterExtension(CreateDescriptor());
            if (GetDependencyStatus().Available)
            {
                ChievfxMcpRuntimeUiAdapterRegistry.Register(new UiToolkitRuntimeUiAdapter());
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

        private static ChievfxMcpExtensionDescriptor CreateDescriptor()
        {
            var status = GetDependencyStatus();
            var descriptor = new ChievfxMcpExtensionDescriptor
            {
                Id = ExtensionId,
                DisplayName = "ChievFX MCP UI Toolkit Runtime",
                Version = "0.1.0",
                Description = status.Available
                    ? "First-party read-only UI Toolkit runtime panel inspection and screen-position probing."
                    : "First-party UI Toolkit runtime inspection unavailable until UI Toolkit runtime types are loaded.",
                ToolRunner = RunTool,
                ResourceReader = ReadResource,
            };

            descriptor.Resources.Add(new ChievfxMcpResourceDescriptor
            {
                Id = "uitoolkit-status",
                Uri = StatusUri,
                Name = "UI Toolkit extension status",
                Description = "Compact UI Toolkit availability, current hierarchy counts, and Play Mode drill-down hints.",
                MimeType = "application/json",
                Category = Category,
            });

            if (!status.Available)
            {
                return descriptor;
            }

            descriptor.Resources.Add(new ChievfxMcpResourceDescriptor
            {
                Id = "uitoolkit-runtime-status",
                Uri = RuntimeStatusUri,
                Name = "Runtime UI Toolkit status",
                Description = "Read-only Play Mode status for UIDocuments, panels, screen coordinates, and warnings.",
                MimeType = "application/json",
                Category = Category,
            });
            descriptor.Resources.Add(new ChievfxMcpResourceDescriptor
            {
                Id = "uitoolkit-runtime-panels",
                Uri = RuntimePanelsUri,
                Name = "Runtime UI Toolkit panels",
                Description = "Compact summary of loaded UIDocuments, PanelSettings, runtime panels, and roots.",
                MimeType = "application/json",
                Category = Category,
            });
            descriptor.Resources.Add(new ChievfxMcpResourceDescriptor
            {
                Id = "uitoolkit-runtime-visible-tree",
                Uri = RuntimeVisibleTreeUri,
                Name = "Runtime UI Toolkit visible tree",
                Description = "Read-only capped visible VisualElement tree for runtime UIDocuments.",
                MimeType = "application/json",
                Category = Category,
            });
            descriptor.Tools.Add(CreateTool(
                "uitoolkit-runtime-probe-screen-position",
                "Probe Play Mode UI Toolkit hit stack at screen position. Requires Play Mode.",
                RuntimeProbeSchema()));
            descriptor.Tools.Add(CreateTool(
                "uitoolkit-runtime-interact",
                "Dry-run or explicitly dispatch guarded Play Mode UI Toolkit pointer click, focus, navigation submit, or standard control value changes.",
                RuntimeInteractSchema()));
            return descriptor;
        }

        private static ChievfxMcpToolDescriptor CreateTool(string name, string description, JObject schema)
        {
            return new ChievfxMcpToolDescriptor
            {
                Name = name,
                Description = description,
                Category = Category,
                InputSchema = schema,
            };
        }

        private static object? RunTool(string toolName, JToken args)
        {
            var status = GetDependencyStatus();
            if (!status.Available)
            {
                return CreateUnavailable(StatusUri, status, $"Tool '{toolName}' requires UI Toolkit runtime types.");
            }

            return toolName switch
            {
                "uitoolkit-runtime-probe-screen-position" => ProbeRuntimeScreenPosition(args, status),
                "uitoolkit-runtime-interact" => InteractRuntime(args, status),
                _ => throw new InvalidOperationException($"Unknown UI Toolkit extension tool '{toolName}'."),
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

            if (string.Equals(uri, RuntimeStatusUri, StringComparison.Ordinal))
            {
                return ReadRuntimeStatus(uri, status);
            }

            if (string.Equals(uri, RuntimePanelsUri, StringComparison.Ordinal))
            {
                return ReadRuntimePanels(uri, status);
            }

            if (string.Equals(uri, RuntimeVisibleTreeUri, StringComparison.Ordinal))
            {
                return ReadRuntimeVisibleTree(uri, status);
            }

            return CreateUnavailable(uri, status, "Unsupported UI Toolkit extension resource URI.");
        }
    }
}
