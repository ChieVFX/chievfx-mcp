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
using static Chievfx.Mcp.Extensions.Ugui.UguiLayoutHelpers;
using static Chievfx.Mcp.Extensions.Ugui.UguiResourcesAndRows;
using static Chievfx.Mcp.Extensions.Ugui.UguiRuntimeControlHelpers;
using static Chievfx.Mcp.Extensions.Ugui.UguiRuntimeHelpers;
using static Chievfx.Mcp.Extensions.Ugui.UguiRuntimeTools;
using static Chievfx.Mcp.Extensions.Ugui.UguiSchemas;
using static Chievfx.Mcp.Extensions.Ugui.UguiSharedHelpers;

namespace Chievfx.Mcp.Extensions.Ugui
{
    internal static class UguiElementHelpers
    {
        internal static GameObject CreateElementObject(string elementType, UguiDependencyStatus status, List<string> warnings, string textBackend)
        {
            if (elementType == "text" && textBackend == "tmp" && status.TmpTextType != null)
            {
                var target = new GameObject(DefaultElementName(elementType), typeof(RectTransform));
                target.SetActive(false);
                var text = target.AddComponent(status.TmpTextType);
                ConfigureTmpTextDefaults(text);
                target.SetActive(true);
                return target;
            }

            var methodName = elementType switch
            {
                "panel" => "CreatePanel",
                "image" => "CreateImage",
                "button" => "CreateButton",
                "slider" => "CreateSlider",
                "text" => "CreateText",
                "empty" => string.Empty,
                _ => throw new ArgumentException($"Unsupported uGUI elementType '{elementType}'."),
            };

            if (string.IsNullOrEmpty(methodName) || status.DefaultControlsType == null)
            {
                return new GameObject(DefaultElementName(elementType), typeof(RectTransform));
            }

            var resources = CreateDefaultControlsResources(status.DefaultControlsType);
            var method = status.DefaultControlsType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
            if (method == null)
            {
                warnings.Add($"UnityEngine.UI.DefaultControls.{methodName} was unavailable; created plain RectTransform fallback.");
                return new GameObject(DefaultElementName(elementType), typeof(RectTransform));
            }

            return (GameObject)method.Invoke(null, new[] { resources });
        }

        internal static GameObject ResolveUguiParent(JToken args, UguiDependencyStatus status, List<string> warnings)
        {
            var canvas = ResolveGameObject(args, "canvasPath", "canvasInstanceId") ?? FindFirstCanvas(status);
            if (canvas == null)
            {
                canvas = (EnsureCanvas(new JObject { ["name"] = "Canvas" }, status)["canvas"] as Dictionary<string, object?>)?["path"] is string path
                    ? ResolveGameObject(path)
                    : FindFirstCanvas(status);
                warnings.Add("Created fallback Canvas because no Canvas existed.");
            }

            var parent = ResolveGameObject(args, "parentPath", "parentInstanceId") ?? canvas;
            return parent ?? throw new InvalidOperationException("Could not resolve or create uGUI parent.");
        }

        internal static bool TryReadImageArgs(JToken args, out JToken imageArgs)
        {
            imageArgs = args["image"] is JObject imageObject ? imageObject : args;
            return args["image"] is JObject
                || args["spritePath"] != null
                || args["spriteGuid"] != null
                || args["color"] != null
                || args["raycastTarget"] != null
                || args["preserveAspect"] != null
                || args["imageType"] != null;
        }

