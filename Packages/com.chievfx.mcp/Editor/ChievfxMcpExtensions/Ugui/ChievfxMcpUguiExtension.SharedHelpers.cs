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
using static Chievfx.Mcp.Extensions.Ugui.UguiResourcesAndRows;
using static Chievfx.Mcp.Extensions.Ugui.UguiRuntimeControlHelpers;
using static Chievfx.Mcp.Extensions.Ugui.UguiRuntimeHelpers;
using static Chievfx.Mcp.Extensions.Ugui.UguiRuntimeTools;
using static Chievfx.Mcp.Extensions.Ugui.UguiSchemas;

namespace Chievfx.Mcp.Extensions.Ugui
{
    internal static class UguiSharedHelpers
    {
        internal static bool IsEnabledComponent(Component component)
        {
            return component is not Behaviour behaviour || behaviour.enabled;
        }

        internal static object? GetMemberValue(object target, string memberName)
        {
            var type = target.GetType();
            var property = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance);
            if (property != null)
            {
                return property.GetValue(target);
            }

            return type.GetField(memberName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target);
        }

        internal static string[] ScreenshotReviewHints()
        {
            return new[]
            {
                "Use screenshot-game-view after scene save or view focus to review actual Canvas rendering.",
                "If Screen Space Overlay UI is missing from screenshot-game-view evidence, temporarily switch the Canvas to Screen Space Camera with the capture camera, enter Play Mode if the UI only renders there, or use screenshot-editor-window on the visible Game View.",
                "Use screenshot-editor-window for Scene/Game view framing if layout depends on editor viewport.",
                "Use ugui-runtime-probe-screen-position in Play Mode to inspect the top-to-bottom uGUI hit stack before runtime interactions.",
            };
        }

        internal static Dictionary<string, object?> CreateEnvelope(string uri, UguiDependencyStatus status)
        {
            return new Dictionary<string, object?>();
        }

        internal static Dictionary<string, object?> ReadStatusResource(string uri, UguiDependencyStatus status)
        {
            var result = new Dictionary<string, object?>
            {
                ["context"] = ChievfxMcpUiStatusHelpers.DescribeEditorContext(),
            };
            if (!status.Available)
            {
                result["reason"] = status.Reason;
                return result;
            }

            result["ugui"] = ChievfxMcpUiStatusHelpers.DescribePackageCapability(
                status.PackageName,
                status.PackageVersion,
                status.PackageSource,
                true);
            if (status.TmpConfigured)
            {
                result["textMeshPro"] = string.IsNullOrWhiteSpace(status.TmpPackageVersion)
                    ? new Dictionary<string, object?> { ["loaded"] = true }
                    : new Dictionary<string, object?> { ["loaded"] = true, ["version"] = status.TmpPackageVersion };
            }
            else
            {
                result["textMeshPro"] = new Dictionary<string, object?> { ["loaded"] = false };
            }

            result["inputModule"] = new Dictionary<string, object?>
            {
                ["standaloneAvailable"] = status.StandaloneInputModuleType != null,
                ["inputSystemAvailable"] = status.InputSystemUiInputModuleType != null,
                ["prefersInputSystem"] = ShouldPreferInputSystemUiModule(),
            };
            result["currentHierarchy"] = ChievfxMcpUiStatusHelpers.DescribeUguiHierarchy(
                status.CanvasType,
                status.EventSystemType,
                status.TmpTextType);
            return result;
        }

        internal static Dictionary<string, object?> CreateToolEnvelope(string operation)
        {
            return new Dictionary<string, object?>();
        }

        internal static Dictionary<string, object?> CreateUnavailable(string uri, UguiDependencyStatus status, string? reason = null)
        {
            return new Dictionary<string, object?>
            {
                ["reason"] = reason ?? status.Reason,
            };
        }

