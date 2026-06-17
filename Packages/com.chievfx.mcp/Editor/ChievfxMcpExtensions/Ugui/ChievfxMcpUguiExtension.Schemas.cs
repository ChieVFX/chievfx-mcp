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
using static Chievfx.Mcp.Extensions.Ugui.UguiSharedHelpers;

namespace Chievfx.Mcp.Extensions.Ugui
{
    internal static class UguiSchemas
    {
        internal static JObject CanvasEnsureSchema()
        {
            return Schema(new JObject
            {
                ["name"] = StringProperty("Canvas name to create or normalize."),
                ["canvasPath"] = StringProperty("Optional existing Canvas transform path."),
                ["canvasInstanceId"] = IntProperty("Optional existing Canvas instance id."),
                ["rect"] = RectProperty(),
            });
        }

        internal static JObject RectGetSchema()
        {
            return Schema(new JObject
            {
                ["paths"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" }, ["description"] = "uGUI element paths. One element is valid." },
                ["instanceIds"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "integer" }, ["description"] = "uGUI element instance ids. One element is valid." },
            });
        }

        internal static JObject RectUpdateSchema()
        {
            return Schema(new JObject
            {
                ["paths"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" }, ["description"] = "uGUI element paths. One element is valid." },
                ["instanceIds"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "integer" }, ["description"] = "uGUI element instance ids. One element is valid." },
                ["rect"] = RectProperty(),
            });
        }

        internal static JObject LayoutGroupSetSchema()
        {
            return Schema(new JObject
            {
                ["paths"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" }, ["description"] = "uGUI parent paths. One element is valid." },
                ["instanceIds"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "integer" }, ["description"] = "uGUI parent instance ids. One element is valid." },
                ["layoutGroup"] = new JObject { ["type"] = "string", ["enum"] = new JArray("vertical", "horizontal", "grid"), ["description"] = "LayoutGroup type to create or update." },
                ["padding"] = PaddingProperty(),
                ["spacing"] = new JObject
                {
                    ["description"] = "Number for vertical/horizontal groups; vector {x,y} for grid groups.",
                    ["oneOf"] = new JArray(
                        new JObject { ["type"] = "number" },
                        Vector2Property("GridLayoutGroup spacing.")),
                },
                ["gridSpacing"] = Vector2Property("GridLayoutGroup spacing."),
                ["childAlignment"] = StringProperty("TextAnchor name, e.g. UpperLeft, MiddleCenter, lower-right."),
                ["childControlWidth"] = BoolProperty("Whether vertical/horizontal group controls child width."),
                ["childControlHeight"] = BoolProperty("Whether vertical/horizontal group controls child height."),
                ["childForceExpandWidth"] = BoolProperty("Whether vertical/horizontal group expands child width."),
                ["childForceExpandHeight"] = BoolProperty("Whether vertical/horizontal group expands child height."),
                ["childScaleWidth"] = BoolProperty("Whether vertical/horizontal group uses child x scale."),
                ["childScaleHeight"] = BoolProperty("Whether vertical/horizontal group uses child y scale."),
                ["reverseArrangement"] = BoolProperty("Reverse child arrangement for vertical/horizontal groups."),
                ["cellSize"] = Vector2Property("GridLayoutGroup cell size."),
                ["startCorner"] = StringProperty("GridLayoutGroup corner, e.g. UpperLeft."),
                ["startAxis"] = StringProperty("GridLayoutGroup axis, Horizontal or Vertical."),
                ["constraint"] = StringProperty("GridLayoutGroup constraint, e.g. FixedColumnCount."),
                ["constraintCount"] = IntProperty("GridLayoutGroup constraint count."),
            });
        }

