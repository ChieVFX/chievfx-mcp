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
using static Chievfx.Mcp.Extensions.Ugui.UguiLayoutHelpers;
using static Chievfx.Mcp.Extensions.Ugui.UguiRuntimeControlHelpers;
using static Chievfx.Mcp.Extensions.Ugui.UguiRuntimeHelpers;
using static Chievfx.Mcp.Extensions.Ugui.UguiRuntimeTools;
using static Chievfx.Mcp.Extensions.Ugui.UguiSchemas;
using static Chievfx.Mcp.Extensions.Ugui.UguiSharedHelpers;

namespace Chievfx.Mcp.Extensions.Ugui
{
    internal static class UguiResourcesAndRows
    {
        internal static Dictionary<string, object?> ReadCanvases(string uri, UguiDependencyStatus status)
        {
            var canvases = FindCanvases(status, includeInactive: true);
            var result = CreateEnvelope(uri, status);
            result["count"] = canvases.Length;
            result["canvases"] = canvases.Select(canvas =>
            {
                var row = CreateGameObjectRow(canvas.gameObject);
                row["detailUri"] = CanvasDetailPrefix + EncodeSegment(GetTransformPath(canvas.transform));
                row["elementCount"] = canvas.GetComponentsInChildren<RectTransform>(true).Length - 1;
                return row;
            }).ToArray();
            return result;
        }

        internal static Dictionary<string, object?> ReadCanvasDetail(string uri, string pathOrInstanceId, UguiDependencyStatus status)
        {
            var target = ResolveGameObject(pathOrInstanceId) ?? ResolveGameObject(int.TryParse(pathOrInstanceId, out var id) ? id : 0);
            var result = CreateEnvelope(uri, status);
            result["requested"] = pathOrInstanceId;
            result["found"] = target != null && HasComponent(target, status.CanvasType);
            result["canvas"] = target == null ? null : CreateGameObjectRow(target);
            result["elements"] = target == null
                ? Array.Empty<Dictionary<string, object?>>()
                : target.GetComponentsInChildren<RectTransform>(true)
                    .Where(rect => rect.gameObject != target)
                    .Take(64)
                    .Select(rect => CreateGameObjectRow(rect.gameObject))
                    .ToArray();
            return result;
        }

        internal static Dictionary<string, object?> ReadSpriteReadiness(string uri, string guidOrPath, UguiDependencyStatus status)
        {
            var warnings = new List<string>();
            var path = ResolveAssetPath(guidOrPath);
            var result = CreateEnvelope(uri, status);
            result["requested"] = guidOrPath;
            result["path"] = path;
            result["guid"] = string.IsNullOrWhiteSpace(path) ? string.Empty : AssetDatabase.AssetPathToGUID(path);
            result["found"] = !string.IsNullOrWhiteSpace(path);

            if (string.IsNullOrWhiteSpace(path))
            {
                warnings.Add("Sprite asset was not found by GUID or project-relative path.");
                result["warnings"] = warnings.ToArray();
                return result;
            }

            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path)
                ?? AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>().FirstOrDefault();
            result["textureType"] = importer?.textureType.ToString() ?? string.Empty;
            result["meshType"] = importer == null ? string.Empty : GetImporterSpriteMeshType(importer).ToString();
            result["pixelsPerUnit"] = importer?.spritePixelsPerUnit ?? sprite?.pixelsPerUnit ?? 0f;
            result["spriteBorder"] = Vector4Row(importer?.spriteBorder ?? sprite?.border ?? Vector4.zero);
            result["alpha"] = new Dictionary<string, object?>
            {
                ["alphaIsTransparency"] = importer?.alphaIsTransparency,
                ["textureHasAlpha"] = importer?.DoesSourceTextureHaveAlpha(),
            };
            result["dimensions"] = new Dictionary<string, int>
            {
                ["width"] = texture == null ? 0 : texture.width,
                ["height"] = texture == null ? 0 : texture.height,
            };

            if (importer == null)
            {
                warnings.Add("Asset is not imported by TextureImporter.");
            }
            else
            {
                if (importer.textureType != TextureImporterType.Sprite)
                {
                    warnings.Add("TextureImporter.textureType should be Sprite for uGUI Image sprites.");
                }

                if (GetImporterSpriteMeshType(importer) != SpriteMeshType.FullRect)
                {
                    warnings.Add("TextureImporter meshType FullRect is recommended for 9-slice uGUI sprites.");
                }

                if (IsZeroBorder(importer.spriteBorder))
                {
                    warnings.Add("Sprite border is zero; Sliced/Tiled Image types require non-zero Sprite.border for 9-slice panels/buttons.");
                }
            }