        internal static UguiDependencyStatus GetDependencyStatus()
        {
            var defaultControls = FindType("UnityEngine.UI.DefaultControls");
            var canvasType = FindType("UnityEngine.Canvas");
            var canvasScalerType = FindType("UnityEngine.UI.CanvasScaler");
            var graphicRaycasterType = FindType("UnityEngine.UI.GraphicRaycaster");
            var packageInfo = TryFindPackageInfo("Packages/com.unity.ugui/package.json");
            var tmpPackageInfo = TryFindPackageInfo("Packages/com.unity.textmeshpro/package.json");
            var tmpTextType = FindType("TMPro.TextMeshProUGUI");
            var available = defaultControls != null
                && canvasType != null
                && canvasScalerType != null
                && graphicRaycasterType != null;
            var reason = available
                ? "com.unity.ugui is installed and UnityEngine.UI authoring types are loaded."
                : "com.unity.ugui is not installed, not compiled, or not loaded; uGUI authoring tools are unavailable.";
            return new UguiDependencyStatus(
                available,
                reason,
                packageInfo?.name ?? "com.unity.ugui",
                packageInfo?.version ?? string.Empty,
                packageInfo?.source.ToString() ?? string.Empty,
                defaultControls != null,
                UguiVersionDefineActive,
                defaultControls,
                canvasType,
                canvasScalerType,
                graphicRaycasterType,
                FindType("UnityEngine.EventSystems.EventSystem"),
                FindType("UnityEngine.EventSystems.BaseInputModule"),
                FindType("UnityEngine.EventSystems.StandaloneInputModule"),
                FindType("UnityEngine.InputSystem.UI.InputSystemUIInputModule"),
                FindType("UnityEngine.UI.Image"),
                FindType("UnityEngine.UI.Button"),
                FindType("UnityEngine.UI.Slider"),
                FindType("UnityEngine.UI.Toggle"),
                FindType("UnityEngine.UI.Scrollbar"),
                FindType("UnityEngine.UI.ScrollRect"),
                FindType("UnityEngine.UI.Dropdown"),
                FindType("TMPro.TMP_Dropdown"),
                FindType("UnityEngine.UI.InputField"),
                FindType("UnityEngine.UI.Selectable"),
                FindType("UnityEngine.UI.Graphic"),
                FindType("UnityEngine.EventSystems.PointerEventData"),
                FindType("UnityEngine.EventSystems.RaycastResult"),
                FindType("UnityEngine.EventSystems.IPointerClickHandler"),
                tmpPackageInfo?.version ?? string.Empty,
                tmpPackageInfo?.source.ToString() ?? string.Empty,
                tmpTextType);
        }

