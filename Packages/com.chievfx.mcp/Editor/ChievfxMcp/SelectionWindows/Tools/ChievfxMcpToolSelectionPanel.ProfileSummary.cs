#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using static Chievfx.Mcp.Editor.ChievfxMcpSelectionUi;
using static Chievfx.Mcp.Editor.ChievfxMcpToolSelectionFormatting;

namespace Chievfx.Mcp.Editor
{
    internal sealed partial class ChievfxMcpToolSelectionPanel
    {
        private void AddCurrentProfileSummary(VisualElement parent)
        {
            var selectedToolRows = GetEffectiveSelectedRows().ToList();
            var toolTotals = SelectionTotals.FromRows(
                "Tools",
                selectedToolRows.Count,
                toolRows.Count,
                selectedToolRows.Sum(row => row.EstimatedTokens),
                toolRows.Sum(row => row.EstimatedTokens),
                selectedToolRows.Sum(row => row.DescriptionEstimatedTokens),
                toolRows.Sum(row => row.DescriptionEstimatedTokens),
                changed: !CurrentToolsMatchActiveRole());
            var resourceTotals = ReadSelectionTotals(
                "Resources",
                ChievfxMcpToolPolicy.ResourceSelectionPath,
                "enabledResourceIds",
                "resources",
                "enabledResourceTemplateIds",
                "resourceTemplates");
            var promptTotals = ReadSelectionTotals(
                "Prompts",
                ChievfxMcpToolPolicy.PromptSelectionPath,
                "enabledPromptNames",
                "prompts");
            var custom = toolTotals.Changed || resourceTotals.Changed || promptTotals.Changed;
            var profileName = !custom && !string.Equals(activeRoleKind, "manual", StringComparison.Ordinal)
                ? activeRoleDisplayName
                : "Custom";
            var aggregateTotals = SelectionTotals.Combine("All MCP", toolTotals, resourceTotals, promptTotals);

            parent.Add(CreateProfileDetailLabel($"Profile: {profileName}"));
            AddTotalsBreakdown(parent, aggregateTotals, toolTotals, resourceTotals, promptTotals);
            parent.Add(CreateProfileDetailLabel($"Status: {(custom ? "Custom changes present" : "Matches active role and default resources/prompts")}"));
        }

        private static void AddTotalsBreakdown(
            VisualElement parent,
            SelectionTotals aggregateTotals,
            SelectionTotals toolTotals,
            SelectionTotals resourceTotals,
            SelectionTotals promptTotals)
        {
            parent.Add(CreateProfileHeadlineLabel(aggregateTotals.FormatSelectedTokenTotals()));
            parent.Add(CreateProfileAllInfoLabel(aggregateTotals.FormatAllTokenTotals()));
            parent.Add(CreateProfileSubLabel($"Tools: {toolTotals.Format()}"));
            parent.Add(CreateProfileSubLabel($"Resources: {resourceTotals.Format()}"));
            parent.Add(CreateProfileSubLabel($"Prompts: {promptTotals.Format()}"));
        }

        private static Label CreateProfileHeadlineLabel(string text)
        {
            var label = CreateProfileBaseLabel(text);
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.fontSize = 13;
            label.style.color = new StyleColor(new Color(0.86f, 0.86f, 0.86f));
            label.style.marginTop = 6;
            return label;
        }

        private static Label CreateProfileAllInfoLabel(string text)
        {
            var label = CreateProfileBaseLabel(text);
            label.style.fontSize = 11;
            label.style.marginLeft = 12;
            label.style.color = new StyleColor(new Color(0.56f, 0.56f, 0.56f));
            return label;
        }

        private static Label CreateProfileSubLabel(string text)
        {
            var label = CreateProfileBaseLabel(text);
            label.style.fontSize = 11;
            label.style.marginLeft = 12;
            label.style.color = new StyleColor(new Color(0.62f, 0.62f, 0.62f));
            return label;
        }

        private static Label CreateProfileDetailLabel(string text)
        {
            var label = CreateProfileBaseLabel(text);
            label.style.color = new StyleColor(new Color(0.70f, 0.70f, 0.70f));
            return label;
        }

        private static Label CreateProfileWarningLabel(string text)
        {
            var label = CreateProfileBaseLabel(text);
            label.style.color = new StyleColor(new Color(1f, 0.78f, 0.48f));
            return label;
        }

        private static Label CreateProfileBaseLabel(string text)
        {
            var label = new Label(text);
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.marginTop = 1;
            label.style.marginBottom = 2;
            return label;
        }

        private bool CurrentToolsMatchActiveRole()
        {
            if (activeRoleManualOverride || string.Equals(activeRoleKind, "manual", StringComparison.Ordinal))
            {
                return false;
            }

            var role = FindRoleByKey(BuildRoleKey(activeRoleKind, activeRoleId, activeCustomRolePath));
            if (role == null)
            {
                return false;
            }

            var currentIds = new HashSet<string>(
                GetEffectiveSelectedRows().Select(row => row.Id),
                StringComparer.Ordinal);
            var roleIds = new HashSet<string>(GetRowsForRole(role).Select(row => row.Id), StringComparer.Ordinal);
            return currentIds.SetEquals(roleIds);
        }

