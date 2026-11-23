#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
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

namespace Chievfx.Mcp.Editor
{
    internal sealed partial class ChievfxMcpResourceSelectionPanel
    {
        private void RenderResources()
        {
            toggles.Clear();
            categorySummaryLabels.Clear();
            categoryStateButtons.Clear();
            resourcesList?.Clear();

            if (resourcesList == null)
            {
                return;
            }

            if (!string.IsNullOrEmpty(loadError))
            {
                resourcesList.Add(CreateResourceLoadErrorState(loadError));
                return;
            }

            if (resourceRows.Count == 0)
            {
                resourcesList.Add(CreateEmptyState(
                    "No ChievFX MCP resource metadata found.",
                    "Reload metadata. Existing saved resource selection remains in effect if Cursor/server can still read it.",
                    ReloadMetadata));
                return;
            }

            var groups = ApplyQuickFilter(GetResourceGroups(), quickFilterText, GetResourceSearchText).ToList();
            if (!string.IsNullOrWhiteSpace(selectedCategory)
                && groups.All(group => !string.Equals(group.Category, selectedCategory, StringComparison.Ordinal)))
            {
                selectedCategory = null;
            }

            var hasQuickFilter = !string.IsNullOrWhiteSpace(quickFilterText);
            resourcesList.Add(new HelpBox(
                hasQuickFilter
                    ? "Quick filter is active. All visible categories are expanded."
                    : ReadOnly
                        ? "Select a category to inspect the resources and templates it exposes."
                        : "Choose a category first. Toggle category availability here, then select a category to inspect and tune individual resources or templates.",
                HelpBoxMessageType.None));

            var detailAdded = false;
            foreach (var group in groups)
            {
                resourcesList.Add(CreateCategoryElement(group.Category, group.Rows));
                if (hasQuickFilter || string.Equals(group.Category, selectedCategory, StringComparison.Ordinal))
                {
                    resourcesList.Add(CreateResourceDetail(group.Category, group.Rows));
                    detailAdded = true;
                }
            }

            if (!detailAdded)
            {
                var detail = CreateSectionCard("Category detail");
                detail.Add(CreateMutedLabel(groups.Count == 0
                    ? "No resources or templates match the quick filter."
                    : "Select a category above to show its resources and templates. Required rows stay locked on."));
                resourcesList.Add(detail);
            }

            resourcesList.Add(new HelpBox(reloadGuidance, HelpBoxMessageType.Warning));
        }

        private VisualElement CreateResourceDetail(string category, IReadOnlyList<ResourceRow> rows)
        {
            var detail = CreateSectionCard($"{category} resources");

            foreach (var row in rows)
            {
                detail.Add(CreateResourceElement(row));
            }

            return detail;
        }

        private IEnumerable<CategoryRows<ResourceRow>> GetResourceGroups()
        {
            return resourceRows
                .GroupBy(row => row.Category)
                .OrderByDescending(group => group.All(row => row.Required))
                .ThenBy(group => GetCategorySortOrder(group.Key))
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new CategoryRows<ResourceRow>(
                    group.Key,
                    group
                    .OrderByDescending(row => row.Required)
                    .ThenBy(row => row.Kind, StringComparer.Ordinal)
                    .ThenBy(row => row.Id, StringComparer.Ordinal)
                    .ToList()));
        }

        private static string GetResourceSearchText(ResourceRow row)
        {
            return $"{row.Id} {row.Name} {row.Category} {row.Kind} {row.Description} {row.Uri} {row.MimeType} {row.ResponseEstimateLabel}";
        }

        private VisualElement CreateResourceLoadErrorState(string error)
        {
            return CreateLoadErrorState(
                "Could not load ChievFX MCP resource metadata.",
                error,
                $"Existing saved resource selection remains in effect if Cursor/server can still read:\n{ChievfxMcpToolPolicy.ResourceSelectionPath}",
                ReloadMetadata);
        }

