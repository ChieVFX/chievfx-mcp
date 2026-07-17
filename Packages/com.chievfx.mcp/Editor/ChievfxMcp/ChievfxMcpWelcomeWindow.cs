#nullable enable
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using static Chievfx.Mcp.Editor.ChievfxMcpSelectionUi;

namespace Chievfx.Mcp.Editor
{
    // Small once-per-session status card: "ready to use" tips when setup is healthy,
    // fix-it guidance when something (usually Python) is broken. Deliberately minimal —
    // detailed controls live in the main MCP window.
    internal sealed class ChievfxMcpWelcomeWindow : EditorWindow
    {
        private const string ShownThisSessionKey = ChievfxMcpToolPolicy.ServerName + ".welcomeShownThisSession";

        [MenuItem("Window/ChievFX MCP Welcome")]
        public static void Open()
        {
            var window = GetWindow<ChievfxMcpWelcomeWindow>(utility: false, title: "Welcome (ChievFX MCP)");
            window.minSize = new Vector2(380, 320);
            window.Show();
            window.Focus();
        }

        public static void ShowOnStartupIfNeeded()
        {
            if (Application.isBatchMode)
            {
                return;
            }

            if (!ChievfxMcpToolPolicy.ShowWelcomeOnStartup)
            {
                return;
            }

            // Once per Unity session, not on every domain reload.
            if (SessionState.GetBool(ShownThisSessionKey, false))
            {
                return;
            }

            SessionState.SetBool(ShownThisSessionKey, true);
            Open();
        }

        public void CreateGUI()
        {
            BuildContent();
        }

        private void OnFocus()
        {
            // Refresh when the user comes back after fixing something (e.g. installing Python).
            BuildContent();
        }

        private void BuildContent()
        {
            rootVisualElement.Clear();
            var content = new ScrollView(ScrollViewMode.Vertical);
            content.style.flexGrow = 1;
            content.style.paddingLeft = 12;
            content.style.paddingRight = 12;
            content.style.paddingTop = 12;
            content.style.paddingBottom = 12;
            rootVisualElement.Add(content);

            var pythonStatus = ChievfxMcpPythonEnvironment.GetStatus();
            var serverScriptExists = File.Exists(ChievfxMcpToolPolicy.ServerScriptPath);
            var clientStates = ChievfxMcpWindow.GetClientSetupStates();
            var allConfigured = true;
            foreach (var state in clientStates)
            {
                allConfigured &= state.Configured;
            }

            var ready = pythonStatus.IsReady && serverScriptExists && allConfigured;

            var title = new Label("ChievFX MCP");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.fontSize = 18;
            content.Add(title);

            var verdict = new Label(ready
                ? "Ready to use. Unity side is set up — open your AI client and go."
                : "Almost there — one or more setup steps need attention below.");
            verdict.style.marginTop = 6;
            verdict.style.marginBottom = 8;
            verdict.style.whiteSpace = WhiteSpace.Normal;
            verdict.style.unityFontStyleAndWeight = FontStyle.Bold;
            verdict.style.color = new StyleColor(ready ? new Color(0.58f, 0.78f, 0.58f) : new Color(1f, 0.88f, 0.58f));
            content.Add(verdict);

            content.Add(BuildChecklist(pythonStatus, serverScriptExists, clientStates));

            if (!pythonStatus.IsReady)
            {
                content.Add(BuildPythonFixCard(pythonStatus));
            }
            else if (!serverScriptExists)
            {
                var card = CreateSectionCard("Fix: server script missing");
                card.Add(CreateMutedLabel(
                    $"Expected at: {ChievfxMcpToolPolicy.ServerScriptPath}\nReinstall or update the com.chievfx.mcp package to restore it."));
                content.Add(card);
            }

            if (ready)
            {
                content.Add(BuildTipsCard(clientStates));
            }

            var actions = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, marginTop = 8 }
            };
            actions.Add(CreateButton("Open MCP Window", ChievfxMcpWindow.OpenStatus));
            actions.Add(CreateButton("Recheck", () =>
            {
                ChievfxMcpPythonLauncher.InvalidateCache();
                ChievfxMcpPythonEnvironment.GetStatus(forceRefresh: true);
                ChievfxMcpWindow.EnsureAllClientConfigs();
                BuildContent();
            }));
            content.Add(actions);