        private static SelectionTotals ReadSelectionTotals(
            string label,
            string path,
            string enabledProperty,
            string metadataProperty,
            string? secondEnabledProperty = null,
            string? secondMetadataProperty = null)
        {
            if (!File.Exists(path))
            {
                return SelectionTotals.Unknown(label);
            }

            try
            {
                var root = JToken.Parse(File.ReadAllText(path));
                if (root is not JObject rootObj)
                {
                    return SelectionTotals.Unknown(label);
                }

                var first = ReadSelectionTotalsPart(rootObj, enabledProperty, metadataProperty);
                var hasSecond = !string.IsNullOrWhiteSpace(secondEnabledProperty) && !string.IsNullOrWhiteSpace(secondMetadataProperty);
                var second = hasSecond
                    ? ReadSelectionTotalsPart(rootObj, secondEnabledProperty!, secondMetadataProperty!)
                    : SelectionTotalsPart.Empty;
                return new SelectionTotals(
                    label,
                    first.SelectedCount + second.SelectedCount,
                    first.TotalCount + second.TotalCount,
                    first.SelectedDescriptorTokens + second.SelectedDescriptorTokens,
                    first.TotalDescriptorTokens + second.TotalDescriptorTokens,
                    first.SelectedDescriptionTokens + second.SelectedDescriptionTokens,
                    first.TotalDescriptionTokens + second.TotalDescriptionTokens,
                    first.Changed || second.Changed,
                    first.HasDescriptionMetadata || second.HasDescriptionMetadata,
                    known: hasSecond ? first.Known || second.Known : first.Known);
            }
            catch (JsonException)
            {
                return SelectionTotals.Unknown(label);
            }
        }

        private static SelectionTotalsPart ReadSelectionTotalsPart(JObject root, string enabledProperty, string metadataProperty)
        {
            if (root[metadataProperty] is not JObject metadata)
            {
                return SelectionTotalsPart.Unknown;
            }

            var allIds = new HashSet<string>(metadata.Properties().Select(property => property.Name), StringComparer.Ordinal);
            var enabledIds = root[enabledProperty] is JArray enabledArray
                ? new HashSet<string>(enabledArray
                    .Where(item => item.Type == JTokenType.String)
                    .Select(item => item.Value<string>() ?? string.Empty)
                    .Where(value => !string.IsNullOrWhiteSpace(value)), StringComparer.Ordinal)
                : new HashSet<string>(allIds, StringComparer.Ordinal);
            var selectedCount = 0;
            var totalDescriptorTokens = 0;
            var selectedDescriptorTokens = 0;
            var totalDescriptionTokens = 0;
            var selectedDescriptionTokens = 0;
            var hasDescriptionMetadata = false;

            foreach (var property in metadata.Properties())
            {
                var row = property.Value;
                var required = row["required"]?.Value<bool>() ?? false;
                var selected = required || enabledIds.Contains(property.Name);
                var descriptorTokens = ReadInt(row, "estimatedTokens");
                var descriptionTokens = ReadInt(row, "descriptionEstimatedTokens");
                hasDescriptionMetadata |= descriptionTokens > 0;
                totalDescriptorTokens += descriptorTokens;
                totalDescriptionTokens += descriptionTokens;
                if (!selected)
                {
                    continue;
                }

                selectedCount++;
                selectedDescriptorTokens += descriptorTokens;
                selectedDescriptionTokens += descriptionTokens;
            }

            return new SelectionTotalsPart(
                selectedCount,
                allIds.Count,
                selectedDescriptorTokens,
                totalDescriptorTokens,
                selectedDescriptionTokens,
                totalDescriptionTokens,
                !enabledIds.SetEquals(allIds),
                hasDescriptionMetadata,
                known: true);
        }