        internal static bool ShouldPreferInputSystemUiModule()
        {
            if (preferInputSystemUiModuleOverrideForTests.HasValue)
            {
                return preferInputSystemUiModuleOverrideForTests.Value;
            }

            var activeInputHandling = typeof(PlayerSettings).GetProperty("activeInputHandling", BindingFlags.Public | BindingFlags.Static);
            var value = activeInputHandling?.GetValue(null)?.ToString();
            var normalized = value == null ? string.Empty : new string(value.Where(character => !char.IsWhiteSpace(character)).ToArray());
            return normalized.IndexOf("InputSystem", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static UnityEditor.PackageManager.PackageInfo? TryFindPackageInfo(string packageJsonPath)
        {
            try
            {
                return UnityEditor.PackageManager.PackageInfo.FindForAssetPath(packageJsonPath);
            }
            catch
            {
                return null;
            }
        }

        internal static Component[] FindCanvases(UguiDependencyStatus status, bool includeInactive)
        {
            if (status.CanvasType == null)
            {
                return Array.Empty<Component>();
            }

            try
            {
                var context = GameObjectBridgeService.GetGameObjectQueryContext();
                return context.Roots
                    .SelectMany(root => GetComponentsInChildren(root, status.CanvasType, includeInactive))
                    .ToArray();
            }
            catch
            {
                return Array.Empty<Component>();
            }
        }

        internal static GameObject? FindFirstCanvas(UguiDependencyStatus status)
        {
            return FindCanvases(status, includeInactive: true).FirstOrDefault()?.gameObject;
        }

        internal static GameObject? FindCanvasByName(UguiDependencyStatus status, string name)
        {
            return FindCanvases(status, includeInactive: true)
                .Select(canvas => canvas.gameObject)
                .FirstOrDefault(go => string.Equals(go.name, name, StringComparison.Ordinal));
        }

        internal static GameObject ResolveRequiredGameObject(JToken args, string pathKey, string instanceIdKey)
        {
            return ResolveGameObject(args, pathKey, instanceIdKey)
                ?? throw new ArgumentException($"Could not resolve GameObject from '{pathKey}' or '{instanceIdKey}'.");
        }

        internal static GameObject? ResolveGameObject(JToken args, string pathKey, string instanceIdKey)
        {
            var instanceId = ReadInt(args, instanceIdKey, 0);
            return ResolveGameObject(instanceId) ?? ResolveGameObject(ReadString(args, pathKey));
        }

        internal static GameObject? ResolveGameObject(int instanceId)
        {
            return instanceId == 0 ? null : UnityObjectIdentity.LegacyInstanceIdToObject(instanceId) as GameObject;
        }

        internal static GameObject? ResolveGameObject(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (string.Equals(root.name, path, StringComparison.Ordinal)
                    || string.Equals(GetTransformPath(root.transform), path, StringComparison.Ordinal))
                {
                    return root;
                }

                var current = FindChildByPath(root.transform, path!);
                if (current != null)
                {
                    return current.gameObject;
                }
            }

            return GameObject.Find(path);
        }

        internal static Transform? FindChildByPath(Transform root, string path)
        {
            var normalized = path.StartsWith(root.name + "/", StringComparison.Ordinal)
                ? path.Substring(root.name.Length + 1)
                : path;
            return root.Find(normalized);
        }

        internal static string GetTransformPath(Transform transform)
        {
            var names = new Stack<string>();
            var current = transform;
            while (current != null)
            {
                names.Push(current.name);
                current = current.parent;
            }

            return string.Join("/", names);
        }

        internal static void MarkDirty(GameObject target)
        {
            EditorUtility.SetDirty(target);
            if (!EditorApplication.isPlayingOrWillChangePlaymode && target.scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(target.scene);
            }
        }

        internal static Dictionary<string, object?> CreateImageRow(Component image)
        {
            var sprite = GetPropertyValue(image, "sprite") as Sprite;
            var color = GetPropertyValue(image, "color");
            if (color is not Color)
            {
                var serialized = new SerializedObject(image);
                var colorProperty = serialized.FindProperty("m_Color");
                if (colorProperty != null)
                {
                    color = colorProperty.colorValue;
                }
            }

            return new Dictionary<string, object?>
            {
                ["imageType"] = Convert.ToString(GetPropertyValue(image, "type"), CultureInfo.InvariantCulture),
                ["sprite"] = sprite == null ? null : CreateSpriteAssetRow(sprite),
                ["color"] = color is Color colorValue ? ColorRow(colorValue) : null,
                ["raycastTarget"] = GetPropertyValue(image, "raycastTarget"),
                ["preserveAspect"] = GetPropertyValue(image, "preserveAspect"),
            };
        }

        internal static Dictionary<string, object?> CreateSpriteAssetRow(Sprite sprite)
        {
            var path = AssetDatabase.GetAssetPath(sprite);
            return new Dictionary<string, object?>
            {
                ["name"] = sprite.name,
                ["path"] = path,
                ["guid"] = string.IsNullOrWhiteSpace(path) ? string.Empty : AssetDatabase.AssetPathToGUID(path),
                ["pixelsPerUnit"] = sprite.pixelsPerUnit,
                ["border"] = Vector4Row(sprite.border),
            };
        }

        internal static Sprite? ResolveSprite(JToken args, List<string> warnings)
        {
            var path = ResolveAssetPath(args, required: true);
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Could not resolve spritePath or spriteGuid to an asset path.");
            }

            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite == null)
            {
                warnings.Add($"Asset '{path}' did not load as Sprite.");
            }