        internal static GameObject CreateProgressbar(UguiDependencyStatus status, List<string> warnings)
        {
            var root = new GameObject(DefaultElementName("progressbar"), typeof(RectTransform));
            var background = new GameObject("Background", typeof(RectTransform));
            background.transform.SetParent(root.transform, false);
            var fill = new GameObject("Fill", typeof(RectTransform));
            fill.transform.SetParent(root.transform, false);

            ApplyRect(background.GetComponent<RectTransform>(), ReadRectArgs(new JObject { ["preset"] = "fill" }), warnings);
            ApplyRect(fill.GetComponent<RectTransform>(), ReadRectArgs(new JObject
            {
                ["anchorMin"] = new JObject { ["x"] = 0, ["y"] = 0 },
                ["anchorMax"] = new JObject { ["x"] = 1, ["y"] = 1 },
                ["offsetMin"] = new JObject { ["x"] = 0, ["y"] = 0 },
                ["offsetMax"] = new JObject { ["x"] = 0, ["y"] = 0 },
            }), warnings);

            if (status.ImageType != null)
            {
                var backgroundImage = EnsureComponent(background, status.ImageType);
                var fillImage = EnsureComponent(fill, status.ImageType);
                if (backgroundImage != null)
                {
                    SetGraphicColor(backgroundImage, new Color(0.25f, 0.25f, 0.25f, 1f));
                }

                if (fillImage != null)
                {
                    SetGraphicColor(fillImage, new Color(0.2f, 0.55f, 1f, 1f));
                }
            }

            return root;
        }

        internal static void ApplyControlValue(GameObject target, string controlType, JToken args, UguiDependencyStatus status, List<string> warnings)
        {
            if (args["value"] == null)
            {
                return;
            }

            var value = Mathf.Clamp01(ReadFloat(args, "value", 0f));
            if (controlType == "slider" && status.SliderType != null)
            {
                var slider = target.GetComponent(status.SliderType) as Component;
                if (slider != null)
                {
                    SetProperty(slider, "value", value);
                }

                return;
            }

            if (controlType == "progressbar")
            {
                var fill = target.transform.Find("Fill") as Transform;
                var rect = fill?.GetComponent<RectTransform>();
                if (rect != null)
                {
                    rect.anchorMax = new Vector2(value, rect.anchorMax.y);
                }
            }
        }

        internal static object CreateDefaultControlsResources(Type defaultControlsType)
        {
            var resourcesType = defaultControlsType.GetNestedType("Resources", BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("UnityEngine.UI.DefaultControls.Resources type is unavailable.");
            return Activator.CreateInstance(resourcesType);
        }

        internal static void ConfigureTmpTextDefaults(Component text)
        {
            var fontProperty = text.GetType().GetProperty("font", BindingFlags.Public | BindingFlags.Instance);
            if (fontProperty == null || fontProperty.GetValue(text) != null)
            {
                return;
            }

            var settingsType = FindType("TMPro.TMP_Settings");
            var defaultFont = settingsType
                ?.GetProperty("defaultFontAsset", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null);
            if (defaultFont != null)
            {
                fontProperty.SetValue(text, defaultFont);
            }
        }

        internal static void NormalizeElement(GameObject target, string elementType, JToken args, UguiDependencyStatus status, List<string> warnings)
        {
            if (elementType == "text" || elementType == "button")
            {
                var textValue = ReadString(args, "text");
                if (!string.IsNullOrEmpty(textValue))
                {
                    foreach (var textComponent in target.GetComponentsInChildren<Component>(true).Where(IsTextComponent))
                    {
                        SetProperty(textComponent, "text", textValue!);
                    }
                }
            }

            if (elementType == "image" && status.ImageType != null)
            {
                EnsureComponent(target, status.ImageType);
            }

            if (elementType == "button" && status.ButtonType != null && target.GetComponent(status.ButtonType) == null)
            {
                EnsureComponent(target, status.ButtonType);
                warnings.Add("Button component added after DefaultControls fallback.");
            }

            if (elementType == "slider" && status.SliderType != null && target.GetComponent(status.SliderType) == null)
            {
                EnsureComponent(target, status.SliderType);
                warnings.Add("Slider component added after DefaultControls fallback.");
            }
        }

        internal static string ResolveTextBackend(JToken args, UguiDependencyStatus status, List<string>? warnings)
        {
            return ResolveTextBackend(ReadString(args, "textBackend") ?? "auto", status.TmpConfigured, warnings);
        }