            var showOnStartupToggle = new Toggle("Show this window when Unity opens")
            {
                value = ChievfxMcpToolPolicy.ShowWelcomeOnStartup
            };
            showOnStartupToggle.style.marginTop = 8;
            showOnStartupToggle.RegisterValueChangedCallback(evt =>
                EditorPrefs.SetBool(ChievfxMcpToolPolicy.ShowWelcomeOnStartupKey, evt.newValue));
            content.Add(showOnStartupToggle);
        }

        private static VisualElement BuildChecklist(
            ChievfxMcpPythonEnvironmentStatus pythonStatus,
            bool serverScriptExists,
            List<ChievfxMcpWindow.ChievfxMcpClientSetupState> clientStates)
        {
            var card = CreateSectionCard("Setup status");

            card.Add(CreateCheckRow(
                pythonStatus.IsReady,
                pythonStatus.IsReady
                    ? $"Python — {pythonStatus.VersionDisplay}"
                    : pythonStatus.PythonFound
                        ? $"Python — found but not usable ({pythonStatus.VersionDisplay})"
                        : "Python — not found"));

            card.Add(CreateCheckRow(
                serverScriptExists,
                serverScriptExists ? "MCP server script — found" : "MCP server script — missing"));

            foreach (var state in clientStates)
            {
                var detectionNote = state.DetectionReliable && !state.ClientDetected
                    ? " (CLI not detected — install it or ignore if unused)"
                    : string.Empty;
                card.Add(CreateCheckRow(
                    state.Configured,
                    state.Configured
                        ? $"{state.DisplayName} — config written{detectionNote}"
                        : $"{state.DisplayName} — config not written{detectionNote}"));
            }

            return card;
        }

        private static VisualElement CreateCheckRow(bool ok, string text)
        {
            var row = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 2 }
            };

            var mark = new Label(ok ? "✓" : "✕");
            mark.style.width = 18;
            mark.style.unityFontStyleAndWeight = FontStyle.Bold;
            mark.style.color = new StyleColor(ok ? new Color(0.5f, 0.8f, 0.5f) : new Color(0.95f, 0.65f, 0.4f));
            row.Add(mark);

            var label = new Label(text);
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.flexShrink = 1;
            row.Add(label);
            return row;
        }

        private static VisualElement BuildPythonFixCard(ChievfxMcpPythonEnvironmentStatus pythonStatus)
        {
            var card = CreateSectionCard("Fix: Python");
            card.Add(new HelpBox(pythonStatus.Guidance, HelpBoxMessageType.Warning));

            var actions = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, marginTop = 4 }
            };

            // "Install Python Packages" is only the right fix when the interpreter itself
            // is fine and requirements are the missing piece.
            if (pythonStatus.PythonFound
                && pythonStatus.VersionSupported
                && !pythonStatus.IsWindowsStoreShim
                && pythonStatus.HasRequiredPackages
                && !pythonStatus.PackagesSatisfied)
            {
                actions.Add(CreateButton("Install Python Packages", () =>
                {
                    EditorUtility.DisplayDialog(
                        "ChievFX MCP",
                        ChievfxMcpPythonEnvironment.TryInstallRequirements(out var error, out var output)
                            ? output
                            : error,
                        "OK");
                }));
            }
            else
            {
                actions.Add(CreateButton("Open python.org Downloads", () =>
                    Application.OpenURL("https://www.python.org/downloads/")));
            }

            card.Add(actions);
            return card;
        }

        private static VisualElement BuildTipsCard(List<ChievfxMcpWindow.ChievfxMcpClientSetupState> clientStates)
        {
            var card = CreateSectionCard("Using it from your AI client");
            card.Add(CreateMutedLabel(
                $"The server is registered as \"{ChievfxMcpToolPolicy.CursorServerName}\" in each client's project config."));

            card.Add(CreateMutedLabel(
                "• Cursor — if tools don't appear, enable the server under Settings > MCP, or reload MCP tools."));
            card.Add(CreateMutedLabel(
                "• Claude Code — project servers may need one-time approval: run /mcp in a session."));
            card.Add(CreateMutedLabel(
                "• Codex — trust the project folder, then restart Codex so it reads .codex/config.toml."));
            card.Add(CreateMutedLabel(
                "• Still not showing? Restart the client app — most clients read MCP config only on startup."));

            foreach (var state in clientStates)
            {
                if (state.DetectionReliable && !state.ClientDetected)
                {
                    card.Add(CreateMutedLabel(
                        $"Note: {state.DisplayName} CLI was not detected on this machine. Its config file is written and harmless; install the CLI if you plan to use it."));
                }
            }

            return card;
        }
    }
}
