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
    internal static partial class ChievfxMcpRuntimeUiAdapterRegistry
    {
        private static Dictionary<string, object?> ProbeScreenPosition(string uri, JToken args)
        {
            ChievfxMcpRuntimeUiProbeCompact.EnsurePlayModeForProbe(EditorApplication.isPlaying || Application.isPlaying);

            var request = args is JObject obj ? obj : new JObject();
            var pageSize = ChievfxMcpRuntimeUiControlFind.DefaultPageSize;
            var warnings = new List<string>();
            var position = ReadProbeScreenPosition(request, warnings);
            var probe = ChievfxMcpRuntimeUiProbeCompact.CreateProbeBlock(
                position.ScreenSize,
                position.ScreenPosition,
                position.NormalizedPosition);
            var adapterRequest = CreateAdapterProbeRequest(request, position);

            Dictionary<string, object?>? uguiSection = null;
            Dictionary<string, object?>? uitoolkitSection = null;
            var anyProbed = false;
            var truncated = false;
            var uguiTotalHits = 0;
            var uitoolkitTotalHits = 0;

            foreach (var registered in SnapshotAdapters())
            {
                var adapter = registered.Adapter;
                if (string.Equals(adapter.FrameworkId, "ugui", StringComparison.Ordinal))
                {
                    uguiSection = ProbeAdapterSection(adapter, adapterRequest, "ugui", position, ref anyProbed, ref truncated);
                    uguiTotalHits = ReadSectionTotalHits(uguiSection);
                    continue;
                }

                if (string.Equals(adapter.FrameworkId, "uitoolkit", StringComparison.Ordinal))
                {
                    uitoolkitSection = ProbeAdapterSection(adapter, adapterRequest, "uitoolkit", position, ref anyProbed, ref truncated);
                    uitoolkitTotalHits = ReadSectionTotalHits(uitoolkitSection);
                }
            }

            var totalHits = uguiTotalHits + uitoolkitTotalHits;
            var totalPages = Math.Max(
                1,
                Math.Max(
                    (int)Math.Ceiling(uguiTotalHits / (double)pageSize),
                    (int)Math.Ceiling(uitoolkitTotalHits / (double)pageSize)));
            var page = Math.Max(1, ReadInt(request, "page", 1));
            if (page > totalPages)
            {
                page = totalPages;
            }

            if (uguiSection != null)
            {
                ChievfxMcpRuntimeUiProbeCompact.PaginateProbeSection(uguiSection, page, pageSize);
            }

            if (uitoolkitSection != null)
            {
                ChievfxMcpRuntimeUiProbeCompact.PaginateProbeSection(uitoolkitSection, page, pageSize);
            }

            return ChievfxMcpRuntimeUiProbeCompact.CreateMergedProbeResult(
                probe,
                runtimeAvailable: anyProbed,
                page,
                totalPages,
                totalHits,
                truncated,
                warnings,
                uguiSection,
                uitoolkitSection);
        }

        // Adapters re-parse the raw request, and their readers understand only x/y + isNormalized — not
        // space:"screenshot", whose Y-flip lives here. So a screenshot-space click was flipped for the echo
        // and for the hit test, but reached the adapter unflipped: the right object was hit while the
        // PointerEventData carried a vertically mirrored position, and any handler reading pressPosition
        // (OnPointerClick, OnBeginDrag, OnDrag) acted on the mirror while the tool reported success.
        // Resolve coordinates once, here, and hand adapters plain absolute screen pixels — which is what
        // CreateAdapterProbeRequest has always done for the probe.
        internal static JToken CreateAdapterInteractionRequest(JObject request, RuntimeScreenPosition? resolvedPosition = null)
        {
            if (!ChievfxMcpRuntimeUiInteractionInput.HasScreenPositionInput(request))
            {
                return request.DeepClone();
            }

            var position = resolvedPosition ?? ReadScreenPosition(request, new List<string>());
            var adapterRequest = (JObject)request.DeepClone();
            adapterRequest.Remove("x");
            adapterRequest.Remove("y");
            adapterRequest.Remove("isNormalized");
            adapterRequest.Remove("normalized");
            adapterRequest.Remove("space");
            RemoveScreenshotRegionKeys(adapterRequest);
            adapterRequest["screenPosition"] = new JObject
            {
                ["x"] = position.ScreenPosition.x,
                ["y"] = position.ScreenPosition.y,
            };
            return adapterRequest;
        }

        // Same contract for drag: both first-party adapters happen to re-read the geometry through this
        // class, so they are correct today, but nothing about the interface says they must — hand them
        // resolved pixels rather than a coordinate space they have to interpret.
        internal static JToken CreateAdapterDragRequest(JObject request, RuntimeDragScreenGeometry geometry)
        {
            var adapterRequest = (JObject)request.DeepClone();
            foreach (var key in new[]
                     {
                         "isNormalized", "space", "normalized", "delta", "deltaX", "deltaY",
                         "startNormalized", "endNormalized", "startScreenPosition", "endScreenPosition",
                     })
            {
                adapterRequest.Remove(key);
            }

            RemoveScreenshotRegionKeys(adapterRequest);
            adapterRequest["x"] = geometry.Start.ScreenPosition.x;
            adapterRequest["y"] = geometry.Start.ScreenPosition.y;
            adapterRequest["toX"] = geometry.End.ScreenPosition.x;
            adapterRequest["toY"] = geometry.End.ScreenPosition.y;
            return adapterRequest;
        }

        private static JObject CreateAdapterProbeRequest(JObject request, RuntimeScreenPosition position)
        {
            var adapterRequest = (JObject)request.DeepClone();
            adapterRequest.Remove("x");
            adapterRequest.Remove("y");
            adapterRequest.Remove("isNormalized");
            adapterRequest.Remove("page");
            adapterRequest.Remove("normalized");
            adapterRequest.Remove("maxRows");
            adapterRequest.Remove("space");
            RemoveScreenshotRegionKeys(adapterRequest);
            adapterRequest["screenPosition"] = new JObject
            {
                ["x"] = position.ScreenPosition.x,
                ["y"] = position.ScreenPosition.y,
            };
            adapterRequest["maxRows"] = AdapterProbeMaxRows;
            return adapterRequest;
        }

        private static int ReadSectionTotalHits(Dictionary<string, object?> section)
        {
            if (section.TryGetValue("hits", out var hitsValue))
            {
                return ChievfxMcpRuntimeUiProbeCompact.ReadHits(new Dictionary<string, object?> { ["hits"] = hitsValue }, string.Empty).Length;
            }

            return 0;
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

        private static RuntimeScreenPosition ReadProbeScreenPosition(JToken args, List<string> warnings)
        {
            var screenSize = ChievfxMcpRuntimeScreenSize.Resolve();
            if (IsScreenshotSpace(args))
            {
                return ReadScreenshotSpacePosition(args, screenSize, warnings);
            }

            var isNormalized = ReadBool(args, "isNormalized", false);
            float x;
            float y;

            if (args["x"] != null || args["y"] != null)
            {
                x = ReadFloat(args, "x", 0f);
                y = ReadFloat(args, "y", 0f);
            }
            else if (TryReadVector2(args["screenPosition"], out var screenPosition))
            {
                x = screenPosition.x;
                y = screenPosition.y;
            }
            else if (TryReadVector2(args["normalized"], out var legacyNormalized))
            {
                isNormalized = true;
                x = legacyNormalized.x;
                y = legacyNormalized.y;
            }
            else
            {
                throw new ArgumentException("ui-runtime-probe requires x and y coordinates.");
            }

            if (isNormalized)
            {
                var normalized = new Vector2(x, y);
                return new RuntimeScreenPosition(
                    new Vector2(normalized.x * screenSize.x, normalized.y * screenSize.y),
                    screenSize,
                    normalized);
            }

            return RuntimeScreenPosition.FromScreenPosition(new Vector2(x, y));
        }

        // Screenshot space: x/y are normalized 0..1 against the screenshot-game-view PNG (TOP-LEFT origin).
        // If that PNG was a crop (left/top/right/bottom), the same four values map the position back onto
        // the full view first. Then flip Y to reach the bottom-left Screen space the runtime uses. Only
        // valid when that screenshot did not report pixelMappingReliable:false (aspects match); the caller
        // is trusted on that.
        private static bool IsScreenshotSpace(JToken args) =>
            string.Equals((args["space"]?.Value<string>() ?? string.Empty).Trim(), "screenshot", StringComparison.OrdinalIgnoreCase);

        // The crop is deliberately NOT inherited from the last screenshot: several agents can share one
        // editor, and a click that silently lands elsewhere is worse than one that asks for the four
        // values. The last tool crop is consulted only to warn when the two visibly disagree.
        private static ScreenshotRegion ReadScreenshotSpaceRegion(JToken args, List<string> warnings)
        {
            var regionSupplied = ScreenshotRegion.HasAnyArgument(args);
            var region = ScreenshotRegion.Read(args);
            var lastCapture = ScreenshotBridgeService.LastGameViewToolRegion;
            if (!lastCapture.HasValue)
            {
                return region;
            }

            if (!regionSupplied && !lastCapture.Value.IsFull)
            {
                warnings.Add(
                    $"space:\"screenshot\" was mapped against the full view, but the last screenshot-game-view was cropped to {lastCapture.Value}. If this position was read off that cropped PNG, pass the same left/top/right/bottom.");
            }
            else if (regionSupplied && !lastCapture.Value.Equals(region))
            {
                warnings.Add(
                    $"left/top/right/bottom ({region}) differ from the last screenshot-game-view crop ({lastCapture.Value}); make sure they describe the PNG this position was read from.");
            }

            return region;
        }

        private static RuntimeScreenPosition ReadScreenshotSpacePosition(JToken args, Vector2 screenSize, List<string> warnings)
        {
            if (args["x"] == null || args["y"] == null)
            {
                throw new ArgumentException("space:\"screenshot\" requires normalized x and y (px/pngWidth, py/pngHeight) read off the screenshot PNG.");
            }

            var x = ReadFloat(args, "x", 0f);
            var y = ReadFloat(args, "y", 0f);
            if (x < -0.001f || x > 1.001f || y < -0.001f || y > 1.001f)
            {
                throw new ArgumentException("space:\"screenshot\" x/y must be normalized 0..1 — divide the pixel you read by pngWidth/pngHeight.");
            }

            var region = ReadScreenshotSpaceRegion(args, warnings);
            var full = region.ToFullViewNormalized(x, y);
            var normalized = new Vector2(full.x, 1f - full.y);
            return new RuntimeScreenPosition(new Vector2(normalized.x * screenSize.x, normalized.y * screenSize.y), screenSize, normalized);
        }

        private static void RemoveScreenshotRegionKeys(JObject request)
        {
            foreach (var key in ScreenshotRegion.ArgumentKeys)
            {
                request.Remove(key);
            }
        }

        private static RuntimeScreenPosition ReadScreenPosition(JToken args, List<string> warnings)
        {
            var screenSize = ChievfxMcpRuntimeScreenSize.Resolve();
            if (IsScreenshotSpace(args))
            {
                return ReadScreenshotSpacePosition(args, screenSize, warnings);
            }

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
                var isNormalized = ReadBool(args, "isNormalized", false);
                var x = ReadFloat(args, "x", 0f);
                var y = ReadFloat(args, "y", 0f);
                if (isNormalized)
                {
                    var normalizedPosition = new Vector2(x, y);
                    return new RuntimeScreenPosition(
                        new Vector2(normalizedPosition.x * screenSize.x, normalizedPosition.y * screenSize.y),
                        screenSize,
                        normalizedPosition);
                }

                return RuntimeScreenPosition.FromScreenPosition(new Vector2(x, y));
            }

            throw new ArgumentException(
                "Runtime UI interaction requires x/y screen coordinates when path, instanceId, visualElementRef, or name is not supplied.");
        }

        private static JObject RuntimeProbeSchema()
        {
            return new JObject
            {
                ["type"] = "object",
                ["required"] = new JArray("x", "y"),
                ["properties"] = new JObject
                {
                    ["x"] = NumberSchema("Screen X. Bottom-left origin is 0; top-right is screen width in pixels, or 1 when isNormalized is true."),
                    ["y"] = NumberSchema("Screen Y. Bottom-left origin is 0; top-right is screen height in pixels, or 1 when isNormalized is true."),
                    ["isNormalized"] = new JObject
                    {
                        ["type"] = "boolean",
                        ["default"] = false,
                        ["description"] = "When true, x/y are normalized 0..1 from bottom-left (0,0) to top-right (1,1). When false (default), x/y are pixels.",
                    },
                    ["space"] = new JObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JArray("screen", "screenshot"),
                        ["default"] = "screen",
                        ["description"] = "screen (default): x/y in Screen space (see isNormalized). screenshot: x/y are 0..1 from the screenshot-game-view PNG (top-left origin), auto Y-flipped; if that PNG was cropped, also pass its left/top/right/bottom. Use only if that screenshot did not report pixelMappingReliable:false.",
                    },
                    ["left"] = NumberSchema("With space:\"screenshot\" only: the left/top/right/bottom crop (0..1, top-left origin) the screenshot-game-view PNG was taken with. Omit for an uncropped screenshot."),
                    ["top"] = NumberSchema("See left."),
                    ["right"] = NumberSchema("See left."),
                    ["bottom"] = NumberSchema("See left."),
                    ["page"] = new JObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = 1,
                        ["default"] = 1,
                        ["description"] = "1-based page index. Each page returns up to 10 hits per framework section.",
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
                    ["framework"] = new JObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JArray("auto", "ugui", "uitoolkit"),
                        ["description"] = "Target framework. auto (default) resolves the field across registered frameworks; ugui/uitoolkit force one.",
                    },
                    ["x"] = NumberSchema("Screen X. Bottom-left origin is 0; top-right is screen width in pixels, or 1 when isNormalized is true."),
                    ["y"] = NumberSchema("Screen Y. Bottom-left origin is 0; top-right is screen height in pixels, or 1 when isNormalized is true."),
                    ["isNormalized"] = new JObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "When true, x and y are normalized bottom-left coordinates where 1 is screen width/height.",
                    },
                    ["path"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "uGUI GameObject transform path, UI Toolkit VisualElement path, or visualElementRef (ve:...) from runtime reads/probes.",
                    },
                    ["instanceId"] = new JObject { ["type"] = "integer", ["description"] = "uGUI target GameObject instance id." },
                    ["text"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Text to type into the field. Use append:true with an empty string to focus without replacing existing text.",
                    },
                    ["append"] = new JObject { ["type"] = "boolean", ["description"] = "Append to the current text instead of replacing it. Defaults false." },
                    ["submit"] = new JObject { ["type"] = "boolean", ["description"] = "After typing, submit/end edit (uGUI onEndEdit, UI Toolkit NavigationSubmit + blur). Defaults false." },
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

        private static string? ReadString(JToken token, string key)
        {
            var value = token[key]?.Value<string>();
            return string.IsNullOrWhiteSpace(value) ? null : value!.Trim();
        }

        private static string RootMessage(Exception ex)
        {
            return ex is System.Reflection.TargetInvocationException { InnerException: { } inner }
                ? RootMessage(inner)
                : ex.Message;
        }

        internal readonly struct RuntimeScreenPosition
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
                var screenSize = ChievfxMcpRuntimeScreenSize.Resolve();
                return new RuntimeScreenPosition(screenPosition, screenSize, new Vector2(screenPosition.x / screenSize.x, screenPosition.y / screenSize.y));
            }
        }
    }
}
