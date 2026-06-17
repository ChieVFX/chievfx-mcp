#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Chievfx.Mcp.Editor;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using static Chievfx.Mcp.Extensions.UiToolkit.ChievfxMcpUiToolkitExtension;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitRuntimeTools;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitResources;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitInteractions;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitPanelQueries;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitRows;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitShared;

namespace Chievfx.Mcp.Extensions.UiToolkit
{
    internal static class UiToolkitSchemas
    {
        internal static JObject RuntimeInteractSchema()
        {
            return Schema(new JObject
            {
                ["action"] = new JObject
                {
                    ["type"] = "string",
                    ["enum"] = new JArray("pointerClick", "pointerDrag", "focus", "navigationSubmit", "setValue"),
                    ["description"] = "Supported guarded interaction. Defaults to pointerClick.",
                },
                ["visualElementRef"] = StringProperty("Target visualElementRef from UI Toolkit runtime reads/probes."),
                ["targetRef"] = StringProperty("Alias for visualElementRef."),
                ["targetPath"] = StringProperty("Target VisualElement path from UI Toolkit runtime reads/probes."),
                ["path"] = StringProperty("Alias for targetPath."),
                ["name"] = StringProperty("Target VisualElement name."),
                ["targetName"] = StringProperty("Alias for name."),
                ["x"] = new JObject { ["type"] = "number", ["description"] = "Screen-space X coordinate in pixels, origin bottom-left." },
                ["y"] = new JObject { ["type"] = "number", ["description"] = "Screen-space Y coordinate in pixels, origin bottom-left." },
                ["screenPosition"] = Vector2Property("Screen-space position in pixels, origin bottom-left. Used when no explicit target is supplied."),
                ["normalized"] = Vector2Property("Optional normalized screen coordinate. x/y in 0..1 are multiplied by current screen/game-view size."),
                ["delta"] = Vector2Property("pointerDrag delta in panel/UI Toolkit coordinates. Positive y moves pointer downward in UI Toolkit panel space."),
                ["steps"] = new JObject { ["type"] = "integer", ["description"] = "pointerDrag move-event count. Defaults to 12, capped to 120." },
                ["value"] = new JObject
                {
                    ["description"] = "Value for setValue: string, boolean, integer, or number for standard UI Toolkit controls.",
                    ["oneOf"] = new JArray(
                        new JObject { ["type"] = "string" },
                        new JObject { ["type"] = "boolean" },
                        new JObject { ["type"] = "integer" },
                        new JObject { ["type"] = "number" }),
                },
                ["text"] = StringProperty("String value alias for TextField-like controls."),
                ["isOn"] = BoolProperty("Boolean value alias for Toggle-like controls."),
                ["invokeCallbacks"] = BoolProperty("For setValue, true uses value property and may invoke callbacks; false prefers SetValueWithoutNotify."),
                ["dryRun"] = BoolProperty("Defaults true. Reports target, plan, and before/after state without dispatching events or mutating values."),
                ["allowStateMutation"] = BoolProperty("Required true for non-dry-run dispatch/value changes because callbacks may mutate game state."),
            });
        }

        internal static JObject Schema(JObject properties)
        {
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
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

        internal static JObject StringProperty(string description)
        {
            return new JObject { ["type"] = "string", ["description"] = description };
        }

        internal static JObject BoolProperty(string description)
        {
            return new JObject { ["type"] = "boolean", ["description"] = description };
        }
    }
}
