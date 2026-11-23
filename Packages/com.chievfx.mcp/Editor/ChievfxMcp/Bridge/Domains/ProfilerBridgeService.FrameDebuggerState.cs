#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;
using PackageManagerClient = UnityEditor.PackageManager.Client;
using PackageManagerPackageInfo = UnityEditor.PackageManager.PackageInfo;


namespace Chievfx.Mcp.Editor
{
    internal sealed partial class ProfilerBridgeService
    {
        private static Dictionary<string, object?> CreateFrameDebuggerState(EditorWindow window, List<string> diagnostics)
        {
            var state = new Dictionary<string, object?>();
            try
            {
                state["enabled"] = GetFrameDebuggerEnabled();
            }
            catch (Exception ex)
            {
                state["enabled"] = null;
                diagnostics.Add($"FrameDebugger.enabled unavailable: {ex.GetBaseException().Message}");
            }

            state["eventCount"] = GetFrameDebuggerEventCount(diagnostics);
            if (EditorApplication.isPlaying)
            {
                state["playPaused"] = EditorApplication.isPaused;
            }

            var currentEventLimit = GetFrameDebuggerEventLimit(window, diagnostics);
            state["currentEventLimit"] = currentEventLimit;
            state["selectedEventIndex"] = GetFrameDebuggerSelectedEventIndex(window, currentEventLimit);
            return state;
        }

        private EditorWindow PrepareFrameDebuggerForInspection(JToken args, List<string> diagnostics, int? selectEventIndex)
        {
            var window = ResolveKnownEditorWindow(
                new[]
                {
                    "UnityEditor.FrameDebuggerWindow",
                    "UnityEditorInternal.FrameDebuggerWindow"
                },
                "Window/Analysis/Frame Debugger",
                "Frame Debugger",
                ReadBool(args, "open", true),
                ReadBool(args, "focus", false),
                diagnostics);

            var autoEnabled = false;
            if (!TryGetFrameDebuggerEnabled(out var enabled, out _)
                || !enabled)
            {
                SetFrameDebuggerEnabled(window, true, diagnostics);
                diagnostics.Add("Frame Debugger enabled automatically for event inspection.");
                autoEnabled = true;
                isPlayModeCostNoteDue = EditorApplication.isPlaying;
            }

            var eventCount = GetFrameDebuggerEventCount(diagnostics);
            if (eventCount.HasValue && eventCount.Value <= 0)
            {
                window.Repaint();
                EditorApplication.QueuePlayerLoopUpdate();
                UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
            }

            eventCount = GetFrameDebuggerEventCount(diagnostics);
            if (eventCount.HasValue && eventCount.Value <= 0)
            {
                // Just turned on, it captures only on the next repaint: the old text sent agents to check the Game view.
                throw new InvalidOperationException(autoEnabled
                    ? "Frame Debugger was off: it is on now and captures a frame on the next repaint of the Game view - call again."
                      + (EditorApplication.isPlaying ? " " + FrameDebuggerPlayModeCost : string.Empty)
                    : "Frame Debugger has no captured events. Make sure a rendered Game/Scene view is available, then retry.");
            }

            // The enable that paused Play came out in an exception ("call again") or in a call the server repeated: the next
            // reply that gets through carries the warning instead.
            if (isPlayModeCostNoteDue && EditorApplication.isPlaying && !diagnostics.Contains(FrameDebuggerPlayModeCost))
            {
                diagnostics.Add(FrameDebuggerPlayModeCost);
            }

            isPlayModeCostNoteDue = false;

            if (selectEventIndex.HasValue)
            {
                if (eventCount.HasValue && selectEventIndex.Value >= eventCount.Value)
                {
                    throw new ArgumentOutOfRangeException(nameof(args), $"eventIndex {selectEventIndex.Value} is outside available Frame Debugger events 0..{eventCount.Value - 1}.");
                }

                if (!IsFrameDebuggerWindowReadyForEventSelection(window))
                {
                    window.Repaint();
                    EditorApplication.QueuePlayerLoopUpdate();
                    throw new InvalidOperationException("Frame Debugger window is enabled but its event tree is still initializing. Retry after the Frame Debugger window repaints.");
                }

                if (!TrySelectFrameDebuggerEventIndex(window, selectEventIndex.Value + 1, out var error))
                {
                    throw new NotSupportedException($"Frame Debugger event selection is unsupported in this Unity version. {error}");
                }
            }

            return window;
        }

