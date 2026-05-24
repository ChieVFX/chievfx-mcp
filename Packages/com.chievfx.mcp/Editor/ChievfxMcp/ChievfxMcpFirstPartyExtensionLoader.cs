#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Chievfx.Mcp.Editor
{
    internal static class ChievfxMcpFirstPartyExtensionLoader
    {
        private static readonly ExtensionRegistrationType[] KnownRegistrationTypes =
        {
            new(
                "Chievfx.Mcp.Extensions.SampleReadOnly",
                "Chievfx.Mcp.Extensions.SampleReadOnly.ChievfxMcpSampleReadOnlyExtension"),
            new(
                "Chievfx.Mcp.Extensions.Ecs",
                "Chievfx.Mcp.Extensions.Ecs.ChievfxMcpEcsExtension"),
            new(
                "Chievfx.Mcp.Extensions.Ugui",
                "Chievfx.Mcp.Extensions.Ugui.ChievfxMcpUguiExtension"),
            new(
                "Chievfx.Mcp.Extensions.UiToolkit",
                "Chievfx.Mcp.Extensions.UiToolkit.ChievfxMcpUiToolkitExtension"),
            new(
                "Chievfx.Mcp.Extensions.Particles",
                "Chievfx.Mcp.Extensions.Particles.ChievfxMcpParticlesExtension"),
            new(
                "Chievfx.Mcp.Extensions.Cameras",
                "Chievfx.Mcp.Extensions.Cameras.ChievfxMcpCamerasExtension"),
            new(
                "Chievfx.Mcp.Editor",
                "Chievfx.Mcp.Extensions.Control.ChievfxMcpControlExtension"),
        };

        public static void EnsureLoaded()
        {
            foreach (var registrationType in KnownRegistrationTypes)
            {
                EnsureRegistrationTypeLoaded(registrationType);
            }
        }

        private static void EnsureRegistrationTypeLoaded(ExtensionRegistrationType registrationType)
        {
            try
            {
                var assembly = FindLoadedAssembly(registrationType.AssemblyName)
                    ?? Assembly.Load(registrationType.AssemblyName);
                var type = assembly.GetType(registrationType.TypeName, throwOnError: false);
                if (type == null)
                {
                    Debug.LogWarning(
                        $"ChievFX MCP could not find extension registration type '{registrationType.TypeName}' in '{registrationType.AssemblyName}'.");
                    return;
                }

                RuntimeHelpers.RunClassConstructor(type.TypeHandle);
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"ChievFX MCP could not load first-party extension assembly '{registrationType.AssemblyName}'. {ex.Message}");
            }
        }

        private static Assembly? FindLoadedAssembly(string assemblyName)
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, assemblyName, StringComparison.Ordinal));
        }

        private readonly struct ExtensionRegistrationType
        {
            public ExtensionRegistrationType(string assemblyName, string typeName)
            {
                AssemblyName = assemblyName;
                TypeName = typeName;
            }

            public string AssemblyName { get; }

            public string TypeName { get; }
        }
    }
}

namespace Chievfx.Mcp.Extensions.Control
{
    [InitializeOnLoad]
    internal static class ChievfxMcpControlExtension
    {
        private const string ExtensionId = "chievfx.control";
        private const string Category = "Control";
        private const string EssentialsCategory = "Essentials";
        private const string StatusUri = "chievfx://extensions/chievfx.control/status";
        private static bool? playModeOverrideForTests;

        static ChievfxMcpControlExtension()
        {
            Chievfx.Mcp.Editor.ChievfxMcpExtensionRegistry.RegisterExtension(CreateDescriptor());
        }

        public static object? RunToolForTests(string toolName, string argsJson)
        {
            return RunTool(toolName, string.IsNullOrWhiteSpace(argsJson) ? new JObject() : JObject.Parse(argsJson));
        }

        public static object? ReadResourceForTests(string uri)
        {
            return ReadResource(uri);
        }

        public static void SetPlayModeOverrideForTests(bool? isPlaying)
        {
            playModeOverrideForTests = isPlaying;
        }

        private static Chievfx.Mcp.Editor.ChievfxMcpExtensionDescriptor CreateDescriptor()
        {
            var api = InputApi.TryCreate(out _, out var reason);
            var descriptor = new Chievfx.Mcp.Editor.ChievfxMcpExtensionDescriptor
            {
                Id = ExtensionId,
                DisplayName = "ChievFX MCP Control",
                Version = "0.1.0",
                Description = api
                    ? "New Input System-first keyboard, mouse, and touch input event helpers for Play Mode control."
                    : "Control helpers unavailable until com.unity.inputsystem is installed and loaded.",
                ToolRunner = RunTool,
                ResourceReader = ReadResource,
            };
            descriptor.Resources.Add(new Chievfx.Mcp.Editor.ChievfxMcpResourceDescriptor
            {
                Id = "control-status",
                Uri = StatusUri,
                Name = "Control extension status",
                Description = "Reports Input System availability, Play Mode state, current devices, and mutation gate requirements.",
                MimeType = "application/json",
                Category = Category,
            });
            descriptor.Tools.Add(Tool("editor-playmode-set", "Enter or exit Unity Play Mode.", PlayModeSetSchema(), EssentialsCategory));
            if (api)
            {
                descriptor.Tools.Add(Tool("input-control-keyboard-event", "Queue a New Input System keyboard down, up, or tap event.", KeyboardSchema()));
                descriptor.Tools.Add(Tool("input-control-mouse-event", "Queue a New Input System mouse button or move event.", MouseSchema()));
                descriptor.Tools.Add(Tool("input-control-mouse-gesture", "Queue a timed mouse down/move/up gesture by interpolating delta over duration. Defaults dryRun=true.", GestureSchema()));
                descriptor.Tools.Add(Tool("input-control-touch-event", "Queue a New Input System touchscreen down, move, up, or tap event.", TouchSchema()));
            }

            _ = reason;
            return descriptor;
        }

        private static Chievfx.Mcp.Editor.ChievfxMcpToolDescriptor Tool(string name, string description, JObject schema, string? category = null)
        {
            return new Chievfx.Mcp.Editor.ChievfxMcpToolDescriptor
            {
                Name = name,
                Description = description,
                Category = category ?? Category,
                InputSchema = schema,
            };
        }

