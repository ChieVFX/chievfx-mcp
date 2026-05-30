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
    internal sealed class ChievfxMcpPromptSelectionPanel
    {
        private const string AllInfoEditorPrefsKey = "ChievfxMcp.Selection.AllInfo";
        private const string PythonCommand = "python3";

        private readonly List<PromptRow> promptRows = new();
        private readonly Dictionary<string, Toggle> toggles = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Button> categoryStateButtons = new(StringComparer.Ordinal);

        private Label? summaryLabel;
        private Label? saveFeedbackLabel;
        private Label? detailLabel;
        private VisualElement? promptsList;
        private VisualElement? guiRoot;
        private bool guiShowTitle;
        private string estimator = "unknown";
        private string descriptorEstimateBasis = "compact MCP prompt descriptor JSON";
        private string descriptionEstimateBasis = "compact MCP prompt name/title/description JSON";
        private string getEnvelopeEstimateBasis = "compact JSON-RPC prompts/get envelope with empty arguments";
        private string responseEstimateNote = "Rough wrapped-result guidance only.";
        private string reloadGuidance = "After changing enabled prompts, reload MCP prompts or restart Cursor.";
        private string loadError = string.Empty;
        private string quickFilterText = string.Empty;
        private bool allInfo;
        private DateTime? lastSavedAtLocal;
        private bool suppressSave;

        public void CreateGUI(VisualElement root, bool showTitle = true)
        {
            guiRoot = root;
            guiShowTitle = showTitle;
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
                content.Add(CreateTitleRow("ChievFX MCP Prompts", allInfo, SetAllInfo));
            }

            content.Add(new HelpBox(
                "Select advertised MCP prompts. Required prompts stay enabled; optional changes auto-save.",
                HelpBoxMessageType.Info));

            summaryLabel = new Label();
            summaryLabel.style.marginTop = 8;
            summaryLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            summaryLabel.style.whiteSpace = WhiteSpace.Normal;
            content.Add(summaryLabel);

            saveFeedbackLabel = new Label("Optional prompt changes auto-save. Reload MCP prompts or restart Cursor after changing selection.");
            saveFeedbackLabel.style.marginTop = 2;
            saveFeedbackLabel.style.marginBottom = 4;
            saveFeedbackLabel.style.color = new StyleColor(new Color(0.58f, 0.78f, 0.58f));
            saveFeedbackLabel.style.whiteSpace = WhiteSpace.Normal;
            content.Add(saveFeedbackLabel);

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
                RenderPrompts();
                RefreshSummary();
            }));

            promptsList = new VisualElement();
            promptsList.style.flexGrow = 1;
            content.Add(promptsList);

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

        private void ReloadMetadata()
        {
            loadError = string.Empty;
            promptRows.Clear();

            try
            {
                LoadMetadataFromPython();
                ApplySavedSelection();
                SaveSelection();
            }
            catch (Exception ex)
            {
                loadError = ex.Message;
                Debug.LogWarning($"ChievFX MCP prompt metadata load failed. {ex}");
            }

            RenderPrompts();
            RefreshSummary();
        }

        private void LoadMetadataFromPython()
        {
            if (!File.Exists(ChievfxMcpToolPolicy.ServerScriptPath))
            {
                throw new FileNotFoundException("ChievFX MCP server script not found.", ChievfxMcpToolPolicy.ServerScriptPath);
            }

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = PythonCommand,
                    WorkingDirectory = ChievfxMcpToolPolicy.ProjectRoot,
                    Arguments = $"{QuoteArg(ChievfxMcpToolPolicy.ServerScriptPath)} --prompt-metadata",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                }
            };

            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start python3 for ChievFX MCP prompt metadata.");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(30000))
            {
                try
                {
                    process.Kill();
                }
                catch (InvalidOperationException)
                {
                    // Process exited before timeout cleanup.
                }

                throw new TimeoutException("Timed out reading ChievFX MCP prompt metadata.");
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"ChievFX MCP prompt metadata command failed ({process.ExitCode}). {stderr}");
            }

            var root = JToken.Parse(stdout);
            estimator = ReadString(root, "estimator", estimator);
            descriptorEstimateBasis = ReadString(root, "promptDescriptorEstimateBasis", descriptorEstimateBasis);
            descriptionEstimateBasis = ReadString(root, "promptDescriptionEstimateBasis", descriptionEstimateBasis);
            getEnvelopeEstimateBasis = ReadString(root, "getEnvelopeEstimateBasis", getEnvelopeEstimateBasis);
            responseEstimateNote = ReadString(root, "responseEstimateNote", responseEstimateNote);
            reloadGuidance = ReadString(root, "guidance", reloadGuidance);
            var requiredPromptNames = ReadStringSet(root, "requiredPromptNames");

            var promptsArray = root["prompts"] as JArray ?? throw new InvalidOperationException("ChievFX MCP prompt metadata response missing `prompts` array.");
            foreach (var promptElement in promptsArray)
            {
                var name = ReadString(promptElement, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var argumentsJson = promptElement["arguments"] is JToken argumentsElement
                    ? FormatJson(argumentsElement)
                    : "[]";

                promptRows.Add(new PromptRow
                {
                    Name = name,
                    Title = ReadString(promptElement, "title", name),
                    Description = ReadString(promptElement, "description"),
                    Category = ReadString(promptElement, "category", "General"),
                    DescriptorHash = ReadString(promptElement, "descriptorHash"),
                    DescriptorPreview = ReadString(promptElement, "descriptorPreview", "{}"),
                    DescriptorBytes = ReadInt(promptElement, "descriptorBytes"),
                    EstimatedTokens = ReadInt(promptElement, "estimatedTokens"),
                    DescriptionEstimatedTokens = ReadInt(promptElement, "descriptionEstimatedTokens"),
                    GetEnvelopePreview = ReadString(promptElement, "getEnvelopePreview", "{}"),
                    GetEnvelopeBytes = ReadInt(promptElement, "getEnvelopeBytes"),
                    GetEnvelopeEstimatedTokens = ReadInt(promptElement, "getEnvelopeEstimatedTokens"),
                    ResponseEstimateLabel = ReadResponseEstimateLabel(promptElement),
                    ArgumentsJson = argumentsJson,
                    ArgumentCount = promptElement["arguments"] is JArray argumentsArray ? argumentsArray.Count : 0,
                    Required = requiredPromptNames.Contains(name)
                });
            }
        }

        private void ApplySavedSelection()
        {
            var allPromptNames = new HashSet<string>(promptRows.Select(row => row.Name), StringComparer.Ordinal);
            var enabledNames = new HashSet<string>(StringComparer.Ordinal);

            if (File.Exists(ChievfxMcpToolPolicy.PromptSelectionPath))
            {
                try
                {
                    var root = JToken.Parse(File.ReadAllText(ChievfxMcpToolPolicy.PromptSelectionPath));
                    enabledNames = ReadStringSet(root, "enabledPromptNames");
                }
                catch (JsonException ex)
                {
                    Debug.LogWarning($"ChievFX MCP could not read prompt selection. Prompts will stay disabled by default. {ex.Message}");
                }
            }

            foreach (var row in promptRows)
            {
                row.Enabled = row.Required || enabledNames.Contains(row.Name);
            }
        }

        private void RenderPrompts()
        {
            toggles.Clear();
            categoryStateButtons.Clear();
            promptsList?.Clear();

            if (promptsList == null)
            {
                return;
            }

            if (!string.IsNullOrEmpty(loadError))
            {
                promptsList.Add(CreateLoadErrorState(
                    "Could not load ChievFX MCP prompt metadata.",
                    loadError,
                    $"Existing saved prompt selection remains in effect if Cursor/server can still read:\n{ChievfxMcpToolPolicy.PromptSelectionPath}",
                    ReloadMetadata));
                return;
            }

            if (promptRows.Count == 0)
            {
                promptsList.Add(CreateEmptyState(
                    "No ChievFX MCP prompt metadata found.",
                    "Reload metadata. Existing saved prompt selection remains in effect if Cursor/server can still read it.",
                    ReloadMetadata));
                return;
            }

            promptsList.Add(new HelpBox(
                "Toggle category availability here. Required prompts stay locked on. Descriptor hashes help compare cached Cursor prompt descriptors after reload.",
                HelpBoxMessageType.None));

            var groups = ApplyQuickFilter(GetPromptGroups(), quickFilterText, GetPromptSearchText).ToList();
            if (groups.Count == 0)
            {
                promptsList.Add(CreateMutedLabel("No prompts match the quick filter."));
            }

            foreach (var group in groups)
            {
                promptsList.Add(CreateCategoryElement(group.Category, group.Rows));
            }

            promptsList.Add(new HelpBox(reloadGuidance, HelpBoxMessageType.Warning));
        }

        private IEnumerable<CategoryRows<PromptRow>> GetPromptGroups()
        {
            return promptRows
                .GroupBy(row => row.Category)
                .OrderBy(group => GetCategorySortOrder(group.Key))
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new CategoryRows<PromptRow>(
                    group.Key,
                    group
                    .OrderByDescending(row => row.Required)
                    .ThenBy(row => row.Name, StringComparer.Ordinal)
                    .ToList()));
        }

        private static string GetPromptSearchText(PromptRow row)
        {
            return $"{row.Name} {row.Title} {row.Category} {row.Description} {row.ResponseEstimateLabel} {row.ArgumentsJson}";
        }

        private VisualElement CreateCategoryElement(string category, IReadOnlyList<PromptRow> rows)
        {
            var enabledCount = rows.Count(row => row.Required || row.Enabled);
            var card = CreateSectionCard(allInfo
                ? $"{category} prompts"
                : $"{category} ({enabledCount}/{rows.Count})");
            var optionalRows = rows.Where(row => !row.Required).ToList();
            var header = CreateMetaRow();
            header.style.marginLeft = 0;

            var stateButton = new Button(() => SetCategoryOptional(category, !AreAllOptionalEnabled(rows)));
            stateButton.style.minWidth = 72;
            stateButton.style.marginRight = 8;
            stateButton.style.unityFontStyleAndWeight = FontStyle.Bold;
            stateButton.SetEnabled(optionalRows.Count > 0);
            ApplyCategoryStateStyle(stateButton, GetOptionalState(rows));
            categoryStateButtons[category] = stateButton;
            header.Add(stateButton);

            if (allInfo)
            {
                var summary = new Label(BuildCategorySummary(rows));
                summary.style.whiteSpace = WhiteSpace.Normal;
                header.Add(summary);
            }
            card.Add(header);

            foreach (var row in rows)
            {
                card.Add(CreatePromptElement(row));
            }

            return card;
        }

        private VisualElement CreatePromptElement(PromptRow row)
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
                    SaveSelection();
                    RenderPrompts();
                    RefreshSummary();
                }
            });
            toggles[row.Name] = toggle;
            header.Add(toggle);

            var nameLabel = new Label(row.Name);
            nameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            nameLabel.style.flexBasis = 180;
            nameLabel.style.flexGrow = 1;
            nameLabel.style.minWidth = 0;
            nameLabel.style.whiteSpace = WhiteSpace.Normal;
            header.Add(nameLabel);
            header.Add(CreateCopyNameButton(row.Name, "prompt"));

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

                var getOverhead = new Label($"Get base ~{row.GetEnvelopeEstimatedTokens} tok");
                getOverhead.style.marginRight = 10;
                getOverhead.style.whiteSpace = WhiteSpace.Normal;
                meta.Add(getOverhead);

                var hash = new Label($"sha256 {ShortHash(row.DescriptorHash)}");
                hash.style.whiteSpace = WhiteSpace.Normal;
                meta.Add(hash);
                container.Add(meta);
            }

            var title = new Label(row.Title);
            title.style.whiteSpace = WhiteSpace.Normal;
            title.style.marginLeft = 28;
            title.style.marginTop = 2;
            title.style.color = new StyleColor(new Color(0.82f, 0.82f, 0.82f));
            container.Add(title);

            var description = new Label(row.Description);
            description.style.whiteSpace = WhiteSpace.Normal;
            description.style.marginLeft = 28;
            description.style.marginTop = 2;
            description.style.color = new StyleColor(new Color(0.68f, 0.68f, 0.68f));
            container.Add(description);

            var arguments = new Label($"Arguments: {row.ArgumentCount}");
            arguments.style.whiteSpace = WhiteSpace.Normal;
            arguments.style.marginLeft = 28;
            arguments.style.marginTop = 2;
            arguments.style.color = new StyleColor(new Color(0.64f, 0.78f, 0.92f));
            container.Add(arguments);

            if (allInfo)
            {
                var response = new Label($"Prompt-get guide: {row.ResponseEstimateLabel}");
                response.style.whiteSpace = WhiteSpace.Normal;
                response.style.marginLeft = 28;
                response.style.marginTop = 2;
                response.style.color = new StyleColor(new Color(0.82f, 0.72f, 0.54f));
                container.Add(response);

                container.Add(CreatePreviewFoldout(
                    "Advanced: descriptor JSON",
                    row.DescriptorPreview,
                    tooltip: $"Exact prompt descriptor JSON\nsha256 {row.DescriptorHash}\n{row.DescriptorBytes} B"));

                container.Add(CreatePreviewFoldout(
                    "Advanced: prompts/get envelope",
                    row.GetEnvelopePreview,
                    72,
                    $"prompts/get base envelope\n{row.GetEnvelopeBytes} B"));

                container.Add(CreatePreviewFoldout("Advanced: arguments JSON", row.ArgumentsJson, 72));
            }

            return container;
        }

        private void SaveSelection()
        {
            if (promptRows.Count == 0)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(ChievfxMcpToolPolicy.PromptSelectionPath)!);
            using (var stream = new FileStream(ChievfxMcpToolPolicy.PromptSelectionPath, FileMode.Create, FileAccess.Write))
            using (var streamWriter = new StreamWriter(stream, new UTF8Encoding(false)))
            using (var writer = new JsonTextWriter(streamWriter) { Formatting = Formatting.Indented })
            {
                writer.WriteStartObject();
                writer.WritePropertyName("schemaVersion");
                writer.WriteValue(1);
                writer.WritePropertyName("updatedAtUtc");
                writer.WriteValue(DateTime.UtcNow.ToString("O"));
                writer.WritePropertyName("source");
                writer.WriteValue("Tools/ChievfxMcp/chievfx_mcp_server.py:PROMPTS");
                writer.WritePropertyName("estimator");
                writer.WriteValue(estimator);
                writer.WritePropertyName("note");
                writer.WriteValue("Token counts estimate compact MCP prompt descriptors only; not exact billable request tokens.");
                writer.WritePropertyName("promptDescriptorEstimateBasis");
                writer.WriteValue(descriptorEstimateBasis);
                writer.WritePropertyName("promptDescriptionEstimateBasis");
                writer.WriteValue(descriptionEstimateBasis);
                writer.WritePropertyName("getEnvelopeEstimateBasis");
                writer.WriteValue(getEnvelopeEstimateBasis);
                writer.WritePropertyName("responseEstimateNote");
                writer.WriteValue(responseEstimateNote);
                writer.WritePropertyName("guidance");
                writer.WriteValue(reloadGuidance);

                writer.WritePropertyName("enabledPromptNames");
                writer.WriteStartArray();
                foreach (var row in promptRows.Where(row => row.Enabled || row.Required).OrderBy(row => row.Name, StringComparer.Ordinal))
                {
                    writer.WriteValue(row.Name);
                }
                writer.WriteEndArray();

                writer.WritePropertyName("prompts");
                writer.WriteStartObject();
                foreach (var row in promptRows.OrderBy(row => row.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(row.Name);
                    writer.WriteStartObject();
                    writer.WritePropertyName("descriptorHash");
                    writer.WriteValue(row.DescriptorHash);
                    writer.WritePropertyName("estimatedTokens");
                    writer.WriteValue(row.EstimatedTokens);
                    writer.WritePropertyName("descriptionEstimatedTokens");
                    writer.WriteValue(row.DescriptionEstimatedTokens);
                    writer.WritePropertyName("descriptorBytes");
                    writer.WriteValue(row.DescriptorBytes);
                    writer.WritePropertyName("getEnvelopeEstimatedTokens");
                    writer.WriteValue(row.GetEnvelopeEstimatedTokens);
                    writer.WritePropertyName("getEnvelopeBytes");
                    writer.WriteValue(row.GetEnvelopeBytes);
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
            foreach (var row in promptRows)
            {
                row.Enabled = row.Required;
            }

            SyncTogglesFromRows();
            SaveSelection();
            RenderPrompts();
            RefreshSummary();
        }

        private void EnableAll()
        {
            foreach (var row in promptRows)
            {
                row.Enabled = true;
            }

            SyncTogglesFromRows();
            SaveSelection();
            RenderPrompts();
            RefreshSummary();
        }

        private void DisableOptional()
        {
            foreach (var row in promptRows)
            {
                row.Enabled = row.Required;
            }

            SyncTogglesFromRows();
            SaveSelection();
            RenderPrompts();
            RefreshSummary();
        }

        private void SetCategoryOptional(string category, bool enabled)
        {
            foreach (var row in promptRows.Where(row => row.Category == category && !row.Required))
            {
                row.Enabled = enabled;
            }

            SyncTogglesFromRows();
            SaveSelection();
            RenderPrompts();
            RefreshSummary();
        }

        private void SyncTogglesFromRows()
        {
            suppressSave = true;
            try
            {
                foreach (var row in promptRows)
                {
                    if (toggles.TryGetValue(row.Name, out var toggle))
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

        private void RefreshSummary()
        {
            var selectedRows = promptRows.Where(row => row.Enabled || row.Required).ToList();
            var selectedDescriptorTokens = selectedRows.Sum(row => row.EstimatedTokens);
            var allDescriptorTokens = promptRows.Sum(row => row.EstimatedTokens);
            var selectedDescriptionTokens = selectedRows.Sum(row => row.DescriptionEstimatedTokens);
            var allDescriptionTokens = promptRows.Sum(row => row.DescriptionEstimatedTokens);
            var selectedGetTokens = selectedRows.Sum(row => row.GetEnvelopeEstimatedTokens);
            var allGetTokens = promptRows.Sum(row => row.GetEnvelopeEstimatedTokens);
            var requiredCount = promptRows.Count(row => row.Required);
            var optionalCount = promptRows.Count(row => !row.Required);
            var selectedOptionalCount = selectedRows.Count(row => !row.Required);
            var categoryCount = promptRows.Select(row => row.Category).Distinct(StringComparer.Ordinal).Count();

            if (summaryLabel != null)
            {
                summaryLabel.text = allInfo
                    ? $"Selected descriptors: ~{selectedDescriptorTokens} tokens across {selectedRows.Count}/{promptRows.Count} prompts | " +
                      $"All prompts descriptors: ~{allDescriptorTokens} tokens\n" +
                      $"Selected descriptions: ~{selectedDescriptionTokens} tokens | All prompts descriptions: ~{allDescriptionTokens} tokens"
                    : $"Selected descriptors: ~{selectedDescriptorTokens} tokens across {selectedRows.Count}/{promptRows.Count} prompts\n" +
                      $"Selected descriptions: ~{selectedDescriptionTokens} tokens";
            }

            if (detailLabel != null)
            {
                detailLabel.text =
                    $"Categories: {categoryCount} | Required: {requiredCount} locked | Optional: {selectedOptionalCount}/{optionalCount} enabled | Estimator: {estimator}\n" +
                    $"Prompt descriptors: selected ~{selectedDescriptorTokens}, all ~{allDescriptorTokens}. {descriptorEstimateBasis}\n" +
                    $"Prompt descriptions: selected ~{selectedDescriptionTokens}, all ~{allDescriptionTokens}. Descriptors already include name/title/description; this line estimates discovery surface separately. {descriptionEstimateBasis}\n" +
                    $"prompts/get base envelope: selected ~{selectedGetTokens}, all ~{allGetTokens}. {getEnvelopeEstimateBasis}\n" +
                    $"Responses: {responseEstimateNote}\n" +
                    $"Selection file: {ChievfxMcpToolPolicy.PromptSelectionPath}\n" +
                    reloadGuidance;
            }
        }

        private void RefreshSaveFeedback()
        {
            if (saveFeedbackLabel == null)
            {
                return;
            }

            saveFeedbackLabel.text = lastSavedAtLocal.HasValue
                ? $"Saved at {lastSavedAtLocal.Value:HH:mm:ss}. Reload MCP prompts or restart Cursor to apply prompt-list changes."
                : "Optional prompt changes auto-save. Reload MCP prompts or restart Cursor after changing selection.";
        }

        private static OptionalState GetOptionalState(IReadOnlyList<PromptRow> rows)
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

        private static bool AreAllOptionalEnabled(IReadOnlyList<PromptRow> rows)
        {
            return rows.Where(row => !row.Required).All(row => row.Enabled);
        }

        private static string BuildCategorySummary(IReadOnlyList<PromptRow> rows)
        {
            var requiredCount = rows.Count(row => row.Required);
            var optionalCount = rows.Count(row => !row.Required);
            var enabledOptionalCount = rows.Count(row => !row.Required && row.Enabled);
            var selectedEstimate = rows.Where(row => row.Required || row.Enabled).Sum(row => row.EstimatedTokens);
            var allEstimate = rows.Sum(row => row.EstimatedTokens);
            var selectedDescriptionEstimate = rows.Where(row => row.Required || row.Enabled).Sum(row => row.DescriptionEstimatedTokens);
            var allDescriptionEstimate = rows.Sum(row => row.DescriptionEstimatedTokens);
            var selectedGetEstimate = rows.Where(row => row.Required || row.Enabled).Sum(row => row.GetEnvelopeEstimatedTokens);
            var state = optionalCount == 0
                ? "Required only"
                : enabledOptionalCount == 0
                    ? "Optional disabled"
                    : enabledOptionalCount == optionalCount
                        ? "Optional enabled"
                        : "Optional partial";

            return $"{state} | Required {requiredCount} | Enabled {enabledOptionalCount}/{optionalCount} optional | Descriptors ~{selectedEstimate}/~{allEstimate} | Descriptions ~{selectedDescriptionEstimate}/~{allDescriptionEstimate} | Get base ~{selectedGetEstimate}";
        }

        private static int GetCategorySortOrder(string category)
        {
            return category switch
            {
                "Editor" => 0,
                "Scene" => 1,
                "Diagnostics" => 2,
                "ugui-design" => 10,
                _ => 100
            };
        }

        private static HashSet<string> ReadStringSet(JToken root, string propertyName)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (root[propertyName] is not JArray array)
            {
                return result;
            }

            foreach (var item in array)
            {
                if (item.Type == JTokenType.String)
                {
                    var value = item.Value<string>();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        result.Add(value!);
                    }
                }
            }

            return result;
        }

        private sealed class PromptRow
        {
            public string Name { get; set; } = string.Empty;

            public string Title { get; set; } = string.Empty;

            public string Description { get; set; } = string.Empty;

            public string Category { get; set; } = "General";

            public string DescriptorHash { get; set; } = string.Empty;

            public string DescriptorPreview { get; set; } = "{}";

            public int DescriptorBytes { get; set; }

            public int EstimatedTokens { get; set; }

            public int DescriptionEstimatedTokens { get; set; }

            public string GetEnvelopePreview { get; set; } = "{}";

            public int GetEnvelopeBytes { get; set; }

            public int GetEnvelopeEstimatedTokens { get; set; }

            public string ResponseEstimateLabel { get; set; } = string.Empty;

            public string ArgumentsJson { get; set; } = "[]";

            public int ArgumentCount { get; set; }

            public bool Required { get; set; }

            public bool Enabled { get; set; }
        }
    }
}
