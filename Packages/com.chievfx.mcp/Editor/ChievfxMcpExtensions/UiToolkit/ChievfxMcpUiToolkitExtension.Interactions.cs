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
using static Chievfx.Mcp.Extensions.UiToolkit.UiToolkitSchemas;

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

        internal static void EnsureRuntimeMutationAllowed(JToken args)
        {
            if (!IsRuntimePlayModeActive())
            {
                throw new InvalidOperationException("Runtime UI Toolkit mutations are gated to Play Mode. Enter Play Mode before dispatching interactions or changing control values.");
            }

            if (!ReadBool(args, "allowStateMutation", false))
            {
                throw new InvalidOperationException("Runtime UI Toolkit mutation requires explicit allowStateMutation:true.");
            }
        }

        internal static RuntimeInteractionResolution ResolveRuntimeInteractionTarget(JToken args, UiToolkitDependencyStatus status, List<string> warnings)
        {
            var explicitTarget = ResolveVisualElement(args, status);
            if (explicitTarget != null)
            {
                return RuntimeInteractionResolution.FromTarget(explicitTarget, "explicitTarget");
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
            var targetRef = ReadString(args, "visualElementRef") ?? ReadString(args, "targetRef");
            var targetPath = ReadString(args, "targetPath") ?? ReadString(args, "path");
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

        internal static Dictionary<string, object?> CreateRuntimeInteractionPlan(string action, object? target, JToken args)
        {
            return new Dictionary<string, object?>
            {
                ["action"] = action,
                ["targetRef"] = target == null ? null : CreateVisualElementRef(target),
                ["events"] = PlannedEvents(action),
                ["delta"] = action == "pointerDrag" && TryReadVector2(args["delta"], out var delta) ? CreateVector2Row(delta) : null,
                ["steps"] = action == "pointerDrag" ? Mathf.Clamp(ReadInt(args, "steps", 12), 1, 120) : null,
                ["value"] = action == "setValue" ? ReadSimpleToken(args["value"] ?? args["text"] ?? args["isOn"]) : null,
                ["invokeCallbacks"] = action == "setValue" ? ReadBool(args, "invokeCallbacks", true) : null,
                ["guard"] = "dryRun must be false, Play Mode active, and allowStateMutation true before dispatch or value mutation.",
            };
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

            var value = ConvertControlValue(args["value"] ?? args["text"] ?? args["isOn"], valueProperty.PropertyType, valueProperty.GetValue(element));
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

        internal static object? ConvertControlValue(JToken? token, Type targetType, object? currentValue)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return currentValue;
            }

            var nullableType = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (nullableType == typeof(string))
            {
                return token.Value<string>() ?? string.Empty;
            }

            if (nullableType == typeof(bool))
            {
                return token.Value<bool>();
            }

            if (nullableType.IsEnum)
            {
                return token.Type == JTokenType.String
                    ? Enum.Parse(nullableType, token.Value<string>() ?? string.Empty, ignoreCase: true)
                    : Enum.ToObject(nullableType, token.Value<int>());
            }

            if (nullableType == typeof(int))
            {
                return token.Value<int>();
            }

            if (nullableType == typeof(float))
            {
                return token.Value<float>();
            }

            if (nullableType == typeof(double))
            {
                return token.Value<double>();
            }

            throw new ArgumentException("Unsupported UI Toolkit value type: " + targetType.FullName);
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
