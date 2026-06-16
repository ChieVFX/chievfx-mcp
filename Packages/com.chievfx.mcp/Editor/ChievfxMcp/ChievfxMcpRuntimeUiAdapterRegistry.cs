#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Chievfx.Mcp.Editor
{
    internal interface IChievfxMcpRuntimeUiAdapter
    {
        string FrameworkId { get; }

        string FrameworkName { get; }

        int Priority { get; }

        bool Available { get; }

        object? Status { get; }

        IEnumerable<string> Resources { get; }

        object? ProbeScreenPosition(JToken request);
    }

    /// <summary>
    /// Optional capability implemented by runtime UI adapters that can focus a runtime text
    /// field and type text into it. Enables the shared cross-framework "ui-runtime-type-text" tool.
    /// </summary>
    internal interface IChievfxMcpRuntimeUiTextInputAdapter
    {
        /// <summary>
        /// Focuses the resolved runtime text field (when requested) and types into it.
        /// Must not mutate state when the request is a dry run or when no text field resolves.
        /// When <paramref name="requireTarget"/> is true the adapter throws if it cannot resolve a
        /// text field; otherwise it returns a result whose "resolved" flag is false.
        /// </summary>
        object? TypeIntoFocusedTextField(JToken request, bool requireTarget);
    }

    /// <summary>
    /// Optional capability for listing enabled, on-screen clickable controls in a framework.
    /// </summary>
    internal interface IChievfxMcpRuntimeUiControlFindAdapter
    {
        object? FindControls(JToken request);
    }

    internal static class ChievfxMcpRuntimeUiAdapterRegistry
    {
        private const string ExtensionId = "chievfx.runtime-ui";
        private const string Category = "Runtime UI";
        private const string CommonCategory = "ui-runtime-common";
        private const string EssentialsCategory = "Essentials";
        private const string UriPrefix = "chievfx://extensions/chievfx.runtime-ui/";
        private const string StatusUri = UriPrefix + "status";
        private const string ProbeToolName = "runtime-ui-probe-screen-position";
        private const string TypeTextToolName = "ui-runtime-type-text";
        private const int DefaultMaxRows = 256;

        private static readonly Regex FrameworkIdPattern = new(@"^[a-z0-9][a-z0-9._-]{0,127}$", RegexOptions.Compiled);
        private static readonly object SyncRoot = new();
        private static readonly Dictionary<string, RegisteredAdapter> Adapters = new(StringComparer.Ordinal);
        private static int nextRegistrationOrder;

        static ChievfxMcpRuntimeUiAdapterRegistry()
        {
            ChievfxMcpExtensionRegistry.RegisterExtension(CreateDescriptor());
        }

        public static void EnsureRegistered()
        {
        }

        public static void Register(IChievfxMcpRuntimeUiAdapter adapter)
        {
            if (adapter == null)
            {
                throw new ArgumentNullException(nameof(adapter));
            }

            if (string.IsNullOrWhiteSpace(adapter.FrameworkId) || !FrameworkIdPattern.IsMatch(adapter.FrameworkId))
            {
                throw new InvalidOperationException($"Runtime UI adapter framework id '{adapter.FrameworkId}' is invalid.");
            }

            lock (SyncRoot)
            {
                Adapters[adapter.FrameworkId] = new RegisteredAdapter(adapter, nextRegistrationOrder++);
            }
        }

        public static bool Unregister(string frameworkId)
        {
            lock (SyncRoot)
            {
                return Adapters.Remove(frameworkId);
            }
        }

        private static ChievfxMcpExtensionDescriptor CreateDescriptor()
        {
            var descriptor = new ChievfxMcpExtensionDescriptor
            {
                Id = ExtensionId,
                DisplayName = "ChievFX MCP Runtime UI",
                Version = "0.1.0",
                Description = "Shared runtime UI adapter registry and merged screen-position probe.",
                ToolRunner = RunTool,
                ResourceReader = ReadResource,
            };
            descriptor.Resources.Add(new ChievfxMcpResourceDescriptor
            {
                Id = "runtime-ui-status",
                Uri = StatusUri,
                Name = "Runtime UI adapter status",
                Description = "Compact runtime UI capability summary, current hierarchy counts, and Play Mode drill-down hints.",
                MimeType = "application/json",
                Category = EssentialsCategory,
            });
            descriptor.Tools.Add(new ChievfxMcpToolDescriptor
            {
                Name = ProbeToolName,
                Description = "Probe Play Mode runtime UI hit stack at screen position. Requires Play Mode.",
                Category = Category,
                InputSchema = RuntimeProbeSchema(),
            });
            descriptor.Tools.Add(new ChievfxMcpToolDescriptor
            {
                Name = TypeTextToolName,
                Description = "Focus a Play Mode text field and type text into it. Works for uGUI InputField/TMP_InputField and UI Toolkit TextField; auto-detects the framework or use framework to force one. Requires Play Mode and allowStateMutation:true to mutate.",
                Category = CommonCategory,
                InputSchema = TypeTextSchema(),
            });
            return descriptor;
        }

        private static object? RunTool(string toolName, JToken args)
        {
            return toolName switch
            {
                ProbeToolName => ProbeScreenPosition("tool://" + ProbeToolName, args),
                TypeTextToolName => TypeText("tool://" + TypeTextToolName, args),
                _ => throw new InvalidOperationException($"Unknown runtime UI registry tool '{toolName}'."),
            };
        }

        private static object? TypeText(string uri, JToken args)
        {
            var request = args is JObject obj ? obj : new JObject();
            var framework = (request["framework"]?.Value<string>() ?? string.Empty).Trim().ToLowerInvariant();
            var candidates = SnapshotAdapters()
                .Where(registered => registered.Adapter is IChievfxMcpRuntimeUiTextInputAdapter)
                .ToArray();
            if (candidates.Length == 0)
            {
                throw new InvalidOperationException("No runtime UI adapter supports text input.");
            }

            if (!string.IsNullOrEmpty(framework) && !string.Equals(framework, "auto", StringComparison.Ordinal))
            {
                var selected = candidates.FirstOrDefault(registered => string.Equals(registered.Adapter.FrameworkId, framework, StringComparison.Ordinal));
                if (selected == null)
                {
                    throw new ArgumentException($"No text-input adapter for framework '{framework}'. Available: {string.Join(", ", candidates.Select(registered => registered.Adapter.FrameworkId))}.");
                }

                if (!selected.Adapter.Available)
                {
                    throw new InvalidOperationException($"Text-input adapter '{framework}' is registered but unavailable.");
                }

                var forced = ((IChievfxMcpRuntimeUiTextInputAdapter)selected.Adapter).TypeIntoFocusedTextField(request.DeepClone(), requireTarget: true);
                return WrapTypeTextResult(uri, selected.Adapter, forced);
            }

            var attempts = new List<Dictionary<string, object?>>();
            foreach (var registered in candidates)
            {
                if (!registered.Adapter.Available)
                {
                    attempts.Add(new Dictionary<string, object?> { ["framework"] = registered.Adapter.FrameworkId, ["available"] = false });
                    continue;
                }

                object? result;
                try
                {
                    result = ((IChievfxMcpRuntimeUiTextInputAdapter)registered.Adapter).TypeIntoFocusedTextField(request.DeepClone(), requireTarget: false);
                }
                catch (Exception ex)
                {
                    attempts.Add(new Dictionary<string, object?> { ["framework"] = registered.Adapter.FrameworkId, ["error"] = RootMessage(ex) });
                    continue;
                }

                if (ReadResolvedFlag(result))
                {
                    return WrapTypeTextResult(uri, registered.Adapter, result);
                }

                attempts.Add(new Dictionary<string, object?> { ["framework"] = registered.Adapter.FrameworkId, ["resolved"] = false, ["detail"] = result });
            }

            return new Dictionary<string, object?>
            {
                ["uri"] = uri,
                ["resolved"] = false,
                ["framework"] = null,
                ["warnings"] = new[] { "No uGUI or UI Toolkit text field resolved from the supplied target or screen position. Provide targetPath/instanceId/name/visualElementRef or a screenPosition over a focusable text field, or set framework explicitly." },
                ["attempts"] = attempts.ToArray(),
            };
        }

        private static Dictionary<string, object?> WrapTypeTextResult(string uri, IChievfxMcpRuntimeUiAdapter adapter, object? result)
        {
            if (!TryReadDictionary(result, out var row))
            {
                row = new Dictionary<string, object?> { ["result"] = result };
            }

            row["uri"] = uri;
            row["framework"] = adapter.FrameworkId;
            return row;
        }

        private static bool ReadResolvedFlag(object? result)
        {
            return TryReadDictionary(result, out var row)
                && row.TryGetValue("resolved", out var resolved)
                && resolved is bool resolvedValue
                && resolvedValue;
        }

        internal static object? ControlFind(JToken args)
        {
            var request = args is JObject obj ? obj : new JObject();
            var framework = (request["framework"]?.Value<string>() ?? string.Empty).Trim().ToLowerInvariant();
            var nameFilter = request["name"]?.Value<string>();
            var controlTypeFilter = ChievfxMcpRuntimeUiControlFind.NormalizeControlTypeFilter(request["controlType"]?.Value<string>());
            var pageSize = ChievfxMcpRuntimeUiControlFind.DefaultPageSize;
            var warnings = new List<string>();
            var controls = new List<Dictionary<string, object?>>();
            var totalMatches = 0;
            var sections = new List<Dictionary<string, object?>>();

            foreach (var registered in SnapshotAdapters())
            {
                if (!string.IsNullOrEmpty(framework)
                    && !string.Equals(framework, "auto", StringComparison.Ordinal)
                    && !string.Equals(registered.Adapter.FrameworkId, framework, StringComparison.Ordinal))
                {
                    continue;
                }

                if (registered.Adapter is not IChievfxMcpRuntimeUiControlFindAdapter finder)
                {
                    continue;
                }

                if (!registered.Adapter.Available)
                {
                    warnings.Add($"Framework '{registered.Adapter.FrameworkId}' is unavailable.");
                    sections.Add(new Dictionary<string, object?>
                    {
                        ["framework"] = registered.Adapter.FrameworkId,
                        ["available"] = false,
                        ["totalMatches"] = 0,
                        ["controls"] = Array.Empty<Dictionary<string, object?>>(),
                    });
                    continue;
                }

                if (!TryReadDictionary(finder.FindControls(request.DeepClone()), out var section))
                {
                    continue;
                }

                sections.Add(section);
                totalMatches += section.TryGetValue("totalMatches", out var total) && total is int totalValue ? totalValue : 0;
                if (section.TryGetValue("warnings", out var sectionWarnings) && sectionWarnings is IEnumerable enumerable)
                {
                    foreach (var warning in enumerable)
                    {
                        if (warning != null)
                        {
                            warnings.Add(Convert.ToString(warning, CultureInfo.InvariantCulture) ?? string.Empty);
                        }
                    }
                }

                if (section.TryGetValue("controls", out var sectionControls) && sectionControls is IEnumerable controlRows)
                {
                    foreach (var controlRow in controlRows)
                    {
                        if (TryReadDictionary(controlRow, out var control))
                        {
                            controls.Add(control);
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(framework)
                && !string.Equals(framework, "auto", StringComparison.Ordinal)
                && sections.Count == 0)
            {
                throw new ArgumentException($"No control-find adapter for framework '{framework}'. Available: {string.Join(", ", SnapshotAdapters().Where(registered => registered.Adapter is IChievfxMcpRuntimeUiControlFindAdapter).Select(registered => registered.Adapter.FrameworkId))}.");
            }

            var totalPages = Math.Max(1, (int)Math.Ceiling(totalMatches / (double)pageSize));
            var page = Math.Max(1, ReadInt(request, "page", 1));
            if (page > totalPages)
            {
                page = totalPages;
            }

            var normalizeCoords = ReadBool(request, "normalizeCoords", false);
            var selected = controls
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToArray();
            var payload = new Dictionary<string, object?>
            {
                ["page"] = page,
                ["totalPages"] = totalPages,
                ["total"] = totalMatches,
                ["nameFilter"] = nameFilter,
                ["controlTypeFilter"] = controlTypeFilter,
                ["normalizeCoords"] = normalizeCoords,
                ["frameworkFilter"] = string.IsNullOrEmpty(framework) ? null : framework,
                ["controls"] = selected,
                ["frameworks"] = sections.ToArray(),
                ["warnings"] = warnings.Where(warning => !string.IsNullOrWhiteSpace(warning)).Distinct().ToArray(),
            };
            var outputFormat = request["outputFormat"]?.Value<string>();
            if (string.Equals(outputFormat, "json", StringComparison.OrdinalIgnoreCase))
            {
                return payload;
            }

            return ChievfxMcpRuntimeUiControlFind.FormatText(page, totalPages, selected, controlTypeFilter, normalizeCoords);
        }

        private static object? ReadResource(string uri)
        {
            if (string.Equals(uri, StatusUri, StringComparison.Ordinal))
            {
                return ReadStatus(uri);
            }

            return null;
        }

        private static Dictionary<string, object?> ReadStatus(string uri)
        {
            var adapters = SnapshotAdapters();
            var canvasType = FindLoadedType("UnityEngine.Canvas");
            var eventSystemType = FindLoadedType("UnityEngine.EventSystems.EventSystem");
            var tmpTextType = FindLoadedType("TMPro.TextMeshProUGUI");
            var uiDocumentType = FindLoadedType("UnityEngine.UIElements.UIDocument");
            var capabilities = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var registered in adapters)
            {
                if (!TryReadDictionary(registered.Adapter.Status, out var status))
                {
                    capabilities[registered.Adapter.FrameworkId] = registered.Adapter.Available
                        ? new Dictionary<string, object?> { ["loaded"] = true }
                        : new Dictionary<string, object?> { ["loaded"] = false };
                    continue;
                }

                if (string.Equals(registered.Adapter.FrameworkId, "ugui", StringComparison.Ordinal))
                {
                    capabilities["ugui"] = ChievfxMcpUiStatusHelpers.ExtractCompactCapability(status);
                    capabilities["textMeshPro"] = Equals(ReadObject(status, "textMeshProConfigured"), true)
                        ? new Dictionary<string, object?> { ["loaded"] = true }
                        : new Dictionary<string, object?> { ["loaded"] = false };
                }
                else if (string.Equals(registered.Adapter.FrameworkId, "uitoolkit", StringComparison.Ordinal))
                {
                    capabilities["uitoolkit"] = ChievfxMcpUiStatusHelpers.ExtractCompactCapability(status);
                }
                else
                {
                    capabilities[registered.Adapter.FrameworkId] = registered.Adapter.Available
                        ? new Dictionary<string, object?> { ["loaded"] = true }
                        : new Dictionary<string, object?> { ["loaded"] = false };
                }
            }

            return new Dictionary<string, object?>
            {
                ["context"] = ChievfxMcpUiStatusHelpers.DescribeEditorContext(),
                ["capabilities"] = capabilities,
                ["currentHierarchy"] = ChievfxMcpUiStatusHelpers.DescribeCrossFrameworkHierarchy(
                    canvasType,
                    eventSystemType,
                    tmpTextType,
                    uiDocumentType),
                ["adapters"] = adapters.Select(CreateCompactAdapterStatusRow).ToArray(),
            };
        }

        private static Dictionary<string, object?> CreateCompactAdapterStatusRow(RegisteredAdapter registered)
        {
            var adapter = registered.Adapter;
            return new Dictionary<string, object?>
            {
                ["frameworkId"] = adapter.FrameworkId,
                ["frameworkName"] = adapter.FrameworkName,
                ["priority"] = adapter.Priority,
            };
        }

        private static Type? FindLoadedType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(fullName, throwOnError: false);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        private static Dictionary<string, object?> ProbeScreenPosition(string uri, JToken args)
        {
            ChievfxMcpRuntimeUiProbeCompact.EnsurePlayModeForProbe(EditorApplication.isPlaying || Application.isPlaying);

            var request = args is JObject obj ? obj : new JObject();
            var maxRows = Mathf.Clamp(ReadInt(request, "maxRows", DefaultMaxRows), 1, 1024);
            var warnings = new List<string>();
            var position = ReadScreenPosition(request, warnings);
            var probe = ChievfxMcpRuntimeUiProbeCompact.CreateProbeBlock(
                position.ScreenSize,
                position.ScreenPosition,
                position.NormalizedPosition);

            Dictionary<string, object?>? uguiSection = null;
            Dictionary<string, object?>? uitoolkitSection = null;
            var anyProbed = false;
            var truncated = false;

            foreach (var registered in SnapshotAdapters())
            {
                var adapter = registered.Adapter;
                if (string.Equals(adapter.FrameworkId, "ugui", StringComparison.Ordinal))
                {
                    uguiSection = ProbeAdapterSection(adapter, request, "ugui", position, ref anyProbed, ref truncated);
                    continue;
                }

                if (string.Equals(adapter.FrameworkId, "uitoolkit", StringComparison.Ordinal))
                {
                    uitoolkitSection = ProbeAdapterSection(adapter, request, "uitoolkit", position, ref anyProbed, ref truncated);
                }
            }

            return ChievfxMcpRuntimeUiProbeCompact.CreateProbeResult(
                probe,
                runtimeAvailable: anyProbed,
                maxRows,
                truncated,
                warnings,
                uguiSection,
                uitoolkitSection);
        }

        private static Dictionary<string, object?> ProbeAdapterSection(
            IChievfxMcpRuntimeUiAdapter adapter,
            JToken request,
            string frameworkId,
            RuntimeScreenPosition position,
            ref bool anyProbed,
            ref bool truncated)
        {
            if (!adapter.Available)
            {
                return frameworkId switch
                {
                    "ugui" => ChievfxMcpRuntimeUiProbeCompact.CreateUguiSection(
                        available: false,
                        probed: false,
                        Array.Empty<Dictionary<string, object?>>(),
                        new[] { "Adapter is registered but unavailable." }),
                    "uitoolkit" => ChievfxMcpRuntimeUiProbeCompact.CreateUiToolkitSection(
                        available: false,
                        probed: false,
                        position.ScreenSize,
                        position.ScreenPosition,
                        Array.Empty<Dictionary<string, object?>>(),
                        new[] { "Adapter is registered but unavailable." }),
                    _ => throw new InvalidOperationException($"Unknown runtime UI adapter '{frameworkId}'."),
                };
            }

            try
            {
                var adapterProbe = adapter.ProbeScreenPosition(request.DeepClone());
                anyProbed = true;
                var sectionTruncated = ChievfxMcpRuntimeUiProbeCompact.ReadTruncated(adapterProbe, frameworkId);
                truncated |= sectionTruncated;
                var hits = ChievfxMcpRuntimeUiProbeCompact.ReadHits(adapterProbe, frameworkId);
                var sectionWarnings = ChievfxMcpRuntimeUiProbeCompact.ReadSectionWarnings(adapterProbe, frameworkId);
                return frameworkId switch
                {
                    "ugui" => ChievfxMcpRuntimeUiProbeCompact.CreateUguiSection(
                        available: true,
                        probed: true,
                        hits,
                        sectionWarnings,
                        sectionTruncated),
                    "uitoolkit" => ChievfxMcpRuntimeUiProbeCompact.CreateUiToolkitSection(
                        available: true,
                        probed: true,
                        position.ScreenSize,
                        position.ScreenPosition,
                        hits,
                        sectionWarnings,
                        sectionTruncated),
                    _ => throw new InvalidOperationException($"Unknown runtime UI adapter '{frameworkId}'."),
                };
            }
            catch (Exception ex)
            {
                return frameworkId switch
                {
                    "ugui" => ChievfxMcpRuntimeUiProbeCompact.CreateUguiSection(
                        available: true,
                        probed: false,
                        Array.Empty<Dictionary<string, object?>>(),
                        new[] { "Adapter probe failed: " + RootMessage(ex) }),
                    "uitoolkit" => ChievfxMcpRuntimeUiProbeCompact.CreateUiToolkitSection(
                        available: true,
                        probed: false,
                        position.ScreenSize,
                        position.ScreenPosition,
                        Array.Empty<Dictionary<string, object?>>(),
                        new[] { "Adapter probe failed: " + RootMessage(ex) }),
                    _ => throw new InvalidOperationException($"Unknown runtime UI adapter '{frameworkId}'."),
                };
            }
        }

        private static RegisteredAdapter[] SnapshotAdapters()
        {
            lock (SyncRoot)
            {
                return Adapters.Values
                    .OrderByDescending(item => item.Adapter.Priority)
                    .ThenBy(item => item.RegistrationOrder)
                    .ThenBy(item => item.Adapter.FrameworkId, StringComparer.Ordinal)
                    .ToArray();
            }
        }

        private static object? ReadObject(Dictionary<string, object?> source, string key)
        {
            return source.TryGetValue(key, out var value) ? value : null;
        }

        private static bool TryReadDictionary(object? value, out Dictionary<string, object?> dictionary)
        {
            if (value is Dictionary<string, object?> typed)
            {
                dictionary = typed;
                return true;
            }

            if (value is JObject obj)
            {
                dictionary = obj.Properties().ToDictionary(property => property.Name, property => (object?)property.Value);
                return true;
            }

            if (value is IDictionary untyped)
            {
                dictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in untyped)
                {
                    if (entry.Key is string key)
                    {
                        dictionary[key] = entry.Value;
                    }
                }

                return true;
            }

            dictionary = new Dictionary<string, object?>();
            return false;
        }

        private static IEnumerable<object?> ReadEnumerable(object value)
        {
            if (value is JArray array)
            {
                return array.Cast<object?>();
            }

            return value is IEnumerable enumerable && value is not string
                ? enumerable.Cast<object?>()
                : Enumerable.Empty<object?>();
        }

        private static bool TryConvertInt(object? value, out int intValue)
        {
            switch (value)
            {
                case int typed:
                    intValue = typed;
                    return true;
                case long typed:
                    intValue = (int)typed;
                    return true;
                case float typed:
                    intValue = Mathf.RoundToInt(typed);
                    return true;
                case double typed:
                    intValue = (int)Math.Round(typed);
                    return true;
                case JValue { Value: int typed }:
                    intValue = typed;
                    return true;
                case JValue { Value: long typed }:
                    intValue = (int)typed;
                    return true;
                case JValue { Value: double typed }:
                    intValue = (int)Math.Round(typed);
                    return true;
                case JValue { Value: string typed } when int.TryParse(typed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                    intValue = parsed;
                    return true;
                default:
                    intValue = 0;
                    return false;
            }
        }

        private static RuntimeScreenPosition ReadScreenPosition(JToken args, List<string> warnings)
        {
            var screenSize = new Vector2(Mathf.Max(1, Screen.width), Mathf.Max(1, Screen.height));
            if (TryReadVector2(args["normalized"], out var normalized))
            {
                return new RuntimeScreenPosition(new Vector2(normalized.x * screenSize.x, normalized.y * screenSize.y), screenSize, normalized);
            }

            if (TryReadVector2(args["screenPosition"], out var screenPosition))
            {
                return RuntimeScreenPosition.FromScreenPosition(screenPosition);
            }

            if (args["x"] != null || args["y"] != null)
            {
                return RuntimeScreenPosition.FromScreenPosition(new Vector2(ReadFloat(args, "x", 0f), ReadFloat(args, "y", 0f)));
            }

            warnings.Add("No screen position supplied; defaulted to normalized center (0.5, 0.5).");
            return new RuntimeScreenPosition(screenSize * 0.5f, screenSize, new Vector2(0.5f, 0.5f));
        }

        private static JObject RuntimeProbeSchema()
        {
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["screenPosition"] = Vector2Schema("Bottom-left-origin screen position in pixels."),
                    ["normalized"] = Vector2Schema("Normalized bottom-left-origin screen position, where 0.5/0.5 is screen center."),
                    ["x"] = NumberSchema("Bottom-left-origin screen x in pixels."),
                    ["y"] = NumberSchema("Bottom-left-origin screen y in pixels."),
                    ["maxRows"] = new JObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = 1,
                        ["maximum"] = 1024,
                        ["default"] = DefaultMaxRows,
                        ["description"] = "Maximum merged hit rows to return.",
                    },
                },
                ["additionalProperties"] = true,
            };
        }

        private static JObject TypeTextSchema()
        {
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["text"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Text to type into the focused text field.",
                    },
                    ["framework"] = new JObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JArray("auto", "ugui", "uitoolkit"),
                        ["description"] = "Target framework. auto (default) resolves the field across registered frameworks; ugui/uitoolkit force one.",
                    },
                    ["targetPath"] = new JObject { ["type"] = "string", ["description"] = "uGUI GameObject transform path or UI Toolkit VisualElement path of the text field." },
                    ["path"] = new JObject { ["type"] = "string", ["description"] = "Alias for targetPath (UI Toolkit)." },
                    ["instanceId"] = new JObject { ["type"] = "integer", ["description"] = "uGUI target GameObject instance id." },
                    ["name"] = new JObject { ["type"] = "string", ["description"] = "UI Toolkit VisualElement name." },
                    ["targetName"] = new JObject { ["type"] = "string", ["description"] = "Alias for name (UI Toolkit)." },
                    ["visualElementRef"] = new JObject { ["type"] = "string", ["description"] = "UI Toolkit visualElementRef from runtime reads/probes." },
                    ["targetRef"] = new JObject { ["type"] = "string", ["description"] = "Alias for visualElementRef." },
                    ["screenPosition"] = Vector2Schema("Bottom-left-origin screen position in pixels used to resolve the field when no explicit target is supplied."),
                    ["normalized"] = Vector2Schema("Normalized bottom-left-origin screen position, where 0.5/0.5 is screen center."),
                    ["x"] = NumberSchema("Bottom-left-origin screen x in pixels."),
                    ["y"] = NumberSchema("Bottom-left-origin screen y in pixels."),
                    ["append"] = new JObject { ["type"] = "boolean", ["description"] = "Append to the current text instead of replacing it. Defaults false." },
                    ["focus"] = new JObject { ["type"] = "boolean", ["description"] = "Focus/select the text field before typing. Defaults true." },
                    ["submit"] = new JObject { ["type"] = "boolean", ["description"] = "After typing, submit/end edit (uGUI onEndEdit, UI Toolkit NavigationSubmit + blur). Defaults false." },
                    ["invokeCallbacks"] = new JObject { ["type"] = "boolean", ["description"] = "When true (default), use notifying setters that fire onValueChanged/ChangeEvent. When false, prefer SetTextWithoutNotify/SetValueWithoutNotify." },
                    ["dryRun"] = new JObject { ["type"] = "boolean", ["description"] = "Report resolved target and plan without focusing, typing, or mutating. Defaults false." },
                    ["allowStateMutation"] = new JObject { ["type"] = "boolean", ["description"] = "Required true for non-dry-run typing because callbacks may mutate game state." },
                },
                ["required"] = new JArray("text"),
                ["additionalProperties"] = true,
            };
        }

        private static JObject Vector2Schema(string description)
        {
            return new JObject
            {
                ["type"] = "object",
                ["description"] = description,
                ["properties"] = new JObject
                {
                    ["x"] = NumberSchema("X coordinate."),
                    ["y"] = NumberSchema("Y coordinate."),
                },
                ["required"] = new JArray("x", "y"),
                ["additionalProperties"] = false,
            };
        }

        private static JObject NumberSchema(string description)
        {
            return new JObject
            {
                ["type"] = "number",
                ["description"] = description,
            };
        }

        private static bool TryReadVector2(JToken? token, out Vector2 value)
        {
            if (token is JObject obj && obj["x"] != null && obj["y"] != null)
            {
                value = new Vector2(ReadFloat(obj, "x", 0f), ReadFloat(obj, "y", 0f));
                return true;
            }

            value = default;
            return false;
        }

        private static int ReadInt(JToken token, string key, int defaultValue)
        {
            return token[key]?.Value<int?>() ?? defaultValue;
        }

        private static bool ReadBool(JToken token, string key, bool defaultValue)
        {
            return token[key]?.Value<bool?>() ?? defaultValue;
        }

        private static float ReadFloat(JToken token, string key, float defaultValue)
        {
            return token[key]?.Value<float?>() ?? defaultValue;
        }

        private static string RootMessage(Exception ex)
        {
            return ex is System.Reflection.TargetInvocationException { InnerException: { } inner }
                ? RootMessage(inner)
                : ex.Message;
        }

        private sealed class RegisteredAdapter
        {
            public RegisteredAdapter(IChievfxMcpRuntimeUiAdapter adapter, int registrationOrder)
            {
                Adapter = adapter;
                RegistrationOrder = registrationOrder;
            }

            public IChievfxMcpRuntimeUiAdapter Adapter { get; }

            public int RegistrationOrder { get; }
        }

        private readonly struct RuntimeScreenPosition
        {
            public RuntimeScreenPosition(Vector2 screenPosition, Vector2 screenSize, Vector2 normalizedPosition)
            {
                ScreenPosition = screenPosition;
                ScreenSize = screenSize;
                NormalizedPosition = normalizedPosition;
            }

            public Vector2 ScreenPosition { get; }

            public Vector2 ScreenSize { get; }

            public Vector2 NormalizedPosition { get; }

            public static RuntimeScreenPosition FromScreenPosition(Vector2 screenPosition)
            {
                var screenSize = new Vector2(Mathf.Max(1, Screen.width), Mathf.Max(1, Screen.height));
                return new RuntimeScreenPosition(screenPosition, screenSize, new Vector2(screenPosition.x / screenSize.x, screenPosition.y / screenSize.y));
            }
        }
    }
}