        private static object? RunTool(string toolName, JToken args)
        {
            if (string.Equals(toolName, "editor-playmode-set", StringComparison.Ordinal))
            {
                return PlayModeSet(args);
            }

            if (!InputApi.TryCreate(out var api, out var reason))
            {
                return Result(toolName, null, null, true, false, Array.Empty<object>(), new[] { reason }, new[] { $"Tool '{toolName}' requires com.unity.inputsystem and loaded Input System types." });
            }

            return toolName switch
            {
                "input-control-keyboard-event" => Keyboard(args, api!),
                "input-control-mouse-event" => Mouse(args, api!),
                "input-control-mouse-gesture" => Gesture(args, api!),
                "input-control-touch-event" => Touch(args, api!),
                _ => throw new InvalidOperationException($"Unknown Control extension tool '{toolName}'."),
            };
        }

        private static object? ReadResource(string uri)
        {
            if (!string.Equals(uri, StatusUri, StringComparison.Ordinal))
            {
                return null;
            }

            var available = InputApi.TryCreate(out var api, out var reason);
            return new Dictionary<string, object?>
            {
                ["uri"] = uri,
                ["extensionId"] = ExtensionId,
                ["available"] = available,
                ["dependencyReason"] = reason,
                ["package"] = "com.unity.inputsystem",
                ["playMode"] = IsPlaying,
                ["requiresPlayModeForMutation"] = true,
                ["requiresAllowStateMutation"] = true,
                ["keyboard"] = DeviceRow("Keyboard", api?.Keyboard),
                ["mouse"] = DeviceRow("Mouse", api?.Mouse),
                ["touchscreen"] = DeviceRow("Touchscreen", api?.Touchscreen),
                ["tools"] = available
                    ? new[] { "editor-playmode-set", "input-control-keyboard-event", "input-control-mouse-event", "input-control-mouse-gesture", "input-control-touch-event" }
                    : new[] { "editor-playmode-set" },
                ["warnings"] = available ? Array.Empty<string>() : new[] { reason },
            };
        }

        private static Dictionary<string, object?> PlayModeSet(JToken args)
        {
            var errors = new List<string>();
            var hasRequestedState = args["isPlaying"]?.Type == JTokenType.Boolean;
            if (!hasRequestedState)
            {
                errors.Add("editor-playmode-set requires isPlaying boolean.");
            }

            var requested = hasRequestedState && args["isPlaying"]!.Value<bool>();
            var before = IsPlaying;
            if (errors.Count == 0)
            {
                EditorApplication.isPlaying = requested;
            }

            var ok = errors.Count == 0;
            var result = new Dictionary<string, object?>
            {
                ["ok"] = ok,
                ["status"] = ok ? (before == requested ? "unchanged" : "requested") : "failed",
                ["requestedIsPlaying"] = hasRequestedState ? requested : null,
                ["isPlaying"] = IsPlaying,
                ["isPlayingOrWillChangePlaymode"] = EditorApplication.isPlayingOrWillChangePlaymode,
            };

            if (!ok)
            {
                result["validationErrors"] = errors.ToArray();
            }

            return result;
        }

        private static Dictionary<string, object?> Keyboard(JToken args, InputApi api)
        {
            var action = Norm(ReadString(args, "action"));
            var keyName = ReadString(args, "key") ?? ReadString(args, "targetKey") ?? string.Empty;
            var dryRun = ReadBool(args, "dryRun", false);
            var errors = new List<string>();
            var warnings = new List<string>();
            var queued = new List<object>();
            if (!OneOf(action, "down", "up", "tap")) errors.Add("action must be one of: down, up, tap.");
            if (api.Keyboard == null) errors.Add("Keyboard.current is null; no keyboard device is available.");
            if (!api.TryKey(keyName, out var key, out var keyError)) errors.Add(keyError);
            object? control = null;
            if (errors.Count == 0 && !api.TryKeyboardControl(key!, out control, out var controlError)) errors.Add(controlError);
            Gate(dryRun, ReadBool(args, "allowStateMutation", false), errors);
            if (errors.Count == 0)
            {
                foreach (var item in action == "tap" ? new[] { ("down", 1f), ("up", 0f) } : new[] { (action, action == "down" ? 1f : 0f) })
                {
                    queued.Add(EventRow("Keyboard", item.Item1, keyName, null, null, -1d));
                    if (!dryRun) api.QueueKeyboardKey(key!, item.Item2 > 0f, -1d);
                }
            }

            var mutated = !dryRun && errors.Count == 0;
            if (mutated && !api.TryUpdate(out var updateWarning) && !string.IsNullOrEmpty(updateWarning))
            {
                warnings.Add(updateWarning);
            }

            return Result("input-control-keyboard-event", "Keyboard", action, dryRun, mutated, queued.ToArray(), warnings.ToArray(), errors.ToArray());
        }

