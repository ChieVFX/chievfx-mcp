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
            var warnings = new List<string>();
            var includeAllComponents = ReadBool(args, "includeAllComponents", false);
            var position = ReadScreenPosition(args, warnings, status);
            var result = CreateEnvelope("tool://ugui-runtime-probe-screen-position", status);
            AddCoordinateInfo(result, position);
            result["warnings"] = warnings.ToArray();
            result["stack"] = Array.Empty<Dictionary<string, object?>>();
            result["hierarchy"] = Array.Empty<string>();
            result["componentScope"] = includeAllComponents ? "all" : "ugui";
            result["count"] = 0;
            result["top"] = null;

            if (!EnsureRuntimeReadAllowed(warnings))
            {
                result["runtimeAvailable"] = false;
                result["warnings"] = warnings.ToArray();
                return result;
            }

            result["runtimeAvailable"] = true;
            AddRuntimeWarnings(status, warnings);
            if (IsOutsideScreen(position.ScreenPosition, position.ScreenSize))
            {
                warnings.Add("Coordinate is outside current screen/game-view bounds.");
            }

            var eventSystem = GetCurrentEventSystem(status);
            result["eventSystem"] = eventSystem == null ? null : CreateGameObjectRow(eventSystem.gameObject);
            if (eventSystem == null)
            {
                warnings.Add("No active EventSystem.current was found; runtime uGUI raycast probe cannot run.");
                result["warnings"] = warnings.ToArray();
                return result;
            }

            status.CanvasType?.GetMethod("ForceUpdateCanvases", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
            var raycastResults = RaycastAll(status, eventSystem, position.ScreenPosition, warnings);
            var stack = raycastResults.Length == 0
                ? CreateRuntimeRectHitStack(position.ScreenPosition, status)
                : raycastResults.Select((raycastResult, index) => CreateRuntimeStackRow(raycastResult, index, status)).ToArray();
            result["stack"] = stack;
            result["hierarchy"] = CreateRuntimeProbeHierarchyLines(stack, includeAllComponents, status);
            result["count"] = stack.Length;
            result["top"] = stack.FirstOrDefault();
            result["warnings"] = warnings.ToArray();
            return result;
        }

        internal static Dictionary<string, object?> RuntimeClick(JToken args, UguiDependencyStatus status)
        {
            var warnings = new List<string>();
            var dryRun = ReadBool(args, "dryRun", false);
            var sequence = (ReadString(args, "sequence") ?? "pointer").Trim().ToLowerInvariant();
            var position = ReadScreenPosition(args, warnings, status);
            var result = CreateRuntimeInteractionEnvelope("tool://ugui-runtime-click", status, dryRun, args, warnings);
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

        internal static Dictionary<string, object?> ReadRuntimeInteractables(string uri, UguiDependencyStatus status)
        {
            var warnings = new List<string>();
            var result = CreateEnvelope(uri, status);
            result["playMode"] = IsRuntimePlayModeActive();
            result["runtimeAvailable"] = EnsureRuntimeReadAllowed(warnings);
            if (Equals(result["runtimeAvailable"], false))
            {
                result["count"] = 0;
                result["interactables"] = Array.Empty<Dictionary<string, object?>>();
                result["warnings"] = warnings.ToArray();
                return result;
            }

            if (Equals(result["runtimeAvailable"], true))
            {
                AddRuntimeWarnings(status, warnings);
            }

            var rows = FindRuntimeCanvases(status)
                .SelectMany(canvas => canvas.GetComponentsInChildren<RectTransform>(false))
                .Select(rect => rect.gameObject)
                .Distinct()
                .Select(go => CreateRuntimeInteractableRow(go, status))
                .Where(row => row != null)
                .Cast<Dictionary<string, object?>>()
                .Take(256)
                .ToArray();
            result["count"] = rows.Length;
            result["interactables"] = rows;
            result["warnings"] = warnings.ToArray();
            return result;
        }
    }
}
