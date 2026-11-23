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

    /// <summary>
    /// Optional capability for clicking runtime UI at a screen position or explicit target.
    /// Enables the shared cross-framework "ui-runtime-click" tool.
    /// </summary>
    internal interface IChievfxMcpRuntimeUiClickAdapter
    {
        object? ClickAtPosition(JToken request);
    }

    /// <summary>
    /// Optional capability for dragging runtime UI at a screen position or explicit target.
    /// Enables the shared cross-framework "ui-runtime-drag" tool.
    /// </summary>
    internal interface IChievfxMcpRuntimeUiDragAdapter
    {
        object? DragAtPosition(JToken request);
    }

    /// <summary>
    /// Optional capability for setting runtime control values (slider, toggle, dropdown, etc.).
    /// Enables the shared cross-framework "ui-runtime-set-control-value" tool.
    /// </summary>
    internal interface IChievfxMcpRuntimeUiSetControlValueAdapter
    {
        object? SetControlValue(JToken request, bool requireTarget);
    }

    /// <summary>
    /// Optional capability for focusing or clearing focus on runtime UI controls.
    /// Enables shared "ui-runtime-focus" and "ui-runtime-clear-focus" tools.
    /// </summary>
    internal interface IChievfxMcpRuntimeUiFocusAdapter
    {
        object? Focus(JToken request, bool requireTarget);

        object? ClearFocus(JToken request);
    }

    internal static partial class ChievfxMcpRuntimeUiAdapterRegistry
    {
        private const string ExtensionId = "chievfx.runtime-ui";
        private const string CommonCategory = "ui-runtime-common";
        private const string EssentialsCategory = "essentials";
        private const string UriPrefix = "chievfx://extensions/chievfx.runtime-ui/";
        private const string StatusUri = UriPrefix + "status";
        private const string ProbeToolName = "ui-runtime-probe";
        private const string TypeTextToolName = "ui-runtime-type-text";
        internal const string ClickToolName = "ui-runtime-click";
        internal const string DragToolName = "ui-runtime-drag";
        internal const string SetControlValueToolName = "ui-runtime-set-control-value";
        internal const string FocusToolName = "ui-runtime-focus";
        internal const string ClearFocusToolName = "ui-runtime-clear-focus";
        private const int AdapterProbeMaxRows = 1024;

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
                Description = "Probe Play Mode runtime UI hit stack at a screen position. Bottom-left origin is (0,0); top-right is screen size in pixels or (1,1) when isNormalized is true. Returns up to 10 hits per page; pass page to fetch more. Requires Play Mode.",
                Category = CommonCategory,
                InputSchema = RuntimeProbeSchema(),
            });
            descriptor.Tools.Add(new ChievfxMcpToolDescriptor
            {
                Name = TypeTextToolName,
                Description = "Focus a Play Mode text field and type text into it like a player. Works for uGUI InputField/TMP_InputField and UI Toolkit TextField; auto-detects the framework or use framework to force one. Requires Play Mode.",
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

        internal static object? ControlFind(JToken args)
        {
            var request = args is JObject obj ? obj : new JObject();
            var framework = (request["framework"]?.Value<string>() ?? string.Empty).Trim().ToLowerInvariant();
            var warnings = new List<string>();
            MapControlFindFilterAliases(request, warnings);
            var wildcards = ChievfxMcpRuntimeUiControlFind.ParseWildcards(request, "wildcards");
            var controlTypeFilter = ChievfxMcpRuntimeUiControlFind.NormalizeControlTypeFilter(request["controlType"]?.Value<string>());
            var pageSize = ChievfxMcpRuntimeUiControlFind.DefaultPageSize;
            var controls = new List<Dictionary<string, object?>>();
            var totalMatches = 0;
            var sections = new List<Dictionary<string, object?>>();
            // texts:true - the screen's visible texts by window, from the frameworks that read them (uGUI).
            var texts = new List<object>();
            var textsTotal = 0;

            foreach (var registered in SnapshotAdapters())
            {
                if (!ChievfxMcpRuntimeUiControlFind.MatchesFrameworkFilter(framework, registered.Adapter.FrameworkId))
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
                if (section.TryGetValue("texts", out var sectionTexts) && sectionTexts is IEnumerable textWindows)
                {
                    foreach (var textWindow in textWindows)
                    {
                        if (textWindow != null)
                        {
                            texts.Add(textWindow);
                        }
                    }

                    textsTotal += section.TryGetValue("textsTotal", out var sectionTextsTotal) && sectionTextsTotal is int sectionTextsCount ? sectionTextsCount : 0;
                    section.Remove("texts");
                    section.Remove("textsTotal");
                }
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

            if (!ChievfxMcpRuntimeUiControlFind.IncludesAllFrameworks(framework)
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
            var screenSize = ChievfxMcpRuntimeScreenSize.Resolve();
            var selected = controls
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToArray();
            if (wildcards.Length > 0 && totalMatches == 0)
            {
                var containsHint = wildcards.All(pattern => pattern.IndexOfAny(new[] { '*', '?' }) < 0)
                    ? $" Patterns without * or ? must match the full control name or path exactly; try '*{wildcards[0]}*' for a contains search."
                    : string.Empty;
                warnings.Add($"No controls matched wildcards [{string.Join(", ", wildcards)}].{containsHint}");
            }
            else if (totalMatches == 0 && !string.IsNullOrWhiteSpace(controlTypeFilter))
            {
                warnings.Add($"No on-screen controls of type '{controlTypeFilter}'.");
            }

            var payload = new Dictionary<string, object?>
            {
                ["page"] = page,
                ["totalPages"] = totalPages,
                ["total"] = totalMatches,
                ["matched"] = wildcards.Length == 0 || totalMatches > 0,
                ["wildcards"] = wildcards.Length == 0 ? null : wildcards,
                ["controlTypeFilter"] = controlTypeFilter,
                ["normalizeCoords"] = normalizeCoords,
                ["screenSize"] = new Dictionary<string, object?>
                {
                    ["width"] = (int)screenSize.x,
                    ["height"] = (int)screenSize.y,
                },
                ["screenSizeSource"] = ChievfxMcpRuntimeScreenSize.DescribeResolvedSource(screenSize),
                ["frameworkFilter"] = string.IsNullOrEmpty(framework) ? null : framework,
                ["controls"] = selected,
                ["frameworks"] = sections.ToArray(),
                ["warnings"] = warnings.Where(warning => !string.IsNullOrWhiteSpace(warning)).Distinct().ToArray(),
            };
            // The texts are the screen's, not the page's: page 1 lists them, the next pages list only more controls.
            if (ReadBool(request, "texts", false) && page == 1)
            {
                payload["texts"] = texts.ToArray();
                payload["textsTotal"] = textsTotal;
            }
            else if (ReadBool(request, "texts", false))
            {
                payload["textsOnPage"] = 1;
            }

            var outputFormat = request["outputFormat"]?.Value<string>();
            if (string.Equals(outputFormat, "json", StringComparison.OrdinalIgnoreCase))
            {
                return payload;
            }

            return payload;
        }

        /// <summary>
        /// The only supported filter argument is 'wildcards'; a misnamed filter (query, name, ...)
        /// would silently apply no filter and return every control as if it matched. Map the common
        /// aliases onto wildcards (contains semantics) and say so, instead of lying.
        /// </summary>
        private static void MapControlFindFilterAliases(JObject request, List<string> warnings)
        {
            if (request["wildcards"] != null && request["wildcards"]!.Type != JTokenType.Null)
            {
                return;
            }

            foreach (var alias in new[] { "query", "name", "nameContains", "namePattern", "search", "pattern", "text" })
            {
                if (request[alias]?.Type != JTokenType.String)
                {
                    continue;
                }

                var value = request[alias]!.Value<string>()?.Trim();
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                var pattern = value!.IndexOfAny(new[] { '*', '?' }) >= 0 ? value! : "*" + value + "*";
                request["wildcards"] = pattern;
                warnings.Add($"'{alias}' is not a ui-control-find argument; interpreted it as wildcards='{pattern}'.");
                return;
            }
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
            foreach (var assembly in UnityAssemblies.GetLoaded())
            {
                var type = assembly.GetType(fullName, throwOnError: false);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
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
    }
}
