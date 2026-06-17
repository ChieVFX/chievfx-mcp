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
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitPanelQueries;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitRows;
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitShared;
namespace Chievfx.Mcp.Extensions.UiToolkit
{
    internal static class UiToolkitInteractions
    {
        internal static bool EnsureRuntimeReadAllowed(List<string> warnings)
        {
            if (IsRuntimePlayModeActive())
            {
                return true;
            }

            warnings.Add("Runtime UI Toolkit reads are gated to Play Mode; enter Play Mode before reading runtime UI state.");
            return false;
        }

        internal static RuntimeInteractionResolution ResolveRuntimeInteractionTarget(JToken args, UiToolkitDependencyStatus status, List<string> warnings)
        {
            if (ChievfxMcpRuntimeUiInteractionInput.HasExplicitTargetInput(args))
            {
                var explicitTarget = ResolveVisualElement(args, status);
                if (explicitTarget != null)
                {
                    return RuntimeInteractionResolution.FromTarget(explicitTarget, "explicitTarget");
                }

                warnings.Add(ChievfxMcpRuntimeUiInteractionInput.FormatTargetNotFoundMessage(args, "UI Toolkit"));
                return new RuntimeInteractionResolution(
                    null,
                    null,
                    null,
                    null,
                    Array.Empty<Dictionary<string, object?>>(),
                    "explicitTarget");
            }

            if (!ChievfxMcpRuntimeUiInteractionInput.HasScreenPositionInput(args))
            {
                throw new ArgumentException(
                    "Runtime UI Toolkit interaction requires path, visualElementRef, name, or x/y screen coordinates.");
            }

            var position = ReadScreenPosition(args, warnings);
            if (IsOutsideScreen(position.ScreenPosition, position.ScreenSize))
            {
                warnings.Add("Coordinate is outside current screen/game-view bounds.");
            }

            var stackRows = new List<Dictionary<string, object?>>();
            foreach (var panelGroup in FindRuntimePanelGroups(status))
            {
                var panelPosition = ConvertScreenToPanel(status, panelGroup, position, warnings);
                if (!panelPosition.HasValue)
                {
                    continue;
                }

                var hits = MergePickAllWithBoundsHits(PickAll(status, panelGroup.Panel, panelPosition.Value, warnings), panelGroup, status, panelPosition.Value);
                foreach (var hit in hits)
                {
                    var row = CreateVisualElementRow(hit, status, panelGroup, includeTextAndValue: true);
                    row["input"] = CreateScreenPositionRow(position);
                    row["panelPosition"] = CreateVector2Row(panelPosition.Value);
                    row["ordering"] = CreatePanelOrderingRow(panelGroup, hit, stackRows.Count);
                    row["raycastResult"] = new Dictionary<string, object?>
                    {
                        ["source"] = "IPanel.PickAll",
                        ["panelRef"] = CreatePanelRef(panelGroup.Panel),
                    };
                    stackRows.Add(row);
                }

                var target = hits.FirstOrDefault(IsRuntimeInteractionCandidate);
                if (target != null)
                {
                    return new RuntimeInteractionResolution(target, panelGroup, position, panelPosition, stackRows.ToArray(), "screenPosition");
                }
            }

            return new RuntimeInteractionResolution(null, null, position, null, stackRows.ToArray(), "screenPosition");
        }

        internal static object? ResolveVisualElement(JToken args, UiToolkitDependencyStatus status)
        {
            var pathValue = ReadString(args, "path");
            var targetRef = ReadString(args, "visualElementRef") ?? ReadString(args, "targetRef");
            if (string.IsNullOrWhiteSpace(targetRef)
                && !string.IsNullOrWhiteSpace(pathValue)
                && pathValue!.StartsWith("ve:", StringComparison.Ordinal))
            {
                targetRef = pathValue;
            }

            var targetPath = !string.IsNullOrWhiteSpace(pathValue) && !pathValue!.StartsWith("ve:", StringComparison.Ordinal)
                ? pathValue
                : ReadString(args, "targetPath");
            var targetName = ReadString(args, "name") ?? ReadString(args, "targetName");
            if (string.IsNullOrWhiteSpace(targetRef)
                && string.IsNullOrWhiteSpace(targetPath)
                && string.IsNullOrWhiteSpace(targetName))
            {
                return null;
            }

