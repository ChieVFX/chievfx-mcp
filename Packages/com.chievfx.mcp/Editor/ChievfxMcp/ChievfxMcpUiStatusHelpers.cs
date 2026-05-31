#nullable enable
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Chievfx.Mcp.Editor
{
    internal static class ChievfxMcpUiStatusHelpers
    {
        internal static Dictionary<string, object?> DescribeEditorContext()
        {
            var playMode = EditorApplication.isPlaying;
            try
            {
                var context = GameObjectBridgeService.GetGameObjectQueryContext();
                var mode = string.Equals(context.Source, "prefabStage", StringComparison.Ordinal) ? "prefab" : "scene";
                return new Dictionary<string, object?>
                {
                    ["mode"] = mode,
                    ["scenePath"] = string.IsNullOrWhiteSpace(context.ScenePath) ? null : context.ScenePath,
                    ["prefabAssetPath"] = string.IsNullOrWhiteSpace(context.PrefabAssetPath) ? null : context.PrefabAssetPath,
                    ["playMode"] = playMode,
                };
            }
            catch
            {
                return new Dictionary<string, object?>
                {
                    ["mode"] = "none",
                    ["playMode"] = playMode,
                };
            }
        }

        internal static Dictionary<string, object?> DescribePackageCapability(
            string packageName,
            string packageVersion,
            string packageSource,
            bool available,
            string? reason = null)
        {
            if (!available)
            {
                return new Dictionary<string, object?>
                {
                    ["package"] = packageName,
                    ["reason"] = reason ?? "Package unavailable.",
                };
            }

            return new Dictionary<string, object?>
            {
                ["package"] = packageName,
                ["version"] = packageVersion,
                ["source"] = packageSource,
            };
        }

        internal static Dictionary<string, object?> DescribeUguiHierarchy(Type? canvasType, Type? eventSystemType, Type? tmpTextType)
        {
            return new Dictionary<string, object?>
            {
                ["canvases"] = CountComponentsInCurrentHierarchy(canvasType),
                ["eventSystems"] = CountComponentsInCurrentHierarchy(eventSystemType),
                ["tmpTexts"] = CountComponentsInCurrentHierarchy(tmpTextType),
            };
        }

        internal static Dictionary<string, object?> DescribeUiToolkitHierarchy(Type? uiDocumentType)
        {
            return new Dictionary<string, object?>
            {
                ["uiDocuments"] = CountComponentsInCurrentHierarchy(uiDocumentType),
            };
        }

        internal static Dictionary<string, object?> DescribeCrossFrameworkHierarchy(
            Type? canvasType,
            Type? eventSystemType,
            Type? tmpTextType,
            Type? uiDocumentType)
        {
            return new Dictionary<string, object?>
            {
                ["canvases"] = CountComponentsInCurrentHierarchy(canvasType),
                ["eventSystems"] = CountComponentsInCurrentHierarchy(eventSystemType),
                ["tmpTexts"] = CountComponentsInCurrentHierarchy(tmpTextType),
                ["uiDocuments"] = CountComponentsInCurrentHierarchy(uiDocumentType),
            };
        }

        internal static int CountComponentsInCurrentHierarchy(Type? componentType, bool includeInactive = true)
        {
            if (componentType == null)
            {
                return 0;
            }

            try
            {
                var context = GameObjectBridgeService.GetGameObjectQueryContext();
                var count = 0;
                foreach (var root in context.Roots)
                {
                    count += root.GetComponentsInChildren(componentType, includeInactive).Length;
                }

                return count;
            }
            catch
            {
                return 0;
            }
        }

        internal static Dictionary<string, object?> ExtractCompactCapability(Dictionary<string, object?> status)
        {
            var available = status.TryGetValue("available", out var availableValue) && Equals(availableValue, true);
            return DescribePackageCapability(
                ReadString(status, "packageName"),
                ReadString(status, "packageVersion"),
                ReadString(status, "packageSource"),
                available,
                available ? null : ReadString(status, "reason"));
        }

        private static string ReadString(Dictionary<string, object?> source, string key)
        {
            return source.TryGetValue(key, out var value) ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty : string.Empty;
        }
    }
}