            result["warnings"] = warnings.ToArray();
            return result;
        }

        internal static Dictionary<string, object?> CreateMutationResult(string operation, GameObject target, List<string> warnings, UguiDependencyStatus status, GameObject? eventSystem)
        {
            var result = CreateToolEnvelope(operation);
            result["target"] = CreateCompactGameObjectRow(target);
            if (eventSystem != null)
            {
                result["eventSystem"] = CreateEventSystemRefRow(eventSystem);
            }

            AddWarnings(result, warnings);
            return result;
        }

        internal static void AddWarnings(Dictionary<string, object?> result, IEnumerable<string> warnings)
        {
            var distinctWarnings = warnings.Distinct().ToArray();
            if (distinctWarnings.Length > 0)
            {
                result["warnings"] = distinctWarnings;
            }
        }

        internal static Dictionary<string, object?> CreateObjectRefRow(GameObject target)
        {
            return new Dictionary<string, object?>
            {
                ["name"] = target.name,
                ["path"] = GetTransformPath(target.transform),
                ["instanceId"] = UnityObjectIdentity.GetLegacyInstanceId(target),
            };
        }

        internal static Dictionary<string, object?> CreateEventSystemRefRow(GameObject eventSystem)
        {
            var row = CreateObjectRefRow(eventSystem);
            row["inputModules"] = eventSystem.GetComponents<BaseInputModule>()
                .Where(module => module != null)
                .Select(module => module.GetType().Name)
                .ToArray();
            return row;
        }

        internal static Dictionary<string, object?> CreateCompactGameObjectRow(GameObject target)
        {
            var result = new Dictionary<string, object?>
            {
                ["name"] = target.name,
                ["path"] = GetTransformPath(target.transform),
                ["instanceId"] = UnityObjectIdentity.GetLegacyInstanceId(target),
                ["components"] = target.GetComponents<Component>().Where(component => component != null).Select(component => component.GetType().Name).ToArray(),
            };
            var rect = target.GetComponent<RectTransform>();
            if (rect != null)
            {
                result["rectTransform"] = CreateCompactRectRow(rect);
            }

            return result;
        }

        internal static GameObject[] ResolveExplicitUiTargets(JToken args)
        {
            var targets = new List<GameObject>();
            if (args["paths"] is JArray paths)
            {
                foreach (var item in paths)
                {
                    if (item.Type != JTokenType.String)
                    {
                        throw new ArgumentException("paths entries must be strings.");
                    }

                    var target = ResolveGameObject(item.Value<string>());
                    if (target != null)
                    {
                        targets.Add(target);
                    }
                }
            }

            if (args["instanceIds"] is JArray ids)
            {
                foreach (var item in ids)
                {
                    if (item.Type != JTokenType.Integer)
                    {
                        throw new ArgumentException("instanceIds entries must be integers.");
                    }

                    var target = ResolveGameObject(item.Value<int>());
                    if (target != null)
                    {
                        targets.Add(target);
                    }
                }
            }

            return targets
                .GroupBy(UnityObjectIdentity.GetLegacyInstanceId)
                .Select(group => group.First())
                .ToArray();
        }

        internal static Dictionary<string, object?>? BuildUguiHierarchyNode(
            GameObject target,
            bool includeInactive,
            bool includeComponents,
            int depth,
            int maxDepth,
            int maxResults,
            ref int emitted,
            ref bool truncated,
            ref bool depthLimited)
        {
            if (truncated || emitted >= maxResults)
            {
                truncated = true;
                return null;
            }

            if (!includeInactive && !target.activeInHierarchy)
            {
                return null;
            }

            if (target.GetComponent<RectTransform>() == null)
            {
                return null;
            }

            emitted++;
            var node = CreateUguiElementRef(target, includeComponents);
            if (depth >= maxDepth)
            {
                if (target.transform.Cast<Transform>().Any(child => child.GetComponent<RectTransform>() != null))
                {
                    depthLimited = true;
                }

                return node;
            }

            var children = new List<Dictionary<string, object?>>();
            foreach (Transform child in target.transform)
            {
                var childNode = BuildUguiHierarchyNode(child.gameObject, includeInactive, includeComponents, depth + 1, maxDepth, maxResults, ref emitted, ref truncated, ref depthLimited);
                if (childNode != null)
                {
                    children.Add(childNode);
                }

                if (truncated)
                {
                    break;
                }
            }

            if (children.Count > 0)
            {
                node["children"] = children.ToArray();
            }

            return node;
        }