            foreach (var document in FindRuntimeDocuments(status))
            {
                var root = GetRootVisualElement(document);
                if (root == null)
                {
                    continue;
                }

                foreach (var item in EnumerateVisibleTree(root, status, DefaultMaxRows * 4))
                {
                    if (!string.IsNullOrWhiteSpace(targetRef)
                        && string.Equals(CreateVisualElementRef(item.Element), targetRef, StringComparison.Ordinal))
                    {
                        return item.Element;
                    }

                    if (!string.IsNullOrWhiteSpace(targetPath)
                        && string.Equals(GetVisualElementPath(item.Element), targetPath, StringComparison.Ordinal))
                    {
                        return item.Element;
                    }

                    if (!string.IsNullOrWhiteSpace(targetName)
                        && string.Equals(ReadMemberString(item.Element, "name"), targetName, StringComparison.Ordinal))
                    {
                        return item.Element;
                    }
                }
            }

            return null;
        }

        internal static bool IsRuntimeInteractionCandidate(object visualElement)
        {
            return ReadBoolMember(visualElement, "enabledInHierarchy", true)
                && !string.Equals(ReadMemberString(visualElement, "pickingMode"), "Ignore", StringComparison.OrdinalIgnoreCase);
        }

        internal static string[] PlannedEvents(string action)
        {
            return action switch
            {
                "pointerClick" => new[] { "PointerDownEvent", "PointerUpEvent", "ClickEvent" },
                "pointerDrag" => new[] { "PointerDownEvent", "PointerMoveEvent...", "PointerUpEvent" },
                "navigationSubmit" => new[] { "NavigationSubmitEvent" },
                "focus" => new[] { "VisualElement.Focus" },
                "setValue" => Array.Empty<string>(),
                _ => Array.Empty<string>(),
            };
        }

        internal static string[] ApplyRuntimeInteraction(string action, object target, Vector2? panelPosition, JToken args, List<string> warnings)
        {
            if (target is not VisualElement element)
            {
                throw new InvalidOperationException("Resolved target is not a UI Toolkit VisualElement.");
            }

            switch (action)
            {
                case "pointerClick":
                    return DispatchPointerClick(element, panelPosition);
                case "pointerDrag":
                    return DispatchPointerDrag(FindPointerDragTarget(element), panelPosition, args);
                case "navigationSubmit":
                    return DispatchNavigationSubmit(element);
                case "focus":
                    element.Focus();
                    return new[] { "VisualElement.Focus" };
                case "setValue":
                    ApplyRuntimeControlValue(element, args, warnings);
                    return Array.Empty<string>();
                default:
                    throw new ArgumentException("Unsupported UI Toolkit runtime interaction action: " + action);
            }
        }

        internal static string[] DispatchPointerClick(VisualElement element, Vector2? panelPosition)
        {
            var position = panelPosition ?? element.worldBound.center;
            using (var pointerDown = PointerDownEvent.GetPooled())
            {
                PreparePointerEvent(pointerDown, position);
                element.SendEvent(pointerDown);
            }

            using (var pointerUp = PointerUpEvent.GetPooled())
            {
                PreparePointerEvent(pointerUp, position);
                element.SendEvent(pointerUp);
            }

            using (var click = ClickEvent.GetPooled())
            {
                PreparePointerEvent(click, position);
                element.SendEvent(click);
            }

            return new[] { "PointerDownEvent", "PointerUpEvent", "ClickEvent" };
        }

        internal static string[] DispatchPointerDrag(VisualElement element, Vector2? panelPosition, JToken args)
        {
            if (!TryReadVector2(args["delta"], out var delta))
            {
                throw new ArgumentException("pointerDrag requires delta:{x,y} in panel/UI Toolkit coordinates.");
            }

            var steps = Mathf.Clamp(ReadInt(args, "steps", 12), 1, 120);
            var start = panelPosition ?? element.worldBound.center;
            using (var pointerDown = PointerDownEvent.GetPooled())
            {
                PreparePointerEvent(pointerDown, start);
                element.SendEvent(pointerDown);
            }

            for (var i = 1; i <= steps; i++)
            {
                var position = start + delta * (i / (float)steps);
                using (var pointerMove = PointerMoveEvent.GetPooled())
                {
                    PreparePointerEvent(pointerMove, position);
                    element.SendEvent(pointerMove);
                }
            }

            using (var pointerUp = PointerUpEvent.GetPooled())
            {
                PreparePointerEvent(pointerUp, start + delta, pressedButtons: 0);
                element.SendEvent(pointerUp);
            }

            return new[] { "PointerDownEvent", "PointerMoveEvent x" + steps.ToString(CultureInfo.InvariantCulture), "PointerUpEvent" };
        }