        internal static string ResolveTextBackend(string requestedBackend, bool tmpConfigured, List<string>? warnings)
        {
            var requested = (requestedBackend ?? "auto").Trim().ToLowerInvariant();
            if (requested == "legacy")
            {
                return "legacy";
            }

            if (requested != "auto" && requested != "tmp")
            {
                warnings?.Add($"Unknown textBackend '{requested}'; used auto.");
                requested = "auto";
            }

            if (tmpConfigured)
            {
                return "tmp";
            }

            if (requested == "tmp")
            {
                warnings?.Add("TMP text backend unavailable; created legacy UnityEngine.UI.Text. Install and configure com.unity.textmeshpro to use TMPro.TextMeshProUGUI.");
            }

            return "legacy";
        }

        internal static bool IsTextComponent(Component component)
        {
            var type = component.GetType();
            return string.Equals(type.FullName, "UnityEngine.UI.Text", StringComparison.Ordinal)
                || string.Equals(type.FullName, "TMPro.TextMeshProUGUI", StringComparison.Ordinal)
                || string.Equals(type.Name, "TextMeshProUGUI", StringComparison.Ordinal);
        }

        internal static Component? EnsureComponent(GameObject target, Type? componentType)
        {
            if (componentType == null)
            {
                return null;
            }

            var existing = target.GetComponent(componentType);
            return existing != null ? existing : target.AddComponent(componentType);
        }

        internal static Component EnsureRequiredComponent(GameObject target, Type? componentType, string displayName)
        {
            return EnsureComponent(target, componentType)
                ?? throw new InvalidOperationException($"{displayName} type is unavailable.");
        }

        internal static GameObject? EnsureEventSystem(UguiDependencyStatus status, List<string> warnings)
        {
            if (status.EventSystemType == null)
            {
                warnings.Add("EventSystem type unavailable; skipped EventSystem fallback.");
                return null;
            }

            var existing = FindObjectsOfType(status.EventSystemType).FirstOrDefault() as Component;
            if (existing != null)
            {
                EnsureEventSystemInputModule(existing.gameObject, status, warnings);
                return existing.gameObject;
            }

            var eventSystem = new GameObject("EventSystem");
            Undo.RegisterCreatedObjectUndo(eventSystem, "ChievFX MCP Create EventSystem");
            eventSystem.AddComponent(status.EventSystemType);
            EnsureEventSystemInputModule(eventSystem, status, warnings);

            warnings.Add("Created fallback EventSystem for uGUI input routing.");
            return eventSystem;
        }

        internal static void EnsureEventSystemInputModule(GameObject eventSystem, UguiDependencyStatus status, List<string> warnings)
        {
            var existingInputSystemModule = status.InputSystemUiInputModuleType == null
                ? null
                : eventSystem.GetComponent(status.InputSystemUiInputModuleType) as Component;
            var prefersInputSystemModule = preferInputSystemUiModuleOverrideForTests.HasValue
                ? ShouldPreferInputSystemUiModule()
                : existingInputSystemModule != null || ShouldPreferInputSystemUiModule();
            var preferredModuleType = prefersInputSystemModule
                ? status.InputSystemUiInputModuleType
                : status.StandaloneInputModuleType ?? status.InputSystemUiInputModuleType;

            if (preferredModuleType == null)
            {
                if (prefersInputSystemModule && status.StandaloneInputModuleType != null)
                {
                    if (RemoveAllInputModules(eventSystem, status, warnings))
                    {
                        MarkDirty(eventSystem);
                    }

                    warnings.Add("Project prefers Input System UI input, but InputSystemUIInputModule is unavailable; skipped StandaloneInputModule to avoid Play Mode input exceptions.");
                    return;
                }

                warnings.Add("No supported EventSystem input module type was available.");
                return;
            }

            var changed = false;
            var preferredModule = eventSystem.GetComponent(preferredModuleType) as Component;
            if (preferredModule == null)
            {
                preferredModule = eventSystem.AddComponent(preferredModuleType);
                warnings.Add($"Added {preferredModuleType.Name} to EventSystem for project input routing.");
                changed = true;
            }

            changed |= RemoveConflictingInputModules(eventSystem, status, preferredModule, preferredModuleType, warnings);
            if (changed)
            {
                MarkDirty(eventSystem);
            }
        }