        internal static Dictionary<string, object?> CreateUguiElementRef(GameObject target)
        {
            return CreateUguiElementRef(target, includeComponentTypes: true);
        }

        internal static Dictionary<string, object?> CreateUguiElementRef(GameObject target, bool includeComponentTypes)
        {
            var result = new Dictionary<string, object?>
            {
                ["name"] = target.name,
                ["path"] = GetTransformPath(target.transform),
                ["instanceId"] = UnityObjectIdentity.GetLegacyInstanceId(target),
                ["activeSelf"] = target.activeSelf,
                ["activeInHierarchy"] = target.activeInHierarchy,
            };
            if (includeComponentTypes)
            {
                result["componentTypes"] = GetComponentTypePreview(target, out var truncated);
                result["componentTypesTruncated"] = truncated;
            }

            return result;
        }

        internal static Dictionary<string, object?> CreateUguiElementDetail(GameObject target, UguiDependencyStatus status, bool normalizedCoords)
        {
            var detail = CreateUguiElementRef(target);
            detail["tag"] = target.tag;
            detail["layer"] = target.layer;
            detail["childCount"] = target.transform.childCount;
            detail["screenRect"] = CreateScreenRectRow(target.GetComponent<RectTransform>(), status, normalizedCoords);

            return detail;
        }

        internal static Dictionary<string, object?> CreateRectDetailRow(GameObject target)
        {
            return new Dictionary<string, object?>
            {
                ["name"] = target.name,
                ["path"] = GetTransformPath(target.transform),
                ["instanceId"] = UnityObjectIdentity.GetLegacyInstanceId(target),
                ["rectTransform"] = CreateRectRow(target.GetComponent<RectTransform>()),
            };
        }

        internal static string[] GetComponentTypePreview(GameObject target, out bool truncated)
        {
            const int maxTypes = 8;
            var componentTypes = target.GetComponents<Component>()
                .Where(component => component != null)
                .Select(component => component.GetType().Name)
                .ToArray();
            truncated = componentTypes.Length > maxTypes;
            return componentTypes.Take(maxTypes).ToArray();
        }

        internal static bool HasComponentNamed(GameObject target, string componentType)
        {
            return target.GetComponents<Component>()
                .Where(component => component != null)
                .Any(component =>
                    string.Equals(component.GetType().Name, componentType, StringComparison.Ordinal)
                    || string.Equals(component.GetType().FullName, componentType, StringComparison.Ordinal));
        }

        internal static Dictionary<string, object?> CreateGameObjectRow(GameObject target)
        {
            return new Dictionary<string, object?>
            {
                ["name"] = target.name,
                ["path"] = GetTransformPath(target.transform),
                ["instanceId"] = UnityObjectIdentity.GetLegacyInstanceId(target),
                ["activeInHierarchy"] = target.activeInHierarchy,
                ["components"] = target.GetComponents<Component>().Where(component => component != null).Select(component => component.GetType().Name).ToArray(),
                ["rectTransform"] = CreateRectRow(target.GetComponent<RectTransform>()),
            };
        }

        internal static Dictionary<string, object?>? CreateRectRow(RectTransform? rect)
        {
            if (rect == null)
            {
                return null;
            }

            return new Dictionary<string, object?>
            {
                ["anchorMin"] = Vector2Row(rect.anchorMin),
                ["anchorMax"] = Vector2Row(rect.anchorMax),
                ["anchoredPosition"] = Vector2Row(rect.anchoredPosition),
                ["sizeDelta"] = Vector2Row(rect.sizeDelta),
                ["pivot"] = Vector2Row(rect.pivot),
                ["offsetMin"] = Vector2Row(rect.offsetMin),
                ["offsetMax"] = Vector2Row(rect.offsetMax),
                ["rect"] = new Dictionary<string, object?>
                {
                    ["width"] = rect.rect.width,
                    ["height"] = rect.rect.height,
                },
            };
        }

