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
using static Chievfx.Mcp.Extensions.Ugui.UguiRuntimeHelpers;
using static Chievfx.Mcp.Extensions.Ugui.UguiRuntimeTools;
using static Chievfx.Mcp.Extensions.Ugui.UguiSchemas;
using static Chievfx.Mcp.Extensions.Ugui.UguiSharedHelpers;

namespace Chievfx.Mcp.Extensions.Ugui
{
    internal static class UguiRuntimeControlHelpers
    {
        internal static void AddCoordinateInfo(Dictionary<string, object?> result, RuntimeScreenPosition position)
        {
            result["coordinateConvention"] = new Dictionary<string, object?>
            {
                ["origin"] = "bottom-left",
                ["units"] = "pixels",
                ["screenSize"] = Vector2Row(position.ScreenSize),
                ["screenPosition"] = Vector2Row(position.ScreenPosition),
                ["normalizedPosition"] = Vector2Row(position.NormalizedPosition),
                ["normalizedInputSupplied"] = position.NormalizedInputSupplied,
            };
        }

        internal static Dictionary<string, object?> CreateCoordinateInfo(RuntimeScreenPosition position)
        {
            return new Dictionary<string, object?>
            {
                ["origin"] = "bottom-left",
                ["units"] = "pixels",
                ["screenSize"] = Vector2Row(position.ScreenSize),
                ["screenPosition"] = Vector2Row(position.ScreenPosition),
                ["normalizedPosition"] = Vector2Row(position.NormalizedPosition),
                ["normalizedInputSupplied"] = position.NormalizedInputSupplied,
            };
        }

        internal static RuntimeScreenPosition ReadScreenPosition(JToken args, List<string> warnings, UguiDependencyStatus status)
        {
            var screenSize = ResolveRuntimeUiScreenSize(status);
            if (args["normalized"] is JObject normalized)
            {
                var normalizedPosition = new Vector2(ReadFloat(normalized, "x", 0f), ReadFloat(normalized, "y", 0f));
                return new RuntimeScreenPosition(
                    new Vector2(normalizedPosition.x * screenSize.x, normalizedPosition.y * screenSize.y),
                    screenSize,
                    normalizedPosition,
                    normalizedInputSupplied: true);
            }

            var source = args["screenPosition"] is JObject screenPosition ? screenPosition : args;
            if (source["x"] == null || source["y"] == null)
            {
                warnings.Add("No screenPosition provided; defaulted to center of current screen/game-view.");
            }

            var position = new Vector2(ReadFloat(source, "x", screenSize.x * 0.5f), ReadFloat(source, "y", screenSize.y * 0.5f));
            return new RuntimeScreenPosition(
                position,
                screenSize,
                new Vector2(position.x / screenSize.x, position.y / screenSize.y),
                normalizedInputSupplied: false);
        }

        internal static RuntimeScreenPosition ReadNamedScreenPosition(JToken args, string screenPositionKey, string normalizedKey, List<string> warnings, UguiDependencyStatus status)
        {
            var screenSize = ResolveRuntimeUiScreenSize(status);
            if (args[normalizedKey] is JObject normalized)
            {
                var normalizedPosition = new Vector2(ReadFloat(normalized, "x", 0f), ReadFloat(normalized, "y", 0f));
                return new RuntimeScreenPosition(
                    new Vector2(normalizedPosition.x * screenSize.x, normalizedPosition.y * screenSize.y),
                    screenSize,
                    normalizedPosition,
                    normalizedInputSupplied: true);
            }

            if (args[screenPositionKey] is JObject screenPosition)
            {
                var position = new Vector2(ReadFloat(screenPosition, "x", screenSize.x * 0.5f), ReadFloat(screenPosition, "y", screenSize.y * 0.5f));
                return new RuntimeScreenPosition(
                    position,
                    screenSize,
                    new Vector2(position.x / screenSize.x, position.y / screenSize.y),
                    normalizedInputSupplied: false);
            }

            warnings.Add($"{screenPositionKey} was not provided; defaulted to center of current screen/game-view.");
            return new RuntimeScreenPosition(
                new Vector2(screenSize.x * 0.5f, screenSize.y * 0.5f),
                screenSize,
                new Vector2(0.5f, 0.5f),
                normalizedInputSupplied: false);
        }

