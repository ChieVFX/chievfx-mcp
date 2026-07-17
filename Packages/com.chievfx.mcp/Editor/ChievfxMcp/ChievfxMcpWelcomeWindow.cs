#nullable enable
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using static Chievfx.Mcp.Editor.ChievfxMcpSelectionUi;

namespace Chievfx.Mcp.Editor
{
    // Minimal once-per-setup status card. Shows only actionable info: the big verdict, what the
    // auto-setup just did, and fix-it guidance when something is broken. Detailed controls live
    // in the main MCP window.
    internal sealed class ChievfxMcpWelcomeWindow : EditorWindow
    {
        private const string CheckedThisSessionKey = ChievfxMcpToolPolicy.ServerName + ".welcomeCheckedThisSession";

        // What the auto-setup just did (one line per written config file), shown in the window
        // so the user sees which files were created/updated. Static on purpose: transient
        // session memory, cleared by the next domain reload once the info has served its point.
        private static List<string>? lastSetupActions;

        [MenuItem("Window/ChievFX MCP Welcome")]
        public static void Open()
        {
            var window = GetWindow<ChievfxMcpWelcomeWindow>(utility: false, title: "Unity (chievfx) MCP");
            window.minSize = new Vector2(360, 220);
            window.Show();
            window.Focus();
        }

        // Surfaces only at initial setup of the plugin: when MCP client configs were just
        // (re)written — telling the user the MCP is now available to Cursor/Claude/Codex — or
        // when something is wrong and needs attention. A healthy, already-configured project
        // never pops it.
        public static void ShowOnStartupIfNeeded(List<string> writtenConfigs)
        {
            if (Application.isBatchMode)
            {
                return;
            }

            if (!ChievfxMcpToolPolicy.ShowWelcomeOnStartup)
            {
                return;
            }

            if (writtenConfigs.Count > 0)
            {
                lastSetupActions = writtenConfigs;
                SessionState.SetBool(CheckedThisSessionKey, true);
                Open();
                return;
            }

            // The health probe runs Python subprocesses, so evaluate it once per Unity session,
            // not on every domain reload.
            if (SessionState.GetBool(CheckedThisSessionKey, false))
            {
                return;
            }

            SessionState.SetBool(CheckedThisSessionKey, true);
            if (IsSetupHealthy())
            {
                return;
            }

            Open();
        }

        private static bool IsSetupHealthy()
        {
            if (!File.Exists(ChievfxMcpToolPolicy.ServerScriptPath))
            {
                return false;
            }

            if (!ChievfxMcpWindow.AreAllClientConfigsCurrent())
            {
                return false;
            }

            return ChievfxMcpPythonEnvironment.GetStatus().IsReady;
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
            var clientsNeedingWrite = ChievfxMcpWindow.GetClientsNeedingConfigWrite();
            var ready = pythonStatus.IsReady && serverScriptExists && clientsNeedingWrite.Count == 0;

            var title = CreateMutedLabel("Unity (chievfx) MCP");
            title.style.fontSize = 12;
            content.Add(title);

            var verdict = new Label(ready ? "Ready to use." : "Needs attention.");
            verdict.style.fontSize = 22;
            verdict.style.unityFontStyleAndWeight = FontStyle.Bold;
            verdict.style.marginTop = 2;
            verdict.style.marginBottom = 8;
            verdict.style.color = new StyleColor(ready ? new Color(0.58f, 0.82f, 0.58f) : new Color(1f, 0.8f, 0.45f));
            content.Add(verdict);

            if (lastSetupActions is { Count: > 0 })
            {
                var doneCard = CreateSectionCard("What was just done");
                foreach (var action in lastSetupActions)
                {
                    doneCard.Add(CreateMutedLabel($"• Wrote {action}"));
                }

                content.Add(doneCard);
            }

            if (ready)
            {
                content.Add(CreateMutedLabel(
                    $"Agents connect automatically on their next start. Cursor only: enable \"{ChievfxMcpToolPolicy.CursorServerName}\" under Settings > MCP."));
            }
            else
            {
                if (!pythonStatus.IsReady)
                {
                    content.Add(BuildPythonFixCard(pythonStatus));
                }

                if (!serverScriptExists)
                {
                    var card = CreateSectionCard("Server script missing");
                    card.Add(CreateMutedLabel(
                        $"Expected at: {ChievfxMcpToolPolicy.ServerScriptPath}\nReinstall or update the com.chievfx.mcp package to restore it."));
                    content.Add(card);
                }

                if (clientsNeedingWrite.Count > 0)
                {
                    var card = CreateSectionCard("MCP config not written");
                    card.Add(CreateMutedLabel($"Missing for: {string.Join(", ", clientsNeedingWrite)}."));
                    card.Add(CreateButton("Write Configs Now", () =>
                    {
                        var written = ChievfxMcpWindow.EnsureAllClientConfigs();
                        if (written.Count > 0)
                        {
                            lastSetupActions = written;
                        }

                        BuildContent();
                    }));
                    content.Add(card);
                }
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
                var written = ChievfxMcpWindow.EnsureAllClientConfigs();
                if (written.Count > 0)
                {
                    lastSetupActions = written;
                }

                BuildContent();
            }));
            content.Add(actions);

            var showOnStartupToggle = new Toggle("Show automatically on first setup or problems")
            {
                value = ChievfxMcpToolPolicy.ShowWelcomeOnStartup,
                tooltip = "Opens this window when MCP client configs were just written for this project or when setup needs attention. A healthy, already-configured project never shows it."
            };
            showOnStartupToggle.style.marginTop = 8;
            showOnStartupToggle.RegisterValueChangedCallback(evt =>
                EditorPrefs.SetBool(ChievfxMcpToolPolicy.ShowWelcomeOnStartupKey, evt.newValue));
            content.Add(showOnStartupToggle);
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
    }
}
