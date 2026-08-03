#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace Chievfx.Mcp.Editor
{
    internal static class ChievfxMcpRuntimeUiProbeCompact
    {
        internal const string ProbeRequiresPlayModeMessage =
            "Runtime UI probe requires Play Mode. Enter Play Mode before probing runtime UI hit stacks.";

        internal static void EnsurePlayModeForProbe(bool isPlayModeActive)
        {
            if (!isPlayModeActive)
            {
                throw new InvalidOperationException(ProbeRequiresPlayModeMessage);
            }
        }

        internal static Dictionary<string, object?> CreateProbeBlock(
            Vector2 screenSize,
            Vector2 screenPosition,
            Vector2 normalizedPosition)
        {
            var block = new Dictionary<string, object?>
            {
                ["origin"] = "bottom-left",
                ["screenSize"] = Vec(screenSize),
                ["normalized"] = Vec(normalizedPosition),
                ["screen"] = Vec(screenPosition),
            };
            var screenSizeSource = ChievfxMcpRuntimeScreenSize.DescribeResolvedSource(screenSize);
            if (screenSizeSource != null)
            {
                block["screenSizeSource"] = screenSizeSource;
            }

            return block;
        }

        internal static Dictionary<string, object?> CreateMergedProbeResult(
            Dictionary<string, object?> probe,
            bool runtimeAvailable,
            int page,
            int totalPages,
            int totalHits,
            bool truncated,
            IReadOnlyList<string> warnings,
            Dictionary<string, object?>? ugui = null,
            Dictionary<string, object?>? uitoolkit = null)
        {
            var result = new Dictionary<string, object?>
            {
                ["probe"] = probe,
                ["runtimeAvailable"] = runtimeAvailable,
                ["page"] = page,
                ["totalPages"] = totalPages,
                ["totalHits"] = totalHits,
                ["pageSize"] = ChievfxMcpRuntimeUiControlFind.DefaultPageSize,
                ["truncated"] = truncated,
                ["warnings"] = warnings.Distinct().ToArray(),
            };
            if (ugui != null)
            {
                result["ugui"] = ugui;
            }

            if (uitoolkit != null)
            {
                result["uitoolkit"] = uitoolkit;
            }

            return result;
        }

        internal static Dictionary<string, object?> PaginateProbeSection(
            Dictionary<string, object?> section,
            int page,
            int pageSize)
        {
            var hits = section.TryGetValue("hits", out var hitsValue)
                ? ReadHitArray(hitsValue)
                : Array.Empty<Dictionary<string, object?>>();
            var totalHits = hits.Length;
            var pageHits = hits
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToArray();
            for (var index = 0; index < pageHits.Length; index++)
            {
                pageHits[index]["i"] = (page - 1) * pageSize + index;
            }

            section["hits"] = pageHits;
            section["count"] = pageHits.Length;
            section["totalHits"] = totalHits;
            return section;
        }

        internal static Dictionary<string, object?> CreateProbeResult(
            Dictionary<string, object?> probe,
            bool runtimeAvailable,
            int maxRows,
            bool truncated,
            IReadOnlyList<string> warnings,
            Dictionary<string, object?>? ugui = null,
            Dictionary<string, object?>? uitoolkit = null)
        {
            var result = new Dictionary<string, object?>
            {
                ["probe"] = probe,
                ["runtimeAvailable"] = runtimeAvailable,
                ["maxRows"] = maxRows,
                ["truncated"] = truncated,
                ["warnings"] = warnings.Distinct().ToArray(),
            };
            if (ugui != null)
            {
                result["ugui"] = ugui;
            }

            if (uitoolkit != null)
            {
                result["uitoolkit"] = uitoolkit;
            }

            return result;
        }

        internal static Dictionary<string, object?> CreateUguiSection(
            bool available,
            bool probed,
            Dictionary<string, object?>[] hits,
            IReadOnlyList<string>? warnings = null,
            bool truncated = false)
        {
            var compactHits = RenumberHits(hits.Select(CompactUguiHit).ToArray());
            var section = new Dictionary<string, object?>
            {
                ["available"] = available,
                ["probed"] = probed,
                ["count"] = compactHits.Length,
                ["hits"] = compactHits,
            };
            if (truncated)
            {
                section["truncated"] = true;
            }

            if (warnings != null && warnings.Count > 0)
            {
                section["warnings"] = warnings.ToArray();
            }

            return section;
        }

        internal static Dictionary<string, object?> CreateUiToolkitSection(
            bool available,
            bool probed,
            Vector2 screenSize,
            Vector2 screenPosition,
            Dictionary<string, object?>[] hits,
            IReadOnlyList<string>? warnings = null,
            bool truncated = false)
        {
            var compactHits = RenumberHits(hits.Select(CompactUiToolkitHit).ToArray());
            var section = new Dictionary<string, object?>
            {
                ["available"] = available,
                ["probed"] = probed,
                ["yInverted"] = true,
                ["panelScreen"] = Vec(new Vector2(screenPosition.x, screenSize.y - screenPosition.y)),
                ["count"] = compactHits.Length,
                ["hits"] = compactHits,
            };
            if (truncated)
            {
                section["truncated"] = true;
            }

            if (warnings != null && warnings.Count > 0)
            {
                section["warnings"] = warnings.ToArray();
            }

            return section;
        }

        internal static Dictionary<string, object?>[] ReadHits(object? probeResult, string frameworkKey)
        {
            if (!TryReadDictionary(probeResult, out var dictionary))
            {
                return Array.Empty<Dictionary<string, object?>>();
            }

            if (dictionary.TryGetValue(frameworkKey, out var section)
                && TryReadDictionary(section, out var sectionRow)
                && sectionRow.TryGetValue("hits", out var hits))
            {
                return ReadHitArray(hits);
            }

            if (dictionary.TryGetValue("hits", out var directHits))
            {
                return ReadHitArray(directHits);
            }

            if (dictionary.TryGetValue("stack", out var legacyStack))
            {
                return ReadHitArray(legacyStack);
            }

            return Array.Empty<Dictionary<string, object?>>();
        }

        internal static bool ReadTruncated(object? probeResult, string frameworkKey)
        {
            if (!TryReadDictionary(probeResult, out var dictionary))
            {
                return false;
            }

            if (dictionary.TryGetValue(frameworkKey, out var section)
                && TryReadDictionary(section, out var sectionRow)
                && sectionRow.TryGetValue("truncated", out var truncated)
                && truncated is bool truncatedValue)
            {
                return truncatedValue;
            }

            return dictionary.TryGetValue("truncated", out var rootTruncated)
                && rootTruncated is bool rootTruncatedValue
                && rootTruncatedValue;
        }

        internal static string[] ReadSectionWarnings(object? probeResult, string frameworkKey)
        {
            if (!TryReadDictionary(probeResult, out var dictionary))
            {
                return Array.Empty<string>();
            }

            if (dictionary.TryGetValue(frameworkKey, out var section)
                && TryReadDictionary(section, out var sectionRow)
                && sectionRow.TryGetValue("warnings", out var warnings))
            {
                return ReadStringArray(warnings);
            }

            if (dictionary.TryGetValue("warnings", out var rootWarnings))
            {
                return ReadStringArray(rootWarnings);
            }

            return Array.Empty<string>();
        }

        internal static Dictionary<string, object?> CompactUguiHit(Dictionary<string, object?> source)
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            CopyString(row, source, "path");
            CopyTypeString(row, source);
            CopyString(row, source, "handlerPath");
            CopyValue(row, source, "instanceId");
            CopyValue(row, source, "interactable");
            CopyValue(row, source, "raycastTarget");
            CopyValue(row, source, "enabled");
            CopyValue(row, source, "disabledComponents");
            CopyValue(row, source, "handlers");

            if (source.TryGetValue("controls", out var controls) && controls != null)
            {
                row["controls"] = controls;
            }
            else if (source.TryGetValue("controlComponents", out var controlComponents) && controlComponents != null)
            {
                row["controls"] = controlComponents;
            }

            var sortingOrder = ReadInt(source, "sortingOrder", int.MinValue);
            if (sortingOrder != int.MinValue && sortingOrder != 0)
            {
                row["sortingOrder"] = sortingOrder;
            }

            return row;
        }

        internal static Dictionary<string, object?> CompactUiToolkitHit(Dictionary<string, object?> source)
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            CopyString(row, source, "path");
            CopyTypeString(row, source);
            CopyString(row, source, "text");
            CopyValue(row, source, "value");
            CopyValue(row, source, "focusable");
            CopyValue(row, source, "enabled");
            CopyString(row, source, "pickingMode");

            var bound = CompactBound(source);
            if (bound != null)
            {
                row["bound"] = bound;
            }

            var sortingOrder = ReadInt(source, "sortingOrder", int.MinValue);
            if (sortingOrder == int.MinValue && source.TryGetValue("ordering", out var ordering) && TryReadDictionary(ordering, out var orderingRow))
            {
                sortingOrder = ReadInt(orderingRow, "sortingOrder", int.MinValue);
            }

            if (sortingOrder != int.MinValue && sortingOrder != 0)
            {
                row["sortingOrder"] = sortingOrder;
            }

            return row;
        }

        private static Dictionary<string, object?>[] RenumberHits(Dictionary<string, object?>[] hits)
        {
            for (var index = 0; index < hits.Length; index++)
            {
                hits[index]["i"] = index;
            }

            return hits;
        }

        private static Dictionary<string, object?>[] ReadHitArray(object? hits)
        {
            if (hits == null)
            {
                return Array.Empty<Dictionary<string, object?>>();
            }

            return hits is IEnumerable enumerable && hits is not string
                ? enumerable.Cast<object?>()
                    .Select(item => TryReadDictionary(item, out var row) ? row : null)
                    .Where(row => row != null)
                    .Cast<Dictionary<string, object?>>()
                    .ToArray()
                : Array.Empty<Dictionary<string, object?>>();
        }

        internal static string[] ReadStringArray(object? value)
        {
            if (value == null)
            {
                return Array.Empty<string>();
            }

            return value is IEnumerable enumerable && value is not string
                ? enumerable.Cast<object?>()
                    .Select(item => Convert.ToString(item, CultureInfo.InvariantCulture))
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Cast<string>()
                    .ToArray()
                : Array.Empty<string>();
        }

        private static Dictionary<string, object?>? CompactBound(Dictionary<string, object?> source)
        {
            if (TryReadDictionary(source.TryGetValue("bound", out var bound) ? bound : null, out var existing))
            {
                return RoundRect(existing);
            }

            if (TryReadDictionary(source.TryGetValue("worldBound", out var worldBound) ? worldBound : null, out var worldBoundRow))
            {
                return RoundRect(worldBoundRow);
            }

            return null;
        }

        private static Dictionary<string, object?> RoundRect(Dictionary<string, object?> rect)
        {
            return new Dictionary<string, object?>
            {
                ["x"] = Round2(ReadFloat(rect, "x")),
                ["y"] = Round2(ReadFloat(rect, "y")),
                ["width"] = Round2(ReadFloat(rect, "width")),
                ["height"] = Round2(ReadFloat(rect, "height")),
            };
        }

        private static void CopyTypeString(Dictionary<string, object?> target, Dictionary<string, object?> source)
        {
            CopyString(target, source, "type");
            if (!target.ContainsKey("type"))
            {
                CopyString(target, source, "type", "typeName");
            }
        }

        private static void CopyString(
            Dictionary<string, object?> target,
            Dictionary<string, object?> source,
            string key,
            string? sourceKey = null)
        {
            var resolvedKey = sourceKey ?? key;
            if (!source.TryGetValue(resolvedKey, out var value) || value == null)
            {
                return;
            }

            var text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(text))
            {
                target[key] = text;
            }
        }

        private static void CopyValue(Dictionary<string, object?> target, Dictionary<string, object?> source, string key)
        {
            if (source.TryGetValue(key, out var value) && value != null)
            {
                target[key] = value;
            }
        }

        private static Dictionary<string, object?> Vec(Vector2 value)
        {
            return new Dictionary<string, object?>
            {
                ["x"] = Round2(value.x),
                ["y"] = Round2(value.y),
            };
        }

        private static double Round2(float value)
        {
            return Math.Round(value, 2, MidpointRounding.AwayFromZero);
        }

        private static float ReadFloat(Dictionary<string, object?> source, string key)
        {
            if (!source.TryGetValue(key, out var value) || value == null)
            {
                return 0f;
            }

            return value switch
            {
                float typed => typed,
                double typed => (float)typed,
                int typed => typed,
                long typed => typed,
                string typed when float.TryParse(typed, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => 0f,
            };
        }

        private static int ReadInt(Dictionary<string, object?> source, string key, int defaultValue)
        {
            return source.TryGetValue(key, out var value) && TryConvertInt(value, out var intValue)
                ? intValue
                : defaultValue;
        }

        private static bool TryReadDictionary(object? value, out Dictionary<string, object?> dictionary)
        {
            if (value is Dictionary<string, object?> typed)
            {
                dictionary = typed;
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
                case string typed when int.TryParse(typed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                    intValue = parsed;
                    return true;
                default:
                    intValue = 0;
                    return false;
            }
        }
    }
}