        internal static bool RemoveConflictingInputModules(
            GameObject eventSystem,
            UguiDependencyStatus status,
            Component preferredModule,
            Type preferredModuleType,
            List<string> warnings)
        {
            var changed = false;
            var keptPreferred = false;
            foreach (var module in GetInputModules(eventSystem, status))
            {
                if (module == null)
                {
                    continue;
                }

                if (!keptPreferred && (ReferenceEquals(module, preferredModule) || module.GetType() == preferredModuleType))
                {
                    keptPreferred = true;
                    continue;
                }

                var removedTypeName = module.GetType().Name;
                Undo.DestroyObjectImmediate(module);
                warnings.Add($"Removed conflicting {removedTypeName} from EventSystem so only {preferredModuleType.Name} remains.");
                changed = true;
            }

            return changed;
        }

        internal static bool RemoveAllInputModules(GameObject eventSystem, UguiDependencyStatus status, List<string> warnings)
        {
            var changed = false;
            foreach (var module in GetInputModules(eventSystem, status))
            {
                if (module == null)
                {
                    continue;
                }

                var removedTypeName = module.GetType().Name;
                Undo.DestroyObjectImmediate(module);
                warnings.Add($"Removed unsupported {removedTypeName} from EventSystem.");
                changed = true;
            }

            return changed;
        }

        internal static void EnsureTmpAvailable(UguiDependencyStatus status)
        {
            if (status.TmpTextType == null)
            {
                throw new InvalidOperationException("TextMeshProUGUI type is unavailable. Install/configure com.unity.textmeshpro first.");
            }
        }