        internal static Vector2 ResolveRuntimeUiScreenSize(UguiDependencyStatus status)
        {
            var screenSize = new Vector2(Math.Max(1, Screen.width), Math.Max(1, Screen.height));
            var displaySize = Display.main != null
                ? new Vector2(Math.Max(1, Display.main.renderingWidth), Math.Max(1, Display.main.renderingHeight))
                : screenSize;

            var canvasSize = FindRuntimeCanvases(status)
                .Where(canvas => canvas.gameObject.activeInHierarchy && IsEnabledComponent(canvas))
                .Select(GetCanvasPixelSize)
                .Where(size => size.x > 0.5f && size.y > 0.5f)
                .OrderByDescending(size => size.x * size.y)
                .FirstOrDefault();

            return new[] { screenSize, displaySize, canvasSize }
                .Where(size => size.x > 0.5f && size.y > 0.5f)
                .OrderByDescending(size => size.x * size.y)
                .First();
        }

        internal static Vector2 GetCanvasPixelSize(Component canvas)
        {
            if (GetPropertyValue(canvas, "pixelRect") is Rect pixelRect && pixelRect.width > 0.5f && pixelRect.height > 0.5f)
            {
                return new Vector2(pixelRect.width, pixelRect.height);
            }

            return canvas.transform is RectTransform rect
                ? new Vector2(rect.rect.width, rect.rect.height)
                : Vector2.zero;
        }

        internal static Dictionary<string, object?> CreateControlStateRow(GameObject target, UguiDependencyStatus status)
        {
            return new Dictionary<string, object?>
            {
                ["target"] = CreateRuntimeElementRow(target, status),
                ["controls"] = GetControlComponents(target, status).Select(CreateControlComponentStateRow).ToArray(),
            };
        }

        internal static Dictionary<string, object?> CreateControlComponentStateRow(Component component)
        {
            var row = new Dictionary<string, object?>
            {
                ["type"] = component.GetType().Name,
                ["enabled"] = IsEnabledComponent(component),
                ["interactable"] = GetPropertyValue(component, "interactable"),
            };
            AddIfNotNull(row, "value", GetPropertyValue(component, "value"));
            AddIfNotNull(row, "isOn", GetPropertyValue(component, "isOn"));
            AddIfNotNull(row, "text", GetPropertyValue(component, "text"));
            return row;
        }

        internal static void AddIfNotNull(Dictionary<string, object?> row, string key, object? value)
        {
            if (value != null)
            {
                row[key] = value;
            }
        }

        internal static Dictionary<string, object?> CreateClickHandlerRow(GameObject target, string sequence)
        {
            if (sequence == "submit")
            {
                var submitTarget = ExecuteEvents.GetEventHandler<ISubmitHandler>(target);
                return new Dictionary<string, object?>
                {
                    ["sequence"] = "submit",
                    ["path"] = submitTarget == null ? null : GetTransformPath(submitTarget.transform),
                    ["component"] = submitTarget == null ? null : FirstAssignableComponentName<ISubmitHandler>(submitTarget),
                };
            }

            var clickTarget = ExecuteEvents.GetEventHandler<IPointerClickHandler>(target);
            return new Dictionary<string, object?>
            {
                ["sequence"] = "pointerEnterDownUpClick",
                ["path"] = clickTarget == null ? null : GetTransformPath(clickTarget.transform),
                ["component"] = clickTarget == null ? null : FirstAssignableComponentName<IPointerClickHandler>(clickTarget),
            };
        }

        internal static Dictionary<string, object?> CreateDragHandlerRow(GameObject target)
        {
            var dragTarget = ExecuteEvents.GetEventHandler<IDragHandler>(target);
            return new Dictionary<string, object?>
            {
                ["sequence"] = "initializePotentialDragBeginDragDragEndDrag",
                ["path"] = dragTarget == null ? null : GetTransformPath(dragTarget.transform),
                ["component"] = dragTarget == null ? null : FirstAssignableComponentName<IDragHandler>(dragTarget),
            };
        }

        internal static string? FirstAssignableComponentName<THandler>(GameObject target)
        {
            return target.GetComponents<Component>()
                .FirstOrDefault(component => component is THandler)
                ?.GetType()
                .Name;
        }

