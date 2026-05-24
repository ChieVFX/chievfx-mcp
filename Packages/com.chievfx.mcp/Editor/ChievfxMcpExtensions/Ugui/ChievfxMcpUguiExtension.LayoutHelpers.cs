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
using static Chievfx.Mcp.Extensions.Ugui.UguiDesignTools;
using static Chievfx.Mcp.Extensions.Ugui.UguiElementHelpers;
using static Chievfx.Mcp.Extensions.Ugui.UguiResourcesAndRows;
using static Chievfx.Mcp.Extensions.Ugui.UguiRuntimeControlHelpers;
using static Chievfx.Mcp.Extensions.Ugui.UguiRuntimeHelpers;
using static Chievfx.Mcp.Extensions.Ugui.UguiRuntimeTools;
using static Chievfx.Mcp.Extensions.Ugui.UguiSchemas;
using static Chievfx.Mcp.Extensions.Ugui.UguiSharedHelpers;

namespace Chievfx.Mcp.Extensions.Ugui
{
    internal static class UguiLayoutHelpers
    {
        internal static RectArgs ReadRectArgs(JToken args)
        {
            var rectToken = args["rect"] is JObject rectObj ? rectObj : args;
            var preset = ReadString(rectToken, "preset") ?? ReadString(args, "rectPreset") ?? "center";
            return new RectArgs(
                preset,
                ReadString(rectToken, "dock"),
                ReadVector2(rectToken, "size", ReadVector2(rectToken, "sizeDelta", new Vector2(160f, 40f))),
                ReadVector2(rectToken, "position", ReadVector2(rectToken, "anchoredPosition", Vector2.zero)),
                ReadFloat(rectToken, "margin", 0f),
                ReadVector2(rectToken, "anchorMin", Vector2.zero),
                ReadVector2(rectToken, "anchorMax", Vector2.one),
                ReadVector2(rectToken, "pivot", new Vector2(0.5f, 0.5f)),
                ReadOptionalVector2(rectToken, "anchoredPosition"),
                ReadOptionalVector2(rectToken, "sizeDelta"),
                ReadOptionalVector2(rectToken, "offsetMin"),
                ReadOptionalVector2(rectToken, "offsetMax"),
                rectToken["anchorMin"] != null || rectToken["anchorMax"] != null);
        }

        internal static void ApplyRect(RectTransform rect, RectArgs args, List<string> warnings)
        {
            var preset = (args.Preset ?? "center").Trim().ToLowerInvariant();
            if (preset == "dock" && !string.IsNullOrWhiteSpace(args.Dock))
            {
                preset = "dock-" + args.Dock!.Trim().ToLowerInvariant();
            }

            switch (preset)
            {
                case "fill":
                case "stretch":
                    rect.anchorMin = Vector2.zero;
                    rect.anchorMax = Vector2.one;
                    rect.pivot = new Vector2(0.5f, 0.5f);
                    rect.offsetMin = new Vector2(args.Margin, args.Margin);
                    rect.offsetMax = new Vector2(-args.Margin, -args.Margin);
                    break;
                case "center":
                    rect.anchorMin = new Vector2(0.5f, 0.5f);
                    rect.anchorMax = new Vector2(0.5f, 0.5f);
                    rect.pivot = args.Pivot;
                    rect.sizeDelta = args.Size;
                    rect.anchoredPosition = args.Position;
                    break;
                case "anchor-size":
                    rect.anchorMin = args.AnchorMin;
                    rect.anchorMax = args.AnchorMax;
                    rect.pivot = args.Pivot;
                    rect.sizeDelta = args.Size;
                    rect.anchoredPosition = args.Position;
                    break;
                case "dock-top":
                    Dock(rect, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(-args.Margin, -args.Size.y - args.Margin), new Vector2(args.Margin, -args.Margin));
                    break;
                case "dock-bottom":
                    Dock(rect, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), new Vector2(args.Margin, args.Margin), new Vector2(-args.Margin, args.Size.y + args.Margin));
                    break;
                case "dock-left":
                    Dock(rect, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f), new Vector2(args.Margin, args.Margin), new Vector2(args.Size.x + args.Margin, -args.Margin));
                    break;
                case "dock-right":
                    Dock(rect, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(1f, 0.5f), new Vector2(-args.Size.x - args.Margin, args.Margin), new Vector2(-args.Margin, -args.Margin));
                    break;
                default:
                    warnings.Add($"Unknown rect preset '{args.Preset}'; used center.");
                    ApplyRect(rect, args.WithPreset("center"), warnings);
                    break;
            }