        private readonly struct SelectionTotals
        {
            public SelectionTotals(
                string label,
                int selectedCount,
                int totalCount,
                int selectedDescriptorTokens,
                int totalDescriptorTokens,
                int selectedDescriptionTokens,
                int totalDescriptionTokens,
                bool changed,
                bool hasDescriptionMetadata,
                bool known)
            {
                Label = label;
                SelectedCount = selectedCount;
                TotalCount = totalCount;
                SelectedDescriptorTokens = selectedDescriptorTokens;
                TotalDescriptorTokens = totalDescriptorTokens;
                SelectedDescriptionTokens = selectedDescriptionTokens;
                TotalDescriptionTokens = totalDescriptionTokens;
                Changed = changed;
                HasDescriptionMetadata = hasDescriptionMetadata;
                Known = known;
            }

            public string Label { get; }

            public int SelectedCount { get; }

            public int TotalCount { get; }

            public int SelectedDescriptorTokens { get; }

            public int TotalDescriptorTokens { get; }

            public int SelectedDescriptionTokens { get; }

            public int TotalDescriptionTokens { get; }

            public bool Changed { get; }

            public bool HasDescriptionMetadata { get; }

            public bool Known { get; }

            public static SelectionTotals FromRows(
                string label,
                int selectedCount,
                int totalCount,
                int selectedDescriptorTokens,
                int totalDescriptorTokens,
                int selectedDescriptionTokens,
                int totalDescriptionTokens,
                bool changed)
            {
                return new SelectionTotals(
                    label,
                    selectedCount,
                    totalCount,
                    selectedDescriptorTokens,
                    totalDescriptorTokens,
                    selectedDescriptionTokens,
                    totalDescriptionTokens,
                    changed,
                    hasDescriptionMetadata: totalDescriptionTokens > 0,
                    known: true);
            }

            public static SelectionTotals Unknown(string label)
            {
                return new SelectionTotals(label, 0, 0, 0, 0, 0, 0, changed: false, hasDescriptionMetadata: false, known: false);
            }

            public static SelectionTotals Combine(string label, params SelectionTotals[] totals)
            {
                return new SelectionTotals(
                    label,
                    totals.Sum(total => total.SelectedCount),
                    totals.Sum(total => total.TotalCount),
                    totals.Sum(total => total.SelectedDescriptorTokens),
                    totals.Sum(total => total.TotalDescriptorTokens),
                    totals.Sum(total => total.SelectedDescriptionTokens),
                    totals.Sum(total => total.TotalDescriptionTokens),
                    totals.Any(total => total.Changed),
                    totals.Any(total => total.HasDescriptionMetadata),
                    totals.Any(total => total.Known));
            }

            public string FormatTokenTotals()
            {
                if (!Known)
                {
                    return $"{Label}: selection metadata not saved yet";
                }

                return $"{Label}: {FormatTokenPair()}";
            }

            public string FormatSelectedTokenTotals()
            {
                if (!Known)
                {
                    return $"{Label}: selection metadata not saved yet";
                }

                var descriptionText = HasDescriptionMetadata
                    ? $"descriptions ~{SelectedDescriptionTokens}"
                    : "descriptions unavailable until metadata reload";
                return $"{Label}: selected descriptors ~{SelectedDescriptorTokens} | selected {descriptionText}";
            }

            public string FormatAllTokenTotals()
            {
                if (!Known)
                {
                    return string.Empty;
                }

                var descriptionText = HasDescriptionMetadata
                    ? $"descriptions ~{TotalDescriptionTokens}"
                    : "descriptions unavailable until metadata reload";
                return $"All available: descriptors ~{TotalDescriptorTokens} | {descriptionText}";
            }

            private string FormatTokenPair()
            {
                var descriptionText = HasDescriptionMetadata
                    ? $"descriptions ~{SelectedDescriptionTokens}/~{TotalDescriptionTokens}"
                    : "descriptions unavailable until metadata reload";
                return $"descriptors ~{SelectedDescriptorTokens}/~{TotalDescriptorTokens} | {descriptionText}";
            }

            public string Format()
            {
                if (!Known)
                {
                    return "selection metadata not saved yet";
                }

                return $"{SelectedCount}/{TotalCount} selected | {FormatTokenPair()}";
            }
        }

        private readonly struct SelectionTotalsPart
        {
            public SelectionTotalsPart(
                int selectedCount,
                int totalCount,
                int selectedDescriptorTokens,
                int totalDescriptorTokens,
                int selectedDescriptionTokens,
                int totalDescriptionTokens,
                bool changed,
                bool hasDescriptionMetadata,
                bool known)
            {
                SelectedCount = selectedCount;
                TotalCount = totalCount;
                SelectedDescriptorTokens = selectedDescriptorTokens;
                TotalDescriptorTokens = totalDescriptorTokens;
                SelectedDescriptionTokens = selectedDescriptionTokens;
                TotalDescriptionTokens = totalDescriptionTokens;
                Changed = changed;
                HasDescriptionMetadata = hasDescriptionMetadata;
                Known = known;
            }

            public int SelectedCount { get; }

            public int TotalCount { get; }

            public int SelectedDescriptorTokens { get; }

            public int TotalDescriptorTokens { get; }

            public int SelectedDescriptionTokens { get; }

            public int TotalDescriptionTokens { get; }

            public bool Changed { get; }

            public bool HasDescriptionMetadata { get; }

            public bool Known { get; }

            public static SelectionTotalsPart Empty => new(0, 0, 0, 0, 0, 0, changed: false, hasDescriptionMetadata: false, known: true);

            public static SelectionTotalsPart Unknown => new(0, 0, 0, 0, 0, 0, changed: false, hasDescriptionMetadata: false, known: false);
        }
    }
}