        internal static Dictionary<string, object?>? CreateScreenRectRow(RectTransform? rect, UguiDependencyStatus status, bool normalizedCoords)
        {
            if (rect == null)
            {
                return null;
            }

            var canvas = FindParentCanvas(rect.gameObject, status);
            var renderMode = Convert.ToString(canvas == null ? null : GetPropertyValue(canvas, "renderMode"), CultureInfo.InvariantCulture);
            var camera = string.Equals(renderMode, "ScreenSpaceOverlay", StringComparison.Ordinal)
                ? null
                : canvas == null ? null : GetPropertyValue(canvas, "worldCamera") as Camera;
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            var screenPoints = corners.Select(corner => RectTransformUtility.WorldToScreenPoint(camera, corner)).ToArray();
            var xMin = screenPoints.Min(point => point.x);
            var xMax = screenPoints.Max(point => point.x);
            var yMin = screenPoints.Min(point => point.y);
            var yMax = screenPoints.Max(point => point.y);
            var center = new Vector2((xMin + xMax) * 0.5f, (yMin + yMax) * 0.5f);
            var screenSize = ResolveRuntimeUiScreenSize(status);

            var result = new Dictionary<string, object?>
            {
                ["origin"] = "bottom-left",
                ["units"] = normalizedCoords ? "normalized" : "pixels",
            };
            if (normalizedCoords)
            {
                result["rect"] = CreateBoundsRow(
                    xMin / screenSize.x,
                    yMin / screenSize.y,
                    xMax / screenSize.x,
                    yMax / screenSize.y);
                result["center"] = RoundedVector2Row(new Vector2(center.x / screenSize.x, center.y / screenSize.y));
            }
            else
            {
                result["rect"] = CreateBoundsRow(xMin, yMin, xMax, yMax);
                result["center"] = RoundedVector2Row(center);
            }

            return result;
        }

        internal static Dictionary<string, object?> RoundedVector2Row(Vector2 value)
        {
            return new Dictionary<string, object?>
            {
                ["x"] = RoundForOutput(value.x),
                ["y"] = RoundForOutput(value.y),
            };
        }

        internal static Dictionary<string, object?> CreateBoundsRow(float xMin, float yMin, float xMax, float yMax)
        {
            return new Dictionary<string, object?>
            {
                ["xMin"] = RoundForOutput(xMin),
                ["yMin"] = RoundForOutput(yMin),
                ["xMax"] = RoundForOutput(xMax),
                ["yMax"] = RoundForOutput(yMax),
                ["width"] = RoundForOutput(xMax - xMin),
                ["height"] = RoundForOutput(yMax - yMin),
            };
        }

        internal static float RoundForOutput(float value)
        {
            return (float)Math.Round(value, 3, MidpointRounding.AwayFromZero);
        }

        internal static Dictionary<string, object?> CreateCompactRectRow(RectTransform rect)
        {
            return new Dictionary<string, object?>
            {
                ["anchorMin"] = Vector2Row(rect.anchorMin),
                ["anchorMax"] = Vector2Row(rect.anchorMax),
                ["position"] = Vector2Row(rect.anchoredPosition),
                ["size"] = Vector2Row(rect.sizeDelta),
                ["offsetMin"] = Vector2Row(rect.offsetMin),
                ["offsetMax"] = Vector2Row(rect.offsetMax),
            };
        }

        internal static Dictionary<string, float> Vector2Row(Vector2 value)
        {
            return new Dictionary<string, float>
            {
                ["x"] = value.x,
                ["y"] = value.y,
            };
        }

        internal static Dictionary<string, float>? Vector2Row(object? value)
        {
            return value is Vector2 vector ? Vector2Row(vector) : null;
        }

        internal static Dictionary<string, float>? Vector3Row(object? value)
        {
            if (value is not Vector3 vector)
            {
                return null;
            }

            return new Dictionary<string, float>
            {
                ["x"] = vector.x,
                ["y"] = vector.y,
                ["z"] = vector.z,
            };
        }

        internal static Dictionary<string, float> Vector4Row(Vector4 value)
        {
            return new Dictionary<string, float>
            {
                ["left"] = value.x,
                ["bottom"] = value.y,
                ["right"] = value.z,
                ["top"] = value.w,
            };
        }

        internal static Dictionary<string, float> ColorRow(Color value)
        {
            return new Dictionary<string, float>
            {
                ["r"] = value.r,
                ["g"] = value.g,
                ["b"] = value.b,
                ["a"] = value.a,
            };
        }
    }
}
