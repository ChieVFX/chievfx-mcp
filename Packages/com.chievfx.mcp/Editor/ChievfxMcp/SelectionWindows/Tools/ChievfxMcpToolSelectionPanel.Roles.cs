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
                    (ChievfxMcpWindow.ShowExperimentalAutonomyTools || !IsAutonomyTool(row) || row.Required)
                    && (row.Required
                    || defaultEnabledIds.Contains(row.Id)
                    || role.EnabledCategoryIds.Contains(row.Category)
                    || role.EnabledToolIds.Contains(row.Id)))
                .ToList();
        }

        private void ApplySelectedRole()
        {
            var role = FindRoleByKey(selectedRoleKey);
            if (role == null)
            {
                return;
            }

            // A role is a saved selection, honored only with manual selection on, as tools-set-role does: applied from a window
            // drawn before the switch went off, it said "Matches active role" while the server kept every tool on.
            if (ChievfxMcpToolPolicy.ManualToolResourceSelection == false)
            {
                ChievfxMcpToolPolicy.SetManualToolResourceSelection(true);
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
            if (!ChievfxMcpWindow.ShowExperimentalPromptsTab)
            {
                ChievfxMcpPromptSelectionPanel.DisableAllSavedPrompts();
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(ChievfxMcpToolPolicy.PromptSelectionPath)!);
            var root = new JObject
            {
                ["schemaVersion"] = 1,
                ["updatedAtUtc"] = DateTime.UtcNow.ToString("O"),
                ["source"] = "Tools~/ChievfxMcp/chievfx_mcp_role_presets.json",
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
    }
}
