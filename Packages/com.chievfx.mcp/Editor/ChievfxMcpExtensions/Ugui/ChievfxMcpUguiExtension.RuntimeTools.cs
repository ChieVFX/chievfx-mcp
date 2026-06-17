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
using static Chievfx.Mcp.Extensions.Ugui.UguiSchemas;
using static Chievfx.Mcp.Extensions.Ugui.UguiSharedHelpers;

namespace Chievfx.Mcp.Extensions.Ugui
{
    internal static class UguiRuntimeTools
    {
        internal static Dictionary<string, object?> ProbeRuntimeScreenPosition(JToken args, UguiDependencyStatus status)
        {
            ChievfxMcpRuntimeUiProbeCompact.EnsurePlayModeForProbe(IsRuntimePlayModeActive());

            var warnings = new List<string>();
            var maxRows = Mathf.Clamp(ReadInt(args, "maxRows", 256), 1, 1024);
            var position = ReadScreenPosition(args, warnings, status);
            var probe = ChievfxMcpRuntimeUiProbeCompact.CreateProbeBlock(
                position.ScreenSize,
                position.ScreenPosition,
                position.NormalizedPosition);

            AddRuntimeWarnings(status, warnings);
            if (IsOutsideScreen(position.ScreenPosition, position.ScreenSize))
            {
                warnings.Add("Coordinate is outside current screen/game-view bounds.");
            }

            var eventSystem = GetCurrentEventSystem(status);
            if (eventSystem == null)
            {
                warnings.Add("No active EventSystem.current was found; runtime uGUI raycast probe cannot run.");
                return ChievfxMcpRuntimeUiProbeCompact.CreateProbeResult(
                    probe,
                    runtimeAvailable: true,
                    maxRows,
                    truncated: false,
                    warnings,
                    ugui: ChievfxMcpRuntimeUiProbeCompact.CreateUguiSection(
                        available: true,
                        probed: false,
                        Array.Empty<Dictionary<string, object?>>(),
                        warnings));
            }

            status.CanvasType?.GetMethod("ForceUpdateCanvases", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
            var raycastResults = RaycastAll(status, eventSystem, position.ScreenPosition, warnings);
            var stack = raycastResults.Length == 0
                ? CreateRuntimeRectHitStack(position.ScreenPosition, status)
                    .Select(CompactRectHitStackRow)
                    .ToArray()
                : raycastResults
                    .Select((raycastResult, index) => CreateCompactProbeStackRow(raycastResult, index, status))
                    .ToArray();
            var truncated = stack.Length > maxRows;
            if (truncated)
            {
                stack = stack.Take(maxRows).ToArray();
            }

            return ChievfxMcpRuntimeUiProbeCompact.CreateProbeResult(
                probe,
                runtimeAvailable: true,
                maxRows,
                truncated,
                warnings,
                ugui: ChievfxMcpRuntimeUiProbeCompact.CreateUguiSection(
                    available: true,
                    probed: true,
                    stack,
                    truncated: truncated));
        }

        internal static Dictionary<string, object?> RuntimeClickAtPosition(JToken args, UguiDependencyStatus status)
        {
            var result = RuntimeClick(args, status);
            var resolved = result.TryGetValue("target", out var target) && target != null;
            result["resolved"] = resolved;
            result["framework"] = "ugui";
            return result;
        }

        internal static Dictionary<string, object?> RuntimeClick(JToken args, UguiDependencyStatus status)
        {
            var warnings = new List<string>();
            var dryRun = ReadBool(args, "dryRun", false);
            var sequence = (ReadString(args, "sequence") ?? "pointer").Trim().ToLowerInvariant();
            var position = ReadScreenPosition(args, warnings, status);
            var result = CreateRuntimeInteractionEnvelope("tool://ui-runtime-click#ugui", status, dryRun, args, warnings);
            AddCoordinateInfo(result, position);

            var eventSystem = RequireRuntimeEventSystem(status, warnings, dryRun);
            result["eventSystem"] = eventSystem == null ? null : CreateGameObjectRow(eventSystem.gameObject);
            var target = ResolveRuntimeInteractionTarget(args, status, eventSystem, position.ScreenPosition, warnings, out var stack);
            result["stack"] = stack;
            result["target"] = target == null ? null : CreateRuntimeElementRow(target, status);
            result["targetStateBefore"] = target == null ? null : CreateControlStateRow(target, status);
            result["intendedHandler"] = target == null ? null : CreateClickHandlerRow(target, sequence);

            if (target == null)
            {
                warnings.Add("No runtime uGUI target resolved for click.");
            }

            if (!dryRun)
            {
                EnsureRuntimeMutationAllowed(args, warnings);
                if (eventSystem == null || target == null)
                {
                    throw new InvalidOperationException("Runtime click requires an active EventSystem and a resolved target.");
                }

                if (sequence == "submit")
                {
                    var handler = ExecuteEvents.GetEventHandler<ISubmitHandler>(target) ?? target;
                    eventSystem.SetSelectedGameObject(handler, new BaseEventData(eventSystem));
                    ExecuteEvents.Execute(handler, new BaseEventData(eventSystem), ExecuteEvents.submitHandler);
                }
                else
                {
                    var clickTarget = ExecuteEvents.GetEventHandler<IPointerClickHandler>(target) ?? target;
                    var pointer = CreatePointerEventData(eventSystem, position.ScreenPosition);
                    ExecuteEvents.Execute(clickTarget, pointer, ExecuteEvents.pointerEnterHandler);
                    ExecuteEvents.Execute(clickTarget, pointer, ExecuteEvents.pointerDownHandler);
                    ExecuteEvents.Execute(clickTarget, pointer, ExecuteEvents.pointerUpHandler);
                    ExecuteEvents.Execute(clickTarget, pointer, ExecuteEvents.pointerClickHandler);
                }
            }

            result["selectedObjectAfter"] = CreateSelectedObjectRow(status);
            result["targetStateAfter"] = target == null ? null : CreateControlStateRow(target, status);
            result["warnings"] = warnings.ToArray();
            return result;
        }

        internal static Dictionary<string, object?> RuntimeDrag(JToken args, UguiDependencyStatus status)
        {
            var warnings = new List<string>();
            var dryRun = ReadBool(args, "dryRun", false);
            var start = ReadNamedScreenPosition(args, "startScreenPosition", "startNormalized", warnings, status);
            var end = ReadNamedScreenPosition(args, "endScreenPosition", "endNormalized", warnings, status);
            var result = CreateRuntimeInteractionEnvelope("tool://ugui-runtime-drag", status, dryRun, args, warnings);
            result["startCoordinateConvention"] = CreateCoordinateInfo(start);
            result["endCoordinateConvention"] = CreateCoordinateInfo(end);

            var eventSystem = RequireRuntimeEventSystem(status, warnings, dryRun);
            result["eventSystem"] = eventSystem == null ? null : CreateGameObjectRow(eventSystem.gameObject);
            var target = ResolveRuntimeInteractionTarget(args, status, eventSystem, start.ScreenPosition, warnings, out var stack);
            result["stack"] = stack;
            result["target"] = target == null ? null : CreateRuntimeElementRow(target, status);
            result["targetStateBefore"] = target == null ? null : CreateControlStateRow(target, status);
            result["intendedHandler"] = target == null ? null : CreateDragHandlerRow(target);

            if (target == null)
            {
                warnings.Add("No runtime uGUI target resolved for drag.");
            }

            if (!dryRun)
            {
                EnsureRuntimeMutationAllowed(args, warnings);
                if (eventSystem == null || target == null)
                {
                    throw new InvalidOperationException("Runtime drag requires an active EventSystem and a resolved target.");
                }

                var dragTarget = ExecuteEvents.GetEventHandler<IDragHandler>(target) ?? target;
                var pointer = CreatePointerEventData(eventSystem, start.ScreenPosition);
                pointer.pointerDrag = dragTarget;
                ExecuteEvents.Execute(dragTarget, pointer, ExecuteEvents.initializePotentialDrag);
                ExecuteEvents.Execute(dragTarget, pointer, ExecuteEvents.beginDragHandler);
                pointer.delta = end.ScreenPosition - start.ScreenPosition;
                pointer.position = end.ScreenPosition;
                ExecuteEvents.Execute(dragTarget, pointer, ExecuteEvents.dragHandler);
                ExecuteEvents.Execute(dragTarget, pointer, ExecuteEvents.endDragHandler);
            }

            result["selectedObjectAfter"] = CreateSelectedObjectRow(status);
            result["targetStateAfter"] = target == null ? null : CreateControlStateRow(target, status);
            result["warnings"] = warnings.ToArray();
            return result;
        }

        internal static Dictionary<string, object?> RuntimeSelect(JToken args, UguiDependencyStatus status)
        {
            var warnings = new List<string>();
            var dryRun = ReadBool(args, "dryRun", false);
            var clear = ReadBool(args, "clear", false);
            var result = CreateRuntimeInteractionEnvelope("tool://ugui-runtime-select", status, dryRun, args, warnings);
            var eventSystem = RequireRuntimeEventSystem(status, warnings, dryRun);
            result["eventSystem"] = eventSystem == null ? null : CreateGameObjectRow(eventSystem.gameObject);
            var target = clear ? null : ResolveRequiredGameObject(args, "targetPath", "instanceId");
            result["target"] = target == null ? null : CreateRuntimeElementRow(target, status);
            result["targetStateBefore"] = target == null ? null : CreateControlStateRow(target, status);
            result["intendedHandler"] = new Dictionary<string, object?>
            {
                ["event"] = clear ? "clearSelection" : "setSelectedGameObject",
                ["path"] = target == null ? null : GetTransformPath(target.transform),
            };

            if (!dryRun)
            {
                EnsureRuntimeMutationAllowed(args, warnings);
                if (eventSystem == null)
                {
                    throw new InvalidOperationException("Runtime select requires an active EventSystem.");
                }

                eventSystem.SetSelectedGameObject(target, new BaseEventData(eventSystem));
            }

            result["selectedObjectAfter"] = CreateSelectedObjectRow(status);
            result["targetStateAfter"] = target == null ? null : CreateControlStateRow(target, status);
            result["warnings"] = warnings.ToArray();
            return result;
        }

        internal static Dictionary<string, object?> RuntimeSetControlValue(JToken args, UguiDependencyStatus status)
        {
            var warnings = new List<string>();
            var dryRun = ReadBool(args, "dryRun", false);
            var result = CreateRuntimeInteractionEnvelope("tool://ugui-runtime-set-control-value", status, dryRun, args, warnings);
            var target = ResolveRequiredGameObject(args, "targetPath", "instanceId");
            var invokeCallbacks = ReadBool(args, "invokeCallbacks", true);
            if (args["invokeCallbacks"] == null)
            {
                warnings.Add("invokeCallbacks was not specified; defaulted to true and may fire game callbacks.");
            }

            result["target"] = CreateRuntimeElementRow(target, status);
            result["targetStateBefore"] = CreateControlStateRow(target, status);
            result["callbackPolicy"] = new Dictionary<string, object?>
            {
                ["invokeCallbacks"] = invokeCallbacks,
                ["explicit"] = args["invokeCallbacks"] != null,
            };
            result["intendedHandler"] = CreateSetControlHandlerRow(target, status, args, invokeCallbacks);

            if (!dryRun)
            {
                EnsureRuntimeMutationAllowed(args, warnings);
                ApplyRuntimeControlValue(target, status, args, invokeCallbacks);
            }

            result["selectedObjectAfter"] = CreateSelectedObjectRow(status);
            result["targetStateAfter"] = CreateControlStateRow(target, status);
            result["warnings"] = warnings.ToArray();
            return result;
        }

        internal static Dictionary<string, object?> TypeTextIntoFocusedTextField(JToken args, UguiDependencyStatus status, bool requireTarget)
        {
            var warnings = new List<string>();
            var dryRun = ReadBool(args, "dryRun", false);
            var append = ReadBool(args, "append", false);
            var submit = ReadBool(args, "submit", false);
            var focus = ReadBool(args, "focus", true);
            var invokeCallbacks = ReadBool(args, "invokeCallbacks", true);
            var text = ReadString(args, "text") ?? ReadString(args, "value")
                ?? throw new ArgumentException("ui-runtime-type-text requires 'text'.");

            var result = CreateRuntimeInteractionEnvelope("tool://ui-runtime-type-text#ugui", status, dryRun, args, warnings);
            result["framework"] = "ugui";

            var position = ReadScreenPosition(args, warnings, status);
            var eventSystem = GetCurrentEventSystem(status) as EventSystem;
            AddRuntimeWarnings(status, warnings);
            result["eventSystem"] = eventSystem == null ? null : CreateGameObjectRow(eventSystem.gameObject);

            var target = ResolveRuntimeInteractionTarget(args, status, eventSystem, position.ScreenPosition, warnings, out var stack);
            result["stack"] = stack;
            var inputField = target == null ? null : ResolveTextInputComponent(target, status);
            var resolved = inputField != null;
            result["resolved"] = resolved;
            result["target"] = target == null ? null : CreateRuntimeElementRow(target, status);
            result["targetStateBefore"] = target == null ? null : CreateControlStateRow(target, status);

            if (!resolved)
            {
                if (requireTarget)
                {
                    throw new ArgumentException(target == null
                        ? "ui-runtime-type-text could not resolve a uGUI target from targetPath/instanceId or screenPosition."
                        : $"Target '{GetTransformPath(target!.transform)}' has no uGUI InputField or TMP_InputField.");
                }

                result["warnings"] = warnings.Distinct().ToArray();
                return result;
            }

            var controlType = inputField!.GetType().Name;
            var textBefore = GetPropertyValue(inputField, "text") as string ?? string.Empty;
            var resultingText = append ? textBefore + text : text;
            result["controlType"] = controlType;
            result["textBefore"] = textBefore;
            result["plan"] = new Dictionary<string, object?>
            {
                ["controlType"] = controlType,
                ["path"] = GetTransformPath(target!.transform),
                ["focus"] = focus,
                ["append"] = append,
                ["submit"] = submit,
                ["invokeCallbacks"] = invokeCallbacks,
                ["textToType"] = text,
                ["resultingText"] = resultingText,
                ["setter"] = invokeCallbacks ? "text property (fires onValueChanged)" : "SetTextWithoutNotify when available",
                ["guard"] = "dryRun must be false, Play Mode active, and allowStateMutation true before focusing or typing.",
            };

            if (!dryRun)
            {
                EnsureRuntimeMutationAllowed(args, warnings);
                if (focus)
                {
                    if (eventSystem == null)
                    {
                        throw new InvalidOperationException("ui-runtime-type-text with focus:true requires an active EventSystem.current.");
                    }

                    eventSystem.SetSelectedGameObject(target, new BaseEventData(eventSystem));
                    InvokeReflectedMethod(inputField, "ActivateInputField");
                }

                ApplyInputFieldText(inputField, resultingText, invokeCallbacks, warnings);
                TrySetProperty(inputField, "caretPosition", resultingText.Length);
                InvokeReflectedMethod(inputField, "ForceLabelUpdate");

                if (submit)
                {
                    InvokeReflectedStringEvent(inputField, "onEndEdit", resultingText);
                    InvokeReflectedStringEvent(inputField, "onSubmit", resultingText);
                    InvokeReflectedMethod(inputField, "DeactivateInputField");
                }
            }

            result["textAfter"] = GetPropertyValue(inputField, "text") as string;
            result["selectedObjectAfter"] = CreateSelectedObjectRow(status);
            result["targetStateAfter"] = CreateControlStateRow(target, status);
            result["warnings"] = warnings.Distinct().ToArray();
            return result;
        }

        internal static Component? ResolveTextInputComponent(GameObject target, UguiDependencyStatus status)
        {
            if (status.InputFieldType != null && target.GetComponent(status.InputFieldType) is Component inputField)
            {
                return inputField;
            }

            var tmpInputFieldType = FindType("TMPro.TMP_InputField");
            return tmpInputFieldType == null ? null : target.GetComponent(tmpInputFieldType) as Component;
        }

        private static void ApplyInputFieldText(Component inputField, string text, bool invokeCallbacks, List<string> warnings)
        {
            if (!invokeCallbacks)
            {
                var method = inputField.GetType().GetMethod("SetTextWithoutNotify", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
                if (method != null)
                {
                    method.Invoke(inputField, new object[] { text });
                    return;
                }

                warnings.Add("SetTextWithoutNotify was not found; falling back to text property setter which fires onValueChanged.");
            }

            SetProperty(inputField, "text", text);
        }

        private static void InvokeReflectedMethod(Component target, string methodName)
        {
            target.GetType()
                .GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)
                ?.Invoke(target, null);
        }

        private static void InvokeReflectedStringEvent(Component target, string eventPropertyName, string value)
        {
            var unityEvent = GetPropertyValue(target, eventPropertyName);
            unityEvent?.GetType()
                .GetMethod("Invoke", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null)
                ?.Invoke(unityEvent, new object[] { value });
        }

        private static void TrySetProperty(Component target, string propertyName, object value)
        {
            var property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (property != null && property.CanWrite)
            {
                try
                {
                    property.SetValue(target, value);
                }
                catch
                {
                    // Best-effort caret placement; ignore controls that reject the value.
                }
            }
        }

        internal static Dictionary<string, object?> ReadRuntimeStatus(string uri, UguiDependencyStatus status)
        {
            var warnings = new List<string>();
            var result = CreateEnvelope(uri, status);
            AddCoordinateInfo(result, RuntimeScreenPosition.FromScreenPosition(new Vector2(Screen.width * 0.5f, Screen.height * 0.5f)));
            result["playMode"] = IsRuntimePlayModeActive();
            result["runtimeAvailable"] = EnsureRuntimeReadAllowed(warnings);
            if (Equals(result["runtimeAvailable"], false))
            {
                result["eventSystem"] = null;
                result["selectedObject"] = null;
                result["canvasCount"] = 0;
                result["activeCanvasCount"] = 0;
            }
            else
            {
                result["eventSystem"] = GetCurrentEventSystem(status) is Component eventSystem ? CreateGameObjectRow(eventSystem.gameObject) : null;
                result["selectedObject"] = CreateSelectedObjectRow(status);
                result["canvasCount"] = FindCanvases(status, includeInactive: true).Length;
                result["activeCanvasCount"] = FindRuntimeCanvases(status).Count(canvas => canvas.gameObject.activeInHierarchy && IsEnabledComponent(canvas));
                AddRuntimeWarnings(status, warnings);
            }

            result["warnings"] = warnings.ToArray();
            return result;
        }

        internal static Dictionary<string, object?> ReadRuntimeCanvases(string uri, UguiDependencyStatus status)
        {
            var warnings = new List<string>();
            var result = CreateEnvelope(uri, status);
            result["playMode"] = IsRuntimePlayModeActive();
            result["runtimeAvailable"] = EnsureRuntimeReadAllowed(warnings);
            if (Equals(result["runtimeAvailable"], false))
            {
                result["count"] = 0;
                result["canvases"] = Array.Empty<Dictionary<string, object?>>();
                result["warnings"] = warnings.ToArray();
                return result;
            }

            if (Equals(result["runtimeAvailable"], true))
            {
                AddRuntimeWarnings(status, warnings);
            }

            var canvases = FindRuntimeCanvases(status);
            result["count"] = canvases.Length;
            result["canvases"] = canvases.Select(canvas => CreateRuntimeCanvasRow(canvas, status)).ToArray();
            result["warnings"] = warnings.ToArray();
            return result;
        }

        internal static Dictionary<string, object?> ReadRuntimeVisibleTree(string uri, UguiDependencyStatus status)
        {
            var warnings = new List<string>();
            var result = CreateEnvelope(uri, status);
            result["playMode"] = IsRuntimePlayModeActive();
            result["runtimeAvailable"] = EnsureRuntimeReadAllowed(warnings);
            if (Equals(result["runtimeAvailable"], false))
            {
                result["count"] = 0;
                result["canvases"] = Array.Empty<Dictionary<string, object?>>();
                result["warnings"] = warnings.ToArray();
                return result;
            }

            if (Equals(result["runtimeAvailable"], true))
            {
                AddRuntimeWarnings(status, warnings);
            }

            var canvases = FindRuntimeCanvases(status)
                .Where(canvas => canvas.gameObject.activeInHierarchy && IsEnabledComponent(canvas))
                .ToArray();
            result["count"] = canvases.Length;
            result["canvases"] = canvases.Select(canvas =>
            {
                var row = CreateRuntimeCanvasRow(canvas, status);
                var elements = canvas.GetComponentsInChildren<RectTransform>(false)
                    .Where(rect => rect.gameObject != canvas.gameObject && IsRuntimeVisibleUiElement(rect.gameObject, status))
                    .Take(256)
                    .Select(rect => CreateRuntimeElementRow(rect.gameObject, status))
                    .ToArray();
                row["elements"] = elements;
                row["elementCount"] = elements.Length;
                row["truncated"] = canvas.GetComponentsInChildren<RectTransform>(false).Count(rect => rect.gameObject != canvas.gameObject) > elements.Length;
                return row;
            }).ToArray();
            result["warnings"] = warnings.ToArray();
            return result;
        }

        internal static Dictionary<string, object?> ControlFind(JToken args, UguiDependencyStatus status)
        {
            status.CanvasType?.GetMethod("ForceUpdateCanvases", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);

            var warnings = new List<string>();
            var nameFilter = ReadString(args, "name");
            var controlTypeFilter = ChievfxMcpRuntimeUiControlFind.NormalizeControlTypeFilter(ReadString(args, "controlType"));
            var screenSize = ResolveRuntimeUiScreenSize(status);
            var matches = FindCanvases(status, includeInactive: false)
                .Where(canvas => canvas.gameObject.activeInHierarchy && IsEnabledComponent(canvas))
                .SelectMany(canvas => canvas.GetComponentsInChildren<RectTransform>(false))
                .Select(rect => rect.gameObject)
                .Distinct()
                .SelectMany(target => GetClickableControlComponents(target, status).Select(control => (target, control)))
                .Where(pair => IsEnabledClickableControl(pair.target, pair.control))
                .Where(pair => string.IsNullOrWhiteSpace(nameFilter) || string.Equals(pair.target.name, nameFilter, StringComparison.Ordinal))
                .Select(pair => (pair.target, pair.control, controlType: ChievfxMcpRuntimeUiControlFind.NormalizeControlType(pair.control.GetType())))
                .Where(entry => string.IsNullOrWhiteSpace(controlTypeFilter)
                    || string.Equals(entry.controlType, controlTypeFilter, StringComparison.Ordinal))
                .Where(entry => TryGetUguiScreenZone(entry.target, status, screenSize, out _))
                .ToArray();

            var rows = matches
                .Select(entry =>
                {
                    TryGetUguiScreenZone(entry.target, status, screenSize, out var zone);
                    return new Dictionary<string, object?>
                    {
                        ["framework"] = "ugui",
                        ["path"] = GetTransformPath(entry.target.transform),
                        ["instanceId"] = UnityObjectIdentity.GetLegacyInstanceId(entry.target),
                        ["controlType"] = entry.controlType,
                        ["zone"] = zone,
                    };
                })
                .ToArray();

            return new Dictionary<string, object?>
            {
                ["framework"] = "ugui",
                ["available"] = status.Available,
                ["playMode"] = IsRuntimePlayModeActive(),
                ["totalMatches"] = matches.Length,
                ["nameFilter"] = nameFilter,
                ["controlTypeFilter"] = controlTypeFilter,
                ["controls"] = rows,
                ["warnings"] = warnings.ToArray(),
            };
        }
    }
}