        internal static JObject LayoutElementSetSchema()
        {
            return Schema(new JObject
            {
                ["paths"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" }, ["description"] = "uGUI child paths. One element is valid." },
                ["instanceIds"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "integer" }, ["description"] = "uGUI child instance ids. One element is valid." },
                ["ignoreLayout"] = BoolProperty("Whether this child opts out of parent layout."),
                ["minWidth"] = new JObject { ["type"] = "number" },
                ["minHeight"] = new JObject { ["type"] = "number" },
                ["preferredWidth"] = new JObject { ["type"] = "number" },
                ["preferredHeight"] = new JObject { ["type"] = "number" },
                ["flexibleWidth"] = new JObject { ["type"] = "number" },
                ["flexibleHeight"] = new JObject { ["type"] = "number" },
                ["layoutPriority"] = IntProperty("LayoutElement priority."),
            });
        }

        internal static JObject LayoutRebuildSchema()
        {
            return Schema(new JObject
            {
                ["paths"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" }, ["description"] = "uGUI root paths to rebuild. One element is valid." },
                ["instanceIds"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "integer" }, ["description"] = "uGUI root instance ids to rebuild. One element is valid." },
            });
        }

        internal static JObject ScrollRectCreateSchema()
        {
            var properties = CommonCompositionProperties();
            properties["direction"] = new JObject { ["type"] = "string", ["enum"] = new JArray("vertical", "horizontal", "both"), ["description"] = "Enabled scroll axes. Defaults vertical." };
            properties["contentLayout"] = new JObject { ["type"] = "string", ["enum"] = new JArray("vertical", "horizontal", "grid", "none"), ["description"] = "LayoutGroup to add to Content. Defaults vertical, or horizontal for horizontal direction." };
            properties["contentSizeFitter"] = BoolProperty("Add ContentSizeFitter to Content. Defaults true; PreferredSize on active scroll axes.");
            properties["padding"] = PaddingProperty();
            properties["spacing"] = new JObject
            {
                ["description"] = "Number for vertical/horizontal content layouts; vector {x,y} for grid.",
                ["oneOf"] = new JArray(new JObject { ["type"] = "number" }, Vector2Property("Grid spacing.")),
            };
            properties["gridSpacing"] = Vector2Property("GridLayoutGroup spacing.");
            properties["cellSize"] = Vector2Property("GridLayoutGroup cell size.");
            properties["constraint"] = StringProperty("GridLayoutGroup constraint, e.g. FixedColumnCount.");
            properties["constraintCount"] = IntProperty("GridLayoutGroup constraint count.");
            properties["backgroundColor"] = FlexibleColorProperty("Root Image background color.");
            return Schema(properties);
        }

        internal static JObject GridCreateSchema()
        {
            var properties = CommonCompositionProperties();
            properties["count"] = IntProperty("Number of cells to create. Defaults 12, max 500.");
            properties["cellNamePrefix"] = StringProperty("Cell name prefix. Defaults Cell.");
            properties["cellType"] = new JObject { ["type"] = "string", ["enum"] = new JArray("empty", "image", "button", "text"), ["description"] = "Cell GameObject type. Defaults image." };
            properties["color"] = FlexibleColorProperty("Single cell color.");
            properties["colors"] = new JObject { ["type"] = "array", ["items"] = FlexibleColorProperty("Palette color."), ["description"] = "Optional repeating color palette." };
            properties["padding"] = PaddingProperty();
            properties["spacing"] = Vector2Property("Grid spacing.");
            properties["gridSpacing"] = Vector2Property("Grid spacing alias.");
            properties["cellSize"] = Vector2Property("Grid cell size.");
            properties["constraint"] = StringProperty("GridLayoutGroup constraint. Defaults FixedColumnCount.");
            properties["constraintCount"] = IntProperty("GridLayoutGroup constraint count. Defaults ceil(sqrt(count)).");
            return Schema(properties);
        }

        internal static JObject CommonCompositionProperties()
        {
            return new JObject
            {
                ["name"] = StringProperty("Created root GameObject name."),
                ["canvasPath"] = StringProperty("Optional Canvas path. Creates/falls back to first Canvas if omitted."),
                ["canvasInstanceId"] = IntProperty("Optional Canvas instance id."),
                ["parentPath"] = StringProperty("Optional parent path under Canvas."),
                ["parentInstanceId"] = IntProperty("Optional parent instance id."),
                ["rect"] = RectProperty(),
            };
        }