        private static Dictionary<string, object?> CreateFrameDebuggerEventSummary(int eventIndex, bool includeDetails, List<string> diagnostics)
        {
            var summary = new Dictionary<string, object?>
            {
                ["index"] = eventIndex,
                ["eventLimit"] = eventIndex + 1
            };

            var utilityType = GetFrameDebuggerUtilityType();
            if (utilityType == null)
            {
                diagnostics.Add("FrameDebuggerUtility type unavailable; frame event data cannot be reported.");
                return summary;
            }

            var frameEvent = GetFrameDebuggerEvent(utilityType, eventIndex, diagnostics);
            if (frameEvent != null)
            {
                var eventType = GetReflectedInstanceValue(frameEvent, "m_Type");
                if (eventType != null)
                {
                    summary["type"] = Convert.ToString(eventType, CultureInfo.InvariantCulture);
                }

                var obj = GetReflectedInstanceValue(frameEvent, "m_Obj") as Object
                    ?? InvokeFrameDebuggerObjectGetter(utilityType, eventIndex) as Object;
                AddObjectSummary(summary, "object", obj);
            }

            var infoName = InvokeFrameDebuggerStringMethod(utilityType, "GetFrameEventInfoName", eventIndex);
            if (!string.IsNullOrWhiteSpace(infoName))
            {
                summary["name"] = infoName;
            }

            var eventData = includeDetails ? CreateFrameDebuggerEventData(utilityType, eventIndex, diagnostics) : null;
            if (eventData != null)
            {
                AddFrameDebuggerEventData(summary, eventData, includeDetails, diagnostics);
            }

            return summary;
        }

        private static object? GetFrameDebuggerEvent(Type utilityType, int eventIndex, List<string> diagnostics)
        {
            if (!TryInvokeReflectedMethod(utilityType, "GetFrameEvents", Array.Empty<object?>(), out var result, out var error)
                || !(result is Array events))
            {
                diagnostics.Add($"FrameDebuggerUtility.GetFrameEvents unavailable: {error}");
                return null;
            }

            return eventIndex >= 0 && eventIndex < events.Length ? events.GetValue(eventIndex) : null;
        }

        private static object? CreateFrameDebuggerEventData(Type utilityType, int eventIndex, List<string> diagnostics)
        {
            var dataType = TryResolveTypeByNames(new[]
            {
                "UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEventData",
                "UnityEditorInternal.FrameDebuggerEventData",
                "UnityEditor.FrameDebuggerEventData"
            });
            if (dataType == null)
            {
                diagnostics.Add("FrameDebuggerEventData type unavailable.");
                return null;
            }

            var data = Activator.CreateInstance(dataType);
            if (data == null)
            {
                diagnostics.Add("FrameDebuggerEventData could not be constructed.");
                return null;
            }

            if (!TryInvokeReflectedMethod(utilityType, "GetFrameEventData", new object?[] { eventIndex, data }, out var ok, out var error)
                || (ok is bool boolOk && !boolOk))
            {
                diagnostics.Add($"FrameDebuggerUtility.GetFrameEventData unavailable for event {eventIndex}: {error}");
                return null;
            }

            // Unity keeps the data of the selected event only: until the editor re-renders with the new selection, the data
            // of the previous one came back under the new number (a batch of event-get gave #5 the shader and draw count of #1).
            if (ReadFirstInt(data, "m_FrameEventIndex") is int dataIndex && dataIndex != eventIndex)
            {
                diagnostics.Add($"FrameDebuggerUtility.GetFrameEventData unavailable for event {eventIndex}: Unity still holds the data of event {dataIndex}");
                return null;
            }

            return data;
        }