        private VisualElement CreateCategoryElement(string category, IReadOnlyList<ResourceRow> rows)
        {
            var container = new VisualElement
            {
                style =
                {
                    borderTopWidth = 1,
                    borderBottomWidth = 1,
                    borderLeftWidth = 1,
                    borderRightWidth = 1,
                    borderTopColor = new Color(0.25f, 0.25f, 0.25f),
                    borderBottomColor = new Color(0.25f, 0.25f, 0.25f),
                    borderLeftColor = new Color(0.25f, 0.25f, 0.25f),
                    borderRightColor = new Color(0.25f, 0.25f, 0.25f),
                    marginBottom = 8,
                    paddingLeft = 8,
                    paddingRight = 8,
                    paddingTop = 7,
                    paddingBottom = 7,
                    backgroundColor = string.Equals(selectedCategory, category, StringComparison.Ordinal)
                        ? new StyleColor(new Color(0.17f, 0.24f, 0.31f))
                        : new StyleColor(new Color(0.13f, 0.13f, 0.13f))
                }
            };
            container.tooltip = GetCategoryDescription(category);
            container.RegisterCallback<MouseUpEvent>(_ =>
            {
                selectedCategory = category;
                RenderResources();
                RefreshSummary();
            });

            var header = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    flexWrap = Wrap.Wrap,
                    marginTop = 6,
                    marginBottom = 4
                }
            };

            if (!ReadOnly)
            {
                var optionalRows = rows.Where(row => !row.Required).ToList();
                var stateButton = CreateCategoryStateButton(rows, () =>
                {
                    SetCategoryOptional(category, !AreAllOptionalEnabled(rows));
                });
                stateButton.SetEnabled(optionalRows.Count > 0);
                stateButton.tooltip = GetCategoryDescription(category);
                stateButton.RegisterCallback<MouseUpEvent>(evt => evt.StopPropagation());
                categoryStateButtons[category] = stateButton;
                header.Add(stateButton);
            }

            var enabledCount = rows.Count(row => row.Required || row.Enabled);
            var title = new Label(allInfo || ReadOnly
                ? $"{category} ({rows.Count} rows)"
                : $"{category} ({enabledCount}/{rows.Count})");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.fontSize = 14;
            title.style.flexBasis = 160;
            title.style.flexGrow = 1;
            title.style.minWidth = 0;
            title.style.whiteSpace = WhiteSpace.Normal;
            header.Add(title);

            if (allInfo && !ReadOnly)
            {
                header.Add(CreateAlwaysSupplyToggle(category));
            }

            if (allInfo)
            {
                var detail = new Label(BuildCategorySummary(rows));
                detail.style.flexBasis = 220;
                detail.style.flexGrow = 2;
                detail.style.minWidth = 0;
                detail.style.whiteSpace = WhiteSpace.Normal;
                detail.style.color = new StyleColor(new Color(0.72f, 0.72f, 0.72f));
                categorySummaryLabels[category] = detail;
                header.Add(detail);
            }

            container.Add(header);
            var intent = CreateMutedLabel(GetCategoryDescription(category));
            intent.style.marginLeft = 32;
            container.Add(intent);

            return container;
        }

        private Toggle CreateAlwaysSupplyToggle(string category)
        {
            var toggle = new Toggle("Always supply")
            {
                value = ChievfxMcpCategorySettings.IsAlwaysSupplied(category),
                tooltip = "Keep this category's tools/resources inline in MCP instructions instead of auto-collapsing them into a chievfx://categories link when it has more than 3 enabled items."
            };
            toggle.style.marginLeft = 8;
            toggle.style.marginRight = 4;
            toggle.style.flexShrink = 0;
            toggle.SetEnabled(!ChievfxMcpCategorySettings.ForceAll);
            toggle.RegisterCallback<MouseUpEvent>(evt => evt.StopPropagation());
            toggle.RegisterValueChangedCallback(evt =>
            {
                ChievfxMcpCategorySettings.SetCategoryAlwaysSupplied(category, evt.newValue);
                ChievfxMcpDebugInstructionsDumper.TryDump("unity-category-always-supply");
            });
            return toggle;
        }

        private Button CreateCategoryStateButton(IReadOnlyList<ResourceRow> rows, Action toggle)
        {
            var button = new Button(toggle);
            button.style.minWidth = 72;
            button.style.marginRight = 8;
            button.style.marginBottom = 4;
            button.style.unityFontStyleAndWeight = FontStyle.Bold;
            ApplyCategoryStateStyle(button, GetOptionalState(rows));
            return button;
        }

        private static OptionalState GetOptionalState(IReadOnlyList<ResourceRow> rows)
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

        private static bool AreAllOptionalEnabled(IReadOnlyList<ResourceRow> rows)
        {
            return rows.Where(row => !row.Required).All(row => row.Enabled);
        }

        private string GetCategoryDescription(string category)
        {
            if (categoryDescriptions.TryGetValue(category, out var description) && !string.IsNullOrWhiteSpace(description))
            {
                return description;
            }

            return category switch
            {
                "editor" => "Unity Editor state and diagnostic resources used while coordinating MCP work.",
                "scene" => "Scene-level context for hierarchy, objects, and current editor work.",
                "gameobject" => "GameObject-oriented templates and resources for targeted scene inspection.",
                "ugui-design" => "Editor-time uGUI Canvas, RectTransform, Image, TMP, and sprite authoring references.",
                "ugui-runtime-control" => "Play Mode uGUI runtime status, visible tree, canvases, and interactable-control references.",
                _ => "General ChievFX MCP resources and templates."
            };
        }

        private VisualElement CreateResourceElement(ResourceRow row)
        {
            var container = new VisualElement
            {
                style =
                {
                    borderBottomWidth = 1,
                    borderBottomColor = new Color(0.22f, 0.22f, 0.22f),
                    paddingTop = 6,
                    paddingBottom = 6
                }
            };

            var header = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    flexWrap = Wrap.Wrap
                }
            };

            if (!ReadOnly)
            {
                var toggle = new Toggle
                {
                    value = row.Enabled
                };
                toggle.SetEnabled(!row.Required);
                toggle.style.width = 24;
                toggle.RegisterValueChangedCallback(evt =>
                {
                    row.Enabled = row.Required || evt.newValue;
                    if (!suppressSave)
                    {
                        SaveSelection();
                        RenderResources();
                        RefreshSummary();
                    }
                });
                toggles[row.Key] = toggle;
                header.Add(toggle);
            }

            var nameLabel = new Label(row.Id);
            nameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            nameLabel.style.flexBasis = 180;
            nameLabel.style.flexGrow = 1;
            nameLabel.style.minWidth = 0;
            nameLabel.style.whiteSpace = WhiteSpace.Normal;
            header.Add(nameLabel);
            header.Add(CreateCopyNameButton(row.Id, "resource"));

            container.Add(header);

            if (allInfo)
            {
                var meta = CreateMetaRow();

                var badge = new Label(row.Required ? "Required" : "Optional");
                badge.style.marginRight = 10;
                badge.style.color = row.Required
                    ? new StyleColor(new Color(0.45f, 0.8f, 1f))
                    : new StyleColor(new Color(0.8f, 0.8f, 0.8f));
                meta.Add(badge);

                var kind = new Label(row.Kind);
                kind.style.marginRight = 10;
                kind.style.color = row.Kind == ResourceKind
                    ? new StyleColor(new Color(0.84f, 0.84f, 0.84f))
                    : new StyleColor(new Color(0.72f, 0.82f, 1f));
                meta.Add(kind);

                var tokens = new Label($"{row.Kind} desc ~{row.EstimatedTokens} tok | Description ~{row.DescriptionEstimatedTokens} tok ({row.DescriptorBytes} B)");
                tokens.style.marginRight = 10;
                tokens.style.whiteSpace = WhiteSpace.Normal;
                meta.Add(tokens);

                var readOverhead = new Label($"Read base ~{row.ReadEnvelopeEstimatedTokens} tok");
                readOverhead.style.whiteSpace = WhiteSpace.Normal;
                meta.Add(readOverhead);
                container.Add(meta);
            }

            var name = new Label(row.Name);
            name.style.whiteSpace = WhiteSpace.Normal;
            name.style.marginLeft = 28;
            name.style.marginTop = 2;
            name.style.color = new StyleColor(new Color(0.82f, 0.82f, 0.82f));
            container.Add(name);

            var description = new Label(row.Description);
            description.style.whiteSpace = WhiteSpace.Normal;
            description.style.marginLeft = 28;
            description.style.marginTop = 2;
            description.style.color = new StyleColor(new Color(0.68f, 0.68f, 0.68f));
            container.Add(description);

            var uri = new Label($"{(row.Kind == ResourceKind ? "URI" : "URI template")}: {row.Uri}");
            uri.style.whiteSpace = WhiteSpace.Normal;
            uri.style.marginLeft = 28;
            uri.style.marginTop = 2;
            uri.style.color = new StyleColor(new Color(0.64f, 0.78f, 0.92f));
            container.Add(uri);

            if (allInfo)
            {
                var response = new Label($"Response guide: {row.ResponseEstimateLabel}");
                response.style.whiteSpace = WhiteSpace.Normal;
                response.style.marginLeft = 28;
                response.style.marginTop = 2;
                response.style.color = new StyleColor(new Color(0.82f, 0.72f, 0.54f));
                container.Add(response);

                container.Add(CreatePreviewFoldout(
                    "Advanced: descriptor JSON",
                    row.DescriptorPreview,
                    tooltip: $"Exact {row.Kind.ToLowerInvariant()} descriptor JSON\nsha256 {row.DescriptorHash}\n{row.DescriptorBytes} B"));

                container.Add(CreatePreviewFoldout(
                    "Advanced: read envelope",
                    row.ReadEnvelopePreview,
                    72,
                    $"resources/read base envelope\n{row.ReadEnvelopeBytes} B"));
            }

            return container;
        }
    }
}