        internal static JObject SimpleCreateSchema()
        {
            return Schema(new JObject
            {
                ["name"] = StringProperty("Optional GameObject name."),
                ["canvasPath"] = StringProperty("Optional Canvas path. Creates/falls back to first Canvas if omitted."),
                ["canvasInstanceId"] = IntProperty("Optional Canvas instance id."),
                ["parentPath"] = StringProperty("Optional parent path under Canvas."),
                ["parentInstanceId"] = IntProperty("Optional parent instance id."),
                ["rect"] = RectProperty(),
                ["image"] = ImageOptionsProperty(),
            });
        }

        internal static JObject ControlCreateSchema()
        {
            return Schema(new JObject
            {
                ["controlType"] = new JObject
                {
                    ["type"] = "string",
                    ["enum"] = new JArray("button", "slider", "progressbar"),
                    ["description"] = "uGUI control to create.",
                },
                ["type"] = new JObject
                {
                    ["type"] = "string",
                    ["enum"] = new JArray("button", "slider", "progressbar"),
                    ["description"] = "Alias for controlType.",
                },
                ["name"] = StringProperty("Optional GameObject name."),
                ["canvasPath"] = StringProperty("Optional Canvas path. Creates/falls back to first Canvas if omitted."),
                ["canvasInstanceId"] = IntProperty("Optional Canvas instance id."),
                ["parentPath"] = StringProperty("Optional parent path under Canvas."),
                ["parentInstanceId"] = IntProperty("Optional parent instance id."),
                ["rect"] = RectProperty(),
                ["text"] = StringProperty("Optional label for button."),
                ["textBackend"] = new JObject
                {
                    ["type"] = "string",
                    ["enum"] = new JArray("auto", "legacy", "tmp"),
                    ["description"] = "Text backend for button labels when applicable.",
                },
                ["image"] = ImageOptionsProperty(),
                ["value"] = new JObject { ["type"] = "number", ["description"] = "Initial slider/progressbar value from 0 to 1." },
            }, "controlType");
        }

        internal static JObject TextMeshProHierarchySchema()
        {
            return Schema(TextMeshProTargetProperties(maxResults: true));
        }

