#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Chievfx.Mcp.Editor;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using static Chievfx.Mcp.Extensions.Ugui.ChievfxMcpUguiExtension;
using static Chievfx.Mcp.Extensions.Ugui.UguiElementHelpers;
using static Chievfx.Mcp.Extensions.Ugui.UguiLayoutHelpers;
using static Chievfx.Mcp.Extensions.Ugui.UguiResourcesAndRows;
using static Chievfx.Mcp.Extensions.Ugui.UguiRuntimeControlHelpers;
using static Chievfx.Mcp.Extensions.Ugui.UguiRuntimeHelpers;
using static Chievfx.Mcp.Extensions.Ugui.UguiRuntimeTools;
using static Chievfx.Mcp.Extensions.Ugui.UguiSchemas;
using static Chievfx.Mcp.Extensions.Ugui.UguiSharedHelpers;

namespace Chievfx.Mcp.Extensions.Ugui
{
    internal static partial class UguiDesignTools
    {
        internal static Dictionary<string, object?> UiHierarchy(JToken args, UguiDependencyStatus status)
        {
            var roots = ResolveExplicitUiTargets(args);
            var maxResults = Math.Max(1, Math.Min(ReadInt(args, "maxResults", ReadInt(args, "maxElements", 64)), 256));
            var maxDepth = Math.Max(0, Math.Min(ReadInt(args, "maxDepth", 6), 24));
            var includeInactive = ResolveIncludeInactive(args, hasExplicitTargets: roots.Length > 0);
            var includeComponents = ReadBool(args, "includeComponents", false);
            var result = CreateToolEnvelope("ugui-ui-hierarchy");
            result["includeInactive"] = includeInactive;
            result["maxResults"] = maxResults;
            // With explicit paths, root the tree at the resolved objects directly (matching how
            // ui-runtime-click / ugui-ui-find resolve paths). Previously this filtered to Canvas
            // components in the subtree, so a valid non-Canvas leaf path (e.g. .../Wife_1) returned
            // count:0 even though the same path worked in the click tools. Without paths, default to
            // the scene's root canvases; a nested canvas is already inside its parent's tree.
            var requestsRoots = args["paths"] is JArray { Count: > 0 } || args["instanceIds"] is JArray { Count: > 0 };
            var hierarchyWarnings = new List<string>();
            AddUnresolvedTargetWarnings(args, hierarchyWarnings);
            if (hierarchyWarnings.Count > 0)
            {
                result["warnings"] = hierarchyWarnings.ToArray();
            }

            var rootObjects = roots.Length > 0 || requestsRoots
                ? roots
                : FindCanvases(status, includeInactive)
                    .Where(canvas => canvas.transform.parent == null || FindParentCanvas(canvas.transform.parent.gameObject, status) == null)
                    .Select(canvas => canvas.gameObject)
                    .ToArray();
            var emitted = 0;
            var truncated = false;
            var depthLimited = false;
            var rows = new List<Dictionary<string, object?>>();
            foreach (var rootObject in rootObjects)
            {
                var node = BuildUguiHierarchyNode(rootObject, includeInactive, includeComponents, 0, maxDepth, maxResults, ref emitted, ref truncated, ref depthLimited);
                if (node != null)
                {
                    rows.Add(node);
                }

                if (truncated)
                {
                    break;
                }
            }

            result["count"] = emitted;
            result["totalObjects"] = rootObjects.Sum(rootObject => rootObject.GetComponentsInChildren<RectTransform>(includeInactive).Length);
            result["maxDepth"] = maxDepth;
            result["truncated"] = truncated;
            result["depthLimited"] = depthLimited;
            result["roots"] = rows.ToArray();
            return result;
        }

        // Edit Mode lists inactive elements by default: authoring hidden panels is normal work. Play Mode asks
        // about the running UI, where every pooled template and closed screen is inactive, so a search lists
        // only active elements unless includeInactive is passed. Explicit paths/instanceIds are always
        // returned (with their active flags) unless includeInactive:false is passed.
        private static bool ResolveIncludeInactive(JToken args, bool hasExplicitTargets)
        {
            if (IsIncludeInactiveSpecified(args))
            {
                return ReadBool(args, "includeInactive", true);
            }

            return hasExplicitTargets || !Application.isPlaying;
        }

        private static bool IsIncludeInactiveSpecified(JToken args)
        {
            return args["includeInactive"] is JToken token && token.Type != JTokenType.Null;
        }

        private static readonly string[] UiFindKnownArguments =
        {
            "paths", "instanceIds", "name", "nameContains", "namePattern", "componentType",
            "includeInactive", "includeDetails", "normalizedCoords", "maxResults", "outputFormat",
        };

