#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;
using static Chievfx.Mcp.Editor.ChievfxMcpSelectionUi;

namespace Chievfx.Mcp.Editor
{
    internal sealed partial class ChievfxMcpWindow : EditorWindow
    {
        private const string TransportStdio = "stdio";
        private const string TransportHttp = "http";
        private const string ClientCursor = "Cursor";
        private const string ClientClaudeCode = "Claude Code";
        private const string ClientCodex = "Codex";
        private const string ClientKimiCode = "Kimi Code";
        private const string ClientRider = "JetBrains Rider";
        private const string AllInfoEditorPrefsKey = "ChievfxMcp.Selection.AllInfo";
        private const string ShowExperimentalPromptsEditorPrefsKey = "ChievfxMcp.Experimental.ShowPromptsTab";
        private const string ShowExperimentalAutonomyToolsEditorPrefsKey = "ChievfxMcp.Experimental.ShowAutonomyTools";

        private static readonly string[] TransportChoices = { TransportStdio, TransportHttp };
        private static readonly string[] ClientChoices = { ClientCursor, ClientClaudeCode, ClientCodex, ClientKimiCode, ClientRider };
        private static Process? httpProcess;
        private static ChievfxMcpTab? pendingTab;

        private IntegerField? portField;
        private IntegerField? timeoutField;
        private PopupField<string>? transportField;
        private PopupField<string>? clientField;
        private Toggle? autoReloadExternallyChangedScenesToggle;
        private Label? summaryLabel;
        private Label? guidanceLabel;
        private HelpBox? setupHelpBox;
        private TextField? previewField;
        private Button? startButton;
        private Button? stopButton;
        private Button? writeConfigButton;
        private Button? launchInstallerButton;
        private Toggle? showPromptsTabToggle;
        private Toggle? showAutonomyToolsToggle;
        private Label? serverChip;
        private Label? pythonChip;
        private Label? pythonPackagesChip;
        private Label? pythonDetailLabel;
        private Button? installPythonPackagesButton;
        private Button? reinstallPythonButton;
        private Label? bridgeChip;
        private Label? httpChip;
        private Label? cursorConfigChip;
        private Label? clientAvailabilityChip;
        private Label? clientConfigPathLabel;
        private Label? clientConfigHintLabel;
        private VisualElement? remoteCmdCard;
        private Toggle? remoteCmdEnabledToggle;
        private TextField? remoteCmdPatternField;
        private Toggle? remoteCmdUnrestrictedAckToggle;
        private HelpBox? remoteCmdUnrestrictedHelpBox;
        private TextField? remoteCmdSecurityKeyField;
        private HelpBox? remoteCmdSecurityKeyHelpBox;
        private TextField? remoteCmdAllowedIpsField;
        private HelpBox? remoteCmdAllowedIpsHelpBox;
        private TextField? remoteCmdBindHostField;
        private HelpBox? remoteCmdBindHostHelpBox;
        private IntegerField? remoteCmdTimeoutField;
        private IntegerField? remoteCmdMaxOutputField;
        private Label? remoteCmdStatusLabel;
        private ChievfxMcpTab activeTab = ChievfxMcpTab.Status;

        // "%&m" = Ctrl+Alt+M on Windows/Linux, Cmd+Alt+M on macOS.
        [MenuItem("Window/ChievFX MCP %&m")]
        public static void Open()
        {
            Open(LoadActiveTab());
        }

        public static void OpenStatus()
        {
            Open(ChievfxMcpTab.Status);
        }

        // Display names of clients whose config file does not carry the current entry for this
        // project copy. Never probes client CLIs, so it is cheap enough for the on-load welcome
        // decision.
        internal static List<string> GetClientsNeedingConfigWrite()
        {
            var transport = GetSavedTransport();
            var port = GetSavedPort();
            var timeout = GetSavedTimeout();
            var needing = new List<string>();
            foreach (var client in ClientChoices)
            {
                var clientInfo = GetClientInfo(client);
                if (!IsClientConfigCurrent(clientInfo, transport, port, timeout))
                {
                    needing.Add(clientInfo.DisplayName);
                }
            }

            return needing;
        }

        internal static bool AreAllClientConfigsCurrent()
        {
            return GetClientsNeedingConfigWrite().Count == 0;
        }

        // Writes the MCP config for every supported client (Cursor, Claude Code, Codex, Kimi Code,
        // JetBrains Rider) for this project copy when it is not already current. Used by the on-load auto-setup so the
        // project is ready without a manual Write Config. No-op for configs already up to date.
        // Returns one human-readable line per config file that was (re)written, e.g.
        // "Cursor — .cursor/mcp.json", for the Welcome window's "what was done" report.
        public static List<string> EnsureAllClientConfigs()
        {
            var transport = GetSavedTransport();
            var port = GetSavedPort();
            var timeout = GetSavedTimeout();

            ChievfxMcpToolPolicy.EnsureBridgeStarted();

            var written = new List<string>();
            foreach (var client in ClientChoices)
            {
                var clientInfo = GetClientInfo(client);
                if (IsClientConfigCurrent(clientInfo, transport, port, timeout))
                {
                    continue;
                }

                try
                {
                    WriteClientConfig(clientInfo, transport, port, timeout);
                    written.Add($"{clientInfo.DisplayName} — {ProjectRelativePath(clientInfo.ConfigPath)}");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"ChievFX MCP could not write {clientInfo.DisplayName} config. {ex.Message}");
                }
            }