        private static Dictionary<string, object?> Mouse(JToken args, InputApi api)
        {
            var action = Norm(ReadString(args, "action"));
            var dryRun = ReadBool(args, "dryRun", false);
            var errors = new List<string>();
            var warnings = new List<string>();
            var queued = new List<object>();
            Vector2? uiToolkitClickPosition = null;
            if (!OneOf(action, "down", "up", "tap", "move")) errors.Add("action must be one of: down, up, tap, move.");
            if (api.Mouse == null) errors.Add("Mouse.current is null; no mouse device is available.");

            if (action == "move")
            {
                var hasPosition = TryVector(args, "position", out var position) || TryVector(args, "screenPosition", out position);
                var hasDelta = TryVector(args, "delta", out var delta);
                if (!hasPosition && !hasDelta) errors.Add("move action requires position/screenPosition or delta.");
                if (hasPosition && hasDelta) warnings.Add("Both position and delta were provided; queued absolute position and reported delta for caller context.");
                object? control = null;
                if (errors.Count == 0 && !api.TryMouseControl(hasPosition ? "position" : "delta", out control, out var controlError)) errors.Add(controlError);
                Gate(dryRun, ReadBool(args, "allowStateMutation", false), errors);
                if (errors.Count == 0)
                {
                    var value = hasPosition ? position : delta;
                    queued.Add(EventRow("Mouse", "move", null, hasPosition ? position : null, hasDelta ? delta : null, -1d));
                    if (!dryRun) api.QueueMouseMove(hasPosition ? position : null, hasDelta ? delta : null, -1d);
                }
            }
            else
            {
                var button = ReadString(args, "button") ?? "left";
                var hasPosition = TryVector(args, "position", out var position) || TryVector(args, "screenPosition", out position);
                object? control = null;
                if (errors.Count == 0 && !api.TryMouseButton(button, out control, out var buttonError)) errors.Add(buttonError);
                if (errors.Count == 0 && hasPosition && !api.TryMouseControl("position", out _, out var positionError)) errors.Add(positionError);
                Gate(dryRun, ReadBool(args, "allowStateMutation", false), errors);
                if (errors.Count == 0)
                {
                    if (hasPosition)
                    {
                        queued.Add(EventRow("Mouse", "move", null, position, null, -1d));
                        if (!dryRun) api.QueueMouseMove(position, null, -1d);
                    }

                    foreach (var item in action == "tap" ? new[] { ("down", 1f), ("up", 0f) } : new[] { (action, action == "down" ? 1f : 0f) })
                    {
                        queued.Add(EventRow("Mouse", item.Item1, button, null, null, -1d));
                        if (!dryRun) api.QueueMouseButton(button, item.Item2 > 0f, -1d);
                    }

                    if (action is "up" or "tap")
                    {
                        uiToolkitClickPosition = hasPosition ? position : api.ReadMousePosition();
                    }
                }
            }

            var mutated = !dryRun && errors.Count == 0;
            if (mutated && !api.TryUpdate(out var updateWarning) && !string.IsNullOrEmpty(updateWarning))
            {
                warnings.Add(updateWarning);
            }

            if (mutated && uiToolkitClickPosition.HasValue)
            {
                TryDispatchUiToolkitPointerClick(uiToolkitClickPosition.Value, warnings);
                TryDispatchUguiPointerClick(uiToolkitClickPosition.Value, warnings);
            }

            return Result("input-control-mouse-event", "Mouse", action, dryRun, mutated, queued.ToArray(), warnings.ToArray(), errors.ToArray());
        }

        private static Dictionary<string, object?> Gesture(JToken args, InputApi api)
        {
            var dryRun = ReadBool(args, "dryRun", true);
            var errors = new List<string>();
            var queued = new List<object>();
            if (!TryVector(args, "delta", out var delta)) errors.Add("mouse gesture requires delta.");
            var hasStart = TryVector(args, "position", out var start) || TryVector(args, "startPosition", out start) || TryVector(args, "screenPosition", out start);
            var durationMs = ReadDouble(args, "durationMs", 250d);
            var ease = Norm(ReadString(args, "ease") ?? "inout");
            var steps = ReadInt(args, "steps", 0);
            steps = steps > 0 ? steps : Mathf.Clamp(Mathf.CeilToInt((float)Math.Max(durationMs, 16d) / 16f), 1, 120);
            if (!OneOf(ease, "inout", "in", "out")) errors.Add("ease must be one of: inout, in, out.");
            if (durationMs < 0d || durationMs > 60000d) errors.Add("durationMs must be between 0 and 60000.");
            if (steps < 1 || steps > 240) errors.Add("steps must be between 1 and 240.");
            if (api.Mouse == null) errors.Add("Mouse.current is null; no mouse device is available.");
            var button = ReadString(args, "button") ?? "left";
            var includeDown = ReadBool(args, "includeDown", true);
            var includeUp = ReadBool(args, "includeUp", true);
            object? buttonControl = null;
            object? positionControl = null;
            object? deltaControl = null;
            if (errors.Count == 0 && (includeDown || includeUp) && !api.TryMouseButton(button, out buttonControl, out var buttonError)) errors.Add(buttonError);
            if (errors.Count == 0 && hasStart && !api.TryMouseControl("position", out positionControl, out var positionError)) errors.Add(positionError);
            if (errors.Count == 0 && !api.TryMouseControl("delta", out deltaControl, out var deltaError)) errors.Add(deltaError);
            Gate(dryRun, ReadBool(args, "allowStateMutation", false), errors);
            if (errors.Count == 0)
            {
                var time = api.Time;
                if (includeDown)
                {
                    queued.Add(EventRow("Mouse", "down", button, null, null, time));
                    if (!dryRun) api.QueueMouseButton(button, true, time);
                }

                var previous = Vector2.zero;
                for (var i = 1; i <= steps; i++)
                {
                    var t = (float)i / steps;
                    var current = delta * Ease(t, ease);
                    var frameDelta = current - previous;
                    previous = current;
                    var eventTime = time + (durationMs / 1000d) * t;
                    queued.Add(EventRow("Mouse", "move", null, hasStart ? start + current : null, frameDelta, eventTime));
                    if (!dryRun)
                    {
                        api.QueueMouseMove(hasStart ? start + current : null, frameDelta, eventTime);
                    }
                }

                if (includeUp)
                {
                    var upTime = time + durationMs / 1000d;
                    queued.Add(EventRow("Mouse", "up", button, null, null, upTime));
                    if (!dryRun) api.QueueMouseButton(button, false, upTime);
                }
            }

            var warnings = new List<string>();
            var mutated = !dryRun && errors.Count == 0;
            if (mutated && !api.TryUpdate(out var updateWarning) && !string.IsNullOrEmpty(updateWarning))
            {
                warnings.Add(updateWarning);
            }

            if (mutated && hasStart && includeDown && includeUp)
            {
                TryDispatchUiToolkitPointerDrag(start, delta, steps, warnings);
                TryDispatchUguiPointerDrag(start, delta, warnings);
            }

            var result = Result("input-control-mouse-gesture", "Mouse", "gesture", dryRun, mutated, queued.ToArray(), warnings.ToArray(), errors.ToArray());
            if (dryRun || errors.Count > 0)
            {
                result["durationMs"] = durationMs;
                result["steps"] = steps;
                result["ease"] = ease;
            }

            return result;
        }

