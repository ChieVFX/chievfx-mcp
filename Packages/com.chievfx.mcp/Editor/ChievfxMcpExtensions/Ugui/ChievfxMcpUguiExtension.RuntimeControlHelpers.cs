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

        internal static bool HasScreenPositionInput(JToken args)
            => ChievfxMcpRuntimeUiInteractionInput.HasScreenPositionInput(args);

        internal static void EnsureNoUnresolvedCoordinateSpace(JToken args)
            => ChievfxMcpRuntimeUiInteractionInput.EnsureNoUnresolvedCoordinateSpace(args);

        internal static bool HasExplicitRuntimeInteractionTarget(JToken args)
            => ChievfxMcpRuntimeUiInteractionInput.HasExplicitTargetInput(args);

        internal static RuntimeScreenPosition ReadInteractionScreenPosition(
            JToken args,
            List<string> warnings,
            UguiDependencyStatus status,
            out bool includeCoordinateInfo)
        {
            if (HasScreenPositionInput(args))
            {
                includeCoordinateInfo = true;
                return ReadScreenPosition(args, warnings, status);
            }

            if (HasExplicitRuntimeInteractionTarget(args))
            {
                includeCoordinateInfo = false;
                var screenSize = ResolveRuntimeUiScreenSize(status);
                return new RuntimeScreenPosition(
                    screenSize * 0.5f,
                    screenSize,
                    new Vector2(0.5f, 0.5f),
                    normalizedInputSupplied: false);
            }

            throw new ArgumentException(
                "Runtime uGUI interaction requires path, instanceId, or x/y screen coordinates.");
        }

        internal static RuntimeScreenPosition ReadScreenPosition(JToken args, List<string> warnings, UguiDependencyStatus status)
        {
            EnsureNoUnresolvedCoordinateSpace(args);
            var screenSize = ResolveRuntimeUiScreenSize(status);
            var isNormalized = ReadBool(args, "isNormalized", false);

            if (args["normalized"] is JObject normalized)
            {
                var normalizedPosition = new Vector2(ReadFloat(normalized, "x", 0f), ReadFloat(normalized, "y", 0f));
                return new RuntimeScreenPosition(
                    new Vector2(normalizedPosition.x * screenSize.x, normalizedPosition.y * screenSize.y),
                    screenSize,
                    normalizedPosition,
                    normalizedInputSupplied: true);
            }

            if (args["screenPosition"] is JObject screenPositionObject)
            {
                var screenPosition = new Vector2(ReadFloat(screenPositionObject, "x", screenSize.x * 0.5f), ReadFloat(screenPositionObject, "y", screenSize.y * 0.5f));
                return new RuntimeScreenPosition(
                    screenPosition,
                    screenSize,
                    new Vector2(screenPosition.x / screenSize.x, screenPosition.y / screenSize.y),
                    normalizedInputSupplied: false);
            }

            if (args["x"] != null || args["y"] != null)
            {
                var x = ReadFloat(args, "x", screenSize.x * 0.5f);
                var y = ReadFloat(args, "y", screenSize.y * 0.5f);
                if (isNormalized)
                {
                    var normalizedPosition = new Vector2(x, y);
                    return new RuntimeScreenPosition(
                        new Vector2(normalizedPosition.x * screenSize.x, normalizedPosition.y * screenSize.y),
                        screenSize,
                        normalizedPosition,
                        normalizedInputSupplied: true);
                }

                var position = new Vector2(x, y);
                return new RuntimeScreenPosition(
                    position,
                    screenSize,
                    new Vector2(position.x / screenSize.x, position.y / screenSize.y),
                    normalizedInputSupplied: false);
            }

            throw new ArgumentException(
                "Runtime uGUI interaction requires x/y screen coordinates when path or instanceId is not supplied.");
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

            throw new ArgumentException(
                $"Runtime uGUI interaction requires {screenPositionKey} or {normalizedKey} screen coordinates.");
        }

        // Screen.* and Display.main both report the Game View window size when read from the editor, so the
        // Game View render target wins here and the canvases this extension already walks are the fallback.
        // See ChievfxMcpRuntimeScreenSize for why picking the largest of the three was not enough.
        internal static Vector2 ResolveRuntimeUiScreenSize(UguiDependencyStatus status)
        {
            return ChievfxMcpRuntimeScreenSize.Resolve(
                () => FindRuntimeCanvases(status)
                    .Where(canvas => canvas.gameObject.activeInHierarchy && IsEnabledComponent(canvas))
                    .Select(GetCanvasPixelSize),
                out _);
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

        internal static Dictionary<string, object?> CreateClickHandlerRow(GameObject target, string handler)
        {
            if (handler == "submit")
            {
                var submitTarget = ExecuteEvents.GetEventHandler<ISubmitHandler>(target);
                return new Dictionary<string, object?>
                {
                    ["handler"] = "submit",
                    ["path"] = submitTarget == null ? null : GetTransformPath(submitTarget.transform),
                    ["component"] = submitTarget == null ? null : FirstAssignableComponentName<ISubmitHandler>(submitTarget),
                };
            }

            var clickTarget = ExecuteEvents.GetEventHandler<IPointerClickHandler>(target);
            return new Dictionary<string, object?>
            {
                ["handler"] = "pointerClick",
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

        internal static Component? ResolveSettableControl(GameObject target, UguiDependencyStatus status)
        {
            for (var current = target.transform; current != null; current = current.parent)
            {
                var control = ResolveSettableControlOnGameObject(current.gameObject, status);
                if (control != null)
                {
                    return control;
                }
            }

            return null;
        }

        internal static Component? ResolveSettableControlOnGameObject(GameObject target, UguiDependencyStatus status)
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

            var textInput = ResolveTextInputComponent(target, status);
            if (textInput != null)
            {
                return textInput;
            }

            return status.TmpDropdownType == null ? null : target.GetComponent(status.TmpDropdownType) as Component;
        }

        internal static string ResolveSetControlOperation(Component control, JToken args)
        {
            if (control is InputField || string.Equals(control.GetType().FullName, "TMPro.TMP_InputField", StringComparison.Ordinal))
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

        internal static void ApplyRuntimeControlValue(Component control, JToken? valueToken, bool invokeCallbacks, UguiDependencyStatus status)
        {
            if (control is Slider slider)
            {
                if (!ChievfxMcpRuntimeUiControlValueParsing.TryParseFlexibleFloat(valueToken, out var value))
                {
                    throw ChievfxMcpRuntimeUiControlValueParsing.InvalidValue(
                        "Slider",
                        valueToken,
                        $"Expected number in range [{slider.minValue.ToString(CultureInfo.InvariantCulture)}, {slider.maxValue.ToString(CultureInfo.InvariantCulture)}].",
                        new object[] { slider.minValue, slider.maxValue });
                }

                if (value < slider.minValue || value > slider.maxValue)
                {
                    throw ChievfxMcpRuntimeUiControlValueParsing.InvalidValue(
                        "Slider",
                        valueToken,
                        $"Out of range [{slider.minValue.ToString(CultureInfo.InvariantCulture)}, {slider.maxValue.ToString(CultureInfo.InvariantCulture)}].",
                        new object[] { slider.minValue, slider.maxValue });
                }

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
                if (!ChievfxMcpRuntimeUiControlValueParsing.TryParseFlexibleFloat(valueToken, out var value))
                {
                    throw ChievfxMcpRuntimeUiControlValueParsing.InvalidValue(
                        "Scrollbar",
                        valueToken,
                        "Expected number in range [0, 1].",
                        new object[] { 0f, 1f });
                }

                if (value < 0f || value > 1f)
                {
                    throw ChievfxMcpRuntimeUiControlValueParsing.InvalidValue(
                        "Scrollbar",
                        valueToken,
                        "Out of range [0, 1].",
                        new object[] { 0f, 1f });
                }

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
                if (!ChievfxMcpRuntimeUiControlValueParsing.TryParseFlexibleBool(valueToken, out var value))
                {
                    throw ChievfxMcpRuntimeUiControlValueParsing.InvalidValue(
                        "Toggle",
                        valueToken,
                        "Expected boolean-like value.",
                        new object[] { true, false, 0, 1, "true", "false", "True", "False" });
                }

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
                ApplyDropdownValue(dropdown, valueToken, invokeCallbacks, option => option.text, dropdown.options, index =>
                {
                    if (invokeCallbacks)
                    {
                        dropdown.value = index;
                    }
                    else
                    {
                        dropdown.SetValueWithoutNotify(index);
                    }
                });
                return;
            }

            if (control is InputField inputField)
            {
                ApplyInputFieldControlValue(inputField, valueToken, invokeCallbacks);
                return;
            }

            if (string.Equals(control.GetType().FullName, "TMPro.TMP_InputField", StringComparison.Ordinal))
            {
                ApplyInputFieldControlValue(control, valueToken, invokeCallbacks);
                return;
            }

            ApplyReflectedDropdownOrValueControl(control, valueToken, invokeCallbacks);
        }

        private static void ApplyDropdownValue<T>(
            object control,
            JToken? valueToken,
            bool invokeCallbacks,
            Func<T, string> readOptionText,
            IList<T> options,
            Action<int> setIndex)
        {
            var optionTexts = options.Select(readOptionText).ToArray();
            if (valueToken == null || valueToken.Type == JTokenType.Null)
            {
                throw ChievfxMcpRuntimeUiControlValueParsing.InvalidValue(
                    control.GetType().Name,
                    valueToken,
                    "Expected option index or option text.",
                    optionTexts.Cast<object>().Prepend(0).Prepend(Math.Max(0, options.Count - 1)));
            }

            if (valueToken.Type == JTokenType.String)
            {
                var requested = valueToken.Value<string>()?.Trim();
                var matchIndex = Array.FindIndex(
                    optionTexts,
                    text => string.Equals(text, requested, StringComparison.OrdinalIgnoreCase));
                if (matchIndex < 0)
                {
                    throw ChievfxMcpRuntimeUiControlValueParsing.InvalidValue(
                        control.GetType().Name,
                        valueToken,
                        $"Unknown option text. Options: [{string.Join(", ", optionTexts.Select(text => "\"" + text + "\""))}].",
                        optionTexts);
                }

                setIndex(matchIndex);
                return;
            }

            if (!ChievfxMcpRuntimeUiControlValueParsing.TryParseFlexibleInt(valueToken, out var index)
                || index < 0
                || index >= options.Count)
            {
                throw ChievfxMcpRuntimeUiControlValueParsing.InvalidValue(
                    control.GetType().Name,
                    valueToken,
                    options.Count == 0
                        ? "Dropdown has no options."
                        : $"Expected option index 0..{options.Count - 1}. Options: [{string.Join(", ", optionTexts.Select(text => "\"" + text + "\""))}].",
                    Enumerable.Range(0, Math.Max(0, options.Count)).Cast<object>().Concat(optionTexts.Cast<object>()));
            }

            setIndex(index);
        }

        private static void ApplyReflectedDropdownOrValueControl(Component control, JToken? valueToken, bool invokeCallbacks)
        {
            var optionsObject = GetPropertyValue(control, "options");
            if (optionsObject is System.Collections.IList options && options.Count > 0)
            {
                ApplyDropdownValue(
                    control,
                    valueToken,
                    invokeCallbacks,
                    option => Convert.ToString(GetPropertyValue(option, "text"), CultureInfo.InvariantCulture) ?? string.Empty,
                    options.Cast<object>().ToArray(),
                    index => ApplyReflectedControlValue(control, new JObject { ["value"] = index }, invokeCallbacks));
                return;
            }

            if (!ChievfxMcpRuntimeUiControlValueParsing.TryParseFlexibleInt(valueToken, out var value))
            {
                throw ChievfxMcpRuntimeUiControlValueParsing.InvalidValue(
                    control.GetType().Name,
                    valueToken,
                    "Expected integer value.");
            }

            ApplyReflectedControlValue(control, new JObject { ["value"] = value }, invokeCallbacks);
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

        private static void ApplyInputFieldControlValue(object inputField, JToken? valueToken, bool invokeCallbacks)
        {
            var currentText = inputField is InputField legacyInputField
                ? legacyInputField.text
                : GetPropertyValue(inputField, "text") as string ?? string.Empty;
            var text = valueToken?.Type == JTokenType.String
                ? valueToken.Value<string>() ?? string.Empty
                : valueToken?.ToString() ?? currentText;
            if (invokeCallbacks)
            {
                if (inputField is InputField legacyField)
                {
                    legacyField.text = text;
                }
                else
                {
                    SetProperty(inputField, "text", text);
                }

                return;
            }

            if (inputField is InputField legacyWithoutNotify)
            {
                legacyWithoutNotify.SetTextWithoutNotify(text);
                return;
            }

            inputField.GetType()
                .GetMethod("SetTextWithoutNotify", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null)
                ?.Invoke(inputField, new object[] { text });
        }

        internal static bool IsOutsideScreen(Vector2 position, Vector2 screenSize)
        {
            return position.x < 0f || position.y < 0f || position.x > screenSize.x || position.y > screenSize.y;
        }
    }
}
