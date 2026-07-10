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
    internal sealed class ChievfxMcpResourceSelectionPanel
    {
        private const string AllInfoEditorPrefsKey = "ChievfxMcp.Selection.AllInfo";
        private const string ResourceKind = "Resource";
        private const string TemplateKind = "Template";

        private readonly List<ResourceRow> resourceRows = new();
        private readonly Dictionary<string, Toggle> toggles = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Label> categorySummaryLabels = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Button> categoryStateButtons = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> categoryDescriptions = new(StringComparer.Ordinal);

        private Label? summaryLabel;
        private Label? saveFeedbackLabel;
        private Label? detailLabel;
        private VisualElement? resourcesList;
        private VisualElement? guiRoot;
        private bool guiShowTitle;
        private string? selectedCategory;
        private string estimator = "unknown";
        private string resourceDescriptorEstimateBasis = "compact MCP resource descriptor JSON";
        private string templateDescriptorEstimateBasis = "compact MCP resource template descriptor JSON";
        private string resourceDescriptionEstimateBasis = "compact MCP resource URI/name/description JSON";
        private string templateDescriptionEstimateBasis = "compact MCP resource template URI/name/description JSON";
        private string readEnvelopeEstimateBasis = "compact JSON-RPC resources/read envelope";
        private string responseEstimateNote = "Rough wrapped-result guidance only.";
        private string reloadGuidance = "After changing enabled resources, reload MCP resources or restart Cursor.";
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
                content.Add(CreateTitleRow("ChievFX MCP Resources", allInfo, SetAllInfo));
            }

            summaryLabel = new Label();
            summaryLabel.style.marginTop = 8;
            summaryLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            summaryLabel.style.whiteSpace = WhiteSpace.Normal;
            content.Add(summaryLabel);

            if (allInfo)
            {
                saveFeedbackLabel = new Label("Optional resource changes auto-save.");
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
                RenderResources();
                RefreshSummary();
            }));

            resourcesList = new VisualElement();
            resourcesList.style.flexGrow = 1;
            content.Add(resourcesList);

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
            resourceRows.Clear();
            categoryDescriptions.Clear();

            try
            {
                LoadMetadataFromPython();
                ApplySavedSelection();
                SaveSelection();
            }
            catch (Exception ex)
            {
                loadError = ex.Message;
                Debug.LogWarning($"ChievFX MCP resource metadata load failed. {ex}");
            }

            RenderResources();
            RefreshSummary();
        }

        private void LoadMetadataFromPython()
        {
            if (!File.Exists(ChievfxMcpToolPolicy.ServerScriptPath))
            {
                throw new FileNotFoundException("ChievFX MCP server script not found.", ChievfxMcpToolPolicy.ServerScriptPath);
            }

            ChievfxMcpExtensionManifestSnapshot.Refresh();

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ChievfxMcpPythonLauncher.ExecutablePath,
                    WorkingDirectory = ChievfxMcpToolPolicy.ProjectRoot,
                    Arguments = $"{QuoteArg(ChievfxMcpToolPolicy.ServerScriptPath)} --resource-metadata",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                }
            };

            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start python3 for ChievFX MCP resource metadata.");
            }

            // Drain stdout/stderr asynchronously to avoid pipe-buffer deadlock when the
            // child writes more than the OS pipe capacity (~64KB on macOS) before exiting.
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

                throw new TimeoutException("Timed out reading ChievFX MCP resource metadata.");
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"ChievFX MCP resource metadata command failed ({process.ExitCode}). {stderr}");
            }

            var root = JToken.Parse(stdout);
            estimator = ReadString(root, "estimator", estimator);
            resourceDescriptorEstimateBasis = ReadString(root, "resourceDescriptorEstimateBasis", resourceDescriptorEstimateBasis);
            templateDescriptorEstimateBasis = ReadString(root, "resourceTemplateDescriptorEstimateBasis", templateDescriptorEstimateBasis);
            resourceDescriptionEstimateBasis = ReadString(root, "resourceDescriptionEstimateBasis", resourceDescriptionEstimateBasis);
            templateDescriptionEstimateBasis = ReadString(root, "resourceTemplateDescriptionEstimateBasis", templateDescriptionEstimateBasis);
            readEnvelopeEstimateBasis = ReadString(root, "readEnvelopeEstimateBasis", readEnvelopeEstimateBasis);
            responseEstimateNote = ReadString(root, "responseEstimateNote", responseEstimateNote);
            reloadGuidance = ReadString(root, "guidance", reloadGuidance);
            LoadCategoryDescriptions(root);

            var requiredResourceIds = ReadStringSet(root, "requiredResourceIds");
            var requiredTemplateIds = ReadStringSet(root, "requiredResourceTemplateIds");

            var resourcesArray = root["resources"] as JArray ?? throw new InvalidOperationException("ChievFX MCP resource metadata response missing `resources` array.");
            foreach (var resourceElement in resourcesArray)
            {
                var id = ReadString(resourceElement, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                resourceRows.Add(ReadRow(resourceElement, ResourceKind, id, "uri", requiredResourceIds.Contains(id)));
            }

            var templatesArray = root["resourceTemplates"] as JArray ?? throw new InvalidOperationException("ChievFX MCP resource metadata response missing `resourceTemplates` array.");
            foreach (var templateElement in templatesArray)
            {
                var id = ReadString(templateElement, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                resourceRows.Add(ReadRow(templateElement, TemplateKind, id, "uriTemplate", requiredTemplateIds.Contains(id)));
            }
        }

        private void LoadCategoryDescriptions(JToken root)
        {
            if (root["categoryDescriptions"] is not JObject descriptions)
            {
                return;
            }

            foreach (var property in descriptions.Properties())
            {
                if (property.Value.Type == JTokenType.String)
                {
                    categoryDescriptions[property.Name] = property.Value.Value<string>() ?? string.Empty;
                }
            }
        }

        private static ResourceRow ReadRow(JToken element, string kind, string id, string uriPropertyName, bool requiredFromMetadata)
        {
            var required = requiredFromMetadata;
            if (element["required"] is JToken requiredElement && requiredElement.Type == JTokenType.Boolean)
            {
                required = requiredElement.Value<bool>();
            }

            return new ResourceRow
            {
                Id = id,
                Kind = kind,
                Name = ReadString(element, "name", id),
                Description = ReadString(element, "description"),
                Uri = ReadString(element, uriPropertyName),
                MimeType = ReadString(element, "mimeType", "text/plain"),
                Category = ReadString(element, "category", "general"),
                DescriptorHash = ReadString(element, "descriptorHash"),
                DescriptorPreview = ReadString(element, "descriptorPreview", "{}"),
                DescriptorBytes = ReadInt(element, "descriptorBytes"),
                EstimatedTokens = ReadInt(element, "estimatedTokens"),
                DescriptionEstimatedTokens = ReadInt(element, "descriptionEstimatedTokens"),
                ReadEnvelopePreview = ReadString(element, "readEnvelopePreview", "{}"),
                ReadEnvelopeBytes = ReadInt(element, "readEnvelopeBytes"),
                ReadEnvelopeEstimatedTokens = ReadInt(element, "readEnvelopeEstimatedTokens"),
                ResponseEstimateLabel = ReadResponseEstimateLabel(element),
                Required = required
            };
        }

        private void ApplySavedSelection()
        {
            var allResourceIds = new HashSet<string>(resourceRows.Where(row => row.Kind == ResourceKind).Select(row => row.Id), StringComparer.Ordinal);
            var allTemplateIds = new HashSet<string>(resourceRows.Where(row => row.Kind == TemplateKind).Select(row => row.Id), StringComparer.Ordinal);
            var enabledResourceIds = new HashSet<string>(allResourceIds, StringComparer.Ordinal);
            var enabledTemplateIds = new HashSet<string>(allTemplateIds, StringComparer.Ordinal);

            if (File.Exists(ChievfxMcpToolPolicy.ResourceSelectionPath))
            {
                try
                {
                    var root = JToken.Parse(File.ReadAllText(ChievfxMcpToolPolicy.ResourceSelectionPath));
                    enabledResourceIds = ReadEnabledIds(root, "enabledResourceIds", allResourceIds);
                    enabledTemplateIds = ReadEnabledIds(root, "enabledResourceTemplateIds", allTemplateIds);
                }
                catch (JsonException ex)
                {
                    Debug.LogWarning($"ChievFX MCP could not read resource selection. All resources will be used. {ex.Message}");
                }
            }

            foreach (var row in resourceRows)
            {
                row.Enabled = row.Required
                    || (row.Kind == ResourceKind && enabledResourceIds.Contains(row.Id))
                    || (row.Kind == TemplateKind && enabledTemplateIds.Contains(row.Id));
            }
        }

        private static HashSet<string> ReadEnabledIds(JToken root, string propertyName, HashSet<string> defaultIds)
        {
            var enabledIds = new HashSet<string>(defaultIds, StringComparer.Ordinal);
            if (root[propertyName] is JArray enabledArray)
            {
                enabledIds.Clear();
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

            return enabledIds;
        }

        private static HashSet<string> ReadStringSet(JToken root, string propertyName)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (root[propertyName] is JArray values)
            {
                foreach (var item in values)
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
            }

            return result;
        }

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
                ? $"{category} ({rows.Count} rows)"
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

        private void SaveSelection()
        {
            if (resourceRows.Count == 0)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(ChievfxMcpToolPolicy.ResourceSelectionPath)!);
            using (var stream = new FileStream(ChievfxMcpToolPolicy.ResourceSelectionPath, FileMode.Create, FileAccess.Write))
            using (var streamWriter = new StreamWriter(stream, new UTF8Encoding(false)))
            using (var writer = new JsonTextWriter(streamWriter) { Formatting = Formatting.Indented })
            {
                writer.WriteStartObject();
                writer.WritePropertyName("schemaVersion");
                writer.WriteValue(1);
                writer.WritePropertyName("updatedAtUtc");
                writer.WriteValue(DateTime.UtcNow.ToString("O"));
                writer.WritePropertyName("source");
                writer.WriteValue("Tools~/ChievfxMcp/chievfx_mcp_server.py:RESOURCES");
                writer.WritePropertyName("estimator");
                writer.WriteValue(estimator);
                writer.WritePropertyName("note");
                writer.WriteValue("Token counts estimate compact MCP resource and resource template descriptors only; not exact billable request tokens.");
                writer.WritePropertyName("resourceDescriptorEstimateBasis");
                writer.WriteValue(resourceDescriptorEstimateBasis);
                writer.WritePropertyName("resourceTemplateDescriptorEstimateBasis");
                writer.WriteValue(templateDescriptorEstimateBasis);
                writer.WritePropertyName("resourceDescriptionEstimateBasis");
                writer.WriteValue(resourceDescriptionEstimateBasis);
                writer.WritePropertyName("resourceTemplateDescriptionEstimateBasis");
                writer.WriteValue(templateDescriptionEstimateBasis);
                writer.WritePropertyName("readEnvelopeEstimateBasis");
                writer.WriteValue(readEnvelopeEstimateBasis);
                writer.WritePropertyName("responseEstimateNote");
                writer.WriteValue(responseEstimateNote);
                writer.WritePropertyName("guidance");
                writer.WriteValue(reloadGuidance);

                writer.WritePropertyName("enabledResourceIds");
                writer.WriteStartArray();
                foreach (var row in resourceRows.Where(row => row.Kind == ResourceKind && (row.Enabled || row.Required)).OrderBy(row => row.Id, StringComparer.Ordinal))
                {
                    writer.WriteValue(row.Id);
                }
                writer.WriteEndArray();

                writer.WritePropertyName("enabledResourceTemplateIds");
                writer.WriteStartArray();
                foreach (var row in resourceRows.Where(row => row.Kind == TemplateKind && (row.Enabled || row.Required)).OrderBy(row => row.Id, StringComparer.Ordinal))
                {
                    writer.WriteValue(row.Id);
                }
                writer.WriteEndArray();

                WriteRowMetadata(writer, "resources", resourceRows.Where(row => row.Kind == ResourceKind));
                WriteRowMetadata(writer, "resourceTemplates", resourceRows.Where(row => row.Kind == TemplateKind));

                writer.WriteEndObject();
            }

            lastSavedAtLocal = DateTime.Now;
            RefreshSaveFeedback();
            ChievfxMcpDebugInstructionsDumper.TryDump("unity-resource-selection-save");
        }

        private static void WriteRowMetadata(JsonTextWriter writer, string propertyName, IEnumerable<ResourceRow> rows)
        {
            writer.WritePropertyName(propertyName);
            writer.WriteStartObject();
            foreach (var row in rows.OrderBy(row => row.Id, StringComparer.Ordinal))
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
                writer.WritePropertyName("readEnvelopeEstimatedTokens");
                writer.WriteValue(row.ReadEnvelopeEstimatedTokens);
                writer.WritePropertyName("readEnvelopeBytes");
                writer.WriteValue(row.ReadEnvelopeBytes);
                writer.WritePropertyName("required");
                writer.WriteValue(row.Required);
                writer.WritePropertyName("category");
                writer.WriteValue(row.Category);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }

        private void ResetRequiredMinimum()
        {
            foreach (var row in resourceRows)
            {
                row.Enabled = row.Required;
            }

            SyncTogglesFromRows();
            SaveSelection();
            RenderResources();
            RefreshSummary();
        }

        private void EnableAll()
        {
            foreach (var row in resourceRows)
            {
                row.Enabled = true;
            }

            SyncTogglesFromRows();
            SaveSelection();
            RenderResources();
            RefreshSummary();
        }

        private void DisableOptional()
        {
            foreach (var row in resourceRows)
            {
                row.Enabled = row.Required;
            }

            SyncTogglesFromRows();
            SaveSelection();
            RenderResources();
            RefreshSummary();
        }

        private void SetCategoryOptional(string category, bool enabled)
        {
            foreach (var row in resourceRows.Where(row => row.Category == category && !row.Required))
            {
                row.Enabled = enabled;
            }

            SyncTogglesFromRows();
            SaveSelection();
            RenderResources();
            RefreshSummary();
        }

        private void SyncTogglesFromRows()
        {
            suppressSave = true;
            try
            {
                foreach (var row in resourceRows)
                {
                    if (toggles.TryGetValue(row.Key, out var toggle))
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
            foreach (var group in resourceRows.GroupBy(row => row.Category))
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
            var selectedRows = resourceRows.Where(row => row.Enabled || row.Required).ToList();
            var selectedResourceTokens = selectedRows.Where(row => row.Kind == ResourceKind).Sum(row => row.EstimatedTokens);
            var allResourceTokens = resourceRows.Where(row => row.Kind == ResourceKind).Sum(row => row.EstimatedTokens);
            var selectedTemplateTokens = selectedRows.Where(row => row.Kind == TemplateKind).Sum(row => row.EstimatedTokens);
            var allTemplateTokens = resourceRows.Where(row => row.Kind == TemplateKind).Sum(row => row.EstimatedTokens);
            var selectedDescriptorTokens = selectedResourceTokens + selectedTemplateTokens;
            var allDescriptorTokens = allResourceTokens + allTemplateTokens;
            var selectedDescriptionTokens = selectedRows.Sum(row => row.DescriptionEstimatedTokens);
            var allDescriptionTokens = resourceRows.Sum(row => row.DescriptionEstimatedTokens);
            var selectedReadTokens = selectedRows.Sum(row => row.ReadEnvelopeEstimatedTokens);
            var allReadTokens = resourceRows.Sum(row => row.ReadEnvelopeEstimatedTokens);
            var requiredCount = resourceRows.Count(row => row.Required);
            var optionalCount = resourceRows.Count(row => !row.Required);
            var selectedOptionalCount = selectedRows.Count(row => !row.Required);
            var categoryCount = resourceRows.Select(row => row.Category).Distinct(StringComparer.Ordinal).Count();

            if (summaryLabel != null)
            {
                summaryLabel.text = allInfo
                    ? $"Selected descriptors: ~{selectedDescriptorTokens} tokens across {selectedRows.Count}/{resourceRows.Count} resources | " +
                      $"All resources descriptors: ~{allDescriptorTokens} tokens\n" +
                      $"Selected descriptions: ~{selectedDescriptionTokens} tokens | All resources descriptions: ~{allDescriptionTokens} tokens"
                    : $"Selected descriptors: ~{selectedDescriptorTokens} tokens across {selectedRows.Count}/{resourceRows.Count} resources\n" +
                      $"Selected descriptions: ~{selectedDescriptionTokens} tokens";
            }

            if (detailLabel != null)
            {
                detailLabel.text =
                    $"Categories: {categoryCount} | Required: {requiredCount} locked | Optional: {selectedOptionalCount}/{optionalCount} enabled | Estimator: {estimator}\n" +
                    $"Descriptors: selected ~{selectedDescriptorTokens}, all resources ~{allDescriptorTokens}. Resources: {resourceDescriptorEstimateBasis}. Templates: {templateDescriptorEstimateBasis}\n" +
                    $"Descriptions: selected ~{selectedDescriptionTokens}, all resources ~{allDescriptionTokens}. Descriptors already include URI/name/description; this line estimates discovery surface separately. Resources: {resourceDescriptionEstimateBasis}. Templates: {templateDescriptionEstimateBasis}\n" +
                    $"resources/read base envelope: selected ~{selectedReadTokens}, all ~{allReadTokens}. {readEnvelopeEstimateBasis}\n" +
                    $"Responses: {responseEstimateNote}\n" +
                    $"Selection file: {ChievfxMcpToolPolicy.ResourceSelectionPath}\n" +
                    reloadGuidance;
            }

            RefreshCategorySummaries();
        }

        private static string BuildCategorySummary(IReadOnlyList<ResourceRow> rows)
        {
            var requiredCount = rows.Count(row => row.Required);
            var optionalCount = rows.Count(row => !row.Required);
            var enabledOptionalCount = rows.Count(row => !row.Required && row.Enabled);
            var selectedResourceTokens = rows.Where(row => row.Kind == ResourceKind && (row.Required || row.Enabled)).Sum(row => row.EstimatedTokens);
            var allResourceTokens = rows.Where(row => row.Kind == ResourceKind).Sum(row => row.EstimatedTokens);
            var selectedTemplateTokens = rows.Where(row => row.Kind == TemplateKind && (row.Required || row.Enabled)).Sum(row => row.EstimatedTokens);
            var allTemplateTokens = rows.Where(row => row.Kind == TemplateKind).Sum(row => row.EstimatedTokens);
            var selectedDescriptorTokens = selectedResourceTokens + selectedTemplateTokens;
            var allDescriptorTokens = allResourceTokens + allTemplateTokens;
            var selectedDescriptionTokens = rows.Where(row => row.Required || row.Enabled).Sum(row => row.DescriptionEstimatedTokens);
            var allDescriptionTokens = rows.Sum(row => row.DescriptionEstimatedTokens);
            var selectedReadTokens = rows.Where(row => row.Required || row.Enabled).Sum(row => row.ReadEnvelopeEstimatedTokens);
            var state = optionalCount == 0
                ? "Required only"
                : enabledOptionalCount == 0
                    ? "Optional disabled"
                    : enabledOptionalCount == optionalCount
                        ? "Optional enabled"
                        : "Optional partial";

            return $"{state} | Required {requiredCount} | Enabled {enabledOptionalCount}/{optionalCount} optional | Descriptors ~{selectedDescriptorTokens}/~{allDescriptorTokens} | Descriptions ~{selectedDescriptionTokens}/~{allDescriptionTokens} | Read base ~{selectedReadTokens}";
        }

        private void RefreshSaveFeedback()
        {
            if (saveFeedbackLabel == null)
            {
                return;
            }

            saveFeedbackLabel.text = lastSavedAtLocal.HasValue
                ? $"Saved at {lastSavedAtLocal.Value:HH:mm:ss}."
                : "Optional resource changes auto-save.";
        }

        private static int GetCategorySortOrder(string category)
        {
            return category switch
            {
                "essentials" => 0,
                "editor" => 1,
                "scene" => 2,
                "gameobject" => 3,
                "ugui-design" => 10,
                "ugui-runtime-control" => 11,
                _ => 100
            };
        }

        private sealed class ResourceRow
        {
            public string Key => $"{Kind}:{Id}";

            public string Id { get; set; } = string.Empty;

            public string Kind { get; set; } = ResourceKind;

            public string Name { get; set; } = string.Empty;

            public string Description { get; set; } = string.Empty;

            public string Uri { get; set; } = string.Empty;

            public string MimeType { get; set; } = "text/plain";

            public string Category { get; set; } = "general";

            public string DescriptorHash { get; set; } = string.Empty;

            public string DescriptorPreview { get; set; } = "{}";

            public int DescriptorBytes { get; set; }

            public int EstimatedTokens { get; set; }

            public int DescriptionEstimatedTokens { get; set; }

            public string ReadEnvelopePreview { get; set; } = "{}";

            public int ReadEnvelopeBytes { get; set; }

            public int ReadEnvelopeEstimatedTokens { get; set; }

            public string ResponseEstimateLabel { get; set; } = string.Empty;

            public bool Required { get; set; }

            public bool Enabled { get; set; }
        }
    }
}