        private static void AddFrameDebuggerEventData(Dictionary<string, object?> summary, object eventData, bool includeDetails, List<string> diagnostics)
        {
            AddString(summary, "shader", ReadFirstString(eventData, "m_RealShaderName", "m_OriginalShaderName", "m_ComputeShaderName", "m_RayTracingShaderName"));
            AddString(summary, "pass", ReadFirstString(eventData, "m_PassName", "m_RayTracingShaderPassName"));
            AddString(summary, "passLightMode", ReadFirstString(eventData, "m_PassLightMode"));
            AddInt(summary, "drawCalls", ReadFirstInt(eventData, "m_DrawCallCount"));
            AddInt(summary, "vertices", ReadFirstInt(eventData, "m_VertexCount"));
            AddInt(summary, "indices", ReadFirstInt(eventData, "m_IndexCount"));
            AddInt(summary, "instances", ReadFirstInt(eventData, "m_InstanceCount"));
            AddObjectSummary(summary, "mesh", GetReflectedInstanceValue(eventData, "m_Mesh") as Object);
            AddInt(summary, "meshSubset", ReadFirstInt(eventData, "m_MeshSubset"));
            AddString(summary, "renderTarget", ReadFirstString(eventData, "m_RenderTargetName"));

            var batchBreakCause = ReadFirstInt(eventData, "m_BatchBreakCause");
            if (batchBreakCause.HasValue)
            {
                summary["batchBreakCause"] = batchBreakCause.Value;
                var batchBreakText = GetBatchBreakCauseText(batchBreakCause.Value, diagnostics);
                if (!string.IsNullOrWhiteSpace(batchBreakText))
                {
                    summary["batchBreakReason"] = batchBreakText;
                }
            }

            if (!includeDetails)
            {
                return;
            }

            AddInt(summary, "shaderInstanceId", ReadFirstInt(eventData, "m_ShaderInstanceID", "m_ComputeShaderInstanceID", "m_RayTracingShaderInstanceID"));
            AddInt(summary, "shaderPassIndex", ReadFirstInt(eventData, "m_ShaderPassIndex"));
            AddInt(summary, "subShaderIndex", ReadFirstInt(eventData, "m_SubShaderIndex"));
            AddInt(summary, "componentInstanceId", ReadFirstInt(eventData, "m_ComponentInstanceID"));
            AddString(summary, "computeKernel", ReadFirstString(eventData, "m_ComputeShaderKernelName"));
            AddInt(summary, "computeThreadGroupsX", ReadFirstInt(eventData, "m_ComputeShaderThreadGroupsX"));
            AddInt(summary, "computeThreadGroupsY", ReadFirstInt(eventData, "m_ComputeShaderThreadGroupsY"));
            AddInt(summary, "computeThreadGroupsZ", ReadFirstInt(eventData, "m_ComputeShaderThreadGroupsZ"));
            AddInt(summary, "renderTargetWidth", ReadFirstInt(eventData, "m_RenderTargetWidth"));
            AddInt(summary, "renderTargetHeight", ReadFirstInt(eventData, "m_RenderTargetHeight"));
            AddInt(summary, "renderTargetFormat", ReadFirstInt(eventData, "m_RenderTargetFormat"));
            AddInt(summary, "renderTargetCount", ReadFirstInt(eventData, "m_RenderTargetCount"));
            AddBool(summary, "renderTargetBackBuffer", ReadFirstBool(eventData, "m_RenderTargetIsBackBuffer"));
            AddString(summary, "textures", DescribeBoundTextures(eventData));
            AddString(summary, "copiesScreenTexture", DescribeScreenCopySource(eventData, ReadFirstInt(eventData, "m_RenderTargetWidth"),
                ReadFirstInt(eventData, "m_RenderTargetHeight"), ReadFirstInt(eventData, "m_VertexCount")));
        }

        // A quad (6 vertices at most) that samples a render texture about the size of its target copies a whole picture: a
        // game's post-processing drawn as a UI quad over the camera's texture is not a blit by name or type, but works like one.
        private static string? DescribeScreenCopySource(object eventData, int? targetWidth, int? targetHeight, int? vertices)
        {
            if (vertices is not > 0 || vertices.Value > 6 || targetWidth is not > 0 || targetHeight is not > 0
                || GetReflectedInstanceValue(eventData, "m_ShaderInfo") is not { } shaderInfo
                || GetReflectedInstanceValue(shaderInfo, "m_Textures") is not Array textures)
            {
                return null;
            }

            foreach (var textureInfo in textures)
            {
                if (textureInfo != null && GetReflectedInstanceValue(textureInfo, "m_Value") is RenderTexture texture
                    && texture.width * 2 >= targetWidth.Value && texture.height * 2 >= targetHeight.Value)
                {
                    return texture.name;
                }
            }

            return null;
        }

        private const int MaxReportedTextures = 8;

