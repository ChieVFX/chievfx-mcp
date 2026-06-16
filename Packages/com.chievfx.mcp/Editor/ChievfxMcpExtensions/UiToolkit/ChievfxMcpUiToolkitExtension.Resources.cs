#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Chievfx.Mcp.Editor;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using static Chievfx.Mcp.Extensions.UiToolkit.ChievfxMcpUiToolkitExtension;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitRuntimeTools;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitInteractions;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitPanelQueries;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitRows;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitShared;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitSchemas;

namespace Chievfx.Mcp.Extensions.UiToolkit
{
    internal static class UiToolkitResources
    {
        internal static Dictionary<string, object?> ReadRuntimeStatus(string uri, UiToolkitDependencyStatus status)
        {
            var warnings = new List<string>();
            var result = CreateEnvelope(uri, status);
            AddCoordinateInfo(result, RuntimeScreenPosition.FromScreenPosition(new Vector2(Screen.width * 0.5f, Screen.height * 0.5f)));
            result["playMode"] = IsRuntimePlayModeActive();
            result["runtimeAvailable"] = EnsureRuntimeReadAllowed(warnings);
            if (Equals(result["runtimeAvailable"], false))
            {
                result["documentCount"] = 0;
                result["panelCount"] = 0;
            }
            else
            {
                var documents = FindRuntimeDocuments(status);
                result["documentCount"] = documents.Length;
                result["panelCount"] = FindRuntimePanelGroups(status).Length;
            }

            result["warnings"] = warnings.ToArray();
            return result;
        }

        internal static Dictionary<string, object?> ReadRuntimePanels(string uri, UiToolkitDependencyStatus status)
        {
            var warnings = new List<string>();
            var result = CreateEnvelope(uri, status);
            result["playMode"] = IsRuntimePlayModeActive();
            result["runtimeAvailable"] = EnsureRuntimeReadAllowed(warnings);
            if (Equals(result["runtimeAvailable"], false))
            {
                result["count"] = 0;
                result["panels"] = Array.Empty<Dictionary<string, object?>>();
                result["documents"] = Array.Empty<Dictionary<string, object?>>();
                result["warnings"] = warnings.ToArray();
                return result;
            }

            var documents = FindRuntimeDocuments(status);
            var panelGroups = FindRuntimePanelGroups(status);
            result["count"] = panelGroups.Length;
            result["panels"] = panelGroups.Select(group => CreatePanelRow(group, status)).ToArray();
            result["documents"] = documents.Select(document => CreateDocumentRow(document, status)).ToArray();
            result["warnings"] = warnings.ToArray();
            return result;
        }

        internal static Dictionary<string, object?> ReadRuntimeVisibleTree(string uri, UiToolkitDependencyStatus status)
        {
            var warnings = new List<string>();
            var result = CreateEnvelope(uri, status);
            result["playMode"] = IsRuntimePlayModeActive();
            result["runtimeAvailable"] = EnsureRuntimeReadAllowed(warnings);
            result["maxRowsPerDocument"] = DefaultMaxRows;
            if (Equals(result["runtimeAvailable"], false))
            {
                result["count"] = 0;
                result["documents"] = Array.Empty<Dictionary<string, object?>>();
                result["warnings"] = warnings.ToArray();
                return result;
            }

            var total = 0;
            var documents = FindRuntimeDocuments(status)
                .Select(document =>
                {
                    var row = CreateDocumentRow(document, status);
                    var root = GetRootVisualElement(document);
                    var elements = root == null
                        ? Array.Empty<Dictionary<string, object?>>()
                        : EnumerateVisibleTree(root, status, DefaultMaxRows, out var truncated)
                            .Select(item =>
                            {
                                var elementRow = CreateVisualElementRow(item.Element, status, PanelGroup.FromDocument(document), includeTextAndValue: true);
                                elementRow["depth"] = item.Depth;
                                return elementRow;
                            })
                            .ToArray();
                    total += elements.Length;
                    row["elements"] = elements;
                    row["elementCount"] = elements.Length;
                    row["truncated"] = root != null && CountVisibleElements(root, status, DefaultMaxRows + 1) > elements.Length;
                    return row;
                })
                .ToArray();
            result["count"] = total;
            result["documents"] = documents;
            result["warnings"] = warnings.ToArray();
            return result;
        }
    }
}