        internal static JObject SiblingDrawOrderSetSchema()
        {
            return Schema(new JObject
            {
                ["paths"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } },
                ["instanceIds"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "integer" } },
                ["placement"] = new JObject { ["type"] = "string", ["enum"] = new JArray("first", "last", "index", "before", "after") },
                ["index"] = new JObject { ["type"] = "integer" },
                ["siblingPath"] = new JObject { ["type"] = "string" },
                ["siblingInstanceId"] = new JObject { ["type"] = "integer" },
            });
        }

        internal static JObject TextMeshProGetSchema()
        {
            return Schema(TextMeshProTargetProperties(maxResults: true));
        }

        internal static JObject TextMeshProSetOrCreateSchema()
        {
            var properties = TextMeshProTargetProperties(maxResults: false);
            properties["isCreate"] = new JObject { ["type"] = "boolean" };
            properties["placement"] = new JObject
            {
                ["type"] = "string",
                ["enum"] = new JArray("same-object", "child"),
                ["description"] = "Where to create TextMeshProUGUI when isCreate is true. same-object adds to the target when possible; child creates/uses childName.",
            };
            properties["childName"] = StringProperty("Child GameObject name used when placement is child, or when same-object creation must fall back to a child. Defaults to Label.");
            properties["text"] = new JObject { ["type"] = "string" };
            properties["fontSize"] = new JObject { ["type"] = "number" };
            properties["color"] = FlexibleColorProperty("Text color.");
            properties["alignment"] = new JObject
            {
                ["type"] = "string",
                ["description"] = "TMP TextAlignmentOptions name. Common aliases such as 'middle center', 'center', 'left', and 'bottom-right' are accepted.",
            };
            properties["bold"] = new JObject { ["type"] = "boolean" };
            properties["wrapping"] = new JObject { ["type"] = "boolean" };
            properties["outlineWidth"] = new JObject { ["type"] = "number" };
            properties["outlineColor"] = FlexibleColorProperty("Text outline color.");
            return Schema(properties);
        }

        internal static JObject ImageOptionsProperty()
        {
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["spritePath"] = StringProperty("Sprite asset path."),
                    ["spriteGuid"] = StringProperty("Sprite asset guid."),
                    ["color"] = FlexibleColorProperty("Image tint."),
                    ["raycastTarget"] = new JObject { ["type"] = "boolean" },
                    ["preserveAspect"] = new JObject { ["type"] = "boolean" },
                    ["imageType"] = new JObject { ["type"] = "string", ["enum"] = new JArray("Auto", "Simple", "Sliced", "Tiled", "Filled") },
                },
                ["additionalProperties"] = false,
            };
        }

        internal static JObject TextMeshProTargetProperties(bool maxResults)
        {
            var properties = new JObject
            {
                ["paths"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } },
                ["instanceIds"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "integer" } },
                ["includeInactive"] = new JObject { ["type"] = "boolean" },
            };
            if (maxResults)
            {
                properties["maxResults"] = new JObject { ["type"] = "integer" };
            }

            return properties;
        }

        internal static JObject ImageSetSchema()
        {
            return Schema(new JObject
            {
                ["targetPath"] = StringProperty("Target uGUI Image GameObject path."),
                ["instanceId"] = IntProperty("Target GameObject instance id."),
                ["spritePath"] = StringProperty("Project asset path for Sprite."),
                ["spriteGuid"] = StringProperty("Asset GUID for Sprite."),
                ["color"] = FlexibleColorProperty("Image tint color."),
                ["raycastTarget"] = BoolProperty("Whether Image receives raycasts."),
                ["preserveAspect"] = BoolProperty("Whether Image preserves sprite aspect ratio."),
                ["imageType"] = new JObject
                {
                    ["type"] = "string",
                    ["enum"] = new JArray("Auto", "Simple", "Sliced", "Tiled", "Filled"),
                    ["description"] = "UnityEngine.UI.Image.type. Prefer Auto: Sliced for sprites with non-zero border, otherwise Simple. Use explicit Sliced/Tiled only for intended 9-slice/tiling.",
                },
            }, "targetPath");
        }

        internal static JObject ImagePrimitiveCreateSchema()
        {
            return Schema(new JObject
            {
                ["name"] = StringProperty("Created Image GameObject name."),
                ["canvasPath"] = StringProperty("Optional Canvas path. Creates/falls back to first Canvas if omitted."),
                ["canvasInstanceId"] = IntProperty("Optional Canvas instance id."),
                ["parentPath"] = StringProperty("Optional parent path under Canvas."),
                ["parentInstanceId"] = IntProperty("Optional parent instance id under Canvas."),
                ["path"] = StringProperty("Generated PNG asset path. Accepts paths with no extension or any extension; output is normalized to .png under Assets/."),
                ["pngPath"] = StringProperty("Deprecated alias for path."),
                ["assetPath"] = StringProperty("Deprecated alias for path."),
                ["primitiveType"] = new JObject { ["type"] = "string", ["enum"] = new JArray("rect", "rounded-rect", "circle", "oval") },
                ["type"] = new JObject { ["type"] = "string", ["enum"] = new JArray("rect", "rounded-rect", "circle", "oval") },
                ["width"] = new JObject { ["type"] = "integer" },
                ["height"] = new JObject { ["type"] = "integer" },
                ["radius"] = new JObject { ["type"] = "number" },
                ["pixelsPerUnit"] = new JObject { ["type"] = "number" },
                ["color"] = FlexibleColorProperty("Generated sprite color."),
                ["raycastTarget"] = BoolProperty("Whether Image receives raycasts."),
                ["imageType"] = new JObject
                {
                    ["type"] = "string",
                    ["enum"] = new JArray("Auto", "Simple", "Sliced", "Tiled", "Filled"),
                    ["description"] = "UnityEngine.UI.Image.type for created Image. Defaults to Auto: Sliced when sprite border is non-zero (e.g. rounded-rect with radius>0 or explicit spriteBorder), otherwise Simple.",
                },
                ["spriteBorder"] = Vector4Property("Override 9-slice sprite border as left,bottom,right,top. Defaults: rounded-rect uses radius for all four sides, other primitives use zero. Set non-zero to enable Sliced/Tiled 9-slice for any primitive."),
                ["rect"] = RectProperty(),
            }, "path");
        }

        internal static JObject SpriteConfigureSchema()
        {
            return Schema(new JObject
            {
                ["path"] = StringProperty("Texture asset path."),
                ["guid"] = StringProperty("Texture asset GUID."),
                ["spritePath"] = StringProperty("Texture asset path alias."),
                ["spriteGuid"] = StringProperty("Texture asset GUID alias."),
                ["pixelsPerUnit"] = new JObject { ["type"] = "number", ["description"] = "TextureImporter.spritePixelsPerUnit." },
                ["spritePixelsPerUnit"] = new JObject { ["type"] = "number", ["description"] = "TextureImporter.spritePixelsPerUnit alias." },
                ["spriteBorder"] = Vector4Property("Sprite border as left,bottom,right,top. Required for Sliced/Tiled 9-slice UI."),
            });
        }

        internal static JObject UiHierarchySchema()
        {
            return Schema(new JObject
            {
                ["paths"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" }, ["description"] = "Optional Canvas/root paths to inspect. One element is valid." },
                ["instanceIds"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "integer" }, ["description"] = "Optional Canvas/root instance ids to inspect. One element is valid." },
                ["includeInactive"] = BoolProperty("Include inactive uGUI elements."),
                ["includeComponents"] = BoolProperty("Include compact component type preview rows. Defaults false."),
                ["maxDepth"] = IntProperty("Maximum hierarchy depth for overview."),
                ["maxResults"] = IntProperty("Maximum hierarchy elements to emit."),
            });
        }

        internal static JObject UiFindSchema()
        {
            return Schema(new JObject
            {
                ["paths"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" }, ["description"] = "Optional uGUI element paths to inspect. One element is valid." },
                ["instanceIds"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "integer" }, ["description"] = "Optional uGUI element instance ids to inspect. One element is valid." },
                ["name"] = StringProperty("Optional exact GameObject name filter."),
                ["componentType"] = StringProperty("Optional component type name filter, e.g. Image, Button, TextMeshProUGUI."),
                ["includeInactive"] = BoolProperty("Include inactive uGUI elements."),
                ["includeDetails"] = BoolProperty("Include RectTransform/Image/TMP details. Defaults true."),
                ["normalizedCoords"] = BoolProperty("Output screenRect in normalized 0..1 screen coordinates instead of pixels."),
                ["maxResults"] = IntProperty("Maximum matching elements to emit."),
            });
        }

        internal static JObject RuntimeTargetAndPositionProperties()
        {
            var properties = RuntimeTargetProperties();
            properties["x"] = new JObject { ["type"] = "number", ["description"] = "Screen-space X coordinate in pixels, origin bottom-left." };
            properties["y"] = new JObject { ["type"] = "number", ["description"] = "Screen-space Y coordinate in pixels, origin bottom-left." };
            properties["screenPosition"] = Vector2Property("Screen-space position in pixels, origin bottom-left.");
            properties["normalized"] = Vector2Property("Optional normalized screen coordinate. x/y in 0..1 are multiplied by current screen/game-view size.");
            return properties;
        }

        internal static JObject RuntimeTargetProperties()
        {
            return new JObject
            {
                ["targetPath"] = StringProperty("Optional target GameObject path. If omitted, screen position resolves top runtime hit."),
                ["instanceId"] = IntProperty("Optional target GameObject instance id."),
            };
        }

        internal static JObject Schema(JObject properties, params string[] required)
        {
            var schema = new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = properties,
            };
            if (required.Length > 0)
            {
                schema["required"] = new JArray(required);
            }

            return schema;
        }

        internal static JObject RectProperty()
        {
            return new JObject
            {
                ["type"] = "object",
                ["description"] = "Semantic RectTransform placement. Raw anchors/position/size/offsets override preset when supplied.",
                ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["preset"] = new JObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JArray("fill", "stretch", "center", "dock", "dock-top", "dock-bottom", "dock-left", "dock-right", "anchor-size"),
                    },
                    ["dock"] = new JObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JArray("top", "bottom", "left", "right"),
                    },
                    ["size"] = Vector2Property("Width/height alias for sizeDelta in center, dock thickness, or anchor-size."),
                    ["sizeDelta"] = Vector2Property("Raw RectTransform.sizeDelta override applied after preset."),
                    ["position"] = Vector2Property("Anchored position alias for anchoredPosition in center or anchor-size."),
                    ["anchoredPosition"] = Vector2Property("Raw RectTransform.anchoredPosition override applied after preset."),
                    ["offsetMin"] = Vector2Property("Raw RectTransform.offsetMin override applied after preset, useful with stretch anchors."),
                    ["offsetMax"] = Vector2Property("Raw RectTransform.offsetMax override applied after preset, useful with stretch anchors."),
                    ["margin"] = new JObject { ["type"] = "number" },
                    ["pivot"] = Vector2Property("Pivot; defaults to center."),
                    ["anchorMin"] = Vector2Property("Raw RectTransform.anchorMin override applied after preset."),
                    ["anchorMax"] = Vector2Property("Raw RectTransform.anchorMax override applied after preset."),
                },
            };
        }

        internal static JObject Vector2Property(string description)
        {
            return new JObject
            {
                ["type"] = "object",
                ["description"] = description,
                ["properties"] = new JObject
                {
                    ["x"] = new JObject { ["type"] = "number" },
                    ["y"] = new JObject { ["type"] = "number" },
                },
            };
        }

        internal static JObject PaddingProperty()
        {
            return new JObject
            {
                ["type"] = "object",
                ["description"] = "LayoutGroup padding.",
                ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["left"] = new JObject { ["type"] = "integer" },
                    ["right"] = new JObject { ["type"] = "integer" },
                    ["top"] = new JObject { ["type"] = "integer" },
                    ["bottom"] = new JObject { ["type"] = "integer" },
                },
            };
        }

        internal static JObject Vector4Property(string description)
        {
            return new JObject
            {
                ["type"] = "object",
                ["description"] = description,
                ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["left"] = new JObject { ["type"] = "number" },
                    ["bottom"] = new JObject { ["type"] = "number" },
                    ["right"] = new JObject { ["type"] = "number" },
                    ["top"] = new JObject { ["type"] = "number" },
                    ["x"] = new JObject { ["type"] = "number" },
                    ["y"] = new JObject { ["type"] = "number" },
                    ["z"] = new JObject { ["type"] = "number" },
                    ["w"] = new JObject { ["type"] = "number" },
                },
            };
        }

        internal static JObject ColorProperty(string description)
        {
            return new JObject
            {
                ["type"] = "object",
                ["description"] = description,
                ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["r"] = new JObject { ["type"] = "number" },
                    ["g"] = new JObject { ["type"] = "number" },
                    ["b"] = new JObject { ["type"] = "number" },
                    ["a"] = new JObject { ["type"] = "number" },
                },
            };
        }

        internal static JObject FlexibleColorProperty(string description)
        {
            return new JObject
            {
                ["description"] = description,
                ["oneOf"] = new JArray(
                    new JObject { ["type"] = "string", ["description"] = "Hex color, e.g. #ff0000, ff0000, #ff000080, or ff000080." },
                    ColorProperty(description)),
            };
        }

        internal static JObject StringProperty(string description)
        {
            return new JObject { ["type"] = "string", ["description"] = description };
        }

        internal static JObject IntProperty(string description)
        {
            return new JObject { ["type"] = "integer", ["description"] = description };
        }

        internal static JObject BoolProperty(string description)
        {
            return new JObject { ["type"] = "boolean", ["description"] = description };
        }
    }
}