        internal static Component[] ResolveOrCreateTmpTextTargets(JToken args, UguiDependencyStatus status, List<string> warnings, out int createdCount)
        {
            EnsureTmpAvailable(status);
            createdCount = 0;
            var explicitTargets = ResolveExplicitUiTargets(args);
            if (explicitTargets.Length == 0)
            {
                return Array.Empty<Component>();
            }

            var placement = (ReadString(args, "placement") ?? "same-object").Trim();
            if (!string.Equals(placement, "same-object", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(placement, "child", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("placement must be 'same-object' or 'child'.");
            }

            var childName = ReadString(args, "childName");
            if (string.IsNullOrWhiteSpace(childName))
            {
                childName = "Label";
            }

            var results = new List<Component>();
            foreach (var target in explicitTargets.Distinct())
            {
                var wasCreated = false;
                Component? text;
                if (string.Equals(placement, "child", StringComparison.OrdinalIgnoreCase))
                {
                    text = EnsureTmpTextChild(target, status.TmpTextType!, childName!, out wasCreated);
                }
                else
                {
                    text = TryEnsureTmpTextOnTarget(target, status.TmpTextType!, warnings, out wasCreated);
                }

                if (text == null)
                {
                    text = EnsureTmpTextChild(target, status.TmpTextType!, childName!, out var fallbackCreated);
                    warnings.Add($"Could not add TextMeshProUGUI directly to '{GetTransformPath(target.transform)}'; created/used child '{childName}' instead.");
                    createdCount += fallbackCreated ? 1 : 0;
                }
                else
                {
                    createdCount += wasCreated ? 1 : 0;
                }

                if (text != null)
                {
                    results.Add(text);
                }
            }

            return results.Distinct().ToArray();
        }

        internal static Component? TryEnsureTmpTextOnTarget(GameObject target, Type tmpTextType, List<string> warnings, out bool created)
        {
            created = false;
            var existing = target.GetComponent(tmpTextType) as Component;
            if (existing != null)
            {
                return existing;
            }

            try
            {
                var text = target.AddComponent(tmpTextType) as Component;
                created = text != null;
                return text;
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not add TextMeshProUGUI to '{GetTransformPath(target.transform)}': {ex.Message}");
                return null;
            }
        }

        internal static Component EnsureTmpTextChild(GameObject target, Type tmpTextType, string childName, out bool created)
        {
            created = false;
            var existingTransform = target.transform.Cast<Transform>().FirstOrDefault(child => child.name == childName);
            var child = existingTransform != null
                ? existingTransform.gameObject
                : new GameObject(childName, typeof(RectTransform));
            if (existingTransform == null)
            {
                Undo.RegisterCreatedObjectUndo(child, "ChievFX MCP Create TextMeshProUGUI Child");
                child.transform.SetParent(target.transform, false);
                var childRect = child.GetComponent<RectTransform>();
                if (childRect != null)
                {
                    childRect.anchorMin = Vector2.zero;
                    childRect.anchorMax = Vector2.one;
                    childRect.offsetMin = Vector2.zero;
                    childRect.offsetMax = Vector2.zero;
                }
                created = true;
            }

            var before = child.GetComponent(tmpTextType);
            var text = before as Component ?? child.AddComponent(tmpTextType) as Component;
            created = created || before == null;
            return text ?? throw new InvalidOperationException("Failed to create TextMeshProUGUI child component.");
        }

        internal static Component[] FindTmpTextComponents(JToken args, UguiDependencyStatus status, bool includeInactive)
        {
            EnsureTmpAvailable(status);
            var explicitTargets = ResolveExplicitUiTargets(args);
            var roots = explicitTargets.Length > 0
                ? explicitTargets
                : FindCanvases(status, includeInactive).Select(canvas => canvas.gameObject).ToArray();
            return roots
                .SelectMany(root => GetComponentsInChildren(root, status.TmpTextType, includeInactive))
                .Where(text => includeInactive || text.gameObject.activeInHierarchy)
                .Distinct()
                .ToArray();
        }

        internal static Dictionary<string, object?> CreateTmpTextRow(Component text)
        {
            var row = new Dictionary<string, object?>
            {
                ["name"] = text.gameObject.name,
                ["path"] = GetTransformPath(text.transform),
                ["instanceId"] = UnityObjectIdentity.GetLegacyInstanceId(text.gameObject),
                ["text"] = Convert.ToString(GetPropertyValue(text, "text"), CultureInfo.InvariantCulture) ?? string.Empty,
                ["font"] = CreateObjectReferenceRow(GetPropertyValue(text, "font") as UnityEngine.Object),
                ["fontSize"] = GetPropertyValue(text, "fontSize"),
                ["fontStyle"] = Convert.ToString(GetPropertyValue(text, "fontStyle"), CultureInfo.InvariantCulture),
                ["alignment"] = Convert.ToString(GetPropertyValue(text, "alignment"), CultureInfo.InvariantCulture),
                ["wrapping"] = GetPropertyValue(text, "enableWordWrapping"),
                ["overflow"] = Convert.ToString(GetPropertyValue(text, "overflowMode"), CultureInfo.InvariantCulture),
                ["color"] = GetPropertyValue(text, "color") is Color color ? ColorRow(color) : null,
                ["outlineWidth"] = GetPropertyValue(text, "outlineWidth"),
                ["outlineColor"] = GetPropertyValue(text, "outlineColor") is Color outlineColor ? ColorRow(outlineColor) : null,
            };
            row["styleKey"] = CreateTmpStyleKey(row);
            return row;
        }

        internal static string CreateTmpStyleKey(Dictionary<string, object?> row)
        {
            var font = row["font"] as Dictionary<string, object?>;
            return string.Join("; ",
                "font:" + (font?["name"] ?? "default"),
                "size:" + FormatCompact(row["fontSize"]),
                "style:" + row["fontStyle"],
                "align:" + row["alignment"],
                "wrap:" + row["wrapping"],
                "overflow:" + row["overflow"],
                "color:" + FormatColor(row["color"]),
                "outline:" + FormatCompact(row["outlineWidth"]) + "/" + FormatColor(row["outlineColor"]));
        }

        internal static string FormatCompact(object? value)
        {
            return value is float f ? f.ToString("0.###", CultureInfo.InvariantCulture) : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        internal static string FormatColor(object? value)
        {
            if (value is not Dictionary<string, float> color)
            {
                return string.Empty;
            }

            return string.Format(CultureInfo.InvariantCulture, "{0:0.###},{1:0.###},{2:0.###},{3:0.###}", color["r"], color["g"], color["b"], color["a"]);
        }

        internal static void WritePrimitiveTexture(string assetPath, string primitiveType, int width, int height, float radius, Color color)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var alpha = PrimitiveAlpha(primitiveType, x + 0.5f, y + 0.5f, width, height, radius);
                    texture.SetPixel(x, y, new Color(color.r, color.g, color.b, color.a * alpha));
                }
            }

            texture.Apply();
            var absolutePath = Path.GetFullPath(Path.Combine(Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty, assetPath));
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath) ?? string.Empty);
            File.WriteAllBytes(absolutePath, texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
        }