        private static Dictionary<string, object?> Touch(JToken args, InputApi api)
        {
            var action = Norm(ReadString(args, "action"));
            var dryRun = ReadBool(args, "dryRun", false);
            var errors = new List<string>();
            var warnings = new List<string>();
            var queued = new List<object>();
            Vector2? uiToolkitClickPosition = null;
            if (!OneOf(action, "down", "up", "tap", "move")) errors.Add("action must be one of: down, up, tap, move.");
            if (api.Touchscreen == null) errors.Add("Touchscreen.current is null; no touchscreen device is available.");
            var touchId = ReadInt(args, "touchId", 1);
            if (touchId < 1) errors.Add("touchId must be greater than or equal to 1.");
            var hasPosition = TryVector(args, "position", out var position) || TryVector(args, "screenPosition", out position);
            var hasDelta = TryVector(args, "delta", out var delta);
            if (!hasPosition && !string.Equals(action, "up", StringComparison.Ordinal))
            {
                errors.Add("touch down, tap, and move actions require position/screenPosition.");
            }

            if (hasDelta && (action == "down" || action == "tap"))
            {
                warnings.Add("delta is ignored for touch down/tap start events; position is the authoritative touchscreen coordinate.");
            }

            object? primaryTouch = null;
            if (errors.Count == 0 && !api.TryTouchControl("primaryTouch", out primaryTouch, out var controlError))
            {
                errors.Add(controlError);
            }

            Gate(dryRun, ReadBool(args, "allowStateMutation", false), errors);
            if (errors.Count == 0)
            {
                var resolvedPosition = hasPosition ? position : api.ReadPrimaryTouchPosition();
                foreach (var item in action == "tap"
                    ? new[] { ("down", "Began", Vector2.zero), ("up", "Ended", Vector2.zero) }
                    : new[] { (action, TouchPhaseForAction(action), hasDelta ? delta : Vector2.zero) })
                {
                    queued.Add(EventRow("Touchscreen", item.Item1, touchId.ToString(CultureInfo.InvariantCulture), resolvedPosition, item.Item3, -1d));
                    if (!dryRun)
                    {
                        api.QueueTouch(touchId, item.Item2, resolvedPosition, item.Item3, -1d);
                    }
                }

                if (action is "up" or "tap")
                {
                    uiToolkitClickPosition = resolvedPosition;
                }
            }

            var mutated = !dryRun && errors.Count == 0;
            if (mutated && !api.TryUpdate(out var updateWarning) && !string.IsNullOrEmpty(updateWarning))
            {
                warnings.Add(updateWarning);
            }

            if (mutated && uiToolkitClickPosition.HasValue)
            {
                TryDispatchUiToolkitPointerClick(uiToolkitClickPosition.Value, warnings);
                TryDispatchUguiPointerClick(uiToolkitClickPosition.Value, warnings);
            }
            else if (mutated && action == "move" && hasPosition && hasDelta)
            {
                TryDispatchUiToolkitPointerDrag(position - delta, delta, 12, warnings);
                TryDispatchUguiPointerDrag(position - delta, delta, warnings);
            }

            var result = Result("input-control-touch-event", "Touchscreen", action, dryRun, mutated, queued.ToArray(), warnings.ToArray(), errors.ToArray());
            result["touchId"] = touchId;
            return result;
        }

        private static Dictionary<string, object?> Result(string tool, string? device, string? action, bool dryRun, bool mutated, object[] queued, string[] warnings, string[] errors)
        {
            var ok = errors.Length == 0;
            var result = new Dictionary<string, object?>
            {
                ["ok"] = ok,
                ["status"] = ok ? (dryRun ? "dry-run" : "success") : "failed",
                ["device"] = device,
                ["action"] = action,
            };

            if (dryRun)
            {
                result["queuedEvents"] = queued;
                result["queuedEventCount"] = queued.Length;
                result["dryRun"] = true;
                result["mutated"] = mutated;
                result["playMode"] = IsPlaying;
                result["mutationGate"] = MutationGateRow(dryRun);
                result["coordinateConvention"] = CoordinateConventionRow();
            }

            if (warnings.Length > 0)
            {
                result["warnings"] = warnings;
            }

            if (!ok)
            {
                result["tool"] = tool;
                result["queuedEvents"] = queued;
                result["queuedEventCount"] = queued.Length;
                result["dryRun"] = dryRun;
                result["mutated"] = mutated;
                result["playMode"] = IsPlaying;
                result["validationErrors"] = errors;
                result["dependency"] = new Dictionary<string, object?> { ["package"] = "com.unity.inputsystem", ["available"] = InputApi.TryCreate(out _, out var reason), ["reason"] = reason };
                result["mutationGate"] = MutationGateRow(dryRun);
                result["coordinateConvention"] = CoordinateConventionRow();
            }

            return result;
        }

        private static Dictionary<string, object?> MutationGateRow(bool dryRun)
        {
            return new Dictionary<string, object?> { ["requiresPlayMode"] = true, ["requiresAllowStateMutation"] = true, ["playMode"] = IsPlaying, ["dryRun"] = dryRun };
        }

        private static Dictionary<string, object?> CoordinateConventionRow()
        {
            return new Dictionary<string, object?> { ["origin"] = "bottom-left", ["unit"] = "screen-pixels", ["xAxis"] = "right", ["yAxis"] = "up" };
        }

        private static void Gate(bool dryRun, bool allowStateMutation, List<string> errors)
        {
            if (dryRun) return;
            if (!IsPlaying) errors.Add("Real input injection requires Play Mode. Set dryRun=true outside Play Mode.");
            if (!allowStateMutation) errors.Add("Real input injection requires allowStateMutation=true.");
        }

        private static void TryDispatchUiToolkitPointerClick(Vector2 screenPosition, List<string> warnings)
        {
            var dryRunArgs = UiToolkitPointerClickArgs(screenPosition, dryRun: true);
            try
            {
                if (!Chievfx.Mcp.Editor.ChievfxMcpExtensionRegistry.TryRunTool("uitoolkit-runtime-interact", dryRunArgs, out var dryRunResult)
                    || dryRunResult is not Dictionary<string, object?> dryRun
                    || !dryRun.TryGetValue("target", out var target)
                    || target == null)
                {
                    return;
                }

                Chievfx.Mcp.Editor.ChievfxMcpExtensionRegistry.TryRunTool("uitoolkit-runtime-interact", UiToolkitPointerClickArgs(screenPosition, dryRun: false), out _);
            }
            catch (Exception ex)
            {
                warnings.Add("UI Toolkit runtime pointer dispatch skipped: " + RootMessage(ex));
            }
        }