        internal static Dictionary<string, object?> CreateSetControlHandlerRow(GameObject target, UguiDependencyStatus status, JToken args, bool invokeCallbacks)
        {
            var control = ResolveSettableControl(target, status)
                ?? throw new ArgumentException($"Target '{GetTransformPath(target.transform)}' has no supported settable uGUI control.");
            return new Dictionary<string, object?>
            {
                ["controlType"] = control.GetType().Name,
                ["path"] = GetTransformPath(target.transform),
                ["operation"] = ResolveSetControlOperation(control, args),
                ["invokeCallbacks"] = invokeCallbacks,
                ["setter"] = invokeCallbacks ? "property" : "SetValueWithoutNotify/SetIsOnWithoutNotify/SetTextWithoutNotify when available",
            };
        }

        internal static Component? ResolveSettableControl(GameObject target, UguiDependencyStatus status)
        {
            if (target.GetComponent<Slider>() is Slider slider)
            {
                return slider;
            }

            if (target.GetComponent<Scrollbar>() is Scrollbar scrollbar)
            {
                return scrollbar;
            }

            if (target.GetComponent<Toggle>() is Toggle toggle)
            {
                return toggle;
            }

            if (target.GetComponent<Dropdown>() is Dropdown dropdown)
            {
                return dropdown;
            }

            if (target.GetComponent<InputField>() is InputField inputField)
            {
                return inputField;
            }

            return status.TmpDropdownType == null ? null : target.GetComponent(status.TmpDropdownType) as Component;
        }

        internal static string ResolveSetControlOperation(Component control, JToken args)
        {
            if (control is InputField)
            {
                return "setText";
            }

            if (control is Toggle)
            {
                return "setIsOn";
            }

            if (control is Dropdown || control.GetType().FullName == "TMPro.TMP_Dropdown")
            {
                return "setSelectedIndex";
            }

            return "setValue";
        }

        internal static void ApplyRuntimeControlValue(GameObject target, UguiDependencyStatus status, JToken args, bool invokeCallbacks)
        {
            var control = ResolveSettableControl(target, status)
                ?? throw new ArgumentException($"Target '{GetTransformPath(target.transform)}' has no supported settable uGUI control.");
            if (control is Slider slider)
            {
                var value = ReadFloat(args, "value", slider.value);
                if (invokeCallbacks)
                {
                    slider.value = value;
                }
                else
                {
                    slider.SetValueWithoutNotify(value);
                }

                return;
            }

            if (control is Scrollbar scrollbar)
            {
                var value = ReadFloat(args, "value", scrollbar.value);
                if (invokeCallbacks)
                {
                    scrollbar.value = value;
                }
                else
                {
                    scrollbar.SetValueWithoutNotify(value);
                }

                return;
            }

            if (control is Toggle toggle)
            {
                var value = ReadBool(args, "value", ReadBool(args, "isOn", toggle.isOn));
                if (invokeCallbacks)
                {
                    toggle.isOn = value;
                }
                else
                {
                    toggle.SetIsOnWithoutNotify(value);
                }

                return;
            }

            if (control is Dropdown dropdown)
            {
                var value = ReadInt(args, "value", dropdown.value);
                if (invokeCallbacks)
                {
                    dropdown.value = value;
                }
                else
                {
                    dropdown.SetValueWithoutNotify(value);
                }

                return;
            }

            if (control is InputField inputField)
            {
                var text = ReadString(args, "text") ?? ReadString(args, "value") ?? inputField.text;
                if (invokeCallbacks)
                {
                    inputField.text = text;
                }
                else
                {
                    inputField.SetTextWithoutNotify(text);
                }

                return;
            }

            ApplyReflectedControlValue(control, args, invokeCallbacks);
        }

        internal static void ApplyReflectedControlValue(Component control, JToken args, bool invokeCallbacks)
        {
            var value = ReadInt(args, "value", GetPropertyValue(control, "value") is int current ? current : 0);
            if (!invokeCallbacks)
            {
                var method = control.GetType().GetMethod("SetValueWithoutNotify", BindingFlags.Public | BindingFlags.Instance);
                if (method != null)
                {
                    method.Invoke(control, new object[] { value });
                    return;
                }
            }

            SetProperty(control, "value", value);
        }

        internal static bool IsOutsideScreen(Vector2 position, Vector2 screenSize)
        {
            return position.x < 0f || position.y < 0f || position.x > screenSize.x || position.y > screenSize.y;
        }
    }
}
