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
        private void RenderTools()
        {
            toggles.Clear();
            categorySummaryLabels.Clear();
            categoryStateButtons.Clear();
            toolsList?.Clear();

            if (toolsList == null)
            {
                return;
            }

            if (!string.IsNullOrEmpty(loadError))
            {
                toolsList.Add(CreateToolLoadErrorState(loadError));
                return;
            }

            if (toolRows.Count == 0)
            {
                toolsList.Add(CreateEmptyState(
                    "No ChievFX MCP tool metadata found.",
                    "Reload metadata. Existing saved tool selection remains in effect if Cursor/server can still read it.",
                    ReloadMetadata));
                return;
            }

            var groups = ApplyQuickFilter(GetToolGroups(), quickFilterText, GetToolSearchText).ToList();
            if (!string.IsNullOrWhiteSpace(selectedCategory)
                && groups.All(group => !string.Equals(group.Category, selectedCategory, StringComparison.Ordinal)))
            {
                selectedCategory = null;
            }

            var hasQuickFilter = !string.IsNullOrWhiteSpace(quickFilterText);
            toolsList.Add(new HelpBox(
                hasQuickFilter
                    ? "Quick filter is active. All visible categories are expanded."
                    : ReadOnly
                        ? "Select a category to inspect the tools it exposes."
                        : "Choose a category first. Toggle category availability here, then select a category to inspect and tune individual tools.",
                HelpBoxMessageType.None));

            var detailAdded = false;
            foreach (var group in groups)
            {
                toolsList.Add(CreateCategoryElement(group.Category, group.Rows));
                if (hasQuickFilter || string.Equals(group.Category, selectedCategory, StringComparison.Ordinal))
                {
                    toolsList.Add(CreateToolDetail(group.Category, group.Rows));
                    detailAdded = true;
                }
            }

            if (!detailAdded)
            {
                var detail = CreateSectionCard("Category detail");
                detail.Add(CreateMutedLabel(groups.Count == 0
                    ? "No tools match the quick filter."
                    : "Select a category above to show its tools. Required tools stay locked on."));
                toolsList.Add(detail);
            }

            toolsList.Add(new HelpBox(
                "After changing enabled tools, reload MCP tools or restart Cursor. Running stdio/http MCP server processes read selection at runtime, but Cursor may cache descriptors.",
                HelpBoxMessageType.Warning));
        }

        private VisualElement CreateToolDetail(string category, IReadOnlyList<ToolRow> rows)
        {
            var detail = CreateSectionCard($"{category} tools");
            if (TryGetCategoryNotice(category, out var notice))
            {
                detail.Add(CreateCategoryNotice(notice));
            }

            foreach (var row in rows)
            {
                detail.Add(CreateToolElement(row));
            }

            return detail;
        }

        private IEnumerable<CategoryRows<ToolRow>> GetToolGroups()
        {
            return toolRows
                .Where(row => ChievfxMcpWindow.ShowExperimentalAutonomyTools
                    || !string.Equals(row.Category, AutonomousCategoryName, StringComparison.Ordinal))
                .GroupBy(row => row.Category)
                .OrderByDescending(group => group.All(row => row.Required))
                .ThenBy(group => GetCategorySortOrder(group.Key))
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new CategoryRows<ToolRow>(
                    group.Key,
                    group
                    .OrderByDescending(row => row.Required)
                    .ThenBy(row => row.Id, StringComparer.Ordinal)
                    .ToList()));
        }

        private static string GetToolSearchText(ToolRow row)
        {
            return $"{row.Id} {row.Category} {row.Description} {row.ResponseEstimateLabel}";
        }

        private VisualElement CreateToolLoadErrorState(string error)
        {
            return CreateLoadErrorState(
                "Could not load ChievFX MCP tool metadata.",
                error,
                $"Existing saved tool selection remains in effect if Cursor/server can still read:\n{ChievfxMcpToolPolicy.ToolSelectionPath}",
                ReloadMetadata);
        }

        private VisualElement CreateCategoryElement(string category, IReadOnlyList<ToolRow> rows)
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
                RenderTools();
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
                ? $"{category} ({rows.Count} tools)"
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
            if (TryGetCategoryNotice(category, out var notice))
            {
                container.Add(CreateCategoryNotice(notice));
            }

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

        private Button CreateCategoryStateButton(IReadOnlyList<ToolRow> rows, Action toggle)
        {
            var button = new Button(toggle);
            button.style.minWidth = 72;
            button.style.marginRight = 8;
            button.style.marginBottom = 4;
            button.style.unityFontStyleAndWeight = FontStyle.Bold;
            ApplyCategoryStateStyle(button, GetOptionalState(rows));
            return button;
        }


        private VisualElement CreateToolElement(ToolRow row)
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
                        MarkManualOverride();
                        SaveSelection();
                        RenderRoleControls();
                        RenderTools();
                        RefreshSummary();
                    }
                });
                toggles[row.Id] = toggle;
                header.Add(toggle);
            }

            var nameLabel = new Label(row.Id);
            nameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            nameLabel.style.flexBasis = 180;
            nameLabel.style.flexGrow = 1;
            nameLabel.style.minWidth = 0;
            nameLabel.style.whiteSpace = WhiteSpace.Normal;
            header.Add(nameLabel);
            header.Add(CreateCopyNameButton(row.Id, "tool"));

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

                var tokens = new Label($"Descriptor ~{row.EstimatedTokens} tok | Description ~{row.DescriptionEstimatedTokens} tok ({row.DescriptorBytes} B)");
                tokens.style.marginRight = 10;
                tokens.style.whiteSpace = WhiteSpace.Normal;
                meta.Add(tokens);

                var callOverhead = new Label($"Call base ~{row.CallEnvelopeEstimatedTokens} tok");
                callOverhead.style.whiteSpace = WhiteSpace.Normal;
                meta.Add(callOverhead);
                container.Add(meta);
            }

            var description = new Label(row.Description);
            description.style.whiteSpace = WhiteSpace.Normal;
            description.style.marginLeft = 28;
            description.style.marginTop = 2;
            description.style.color = new StyleColor(new Color(0.68f, 0.68f, 0.68f));
            container.Add(description);

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
                    tooltip: $"Exact descriptor JSON\nsha256 {row.DescriptorHash}\n{row.DescriptorBytes} B"));

                container.Add(CreatePreviewFoldout("Advanced: inputSchema JSON", row.SchemaJson, 72));
            }

            return container;
        }
    }
}