        private static JObject UiToolkitPointerClickArgs(Vector2 screenPosition, bool dryRun)
        {
            return new JObject
            {
                ["action"] = "pointerClick",
                ["normalized"] = NormalizedScreenPosition(screenPosition),
                ["dryRun"] = dryRun,
                ["allowStateMutation"] = true,
            };
        }

        private static void TryDispatchUiToolkitPointerDrag(Vector2 screenStartPosition, Vector2 screenDelta, int steps, List<string> warnings)
        {
            var dryRunArgs = UiToolkitPointerDragArgs(screenStartPosition, screenDelta, steps, dryRun: true);
            try
            {
                if (!Chievfx.Mcp.Editor.ChievfxMcpExtensionRegistry.TryRunTool("uitoolkit-runtime-interact", dryRunArgs, out var dryRunResult)
                    || dryRunResult is not Dictionary<string, object?> dryRun
                    || !dryRun.TryGetValue("target", out var target)
                    || target == null)
                {
                    return;
                }

                Chievfx.Mcp.Editor.ChievfxMcpExtensionRegistry.TryRunTool("uitoolkit-runtime-interact", UiToolkitPointerDragArgs(screenStartPosition, screenDelta, steps, dryRun: false), out _);
            }
            catch (Exception ex)
            {
                warnings.Add("UI Toolkit runtime pointer drag skipped: " + RootMessage(ex));
            }
        }

        private static JObject UiToolkitPointerDragArgs(Vector2 screenStartPosition, Vector2 screenDelta, int steps, bool dryRun)
        {
            return new JObject
            {
                ["action"] = "pointerDrag",
                ["normalized"] = NormalizedScreenPosition(screenStartPosition),
                ["delta"] = new JObject { ["x"] = screenDelta.x, ["y"] = -screenDelta.y },
                ["steps"] = Mathf.Clamp(steps, 1, 120),
                ["dryRun"] = dryRun,
                ["allowStateMutation"] = true,
            };
        }

        private static void TryDispatchUguiPointerClick(Vector2 screenPosition, List<string> warnings)
        {
            var dryRunArgs = UguiPointerClickArgs(screenPosition, dryRun: true);
            try
            {
                if (!Chievfx.Mcp.Editor.ChievfxMcpExtensionRegistry.TryRunTool("ugui-runtime-click", dryRunArgs, out var dryRunResult)
                    || dryRunResult is not Dictionary<string, object?> dryRun
                    || !dryRun.TryGetValue("target", out var target)
                    || target == null)
                {
                    return;
                }

                Chievfx.Mcp.Editor.ChievfxMcpExtensionRegistry.TryRunTool("ugui-runtime-click", UguiPointerClickArgs(screenPosition, dryRun: false), out _);
            }
            catch (Exception ex)
            {
                warnings.Add("uGUI runtime pointer dispatch skipped: " + RootMessage(ex));
            }
        }

        private static JObject UguiPointerClickArgs(Vector2 screenPosition, bool dryRun)
        {
            return new JObject
            {
                ["normalized"] = NormalizedScreenPosition(screenPosition),
                ["dryRun"] = dryRun,
                ["allowStateMutation"] = true,
            };
        }

        private static void TryDispatchUguiPointerDrag(Vector2 screenStartPosition, Vector2 screenDelta, List<string> warnings)
        {
            var dryRunArgs = UguiPointerDragArgs(screenStartPosition, screenDelta, dryRun: true);
            try
            {
                if (!Chievfx.Mcp.Editor.ChievfxMcpExtensionRegistry.TryRunTool("ugui-runtime-drag", dryRunArgs, out var dryRunResult)
                    || dryRunResult is not Dictionary<string, object?> dryRun
                    || !dryRun.TryGetValue("target", out var target)
                    || target == null)
                {
                    return;
                }

                Chievfx.Mcp.Editor.ChievfxMcpExtensionRegistry.TryRunTool("ugui-runtime-drag", UguiPointerDragArgs(screenStartPosition, screenDelta, dryRun: false), out _);
            }
            catch (Exception ex)
            {
                warnings.Add("uGUI runtime pointer drag skipped: " + RootMessage(ex));
            }
        }

        private static JObject UguiPointerDragArgs(Vector2 screenStartPosition, Vector2 screenDelta, bool dryRun)
        {
            var start = NormalizedScreenPosition(screenStartPosition);
            var end = NormalizedScreenPosition(screenStartPosition + screenDelta);
            return new JObject
            {
                ["startNormalized"] = start,
                ["endNormalized"] = end,
                ["dryRun"] = dryRun,
                ["allowStateMutation"] = true,
            };
        }

        private static JObject NormalizedScreenPosition(Vector2 screenPosition)
        {
            return new JObject
            {
                ["x"] = Mathf.Clamp01(screenPosition.x / Mathf.Max(1f, Screen.width)),
                ["y"] = Mathf.Clamp01(screenPosition.y / Mathf.Max(1f, Screen.height)),
            };
        }

        private static string RootMessage(Exception ex)
        {
            while (ex is TargetInvocationException && ex.InnerException != null)
            {
                ex = ex.InnerException;
            }

            return string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
        }

        private static bool IsPlaying => playModeOverrideForTests ?? EditorApplication.isPlaying;

        private static Dictionary<string, object?> EventRow(string device, string action, string? target, Vector2? position, Vector2? delta, double time)
        {
            return new Dictionary<string, object?>
            {
                ["device"] = device,
                ["action"] = action,
                ["target"] = target,
                ["position"] = position.HasValue ? Vec(position.Value) : null,
                ["delta"] = delta.HasValue ? Vec(delta.Value) : null,
                ["time"] = time < 0d ? "default" : time.ToString("0.######", CultureInfo.InvariantCulture),
            };
        }

        private static Dictionary<string, object?> Vec(Vector2 v) => new() { ["x"] = v.x, ["y"] = v.y };