        // The event data names no material, so the bound textures are what tells two UI quads of one shader apart
        // ("_MainTex=gui_minimapshadow 128x128").
        private static string? DescribeBoundTextures(object eventData)
        {
            if (GetReflectedInstanceValue(eventData, "m_ShaderInfo") is not { } shaderInfo
                || GetReflectedInstanceValue(shaderInfo, "m_Textures") is not Array textures)
            {
                return null;
            }

            var described = new List<string>();
            foreach (var textureInfo in textures)
            {
                if (textureInfo == null || described.Count >= MaxReportedTextures)
                {
                    continue;
                }

                var property = ReadStringMember(textureInfo, "m_Name");
                var texture = GetReflectedInstanceValue(textureInfo, "m_Value") as Texture;
                var textureName = texture != null ? texture.name : ReadStringMember(textureInfo, "m_TextureName");
                if (string.IsNullOrEmpty(textureName))
                {
                    continue;
                }

                described.Add(texture != null ? $"{property}={textureName} {texture.width}x{texture.height}" : $"{property}={textureName}");
            }

            return described.Count > 0 ? string.Join(", ", described) : null;
        }

        private static string? GetBatchBreakCauseText(int cause, List<string> diagnostics)
        {
            var utilityType = GetFrameDebuggerUtilityType();
            if (utilityType == null)
            {
                diagnostics.Add("FrameDebuggerUtility.GetBatchBreakCauseStrings unavailable: utility type missing.");
                return null;
            }

            if (!TryInvokeReflectedMethod(utilityType, "GetBatchBreakCauseStrings", Array.Empty<object?>(), out var result, out var error)
                || !(result is string[] reasons))
            {
                diagnostics.Add($"FrameDebuggerUtility.GetBatchBreakCauseStrings unavailable: {error}");
                return null;
            }

            return cause >= 0 && cause < reasons.Length ? reasons[cause] : null;
        }

        private static Type GetFrameDebuggerType()
        {
            return TryResolveTypeByNames(new[] { "UnityEngine.FrameDebugger", "UnityEditorInternal.FrameDebugger", "UnityEditor.FrameDebugger" })
                ?? throw new NotSupportedException("FrameDebugger API is unavailable in this Unity version.");
        }

        private static Type? GetFrameDebuggerUtilityType()
        {
            return TryResolveTypeByNames(new[]
            {
                "UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility",
                "UnityEditorInternal.FrameDebuggerUtility",
                "UnityEditor.FrameDebuggerUtility"
            });
        }

        private static bool GetFrameDebuggerEnabled()
        {
            var type = GetFrameDebuggerType();
            var value = GetReflectedStaticValue(type, "enabled") ?? GetReflectedStaticValue(type, "Enabled");
            if (value == null)
            {
                if (TryInvokeReflectedMethod(type, "IsLocalEnabled", Array.Empty<object?>(), out var localEnabled, out _))
                {
                    return Convert.ToBoolean(localEnabled, CultureInfo.InvariantCulture);
                }

                throw new NotSupportedException("FrameDebugger.enabled/IsLocalEnabled is unavailable in this Unity version.");
            }

            return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        }

        // Turning the Frame Debugger on in Play Mode pauses the game, and turning it off leaves it paused: the game sat frozen
        // with nothing saying why. The pause state from before enabling comes back on disable.
        private static bool? playPausedBeforeFrameDebugger;

        // An inspection tool turned the Frame Debugger on in Play Mode, and its reply with the warning has not reached the agent yet.
        private static bool isPlayModeCostNoteDue;

        // Unity's Frame Debugger in Play Mode keeps memory for every frame it re-renders for a selected event (an enable,
        // each selection or draw-call screenshot) until the editor restarts: a long walk over events grew a big project's
        // editor by 24 GB, one enable with a draw-call screenshot cost 2-4 GB in the MadOut2 world, and three minutes of
        // pick-pixel and draw-call screenshots there 12 GB. A paused online game also drops its session after half a minute or so.
        private const string FrameDebuggerPlayModeCost =
            "Play Mode is paused while the Frame Debugger is on: a networked game may time out and drop its session. "
            + "In Play Mode every enable, event selection and draw-call screenshot keeps editor memory until a restart "
            + "(tens of MB in a small scene, 2-4 GB per enable in a game world): read only the events you need, turn it off when done, "
            + "and watch bridge-get-status editorHealth.";

        private static void SetFrameDebuggerEnabled(EditorWindow window, bool enabled, List<string> diagnostics)
        {
            if (enabled && EditorApplication.isPlaying && playPausedBeforeFrameDebugger == null)
            {
                playPausedBeforeFrameDebugger = EditorApplication.isPaused;
                diagnostics.Add(FrameDebuggerPlayModeCost);
            }

            SetFrameDebuggerEnabledCore(window, enabled, diagnostics);
            if (!enabled)
            {
                isPlayModeCostNoteDue = false;
                RestorePlayPause(diagnostics);
            }
        }