            return sprite;
        }

        internal static Sprite? LoadSpriteAtPath(string assetPath)
        {
            return AssetDatabase.LoadAssetAtPath<Sprite>(assetPath)
                ?? AssetDatabase.LoadAllAssetsAtPath(assetPath).OfType<Sprite>().FirstOrDefault();
        }

        internal static string? ResolveAssetPath(JToken args, bool required)
        {
            var path = ReadString(args, "spritePath") ?? ReadString(args, "path");
            var guid = ReadString(args, "spriteGuid") ?? ReadString(args, "guid");
            var resolved = !string.IsNullOrWhiteSpace(guid)
                ? AssetDatabase.GUIDToAssetPath(guid)
                : ResolveAssetPath(path);
            if (required && string.IsNullOrWhiteSpace(resolved))
            {
                throw new ArgumentException("spritePath/path or spriteGuid/guid is required.");
            }

            return resolved;
        }

        internal static string? ResolveAssetPath(string? guidOrPath)
        {
            if (string.IsNullOrWhiteSpace(guidOrPath))
            {
                return null;
            }

            if (guidOrPath!.StartsWith("Assets/", StringComparison.Ordinal)
                || guidOrPath.StartsWith("Packages/", StringComparison.Ordinal))
            {
                return guidOrPath;
            }

            var path = AssetDatabase.GUIDToAssetPath(guidOrPath);
            return string.IsNullOrWhiteSpace(path) ? guidOrPath : path;
        }

        internal static string ChooseAutoImageType(Sprite? sprite)
        {
            return sprite != null && !IsZeroBorder(sprite.border) ? "Sliced" : "Simple";
        }

        internal static void AddSlicedSpriteWarnings(Sprite? sprite, string imageType, List<string> warnings)
        {
            if (!string.Equals(imageType, "Sliced", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(imageType, "Tiled", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (sprite == null)
            {
                warnings.Add($"{imageType} needs a Sprite with non-zero border.");
                return;
            }

            if (IsZeroBorder(sprite.border))
            {
                warnings.Add($"{imageType} needs non-zero sprite border; use Simple for plain panels.");
            }
        }

        internal static bool IsZeroBorder(Vector4 border)
        {
            return Mathf.Approximately(border.x, 0f)
                && Mathf.Approximately(border.y, 0f)
                && Mathf.Approximately(border.z, 0f)
                && Mathf.Approximately(border.w, 0f);
        }

        internal static SpriteMeshType GetImporterSpriteMeshType(TextureImporter importer)
        {
            var settings = new TextureImporterSettings();
            importer.ReadTextureSettings(settings);
            return settings.spriteMeshType;
        }

        internal static void SetImporterSpriteMeshType(TextureImporter importer, SpriteMeshType meshType)
        {
            var settings = new TextureImporterSettings();
            importer.ReadTextureSettings(settings);
            settings.spriteMeshType = meshType;
            importer.SetTextureSettings(settings);
        }

        internal static Type? FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = assembly.GetType(fullName, throwOnError: false);
                    if (type != null)
                    {
                        return type;
                    }
                }
                catch
                {
                    continue;
                }
            }

            return Type.GetType(fullName, throwOnError: false);
        }

        internal static UnityEngine.Object[] FindObjectsOfType(Type type)
        {
            return Resources.FindObjectsOfTypeAll(type)
                .Where(obj => obj is Component component && component.gameObject.scene.IsValid())
                .ToArray();
        }

        internal static bool HasComponent(GameObject target, Type? componentType)
        {
            return componentType != null && target.GetComponent(componentType) != null;
        }

        internal static Component[] GetComponentsInChildren(GameObject target, Type? componentType, bool includeInactive)
        {
            if (componentType == null)
            {
                return Array.Empty<Component>();
            }

            return target.GetComponentsInChildren(componentType, includeInactive)
                .OfType<Component>()
                .ToArray();
        }

        internal static Component[] GetInputModules(GameObject eventSystem, UguiDependencyStatus status)
        {
            if (status.BaseInputModuleType != null)
            {
                return eventSystem.GetComponents(status.BaseInputModuleType)
                    .OfType<Component>()
                    .ToArray();
            }

            var modules = new List<Component>();
            AddInputModules(modules, eventSystem, status.StandaloneInputModuleType);
            AddInputModules(modules, eventSystem, status.InputSystemUiInputModuleType);
            return modules.ToArray();
        }

        internal static void AddInputModules(List<Component> modules, GameObject eventSystem, Type? moduleType)
        {
            if (moduleType == null)
            {
                return;
            }

            foreach (var module in eventSystem.GetComponents(moduleType).OfType<Component>())
            {
                if (!modules.Any(existing => ReferenceEquals(existing, module)))
                {
                    modules.Add(module);
                }
            }
        }

        internal static void SetProperty(object target, string propertyName, object value)
        {
            var property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (property == null)
            {
                return;
            }

            if (value is Color color && property.PropertyType == typeof(Color32))
            {
                property.SetValue(target, (Color32)color);
                return;
            }

            if (value is Color32 color32 && property.PropertyType == typeof(Color))
            {
                property.SetValue(target, (Color)color32);
                return;
            }

            property.SetValue(target, value);
        }

        internal static void SetGraphicColor(Component graphic, Color color)
        {
            var serialized = new SerializedObject(graphic);
            var colorProperty = serialized.FindProperty("m_Color");
            if (colorProperty != null)
            {
                colorProperty.colorValue = color;
                serialized.ApplyModifiedProperties();
            }
            else
            {
                SetProperty(graphic, "color", color);
            }
        }

        internal static object? GetPropertyValue(object target, string propertyName)
        {
            var property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            return property?.GetValue(target);
        }

        internal static void SetEnumProperty(object target, string propertyName, string value)
        {
            var property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new ArgumentException($"Property '{propertyName}' was not found on {target.GetType().Name}.");
            if (!property.PropertyType.IsEnum)
            {
                throw new ArgumentException($"Property '{propertyName}' is not an enum on {target.GetType().Name}.");
            }

            var parsed = Enum.Parse(property.PropertyType, value, ignoreCase: true);
            property.SetValue(target, parsed);
        }

        internal static string NormalizeTmpAlignment(string value)
        {
            var normalized = new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            return normalized switch
            {
                "" => "TopLeft",
                "center" or "middlecenter" or "midcenter" or "centermiddle" => "Center",
                "left" or "middleleft" or "midleft" or "leftmiddle" => "Left",
                "right" or "middleright" or "midright" or "rightmiddle" => "Right",
                "top" or "topcenter" or "centertop" => "Top",
                "bottom" or "bottomcenter" or "centerbottom" => "Bottom",
                "upperleft" => "TopLeft",
                "upperright" => "TopRight",
                "lowerleft" => "BottomLeft",
                "lowerright" => "BottomRight",
                "topleft" => "TopLeft",
                "topright" => "TopRight",
                "bottomleft" => "BottomLeft",
                "bottomright" => "BottomRight",
                _ => value.Replace(" ", string.Empty).Replace("-", string.Empty).Replace("_", string.Empty),
            };
        }

        internal static string DefaultElementName(string elementType)
        {
            return elementType switch
            {
                "panel" => "Panel",
                "image" => "Image",
                "button" => "Button",
                "slider" => "Slider",
                "text" => "Text",
                _ => "UI Element",
            };
        }

        internal static string? ReadString(JToken token, string name)
        {
            return token[name]?.Type == JTokenType.String ? token[name]!.Value<string>() : null;
        }

        internal static int ReadInt(JToken token, string name, int fallback)
        {
            return token[name]?.Type == JTokenType.Integer ? token[name]!.Value<int>() : fallback;
        }

        internal static bool ReadBool(JToken token, string name, bool fallback)
        {
            return token[name]?.Type == JTokenType.Boolean ? token[name]!.Value<bool>() : fallback;
        }

        internal static float ReadFloat(JToken token, string name, float fallback)
        {
            var value = token[name];
            return value != null && (value.Type == JTokenType.Float || value.Type == JTokenType.Integer)
                ? value.Value<float>()
                : fallback;
        }

        internal static Vector2 ReadVector2(JToken token, string name, Vector2 fallback)
        {
            if (token[name] is not JObject obj)
            {
                return fallback;
            }

            return new Vector2(ReadFloat(obj, "x", fallback.x), ReadFloat(obj, "y", fallback.y));
        }

        internal static Vector2? ReadOptionalVector2(JToken token, string name)
        {
            return token[name] is JObject obj
                ? new Vector2(ReadFloat(obj, "x", 0f), ReadFloat(obj, "y", 0f))
                : null;
        }

        internal static Vector4 ReadVector4(JObject obj, Vector4 fallback)
        {
            return new Vector4(
                ReadFloat(obj, "left", ReadFloat(obj, "x", fallback.x)),
                ReadFloat(obj, "bottom", ReadFloat(obj, "y", fallback.y)),
                ReadFloat(obj, "right", ReadFloat(obj, "z", fallback.z)),
                ReadFloat(obj, "top", ReadFloat(obj, "w", fallback.w)));
        }

        internal static Color ReadColor(JObject obj, Color fallback)
        {
            var channelNames = obj["a"] != null
                ? new[] { "r", "g", "b", "a" }
                : new[] { "r", "g", "b" };
            var useByteChannels = channelNames.All(name => obj[name]?.Type == JTokenType.Integer)
                && channelNames.Any(name => obj[name]!.Value<int>() > 1);
            if (useByteChannels)
            {
                return new Color(
                    Mathf.Clamp(ReadInt(obj, "r", Mathf.RoundToInt(fallback.r * 255f)), 0, 255) / 255f,
                    Mathf.Clamp(ReadInt(obj, "g", Mathf.RoundToInt(fallback.g * 255f)), 0, 255) / 255f,
                    Mathf.Clamp(ReadInt(obj, "b", Mathf.RoundToInt(fallback.b * 255f)), 0, 255) / 255f,
                    Mathf.Clamp(ReadInt(obj, "a", Mathf.RoundToInt(fallback.a * 255f)), 0, 255) / 255f);
            }

            return new Color(
                ReadFloat(obj, "r", fallback.r),
                ReadFloat(obj, "g", fallback.g),
                ReadFloat(obj, "b", fallback.b),
                ReadFloat(obj, "a", fallback.a));
        }

        internal static Color ReadColor(JToken token, Color fallback)
        {
            if (token is JObject obj)
            {
                return ReadColor(obj, fallback);
            }

            if (token.Type != JTokenType.String)
            {
                return fallback;
            }

            var hex = token.Value<string>()?.Trim().TrimStart('#') ?? string.Empty;
            if (hex.Length != 6 && hex.Length != 8)
            {
                return fallback;
            }

            if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                return fallback;
            }

            var r = hex.Length == 6 ? (value >> 16) & 0xff : (value >> 24) & 0xff;
            var g = hex.Length == 6 ? (value >> 8) & 0xff : (value >> 16) & 0xff;
            var b = hex.Length == 6 ? value & 0xff : (value >> 8) & 0xff;
            var a = hex.Length == 6 ? 0xff : value & 0xff;
            return new Color(r / 255f, g / 255f, b / 255f, a / 255f);
        }

        internal static string EncodeSegment(string value)
        {
            return Uri.EscapeDataString(value ?? string.Empty);
        }

        internal static string DecodeSegment(string value)
        {
            return Uri.UnescapeDataString(value ?? string.Empty);
        }
    }
}
