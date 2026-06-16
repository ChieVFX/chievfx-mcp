#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Chievfx.Mcp.Editor
{
    internal static class ChievfxMcpToolSelectionFormatting
    {
        public static string BuildRoleTargetSummary(RoleDefinition role)
        {
            var fragments = new List<string>();
            if (role.EnabledCategoryIds.Count > 0)
            {
                fragments.Add($"categories {FormatCompactList(role.EnabledCategoryIds)}");
            }

            if (role.EnabledToolIds.Count > 0)
            {
                fragments.Add($"tools {FormatCompactList(role.EnabledToolIds)}");
            }

            if (role.EnabledPromptNames.Count > 0)
            {
                fragments.Add($"prompts {FormatCompactList(role.EnabledPromptNames)}");
            }

            return fragments.Count == 0
                ? string.Empty
                : $" Targets: {string.Join("; ", fragments)}.";
        }

        public static string FormatCompactList(IReadOnlyList<string> values, int limit = 4)
        {
            var shown = values.Take(limit).ToList();
            var suffix = values.Count > shown.Count ? $" +{values.Count - shown.Count} more" : string.Empty;
            return $"{string.Join(", ", shown)}{suffix}";
        }

        public static OptionalState GetOptionalState(IReadOnlyList<ToolRow> rows)
        {
            var optionalCount = rows.Count(row => !row.Required);
            if (optionalCount == 0)
            {
                return OptionalState.RequiredOnly;
            }

            var enabledOptionalCount = rows.Count(row => !row.Required && row.Enabled);
            if (enabledOptionalCount == 0)
            {
                return OptionalState.Off;
            }

            return enabledOptionalCount == optionalCount ? OptionalState.On : OptionalState.Mixed;
        }

        public static bool AreAllOptionalEnabled(IReadOnlyList<ToolRow> rows)
        {
            return rows.Where(row => !row.Required).All(row => row.Enabled);
        }

        public static string GetCategoryDescription(string category)
        {
            return category switch
            {
                "Essentials" => "Core screenshots, bridge health, event stream, and safe MCP basics used by most sessions.",
                "Editor Window" => "Open, focus, list, and capture Unity Editor windows for UI verification.",
                "Autonomous" => "Discovery and self-configuration tools that help agents inspect and adjust MCP availability.",
                "Scene" => "Inspect and change scene state, hierarchy, play mode, and scene assets.",
                "GameObject" => "Find, inspect, create, and edit GameObjects and components in active scenes.",
                "Prefab" => "Open, instantiate, create, save, and close prefab editing workflows.",
                "Package Manager" => "Inspect and change Unity packages. Higher-risk because package operations can reload the project.",
                "Script Execution / Tests" => "Run tests or trusted C# methods inside Unity. Higher-risk because local code executes.",
                "Control" => "Queue Play Mode keyboard and mouse Input System events behind dry-run and mutation gates.",
                "ugui-design" => "Author and inspect editor-time uGUI Canvas, RectTransform, layout, Image, TMP, and sprite setup.",
                "ui-runtime-common" => "Shared Play Mode runtime UI tools: cross-framework probe, control discovery, and text input.",
                "ugui-runtime-control" => "Probe and control Play Mode uGUI elements: hit stacks, clicks, drags, selection, and control values.",
                "cinemachine-and-timeline" => "Author and inspect Cinemachine cameras, Timeline directors, shots, and camera QA workflows.",
                "Profiler" => "Record and inspect Unity profiler state, counters, and captures.",
                _ => "General ChievFX MCP tools for Unity automation."
            };
        }

        public static Label CreateCategoryNotice(string text)
        {
            var notice = new Label(text);
            notice.style.whiteSpace = WhiteSpace.Normal;
            notice.style.marginLeft = 2;
            notice.style.marginTop = -1;
            notice.style.marginBottom = 4;
            notice.style.color = new StyleColor(new Color(1f, 0.72f, 0.34f));
            return notice;
        }

        public static string BuildCategorySummary(IReadOnlyList<ToolRow> rows)
        {
            var requiredCount = rows.Count(row => row.Required);
            var optionalCount = rows.Count(row => !row.Required);
            var enabledOptionalCount = rows.Count(row => !row.Required && row.Enabled);
            var selectedEstimate = rows.Where(row => row.Required || row.Enabled).Sum(row => row.EstimatedTokens);
            var allEstimate = rows.Sum(row => row.EstimatedTokens);
            var selectedDescriptionEstimate = rows.Where(row => row.Required || row.Enabled).Sum(row => row.DescriptionEstimatedTokens);
            var allDescriptionEstimate = rows.Sum(row => row.DescriptionEstimatedTokens);
            var selectedCallEstimate = rows.Where(row => row.Required || row.Enabled).Sum(row => row.CallEnvelopeEstimatedTokens);
            var state = optionalCount == 0
                ? "Required only"
                : enabledOptionalCount == 0
                    ? "Optional disabled"
                    : enabledOptionalCount == optionalCount
                        ? "Optional enabled"
                        : "Optional partial";

            return $"{state} | Required {requiredCount} | Enabled {enabledOptionalCount}/{optionalCount} optional | Descriptors ~{selectedEstimate}/~{allEstimate} | Descriptions ~{selectedDescriptionEstimate}/~{allDescriptionEstimate} | Call base ~{selectedCallEstimate}";
        }

        public static bool TryGetCategoryNotice(string category, out string notice)
        {
            switch (category)
            {
                case "Package Manager":
                    notice = "High risk: installs/removes Unity packages and can trigger package or domain reload changes.";
                    return true;
                case "Script Execution / Tests":
                    notice = "High risk: executes local C# code and can run long tests. Enable only for trusted tasks.";
                    return true;
                default:
                    notice = string.Empty;
                    return false;
            }
        }

        public static int GetCategorySortOrder(string category)
        {
            return category switch
            {
                "Essentials" => 0,
                "Editor Window" => 10,
                "Autonomous" => 15,
                "Scene" => 20,
                "GameObject" => 30,
                "Prefab" => 40,
                "Package Manager" => 50,
                "Script Execution / Tests" => 60,
                "Profiler" => 70,
                "Frame Debugger" => 80,
                "cinemachine-and-timeline" => 85,
                "ugui-design" => 90,
                "ui-runtime-common" => 90,
                "ugui-runtime-control" => 91,
                "OBSOLETE" => 999,
                _ => 100
            };
        }

        public static List<string> ReadStringArray(JToken? token)
        {
            if (token is not JArray array)
            {
                return new List<string>();
            }

            return array
                .Where(item => item.Type == JTokenType.String)
                .Select(item => item.Value<string>() ?? string.Empty)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
        }

        public static string BuildRoleKey(string kind, string roleId, string assetPath)
        {
            return string.Equals(kind, "custom", StringComparison.Ordinal)
                ? $"custom:{assetPath}:{roleId}"
                : $"{kind}:{roleId}";
        }
    }
}