        private static void RestorePlayPause(List<string> diagnostics)
        {
            var pausedBefore = playPausedBeforeFrameDebugger;
            playPausedBeforeFrameDebugger = null;
            if (pausedBefore == false && EditorApplication.isPlaying && EditorApplication.isPaused)
            {
                EditorApplication.isPaused = false;
                diagnostics.Add("Play Mode unpaused: the Frame Debugger paused it when it was turned on.");
            }
        }

        private static void SetFrameDebuggerEnabledCore(EditorWindow window, bool enabled, List<string> diagnostics)
        {
            var stateKnown = TryGetFrameDebuggerEnabled(out var currentEnabled, out var stateError);
            if (!stateKnown)
            {
                diagnostics.Add($"FrameDebugger.enabled state unavailable before set: {stateError}");
            }

            if (stateKnown && currentEnabled == enabled)
            {
                if (!enabled || !IsFrameDebuggerWindowMissingEnabledState(window))
                {
                    return;
                }

                diagnostics.Add("Frame Debugger was enabled without initialized window state; restarting through FrameDebuggerWindow.");
                if (TryInvokeReflectedMethod(window, "DisableFrameDebugger", Array.Empty<object?>(), out _, out _))
                {
                    currentEnabled = false;
                }
                else
                {
                    SetFrameDebuggerEnabledViaApi(false);
                    currentEnabled = false;
                }
            }

            var methodName = enabled ? "EnableFrameDebugger" : "DisableFrameDebugger";
            if (TryInvokeReflectedMethod(window, methodName, Array.Empty<object?>(), out _, out var windowError))
            {
                if (TryGetFrameDebuggerEnabled(out var afterEnabled, out _)
                    && afterEnabled != enabled)
                {
                    throw new InvalidOperationException(enabled
                        ? "Frame Debugger window did not enable. Make sure a Game view can be shown and is not docked in the same tab group as the Frame Debugger."
                        : "Frame Debugger window did not disable.");
                }

                return;
            }

            if (enabled)
            {
                throw new NotSupportedException($"Safe FrameDebuggerWindow.EnableFrameDebugger reflection is unavailable in this Unity version. {windowError}");
            }

            diagnostics.Add($"FrameDebuggerWindow.DisableFrameDebugger unavailable; falling back to FrameDebugger API. {windowError}");
            SetFrameDebuggerEnabledViaApi(false);
        }

        private static bool TryGetFrameDebuggerEnabled(out bool enabled, out string? error)
        {
            enabled = false;
            error = null;
            try
            {
                enabled = GetFrameDebuggerEnabled();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetBaseException().Message;
                return false;
            }
        }

        private static void SetFrameDebuggerEnabledViaApi(bool enabled)
        {
            var type = GetFrameDebuggerType();
            if (!TrySetReflectedMember(type, "enabled", enabled, out var error))
            {
                var utilityType = GetFrameDebuggerUtilityType();
                if (utilityType != null)
                {
                    var remotePlayerGuid = 0;
                    if (TryInvokeReflectedMethod(utilityType, "GetRemotePlayerGUID", Array.Empty<object?>(), out var remoteResult, out _)
                        && TryConvertToInt(remoteResult, out var reflectedRemotePlayerGuid))
                    {
                        remotePlayerGuid = reflectedRemotePlayerGuid;
                    }

                    if (TryInvokeReflectedMethod(utilityType, "SetEnabled", new object?[] { enabled, remotePlayerGuid }, out _, out error))
                    {
                        return;
                    }
                }

                throw new NotSupportedException($"FrameDebugger enabled assignment is unsupported in this Unity version. {error}");
            }
        }

        private static bool IsFrameDebuggerWindowMissingEnabledState(EditorWindow window)
        {
            return EditorWindowBridgeService.GetReflectedValue(window, "m_EventDetailsView") == null;
        }

        private static bool IsFrameDebuggerWindowReadyForEventSelection(EditorWindow window)
        {
            // Unity renamed the tree field: 6.x has m_Tree, older versions m_TreeView. Requiring only the
            // old name made this permanently false on Unity 6, so every eventIndex/eventLimit call failed
            // with "event tree is still initializing".
            return EditorWindowBridgeService.GetReflectedValue(window, "m_EventDetailsView") != null
                && (EditorWindowBridgeService.GetReflectedValue(window, "m_Tree") != null
                    || EditorWindowBridgeService.GetReflectedValue(window, "m_TreeView") != null);
        }