        internal static VisualElement FindPointerDragTarget(VisualElement element)
        {
            for (var current = element; current != null; current = current.parent)
            {
                if (current is ScrollView)
                {
                    return current;
                }
            }

            return element;
        }

        internal static string[] DispatchNavigationSubmit(VisualElement element)
        {
            using (var submit = NavigationSubmitEvent.GetPooled(EventModifiers.None))
            {
                element.SendEvent(submit);
            }

            return new[] { "NavigationSubmitEvent" };
        }

        internal static void PreparePointerEvent(EventBase evt, Vector2 position, int pressedButtons = 1)
        {
            SetWritableProperty(evt, "target", null);
            SetWritableProperty(evt, "position", (Vector3)position);
            SetWritableProperty(evt, "localPosition", (Vector3)position);
            SetWritableProperty(evt, "button", 0);
            SetWritableProperty(evt, "pressedButtons", pressedButtons);
            SetWritableProperty(evt, "clickCount", 1);
            SetWritableProperty(evt, "pointerId", PointerId.mousePointerId);
            SetWritableProperty(evt, "pointerType", UnityEngine.UIElements.PointerType.mouse);
            SetWritableProperty(evt, "isPrimary", true);
        }

        internal static void ApplyRuntimeControlValue(VisualElement element, JToken args, List<string> warnings)
        {
            var valueProperty = element.GetType().GetProperty("value", BindingFlags.Public | BindingFlags.Instance);
            if (valueProperty == null || !valueProperty.CanWrite)
            {
                throw new ArgumentException($"Target '{GetVisualElementPath(element)}' has no supported writable UI Toolkit value property.");
            }

            var invokeCallbacks = ReadBool(args, "invokeCallbacks", true);
            if (args["invokeCallbacks"] == null)
            {
                warnings.Add("invokeCallbacks was not specified; defaulted to true and may fire game callbacks.");
            }

            var value = ConvertControlValue(args["value"] ?? args["text"] ?? args["isOn"], valueProperty.PropertyType, valueProperty.GetValue(element), element);
            if (!invokeCallbacks)
            {
                var setWithoutNotify = element.GetType().GetMethod("SetValueWithoutNotify", BindingFlags.Public | BindingFlags.Instance);
                if (setWithoutNotify != null)
                {
                    setWithoutNotify.Invoke(element, new[] { value });
                    return;
                }

                warnings.Add("SetValueWithoutNotify was not found; falling back to value property setter.");
            }

            valueProperty.SetValue(element, value);
        }

        internal static object? ConvertControlValue(JToken? token, Type targetType, object? currentValue, object? element = null)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return currentValue;
            }

            var nullableType = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (nullableType == typeof(string))
            {
                var text = token.Type == JTokenType.String ? token.Value<string>() ?? string.Empty : token.ToString();
                if (element != null && GetMemberValue(element, "choices") is IEnumerable choicesEnumerable)
                {
                    var choices = choicesEnumerable.Cast<object>()
                        .Select(choice => Convert.ToString(choice, CultureInfo.InvariantCulture) ?? string.Empty)
                        .ToArray();
                    if (choices.Length > 0)
                    {
                        var match = choices.FirstOrDefault(choice => string.Equals(choice, text, StringComparison.OrdinalIgnoreCase));
                        if (match == null)
                        {
                            throw ChievfxMcpRuntimeUiControlValueParsing.InvalidValue(
                                element.GetType().Name,
                                token,
                                "Unknown choice.",
                                choices);
                        }

                        return match;
                    }
                }

                return text;
            }