        private static Dictionary<string, object?> DeviceRow(string kind, object? device)
        {
            return new Dictionary<string, object?>
            {
                ["kind"] = kind,
                ["available"] = device != null,
                ["type"] = device?.GetType().FullName,
                ["displayName"] = device?.GetType().GetProperty("displayName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)?.GetValue(device)?.ToString(),
                ["name"] = device?.GetType().GetProperty("name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)?.GetValue(device)?.ToString(),
            };
        }

        private static float Ease(float t, string ease)
        {
            t = Mathf.Clamp01(t);
            return ease switch
            {
                "in" => t * t,
                "out" => 1f - (1f - t) * (1f - t),
                _ => t < 0.5f ? 2f * t * t : 1f - Mathf.Pow(-2f * t + 2f, 2f) / 2f,
            };
        }

        private static string TouchPhaseForAction(string action)
        {
            return action switch
            {
                "down" => "Began",
                "move" => "Moved",
                "up" => "Ended",
                _ => "Moved",
            };
        }

        private static string? ReadString(JToken token, string name) => token[name]?.Type == JTokenType.String ? token[name]!.Value<string>() : null;
        private static bool ReadBool(JToken token, string name, bool value) => token[name]?.Type == JTokenType.Boolean ? token[name]!.Value<bool>() : value;
        private static int ReadInt(JToken token, string name, int value) => token[name]?.Type == JTokenType.Integer ? token[name]!.Value<int>() : value;
        private static double ReadDouble(JToken token, string name, double value) => token[name]?.Type is JTokenType.Integer or JTokenType.Float ? token[name]!.Value<double>() : value;
        private static bool OneOf(string value, params string[] options) => options.Any(option => string.Equals(value, option, StringComparison.Ordinal));
        private static string Norm(string? value) => new((value ?? string.Empty).Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        private static bool TryVector(JToken token, string name, out Vector2 value)
        {
            value = default;
            if (token[name] is not JObject obj) return false;
            value = new Vector2(obj["x"]?.Value<float>() ?? 0f, obj["y"]?.Value<float>() ?? 0f);
            return obj["x"] != null || obj["y"] != null;
        }

        private static JObject KeyboardSchema() => Schema(new JObject
        {
            ["action"] = Enum("Keyboard action.", "down", "up", "tap"),
            ["key"] = Str("Input System Key enum name."),
            ["durationMs"] = Num("Optional metadata; tap queues down then up."),
            ["dryRun"] = Bool("Report intended events without input mutation."),
            ["allowStateMutation"] = Bool("Required true for real input injection."),
        }, "action", "key");

        private static JObject MouseSchema() => Schema(new JObject
        {
            ["action"] = Enum("Mouse action.", "down", "up", "tap", "move"),
            ["button"] = Enum("Mouse button.", "left", "right", "middle", "forward", "back"),
            ["position"] = Vector("Absolute screen position, origin bottom-left."),
            ["screenPosition"] = Vector("Alias for position."),
            ["delta"] = Vector("Relative mouse delta."),
            ["dryRun"] = Bool("Report intended events without input mutation."),
            ["allowStateMutation"] = Bool("Required true for real input injection."),
        }, "action");

        private static JObject GestureSchema() => Schema(new JObject
        {
            ["button"] = Enum("Mouse button.", "left", "right", "middle", "forward", "back"),
            ["position"] = Vector("Optional absolute start screen position."),
            ["startPosition"] = Vector("Alias for position."),
            ["screenPosition"] = Vector("Alias for position."),
            ["delta"] = Vector("Total gesture delta."),
            ["durationMs"] = Num("Gesture duration in milliseconds."),
            ["steps"] = Int("Interpolation steps, 1..240."),
            ["ease"] = Enum("Interpolation curve.", "inout", "in", "out"),
            ["includeDown"] = Bool("Queue button down at gesture start."),
            ["includeUp"] = Bool("Queue button up at gesture end."),
            ["dryRun"] = Bool("Defaults true for gesture."),
            ["allowStateMutation"] = Bool("Required true for real input injection."),
        }, "delta");

        private static JObject TouchSchema() => Schema(new JObject
        {
            ["action"] = Enum("Touch action.", "down", "up", "tap", "move"),
            ["touchId"] = Int("Touch identifier. Defaults to 1."),
            ["position"] = Vector("Absolute screen position, origin bottom-left."),
            ["screenPosition"] = Vector("Alias for position."),
            ["delta"] = Vector("Relative touch delta for move/up metadata."),
            ["dryRun"] = Bool("Report intended events without input mutation."),
            ["allowStateMutation"] = Bool("Required true for real input injection."),
        }, "action");

        private static JObject PlayModeSetSchema() => Schema(new JObject
        {
            ["isPlaying"] = Bool("Desired Play Mode state. true enters Play Mode; false exits Play Mode."),
        }, "isPlaying");

        private static JObject Schema(JObject properties, params string[] required)
        {
            var schema = new JObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = properties };
            if (required.Length > 0) schema["required"] = new JArray(required);
            return schema;
        }

        private static JObject Str(string description) => new() { ["type"] = "string", ["description"] = description };
        private static JObject Bool(string description) => new() { ["type"] = "boolean", ["description"] = description };
        private static JObject Num(string description) => new() { ["type"] = "number", ["description"] = description };
        private static JObject Int(string description) => new() { ["type"] = "integer", ["description"] = description };
        private static JObject Enum(string description, params string[] values) => new() { ["type"] = "string", ["description"] = description, ["enum"] = new JArray(values) };
        private static JObject Vector(string description) => new()
        {
            ["type"] = "object",
            ["description"] = description,
            ["additionalProperties"] = false,
            ["properties"] = new JObject { ["x"] = Num("X component."), ["y"] = Num("Y component.") },
        };

        private sealed class InputApi
        {
            private readonly Type keyboardType;
            private readonly Type mouseType;
            private readonly Type touchscreenType;
            private readonly Type keyType;
            private readonly MethodInfo queueState;
            private readonly MethodInfo? updateMethod;
            private readonly Type keyboardStateType;
            private readonly Type mouseStateType;
            private readonly Type mouseButtonType;
            private readonly Type touchStateType;
            private readonly Type touchPhaseType;

            private InputApi(Type inputSystemType, Type keyboardType, Type mouseType, Type touchscreenType, Type keyType, MethodInfo queueState, MethodInfo? updateMethod, Type keyboardStateType, Type mouseStateType, Type mouseButtonType, Type touchStateType, Type touchPhaseType)
            {
                this.keyboardType = keyboardType;
                this.mouseType = mouseType;
                this.touchscreenType = touchscreenType;
                this.keyType = keyType;
                this.queueState = queueState;
                this.updateMethod = updateMethod;
                this.keyboardStateType = keyboardStateType;
                this.mouseStateType = mouseStateType;
                this.mouseButtonType = mouseButtonType;
                this.touchStateType = touchStateType;
                this.touchPhaseType = touchPhaseType;
                Keyboard = keyboardType.GetProperty("current", BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase)?.GetValue(null);
                Mouse = mouseType.GetProperty("current", BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase)?.GetValue(null);
                Touchscreen = touchscreenType.GetProperty("current", BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase)?.GetValue(null);
                Time = EditorApplication.timeSinceStartup;
                _ = inputSystemType;
            }

            public object? Keyboard { get; }
            public object? Mouse { get; }
            public object? Touchscreen { get; }
            public double Time { get; }

            public static bool TryCreate(out InputApi? api, out string reason)
            {
                api = null;
#if CHIEVFX_MCP_HAS_INPUTSYSTEM
                var inputSystemType = FindType("UnityEngine.InputSystem.InputSystem");
                var keyboardType = FindType("UnityEngine.InputSystem.Keyboard");
                var mouseType = FindType("UnityEngine.InputSystem.Mouse");
                var touchscreenType = FindType("UnityEngine.InputSystem.Touchscreen");
                var keyType = FindType("UnityEngine.InputSystem.Key");
                var keyboardStateType = FindType("UnityEngine.InputSystem.LowLevel.KeyboardState");
                var mouseStateType = FindType("UnityEngine.InputSystem.LowLevel.MouseState");
                var mouseButtonType = FindType("UnityEngine.InputSystem.LowLevel.MouseButton");
                var touchStateType = FindType("UnityEngine.InputSystem.LowLevel.TouchState");
                var touchPhaseType = FindType("UnityEngine.InputSystem.TouchPhase");
                if (inputSystemType == null || keyboardType == null || mouseType == null || touchscreenType == null || keyType == null || keyboardStateType == null || mouseStateType == null || mouseButtonType == null || touchStateType == null || touchPhaseType == null)
                {
                    reason = "Input System package is installed but required UnityEngine.InputSystem types are not loaded.";
                    return false;
                }

                var queueState = inputSystemType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(method => method.Name == "QueueStateEvent" && method.IsGenericMethodDefinition && method.GetParameters().Length >= 2);
                if (queueState == null)
                {
                    reason = "Input System QueueStateEvent API is unavailable.";
                    return false;
                }

                var updateMethod = inputSystemType.GetMethod("Update", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                api = new InputApi(inputSystemType, keyboardType, mouseType, touchscreenType, keyType, queueState, updateMethod, keyboardStateType, mouseStateType, mouseButtonType, touchStateType, touchPhaseType);
                reason = "Input System types loaded.";
                return true;
#else
                reason = "com.unity.inputsystem package version define is not active.";
                return false;
#endif
            }

            public bool TryKey(string name, out object? key, out string error)
            {
                key = null;
                if (string.IsNullOrWhiteSpace(name))
                {
                    error = "key is required.";
                    return false;
                }

                var aliases = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["return"] = "Enter",
                    ["esc"] = "Escape",
                    ["spacebar"] = "Space",
                    ["ctrl"] = "LeftCtrl",
                    ["control"] = "LeftCtrl",
                    ["shift"] = "LeftShift",
                    ["alt"] = "LeftAlt",
                };
                var normalized = Norm(name);
                var match = System.Enum.GetNames(keyType).FirstOrDefault(enumName => Norm(enumName) == normalized)
                    ?? (aliases.TryGetValue(normalized, out var alias) ? alias : null);
                if (match == null)
                {
                    error = $"Invalid key '{name}'. Use a UnityEngine.InputSystem.Key enum name.";
                    return false;
                }

                key = System.Enum.Parse(keyType, match);
                error = string.Empty;
                return true;
            }

            public bool TryKeyboardControl(object key, out object? control, out string error)
            {
                control = keyboardType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(property => property.Name == "Item" && property.GetIndexParameters().FirstOrDefault()?.ParameterType == keyType)
                    ?.GetValue(Keyboard, new[] { key });
                error = control == null ? $"Keyboard control for key '{key}' is unavailable." : string.Empty;
                return control != null;
            }

            public bool TryMouseButton(string name, out object? control, out string error)
            {
                var property = Norm(name) switch
                {
                    "" or "left" => "leftButton",
                    "right" => "rightButton",
                    "middle" => "middleButton",
                    "forward" => "forwardButton",
                    "back" => "backButton",
                    _ => null,
                };
                control = property == null ? null : mouseType.GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)?.GetValue(Mouse);
                error = property == null ? $"Invalid mouse button '{name}'. Use left, right, middle, forward, or back." : control == null ? $"Mouse control '{property}' is unavailable." : string.Empty;
                return control != null;
            }

            public bool TryMouseControl(string name, out object? control, out string error)
            {
                control = mouseType.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)?.GetValue(Mouse);
                error = control == null ? $"Mouse control '{name}' is unavailable." : string.Empty;
                return control != null;
            }

            public bool TryTouchControl(string name, out object? control, out string error)
            {
                control = touchscreenType.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)?.GetValue(Touchscreen);
                error = control == null ? $"Touchscreen control '{name}' is unavailable." : string.Empty;
                return control != null;
            }