        private static void AddUnresolvedTargetWarnings(JToken args, List<string> warnings)
        {
            if (args["paths"] is JArray paths)
            {
                foreach (var path in paths.Where(item => item.Type == JTokenType.String).Select(item => item.Value<string>()))
                {
                    if (ResolveGameObject(path) == null)
                    {
                        warnings.Add($"Nothing exists at path '{path}' (loaded scenes and DontDestroyOnLoad). Paths are exact names joined by '/'; find the element with nameContains first.");
                    }
                }
            }

            if (args["instanceIds"] is JArray ids)
            {
                foreach (var id in ids.Where(item => item.Type == JTokenType.Integer).Select(item => item.Value<int>()))
                {
                    if (ResolveGameObject(id) == null)
                    {
                        warnings.Add($"No GameObject with instanceId {id}.");
                    }
                }
            }
        }

        internal static Dictionary<string, object?> UiFind(JToken args, UguiDependencyStatus status)
        {
            var warnings = new List<string>();
            // An unrecognized filter argument would silently apply no filter and report every
            // element as a match; call it out so a miss never masquerades as results.
            if (args is JObject requestObject)
            {
                var unknown = requestObject.Properties()
                    .Select(property => property.Name)
                    .Where(key => !UiFindKnownArguments.Contains(key, StringComparer.OrdinalIgnoreCase))
                    .ToArray();
                if (unknown.Length > 0)
                {
                    warnings.Add($"Ignored unsupported argument(s): {string.Join(", ", unknown)}. Supported filters: name (exact), nameContains (substring), namePattern (wildcards * ?), componentType, paths, instanceIds.");
                }
            }

            var includeDetails = ReadBool(args, "includeDetails", true);
            var normalizedCoords = ReadBool(args, "normalizedCoords", false);
            var maxResults = Math.Max(1, Math.Min(ReadInt(args, "maxResults", 16), 64));
            var explicitTargets = ResolveExplicitUiTargets(args);
            var name = ReadString(args, "name");
            var nameContains = ReadString(args, "nameContains");
            var namePattern = ReadString(args, "namePattern");
            var componentType = ReadString(args, "componentType");
            // Asked-for targets narrow the search even when none resolves: a stale or mistyped path used to fall back to
            // every element in the scene, reported as matches.
            var hasExplicitTarget = args["paths"] is JArray { Count: > 0 } || args["instanceIds"] is JArray { Count: > 0 };
            AddUnresolvedTargetWarnings(args, warnings);
            var includeInactive = ResolveIncludeInactive(args, hasExplicitTarget);

            GameObject[] FindMatches(bool withInactive)
            {
                var source = hasExplicitTarget
                    ? explicitTargets
                    : FindCanvases(status, withInactive)
                        .SelectMany(canvas => canvas.GetComponentsInChildren<RectTransform>(withInactive).Select(rect => rect.gameObject))
                        .Distinct()
                        .ToArray();
                return source
                    .Where(target => withInactive || target.activeInHierarchy)
                    .Where(target => string.IsNullOrWhiteSpace(name) || string.Equals(target.name, name, StringComparison.Ordinal))
                    .Where(target => string.IsNullOrWhiteSpace(nameContains) || target.name.IndexOf(nameContains!, StringComparison.OrdinalIgnoreCase) >= 0)
                    .Where(target => string.IsNullOrWhiteSpace(namePattern) || GameObjectBridgeService.WildcardMatches(target.name, namePattern!))
                    .Where(target => string.IsNullOrWhiteSpace(componentType) || HasComponentNamed(target, componentType!))
                    .ToArray();
            }

            var matches = FindMatches(includeInactive);
            var hasFilter = hasExplicitTarget
                || !string.IsNullOrWhiteSpace(name)
                || !string.IsNullOrWhiteSpace(nameContains)
                || !string.IsNullOrWhiteSpace(namePattern)
                || !string.IsNullOrWhiteSpace(componentType);
            if (!includeInactive && !IsIncludeInactiveSpecified(args))
            {
                var hiddenInactive = FindMatches(withInactive: true).Length - matches.Length;
                if (hiddenInactive > 0)
                {
                    warnings.Add($"{hiddenInactive} inactive match(es) not shown: in Play Mode only active elements are listed by default. Pass includeInactive:true to include them.");
                }
            }

            if (hasFilter && matches.Length == 0)
            {
                warnings.Add("No uGUI elements matched the filters.");
            }

            var selected = matches
                .Take(maxResults)
                .Select(target => includeDetails ? CreateUguiElementDetail(target, status, normalizedCoords) : CreateUguiElementRef(target))
                .ToArray();
            var result = CreateToolEnvelope("ugui-ui-find");
            result["count"] = selected.Length;
            result["totalMatches"] = matches.Length;
            result["matched"] = !hasFilter || matches.Length > 0;
            result["maxResults"] = maxResults;
            result["includeInactive"] = includeInactive;
            result["includeDetails"] = includeDetails;
            result["normalizedCoords"] = normalizedCoords;
            result["truncated"] = matches.Length > selected.Length;
            result["objects"] = selected;
            if (warnings.Count > 0)
            {
                result["warnings"] = warnings.ToArray();
            }

            return result;
        }

