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
        private const string AllInfoEditorPrefsKey = "ChievfxMcp.Selection.AllInfo";
        private const string AutonomousCategoryName = "autonomous";

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

        // Default expose-all mode: the Tools tab is a read-only listing (no per-tool/category toggles,
        // no bulk/role controls). Interactive selection only when the user opts into manual mode.
        private static bool ReadOnly => !ChievfxMcpToolPolicy.ManualToolResourceSelection;

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

            if (ReadOnly)
            {
                content.Add(new HelpBox(
                    "All non-hidden tools are available. This list is read-only. To choose tools and categories yourself, turn on \"Customize which tools & resources are exposed\" in Connection.",
                    HelpBoxMessageType.Info));
                content.Add(CreateButton("Connection", ChievfxMcpWindow.OpenStatus));
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
                return;
            }

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
            actions.Add(CreateButton("Save Selection", () => SaveSelection()));
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

        public static void RemoveAutonomyToolsFromSavedSelection()
        {
            if (!File.Exists(ChievfxMcpToolPolicy.ToolSelectionPath))
            {
                return;
            }

            try
            {
                var root = JToken.Parse(File.ReadAllText(ChievfxMcpToolPolicy.ToolSelectionPath));
                if (root is not JObject rootObj)
                {
                    return;
                }

                var removed = RemoveAutonomyToolIds(rootObj["enabledToolIds"]);
                if (rootObj["roleState"] is JObject roleState)
                {
                    removed |= RemoveAutonomyToolIds(roleState["appliedEnabledToolIds"]);
                }

                if (!removed)
                {
                    return;
                }

                rootObj["updatedAtUtc"] = DateTime.UtcNow.ToString("O");
                File.WriteAllText(ChievfxMcpToolPolicy.ToolSelectionPath, rootObj.ToString(Formatting.Indented) + Environment.NewLine, new UTF8Encoding(false));
                ChievfxMcpDebugInstructionsDumper.TryDump("unity-autonomy-tools-hidden");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                Debug.LogWarning($"ChievFX MCP could not force hidden Autonomy tools off. {ex.Message}");
            }
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
                if (ReadOnly)
                {
                    // Reflect the expose-all advertised set (every non-hidden tool) so summaries/counts
                    // match what the server serves. Never persist — the saved manual selection stays
                    // intact for when the user turns customization back on.
                    foreach (var row in toolRows)
                    {
                        row.Enabled = row.Required || (!IsAutonomyTool(row) && !IsObsoleteTool(row));
                    }
                }
                else
                {
                    SaveSelection(dumpDebugInstructions: false);
                }
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
            requiredIds.UnionWith(toolRows
                .Where(row => string.Equals(row.Category, "essentials", StringComparison.Ordinal))
                .Select(row => row.Id));
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
                // First install: enable every tool except the autonomy/discovery helpers,
                // which stay hidden from the Tools tab by default. Leave the role state as the
                // "manual" defaults set above, since this selection is not a built-in role preset.
                enabledIds.UnionWith(ChievfxMcpToolPolicy.DefaultEnabledToolIds);
                enabledIds.UnionWith(toolRows
                    .Where(row => row.Required || !IsAutonomyTool(row))
                    .Select(row => row.Id));
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

        private void SaveSelection(bool dumpDebugInstructions = true)
        {
            if (toolRows.Count == 0)
            {
                return;
            }

            ForceHiddenAutonomyToolsOff();
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
                writer.WriteValue("Tools~/ChievfxMcp/chievfx_mcp_server.py:TOOLS");
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
            // Skip the python dump + log on the initial metadata reload (window open) so merely
            // opening the panel does not spawn a process or rewrite debug_instructions.md.
            if (dumpDebugInstructions)
            {
                ChievfxMcpDebugInstructionsDumper.TryDump("unity-tool-selection-save");
            }
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
            return string.Equals(row.Category, "obsolete", StringComparison.Ordinal);
        }

        private void ForceHiddenAutonomyToolsOff()
        {
            if (ChievfxMcpWindow.ShowExperimentalAutonomyTools)
            {
                return;
            }

            foreach (var row in toolRows.Where(row => !row.Required && IsAutonomyTool(row)))
            {
                row.Enabled = false;
            }
        }

        private static bool IsAutonomyTool(ToolRow row)
        {
            return string.Equals(row.Category, AutonomousCategoryName, StringComparison.Ordinal)
                || ChievfxMcpToolPolicy.AutonomousToolIds.Contains(row.Id, StringComparer.Ordinal);
        }

        private static bool RemoveAutonomyToolIds(JToken? token)
        {
            if (token is not JArray array)
            {
                return false;
            }

            var autonomyToolIds = new HashSet<string>(ChievfxMcpToolPolicy.AutonomousToolIds, StringComparer.Ordinal);
            var removed = false;
            for (var index = array.Count - 1; index >= 0; index--)
            {
                if (array[index]?.Type == JTokenType.String
                    && autonomyToolIds.Contains(array[index]!.Value<string>() ?? string.Empty))
                {
                    array.RemoveAt(index);
                    removed = true;
                }
            }

            return removed;
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
            var selectedRows = GetEffectiveSelectedRows().ToList();
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

        private IEnumerable<ToolRow> GetEffectiveSelectedRows()
        {
            return toolRows.Where(row => IsEffectivelySelected(row));
        }

        private static bool IsEffectivelySelected(ToolRow row)
        {
            if (!ChievfxMcpWindow.ShowExperimentalAutonomyTools && IsAutonomyTool(row))
            {
                return row.Required;
            }

            return row.Enabled || row.Required;
        }
    }
}