        internal static string NormalizePrimitivePngPath(string? rawPath)
        {
            var path = rawPath == null || string.IsNullOrWhiteSpace(rawPath)
                ? "Assets/Testrun/Generated/UguiPrimitive"
                : rawPath.Trim().Replace('\\', '/');
            if (Path.IsPathRooted(path))
            {
                const string assetsMarker = "/Assets/";
                var markerIndex = path.IndexOf(assetsMarker, StringComparison.OrdinalIgnoreCase);
                path = markerIndex >= 0
                    ? "Assets/" + path[(markerIndex + assetsMarker.Length)..]
                    : "Assets/Testrun/Generated/" + Path.GetFileName(path);
            }
            else if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                path = "Assets/" + path.TrimStart('/');
            }

            var directory = Path.GetDirectoryName(path)?.Replace('\\', '/');
            var fileName = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = "UguiPrimitive";
            }

            var extension = Path.GetExtension(fileName);
            if (!string.IsNullOrEmpty(extension))
            {
                fileName = fileName[..^extension.Length];
            }

            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = "UguiPrimitive";
            }

            return string.IsNullOrWhiteSpace(directory)
                ? fileName + ".png"
                : directory + "/" + fileName + ".png";
        }

        internal static float PrimitiveAlpha(string primitiveType, float x, float y, int width, int height, float radius)
        {
            if (primitiveType == "rect")
            {
                return 1f;
            }

            var nx = (x / width) * 2f - 1f;
            var ny = (y / height) * 2f - 1f;
            if (primitiveType == "circle" || primitiveType == "oval")
            {
                return nx * nx + ny * ny <= 1f ? 1f : 0f;
            }

            var r = Math.Min(radius, Math.Min(width, height) * 0.5f);
            if (r <= 0f)
            {
                return 1f;
            }

            var dx = Math.Max(Math.Abs(x - width * 0.5f) - (width * 0.5f - r), 0f);
            var dy = Math.Max(Math.Abs(y - height * 0.5f) - (height * 0.5f - r), 0f);
            return dx * dx + dy * dy <= r * r ? 1f : 0f;
        }

        internal static void ConfigureSpriteImporter(string assetPath, float pixelsPerUnit, Vector4 border)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter
                ?? throw new InvalidOperationException($"Generated texture '{assetPath}' is not imported by TextureImporter.");
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            SetImporterSpriteMeshType(importer, SpriteMeshType.FullRect);
            importer.alphaIsTransparency = true;
            importer.spritePixelsPerUnit = pixelsPerUnit;
            importer.spriteBorder = border;
            importer.SaveAndReimport();
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
        }

        internal static Vector4 PrimitiveBorder(string primitiveType, float radius)
        {
            return primitiveType == "rounded-rect" && radius > 0f
                ? new Vector4(radius, radius, radius, radius)
                : Vector4.zero;
        }
    }
}