            if (nullableType == typeof(bool))
            {
                if (ChievfxMcpRuntimeUiControlValueParsing.TryParseFlexibleBool(token, out var boolValue))
                {
                    return boolValue;
                }

                throw ChievfxMcpRuntimeUiControlValueParsing.InvalidValue(
                    element == null ? "Toggle" : element.GetType().Name,
                    token,
                    "Expected boolean-like value.",
                    new object[] { true, false, 0, 1, "true", "false", "True", "False" });
            }

            if (nullableType.IsEnum)
            {
                if (token.Type == JTokenType.String)
                {
                    var names = Enum.GetNames(nullableType);
                    var requested = token.Value<string>()?.Trim();
                    var match = names.FirstOrDefault(name => string.Equals(name, requested, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                    {
                        return Enum.Parse(nullableType, match, ignoreCase: true);
                    }

                    throw ChievfxMcpRuntimeUiControlValueParsing.InvalidValue(
                        element == null ? nullableType.Name : element.GetType().Name,
                        token,
                        "Unknown enum name.",
                        names);
                }

                if (ChievfxMcpRuntimeUiControlValueParsing.TryParseFlexibleInt(token, out var enumIndex))
                {
                    return Enum.ToObject(nullableType, enumIndex);
                }

                throw ChievfxMcpRuntimeUiControlValueParsing.InvalidValue(
                    element == null ? nullableType.Name : element.GetType().Name,
                    token,
                    "Expected enum name or integer.",
                    Enum.GetNames(nullableType));
            }

            if (nullableType == typeof(int))
            {
                if (ChievfxMcpRuntimeUiControlValueParsing.TryParseFlexibleInt(token, out var intValue))
                {
                    ValidateNumericRange(element, token, intValue, nullableType);
                    return intValue;
                }

                throw ChievfxMcpRuntimeUiControlValueParsing.InvalidValue(
                    element == null ? "Integer" : element.GetType().Name,
                    token,
                    "Expected integer.");
            }

            if (nullableType == typeof(float))
            {
                if (ChievfxMcpRuntimeUiControlValueParsing.TryParseFlexibleFloat(token, out var floatValue))
                {
                    ValidateNumericRange(element, token, floatValue, nullableType);
                    return floatValue;
                }

                throw ChievfxMcpRuntimeUiControlValueParsing.InvalidValue(
                    element == null ? "Float" : element.GetType().Name,
                    token,
                    "Expected number.");
            }

            if (nullableType == typeof(double))
            {
                if (ChievfxMcpRuntimeUiControlValueParsing.TryParseFlexibleFloat(token, out var doubleValue))
                {
                    ValidateNumericRange(element, token, doubleValue, nullableType);
                    return (double)doubleValue;
                }

                throw ChievfxMcpRuntimeUiControlValueParsing.InvalidValue(
                    element == null ? "Double" : element.GetType().Name,
                    token,
                    "Expected number.");
            }

            throw new ArgumentException("Unsupported UI Toolkit value type: " + targetType.FullName);
        }

        private static void ValidateNumericRange(object? element, JToken token, float value, Type valueType)
        {
            if (element == null)
            {
                return;
            }

            var low = ReadMemberFloat(element, "lowValue");
            var high = ReadMemberFloat(element, "highValue");
            if (!low.HasValue || !high.HasValue)
            {
                return;
            }

            if (value < low.Value || value > high.Value)
            {
                throw ChievfxMcpRuntimeUiControlValueParsing.InvalidValue(
                    element.GetType().Name,
                    token,
                    $"Out of range [{low.Value.ToString(CultureInfo.InvariantCulture)}, {high.Value.ToString(CultureInfo.InvariantCulture)}].",
                    new object[] { low.Value, high.Value });
            }
        }

        private static float? ReadMemberFloat(object target, string memberName)
        {
            var value = ReadSimpleMemberValue(target, memberName);
            return value switch
            {
                float floatValue => floatValue,
                double doubleValue => (float)doubleValue,
                int intValue => intValue,
                _ => null,
            };
        }

        internal static Dictionary<string, object?> TypeTextIntoFocusedTextField(JToken args, UiToolkitDependencyStatus status, bool requireTarget)
        {
            var warnings = new List<string>();
            var append = ReadBool(args, "append", false);
            var submit = ReadBool(args, "submit", false);
            var text = ReadString(args, "text") ?? ReadString(args, "value")
                ?? throw new ArgumentException("ui-runtime-type-text requires 'text'.");

            var result = CreateEnvelope("tool://ui-runtime-type-text#uitoolkit", status);
            result["framework"] = "uitoolkit";
            result["playMode"] = IsRuntimePlayModeActive();
            result["runtimeAvailable"] = EnsureRuntimeReadAllowed(warnings);
            result["focusedElementBefore"] = CreateFocusedElementRow(status);

            var resolution = ResolveRuntimeInteractionTarget(args, status, warnings);
            result["stack"] = resolution.Stack;
            result["resolvedBy"] = resolution.ResolvedBy;

            var element = resolution.Target as VisualElement;
            var group = resolution.Group ?? (element == null ? null : PanelGroup.FromElement(element));
            var valueProperty = element?.GetType().GetProperty("value", BindingFlags.Public | BindingFlags.Instance);
            var isTextField = element != null
                && valueProperty != null
                && valueProperty.CanWrite
                && (Nullable.GetUnderlyingType(valueProperty.PropertyType) ?? valueProperty.PropertyType) == typeof(string);
            result["resolved"] = isTextField;
            result["target"] = element == null ? null : CreateVisualElementRow(element, status, group!, includeTextAndValue: true);
            result["targetStateBefore"] = element == null ? null : CreateVisualElementStateRow(element, status);

            if (!isTextField)
            {
                if (requireTarget)
                {
                    throw new ArgumentException(element == null
                        ? "ui-runtime-type-text could not resolve a UI Toolkit target from path or screen position."
                        : $"Target '{GetVisualElementPath(element)}' has no writable string value (not a TextField).");
                }

                result["warnings"] = warnings.Distinct().ToArray();
                return result;
            }

            if (!IsRuntimePlayModeActive())
            {
                throw new InvalidOperationException("ui-runtime-type-text requires Play Mode. Enter Play Mode before typing into runtime text fields.");
            }

            var textBefore = valueProperty!.GetValue(element) as string ?? string.Empty;
            result["controlType"] = element!.GetType().Name;
            result["textBefore"] = textBefore;

            TypeWithRealKeyboard(element, status, text, append, focus: true, warnings);

            if (submit)
            {
                DispatchNavigationSubmit(element);
                element.Blur();
            }

            result["textAfter"] = valueProperty.GetValue(element) as string;
            result["focusedElementAfter"] = CreateFocusedElementRow(status);
            result["targetStateAfter"] = CreateVisualElementStateRow(element, status);
            result["warnings"] = warnings.Distinct().ToArray();
            return result;
        }

        /// <summary>
        /// Imitates a real player: focuses the field, then dispatches one KeyDownEvent per character
        /// so the text-editing engine inserts characters and repaints the visible field (placeholder
        /// hides, value commits, ChangeEvents fire). Uses only API stable since Unity 2019/2022.
        /// </summary>
        private static void TypeWithRealKeyboard(VisualElement element, UiToolkitDependencyStatus status, string text, bool append, bool focus, List<string> warnings)
        {
            if (focus)
            {
                element.Focus();
            }

            // Replace mode: select existing text so the first keystroke overwrites it, just like a
            // player pressing Ctrl+A. Append mode: drop the selection and place the caret at the end.
            if (!append)
            {
                if (!TryInvokeNoArg(element, "SelectAll"))
                {
                    var current = (element.GetType().GetProperty("value", BindingFlags.Public | BindingFlags.Instance)?.GetValue(element) as string) ?? string.Empty;
                    TrySetCaretToEnd(element, current.Length);
                }
            }
            else
            {
                var current = (element.GetType().GetProperty("value", BindingFlags.Public | BindingFlags.Instance)?.GetValue(element) as string) ?? string.Empty;
                TrySetCaretToEnd(element, current.Length);
            }

            // Dispatch key events to the element that actually holds focus (the inner text input),
            // falling back to the field itself when the focus controller does not expose one.
            var editTarget = GetCurrentFocusedElement(status) as VisualElement ?? element;
            var dispatched = 0;
            foreach (var character in text)
            {
                var keyCode = MapCharToKeyCode(character);
                try
                {
                    using (var keyDown = KeyDownEvent.GetPooled(character, keyCode, EventModifiers.None))
                    {
                        editTarget.SendEvent(keyDown);
                    }

                    dispatched++;
                }
                catch (Exception ex)
                {
                    warnings.Add($"KeyDownEvent dispatch failed at character {dispatched}: {ex.Message}");
                    break;
                }
            }

            // Fallback: if real key events did not change the value (some controls block synthetic key
            // input), commit the text directly so the QA flow still produces the intended state.
            var valueProperty = element.GetType().GetProperty("value", BindingFlags.Public | BindingFlags.Instance);
            var produced = valueProperty?.GetValue(element) as string ?? string.Empty;
            var expected = append ? text /* suffix */ : text;
            var matched = append ? produced.EndsWith(text, StringComparison.Ordinal) : string.Equals(produced, expected, StringComparison.Ordinal);
            if (!matched && valueProperty != null && valueProperty.CanWrite)
            {
                warnings.Add("Synthetic keystrokes did not update the field; committed text via value setter as a fallback.");
                var fallbackText = append ? produced + text : text;
                valueProperty.SetValue(element, fallbackText);
                TrySetCaretToEnd(element, fallbackText.Length);
            }
        }

        private static object? GetCurrentFocusedElement(UiToolkitDependencyStatus status)
        {
            return FindRuntimePanelGroups(status)
                .Select(group =>
                {
                    var focusController = group.Panel == null ? null : GetMemberValue(group.Panel, "focusController");
                    return focusController == null ? null : GetMemberValue(focusController, "focusedElement");
                })
                .FirstOrDefault(focused => focused != null);
        }

        private static bool TryInvokeNoArg(object target, string methodName)
        {
            var method = target.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (method == null)
            {
                return false;
            }

            try
            {
                method.Invoke(target, null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static KeyCode MapCharToKeyCode(char character)
        {
            switch (character)
            {
                case '\n':
                case '\r':
                    return KeyCode.Return;
                case '\t':
                    return KeyCode.Tab;
                case ' ':
                    return KeyCode.Space;
                case '\b':
                    return KeyCode.Backspace;
                default:
                    return KeyCode.None;
            }
        }

        private static void TrySetCaretToEnd(VisualElement element, int caretIndex)
        {
            foreach (var propertyName in new[] { "cursorIndex", "selectIndex" })
            {
                var property = element.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                if (property != null && property.CanWrite && property.PropertyType == typeof(int))
                {
                    try
                    {
                        property.SetValue(element, caretIndex);
                    }
                    catch
                    {
                        // Best-effort caret placement; some controls expose caret state differently.
                    }
                }
            }
        }

        internal static Dictionary<string, object?>? CreateFocusedElementRow(UiToolkitDependencyStatus status)
        {
            var focused = FindRuntimePanelGroups(status)
                .Select(group =>
                {
                    var focusController = group.Panel == null ? null : GetMemberValue(group.Panel, "focusController");
                    var focusedElement = focusController == null ? null : GetMemberValue(focusController, "focusedElement");
                    return new { group, focused = focusedElement };
                })
                .FirstOrDefault(item => item.focused != null);
            return focused == null || focused.focused == null
                ? null
                : CreateVisualElementRow(focused.focused, status, focused.group, includeTextAndValue: true);
        }

        internal static Dictionary<string, object?> CreateVisualElementStateRow(object visualElement, UiToolkitDependencyStatus status)
        {
            return new Dictionary<string, object?>
            {
                ["target"] = CreateVisualElementRow(visualElement, status, PanelGroup.FromElement(visualElement), includeTextAndValue: true),
                ["focused"] = IsFocusedElement(visualElement),
                ["value"] = ReadSimpleMemberValue(visualElement, "value"),
            };
        }

        internal static bool IsFocusedElement(object visualElement)
        {
            var panel = GetPanel(visualElement);
            var focusController = panel == null ? null : GetMemberValue(panel, "focusController");
            return focusController != null && ReferenceEquals(GetMemberValue(focusController, "focusedElement"), visualElement);
        }

        internal static void SetWritableProperty(object target, string propertyName, object? value)
        {
            var property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (property != null && property.CanWrite)
            {
                property.SetValue(target, value);
            }
        }
    }
}
