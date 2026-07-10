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
    internal sealed class ChievfxMcpWindow : EditorWindow
    {
        private const string TransportStdio = "stdio";
        private const string TransportHttp = "http";
        private const string ClientCursor = "Cursor";
        private const string ClientClaudeCode = "Claude Code";
        private const string ClientCodex = "Codex";
        private const string AllInfoEditorPrefsKey = "ChievfxMcp.Selection.AllInfo";
        private const string ShowExperimentalPromptsEditorPrefsKey = "ChievfxMcp.Experimental.ShowPromptsTab";
        private const string ShowExperimentalAutonomyToolsEditorPrefsKey = "ChievfxMcp.Experimental.ShowAutonomyTools";

        private static readonly string[] TransportChoices = { TransportStdio, TransportHttp };
        private static readonly string[] ClientChoices = { ClientCursor, ClientClaudeCode, ClientCodex };
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
        private Label? bridgeChip;
        private Label? httpChip;
        private Label? cursorConfigChip;
        private Label? clientAvailabilityChip;
        private Label? clientConfigPathLabel;
        private Label? clientConfigHintLabel;
        private ChievfxMcpTab activeTab = ChievfxMcpTab.Status;

        [MenuItem("Window/ChievFX MCP")]
        public static void Open()
        {
            Open(LoadActiveTab());
        }

        public static void OpenStatus()
        {
            Open(ChievfxMcpTab.Status);
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
            tabs.Add(CreateTabButton("Presets", ChievfxMcpTab.Presets));
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
            settings.Add(CreateActionRow(installPythonPackagesButton));

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

            var automation = CreateSectionCard("Automation");
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

        private static string ServerScriptPath => ChievfxMcpToolPolicy.ServerScriptPath;

        private static string HttpUrl(int port)
        {
            return $"http://127.0.0.1:{port}";
        }

        public static bool ShowExperimentalPromptsTab => EditorPrefs.GetBool(ShowExperimentalPromptsEditorPrefsKey, false);

        public static bool ShowExperimentalAutonomyTools => EditorPrefs.GetBool(ShowExperimentalAutonomyToolsEditorPrefsKey, false);

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
                "screenshot-editor-window" => "Capture Unity tabs like Console/Inspector without OS screenshots.",
                "tool-batch" => "Run one enabled MCP tool for many argument objects.",
                "assets-refresh" => "Import non-script assets by path/type after file changes.",
                "recompile" => "Request Unity script compilation and wait for idle.",
                "console-clear-logs" => "Clear logs before an isolated run.",
                "console-get-logs" => "Read recent Unity Console output (compact, duplicate-stacked) without filesystem scraping.",
                "console-get-logs-single" => "Fetch one full Unity Console entry by id from console-get-logs.",
                "reflection-method-find" => "Discover callable C# methods.",
                "reflection-method-find-single" => "Fetch full info for one discovered C# method.",
                "reflection-method-call" => "Invoke discovered C# methods.",
                "profiler-get-state" => "Check Unity profiler recording state.",
                "profiler-start-recording" => "Start a profiler capture.",
                "profiler-stop-recording" => "Stop and save a profiler capture.",
                "profiler-counters-get" => "Read memory counters from the profiler.",
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
                clientConfigHintLabel.text = clientInfo.Hint;
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

        private void UpdatePythonStatusUi(ChievfxMcpPythonEnvironmentStatus pythonStatus)
        {
            if (pythonStatus.PythonFound && pythonStatus.VersionSupported && !pythonStatus.IsWindowsStoreShim)
            {
                UpdateStateChip(pythonChip, "Python OK", StatusChipState.Good);
            }
            else if (pythonStatus.PythonFound)
            {
                var label = pythonStatus.IsWindowsStoreShim
                    ? "Python shim"
                    : "Python unsupported";
                UpdateStateChip(pythonChip, label, StatusChipState.Warning);
            }
            else
            {
                UpdateStateChip(pythonChip, "Python missing", StatusChipState.Warning);
            }

            if (!pythonStatus.PythonFound || !pythonStatus.VersionSupported || pythonStatus.IsWindowsStoreShim)
            {
                UpdateStateChip(pythonPackagesChip, "Packages unknown", StatusChipState.Neutral);
            }
            else if (!pythonStatus.HasRequiredPackages)
            {
                UpdateStateChip(pythonPackagesChip, "Packages none", StatusChipState.Good);
            }
            else if (pythonStatus.PackagesSatisfied)
            {
                UpdateStateChip(pythonPackagesChip, "Packages OK", StatusChipState.Good);
            }
            else
            {
                UpdateStateChip(pythonPackagesChip, "Packages missing", StatusChipState.Warning);
            }

            if (pythonDetailLabel != null)
            {
                if (pythonStatus.PythonFound)
                {
                    pythonDetailLabel.text =
                        $"Python: {pythonStatus.ExecutablePath} — {pythonStatus.VersionDisplay}";
                }
                else
                {
                    pythonDetailLabel.text = pythonStatus.Guidance;
                }
            }

            if (installPythonPackagesButton != null)
            {
                installPythonPackagesButton.style.display =
                    pythonStatus.HasRequiredPackages
                    && pythonStatus.PythonFound
                    && pythonStatus.VersionSupported
                    && !pythonStatus.IsWindowsStoreShim
                    && !pythonStatus.PackagesSatisfied
                        ? DisplayStyle.Flex
                        : DisplayStyle.None;
            }
        }

        private static string BuildSetupGuidance(
            ChievfxMcpPythonEnvironmentStatus pythonStatus,
            string transport,
            bool serverScriptExists,
            bool bridgeRunning,
            bool httpRunning,
            bool configured,
            McpClientInfo clientInfo,
            bool clientAvailable)
        {
            if (!pythonStatus.IsReady)
            {
                return pythonStatus.Guidance;
            }

            if (!serverScriptExists)
            {
                return $"Server script missing. Confirm project install before writing {clientInfo.DisplayName} config.";
            }

            if (!clientAvailable)
            {
                return $"{clientInfo.DisplayName} CLI not found. Install it or pick another MCP client.";
            }

            if (!configured)
            {
                return $"Write {clientInfo.DisplayName} Config, then reload MCP tools or restart {clientInfo.DisplayName}.";
            }

            if (!bridgeRunning)
            {
                return "Start Bridge before using Unity-backed tools.";
            }

            if (transport == TransportHttp && !httpRunning)
            {
                return "HTTP transport selected. Start HTTP before Cursor connects over HTTP.";
            }

            return $"Ready. Reload MCP tools or restart {clientInfo.DisplayName} after config or selection changes.";
        }

        private static void LaunchPythonInstaller()
        {
            if (!ChievfxMcpPythonLauncher.TryLaunchInstaller(out var error))
            {
                EditorUtility.DisplayDialog("ChievFX MCP", error, "OK");
            }
        }

        private void InstallPythonPackages()
        {
            if (ChievfxMcpPythonEnvironment.TryInstallRequirements(out var error, out var output))
            {
                EditorUtility.DisplayDialog("ChievFX MCP", output, "OK");
            }
            else
            {
                EditorUtility.DisplayDialog("ChievFX MCP", error, "OK");
            }

            RefreshUi(forcePythonRefresh: true);
        }

        private void StartHttpServer()
        {
            SavePreferences();

            if (GetTransport() != TransportHttp)
            {
                EditorUtility.DisplayDialog("ChievFX MCP", "Switch transport to HTTP before starting the HTTP server.", "OK");
                return;
            }

            if (!File.Exists(ServerScriptPath))
            {
                EditorUtility.DisplayDialog("ChievFX MCP", $"ChievFX MCP server script not found:\n{ServerScriptPath}", "OK");
                return;
            }

            if (IsHttpServerRunning())
            {
                RefreshUi();
                return;
            }

            ChievfxMcpToolPolicy.EnsureBridgeStarted();
            if (!TryStartHttpServerProcess(GetPort(), GetTimeout(), out var error))
            {
                EditorUtility.DisplayDialog("ChievFX MCP", error, "OK");
                return;
            }

            RefreshUi();
        }

        private void StopHttpServer()
        {
            StopHttpServerProcess();
            RefreshUi();
        }

        internal static void StopHttpServerProcess()
        {
            var process = httpProcess;
            if (process != null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                    }
                }
                catch (InvalidOperationException)
                {
                    // Process exited between HasExited and Kill.
                }
                finally
                {
                    process.Dispose();
                    httpProcess = null;
                }
            }
            else
            {
                var pid = EditorPrefs.GetInt(PrefKey("httpPid"), 0);
                if (pid > 0 && TryGetProcess(pid, out var storedProcess))
                {
                    try
                    {
                        storedProcess.Kill();
                    }
                    catch (InvalidOperationException)
                    {
                        // Process exited before cleanup.
                    }
                    finally
                    {
                        storedProcess.Dispose();
                    }
                }
            }

            EditorPrefs.DeleteKey(PrefKey("httpPid"));
        }

        private void WriteSelectedClientConfig()
        {
            SavePreferences();
            ChievfxMcpToolPolicy.EnsureBridgeStarted();
            var clientInfo = GetClientInfo(GetClient());
            Directory.CreateDirectory(Path.GetDirectoryName(clientInfo.ConfigPath)!);
            File.WriteAllText(clientInfo.ConfigPath, BuildClientConfigPreview(clientInfo, GetTransport(), GetPort(), GetTimeout()), new UTF8Encoding(false));
            RefreshUi();
            EditorUtility.DisplayDialog(
                "ChievFX MCP",
                $"{clientInfo.DisplayName} config written. Reload MCP tools or restart {clientInfo.DisplayName} before {ChievfxMcpToolPolicy.CursorServerName} appears in the current session.",
                "OK");
        }

        private void StartBridge()
        {
            ChievfxMcpToolPolicy.EnsureBridgeStarted();
            RefreshUi();
            EditorUtility.DisplayDialog("ChievFX MCP", $"Unity bridge IPC is active at:\n{ChievfxMcpToolPolicy.BridgeDirectory}", "OK");
        }

        private void CopyPreview()
        {
            EditorGUIUtility.systemCopyBuffer = BuildClientConfigPreview(GetClientInfo(GetClient()), GetTransport(), GetPort(), GetTimeout());
        }

        private static McpClientInfo GetClientInfo(string client)
        {
            if (client == ClientClaudeCode)
            {
                return new McpClientInfo(
                    ClientClaudeCode,
                    ClaudeCodeConfigPath,
                    "Claude Code reads project MCP servers from .mcp.json on session start. Project-scoped servers may need one-time approval from /mcp.",
                    McpClientConfigFormat.JsonMcpServers,
                    true,
                    "claude",
                    "Claude CLI found",
                    "Claude CLI missing");
            }

            if (client == ClientCodex)
            {
                return new McpClientInfo(
                    ClientCodex,
                    CodexConfigPath,
                    "Codex reads project MCP servers from .codex/config.toml after the project is trusted. Restart Codex after writing config.",
                    McpClientConfigFormat.CodexToml,
                    true,
                    "codex",
                    "Codex CLI found",
                    "Codex CLI missing");
            }

            return new McpClientInfo(
                ClientCursor,
                CursorConfigPath,
                "Write config after changing connection settings. Reload MCP tools or restart Cursor afterward.",
                McpClientConfigFormat.JsonMcpServers,
                false,
                null,
                "Cursor selected",
                "Cursor unavailable");
        }

        private static bool IsClientAvailable(McpClientInfo clientInfo)
        {
            if (!clientInfo.RequiresToolProbe || string.IsNullOrWhiteSpace(clientInfo.ProbeExecutableName))
            {
                return true;
            }

            if (IsExecutableAvailable(clientInfo.ProbeExecutableName!))
            {
                return true;
            }

            // Claude Code's Store (MSIX) build installs under a package-redirected AppData
            // path that non-packaged processes like the Unity editor cannot see, so a plain
            // executable probe fails even when it is installed. Fall back to signals that are
            // not affected by package redirection.
            return string.Equals(clientInfo.ProbeExecutableName, "claude", StringComparison.OrdinalIgnoreCase)
                && IsClaudeCodeInstalled();
        }

        private static bool IsClaudeCodeInstalled()
        {
            // Claude Code creates a ~/.claude home directory on first run. It is a plain
            // user-profile directory, unaffected by MSIX package redirection.
            var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
            if (string.IsNullOrWhiteSpace(userProfile))
            {
                try
                {
                    userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                }
                catch (PlatformNotSupportedException)
                {
                    userProfile = null;
                }
            }

            if (!string.IsNullOrWhiteSpace(userProfile) && Directory.Exists(Path.Combine(userProfile!, ".claude")))
            {
                return true;
            }

            // Definitive when Claude Code (or the desktop app's bundled CLI) is running.
            try
            {
                var processes = Process.GetProcessesByName("claude");
                try
                {
                    if (processes.Length > 0)
                    {
                        return true;
                    }
                }
                finally
                {
                    foreach (var process in processes)
                    {
                        process.Dispose();
                    }
                }
            }
            catch
            {
                // Process enumeration can fail under restricted environments; ignore.
            }

            return false;
        }

        private string BuildClientConfigPreview(McpClientInfo clientInfo, string transport, int port, int timeout)
        {
            if (clientInfo.Format == McpClientConfigFormat.CodexToml)
            {
                return BuildCodexConfigPreview(transport, port, timeout);
            }

            var mcpServers = new JObject();
            foreach (var existingServer in ReadExistingServersForPreview(clientInfo.ConfigPath, port))
            {
                mcpServers[existingServer.Name] = existingServer.Value;
            }

            mcpServers[ChievfxMcpToolPolicy.CursorServerName] = BuildExpectedCursorServerEntry(transport, port, timeout);

            var root = new JObject { ["mcpServers"] = mcpServers };
            return root.ToString(Formatting.Indented);
        }

        private static string BuildCodexConfigPreview(string transport, int port, int timeout)
        {
            var existing = File.Exists(CodexConfigPath)
                ? RemoveManagedCodexServerSections(File.ReadAllText(CodexConfigPath), port).TrimEnd()
                : string.Empty;
            var serverBlock = BuildExpectedCodexServerBlock(transport, port, timeout);
            return string.IsNullOrWhiteSpace(existing)
                ? serverBlock
                : $"{existing}{Environment.NewLine}{Environment.NewLine}{serverBlock}";
        }

        private static string BuildExpectedCodexServerBlock(string transport, int port, int timeout)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"[mcp_servers.{TomlQuotedKey(ChievfxMcpToolPolicy.CursorServerName)}]");
            if (transport == TransportHttp)
            {
                builder.AppendLine($"url = {TomlString(HttpUrl(port))}");
            }
            else
            {
                var args = TomlArray(new[]
                {
                    ServerScriptPath,
                    "--transport",
                    TransportStdio,
                    "--project-root",
                    ProjectRoot,
                    "--bridge-dir",
                    ChievfxMcpToolPolicy.BridgeDirectory,
                    "--timeout",
                    timeout.ToString()
                });
                builder.AppendLine($"command = {TomlString(ChievfxMcpPythonLauncher.ExecutablePath)}");
                builder.AppendLine($"args = {args}");
            }

            builder.AppendLine($"tool_timeout_sec = {Mathf.CeilToInt(timeout / 1000f)}");
            return builder.ToString().TrimEnd();
        }

        private static string TomlArray(IEnumerable<string> values)
        {
            var builder = new StringBuilder("[");
            var first = true;
            foreach (var value in values)
            {
                if (!first)
                {
                    builder.Append(", ");
                }

                builder.Append(TomlString(value));
                first = false;
            }

            builder.Append(']');
            return builder.ToString().TrimEnd();
        }

        private static string TomlQuotedKey(string value)
        {
            return TomlString(value);
        }

        private static string TomlString(string value)
        {
            return "\"" + value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t") + "\"";
        }

        private static string RemoveManagedCodexServerSections(string text, int port)
        {
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var output = new StringBuilder();
            var index = 0;
            while (index < lines.Length)
            {
                var line = lines[index];
                if (!IsTomlSectionHeader(line))
                {
                    output.AppendLine(line);
                    index++;
                    continue;
                }

                var start = index;
                index++;
                while (index < lines.Length && !IsTomlSectionHeader(lines[index]))
                {
                    index++;
                }

                var block = string.Join("\n", lines, start, index - start);
                if (ShouldSkipCodexSection(block, port))
                {
                    continue;
                }

                output.AppendLine(block);
            }

            return output.ToString();
        }

        private static bool IsTomlSectionHeader(string line)
        {
            var trimmed = line.Trim();
            return trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal);
        }

        private static bool ShouldSkipCodexSection(string block, int port)
        {
            var firstLineEnd = block.IndexOf('\n');
            var header = firstLineEnd >= 0 ? block.Substring(0, firstLineEnd).Trim() : block.Trim();
            if (!header.StartsWith("[mcp_servers.", StringComparison.Ordinal))
            {
                return false;
            }

            if (header.Contains(ChievfxMcpToolPolicy.CursorServerName, StringComparison.Ordinal)
                || header.Contains(ChievfxMcpToolPolicy.ServerName, StringComparison.Ordinal))
            {
                return true;
            }

            return block.Contains(ServerScriptPath, StringComparison.Ordinal)
                || block.Contains(HttpUrl(port), StringComparison.Ordinal);
        }

        private static JObject BuildExpectedCursorServerEntry(string transport, int port, int timeout)
        {
            var server = new JObject();
            if (transport == TransportHttp)
            {
                server["type"] = TransportHttp;
                server["url"] = HttpUrl(port);
                return server;
            }

            server["type"] = TransportStdio;
            server["command"] = ChievfxMcpPythonLauncher.ExecutablePath;
            server["args"] = new JArray(
                ServerScriptPath,
                "--transport",
                TransportStdio,
                "--project-root",
                ProjectRoot,
                "--bridge-dir",
                ChievfxMcpToolPolicy.BridgeDirectory,
                "--timeout",
                timeout.ToString());
            return server;
        }

        private static List<CursorServerConfig> ReadExistingServersForPreview(string configPath, int port)
        {
            var servers = new List<CursorServerConfig>();
            if (!File.Exists(configPath))
            {
                return servers;
            }

            try
            {
                var root = JToken.Parse(File.ReadAllText(configPath));
                if (root is not JObject rootObj
                    || rootObj["mcpServers"] is not JObject mcpServers)
                {
                    return servers;
                }

                foreach (var server in mcpServers.Properties())
                {
                    if (ShouldSkipExistingServer(server.Name, server.Value, port))
                    {
                        continue;
                    }

                    servers.Add(new CursorServerConfig(server.Name, server.Value.DeepClone()));
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"ChievFX MCP could not parse existing MCP client config. Preview will replace it. {ex.Message}");
            }

            return servers;
        }

        private static bool ShouldSkipExistingServer(string name, JToken value, int port)
        {
            if (ChievfxMcpToolPolicy.IsManagedCursorServerName(name))
            {
                // Skip the current project-unique entry (rewritten below) and any
                // legacy entry this package wrote, so writing config migrates them.
                return true;
            }

            return IsSameProjectLocalMcpServer(value, port);
        }

        private static bool IsSameProjectLocalMcpServer(JToken server, int port)
        {
            if (server is not JObject serverObj)
            {
                return false;
            }

            var commandElement = serverObj["command"];
            if (commandElement?.Type == JTokenType.String
                && IsSamePath(commandElement.Value<string>(), ServerScriptPath))
            {
                return true;
            }

            if (serverObj["args"] is JToken argsElement
                && ArgsContainPath(argsElement, ServerScriptPath))
            {
                return true;
            }

            if (commandElement?.Type == JTokenType.String
                && IsSameProjectUpstreamUnityServer(commandElement.Value<string>()))
            {
                return true;
            }

            var urlElement = serverObj["url"];
            if (urlElement?.Type == JTokenType.String
                && IsSameLocalHttpEndpoint(urlElement.Value<string>(), port))
            {
                return true;
            }

            return false;
        }

        private static bool ArgsContainPath(JToken argsElement, string path)
        {
            if (argsElement is not JArray argsArray)
            {
                return false;
            }

            foreach (var item in argsArray)
            {
                if (item.Type == JTokenType.String && IsSamePath(item.Value<string>(), path))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsSameProjectUpstreamUnityServer(string? command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return false;
            }

            var normalizedCommand = command!.Replace('\\', '/');
            var normalizedProjectRoot = ProjectRoot.Replace('\\', '/');
            return normalizedCommand.StartsWith(normalizedProjectRoot, StringComparison.Ordinal)
                && normalizedCommand.Contains("/Library/mcp-server/", StringComparison.Ordinal)
                && normalizedCommand.EndsWith("/unity-mcp-server", StringComparison.Ordinal);
        }

        private static bool IsSamePath(string? first, string second)
        {
            if (string.IsNullOrWhiteSpace(first))
            {
                return false;
            }

            try
            {
                return string.Equals(
                    Path.GetFullPath(first),
                    Path.GetFullPath(second),
                    RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            }
            catch
            {
                return string.Equals(first, second, StringComparison.Ordinal);
            }
        }

        private static bool IsExecutableAvailable(string executableName)
        {
            foreach (var candidate in EnumerateExecutableCandidates(executableName))
            {
                if (File.Exists(candidate))
                {
                    return true;
                }
            }

            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = executableName,
                        Arguments = "--version",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    },
                };
                process.Start();
                if (!process.WaitForExit(2000))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch
                    {
                        // Ignore kill failures on a probe process.
                    }

                    return false;
                }

                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private static IEnumerable<string> EnumerateExecutableCandidates(string executableName)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void YieldPath(List<string> results, string? path)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                var trimmed = path!.Trim().Trim('"');
                if (seen.Add(trimmed))
                {
                    results.Add(trimmed);
                }
            }

            var results = new List<string>();
            var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var directory in pathVariable.Split(Path.PathSeparator))
            {
                foreach (var candidate in ExecutablePathVariants(Path.Combine(directory, executableName)))
                {
                    YieldPath(results, candidate);
                }
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Resolve well-known roots from environment variables first because Unity's
                // Mono returns inconsistent results from Environment.GetFolderPath; fall back
                // to GetFolderPath so detection still works if a variable is unset.
                var appDataRoots = ResolveWindowsRoots("APPDATA", Environment.SpecialFolder.ApplicationData, Path.Combine("AppData", "Roaming"));
                var localAppDataRoots = ResolveWindowsRoots("LOCALAPPDATA", Environment.SpecialFolder.LocalApplicationData, Path.Combine("AppData", "Local"));

                foreach (var appData in appDataRoots)
                {
                    foreach (var candidate in ExecutablePathVariants(Path.Combine(appData, "npm", executableName)))
                    {
                        YieldPath(results, candidate);
                    }
                }

                // The native Claude Code installer (and the desktop-bundled CLI) places the
                // executable under a versioned directory that is not added to PATH, e.g.
                // %APPDATA%\Claude\claude-code\<version>\claude.exe. Scan those roots so the
                // client is detected even without a PATH shim.
                if (string.Equals(executableName, "claude", StringComparison.OrdinalIgnoreCase))
                {
                    var installRoots = new List<string>();
                    foreach (var appData in appDataRoots)
                    {
                        installRoots.Add(Path.Combine(appData, "Claude", "claude-code"));
                        installRoots.Add(Path.Combine(appData, "Claude", "claude-code-vm"));
                    }

                    foreach (var localAppData in localAppDataRoots)
                    {
                        installRoots.Add(Path.Combine(localAppData, "Claude", "claude-code"));
                        installRoots.Add(Path.Combine(localAppData, "Claude", "claude-code-vm"));
                    }

                    foreach (var root in installRoots)
                    {
                        foreach (var candidate in EnumerateVersionedExecutables(root, executableName))
                        {
                            YieldPath(results, candidate);
                        }
                    }
                }
            }
            else
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                YieldPath(results, $"/opt/homebrew/bin/{executableName}");
                YieldPath(results, $"/usr/local/bin/{executableName}");
                YieldPath(results, $"/usr/bin/{executableName}");
                YieldPath(results, Path.Combine(home, ".npm-global", "bin", executableName));
                YieldPath(results, Path.Combine(home, ".local", "bin", executableName));
            }

            return results;
        }

        private static IEnumerable<string> ExecutablePathVariants(string basePath)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                yield return basePath;
                yield break;
            }

            yield return basePath;
            yield return basePath + ".cmd";
            yield return basePath + ".exe";
            yield return basePath + ".bat";
        }

        private static IReadOnlyList<string> ResolveWindowsRoots(string environmentVariable, Environment.SpecialFolder specialFolder, string userProfileRelative)
        {
            var roots = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string? value)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return;
                }

                var trimmed = value!.Trim();
                if (seen.Add(trimmed))
                {
                    roots.Add(trimmed);
                }
            }

            Add(Environment.GetEnvironmentVariable(environmentVariable));
            try
            {
                Add(Environment.GetFolderPath(specialFolder));
            }
            catch (PlatformNotSupportedException)
            {
                // GetFolderPath can throw under some Mono configurations; other sources cover us.
            }

            // Unity's Mono runtime frequently leaves APPDATA/LOCALAPPDATA unset and returns
            // empty strings from GetFolderPath, so derive the folder from USERPROFILE (which is
            // reliably present) as a final fallback.
            var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
            if (string.IsNullOrWhiteSpace(userProfile))
            {
                try
                {
                    userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                }
                catch (PlatformNotSupportedException)
                {
                    userProfile = null;
                }
            }

            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                Add(Path.Combine(userProfile!, userProfileRelative));
            }

            return roots;
        }

        private static IEnumerable<string> EnumerateVersionedExecutables(string root, string executableName)
        {
            string[] versionDirectories;
            try
            {
                versionDirectories = Directory.Exists(root)
                    ? Directory.GetDirectories(root)
                    : Array.Empty<string>();
            }
            catch (IOException)
            {
                versionDirectories = Array.Empty<string>();
            }
            catch (UnauthorizedAccessException)
            {
                versionDirectories = Array.Empty<string>();
            }

            foreach (var versionDirectory in versionDirectories)
            {
                foreach (var candidate in ExecutablePathVariants(Path.Combine(versionDirectory, executableName)))
                {
                    yield return candidate;
                }
            }
        }

        private static bool IsSameLocalHttpEndpoint(string? url, int port)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return false;
            }

            return (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) || uri.Host == "127.0.0.1")
                && uri.Port == port;
        }

        private static bool IsClientConfigCurrent(McpClientInfo clientInfo, string transport, int port, int timeout)
        {
            if (!File.Exists(clientInfo.ConfigPath))
            {
                return false;
            }

            if (clientInfo.Format == McpClientConfigFormat.CodexToml)
            {
                return string.Equals(
                    NormalizeConfigText(File.ReadAllText(clientInfo.ConfigPath)),
                    NormalizeConfigText(BuildCodexConfigPreview(transport, port, timeout)),
                    StringComparison.Ordinal);
            }

            try
            {
                var root = JToken.Parse(File.ReadAllText(clientInfo.ConfigPath));
                if (root is not JObject rootObj
                    || rootObj["mcpServers"] is not JObject mcpServers
                    || mcpServers[ChievfxMcpToolPolicy.CursorServerName] is not JToken server)
                {
                    return false;
                }

                return JToken.DeepEquals(server, BuildExpectedCursorServerEntry(transport, port, timeout));
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static string NormalizeConfigText(string text)
        {
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        }

        private static bool IsHttpServerRunning()
        {
            if (httpProcess != null && !httpProcess.HasExited)
            {
                return true;
            }

            var pid = EditorPrefs.GetInt(PrefKey("httpPid"), 0);
            return pid > 0 && TryGetProcess(pid, out _);
        }

        private static bool TryStartHttpServerProcess(int port, int timeout, out string error)
        {
            error = string.Empty;
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ChievfxMcpPythonLauncher.ExecutablePath,
                    WorkingDirectory = ProjectRoot,
                    Arguments = BuildHttpServerArguments(port, timeout),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = false,
                    RedirectStandardOutput = false
                },
                EnableRaisingEvents = true
            };

            process.Exited += (_, _) => EditorApplication.delayCall += RefreshOpenWindows;

            if (!process.Start())
            {
                error = "Failed to start HTTP server process.";
                process.Dispose();
                return false;
            }

            httpProcess = process;
            EditorPrefs.SetInt(PrefKey("httpPid"), process.Id);
            return true;
        }

        private static void RefreshOpenWindows()
        {
            foreach (var window in UnityEngine.Resources.FindObjectsOfTypeAll<ChievfxMcpWindow>())
            {
                window.RefreshUi();
            }
        }

        private static bool TryGetProcess(int pid, out Process process)
        {
            try
            {
                process = Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch
            {
                process = null!;
                return false;
            }
        }

        private static string BuildHttpServerArguments(int port, int timeout)
        {
            return $"{QuoteArg(ServerScriptPath)} --transport {TransportHttp} --port {port} --project-root {QuoteArg(ProjectRoot)} --bridge-dir {QuoteArg(ChievfxMcpToolPolicy.BridgeDirectory)} --timeout {timeout}";
        }

        private static string QuoteArg(string value)
        {
            return $"\"{value.Replace("\"", "\\\"")}\"";
        }

        private static void LogServerLine(string? line)
        {
            if (!string.IsNullOrEmpty(line))
            {
                Debug.Log($"[ChievFX MCP] {line}");
            }
        }

        private int GetPort()
        {
            return Mathf.Max(1, portField?.value ?? ChievfxMcpToolPolicy.DefaultMcpPort);
        }

        private int GetTimeout()
        {
            return Mathf.Max(1000, timeoutField?.value ?? ChievfxMcpToolPolicy.DefaultTimeoutMs);
        }

        private string GetTransport()
        {
            return transportField?.value == TransportHttp ? TransportHttp : TransportStdio;
        }

        private string GetClient()
        {
            return clientField?.value == ClientClaudeCode || clientField?.value == ClientCodex
                ? clientField.value
                : ClientCursor;
        }

        private void SavePreferences()
        {
            if (portField != null)
            {
                EditorPrefs.SetInt(PrefKey("port"), GetPort());
            }

            if (timeoutField != null)
            {
                EditorPrefs.SetInt(PrefKey("timeout"), GetTimeout());
            }

            if (transportField != null)
            {
                EditorPrefs.SetString(PrefKey("transport"), GetTransport());
            }

            if (clientField != null)
            {
                EditorPrefs.SetString(PrefKey("client"), GetClient());
            }

            if (autoReloadExternallyChangedScenesToggle != null)
            {
                EditorPrefs.SetBool(
                    ChievfxMcpToolPolicy.AutoReloadExternallyChangedScenesKey,
                    autoReloadExternallyChangedScenesToggle.value);
            }
        }

        private static int LoadTransportIndex()
        {
            return EditorPrefs.GetString(PrefKey("transport"), TransportStdio) == TransportHttp ? 1 : 0;
        }

        private static int LoadClientIndex()
        {
            var saved = EditorPrefs.GetString(PrefKey("client"), ClientCursor);
            for (var i = 0; i < ClientChoices.Length; i++)
            {
                if (string.Equals(saved, ClientChoices[i], StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return 0;
        }

        private static ChievfxMcpTab LoadActiveTab()
        {
            var value = EditorPrefs.GetString(PrefKey("activeTab"), ChievfxMcpTab.Status.ToString());
            return Enum.TryParse(value, out ChievfxMcpTab tab) ? tab : ChievfxMcpTab.Status;
        }

        private static void SaveActiveTab(ChievfxMcpTab tab)
        {
            EditorPrefs.SetString(PrefKey("activeTab"), tab.ToString());
        }

        private static int LoadInt(string key, int defaultValue)
        {
            return EditorPrefs.GetInt(PrefKey(key), defaultValue);
        }

        private static string PrefKey(string key)
        {
            return $"{ChievfxMcpToolPolicy.ServerName}.{key}";
        }

        private enum McpClientConfigFormat
        {
            JsonMcpServers,
            CodexToml
        }

        private readonly struct McpClientInfo
        {
            public McpClientInfo(
                string displayName,
                string configPath,
                string hint,
                McpClientConfigFormat format,
                bool requiresToolProbe,
                string? probeExecutableName,
                string availableLabel,
                string missingLabel)
            {
                DisplayName = displayName;
                ConfigPath = configPath;
                Hint = hint;
                Format = format;
                RequiresToolProbe = requiresToolProbe;
                ProbeExecutableName = probeExecutableName;
                AvailableLabel = availableLabel;
                MissingLabel = missingLabel;
            }

            public string DisplayName { get; }

            public string ConfigPath { get; }

            public string Hint { get; }

            public McpClientConfigFormat Format { get; }

            public bool RequiresToolProbe { get; }

            public string? ProbeExecutableName { get; }

            public string AvailableLabel { get; }

            public string MissingLabel { get; }
        }

        private readonly struct CursorServerConfig
        {
            public CursorServerConfig(string name, JToken value)
            {
                Name = name;
                Value = value;
            }

            public string Name { get; }

            public JToken Value { get; }
        }
    }
}