            public void QueueKeyboardKey(object key, bool pressed, double eventTime)
            {
                var state = CreateKeyboardState();
                SetKeyboardStateKey(state, key, pressed);
                QueueState(Keyboard!, state, keyboardStateType, eventTime);
            }

            public void QueueMouseButton(string buttonName, bool pressed, double eventTime)
            {
                var state = CreateMouseState(null, null);
                state = WithMouseButton(state, buttonName, pressed);
                QueueState(Mouse!, state, mouseStateType, eventTime);
            }

            public void QueueMouseMove(Vector2? position, Vector2? delta, double eventTime)
            {
                var state = CreateMouseState(position, delta);
                QueueState(Mouse!, state, mouseStateType, eventTime);
            }

            public void QueueTouch(int touchId, string phaseName, Vector2 position, Vector2 delta, double eventTime)
            {
                var state = CreateTouchState(touchId, phaseName, position, delta);
                QueueState(Touchscreen!, state, touchStateType, eventTime);
            }

            public bool TryUpdate(out string warning)
            {
                warning = string.Empty;
                if (updateMethod == null)
                {
                    warning = "Queued input but could not call InputSystem.Update(); state may not be visible until next player loop.";
                    return false;
                }

                updateMethod.Invoke(null, Array.Empty<object>());
                return true;
            }