            // Writing .codex/config.toml is not enough on its own: Codex only reads a project's
            // .codex/ layer once the project is trusted at user level. Idempotent — a project that
            // already has a trust decision (either answer) is left alone.
            if (ChievfxMcpToolPolicy.EnsureCodexProjectTrust
                && ChievfxMcpCodexTrust.TryEnsureProjectTrusted(out var trustDetail))
            {
                written.Add($"Codex project trust — {ChievfxMcpCodexTrust.ConfigPath}");
                Debug.Log($"[ChievFX MCP] {trustDetail}");
            }

            // The same reference the startup instructions can only point at, placed where each client
            // loads skills from. Idempotent, so an unchanged project writes nothing.
            written.AddRange(ChievfxMcpClientSkillWriter.WriteAll());

            if (written.Count > 0)
            {
                Debug.Log($"[ChievFX MCP] Wrote MCP client configs/skills for this project: {string.Join("; ", written)}.");
                RefreshOpenWindows();
            }

            return written;
        }

        private static string ProjectRelativePath(string path)
        {
            try
            {
                var root = ProjectRoot.Replace('\\', '/').TrimEnd('/') + "/";
                var normalized = Path.GetFullPath(path).Replace('\\', '/');
                return normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    ? normalized.Substring(root.Length)
                    : path;
            }
            catch
            {
                return path;
            }
        }

        public static void OpenTools()
        {
            Open(ChievfxMcpTab.Tools);
        }

        public static void OpenPresets()
        {
            Open(ChievfxMcpTab.Presets);
        }

        public static void OpenResources()
        {
            Open(ChievfxMcpTab.Resources);
        }

        public static void OpenPrompts()
        {
            Open(ChievfxMcpTab.Prompts);
        }

        public static void EnforceExperimentalVisibility()
        {
            if (!ShowExperimentalPromptsTab)
            {
                ChievfxMcpPromptSelectionPanel.DisableAllSavedPrompts();
            }

            if (!ShowExperimentalAutonomyTools)
            {
                ChievfxMcpToolSelectionPanel.RemoveAutonomyToolsFromSavedSelection();
            }
        }

        private static void Open(ChievfxMcpTab tab)
        {
            var window = GetWindow<ChievfxMcpWindow>();
            pendingTab = tab;
            window.activeTab = tab;
            SaveActiveTab(tab);
            window.titleContent = new GUIContent("MCP (ChievFX)");
            window.minSize = new Vector2(320, 420);
            window.Show();
            window.Focus();
            if (window.rootVisualElement.childCount > 0)
            {
                window.BuildWindow();
            }
        }

        public void CreateGUI()
        {
            activeTab = pendingTab ?? LoadActiveTab();
            pendingTab = null;
            BuildWindow();
        }

        private void OnFocus()
        {
            RefreshUi();
        }

        private void BuildWindow()
        {
            EnforceExperimentalVisibility();

            if (activeTab == ChievfxMcpTab.Prompts && !ShowExperimentalPromptsTab)
            {
                activeTab = ChievfxMcpTab.Status;
                pendingTab = ChievfxMcpTab.Status;
                SaveActiveTab(ChievfxMcpTab.Status);
            }

            titleContent = new GUIContent("MCP (ChievFX)");
            rootVisualElement.Clear();
            var content = new ScrollView(ScrollViewMode.Vertical);
            content.style.flexGrow = 1;
            content.style.paddingLeft = 12;
            content.style.paddingRight = 12;
            content.style.paddingTop = 12;
            content.style.paddingBottom = 12;
            rootVisualElement.Add(content);

            content.Add(CreateWindowTitleRow());

            content.Add(CreateTabBar());

            switch (activeTab)
            {
                case ChievfxMcpTab.Presets:
                    new ChievfxMcpToolSelectionPanel().CreateRolePresetGUI(content);
                    return;
                case ChievfxMcpTab.Tools:
                    new ChievfxMcpToolSelectionPanel().CreateGUI(content, showTitle: false);
                    return;
                case ChievfxMcpTab.Resources:
                    new ChievfxMcpResourceSelectionPanel().CreateGUI(content, showTitle: false);
                    return;
                case ChievfxMcpTab.Prompts:
                    new ChievfxMcpPromptSelectionPanel().CreateGUI(content, showTitle: false);
                    return;
                default:
                    BuildStatusTab(content);
                    return;
            }
        }

