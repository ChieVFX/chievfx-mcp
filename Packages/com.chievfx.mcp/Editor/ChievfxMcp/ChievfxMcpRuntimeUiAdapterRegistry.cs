#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
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

    internal static class ChievfxMcpRuntimeUiAdapterRegistry
    {
        private const string ExtensionId = "chievfx.runtime-ui";
        private const string Category = "Runtime UI";
        private const string UriPrefix = "chievfx://extensions/chievfx.runtime-ui/";
        private const string StatusUri = UriPrefix + "status";
        private const string RuntimeProbeUri = UriPrefix + "runtime/probe-screen-position";
        private const string ProbeToolName = "runtime-ui-probe-screen-position";
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
                Category = Category,
            });
            descriptor.Resources.Add(new ChievfxMcpResourceDescriptor
            {
                Id = "runtime-ui-runtime-probe-screen-position",
                Uri = RuntimeProbeUri,
                Name = "Merged runtime UI screen-position probe",
                Description = "Read-only default-center merged runtime UI hit stack across registered adapters.",
                MimeType = "application/json",
                Category = Category,
            });
            descriptor.Tools.Add(new ChievfxMcpToolDescriptor
            {
                Name = ProbeToolName,
                Description = "Probe Play Mode runtime UI hit stack at screen position.",
                Category = Category,
                InputSchema = RuntimeProbeSchema(),
            });
            return descriptor;
        }

        private static object? RunTool(string toolName, JToken args)
        {
            return toolName switch
            {
                ProbeToolName => ProbeScreenPosition("tool://" + ProbeToolName, args),
                _ => throw new InvalidOperationException($"Unknown runtime UI registry tool '{toolName}'."),
            };
        }

        private static object? ReadResource(string uri)
        {
            if (string.Equals(uri, StatusUri, StringComparison.Ordinal))
            {
                return ReadStatus(uri);
            }

            if (string.Equals(uri, RuntimeProbeUri, StringComparison.Ordinal))
            {
                return ProbeScreenPosition(uri, new JObject());
            }

            return CreateUnavailable(uri, "Unsupported runtime UI registry resource URI.");
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
            var request = args is JObject obj ? obj : new JObject();
            var maxRows = Mathf.Clamp(ReadInt(request, "maxRows", DefaultMaxRows), 1, 1024);
            var warnings = new List<string>();
            var position = ReadScreenPosition(request, warnings);
            var adapters = SnapshotAdapters();
            var adapterResults = new List<Dictionary<string, object?>>();
            var mergedRows = new List<MergedHit>();

            foreach (var registered in adapters)
            {
                var adapter = registered.Adapter;
                var adapterResult = CreateAdapterStatusRow(registered);
                if (!adapter.Available)
                {
                    adapterResult["probed"] = false;
                    adapterResult["warnings"] = new[] { "Adapter is registered but unavailable." };
                    adapterResults.Add(adapterResult);
                    continue;
                }

                try
                {
                    var probe = adapter.ProbeScreenPosition(request.DeepClone());
                    adapterResult["probed"] = true;
                    AddProbeSummary(adapterResult, probe);
                    adapterResults.Add(adapterResult);

                    foreach (var row in ReadStackRows(probe))
                    {
                        var hitOrder = mergedRows.Count(candidate => string.Equals(candidate.FrameworkId, adapter.FrameworkId, StringComparison.Ordinal));
                        var sortable = CreateSortableHit(row, registered, hitOrder);
                        mergedRows.Add(sortable);
                    }
                }
                catch (Exception ex)
                {
                    adapterResult["probed"] = false;
                    adapterResult["warnings"] = new[] { "Adapter probe failed: " + RootMessage(ex) };
                    adapterResults.Add(adapterResult);
                }
            }

            var orderedRows = mergedRows
                .OrderByDescending(row => row.AdapterPriority)
                .ThenByDescending(row => row.SortingOrder)
                .ThenByDescending(row => row.DocumentDepth)
                .ThenBy(row => row.HitOrder)
                .ThenBy(row => row.RegistrationOrder)
                .ThenBy(row => row.FrameworkId, StringComparer.Ordinal)
                .Take(maxRows)
                .Select((row, index) => row.ToDictionary(index))
                .ToArray();

            return new Dictionary<string, object?>
            {
                ["uri"] = uri,
                ["readAtUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["extensionId"] = ExtensionId,
                ["input"] = CreateScreenPositionRow(position),
                ["coordinateConvention"] = CreateCoordinateInfo(position),
                ["adapterCount"] = adapters.Length,
                ["availableAdapterCount"] = adapters.Count(adapter => adapter.Adapter.Available),
                ["adapters"] = adapterResults.ToArray(),
                ["stack"] = orderedRows,
                ["count"] = orderedRows.Length,
                ["top"] = orderedRows.FirstOrDefault(),
                ["maxRows"] = maxRows,
                ["truncated"] = mergedRows.Count > orderedRows.Length,
                ["warnings"] = warnings.Distinct().ToArray(),
            };
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

        private static Dictionary<string, object?> CreateAdapterStatusRow(RegisteredAdapter registered)
        {
            var adapter = registered.Adapter;
            return new Dictionary<string, object?>
            {
                ["frameworkId"] = adapter.FrameworkId,
                ["frameworkName"] = adapter.FrameworkName,
                ["priority"] = adapter.Priority,
                ["available"] = adapter.Available,
                ["registrationOrder"] = registered.RegistrationOrder,
                ["resources"] = adapter.Resources?.ToArray() ?? Array.Empty<string>(),
                ["status"] = adapter.Status,
            };
        }

        private static void AddProbeSummary(Dictionary<string, object?> adapterResult, object? probe)
        {
            if (!TryReadDictionary(probe, out var dictionary))
            {
                adapterResult["count"] = 0;
                adapterResult["runtimeAvailable"] = null;
                adapterResult["warnings"] = Array.Empty<string>();
                return;
            }

            adapterResult["count"] = ReadObject(dictionary, "count");
            adapterResult["runtimeAvailable"] = ReadObject(dictionary, "runtimeAvailable");
            adapterResult["warnings"] = ReadObject(dictionary, "warnings") ?? Array.Empty<string>();
            adapterResult["top"] = ReadObject(dictionary, "top");
        }

        private static IEnumerable<Dictionary<string, object?>> ReadStackRows(object? probe)
        {
            if (!TryReadDictionary(probe, out var dictionary)
                || !dictionary.TryGetValue("stack", out var stack)
                || stack == null)
            {
                yield break;
            }

            foreach (var item in ReadEnumerable(stack))
            {
                if (TryReadDictionary(item, out var row))
                {
                    yield return row;
                }
            }
        }

        private static MergedHit CreateSortableHit(Dictionary<string, object?> source, RegisteredAdapter registered, int hitOrder)
        {
            var sortingOrder = ReadIntFromPaths(source, 0,
                "ordering.sortingOrder",
                "raycastResult.sortingOrder",
                "sorting.sortingOrder",
                "canvas.sorting.sortingOrder",
                "panelSettings.sortingOrder");
            var documentDepth = ReadIntFromPaths(source, 0,
                "ordering.documentDepth",
                "ordering.depth",
                "depth",
                "raycastResult.depth");
            return new MergedHit(
                registered.Adapter.FrameworkId,
                registered.Adapter.FrameworkName,
                registered.Adapter.Priority,
                registered.RegistrationOrder,
                sortingOrder,
                documentDepth,
                ReadIntFromPaths(source, hitOrder, "ordering.hitOrder", "stackIndex"),
                source);
        }

        private static int ReadIntFromPaths(Dictionary<string, object?> source, int defaultValue, params string[] paths)
        {
            foreach (var path in paths)
            {
                if (TryReadPath(source, path.Split('.'), out var value) && TryConvertInt(value, out var intValue))
                {
                    return intValue;
                }
            }

            return defaultValue;
        }

        private static bool TryReadPath(object? source, IReadOnlyList<string> segments, out object? value)
        {
            value = source;
            foreach (var segment in segments)
            {
                if (!TryReadDictionary(value, out var dictionary) || !dictionary.TryGetValue(segment, out value))
                {
                    value = null;
                    return false;
                }
            }

            return true;
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

        private static Dictionary<string, object?> CreateCoordinateInfo(RuntimeScreenPosition position)
        {
            return new Dictionary<string, object?>
            {
                ["origin"] = "bottom-left",
                ["screenSize"] = CreateVector2Row(position.ScreenSize),
                ["screenPosition"] = CreateVector2Row(position.ScreenPosition),
                ["normalizedPosition"] = CreateVector2Row(position.NormalizedPosition),
            };
        }

        private static Dictionary<string, object?> CreateScreenPositionRow(RuntimeScreenPosition position)
        {
            return new Dictionary<string, object?>
            {
                ["screenPosition"] = CreateVector2Row(position.ScreenPosition),
                ["normalizedPosition"] = CreateVector2Row(position.NormalizedPosition),
                ["origin"] = "bottom-left",
            };
        }

        private static Dictionary<string, object?> CreateVector2Row(Vector2 value)
        {
            return new Dictionary<string, object?>
            {
                ["x"] = value.x,
                ["y"] = value.y,
            };
        }

        private static Dictionary<string, object?> CreateUnavailable(string uri, string reason)
        {
            return new Dictionary<string, object?> { ["reason"] = reason };
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

        private readonly struct MergedHit
        {
            public MergedHit(
                string frameworkId,
                string frameworkName,
                int adapterPriority,
                int registrationOrder,
                int sortingOrder,
                int documentDepth,
                int hitOrder,
                Dictionary<string, object?> source)
            {
                FrameworkId = frameworkId;
                FrameworkName = frameworkName;
                AdapterPriority = adapterPriority;
                RegistrationOrder = registrationOrder;
                SortingOrder = sortingOrder;
                DocumentDepth = documentDepth;
                HitOrder = hitOrder;
                Source = source;
            }

            public string FrameworkId { get; }

            public string FrameworkName { get; }

            public int AdapterPriority { get; }

            public int RegistrationOrder { get; }

            public int SortingOrder { get; }

            public int DocumentDepth { get; }

            public int HitOrder { get; }

            private Dictionary<string, object?> Source { get; }

            public Dictionary<string, object?> ToDictionary(int mergedStackIndex)
            {
                var row = new Dictionary<string, object?>(Source, StringComparer.Ordinal)
                {
                    ["mergedStackIndex"] = mergedStackIndex,
                    ["frameworkId"] = FrameworkId,
                    ["frameworkName"] = FrameworkName,
                    ["adapterPriority"] = AdapterPriority,
                    ["ordering"] = new Dictionary<string, object?>
                    {
                        ["adapterPriority"] = AdapterPriority,
                        ["registrationOrder"] = RegistrationOrder,
                        ["sortingOrder"] = SortingOrder,
                        ["documentDepth"] = DocumentDepth,
                        ["hitOrder"] = HitOrder,
                    },
                };
                return row;
            }
        }
    }
}