            private void QueueState(object device, object state, Type stateType, double eventTime)
            {
                var method = queueState.MakeGenericMethod(stateType);
                var parameters = method.GetParameters();
                method.Invoke(null, parameters.Length >= 3 ? new[] { device, state, eventTime } : new[] { device, state });
            }

            private object CreateKeyboardState()
            {
                var state = Activator.CreateInstance(keyboardStateType)!;
                if (keyboardType.GetProperty("allKeys", BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)?.GetValue(Keyboard) is System.Collections.IEnumerable keys)
                {
                    foreach (var keyControl in keys)
                    {
                        var keyCode = keyControl?.GetType().GetProperty("keyCode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)?.GetValue(keyControl);
                        if (keyCode != null)
                        {
                            SetKeyboardStateKey(state, keyCode, ReadPressed(keyControl));
                        }
                    }
                }

                return state;
            }

            private void SetKeyboardStateKey(object state, object key, bool pressed)
            {
                keyboardStateType.GetMethod("Set", BindingFlags.Instance | BindingFlags.Public, null, new[] { keyType, typeof(bool) }, null)
                    ?.Invoke(state, new[] { key, pressed });
            }

            private object CreateMouseState(Vector2? position, Vector2? delta)
            {
                var state = Activator.CreateInstance(mouseStateType)!;
                foreach (var buttonName in new[] { "left", "right", "middle", "forward", "back" })
                {
                    if (TryMouseButton(buttonName, out var control, out _))
                    {
                        state = WithMouseButton(state, buttonName, ReadPressed(control));
                    }
                }

                SetMouseVector(state, "position", position ?? ReadMouseVector("position"));
                SetMouseVector(state, "delta", delta ?? Vector2.zero);
                return state;
            }

            private object WithMouseButton(object state, string buttonName, bool pressed)
            {
                var enumName = Norm(buttonName) switch
                {
                    "" or "left" => "Left",
                    "right" => "Right",
                    "middle" => "Middle",
                    "forward" => "Forward",
                    "back" => "Back",
                    _ => "Left",
                };
                var button = System.Enum.Parse(mouseButtonType, enumName);
                return mouseStateType.GetMethod("WithButton", BindingFlags.Instance | BindingFlags.Public, null, new[] { mouseButtonType, typeof(bool) }, null)
                    ?.Invoke(state, new[] { button, pressed }) ?? state;
            }

            private static bool ReadPressed(object? control)
            {
                return control?.GetType().GetProperty("isPressed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)?.GetValue(control) is bool pressed && pressed;
            }

            private Vector2 ReadMouseVector(string property)
            {
                var control = mouseType.GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)?.GetValue(Mouse);
                var readValue = control?.GetType().GetMethod("ReadValue", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                return readValue?.ReturnType == typeof(Vector2) && readValue.Invoke(control, Array.Empty<object>()) is Vector2 value ? value : Vector2.zero;
            }

            public Vector2 ReadMousePosition()
            {
                return ReadMouseVector("position");
            }

            private void SetMouseVector(object state, string fieldName, Vector2 value)
            {
                var field = mouseStateType.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                if (field != null && field.FieldType == typeof(Vector2))
                {
                    field.SetValue(state, value);
                }
            }

            public Vector2 ReadPrimaryTouchPosition()
            {
                var primaryTouch = touchscreenType.GetProperty("primaryTouch", BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)?.GetValue(Touchscreen);
                var position = primaryTouch?.GetType().GetProperty("position", BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)?.GetValue(primaryTouch);
                var readValue = position?.GetType().GetMethod("ReadValue", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                return readValue?.ReturnType == typeof(Vector2) && readValue.Invoke(position, Array.Empty<object>()) is Vector2 value ? value : Vector2.zero;
            }

            private object CreateTouchState(int touchId, string phaseName, Vector2 position, Vector2 delta)
            {
                var state = Activator.CreateInstance(touchStateType)!;
                SetTouchMember(state, "touchId", touchId);
                SetTouchMember(state, "position", position);
                SetTouchMember(state, "delta", delta);
                SetTouchMember(state, "startPosition", position);
                SetTouchMember(state, "pressure", phaseName is "Ended" or "Canceled" ? 0f : 1f);
                SetTouchMember(state, "phase", System.Enum.Parse(touchPhaseType, phaseName));
                SetTouchMember(state, "isPrimaryTouch", true);
                return state;
            }

            private void SetTouchMember(object state, string memberName, object value)
            {
                var property = touchStateType.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                if (property != null && property.CanWrite && CanAssign(property.PropertyType, value))
                {
                    property.SetValue(state, value);
                    return;
                }

                var field = touchStateType.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                if (field != null && CanAssign(field.FieldType, value))
                {
                    field.SetValue(state, value);
                }
            }

            private static bool CanAssign(Type targetType, object value)
            {
                var valueType = value.GetType();
                if (targetType.IsInstanceOfType(value))
                {
                    return true;
                }

                return targetType.IsEnum && valueType == targetType;
            }

            private static Type? FindType(string fullName)
            {
                return AppDomain.CurrentDomain.GetAssemblies()
                    .Select(assembly => assembly.GetType(fullName, throwOnError: false))
                    .FirstOrDefault(type => type != null);
            }
        }
    }
}