        internal static Dictionary<string, object?> TextMeshProHierarchy(JToken args, UguiDependencyStatus status)
        {
            EnsureTmpAvailable(status);
            var maxResults = Math.Max(1, Math.Min(ReadInt(args, "maxResults", 64), 256));
            var includeInactive = ReadBool(args, "includeInactive", true);
            var texts = FindTmpTextComponents(args, status, includeInactive)
                .Take(maxResults)
                .Select(CreateTmpTextRow)
                .ToArray();
            var groups = texts
                .GroupBy(row => (string)row["styleKey"]!)
                .Select(group => new Dictionary<string, object?>
                {
                    ["style"] = group.Key,
                    ["count"] = group.Count(),
                    ["items"] = group.Select(row => new Dictionary<string, object?>
                    {
                        ["name"] = row["name"],
                        ["path"] = row["path"],
                        ["instanceId"] = row["instanceId"],
                        ["text"] = row["text"],
                    }).ToArray(),
                })
                .ToArray();
            var result = CreateToolEnvelope("ugui-textmeshpro-hierarchy");
            result["count"] = texts.Length;
            result["groupCount"] = groups.Length;
            result["maxResults"] = maxResults;
            result["groups"] = groups;
            return result;
        }

        internal static Dictionary<string, object?> TextMeshProGet(JToken args, UguiDependencyStatus status)
        {
            EnsureTmpAvailable(status);
            var includeInactive = ReadBool(args, "includeInactive", true);
            var rows = FindTmpTextComponents(args, status, includeInactive)
                .Take(Math.Max(1, Math.Min(ReadInt(args, "maxResults", 16), 64)))
                .Select(CreateTmpTextRow)
                .ToArray();
            var result = CreateToolEnvelope("ugui-textmeshpro-get");
            result["count"] = rows.Length;
            result["texts"] = rows;
            return result;
        }

        internal static Dictionary<string, object?> TextMeshProSetOrCreate(JToken args, UguiDependencyStatus status)
        {
            EnsureTmpAvailable(status);
            var warnings = new List<string>();
            var isCreate = ReadBool(args, "isCreate", false);
            var createdCount = 0;
            var targets = isCreate
                ? ResolveOrCreateTmpTextTargets(args, status, warnings, out createdCount)
                : FindTmpTextComponents(args, status, includeInactive: true).ToArray();
            if (targets.Length == 0)
            {
                throw new ArgumentException("ugui-textmeshpro-set-or-create requires paths or instanceIds resolving to TextMeshProUGUI components. With isCreate:true, paths/instanceIds may target any uGUI GameObject; use placement:'same-object' or placement:'child'.");
            }

            foreach (var text in targets)
            {
                Undo.RecordObject(text, "ChievFX MCP Update TextMeshProUGUI");
                if (isCreate)
                {
                    ConfigureTmpTextDefaults(text);
                }

                if (args["text"] != null)
                {
                    SetProperty(text, "text", ReadString(args, "text") ?? string.Empty);
                }
                if (args["fontSize"] != null)
                {
                    SetProperty(text, "fontSize", ReadFloat(args, "fontSize", Convert.ToSingle(GetPropertyValue(text, "fontSize"), CultureInfo.InvariantCulture)));
                }
                if (args["color"] != null)
                {
                    SetProperty(text, "color", ReadColor(args["color"]!, Color.white));
                }
                if (args["alignment"] != null)
                {
                    SetEnumProperty(text, "alignment", NormalizeTmpAlignment(ReadString(args, "alignment") ?? "TopLeft"));
                }
                if (args["bold"] != null)
                {
                    SetEnumProperty(text, "fontStyle", ReadBool(args, "bold", false) ? "Bold" : "Normal");
                }
                if (args["wrapping"] != null)
                {
                    SetProperty(text, "enableWordWrapping", ReadBool(args, "wrapping", true));
                }
                if (args["outlineWidth"] != null)
                {
                    SetProperty(text, "outlineWidth", ReadFloat(args, "outlineWidth", 0f));
                }
                if (args["outlineColor"] != null)
                {
                    SetProperty(text, "outlineColor", ReadColor(args["outlineColor"]!, Color.black));
                }

                MarkDirty(text.gameObject);
            }

            var result = CreateToolEnvelope("ugui-textmeshpro-set-or-create");
            result["success"] = true;
            result["updatedCount"] = targets.Length;
            result["createdCount"] = createdCount;
            result["skippedCount"] = 0;
            result["createdOrEnsured"] = isCreate;
            AddWarnings(result, warnings);
            return result;
        }
    }
}
