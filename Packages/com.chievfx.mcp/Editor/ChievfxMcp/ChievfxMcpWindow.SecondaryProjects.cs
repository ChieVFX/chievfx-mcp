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
    internal sealed partial class ChievfxMcpWindow
    {
        // Other Unity projects exposed to this project's agent as extra MCP servers, so one session can
        // drive two editors — built-in vs URP comparisons, or a server copy plus a client copy.
        private VisualElement CreateSecondaryProjectsCard()
        {
            var card = CreateSectionCard("Secondary Projects");
            card.Add(CreateMutedLabel(
                "Add another Unity project that has ChievFX MCP installed and its server is injected into this project's client configs, "
                + "next to this project's own. Each one runs that project's package and bridge, and tells the agent it is the secondary project and where it lives."));

            var projects = ChievfxMcpSecondaryProjects.Load();
            if (projects.Count == 0)
            {
                card.Add(CreateMutedLabel("None added."));
            }

            foreach (var project in projects)
            {
                card.Add(CreateSecondaryProjectRow(project));
            }

            card.Add(CreateActionRow(CreateButton("Add Project...", AddSecondaryProject)));
            return card;
        }

        private VisualElement CreateSecondaryProjectRow(ChievfxMcpSecondaryProject project)
        {
            var row = new VisualElement
            {
                style =
                {
                    marginTop = 6,
                    paddingLeft = 6,
                    borderLeftWidth = 2,
                    borderLeftColor = new Color(0.4f, 0.4f, 0.4f)
                }
            };

            var header = new Label($"{project.FolderName}  ({project.ServerName})");
            header.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.style.whiteSpace = WhiteSpace.Normal;
            row.Add(header);
            row.Add(CreateMutedLabel(project.ProjectRoot));

            var note = new TextField("Note")
            {
                value = project.Note,
                isDelayed = true,
                tooltip = "Optional one-liner folded into what the agent is told about this project, e.g. \"server copy\" or \"URP\"."
            };
            var projectRoot = project.ProjectRoot;
            note.RegisterValueChangedCallback(evt =>
            {
                ChievfxMcpSecondaryProjects.SetNote(projectRoot, evt.newValue ?? string.Empty);
                ApplySecondaryProjectChange();
            });
            row.Add(note);

            row.Add(CreateActionRow(CreateButton("Remove", () =>
            {
                if (!EditorUtility.DisplayDialog(
                        "ChievFX MCP",
                        $"Remove {ChievfxMcpSecondaryProjects.FolderNameOf(projectRoot)} from this project's MCP client configs?\n\n{projectRoot}\n\nThe project itself is not touched.",
                        "Remove",
                        "Cancel"))
                {
                    return;
                }

                ChievfxMcpSecondaryProjects.Remove(projectRoot);
                ApplySecondaryProjectChange();
                BuildWindow();
            })));

            return row;
        }

        private void AddSecondaryProject()
        {
            var picked = EditorUtility.OpenFolderPanel("Select the other Unity project (or any folder inside it)", string.Empty, string.Empty);
            if (string.IsNullOrEmpty(picked))
            {
                return;
            }

            if (!ChievfxMcpSecondaryProjects.TryAdd(picked, out var error, out var warning))
            {
                EditorUtility.DisplayDialog("ChievFX MCP", error, "OK");
                return;
            }

            ApplySecondaryProjectChange();
            BuildWindow();

            var message = "Added. Reload MCP tools or restart your client so the second project's server appears.";
            if (!string.IsNullOrEmpty(warning))
            {
                message = $"{warning}\n\n{message}";
            }

            EditorUtility.DisplayDialog("ChievFX MCP", message, "OK");
        }

        // The set of servers this package owns in each client config just changed, so push it out now
        // instead of waiting for the next Unity open. Idempotent: configs already correct are not touched.
        private void ApplySecondaryProjectChange()
        {
            try
            {
                EnsureAllClientConfigs();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"ChievFX MCP could not update client configs after a secondary project change. {ex.Message}");
            }

            RefreshUi();
        }
    }
}
