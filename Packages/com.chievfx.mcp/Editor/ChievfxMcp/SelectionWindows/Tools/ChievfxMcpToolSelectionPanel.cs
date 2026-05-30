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
    internal sealed class ChievfxMcpToolSelectionPanel
    {
        private const string AllInfoEditorPrefsKey = "ChievfxMcp.Selection.AllInfo";

        private readonly List<ToolRow> toolRows = new();
        private readonly Dictionary<string, Toggle> toggles = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Label> categorySummaryLabels = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Button> categoryStateButtons = new(StringComparer.Ordinal);
        private readonly List<RoleDefinition> roleDefinitions = new();

        private Label? summaryLabel;
        private Label? saveFeedbackLabel;
        private Label? detailLabel;
        private VisualElement? roleSummaryDetails;
        private VisualElement? roleControls;
        private VisualElement? toolsList;
        private VisualElement? guiRoot;
        private bool guiShowTitle;
        private string selectedRoleKey = string.Empty;
        private string activeRoleKind = "manual";
        private string activeRoleId = string.Empty;
        private string activeRoleDisplayName = "Manual";
        private string activeCustomRolePath = string.Empty;
        private bool activeRoleManualOverride;
        private ChievfxMcpToolRoleAsset? selectedCustomRole;
        private string? selectedCategory;
        private string estimator = "unknown";
        private string descriptorEstimateBasis = "compact MCP tool descriptor JSON";
        private string descriptionEstimateBasis = "compact MCP tool name/description JSON";
        private string callEnvelopeEstimateBasis = "compact JSON-RPC tools/call envelope with empty arguments";
        private string responseEstimateNote = "Rough wrapped-result guidance only.";
        private string loadError = string.Empty;
        private string quickFilterText = string.Empty;
        private bool allInfo;
        private DateTime? lastSavedAtLocal;
        private bool suppressSave;

        public void CreateGUI(VisualElement root, bool showTitle = true)
        {
            guiRoot = root;
            guiShowTitle = showTitle;
            roleControls = null;
            toolsList = null;
            summaryLabel = null;
            saveFeedbackLabel = null;
            detailLabel = null;
            roleSummaryDetails = null;
            allInfo = EditorPrefs.GetBool(AllInfoEditorPrefsKey, false);

            VisualElement content;
            if (showTitle)
            {
                root.Clear();
                content = CreateRootScroll(root);
            }
            else
            {
                content = root;
            }

            if (showTitle)
            {
                content.Add(CreateTitleRow("ChievFX MCP Tools", allInfo, SetAllInfo));
            }

            summaryLabel = new Label();
            summaryLabel.style.marginTop = 8;
            summaryLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            summaryLabel.style.whiteSpace = WhiteSpace.Normal;
            content.Add(summaryLabel);

            if (allInfo)
            {
                saveFeedbackLabel = new Label("Optional tool changes auto-save.");
                saveFeedbackLabel.style.marginTop = 2;
                saveFeedbackLabel.style.marginBottom = 4;
                saveFeedbackLabel.style.color = new StyleColor(new Color(0.58f, 0.78f, 0.58f));
                saveFeedbackLabel.style.whiteSpace = WhiteSpace.Normal;
                content.Add(saveFeedbackLabel);
            }

            if (allInfo)
            {
                detailLabel = new Label();
                detailLabel.style.whiteSpace = WhiteSpace.Normal;
                detailLabel.style.marginBottom = 8;
                var detailsFoldout = new Foldout
                {
                    text = "Token/cache details",
                    value = false
                };
                detailsFoldout.Add(detailLabel);
                content.Add(detailsFoldout);
            }

            var actions = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    flexWrap = Wrap.Wrap,
                    marginBottom = 8
                }
            };
            actions.Add(CreateButton("Reload Metadata", ReloadMetadata));
            actions.Add(CreateButton("Save Selection", SaveSelection));
            actions.Add(CreateButton("Reset Required Minimum", ResetRequiredMinimum));
            actions.Add(CreateButton("Enable All", EnableAll));
            actions.Add(CreateButton("Disable Optional", DisableOptional));
            actions.Add(CreateButton("Presets", ChievfxMcpWindow.OpenPresets));
            actions.Add(CreateButton("Connection", ChievfxMcpWindow.OpenStatus));
            content.Add(actions);

            content.Add(CreateQuickFilterField(quickFilterText, value =>
            {
                quickFilterText = value;
                RenderTools();
                RefreshSummary();
            }));

            toolsList = new VisualElement();
            toolsList.style.flexGrow = 1;
            content.Add(toolsList);

            ReloadMetadata();
        }

        private void SetAllInfo(bool value)
        {
            allInfo = value;
            EditorPrefs.SetBool(AllInfoEditorPrefsKey, allInfo);
            if (guiRoot != null)
            {
                CreateGUI(guiRoot, guiShowTitle);
            }
        }

        public void CreateRolePresetGUI(VisualElement root)
        {
            roleControls = null;
            toolsList = null;
            summaryLabel = null;
            saveFeedbackLabel = null;
            detailLabel = null;
            roleSummaryDetails = null;

            root.Add(new HelpBox(
                "Role presets define the MCP capability profile for a session. Applying one updates advertised tools; resources and prompts can grow into the same model.",
                HelpBoxMessageType.Info));

            saveFeedbackLabel = new Label("Role changes auto-save.");
            saveFeedbackLabel.style.marginTop = 2;
            saveFeedbackLabel.style.marginBottom = 4;
            saveFeedbackLabel.style.color = new StyleColor(new Color(0.58f, 0.78f, 0.58f));
            saveFeedbackLabel.style.whiteSpace = WhiteSpace.Normal;
            root.Add(saveFeedbackLabel);

            roleControls = new VisualElement();
            root.Add(roleControls);

            ReloadMetadata();
        }

        private void ReloadMetadata()
        {
            loadError = string.Empty;
            toolRows.Clear();

            try
            {
                var metadata = ChievfxMcpToolMetadataRepository.LoadMetadata(
                    descriptorEstimateBasis,
                    descriptionEstimateBasis,
                    callEnvelopeEstimateBasis,
                    responseEstimateNote);
                toolRows.AddRange(metadata.Tools);
                estimator = metadata.Estimator;
                descriptorEstimateBasis = metadata.DescriptorEstimateBasis;
                descriptionEstimateBasis = metadata.DescriptionEstimateBasis;
                callEnvelopeEstimateBasis = metadata.CallEnvelopeEstimateBasis;
                responseEstimateNote = metadata.ResponseEstimateNote;
                LoadRoleDefinitions();
                ApplySavedSelection();
                SaveSelection();
            }
            catch (Exception ex)
            {
                loadError = ex.Message;
                Debug.LogWarning($"ChievFX MCP tool metadata load failed. {ex}");
            }

            RenderRoleControls();
            RenderTools();
            RefreshSummary();
        }


        private void ApplySavedSelection()
        {
            var requiredIds = new HashSet<string>(ChievfxMcpToolPolicy.RequiredToolIds, StringComparer.Ordinal);
            var enabledIds = new HashSet<string>(requiredIds, StringComparer.Ordinal);
            var hasSavedEnabledIds = false;
            activeRoleKind = "manual";
            activeRoleId = string.Empty;
            activeRoleDisplayName = "Manual";
            activeCustomRolePath = string.Empty;
            activeRoleManualOverride = false;

            if (File.Exists(ChievfxMcpToolPolicy.ToolSelectionPath))
            {
                try
                {
                    var root = JToken.Parse(File.ReadAllText(ChievfxMcpToolPolicy.ToolSelectionPath));
                    if (root is JObject rootObj)
                    {
                        if (rootObj["enabledToolIds"] is JArray enabledArray)
                        {
                            hasSavedEnabledIds = true;
                            foreach (var item in enabledArray)
                            {
                                if (item.Type == JTokenType.String)
                                {
                                    var id = item.Value<string>();
                                    if (!string.IsNullOrWhiteSpace(id))
                                    {
                                        enabledIds.Add(id!);
                                    }
                                }
                            }
                        }

                        if (rootObj["roleState"] is JObject roleState)
                        {
                            activeRoleKind = ReadString(roleState, "kind", "manual");
                            activeRoleId = ReadString(roleState, "roleId");
                            activeRoleDisplayName = ReadString(roleState, "displayName", string.IsNullOrWhiteSpace(activeRoleId) ? "Manual" : activeRoleId);
                            activeCustomRolePath = ReadString(roleState, "customAssetPath");
                            activeRoleManualOverride = roleState["manualOverride"]?.Value<bool>() ?? false;
                        }
                    }
                }
                catch (JsonException ex)
                {
                    Debug.LogWarning($"ChievFX MCP could not read tool selection. Required minimum will be used. {ex.Message}");
                }
            }

            if (!hasSavedEnabledIds)
            {
                var defaultRole = roleDefinitions.FirstOrDefault(role =>
                    string.Equals(role.Kind, "built-in", StringComparison.Ordinal)
                    && string.Equals(role.Id, "developer", StringComparison.Ordinal));
                if (defaultRole != null)
                {
                    enabledIds.UnionWith(ChievfxMcpToolPolicy.DefaultEnabledToolIds);
                    enabledIds.UnionWith(GetRowsForRole(defaultRole).Select(row => row.Id));
                    activeRoleKind = defaultRole.Kind;
                    activeRoleId = defaultRole.Id;
                    activeRoleDisplayName = defaultRole.DisplayName;
                    activeCustomRolePath = defaultRole.AssetPath;
                }
                else
                {
                    enabledIds.UnionWith(ChievfxMcpToolPolicy.DefaultEnabledToolIds);
                }
            }

            foreach (var row in toolRows)
            {
                row.Enabled = row.Required || enabledIds.Contains(row.Id);
            }

            selectedRoleKey = BuildRoleKey(activeRoleKind, activeRoleId, activeCustomRolePath);
            selectedCustomRole = string.IsNullOrWhiteSpace(activeCustomRolePath)
                ? null
                : AssetDatabase.LoadAssetAtPath<ChievfxMcpToolRoleAsset>(activeCustomRolePath);
        }

        private void RenderRoleControls()
        {
            roleControls?.Clear();
            if (roleControls == null)
            {
                return;
            }

            var currentCard = CreateSectionCard("Current MCP profile");
            currentCard.Add(CreateMutedLabel("What Cursor currently sees after saved tool/resource/prompt selection is applied."));
            AddCurrentProfileSummary(currentCard);
            roleControls.Add(currentCard);

            var card = CreateSectionCard("Role presets");
            card.Add(CreateMutedLabel("Choose a role, preview its tool impact, then apply it. Required tools stay locked on."));

            var choices = roleDefinitions.Select(role => role.Key).ToList();
            if (choices.Count == 0)
            {
                card.Add(CreateMutedLabel("No role presets found. Manual tool controls still work."));
                roleControls.Add(card);
                return;
            }

            if (string.IsNullOrWhiteSpace(selectedRoleKey) || !choices.Contains(selectedRoleKey))
            {
                selectedRoleKey = choices[0];
            }

            var popup = new PopupField<string>("Role to apply", choices, selectedRoleKey, FormatRoleChoice, FormatRoleChoice);
            popup.RegisterValueChangedCallback(evt =>
            {
                selectedRoleKey = evt.newValue;
                RefreshRoleSummary();
            });
            card.Add(popup);

            roleSummaryDetails = new VisualElement();
            card.Add(roleSummaryDetails);

            var roleActions = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    flexWrap = Wrap.Wrap,
                    marginTop = 4
                }
            };
            Button? saveCustomRoleButton = null;
            Label? customRoleSaveHint = null;
            void RefreshCustomRoleSaveAffordance()
            {
                if (saveCustomRoleButton == null || customRoleSaveHint == null)
                {
                    return;
                }

                var hasCustomRole = selectedCustomRole != null;
                saveCustomRoleButton.SetEnabled(hasCustomRole);
                customRoleSaveHint.text = hasCustomRole
                    ? $"Save will update {selectedCustomRole!.name}."
                    : "Create Custom Role or assign a Custom role asset to enable saving the current tool selection.";
            }

            roleActions.Add(CreateButton("Apply Role", ApplySelectedRole));
            roleActions.Add(CreateButton("Reset", ResetRoleState));
            roleActions.Add(CreateButton("Create Custom Role", CreateCustomRoleAsset));
            saveCustomRoleButton = CreateButton("Save Selection To Custom Role", SaveCurrentSelectionToCustomRole);
            roleActions.Add(saveCustomRoleButton);
            card.Add(roleActions);

            var customField = new ObjectField("Custom role asset")
            {
                objectType = typeof(ChievfxMcpToolRoleAsset),
                value = selectedCustomRole
            };
            customField.RegisterValueChangedCallback(evt =>
            {
                selectedCustomRole = evt.newValue as ChievfxMcpToolRoleAsset;
                RefreshCustomRoleSaveAffordance();
                if (selectedCustomRole != null)
                {
                    var path = AssetDatabase.GetAssetPath(selectedCustomRole);
                    var match = roleDefinitions.FirstOrDefault(role => string.Equals(role.AssetPath, path, StringComparison.Ordinal));
                    if (match != null)
                    {
                        selectedRoleKey = match.Key;
                        RenderRoleControls();
                    }
                }
            });
            card.Add(customField);

            customRoleSaveHint = CreateMutedLabel(string.Empty);
            card.Add(customRoleSaveHint);

            roleControls.Add(card);
            RefreshCustomRoleSaveAffordance();
            RefreshRoleSummary();
        }

        private string FormatRoleChoice(string key)
        {
            var role = FindRoleByKey(key);
            return role == null ? key : $"{role.DisplayName} ({role.Kind})";
        }

        private void RefreshRoleSummary()
        {
            if (roleSummaryDetails == null)
            {
                return;
            }

            roleSummaryDetails.Clear();
            var selectedRole = FindRoleByKey(selectedRoleKey);
            var activeName = activeRoleManualOverride ? $"{activeRoleDisplayName} (modified)" : activeRoleDisplayName;
            if (selectedRole == null)
            {
                roleSummaryDetails.Add(CreateProfileDetailLabel($"Active: {activeName}"));
                return;
            }

            var availableCategories = new HashSet<string>(toolRows.Select(row => row.Category), StringComparer.Ordinal);
            var availableTools = new HashSet<string>(toolRows.Select(row => row.Id), StringComparer.Ordinal);
            var missingCategories = selectedRole.EnabledCategoryIds.Where(id => !availableCategories.Contains(id)).ToList();
            var missingTools = selectedRole.EnabledToolIds.Where(id => !availableTools.Contains(id)).ToList();
            var selectedRows = GetRowsForRole(selectedRole);
            var matchedOptional = selectedRows.Count(row => !row.Required);
            var requiredCount = selectedRows.Count(row => row.Required);
            var callTokens = selectedRows.Sum(row => row.CallEnvelopeEstimatedTokens);
            var roleToolTotals = SelectionTotals.FromRows(
                "Tools",
                selectedRows.Count,
                toolRows.Count,
                selectedRows.Sum(row => row.EstimatedTokens),
                toolRows.Sum(row => row.EstimatedTokens),
                selectedRows.Sum(row => row.DescriptionEstimatedTokens),
                toolRows.Sum(row => row.DescriptionEstimatedTokens),
                changed: true);
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
            var aggregateTotals = SelectionTotals.Combine("All MCP", roleToolTotals, resourceTotals, promptTotals);
            var defaultEnabledIds = new HashSet<string>(ChievfxMcpToolPolicy.DefaultEnabledToolIds, StringComparer.Ordinal);
            var categoryList = selectedRows
                .Select(row => row.Category)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(GetCategorySortOrder)
                .ThenBy(category => category, StringComparer.Ordinal)
                .ToList();
            var explicitToolList = selectedRows
                .Where(row => !row.Required && (defaultEnabledIds.Contains(row.Id) || selectedRole.EnabledToolIds.Contains(row.Id)))
                .Select(row => row.Id)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();

            var targetSummary = BuildRoleTargetSummary(selectedRole);
            roleSummaryDetails.Add(CreateProfileDetailLabel($"Active: {activeName}"));
            roleSummaryDetails.Add(CreateProfileDetailLabel($"Role to apply: {selectedRole.DisplayName} ({selectedRole.Kind})"));
            roleSummaryDetails.Add(CreateProfileDetailLabel($"Purpose: {selectedRole.Description}"));
            AddTotalsBreakdown(roleSummaryDetails, aggregateTotals, roleToolTotals, resourceTotals, promptTotals);
            roleSummaryDetails.Add(CreateProfileDetailLabel($"Tools after apply: {selectedRows.Count}/{toolRows.Count} ({requiredCount} required, {matchedOptional} optional) | Call base: ~{callTokens}"));
            roleSummaryDetails.Add(CreateProfileDetailLabel($"Categories: {FormatCompactList(categoryList, 8)}"));
            roleSummaryDetails.Add(CreateProfileDetailLabel($"Direct/default tools: {FormatCompactList(explicitToolList, 8)}{targetSummary}"));
            if (missingCategories.Count > 0)
            {
                roleSummaryDetails.Add(CreateProfileWarningLabel($"Unavailable categories: {string.Join(", ", missingCategories)}"));
            }

            if (missingTools.Count > 0)
            {
                roleSummaryDetails.Add(CreateProfileWarningLabel($"Unavailable tools: {string.Join(", ", missingTools)}"));
            }
        }


        private RoleDefinition? FindRoleByKey(string key)
        {
            return roleDefinitions.FirstOrDefault(role => string.Equals(role.Key, key, StringComparison.Ordinal));
        }

        private RoleDefinition? FindDeveloperRole()
        {
            return roleDefinitions.FirstOrDefault(role =>
                string.Equals(role.Kind, "built-in", StringComparison.Ordinal)
                && string.Equals(role.Id, "developer", StringComparison.Ordinal));
        }

        private List<ToolRow> GetRowsForRole(RoleDefinition role)
        {
            var defaultEnabledIds = new HashSet<string>(ChievfxMcpToolPolicy.DefaultEnabledToolIds, StringComparer.Ordinal);
            return toolRows.Where(row =>
                    row.Required
                    || defaultEnabledIds.Contains(row.Id)
                    || role.EnabledCategoryIds.Contains(row.Category)
                    || role.EnabledToolIds.Contains(row.Id))
                .ToList();
        }

        private void AddCurrentProfileSummary(VisualElement parent)
        {
            var selectedToolRows = toolRows.Where(row => row.Enabled || row.Required).ToList();
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
                toolRows.Where(row => row.Enabled || row.Required).Select(row => row.Id),
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

        private void ApplySelectedRole()
        {
            var role = FindRoleByKey(selectedRoleKey);
            if (role == null)
            {
                return;
            }

            var defaultEnabledIds = new HashSet<string>(ChievfxMcpToolPolicy.DefaultEnabledToolIds, StringComparer.Ordinal);
            foreach (var row in toolRows)
            {
                row.Enabled = row.Required
                    || defaultEnabledIds.Contains(row.Id)
                    || role.EnabledCategoryIds.Contains(row.Category)
                    || role.EnabledToolIds.Contains(row.Id);
            }

            activeRoleKind = role.Kind;
            activeRoleId = role.Id;
            activeRoleDisplayName = role.DisplayName;
            activeCustomRolePath = role.AssetPath;
            activeRoleManualOverride = false;
            selectedCustomRole = role.Asset;
            SyncTogglesFromRows();
            SaveSelection();
            SavePromptSelectionForRole(role);
            RenderRoleControls();
            RenderTools();
            RefreshSummary();
        }

        private static void SavePromptSelectionForRole(RoleDefinition role)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ChievfxMcpToolPolicy.PromptSelectionPath)!);
            var root = new JObject
            {
                ["schemaVersion"] = 1,
                ["updatedAtUtc"] = DateTime.UtcNow.ToString("O"),
                ["source"] = "Tools/ChievfxMcp/chievfx_mcp_role_presets.json",
                ["enabledPromptNames"] = new JArray(role.EnabledPromptNames.OrderBy(name => name, StringComparer.Ordinal))
            };
            File.WriteAllText(ChievfxMcpToolPolicy.PromptSelectionPath, root.ToString(Formatting.Indented) + Environment.NewLine, new UTF8Encoding(false));
        }

        private void ResetRoleState()
        {
            var developerRole = FindDeveloperRole();
            if (developerRole == null)
            {
                EditorUtility.DisplayDialog("ChievFX MCP", "Developer role preset could not be found.", "OK");
                return;
            }

            selectedRoleKey = developerRole.Key;
            ApplySelectedRole();
        }

        private void CreateCustomRoleAsset()
        {
            Directory.CreateDirectory(Path.Combine(ChievfxMcpToolPolicy.ProjectRoot, ChievfxMcpToolPolicy.ToolRoleAssetDefaultDirectory));
            var path = AssetDatabase.GenerateUniqueAssetPath($"{ChievfxMcpToolPolicy.ToolRoleAssetDefaultDirectory}/CustomMcpRole.asset");
            var asset = ScriptableObject.CreateInstance<ChievfxMcpToolRoleAsset>();
            asset.roleId = "custom-" + Guid.NewGuid().ToString("N");
            asset.displayName = "Custom MCP Role";
            asset.description = "Project-specific MCP role. Edit categories/tools here or save current selection into it.";
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            Selection.activeObject = asset;
            selectedCustomRole = asset;
            LoadRoleDefinitions();
            selectedRoleKey = BuildRoleKey("custom", asset.roleId, path);
            RenderRoleControls();
        }

        private void SaveCurrentSelectionToCustomRole()
        {
            var role = selectedCustomRole;
            if (role == null)
            {
                role = FindRoleByKey(selectedRoleKey)?.Asset;
            }

            if (role == null)
            {
                EditorUtility.DisplayDialog("ChievFX MCP", "Select or create a custom role asset first.", "OK");
                return;
            }

            role.enabledCategoryIds = toolRows
                .Where(row => !row.Required && row.Enabled)
                .GroupBy(row => row.Category)
                .Where(group => group.All(row => row.Enabled))
                .Select(group => group.Key)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
            var categorySet = new HashSet<string>(role.enabledCategoryIds, StringComparer.Ordinal);
            role.enabledToolIds = toolRows
                .Where(row => !row.Required && row.Enabled && !categorySet.Contains(row.Category))
                .Select(row => row.Id)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
            EditorUtility.SetDirty(role);
            AssetDatabase.SaveAssets();
            LoadRoleDefinitions();
            selectedRoleKey = BuildRoleKey("custom", role.roleId, AssetDatabase.GetAssetPath(role));
            RenderRoleControls();
        }

        private void LoadRoleDefinitions()
        {
            roleDefinitions.Clear();
            roleDefinitions.AddRange(ChievfxMcpToolRoleRepository.LoadRoleDefinitions());
        }

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
                .GroupBy(row => row.Category)
                .OrderBy(group => GetCategorySortOrder(group.Key))
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

            var enabledCount = rows.Count(row => row.Required || row.Enabled);
            var title = new Label(allInfo
                ? $"{category} ({rows.Count} tools)"
                : $"{category} ({enabledCount}/{rows.Count})");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.fontSize = 14;
            title.style.flexBasis = 160;
            title.style.flexGrow = 1;
            title.style.minWidth = 0;
            title.style.whiteSpace = WhiteSpace.Normal;
            header.Add(title);

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

        private void SaveSelection()
        {
            if (toolRows.Count == 0)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(ChievfxMcpToolPolicy.ToolSelectionPath)!);
            using (var stream = new FileStream(ChievfxMcpToolPolicy.ToolSelectionPath, FileMode.Create, FileAccess.Write))
            using (var streamWriter = new StreamWriter(stream, new UTF8Encoding(false)))
            using (var writer = new JsonTextWriter(streamWriter) { Formatting = Formatting.Indented })
            {
                writer.WriteStartObject();
                writer.WritePropertyName("schemaVersion");
                writer.WriteValue(1);
                writer.WritePropertyName("updatedAtUtc");
                writer.WriteValue(DateTime.UtcNow.ToString("O"));
                writer.WritePropertyName("source");
                writer.WriteValue("Tools/ChievfxMcp/chievfx_mcp_server.py:TOOLS");
                writer.WritePropertyName("estimator");
                writer.WriteValue(estimator);
                writer.WritePropertyName("note");
                writer.WriteValue("Token counts estimate compact JSON MCP descriptors only; not exact billable request tokens.");
                writer.WritePropertyName("descriptorEstimateBasis");
                writer.WriteValue(descriptorEstimateBasis);
                writer.WritePropertyName("descriptionEstimateBasis");
                writer.WriteValue(descriptionEstimateBasis);
                writer.WritePropertyName("callEnvelopeEstimateBasis");
                writer.WriteValue(callEnvelopeEstimateBasis);
                writer.WritePropertyName("responseEstimateNote");
                writer.WriteValue(responseEstimateNote);

                writer.WritePropertyName("roleState");
                writer.WriteStartObject();
                writer.WritePropertyName("kind");
                writer.WriteValue(activeRoleKind);
                writer.WritePropertyName("roleId");
                writer.WriteValue(activeRoleId);
                writer.WritePropertyName("displayName");
                writer.WriteValue(activeRoleDisplayName);
                writer.WritePropertyName("customAssetPath");
                writer.WriteValue(activeCustomRolePath);
                writer.WritePropertyName("manualOverride");
                writer.WriteValue(activeRoleManualOverride);
                writer.WritePropertyName("appliedEnabledToolIds");
                writer.WriteStartArray();
                foreach (var row in toolRows.Where(row => row.Enabled || row.Required).OrderBy(row => row.Id, StringComparer.Ordinal))
                {
                    writer.WriteValue(row.Id);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();

                writer.WritePropertyName("enabledToolIds");
                writer.WriteStartArray();
                foreach (var row in toolRows.Where(row => row.Enabled || row.Required).OrderBy(row => row.Id, StringComparer.Ordinal))
                {
                    writer.WriteValue(row.Id);
                }
                writer.WriteEndArray();

                writer.WritePropertyName("tools");
                writer.WriteStartObject();
                foreach (var row in toolRows.OrderBy(row => row.Id, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(row.Id);
                    writer.WriteStartObject();
                    writer.WritePropertyName("descriptorHash");
                    writer.WriteValue(row.DescriptorHash);
                    writer.WritePropertyName("estimatedTokens");
                    writer.WriteValue(row.EstimatedTokens);
                    writer.WritePropertyName("descriptionEstimatedTokens");
                    writer.WriteValue(row.DescriptionEstimatedTokens);
                    writer.WritePropertyName("descriptorBytes");
                    writer.WriteValue(row.DescriptorBytes);
                    writer.WritePropertyName("callEnvelopeEstimatedTokens");
                    writer.WriteValue(row.CallEnvelopeEstimatedTokens);
                    writer.WritePropertyName("callEnvelopeBytes");
                    writer.WriteValue(row.CallEnvelopeBytes);
                    writer.WritePropertyName("required");
                    writer.WriteValue(row.Required);
                    writer.WritePropertyName("category");
                    writer.WriteValue(row.Category);
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();

                writer.WriteEndObject();
            }

            lastSavedAtLocal = DateTime.Now;
            RefreshSaveFeedback();
        }

        private void ResetRequiredMinimum()
        {
            MarkManualOverride(clearRole: true);
            foreach (var row in toolRows)
            {
                row.Enabled = row.Required;
            }

            SyncTogglesFromRows();
            SaveSelection();
            RenderRoleControls();
            RenderTools();
            RefreshSummary();
        }

        private void EnableAll()
        {
            MarkManualOverride(clearRole: true);
            foreach (var row in toolRows)
            {
                if (IsObsoleteTool(row))
                {
                    continue;
                }

                row.Enabled = true;
            }

            SyncTogglesFromRows();
            SaveSelection();
            RenderRoleControls();
            RenderTools();
            RefreshSummary();
        }

        private void DisableOptional()
        {
            MarkManualOverride(clearRole: true);
            foreach (var row in toolRows)
            {
                row.Enabled = row.Required;
            }

            SyncTogglesFromRows();
            SaveSelection();
            RenderRoleControls();
            RenderTools();
            RefreshSummary();
        }

        private static bool IsObsoleteTool(ToolRow row)
        {
            return string.Equals(row.Category, "OBSOLETE", StringComparison.Ordinal);
        }

        private void SetCategoryOptional(string category, bool enabled)
        {
            MarkManualOverride();
            foreach (var row in toolRows.Where(row => row.Category == category && !row.Required))
            {
                row.Enabled = enabled;
            }

            SyncTogglesFromRows();
            SaveSelection();
            RenderRoleControls();
            RenderTools();
            RefreshSummary();
        }

        private void MarkManualOverride(bool clearRole = false)
        {
            if (clearRole || activeRoleKind == "manual")
            {
                activeRoleKind = "manual";
                activeRoleId = string.Empty;
                activeRoleDisplayName = "Manual";
                activeCustomRolePath = string.Empty;
                activeRoleManualOverride = false;
                return;
            }

            activeRoleManualOverride = true;
        }

        private void SyncTogglesFromRows()
        {
            suppressSave = true;
            try
            {
                foreach (var row in toolRows)
                {
                    if (toggles.TryGetValue(row.Id, out var toggle))
                    {
                        toggle.value = row.Enabled || row.Required;
                    }
                }
            }
            finally
            {
                suppressSave = false;
            }
        }

        private void RefreshCategorySummaries()
        {
            foreach (var group in toolRows.GroupBy(row => row.Category))
            {
                if (categorySummaryLabels.TryGetValue(group.Key, out var label))
                {
                    var rows = group.ToList();
                    label.text = BuildCategorySummary(rows);
                    if (categoryStateButtons.TryGetValue(group.Key, out var button))
                    {
                        ApplyCategoryStateStyle(button, GetOptionalState(rows));
                    }
                }
            }
        }

        private void RefreshSummary()
        {
            var selectedRows = toolRows.Where(row => row.Enabled || row.Required).ToList();
            var selectedDescriptorTokens = selectedRows.Sum(row => row.EstimatedTokens);
            var allDescriptorTokens = toolRows.Sum(row => row.EstimatedTokens);
            var selectedDescriptionTokens = selectedRows.Sum(row => row.DescriptionEstimatedTokens);
            var allDescriptionTokens = toolRows.Sum(row => row.DescriptionEstimatedTokens);
            var selectedCallTokens = selectedRows.Sum(row => row.CallEnvelopeEstimatedTokens);
            var allCallTokens = toolRows.Sum(row => row.CallEnvelopeEstimatedTokens);
            var requiredCount = toolRows.Count(row => row.Required);
            var optionalCount = toolRows.Count(row => !row.Required);
            var selectedOptionalCount = selectedRows.Count(row => !row.Required);
            var categoryCount = toolRows.Select(row => row.Category).Distinct(StringComparer.Ordinal).Count();

            if (summaryLabel != null)
            {
                summaryLabel.text = allInfo
                    ? $"Selected descriptors: ~{selectedDescriptorTokens} tokens across {selectedRows.Count}/{toolRows.Count} tools | " +
                      $"All tools descriptors: ~{allDescriptorTokens} tokens\n" +
                      $"Selected descriptions: ~{selectedDescriptionTokens} tokens | All tools descriptions: ~{allDescriptionTokens} tokens"
                    : $"Selected descriptors: ~{selectedDescriptorTokens} tokens across {selectedRows.Count}/{toolRows.Count} tools\n" +
                      $"Selected descriptions: ~{selectedDescriptionTokens} tokens";
            }

            if (detailLabel != null)
            {
                detailLabel.text =
                    $"Categories: {categoryCount} | Required: {requiredCount} locked | Optional: {selectedOptionalCount}/{optionalCount} enabled | Estimator: {estimator}\n" +
                    $"Descriptor basis: {descriptorEstimateBasis}\n" +
                    $"Description basis: selected ~{selectedDescriptionTokens} tokens, all tools ~{allDescriptionTokens}. Descriptors already include name/description; this line estimates discovery surface separately. {descriptionEstimateBasis}\n" +
                    $"Call-envelope base: selected ~{selectedCallTokens} tokens, all tools ~{allCallTokens}. {callEnvelopeEstimateBasis}\n" +
                    $"Responses: {responseEstimateNote} Screenshot tools return image content; visual-token billing is model/client specific.\n" +
                    $"Selection file: {ChievfxMcpToolPolicy.ToolSelectionPath}\n" +
                    "Reload MCP tools or restart Cursor after changing selection.";
            }

            RefreshCategorySummaries();
        }


        private void RefreshSaveFeedback()
        {
            if (saveFeedbackLabel == null)
            {
                return;
            }

            saveFeedbackLabel.text = lastSavedAtLocal.HasValue
                ? $"Saved at {lastSavedAtLocal.Value:HH:mm:ss}."
                : "Optional tool changes auto-save.";
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