        private VisualElement CreateWindowTitleRow()
        {
            var row = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center
                }
            };

            var title = new Label("MCP (ChievFX)");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.fontSize = 18;
            title.style.flexGrow = 1;
            row.Add(title);

            var allInfo = EditorPrefs.GetBool(AllInfoEditorPrefsKey, false);
            row.Add(CreateAllInfoButton(allInfo, value =>
            {
                EditorPrefs.SetBool(AllInfoEditorPrefsKey, value);
                BuildWindow();
            }));

            return row;
        }

        private VisualElement CreateTabBar()
        {
            var tabs = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    flexWrap = Wrap.Wrap,
                    marginTop = 8,
                    marginBottom = 8
                }
            };

            tabs.Add(CreateTabButton("Connection", ChievfxMcpTab.Status));
            // Presets only make sense when the user is customizing the selection; hidden in the default
            // expose-all mode.
            if (ChievfxMcpToolPolicy.ManualToolResourceSelection)
            {
                tabs.Add(CreateTabButton("Presets", ChievfxMcpTab.Presets));
            }

            tabs.Add(CreateTabButton("Tools", ChievfxMcpTab.Tools));
            tabs.Add(CreateTabButton("Resources", ChievfxMcpTab.Resources));
            if (ShowExperimentalPromptsTab)
            {
                tabs.Add(CreateTabButton("Prompts", ChievfxMcpTab.Prompts));
            }

            return tabs;
        }

        private Button CreateTabButton(string text, ChievfxMcpTab tab)
        {
            var button = new Button(() =>
            {
                activeTab = tab;
                pendingTab = tab;
                SaveActiveTab(tab);
                BuildWindow();
            })
            {
                text = text
            };
            button.style.minWidth = 88;
            button.style.marginRight = 4;
            button.style.marginBottom = 4;
            button.style.unityFontStyleAndWeight = activeTab == tab ? FontStyle.Bold : FontStyle.Normal;
            button.style.backgroundColor = activeTab == tab
                ? new StyleColor(new Color(0.22f, 0.32f, 0.42f))
                : new StyleColor(new Color(0.16f, 0.16f, 0.16f));
            return button;
        }

        private void BuildStatusTab(VisualElement content)
        {
            setupHelpBox = new HelpBox(string.Empty, HelpBoxMessageType.Warning);
            content.Add(setupHelpBox);

            summaryLabel = new Label();
            summaryLabel.style.marginTop = 8;
            summaryLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            summaryLabel.style.whiteSpace = WhiteSpace.Normal;
            content.Add(summaryLabel);

            guidanceLabel = new Label();
            guidanceLabel.style.marginTop = 2;
            guidanceLabel.style.marginBottom = 4;
            guidanceLabel.style.whiteSpace = WhiteSpace.Normal;
            content.Add(guidanceLabel);

            var settings = CreateSectionCard("Connection");
            var connectionState = CreateChipRow();
            serverChip = CreateStateChip("Server unknown", StatusChipState.Neutral);
            pythonChip = CreateStateChip("Python unknown", StatusChipState.Neutral);
            pythonPackagesChip = CreateStateChip("Packages unknown", StatusChipState.Neutral);
            connectionState.Add(serverChip);
            connectionState.Add(pythonChip);
            connectionState.Add(pythonPackagesChip);
            settings.Add(connectionState);

            pythonDetailLabel = CreateMutedLabel(string.Empty);
            settings.Add(pythonDetailLabel);
            installPythonPackagesButton = CreateButton("Install Python Packages", InstallPythonPackages);
            reinstallPythonButton = CreateButton("Reinstall Python", ReinstallManagedPython);
            settings.Add(CreateActionRow(installPythonPackagesButton, reinstallPythonButton));

            var useSystemPythonToggle = new Toggle("Use system Python (dev)")
            {
                value = ChievfxMcpToolPolicy.UseSystemPython,
                tooltip =
                    "When off (default), MCP uses a managed portable CPython under ~/.chievfx-mcp/env/ and ignores system interpreters. When on, resolve Python from PATH / known install locations instead."
            };
            useSystemPythonToggle.RegisterValueChangedCallback(evt =>
            {
                EditorPrefs.SetBool(ChievfxMcpToolPolicy.UseSystemPythonKey, evt.newValue);
                RefreshUi(forcePythonRefresh: true);
            });
            settings.Add(useSystemPythonToggle);
            settings.Add(CreateMutedLabel(
                "Default uses managed Python downloaded into ~/.chievfx-mcp/env/. Enable system Python only for local debugging."));

            transportField = new PopupField<string>(new List<string>(TransportChoices), LoadTransportIndex())
            {
                label = "Transport"
            };
            transportField.RegisterValueChangedCallback(_ => RefreshUi());
            settings.Add(transportField);

            portField = new IntegerField("MCP port") { value = LoadInt("port", ChievfxMcpToolPolicy.DefaultMcpPort) };
            portField.RegisterValueChangedCallback(_ => RefreshUi());
            settings.Add(portField);

            timeoutField = new IntegerField("Tool timeout ms") { value = LoadInt("timeout", ChievfxMcpToolPolicy.DefaultTimeoutMs) };
            timeoutField.RegisterValueChangedCallback(_ => RefreshUi());
            settings.Add(timeoutField);

            content.Add(settings);

            var runtime = CreateSectionCard("Runtime");
            var runtimeState = CreateChipRow();
            bridgeChip = CreateStateChip("Bridge unknown", StatusChipState.Neutral);
            httpChip = CreateStateChip("HTTP unknown", StatusChipState.Neutral);
            runtimeState.Add(bridgeChip);
            runtimeState.Add(httpChip);
            runtime.Add(runtimeState);

            startButton = CreateButton("Start HTTP", StartHttpServer);
            stopButton = CreateButton("Stop HTTP", StopHttpServer);
            runtime.Add(CreateActionRow(startButton, stopButton, CreateButton("Start Bridge", StartBridge), CreateButton("Refresh", () => RefreshUi(true))));
            content.Add(runtime);

            content.Add(CreateRemoteCommandCard());

            var cursorConfig = CreateSectionCard("MCP Client Config");
            var configState = CreateChipRow();
            cursorConfigChip = CreateStateChip("Config unknown", StatusChipState.Neutral);
            clientAvailabilityChip = CreateStateChip("Client unknown", StatusChipState.Neutral);
            configState.Add(cursorConfigChip);
            configState.Add(clientAvailabilityChip);
            cursorConfig.Add(configState);
            clientField = new PopupField<string>(new List<string>(ClientChoices), LoadClientIndex())
            {
                label = "Client"
            };
            clientField.RegisterValueChangedCallback(_ => RefreshUi());
            cursorConfig.Add(clientField);
            clientConfigPathLabel = CreateMutedLabel(string.Empty);
            cursorConfig.Add(clientConfigPathLabel);
            clientConfigHintLabel = CreateMutedLabel(string.Empty);
            cursorConfig.Add(clientConfigHintLabel);
            writeConfigButton = CreateButton("Write Config", WriteSelectedClientConfig);
            cursorConfig.Add(CreateActionRow(writeConfigButton, CreateButton("Copy Preview", CopyPreview)));
            content.Add(cursorConfig);

            content.Add(CreateSecondaryProjectsCard());

            var exposure = CreateSectionCard("Tool & Resource Exposure");
            var manualSelectionToggle = new Toggle("Customize which tools & resources are exposed")
            {
                value = ChievfxMcpToolPolicy.ManualToolResourceSelection,
                tooltip = "Off (default): every non-hidden tool and resource is available; the Tools and Resources tabs are read-only and the Presets tab is hidden. On: hand-pick tools, resources, and categories (Presets tab + per-item toggles); your saved selection is honored."
            };
            manualSelectionToggle.RegisterValueChangedCallback(evt =>
            {
                ChievfxMcpToolPolicy.SetManualToolResourceSelection(evt.newValue);
                ChievfxMcpReloadSignal.RequestReload("unity-manual-selection-toggle");
                if (!evt.newValue && activeTab == ChievfxMcpTab.Presets)
                {
                    activeTab = ChievfxMcpTab.Status;
                    SaveActiveTab(activeTab);
                }

                BuildWindow();
            });
            exposure.Add(manualSelectionToggle);
            exposure.Add(CreateMutedLabel("Off exposes all non-hidden tools & resources and keeps the Tools/Resources tabs read-only. Turn on to choose per tool, resource, and category, and to use Presets."));
            content.Add(exposure);

            var automation = CreateSectionCard("Automation");
            var autoWriteClientConfigsToggle = new Toggle("Auto-write MCP client configs on Unity open")
            {
                value = ChievfxMcpToolPolicy.AutoWriteClientConfigs,
                tooltip = "When on, each time Unity opens this project the MCP config for every supported client (Cursor, Claude Code, Codex, Kimi Code, JetBrains Rider) is written for this project copy if it is missing, stale, or points at a different copy. Default on."
            };
            autoWriteClientConfigsToggle.RegisterValueChangedCallback(evt =>
                EditorPrefs.SetBool(ChievfxMcpToolPolicy.AutoWriteClientConfigsKey, evt.newValue));
            automation.Add(autoWriteClientConfigsToggle);
            automation.Add(CreateMutedLabel("Writes .cursor/mcp.json, .mcp.json, .codex/config.toml, .kimi-code/mcp.json, and .ai/mcp/mcp.json so the project is ready without a manual Write Config. Turn off to manage client configs yourself."));

            var ensureCodexTrustToggle = new Toggle("Record Codex project trust")
            {
                value = ChievfxMcpToolPolicy.EnsureCodexProjectTrust,
                tooltip = "When on, adds [projects.'<project path>'] trust_level = \"trusted\" to your user-level Codex config (CODEX_HOME or ~/.codex/config.toml) if no trust decision covers this project yet. Codex skips a project's .codex/config.toml — where this project's MCP server is declared — until the project is trusted, and hosts that drive Codex without its trust prompt never record it. Existing decisions, including \"untrusted\", are never changed. Default on."
            };
            ensureCodexTrustToggle.RegisterValueChangedCallback(evt =>
                EditorPrefs.SetBool(ChievfxMcpToolPolicy.EnsureCodexProjectTrustKey, evt.newValue));
            automation.Add(ensureCodexTrustToggle);
            automation.Add(CreateMutedLabel("Appends one entry to the global Codex config; the MCP server itself stays project-local. Trusting a directory also lets Codex default to workspace-write there. Turn off to answer Codex's own trust prompt yourself."));

            autoReloadExternallyChangedScenesToggle = new Toggle("Auto-reload externally changed open scenes")
            {
                value = ChievfxMcpToolPolicy.AutoReloadExternallyChangedScenes
            };
            autoReloadExternallyChangedScenesToggle.RegisterValueChangedCallback(_ => RefreshUi());
            automation.Add(autoReloadExternallyChangedScenesToggle);
            automation.Add(CreateMutedLabel("When scene files change on disk, reload them automatically so Unity's modal reload prompt does not block MCP work."));

            var autoReloadCursorToggle = new Toggle("Auto-reload Cursor MCP on availability change")
            {
                value = ChievfxMcpToolPolicy.AutoReloadCursorOnAvailabilityChange,
                tooltip = "When on, changing tool/resource/prompt availability writes .cursor/reload-mcps.json so the reload-mcps extension reconnects Cursor and refreshes its instructions without a manual reload. Default on."
            };
            autoReloadCursorToggle.RegisterValueChangedCallback(evt =>
                EditorPrefs.SetBool(ChievfxMcpToolPolicy.AutoReloadCursorOnAvailabilityChangeKey, evt.newValue));
            automation.Add(autoReloadCursorToggle);
            automation.Add(CreateMutedLabel("Cursor only re-reads MCP instructions on a reconnect, so a live availability edit otherwise stays stale until you reload manually. Requires the reload-mcps extension installed to watch the signal file."));
            var debugModeToggle = new Toggle("Debug mode")
            {
                value = ChievfxMcpDebugSettings.DebugMode,
                tooltip = "Generates .temp/debug_instructions.md and .temp/descriptors/ showing what gets sent to the agent on MCP startup."
            };
            debugModeToggle.RegisterValueChangedCallback(evt =>
            {
                ChievfxMcpDebugSettings.SetDebugMode(evt.newValue);
                if (evt.newValue)
                {
                    ChievfxMcpDebugInstructionsDumper.TryDump("unity-debug-mode-enabled");
                }
            });
            automation.Add(debugModeToggle);
            automation.Add(CreateMutedLabel("When on, writes debug_instructions (initialize.instructions snapshot) and per-tool tools/list JSON under .temp/descriptors/. Default off."));
            content.Add(automation);

            content.Add(CreateConfigPreviewFoldout());

            var advanced = new Foldout
            {
                text = "Advanced details",
                value = false
            };
            advanced.style.marginTop = 4;
            advanced.Add(CreateMutedLabel($"Server script: {ServerScriptPath}"));
            advanced.Add(CreateMutedLabel($"Python requirements: {ChievfxMcpToolPolicy.RequirementsPath}"));
            advanced.Add(CreateMutedLabel($"Bridge IPC: {ChievfxMcpToolPolicy.BridgeDirectory}"));
            launchInstallerButton = CreateButton("Launch Python Installer", LaunchPythonInstaller);
            advanced.Add(CreateActionRow(launchInstallerButton));
            advanced.Add(CreateMutedLabel(
                "Opens the PyQt drag-and-drop installer from Packages/com.chievfx.mcp/Install~/. FROM/TO are remembered per launching Unity project. Uses Install~/.venv when present."));
            var forceAllCategoriesToggle = new Toggle("Force all categories always-supplied")
            {
                value = ChievfxMcpCategorySettings.ForceAll,
                tooltip = "When on, no category auto-collapses in MCP instructions; every enabled tool/resource is listed inline. Costs more tokens. Default off."
            };
            forceAllCategoriesToggle.RegisterValueChangedCallback(evt =>
            {
                ChievfxMcpCategorySettings.SetForceAll(evt.newValue);
                ChievfxMcpDebugInstructionsDumper.TryDump("unity-force-all-categories");
            });
            advanced.Add(forceAllCategoriesToggle);
            advanced.Add(CreateMutedLabel("Categories with more than 3 enabled items collapse into a chievfx://categories link unless marked always-supplied (per-category toggle in Tools/Resources info mode)."));
            var stripStyleTagsToggle = new Toggle("Strip style tags from console log messages")
            {
                value = ChievfxMcpToolPolicy.StripStyleTagsFromConsoleLogs,
                tooltip = "When on, removes rich-text <b> and <color> tags from console-get-logs and console-get-logs-single messages. Only tags with both opening and closing present are stripped. Default on."
            };
            stripStyleTagsToggle.RegisterValueChangedCallback(evt =>
                EditorPrefs.SetBool(ChievfxMcpToolPolicy.StripStyleTagsFromConsoleLogsKey, evt.newValue));
            advanced.Add(stripStyleTagsToggle);
            advanced.Add(CreateMutedLabel("Unity console messages often carry <b>/<color> markup. Stripping keeps the agent-facing text clean without touching the live Unity Console. Default on."));
            advanced.Add(CreateExperimentalFoldout());
            content.Add(advanced);

            RefreshUi();
        }

        private VisualElement CreateExperimentalFoldout()
        {
            var experimental = new Foldout
            {
                text = "Experimental",
                value = false
            };
            showPromptsTabToggle = new Toggle("Show Prompts tab")
            {
                value = ShowExperimentalPromptsTab
            };
            showPromptsTabToggle.RegisterValueChangedCallback(evt =>
            {
                EditorPrefs.SetBool(ShowExperimentalPromptsEditorPrefsKey, evt.newValue);
                if (!evt.newValue)
                {
                    ChievfxMcpPromptSelectionPanel.DisableAllSavedPrompts();
                }
                BuildWindow();
            });
            experimental.Add(showPromptsTabToggle);
            experimental.Add(CreateMutedLabel("Prompts are hidden by default while the prompt catalog stays experimental."));
            showAutonomyToolsToggle = new Toggle("Show Autonomy tools")
            {
                value = ShowExperimentalAutonomyTools
            };
            showAutonomyToolsToggle.RegisterValueChangedCallback(evt =>
            {
                EditorPrefs.SetBool(ShowExperimentalAutonomyToolsEditorPrefsKey, evt.newValue);
                if (!evt.newValue)
                {
                    ChievfxMcpToolSelectionPanel.RemoveAutonomyToolsFromSavedSelection();
                }
            });
            experimental.Add(showAutonomyToolsToggle);
            experimental.Add(CreateMutedLabel("Autonomous self-configuration tools are off by default and hidden from manual tool selection unless explicitly shown."));
            return experimental;
        }

        private enum ChievfxMcpTab
        {
            Status,
            Presets,
            Tools,
            Resources,
            Prompts
        }

        private static string ProjectRoot => ChievfxMcpToolPolicy.ProjectRoot;

        private static string CursorConfigPath => ChievfxMcpToolPolicy.CursorConfigPath;

        private static string ClaudeCodeConfigPath => ChievfxMcpToolPolicy.ClaudeCodeConfigPath;

        private static string CodexConfigPath => ChievfxMcpToolPolicy.CodexConfigPath;

        private static string KimiCodeConfigPath => ChievfxMcpToolPolicy.KimiCodeConfigPath;

        private static string RiderConfigPath => ChievfxMcpToolPolicy.RiderConfigPath;

        private static string ServerScriptPath => ChievfxMcpToolPolicy.ServerScriptPath;

        private static VisualElement CreateChipRow()
        {
            return new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    flexWrap = Wrap.Wrap,
                    marginTop = 2,
                    marginBottom = 4
                }
            };
        }

        private static VisualElement CreateActionRow(params Button[] buttons)
        {
            var row = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    flexGrow = 1,
                    flexWrap = Wrap.Wrap,
                    marginTop = 4
                }
            };

            foreach (var button in buttons)
            {
                row.Add(button);
            }

            return row;
        }

        private VisualElement CreateConfigPreviewFoldout()
        {
            var foldout = new Foldout
            {
                text = "Config preview",
                value = false
            };
            foldout.style.marginTop = 8;

            previewField = new TextField
            {
                multiline = true,
                isReadOnly = true
            };
            previewField.style.minHeight = 140;
            previewField.style.whiteSpace = WhiteSpace.Normal;
            previewField.style.flexGrow = 1;
            foldout.Add(previewField);
            return foldout;
        }

        private static Foldout CreateRequiredToolsFoldout()
        {
            var tools = ChievfxMcpToolPolicy.RequiredToolIds;
            var toolsFoldout = new Foldout
            {
                text = $"Required tool visibility ({tools.Length} required tools)",
                value = false
            };

            var summary = new Label($"{tools.Length} required tools");
            summary.style.unityFontStyleAndWeight = FontStyle.Bold;
            summary.style.marginBottom = 4;
            toolsFoldout.Add(summary);

            foreach (var toolId in tools)
            {
                toolsFoldout.Add(CreateRequiredToolRow(toolId));
            }

            toolsFoldout.Add(new HelpBox(
                "These tools are always advertised. Optional tools can be enabled in the Tools tab. Tool calls are served by the ChievFX Unity bridge, not by ai-game-developer.",
                HelpBoxMessageType.None));
            return toolsFoldout;
        }

        private static VisualElement CreateRequiredToolRow(string toolId)
        {
            var row = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    flexWrap = Wrap.Wrap,
                    alignItems = Align.Center,
                    marginBottom = 2
                }
            };

            var toolLabel = new Label($"[x] {toolId}");
            toolLabel.style.flexBasis = 180;
            toolLabel.style.flexGrow = 1;
            toolLabel.style.minWidth = 0;
            toolLabel.style.marginRight = 8;
            toolLabel.style.whiteSpace = WhiteSpace.Normal;
            row.Add(toolLabel);

            var purposeLabel = new Label(GetRequiredToolPurpose(toolId));
            purposeLabel.style.color = new StyleColor(new Color(0.62f, 0.62f, 0.62f));
            purposeLabel.style.flexBasis = 160;
            purposeLabel.style.flexGrow = 1;
            purposeLabel.style.minWidth = 0;
            purposeLabel.style.whiteSpace = WhiteSpace.Normal;
            row.Add(purposeLabel);

            return row;
        }

        private static string GetRequiredToolPurpose(string toolId)
        {
            return toolId switch
            {
                "screenshot-game-view" => "Capture the current Game View.",
                "screenshot-camera" => "Capture from a Unity camera.",
                "game-view-record" => "Record the Game View in Play Mode as an mp4 (ffmpeg builds it).",
                "screenshot-editor-window" => "Capture Unity tabs like Console/Inspector without OS screenshots.",
                "tool-batch" => "Run several tool calls (mixed tools, pauses) in one request and get every output.",
                "assets-refresh" => "Import non-script assets by path/type after file changes.",
                "recompile" => "Request Unity script compilation and wait for idle.",
                "console-clear-logs" => "Clear logs before an isolated run.",
                "console-get-logs" => "Read recent Unity Console output (compact, duplicate-stacked) without filesystem scraping.",
                "console-get-logs-single" => "Fetch one full Unity Console entry by id from console-get-logs.",
                "reflection-method-find" => "Discover callable C# methods.",
                "reflection-method-find-single" => "Fetch full info for one discovered C# method.",
                "reflection-method-call" => "Invoke discovered C# methods.",
                "mcp-resource-read" => "Read MCP resources for clients without a resource-read tool (subagents).",
                "profiler-get-state" => "Check Unity profiler recording state.",
                "profiler-start-recording" => "Start a profiler capture.",
                "profiler-stop-recording" => "Stop and save a profiler capture.",
                "profiler-counters-get" => "Read memory counters; in Play Mode also fps, frame time and draw calls.",
                _ => "Required ChievFX MCP capability."
            };
        }

        private void RefreshUi(bool forcePythonRefresh = false)
        {
            SavePreferences();

            if (forcePythonRefresh)
            {
                ChievfxMcpPythonLauncher.InvalidateCache();
            }

            var pythonStatus = ChievfxMcpPythonEnvironment.GetStatus(forcePythonRefresh);
            var port = GetPort();
            var timeout = GetTimeout();
            var transport = GetTransport();
            var client = GetClient();
            var clientInfo = GetClientInfo(client);
            var serverScriptExists = File.Exists(ServerScriptPath);
            var bridgeRunning = ChievfxMcpBridge.IsRunning;
            var httpRunning = IsHttpServerRunning();
            var configured = IsClientConfigCurrent(clientInfo, transport, port, timeout);
            var clientAvailable = IsClientAvailable(clientInfo);

            if (summaryLabel != null)
            {
                var httpSummary = transport == TransportHttp
                    ? $" | HTTP {(httpRunning ? "running" : "stopped")}"
                    : string.Empty;
                var pythonSummary = pythonStatus.IsReady
                    ? "Python ready"
                    : "Python needs setup";
                summaryLabel.text =
                    $"{pythonSummary} | Server: script {(serverScriptExists ? "found" : "missing")} | Bridge {(bridgeRunning ? "running" : "stopped")}{httpSummary} | {clientInfo.DisplayName} {(configured ? "configured" : "needs write")}";
                summaryLabel.style.color = new StyleColor(!pythonStatus.IsReady || !configured || !clientAvailable || (transport == TransportHttp && !httpRunning)
                    ? new Color(1f, 0.88f, 0.58f)
                    : new Color(0.78f, 0.78f, 0.78f));
            }

            if (guidanceLabel != null)
            {
                var guidanceGood = pythonStatus.IsReady && serverScriptExists && bridgeRunning && configured && clientAvailable && (transport != TransportHttp || httpRunning);
                guidanceLabel.text = BuildSetupGuidance(pythonStatus, transport, serverScriptExists, bridgeRunning, httpRunning, configured, clientInfo, clientAvailable);
                guidanceLabel.style.color = new StyleColor(guidanceGood ? new Color(0.58f, 0.78f, 0.58f) : new Color(0.72f, 0.72f, 0.72f));
            }

            if (setupHelpBox != null)
            {
                var guidanceGood = pythonStatus.IsReady && serverScriptExists && bridgeRunning && configured && clientAvailable && (transport != TransportHttp || httpRunning);
                setupHelpBox.text = BuildSetupGuidance(pythonStatus, transport, serverScriptExists, bridgeRunning, httpRunning, configured, clientInfo, clientAvailable);
                setupHelpBox.style.display = guidanceGood ? DisplayStyle.None : DisplayStyle.Flex;
            }

            UpdatePythonStatusUi(pythonStatus);
            UpdateStateChip(serverChip, serverScriptExists ? "Server found" : "Server missing", serverScriptExists ? StatusChipState.Good : StatusChipState.Warning);
            UpdateStateChip(bridgeChip, bridgeRunning ? "Bridge running" : "Bridge stopped", bridgeRunning ? StatusChipState.Good : StatusChipState.Neutral);
            UpdateStateChip(httpChip, httpRunning ? "HTTP running" : "HTTP stopped", httpRunning ? StatusChipState.Good : StatusChipState.Warning);
            if (httpChip != null)
            {
                httpChip.style.display = transport == TransportHttp ? DisplayStyle.Flex : DisplayStyle.None;
            }
            UpdateStateChip(cursorConfigChip, configured ? "Configured" : "Needs write", configured ? StatusChipState.Good : StatusChipState.Warning);
            UpdateStateChip(clientAvailabilityChip, clientAvailable ? clientInfo.AvailableLabel : clientInfo.MissingLabel, clientAvailable ? StatusChipState.Good : StatusChipState.Warning);

            if (clientAvailabilityChip != null)
            {
                clientAvailabilityChip.style.display = clientInfo.RequiresToolProbe ? DisplayStyle.Flex : DisplayStyle.None;
            }

            if (clientConfigPathLabel != null)
            {
                clientConfigPathLabel.text = $"{clientInfo.DisplayName} config: {clientInfo.ConfigPath}";
            }

            if (clientConfigHintLabel != null)
            {
                // Codex's project config only loads for a trusted project, so its real state is the
                // config file plus the user-level trust record. Read it only while Codex is the
                // selected client, so the other clients' refresh stays file-free.
                var hint = clientInfo.Hint;
                if (string.Equals(clientInfo.DisplayName, ClientCodex, StringComparison.Ordinal))
                {
                    hint += ChievfxMcpCodexTrust.HasTrustDecision()
                        ? $"\n\nProject trust: recorded in {ChievfxMcpCodexTrust.ConfigPath}."
                        : $"\n\nProject trust: MISSING from {ChievfxMcpCodexTrust.ConfigPath} — until it is there Codex ignores .codex/config.toml entirely and loads no Unity tools. Write Codex Config adds it.";
                }

                // Codex's generated block is a url entry with no header table this package writes, so the
                // key cannot travel in it. Say so here rather than writing a TOML key Codex may reject.
                if (transport == TransportHttp
                    && ChievfxMcpToolPolicy.RemoteCmdRequiresClientKey
                    && string.Equals(clientInfo.DisplayName, ClientCodex, StringComparison.Ordinal))
                {
                    hint += $"\n\nremote-cmd: this config cannot carry the {ChievfxMcpToolPolicy.RemoteCmdSecurityKeyHeader} "
                            + "header, and the server refuses requests without it. Use a client whose config supports "
                            + "headers (Cursor, Claude Code, Kimi, Rider) for remote-cmd over HTTP.";
                }

                clientConfigHintLabel.text = hint;
            }

            if (writeConfigButton != null)
            {
                writeConfigButton.text = $"Write {clientInfo.DisplayName} Config";
            }

            if (previewField != null)
            {
                previewField.value = BuildClientConfigPreview(clientInfo, transport, port, timeout);
            }

            if (startButton != null)
            {
                startButton.SetEnabled(transport == TransportHttp && serverScriptExists && pythonStatus.IsReady && !httpRunning);
            }

            if (stopButton != null)
            {
                stopButton.SetEnabled(httpRunning);
            }

            if (portField != null)
            {
                portField.style.display = transport == TransportHttp ? DisplayStyle.Flex : DisplayStyle.None;
            }

            RefreshRemoteCommandCard(transport);

            if (launchInstallerButton != null)
            {
                launchInstallerButton.SetEnabled(ChievfxMcpToolPolicy.TryResolveInstallerScriptPath(out _));
            }
        }

        private static void UpdateStateChip(Label? chip, string text, StatusChipState state)
        {
            if (chip == null)
            {
                return;
            }

            chip.text = text;
            ApplyStateChipStyle(chip, state);
        }
    }
}