            ApplyRawRectOverrides(rect, args, warnings);
        }

        internal static void ApplyRawRectOverrides(RectTransform rect, RectArgs args, List<string> warnings)
        {
            if (args.UsesRawAnchors)
            {
                rect.anchorMin = args.AnchorMin;
                rect.anchorMax = args.AnchorMax;
            }

            if (args.SizeDelta.HasValue)
            {
                rect.sizeDelta = args.SizeDelta.Value;
            }

            if (args.AnchoredPosition.HasValue)
            {
                rect.anchoredPosition = args.AnchoredPosition.Value;
            }

            if (args.OffsetMin.HasValue)
            {
                rect.offsetMin = args.OffsetMin.Value;
            }

            if (args.OffsetMax.HasValue)
            {
                rect.offsetMax = args.OffsetMax.Value;
            }
        }

        internal static void Dock(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 offsetMin, Vector2 offsetMax)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
        }

        internal static LayoutGroup? GetParentLayoutGroup(RectTransform rect)
        {
            var layoutElement = rect.GetComponent<LayoutElement>();
            if (layoutElement != null && layoutElement.ignoreLayout)
            {
                return null;
            }

            return rect.parent == null ? null : rect.parent.GetComponent<LayoutGroup>();
        }

        internal static Dictionary<string, object?> CreateLayoutDrivenRow(GameObject target, LayoutGroup layoutParent)
        {
            return new Dictionary<string, object?>
            {
                ["path"] = GetTransformPath(target.transform),
                ["instanceId"] = UnityObjectIdentity.GetLegacyInstanceId(target),
                ["parentPath"] = GetTransformPath(layoutParent.transform),
                ["parentInstanceId"] = UnityObjectIdentity.GetLegacyInstanceId(layoutParent.gameObject),
                ["layoutGroupType"] = layoutParent.GetType().Name,
            };
        }

        internal static LayoutGroup EnsureLayoutGroup(GameObject target, string layoutType)
        {
            switch (layoutType)
            {
                case "vertical":
                case "verticallayoutgroup":
                {
                    RemoveOtherLayoutGroups<VerticalLayoutGroup>(target);
                    return target.GetComponent<VerticalLayoutGroup>() ?? target.AddComponent<VerticalLayoutGroup>();
                }
                case "horizontal":
                case "horizontallayoutgroup":
                {
                    RemoveOtherLayoutGroups<HorizontalLayoutGroup>(target);
                    return target.GetComponent<HorizontalLayoutGroup>() ?? target.AddComponent<HorizontalLayoutGroup>();
                }
                case "grid":
                case "gridlayoutgroup":
                {
                    RemoveOtherLayoutGroups<GridLayoutGroup>(target);
                    return target.GetComponent<GridLayoutGroup>() ?? target.AddComponent<GridLayoutGroup>();
                }
                default:
                    throw new ArgumentException($"Unsupported layoutGroup '{layoutType}'. Use vertical, horizontal, or grid.");
            }
        }

        internal static void ConfigureScrollContentRect(RectTransform rect, string direction)
        {
            switch (direction)
            {
                case "horizontal":
                    rect.anchorMin = new Vector2(0f, 0f);
                    rect.anchorMax = new Vector2(0f, 1f);
                    rect.pivot = new Vector2(0f, 0.5f);
                    rect.offsetMin = Vector2.zero;
                    rect.offsetMax = Vector2.zero;
                    break;
                case "both":
                    rect.anchorMin = new Vector2(0f, 1f);
                    rect.anchorMax = new Vector2(0f, 1f);
                    rect.pivot = new Vector2(0f, 1f);
                    rect.anchoredPosition = Vector2.zero;
                    rect.sizeDelta = Vector2.zero;
                    break;
                default:
                    rect.anchorMin = new Vector2(0f, 1f);
                    rect.anchorMax = new Vector2(1f, 1f);
                    rect.pivot = new Vector2(0.5f, 1f);
                    rect.offsetMin = Vector2.zero;
                    rect.offsetMax = Vector2.zero;
                    break;
            }
        }

        internal static void ConfigureContentLayout(GameObject content, string contentLayout, JToken args, List<string> warnings)
        {
            if (contentLayout == "none")
            {
                return;
            }

            var group = EnsureLayoutGroup(content, contentLayout);
            ApplyLayoutGroup(group, args, warnings);
            if (group is HorizontalOrVerticalLayoutGroup axisGroup)
            {
                axisGroup.childControlWidth = ReadBool(args, "childControlWidth", axisGroup is VerticalLayoutGroup);
                axisGroup.childControlHeight = ReadBool(args, "childControlHeight", axisGroup is HorizontalLayoutGroup);
                axisGroup.childForceExpandWidth = ReadBool(args, "childForceExpandWidth", axisGroup is VerticalLayoutGroup);
                axisGroup.childForceExpandHeight = ReadBool(args, "childForceExpandHeight", false);
            }
        }

        internal static void ConfigureContentSizeFitter(GameObject content, string contentLayout, string direction, bool enabled)
        {
            if (!enabled || contentLayout == "none")
            {
                return;
            }

            var fitter = content.GetComponent<ContentSizeFitter>() ?? content.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = direction == "horizontal" || direction == "both"
                ? ContentSizeFitter.FitMode.PreferredSize
                : ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = direction == "vertical" || direction == "both"
                ? ContentSizeFitter.FitMode.PreferredSize
                : ContentSizeFitter.FitMode.Unconstrained;
        }

        internal static GameObject CreateGridCell(string cellType, UguiDependencyStatus status, List<string> warnings)
        {
            switch (cellType)
            {
                case "empty":
                    return new GameObject("Cell", typeof(RectTransform));
                case "button":
                case "text":
                case "image":
                    return CreateElementObject(cellType, status, warnings, cellType == "text" ? ResolveTextBackend("auto", status.TmpConfigured, warnings) : "legacy");
                default:
                    throw new ArgumentException($"Unsupported cellType '{cellType}'. Use empty, image, button, or text.");
            }
        }

        internal static void ApplyCellColor(GameObject cell, int index, int count, JToken args)
        {
            var color = ReadCellColor(index, count, args);
            foreach (var image in cell.GetComponentsInChildren<Image>(true))
            {
                image.color = color;
            }
        }

        internal static Color ReadCellColor(int index, int count, JToken args)
        {
            if (args["colors"] is JArray colors && colors.Count > 0)
            {
                return ReadColor(colors[index % colors.Count], Color.white);
            }

            if (args["color"] != null)
            {
                return ReadColor(args["color"]!, Color.white);
            }

            return Color.HSVToRGB(count <= 1 ? 0f : (float)index / count, 0.55f, 0.95f);
        }

        internal static void RemoveOtherLayoutGroups<TKeep>(GameObject target)
            where TKeep : LayoutGroup
        {
            foreach (var group in target.GetComponents<LayoutGroup>())
            {
                if (group is TKeep)
                {
                    continue;
                }

                Undo.DestroyObjectImmediate(group);
            }
        }

        internal static void ApplyLayoutGroup(LayoutGroup group, JToken args, List<string> warnings)
        {
            if (args["padding"] is JObject padding)
            {
                group.padding = new RectOffset(
                    ReadInt(padding, "left", group.padding.left),
                    ReadInt(padding, "right", group.padding.right),
                    ReadInt(padding, "top", group.padding.top),
                    ReadInt(padding, "bottom", group.padding.bottom));
            }

            var alignment = ReadString(args, "childAlignment") ?? ReadString(args, "alignment");
            if (!string.IsNullOrWhiteSpace(alignment))
            {
                group.childAlignment = ParseTextAnchor(alignment!);
            }

            if (group is HorizontalOrVerticalLayoutGroup axisGroup)
            {
                ApplyHorizontalOrVerticalLayoutGroup(axisGroup, args);
                return;
            }

            if (group is GridLayoutGroup gridGroup)
            {
                ApplyGridLayoutGroup(gridGroup, args, warnings);
            }
        }

        internal static void ApplyHorizontalOrVerticalLayoutGroup(HorizontalOrVerticalLayoutGroup group, JToken args)
        {
            if (args["spacing"] != null)
            {
                group.spacing = ReadFloat(args, "spacing", group.spacing);
            }

            if (args["childControlWidth"] != null)
            {
                group.childControlWidth = ReadBool(args, "childControlWidth", group.childControlWidth);
            }

            if (args["childControlHeight"] != null)
            {
                group.childControlHeight = ReadBool(args, "childControlHeight", group.childControlHeight);
            }

            if (args["childForceExpandWidth"] != null)
            {
                group.childForceExpandWidth = ReadBool(args, "childForceExpandWidth", group.childForceExpandWidth);
            }

            if (args["childForceExpandHeight"] != null)
            {
                group.childForceExpandHeight = ReadBool(args, "childForceExpandHeight", group.childForceExpandHeight);
            }

            if (args["childScaleWidth"] != null)
            {
                group.childScaleWidth = ReadBool(args, "childScaleWidth", group.childScaleWidth);
            }

            if (args["childScaleHeight"] != null)
            {
                group.childScaleHeight = ReadBool(args, "childScaleHeight", group.childScaleHeight);
            }

            if (args["reverseArrangement"] != null)
            {
                group.reverseArrangement = ReadBool(args, "reverseArrangement", group.reverseArrangement);
            }
        }

        internal static void ApplyGridLayoutGroup(GridLayoutGroup group, JToken args, List<string> warnings)
        {
            group.cellSize = ReadVector2(args, "cellSize", group.cellSize);
            group.spacing = ReadVector2(args, "gridSpacing", ReadVector2(args, "spacing", group.spacing));

            var startCorner = ReadString(args, "startCorner");
            if (!string.IsNullOrWhiteSpace(startCorner))
            {
                group.startCorner = ParseEnum<GridLayoutGroup.Corner>(startCorner!);
            }

            var startAxis = ReadString(args, "startAxis");
            if (!string.IsNullOrWhiteSpace(startAxis))
            {
                group.startAxis = ParseEnum<GridLayoutGroup.Axis>(startAxis!);
            }

            var constraint = ReadString(args, "constraint");
            if (!string.IsNullOrWhiteSpace(constraint))
            {
                group.constraint = ParseEnum<GridLayoutGroup.Constraint>(constraint!);
            }

            if (args["constraintCount"] != null)
            {
                if (string.IsNullOrWhiteSpace(constraint))
                {
                    group.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
                }

                group.constraintCount = Math.Max(1, ReadInt(args, "constraintCount", group.constraintCount));
            }

            if (args["spacing"] != null && args["spacing"]!.Type != JTokenType.Object)
            {
                warnings.Add("GridLayoutGroup spacing uses vector object; numeric spacing ignored. Use gridSpacing or spacing:{x,y}.");
            }
        }

        internal static void ApplyLayoutElement(LayoutElement element, JToken args)
        {
            if (args["ignoreLayout"] != null)
            {
                element.ignoreLayout = ReadBool(args, "ignoreLayout", element.ignoreLayout);
            }

            if (args["minWidth"] != null)
            {
                element.minWidth = ReadFloat(args, "minWidth", element.minWidth);
            }

            if (args["minHeight"] != null)
            {
                element.minHeight = ReadFloat(args, "minHeight", element.minHeight);
            }

            if (args["preferredWidth"] != null)
            {
                element.preferredWidth = ReadFloat(args, "preferredWidth", element.preferredWidth);
            }

            if (args["preferredHeight"] != null)
            {
                element.preferredHeight = ReadFloat(args, "preferredHeight", element.preferredHeight);
            }

            if (args["flexibleWidth"] != null)
            {
                element.flexibleWidth = ReadFloat(args, "flexibleWidth", element.flexibleWidth);
            }

            if (args["flexibleHeight"] != null)
            {
                element.flexibleHeight = ReadFloat(args, "flexibleHeight", element.flexibleHeight);
            }

            if (args["layoutPriority"] != null)
            {
                element.layoutPriority = ReadInt(args, "layoutPriority", element.layoutPriority);
            }
        }

        internal static Dictionary<string, object?> CreateLayoutGroupRow(LayoutGroup group)
        {
            var row = new Dictionary<string, object?>
            {
                ["path"] = GetTransformPath(group.transform),
                ["instanceId"] = UnityObjectIdentity.GetLegacyInstanceId(group.gameObject),
                ["layoutGroupType"] = group.GetType().Name,
                ["padding"] = RectOffsetRow(group.padding),
                ["childAlignment"] = group.childAlignment.ToString(),
            };

            if (group is HorizontalOrVerticalLayoutGroup axisGroup)
            {
                row["spacing"] = axisGroup.spacing;
                row["childControlWidth"] = axisGroup.childControlWidth;
                row["childControlHeight"] = axisGroup.childControlHeight;
                row["childForceExpandWidth"] = axisGroup.childForceExpandWidth;
                row["childForceExpandHeight"] = axisGroup.childForceExpandHeight;
                row["childScaleWidth"] = axisGroup.childScaleWidth;
                row["childScaleHeight"] = axisGroup.childScaleHeight;
                row["reverseArrangement"] = axisGroup.reverseArrangement;
            }
            else if (group is GridLayoutGroup gridGroup)
            {
                row["cellSize"] = Vector2Row(gridGroup.cellSize);
                row["spacing"] = Vector2Row(gridGroup.spacing);
                row["startCorner"] = gridGroup.startCorner.ToString();
                row["startAxis"] = gridGroup.startAxis.ToString();
                row["constraint"] = gridGroup.constraint.ToString();
                row["constraintCount"] = gridGroup.constraintCount;
            }

            return row;
        }

        internal static Dictionary<string, object?> CreateContentLayoutSummary(GameObject content)
        {
            var group = content.GetComponent<LayoutGroup>();
            var fitter = content.GetComponent<ContentSizeFitter>();
            return new Dictionary<string, object?>
            {
                ["type"] = group == null ? "none" : group.GetType().Name,
                ["contentSizeFitter"] = fitter == null
                    ? null
                    : new Dictionary<string, object?>
                    {
                        ["horizontalFit"] = fitter.horizontalFit.ToString(),
                        ["verticalFit"] = fitter.verticalFit.ToString(),
                    },
            };
        }

        internal static Dictionary<string, object?> CreateGridLayoutSummary(GridLayoutGroup group)
        {
            return new Dictionary<string, object?>
            {
                ["type"] = "GridLayoutGroup",
                ["cellSize"] = Vector2Row(group.cellSize),
                ["spacing"] = Vector2Row(group.spacing),
                ["constraint"] = group.constraint.ToString(),
                ["constraintCount"] = group.constraintCount,
                ["padding"] = RectOffsetRow(group.padding),
            };
        }

        internal static Dictionary<string, object?> CreateLayoutElementRow(LayoutElement element)
        {
            return new Dictionary<string, object?>
            {
                ["path"] = GetTransformPath(element.transform),
                ["instanceId"] = UnityObjectIdentity.GetLegacyInstanceId(element.gameObject),
                ["ignoreLayout"] = element.ignoreLayout,
                ["minWidth"] = element.minWidth,
                ["minHeight"] = element.minHeight,
                ["preferredWidth"] = element.preferredWidth,
                ["preferredHeight"] = element.preferredHeight,
                ["flexibleWidth"] = element.flexibleWidth,
                ["flexibleHeight"] = element.flexibleHeight,
                ["layoutPriority"] = element.layoutPriority,
            };
        }

        internal static Dictionary<string, int> RectOffsetRow(RectOffset offset)
        {
            return new Dictionary<string, int>
            {
                ["left"] = offset.left,
                ["right"] = offset.right,
                ["top"] = offset.top,
                ["bottom"] = offset.bottom,
            };
        }

        internal static TextAnchor ParseTextAnchor(string value)
        {
            return ParseEnum<TextAnchor>(NormalizeEnumToken(value));
        }

        internal static TEnum ParseEnum<TEnum>(string value)
            where TEnum : struct
        {
            if (Enum.TryParse<TEnum>(NormalizeEnumToken(value), ignoreCase: true, out var parsed))
            {
                return parsed;
            }

            throw new ArgumentException($"Unsupported {typeof(TEnum).Name} value '{value}'.");
        }

        internal static string NormalizeEnumToken(string value)
        {
            return value.Replace("-", string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty);
        }
    }
}
