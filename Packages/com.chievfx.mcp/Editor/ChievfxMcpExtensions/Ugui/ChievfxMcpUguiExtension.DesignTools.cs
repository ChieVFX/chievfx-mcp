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
    internal static class UguiDesignTools
    {
        internal static Dictionary<string, object?> EnsureCanvas(JToken args, UguiDependencyStatus status)
        {
            var warnings = new List<string>();
            var name = ReadString(args, "name") ?? "Canvas";
            var existing = ResolveGameObject(args, "canvasPath", "canvasInstanceId")
                ?? FindCanvasByName(status, name)
                ?? FindFirstCanvas(status);

            GameObject canvasObject;
            if (existing == null)
            {
                canvasObject = new GameObject(name, typeof(RectTransform));
                Undo.RegisterCreatedObjectUndo(canvasObject, "ChievFX MCP Create uGUI Canvas");
            }
            else
            {
                canvasObject = existing;
                Undo.RegisterFullObjectHierarchyUndo(canvasObject, "ChievFX MCP Normalize uGUI Canvas");
            }

            canvasObject.name = name;
            var canvas = EnsureRequiredComponent(canvasObject, status.CanvasType, "UnityEngine.Canvas");
            SetEnumProperty(canvas, "renderMode", "ScreenSpaceOverlay");
            EnsureComponent(canvasObject, status.CanvasScalerType);
            EnsureComponent(canvasObject, status.GraphicRaycasterType);

            var rect = canvasObject.GetComponent<RectTransform>();
            ApplyRect(rect, ReadRectArgs(args), warnings);

            var eventSystem = EnsureEventSystem(status, warnings);
            MarkDirty(canvasObject);
            var result = CreateToolEnvelope("ugui-canvas-ensure");
            result["success"] = true;
            result["canvas"] = CreateObjectRefRow(canvasObject);
            if (eventSystem != null)
            {
                result["eventSystem"] = CreateEventSystemRefRow(eventSystem);
            }

            AddWarnings(result, warnings);
            return result;
        }

        internal static Dictionary<string, object?> GetRect(JToken args, UguiDependencyStatus status)
        {
            var targets = ResolveExplicitUiTargets(args);
            if (targets.Length == 0)
            {
                throw new ArgumentException("ugui-rect-get requires paths or instanceIds.");
            }

            var rows = targets.Select(CreateRectDetailRow).ToArray();
            var result = CreateToolEnvelope("ugui-rect-get");
            result["count"] = rows.Length;
            result["totalMatches"] = targets.Length;
            result["rects"] = rows;
            return result;
        }

        internal static Dictionary<string, object?> UpdateRect(JToken args, UguiDependencyStatus status)
        {
            var warnings = new List<string>();
            var targets = ResolveExplicitUiTargets(args);
            if (targets.Length == 0)
            {
                throw new ArgumentException("ugui-rect-update requires paths or instanceIds.");
            }

            var rectArgs = ReadRectArgs(args);
            var layoutDrivenTargets = new List<Dictionary<string, object?>>();
            foreach (var target in targets)
            {
                var rect = target.GetComponent<RectTransform>() ?? target.AddComponent<RectTransform>();
                Undo.RecordObject(rect, "ChievFX MCP Update uGUI Rect");
                ApplyRect(rect, rectArgs, warnings);
                var layoutParent = GetParentLayoutGroup(rect);
                if (layoutParent != null)
                {
                    layoutDrivenTargets.Add(CreateLayoutDrivenRow(target, layoutParent));
                    warnings.Add($"'{GetTransformPath(target.transform)}' is under {layoutParent.GetType().Name}; parent layout may drive anchors, position, and size. Prefer ugui-layout-group-set, ugui-layout-element-set, then ugui-layout-rebuild.");
                    LayoutRebuilder.MarkLayoutForRebuild((RectTransform)layoutParent.transform);
                }

                MarkDirty(target);
            }

            var result = CreateToolEnvelope("ugui-rect-update");
            result["success"] = true;
            result["updatedCount"] = targets.Length;
            result["layoutDrivenCount"] = layoutDrivenTargets.Count;
            result["layoutDrivenTargets"] = layoutDrivenTargets.ToArray();
            AddWarnings(result, warnings);
            return result;
        }

        internal static Dictionary<string, object?> SetLayoutGroup(JToken args, UguiDependencyStatus status)
        {
            var warnings = new List<string>();
            var targets = ResolveExplicitUiTargets(args);
            if (targets.Length == 0)
            {
                throw new ArgumentException("ugui-layout-group-set requires paths or instanceIds.");
            }

            var layoutType = (ReadString(args, "layoutGroup") ?? ReadString(args, "type") ?? "vertical").Trim().ToLowerInvariant();
            var rows = new List<Dictionary<string, object?>>();
            foreach (var target in targets)
            {
                var rect = target.GetComponent<RectTransform>() ?? target.AddComponent<RectTransform>();
                var group = EnsureLayoutGroup(target, layoutType);
                Undo.RecordObject(group, "ChievFX MCP Set uGUI Layout Group");
                ApplyLayoutGroup(group, args, warnings);
                LayoutRebuilder.MarkLayoutForRebuild(rect);
                MarkDirty(target);
                rows.Add(CreateLayoutGroupRow(group));
            }

            var result = CreateToolEnvelope("ugui-layout-group-set");
            result["success"] = true;
            result["updatedCount"] = targets.Length;
            result["layoutGroups"] = rows.ToArray();
            AddWarnings(result, warnings);
            return result;
        }

        internal static Dictionary<string, object?> SetLayoutElement(JToken args, UguiDependencyStatus status)
        {
            var targets = ResolveExplicitUiTargets(args);
            if (targets.Length == 0)
            {
                throw new ArgumentException("ugui-layout-element-set requires paths or instanceIds.");
            }

            var rows = new List<Dictionary<string, object?>>();
            foreach (var target in targets)
            {
                var rect = target.GetComponent<RectTransform>() ?? target.AddComponent<RectTransform>();
                var element = target.GetComponent<LayoutElement>() ?? target.AddComponent<LayoutElement>();
                Undo.RecordObject(element, "ChievFX MCP Set uGUI Layout Element");
                ApplyLayoutElement(element, args);
                if (rect.parent is RectTransform parentRect)
                {
                    LayoutRebuilder.MarkLayoutForRebuild(parentRect);
                }

                MarkDirty(target);
                rows.Add(CreateLayoutElementRow(element));
            }

            var result = CreateToolEnvelope("ugui-layout-element-set");
            result["success"] = true;
            result["updatedCount"] = targets.Length;
            result["layoutElements"] = rows.ToArray();
            return result;
        }

        internal static Dictionary<string, object?> RebuildLayout(JToken args, UguiDependencyStatus status)
        {
            var targets = ResolveExplicitUiTargets(args);
            if (targets.Length == 0)
            {
                throw new ArgumentException("ugui-layout-rebuild requires paths or instanceIds.");
            }

            Canvas.ForceUpdateCanvases();
            var rows = new List<Dictionary<string, object?>>();
            foreach (var target in targets)
            {
                var rect = target.GetComponent<RectTransform>();
                if (rect == null)
                {
                    continue;
                }

                LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
                Canvas.ForceUpdateCanvases();
                rows.Add(CreateRectDetailRow(target));
            }

            var result = CreateToolEnvelope("ugui-layout-rebuild");
            result["success"] = true;
            result["rebuiltCount"] = rows.Count;
            result["rects"] = rows.ToArray();
            if (rows.Count != targets.Length)
            {
                result["warnings"] = new[] { "Skipped one or more targets without RectTransform." };
            }

            return result;
        }

        internal static Dictionary<string, object?> CreateScrollRect(JToken args, UguiDependencyStatus status)
        {
            var warnings = new List<string>();
            var canvas = ResolveGameObject(args, "canvasPath", "canvasInstanceId") ?? FindFirstCanvas(status);
            if (canvas == null)
            {
                canvas = (EnsureCanvas(new JObject { ["name"] = "Canvas" }, status)["canvas"] as Dictionary<string, object?>)?["path"] is string path
                    ? ResolveGameObject(path)
                    : FindFirstCanvas(status);
                warnings.Add("Created fallback Canvas because no Canvas existed.");
            }

            var parent = ResolveGameObject(args, "parentPath", "parentInstanceId") ?? canvas
                ?? throw new InvalidOperationException("Could not resolve or create uGUI parent.");
            var name = ReadString(args, "name") ?? "Scroll View";
            var direction = (ReadString(args, "direction") ?? "vertical").Trim().ToLowerInvariant();
            var contentLayout = (ReadString(args, "contentLayout") ?? (direction == "horizontal" ? "horizontal" : "vertical")).Trim().ToLowerInvariant();

            var root = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(ScrollRect));
            Undo.RegisterCreatedObjectUndo(root, "ChievFX MCP Create uGUI ScrollRect");
            root.transform.SetParent(parent.transform, false);
            ApplyRect(root.GetComponent<RectTransform>(), ReadRectArgs(args), warnings);
            var rootImage = root.GetComponent<Image>();
            rootImage.color = args["backgroundColor"] != null ? ReadColor(args["backgroundColor"]!, new Color(1f, 1f, 1f, 0.1f)) : new Color(1f, 1f, 1f, 0.1f);

            var viewport = new GameObject("Viewport", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(RectMask2D));
            Undo.RegisterCreatedObjectUndo(viewport, "ChievFX MCP Create uGUI Viewport");
            viewport.transform.SetParent(root.transform, false);
            ApplyRect(viewport.GetComponent<RectTransform>(), new RectArgs("stretch", null, Vector2.zero, Vector2.zero, 0f, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), null, null, null, null, false), warnings);
            viewport.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.02f);

            var content = new GameObject("Content", typeof(RectTransform));
            Undo.RegisterCreatedObjectUndo(content, "ChievFX MCP Create uGUI Content");
            content.transform.SetParent(viewport.transform, false);
            ConfigureScrollContentRect(content.GetComponent<RectTransform>(), direction);
            ConfigureContentLayout(content, contentLayout, args, warnings);
            ConfigureContentSizeFitter(content, contentLayout, direction, ReadBool(args, "contentSizeFitter", true));

            var scrollRect = root.GetComponent<ScrollRect>();
            scrollRect.viewport = viewport.GetComponent<RectTransform>();
            scrollRect.content = content.GetComponent<RectTransform>();
            scrollRect.horizontal = direction == "horizontal" || direction == "both";
            scrollRect.vertical = direction == "vertical" || direction == "both";
            scrollRect.movementType = ScrollRect.MovementType.Clamped;

            EnsureEventSystem(status, warnings);
            LayoutRebuilder.ForceRebuildLayoutImmediate(root.GetComponent<RectTransform>());
            MarkDirty(root);
            MarkDirty(viewport);
            MarkDirty(content);

            var result = CreateToolEnvelope("ugui-scrollrect-create");
            result["success"] = true;
            result["root"] = CreateObjectRefRow(root);
            result["parts"] = new Dictionary<string, object?>
            {
                ["viewport"] = CreateObjectRefRow(viewport),
                ["content"] = CreateObjectRefRow(content),
            };
            result["scroll"] = new Dictionary<string, object?>
            {
                ["horizontal"] = scrollRect.horizontal,
                ["vertical"] = scrollRect.vertical,
                ["movementType"] = scrollRect.movementType.ToString(),
            };
            result["contentLayout"] = CreateContentLayoutSummary(content);
            AddWarnings(result, warnings);
            return result;
        }

        internal static Dictionary<string, object?> CreateGrid(JToken args, UguiDependencyStatus status)
        {
            var warnings = new List<string>();
            var canvas = ResolveGameObject(args, "canvasPath", "canvasInstanceId") ?? FindFirstCanvas(status);
            if (canvas == null)
            {
                canvas = (EnsureCanvas(new JObject { ["name"] = "Canvas" }, status)["canvas"] as Dictionary<string, object?>)?["path"] is string path
                    ? ResolveGameObject(path)
                    : FindFirstCanvas(status);
                warnings.Add("Created fallback Canvas because no Canvas existed.");
            }

            var parent = ResolveGameObject(args, "parentPath", "parentInstanceId") ?? canvas
                ?? throw new InvalidOperationException("Could not resolve or create uGUI parent.");
            var count = Math.Max(0, Math.Min(ReadInt(args, "count", 12), 500));
            var cellType = (ReadString(args, "cellType") ?? "image").Trim().ToLowerInvariant();
            var cellNamePrefix = ReadString(args, "cellNamePrefix") ?? "Cell";

            var grid = new GameObject(ReadString(args, "name") ?? "Grid", typeof(RectTransform));
            Undo.RegisterCreatedObjectUndo(grid, "ChievFX MCP Create uGUI Grid");
            grid.transform.SetParent(parent.transform, false);
            ApplyRect(grid.GetComponent<RectTransform>(), ReadRectArgs(args), warnings);
            var group = grid.AddComponent<GridLayoutGroup>();
            ApplyLayoutGroup(group, args, warnings);
            if (args["constraintCount"] == null)
            {
                group.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
                group.constraintCount = Math.Max(1, Mathf.CeilToInt(Mathf.Sqrt(Math.Max(1, count))));
            }

            var cells = new List<Dictionary<string, object?>>();
            for (var i = 0; i < count; i++)
            {
                var cell = CreateGridCell(cellType, status, warnings);
                Undo.RegisterCreatedObjectUndo(cell, "ChievFX MCP Create uGUI Grid Cell");
                cell.name = $"{cellNamePrefix} {i + 1}";
                cell.transform.SetParent(grid.transform, false);
                var rect = cell.GetComponent<RectTransform>() ?? cell.AddComponent<RectTransform>();
                rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = group.cellSize;
                ApplyCellColor(cell, i, count, args);
                cells.Add(CreateObjectRefRow(cell));
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(grid.GetComponent<RectTransform>());
            MarkDirty(grid);
            var result = CreateToolEnvelope("ugui-grid-create");
            result["success"] = true;
            result["grid"] = CreateObjectRefRow(grid);
            result["layout"] = CreateGridLayoutSummary(group);
            result["cellCount"] = cells.Count;
            result["cells"] = new Dictionary<string, object?>
            {
                ["first"] = cells.Count > 0 ? cells[0] : null,
                ["last"] = cells.Count > 1 ? cells[cells.Count - 1] : cells.Count == 1 ? cells[0] : null,
                ["all"] = cells.Count <= 12 ? cells.ToArray() : null,
            };
            AddWarnings(result, warnings);
            return result;
        }

        internal static Dictionary<string, object?> SetSiblingDrawOrder(JToken args, UguiDependencyStatus status)
        {
            var targets = ResolveExplicitUiTargets(args);
            if (targets.Length == 0)
            {
                throw new ArgumentException("ugui-sibling-draworder-set requires paths or instanceIds.");
            }

            foreach (var target in targets)
            {
                if (target.GetComponent<RectTransform>() == null)
                {
                    throw new ArgumentException($"Target '{GetTransformPath(target.transform)}' is not a uGUI RectTransform.");
                }
                if (target.transform.parent == null)
                {
                    throw new ArgumentException($"Target '{GetTransformPath(target.transform)}' has no parent, so sibling draw order cannot be set.");
                }
            }

            var parent = targets[0].transform.parent;
            if (targets.Any(target => target.transform.parent != parent))
            {
                throw new ArgumentException("All targets must share one parent when setting sibling draw order.");
            }

            var placement = (ReadString(args, "placement") ?? (args["index"] != null ? "index" : "last")).Trim().ToLowerInvariant();
            var desiredOrder = BuildSiblingDrawOrder(args, placement, parent!, targets);

            Undo.RegisterFullObjectHierarchyUndo(parent.gameObject, "ChievFX MCP Set uGUI Draw Order");
            for (var i = 0; i < desiredOrder.Count; i++)
            {
                desiredOrder[i].transform.SetSiblingIndex(i);
                MarkDirty(desiredOrder[i]);
            }
            MarkDirty(parent.gameObject);

            var rows = targets.Select(target => new Dictionary<string, object?>
            {
                ["name"] = target.name,
                ["path"] = GetTransformPath(target.transform),
                ["instanceId"] = UnityObjectIdentity.GetLegacyInstanceId(target),
                ["siblingIndex"] = target.transform.GetSiblingIndex(),
            }).ToArray();
            var result = CreateToolEnvelope("ugui-sibling-draworder-set");
            result["success"] = true;
            result["updatedCount"] = targets.Length;
            result["parentPath"] = GetTransformPath(parent);
            result["targets"] = rows;
            result["siblingOrder"] = CreateSiblingOrderWindow(parent, targets);
            return result;
        }

        internal static object[] CreateSiblingOrderWindow(Transform parent, GameObject[] targets)
        {
            const int fullLimit = 10;
            const int windowRadius = 4;
            var siblings = parent.Cast<Transform>().Select(child => child.gameObject).ToArray();
            var targetIndexes = targets.Select(target => target.transform.GetSiblingIndex()).OrderBy(index => index).ToArray();
            var center = targetIndexes.Length > 0 ? targetIndexes[targetIndexes.Length / 2] : 0;
            var start = siblings.Length <= fullLimit ? 0 : Math.Max(0, center - windowRadius);
            var end = siblings.Length <= fullLimit ? siblings.Length - 1 : Math.Min(siblings.Length - 1, center + windowRadius);

            var result = new List<Dictionary<string, object?>>();
            var shownCount = (end >= start ? end - start + 1 : 0) + (start > 0 ? 1 : 0) + (end < siblings.Length - 1 ? 1 : 0);
            result.Add(new Dictionary<string, object?> { ["showing"] = $"{shownCount}/{siblings.Length}" });
            result.Add(new Dictionary<string, object?> { ["truncated"] = siblings.Length > fullLimit });
            if (start > 0)
            {
                result.Add(new Dictionary<string, object?> { [$"...{start}_more"] = "..." });
            }

            foreach (var index in Enumerable.Range(start, Math.Max(0, end - start + 1)))
            {
                result.Add(new Dictionary<string, object?> { [$"{index}:"] = siblings[index].name });
            }

            if (end < siblings.Length - 1)
            {
                result.Add(new Dictionary<string, object?> { [$"...{siblings.Length - end - 1}_more"] = "..." });
            }

            return result.ToArray();
        }

        internal static List<GameObject> BuildSiblingDrawOrder(JToken args, string placement, Transform parent, GameObject[] targets)
        {
            var targetSet = new HashSet<GameObject>(targets);
            var existingOrder = parent.Cast<Transform>().Select(child => child.gameObject).ToList();
            var remaining = existingOrder.Where(child => !targetSet.Contains(child)).ToList();
            int insertionIndex;
            switch (placement)
            {
                case "first":
                    insertionIndex = 0;
                    break;
                case "last":
                    insertionIndex = remaining.Count;
                    break;
                case "index":
                    if (args["index"] == null)
                    {
                        throw new ArgumentException("placement 'index' requires index.");
                    }
                    insertionIndex = Mathf.Clamp(ReadInt(args, "index", 0), 0, remaining.Count);
                    break;
                case "before":
                case "after":
                    var sibling = ResolveGameObject(args, "siblingPath", "siblingInstanceId")
                        ?? throw new ArgumentException("placement 'before'/'after' requires siblingPath or siblingInstanceId.");
                    if (targetSet.Contains(sibling))
                    {
                        throw new ArgumentException("sibling target cannot also be one of the moved targets.");
                    }
                    if (sibling.transform.parent != parent)
                    {
                        throw new ArgumentException("sibling target must share the same parent as moved targets.");
                    }
                    var siblingIndex = remaining.IndexOf(sibling);
                    insertionIndex = siblingIndex + (placement == "after" ? 1 : 0);
                    break;
                default:
                    throw new ArgumentException($"Unsupported draw order placement '{placement}'.");
            }

            remaining.InsertRange(insertionIndex, targets);
            return remaining;
        }

        internal static Dictionary<string, object?> CreateSimple(JToken args, UguiDependencyStatus status)
        {
            var warnings = new List<string>();
            var name = ReadString(args, "name") ?? "UiElement";
            var parent = ResolveUguiParent(args, status, warnings);
            var created = new GameObject(name, typeof(RectTransform));
            Undo.RegisterCreatedObjectUndo(created, "ChievFX MCP Create uGUI Element");
            created.transform.SetParent(parent.transform, false);
            ApplyRect(created.GetComponent<RectTransform>() ?? created.AddComponent<RectTransform>(), ReadRectArgs(args), warnings);
            if (TryReadImageArgs(args, out var imageArgs))
            {
                var image = EnsureComponent(created, status.ImageType)
                    ?? throw new InvalidOperationException("UnityEngine.UI.Image type is unavailable.");
                ApplyImageSettings(created, image, imageArgs, status, warnings);
            }

            EnsureEventSystem(status, warnings);
            MarkDirty(created);
            var result = CreateMutationResult("ugui-create-simple", created, warnings, status, null);
            result["hasImage"] = HasComponent(created, status.ImageType);

            return result;
        }

        internal static Dictionary<string, object?> CreateControl(JToken args, UguiDependencyStatus status)
        {
            var warnings = new List<string>();
            var controlType = (ReadString(args, "controlType") ?? ReadString(args, "type") ?? "button").Trim().ToLowerInvariant();
            var name = ReadString(args, "name") ?? DefaultElementName(controlType);
            var parent = ResolveUguiParent(args, status, warnings);
            var textBackend = controlType == "button" ? ResolveTextBackend(args, status, warnings) : "legacy";
            var created = controlType == "progressbar"
                ? CreateProgressbar(status, warnings)
                : CreateElementObject(controlType, status, warnings, textBackend);
            Undo.RegisterCreatedObjectUndo(created, "ChievFX MCP Create uGUI Control");
            created.name = name;
            created.transform.SetParent(parent.transform, false);
            ApplyRect(created.GetComponent<RectTransform>() ?? created.AddComponent<RectTransform>(), ReadRectArgs(args), warnings);
            NormalizeElement(created, controlType, args, status, warnings);
            if (TryReadImageArgs(args, out var imageArgs) && status.ImageType != null)
            {
                var image = created.GetComponent(status.ImageType) as Component ?? EnsureComponent(created, status.ImageType);
                if (image != null)
                {
                    ApplyImageSettings(created, image, imageArgs, status, warnings);
                }
            }

            ApplyControlValue(created, controlType, args, status, warnings);
            EnsureEventSystem(status, warnings);
            MarkDirty(created);
            var result = CreateMutationResult("ugui-create-control", created, warnings, status, null);
            result["controlType"] = controlType;
            if (controlType == "button")
            {
                result["textBackend"] = textBackend;
            }
            return result;
        }

        internal static Dictionary<string, object?> SetImage(JToken args, UguiDependencyStatus status)
        {
            var warnings = new List<string>();
            var target = ResolveRequiredGameObject(args, "targetPath", "instanceId");
            var image = target.GetComponent(status.ImageType) as Component
                ?? EnsureComponent(target, status.ImageType)
                ?? throw new InvalidOperationException("UnityEngine.UI.Image type is unavailable.");
            Undo.RecordObject(image, "ChievFX MCP Set uGUI Image");
            ApplyImageSettings(target, image, args, status, warnings);

            MarkDirty(target);
            var result = CreateMutationResult("ugui-image-set", target, warnings, status, null);
            result["image"] = CreateImageRow(image);
            return result;
        }

        internal static void ApplyImageSettings(GameObject target, Component image, JToken args, UguiDependencyStatus status, List<string> warnings)
        {
            Sprite? sprite = null;
            if (args["spritePath"] != null || args["spriteGuid"] != null)
            {
                sprite = ResolveSprite(args, warnings);
                SetProperty(image, "sprite", sprite!);
            }

            if (args["color"] != null)
            {
                SetGraphicColor(image, ReadColor(args["color"]!, Color.white));
            }

            if (args["raycastTarget"] != null)
            {
                SetProperty(image, "raycastTarget", ReadBool(args, "raycastTarget", true));
            }

            if (args["preserveAspect"] != null)
            {
                SetProperty(image, "preserveAspect", ReadBool(args, "preserveAspect", false));
            }

            var requestedImageType = ReadString(args, "imageType") ?? ReadString(args, "type");
            var effectiveSprite = sprite ?? GetPropertyValue(image, "sprite") as Sprite;
            var autoImageType = string.Equals(requestedImageType, "Auto", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrWhiteSpace(requestedImageType) && sprite != null);
            if (autoImageType)
            {
                SetEnumProperty(image, "type", ChooseAutoImageType(effectiveSprite));
            }
            else if (!string.IsNullOrWhiteSpace(requestedImageType))
            {
                SetEnumProperty(image, "type", requestedImageType!);
            }

            var effectiveType = Convert.ToString(GetPropertyValue(image, "type"), CultureInfo.InvariantCulture) ?? string.Empty;
            if (!autoImageType)
            {
                AddSlicedSpriteWarnings(effectiveSprite, effectiveType, warnings);
            }
        }

        internal static Dictionary<string, object?> ConfigureSprite(JToken args, UguiDependencyStatus status)
        {
            var warnings = new List<string>();
            var path = ResolveAssetPath(args, required: true)
                ?? throw new ArgumentException("spritePath or spriteGuid is required.");
            var importer = AssetImporter.GetAtPath(path) as TextureImporter
                ?? throw new ArgumentException($"Asset '{path}' is not imported by TextureImporter.");

            importer.textureType = TextureImporterType.Sprite;
            SetImporterSpriteMeshType(importer, SpriteMeshType.FullRect);
            importer.spritePixelsPerUnit = ReadFloat(args, "pixelsPerUnit", ReadFloat(args, "spritePixelsPerUnit", importer.spritePixelsPerUnit));
            if (args["spriteBorder"] is JObject borderObject)
            {
                importer.spriteBorder = ReadVector4(borderObject, importer.spriteBorder);
            }

            importer.SaveAndReimport();
            var result = ReadSpriteReadiness("tool://ugui-sprite-configure", path, status);
            result["warnings"] = ((string[])result["warnings"]!).Concat(warnings).ToArray();
            return result;
        }

        internal static Dictionary<string, object?> UiHierarchy(JToken args, UguiDependencyStatus status)
        {
            var roots = ResolveExplicitUiTargets(args);
            var maxResults = Math.Max(1, Math.Min(ReadInt(args, "maxResults", ReadInt(args, "maxElements", 64)), 256));
            var maxDepth = Math.Max(0, Math.Min(ReadInt(args, "maxDepth", 6), 24));
            var includeInactive = ReadBool(args, "includeInactive", true);
            var includeComponents = ReadBool(args, "includeComponents", false);
            var result = CreateToolEnvelope("ugui-ui-hierarchy");
            result["maxResults"] = maxResults;
            var canvases = roots.Length > 0
                ? roots.SelectMany(root => GetComponentsInChildren(root, status.CanvasType, includeInactive)).Distinct().ToArray()
                : FindCanvases(status, includeInactive);
            var emitted = 0;
            var truncated = false;
            var depthLimited = false;
            var rows = new List<Dictionary<string, object?>>();
            foreach (var canvas in canvases)
            {
                var node = BuildUguiHierarchyNode(canvas.gameObject, includeInactive, includeComponents, 0, maxDepth, maxResults, ref emitted, ref truncated, ref depthLimited);
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
            result["totalObjects"] = canvases.Sum(canvas => canvas.GetComponentsInChildren<RectTransform>(includeInactive).Length);
            result["maxDepth"] = maxDepth;
            result["truncated"] = truncated;
            result["depthLimited"] = depthLimited;
            result["roots"] = rows.ToArray();
            return result;
        }

        private static readonly string[] UiFindKnownArguments =
        {
            "paths", "instanceIds", "name", "nameContains", "namePattern", "componentType",
            "includeInactive", "includeDetails", "normalizedCoords", "maxResults", "outputFormat",
        };

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

            var includeInactive = ReadBool(args, "includeInactive", true);
            var includeDetails = ReadBool(args, "includeDetails", true);
            var normalizedCoords = ReadBool(args, "normalizedCoords", false);
            var maxResults = Math.Max(1, Math.Min(ReadInt(args, "maxResults", 16), 64));
            var explicitTargets = ResolveExplicitUiTargets(args);
            var name = ReadString(args, "name");
            var nameContains = ReadString(args, "nameContains");
            var namePattern = ReadString(args, "namePattern");
            var componentType = ReadString(args, "componentType");
            var hasExplicitTarget = explicitTargets.Length > 0;
            var source = hasExplicitTarget
                ? explicitTargets
                : FindCanvases(status, includeInactive)
                    .SelectMany(canvas => canvas.GetComponentsInChildren<RectTransform>(includeInactive).Select(rect => rect.gameObject))
                    .Distinct()
                    .ToArray();
            var matches = source
                .Where(target => includeInactive || target.activeInHierarchy)
                .Where(target => string.IsNullOrWhiteSpace(name) || string.Equals(target.name, name, StringComparison.Ordinal))
                .Where(target => string.IsNullOrWhiteSpace(nameContains) || target.name.IndexOf(nameContains!, StringComparison.OrdinalIgnoreCase) >= 0)
                .Where(target => string.IsNullOrWhiteSpace(namePattern) || GameObjectBridgeService.WildcardMatches(target.name, namePattern!))
                .Where(target => string.IsNullOrWhiteSpace(componentType) || HasComponentNamed(target, componentType!))
                .ToArray();
            var hasFilter = hasExplicitTarget
                || !string.IsNullOrWhiteSpace(name)
                || !string.IsNullOrWhiteSpace(nameContains)
                || !string.IsNullOrWhiteSpace(namePattern)
                || !string.IsNullOrWhiteSpace(componentType);
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

        internal static Dictionary<string, object?> CreateImagePrimitive(JToken args, UguiDependencyStatus status)
        {
            var warnings = new List<string>();
            var pngPath = NormalizePrimitivePngPath(ReadString(args, "path") ?? ReadString(args, "pngPath") ?? ReadString(args, "assetPath"));
            if (args["pngPath"] != null && args["path"] == null)
            {
                warnings.Add("pngPath is deprecated for ugui-image-primitive-create; use path.");
            }
            if (args["assetPath"] != null && args["path"] == null && args["pngPath"] == null)
            {
                warnings.Add("assetPath is deprecated for ugui-image-primitive-create; use path.");
            }

            var primitiveType = (ReadString(args, "primitiveType") ?? ReadString(args, "type") ?? "rounded-rect").Trim().ToLowerInvariant();
            var width = Math.Max(4, Math.Min(ReadInt(args, "width", 64), 2048));
            var height = Math.Max(4, Math.Min(ReadInt(args, "height", 64), 2048));
            var radius = Math.Max(0f, ReadFloat(args, "radius", Math.Min(width, height) * 0.2f));
            var color = args["color"] != null ? ReadColor(args["color"]!, Color.white) : Color.white;
            WritePrimitiveTexture(pngPath, primitiveType, width, height, radius, color);
            var border = args["spriteBorder"] is JObject borderObject
                ? ReadVector4(borderObject, PrimitiveBorder(primitiveType, radius))
                : PrimitiveBorder(primitiveType, radius);
            ConfigureSpriteImporter(pngPath, pixelsPerUnit: ReadFloat(args, "pixelsPerUnit", 100f), border: border);
            var sprite = LoadSpriteAtPath(pngPath)
                ?? throw new InvalidOperationException($"Generated primitive sprite did not import at '{pngPath}'.");

            var canvas = ResolveGameObject(args, "canvasPath", "canvasInstanceId") ?? FindFirstCanvas(status);
            if (canvas == null)
            {
                canvas = (EnsureCanvas(new JObject { ["name"] = "Canvas" }, status)["canvas"] as Dictionary<string, object?>)?["path"] is string path
                    ? ResolveGameObject(path)
                    : FindFirstCanvas(status);
                warnings.Add("Created fallback Canvas because no Canvas existed.");
            }

            var parent = ResolveGameObject(args, "parentPath", "parentInstanceId") ?? canvas
                ?? throw new InvalidOperationException("Could not resolve or create uGUI parent.");
            var created = new GameObject(ReadString(args, "name") ?? Path.GetFileNameWithoutExtension(pngPath), typeof(RectTransform));
            Undo.RegisterCreatedObjectUndo(created, "ChievFX MCP Create uGUI Primitive Image");
            created.transform.SetParent(parent.transform, false);
            ApplyRect(created.GetComponent<RectTransform>()!, ReadRectArgs(args), warnings);
            var image = EnsureComponent(created, status.ImageType)
                ?? throw new InvalidOperationException("UnityEngine.UI.Image type is unavailable.");
            SetProperty(image, "sprite", sprite);
            SetProperty(image, "color", Color.white);
            SetProperty(image, "raycastTarget", ReadBool(args, "raycastTarget", true));

            var requestedImageType = ReadString(args, "imageType");
            var autoImageType = string.IsNullOrWhiteSpace(requestedImageType)
                || string.Equals(requestedImageType, "Auto", StringComparison.OrdinalIgnoreCase);
            if (autoImageType)
            {
                SetEnumProperty(image, "type", ChooseAutoImageType(sprite));
            }
            else
            {
                SetEnumProperty(image, "type", requestedImageType!);
                AddSlicedSpriteWarnings(sprite, requestedImageType!, warnings);
            }

            MarkDirty(created);

            var result = CreateToolEnvelope("ugui-image-primitive-create");
            result["success"] = true;
            result["path"] = pngPath;
            result["gameObjectPath"] = GetTransformPath(created.transform);
            result["spriteBorder"] = Vector4Row(border);
            result["image"] = CreateImageRow(image);
            AddWarnings(result, warnings);
            return result;
        }
    }
}