        private static int? GetFrameDebuggerEventCount(List<string> diagnostics)
        {
            var utilityType = GetFrameDebuggerUtilityType();
            if (utilityType == null)
            {
                diagnostics.Add("FrameDebuggerUtility type unavailable; event count cannot be reported.");
                return null;
            }

            foreach (var memberName in new[] { "count", "eventCount", "frameEventCount" })
            {
                if (TryReadStaticIntMember(utilityType, memberName, out var value))
                {
                    return value;
                }
            }

            foreach (var methodName in new[] { "GetFrameEventCount", "GetEventCount" })
            {
                if (TryInvokeReflectedMethod(utilityType, methodName, Array.Empty<object?>(), out var result, out _)
                    && TryConvertToInt(result, out var value))
                {
                    return value;
                }
            }

            if (TryInvokeReflectedMethod(utilityType, "GetFrameEvents", Array.Empty<object?>(), out var frameEvents, out _)
                && frameEvents is Array frameEventArray)
            {
                return frameEventArray.Length;
            }

            diagnostics.Add("FrameDebuggerUtility event count members unavailable.");
            return null;
        }

        private static int? GetFrameDebuggerEventLimit(EditorWindow window, List<string> diagnostics)
        {
            var utilityType = GetFrameDebuggerUtilityType();
            if (utilityType != null)
            {
                foreach (var memberName in new[] { "limit", "eventLimit", "currentEventLimit" })
                {
                    if (TryReadStaticIntMember(utilityType, memberName, out var value))
                    {
                        return value;
                    }
                }
            }

            foreach (var memberName in new[] { "m_FrameEventLimit", "m_CurrentFrameEventLimit", "m_FrameDebuggerEventLimit" })
            {
                var value = ReadNullableIntMember(window, memberName);
                if (value.HasValue)
                {
                    return value;
                }
            }

            diagnostics.Add("Frame Debugger current event limit members unavailable.");
            return null;
        }

        private static int? GetFrameDebuggerSelectedEventIndex(EditorWindow window, int? currentEventLimit)
        {
            foreach (var target in EnumerateFrameDebuggerTreeViewTargets(window))
            {
                foreach (var memberName in new[] { "selectedFrameEventIndex", "m_SelectedFrameEventIndex", "selectedEventIndex", "m_SelectedEventIndex" })
                {
                    var value = ReadNullableIntMember(target, memberName);
                    if (value.HasValue)
                    {
                        return value;
                    }
                }

                var treeController = EditorWindowBridgeService.GetReflectedValue(target, "m_TreeView") ?? target;
                if (TryInvokeReflectedMethod(treeController, "GetSelection", Array.Empty<object?>(), out var selection, out _)
                    && selection is IEnumerable selectedIds)
                {
                    foreach (var selectedId in selectedIds)
                    {
                        if (TryConvertToInt(selectedId, out var reflectedSelectedId) && reflectedSelectedId > 0)
                        {
                            return reflectedSelectedId - 1;
                        }
                    }
                }
            }

            return currentEventLimit.HasValue && currentEventLimit.Value > 0
                ? currentEventLimit.Value - 1
                : null;
        }

        private static bool TryChangeFrameDebuggerEventLimit(EditorWindow window, int eventLimit, out string? error)
        {
            if (TryInvokeReflectedMethod(window, "ChangeFrameEventLimit", new object?[] { eventLimit }, out _, out error))
            {
                TryInvokeReflectedMethod(window, "RepaintOnLimitChange", Array.Empty<object?>(), out _, out _);
                return true;
            }

            return false;
        }

        private static bool TrySelectFrameDebuggerEventIndex(EditorWindow window, int frameEventLimit, out string? error)
        {
            if (TryChangeFrameDebuggerEventLimit(window, frameEventLimit, out error))
            {
                return true;
            }

            foreach (var target in EnumerateFrameDebuggerTreeViewTargets(window))
            {
                if (TryInvokeReflectedMethod(target, "SelectFrameEventIndex", new object?[] { frameEventLimit }, out _, out error))
                {
                    TryInvokeReflectedMethod(window, "RepaintOnLimitChange", Array.Empty<object?>(), out _, out _);
                    return true;
                }
            }

            return false;
        }
    }
}
