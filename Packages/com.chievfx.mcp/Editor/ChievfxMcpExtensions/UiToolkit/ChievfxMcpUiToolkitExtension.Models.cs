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
using static Chievfx.Mcp.Extensions.UiToolkit.ChievfxMcpUiToolkitExtension;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitRuntimeTools;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitResources;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitInteractions;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitPanelQueries;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitRows;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitShared;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitSchemas;

namespace Chievfx.Mcp.Extensions.UiToolkit
{
    internal readonly struct RuntimeScreenPosition
    {
        public RuntimeScreenPosition(Vector2 screenPosition, Vector2 screenSize, Vector2 normalizedPosition, bool normalizedInputSupplied)
        {
            ScreenPosition = screenPosition;
            ScreenSize = screenSize;
            NormalizedPosition = normalizedPosition;
            NormalizedInputSupplied = normalizedInputSupplied;
        }

        public Vector2 ScreenPosition { get; }

        public Vector2 ScreenSize { get; }

        public Vector2 NormalizedPosition { get; }

        public bool NormalizedInputSupplied { get; }

        public static RuntimeScreenPosition FromScreenPosition(Vector2 screenPosition)
        {
            var screenSize = new Vector2(Mathf.Max(1, Screen.width), Mathf.Max(1, Screen.height));
            return new RuntimeScreenPosition(
                screenPosition,
                screenSize,
                new Vector2(screenPosition.x / screenSize.x, screenPosition.y / screenSize.y),
                normalizedInputSupplied: false);
        }
    }

    internal readonly struct TreeItem
    {
        public TreeItem(object element, int depth)
        {
            Element = element;
            Depth = depth;
        }

        public object Element { get; }

        public int Depth { get; }
    }

    internal readonly struct RuntimeInteractionResolution
    {
        public RuntimeInteractionResolution(
            object? target,
            PanelGroup? group,
            RuntimeScreenPosition? position,
            Vector2? panelPosition,
            Dictionary<string, object?>[] stack,
            string resolvedBy)
        {
            Target = target;
            Group = group;
            Position = position;
            PanelPosition = panelPosition;
            Stack = stack;
            ResolvedBy = resolvedBy;
        }

        public object? Target { get; }

        public PanelGroup? Group { get; }

        public RuntimeScreenPosition? Position { get; }

        public Vector2? PanelPosition { get; }

        public Dictionary<string, object?>[] Stack { get; }

        public string ResolvedBy { get; }

        public static RuntimeInteractionResolution FromTarget(object target, string resolvedBy)
        {
            return new RuntimeInteractionResolution(
                target,
                PanelGroup.FromElement(target),
                null,
                null,
                Array.Empty<Dictionary<string, object?>>(),
                resolvedBy);
        }
    }

    internal sealed class PanelGroup
    {
        private PanelGroup(object? panel, Component[] documents)
        {
            Panel = panel;
            Documents = documents;
        }

        public object? Panel { get; }

        public Component[] Documents { get; }

        public static PanelGroup FromDocument(Component document)
        {
            var root = GetRootVisualElement(document);
            return new PanelGroup(root == null ? null : GetPanel(root), new[] { document });
        }

        public static PanelGroup FromPanel(object panel, Component[] documents)
        {
            return new PanelGroup(panel, documents);
        }

        public static PanelGroup FromElement(object visualElement)
        {
            return new PanelGroup(GetPanel(visualElement), Array.Empty<Component>());
        }
    }

    internal sealed class UiToolkitDependencyStatus
    {
        public UiToolkitDependencyStatus(
            bool available,
            string reason,
            string packageName,
            string packageVersion,
            string packageSource,
            bool versionDefineActive,
            bool modulePresent,
            Type? uiDocumentType,
            Type? panelSettingsType,
            Type? iPanelType,
            Type? runtimePanelUtilsType,
            Type? visualElementType)
        {
            Available = available;
            Reason = reason;
            PackageName = packageName;
            PackageVersion = packageVersion;
            PackageSource = packageSource;
            VersionDefineActive = versionDefineActive;
            ModulePresent = modulePresent;
            UIDocumentType = uiDocumentType;
            PanelSettingsType = panelSettingsType;
            IPanelType = iPanelType;
            RuntimePanelUtilsType = runtimePanelUtilsType;
            VisualElementType = visualElementType;
        }

        public bool Available { get; }

        public string Reason { get; }

        public string PackageName { get; }

        public string PackageVersion { get; }

        public string PackageSource { get; }

        public bool VersionDefineActive { get; }

        public bool ModulePresent { get; }

        public Type? UIDocumentType { get; }

        public Type? PanelSettingsType { get; }

        public Type? IPanelType { get; }

        public Type? RuntimePanelUtilsType { get; }

        public Type? VisualElementType { get; }

        public Dictionary<string, object?> ToDictionary()
        {
            return new Dictionary<string, object?>
            {
                ["packageName"] = PackageName,
                ["packageVersion"] = PackageVersion,
                ["packageSource"] = PackageSource,
                ["versionDefineActive"] = VersionDefineActive,
                ["modulePresent"] = ModulePresent,
                ["uiDocumentLoaded"] = UIDocumentType != null,
                ["panelSettingsLoaded"] = PanelSettingsType != null,
                ["iPanelLoaded"] = IPanelType != null,
                ["runtimePanelUtilsLoaded"] = RuntimePanelUtilsType != null,
                ["visualElementLoaded"] = VisualElementType != null,
                ["available"] = Available,
                ["reason"] = Reason,
            };
        }
    }

    internal sealed class UiToolkitRuntimeUiAdapter : IChievfxMcpRuntimeUiAdapter, IChievfxMcpRuntimeUiTextInputAdapter
    {
        public string FrameworkId => "uitoolkit";

        public string FrameworkName => "UI Toolkit";

        public int Priority => 100;

        public bool Available => GetDependencyStatus().Available;

        public object? Status => GetDependencyStatus().ToDictionary();

        public IEnumerable<string> Resources => new[] { RuntimeStatusUri, RuntimePanelsUri, RuntimeVisibleTreeUri, RuntimeInteractablesUri };

        public object? ProbeScreenPosition(JToken request)
        {
            return ProbeRuntimeScreenPosition(request, GetDependencyStatus());
        }

        public object? TypeIntoFocusedTextField(JToken request, bool requireTarget)
        {
            return TypeTextIntoFocusedTextField(request, GetDependencyStatus(), requireTarget);
        }
    }
}
