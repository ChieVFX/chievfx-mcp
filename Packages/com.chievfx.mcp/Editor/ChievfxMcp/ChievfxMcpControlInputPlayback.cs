#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Chievfx.Mcp.Extensions.Control
{
    /// <summary>
    /// Applies injected input from inside the player loop's own input updates. State writes go
    /// through InputState.Change during InputSystem.onBeforeUpdate of a player update (the pattern
    /// Unity's VirtualMouseInput uses): the native event queue is bypassed, so injection works even
    /// when the editor application has no OS focus (natively queued events are silently dropped
    /// then), and press/release edges land in the exact update step game code polls with
    /// wasPressedThisFrame. Multi-step sequences (taps, gestures) are spaced across player updates.
    /// </summary>
    internal static class ChievfxMcpControlInputPlayback
    {
        internal sealed class Step
        {
            public int FrameGapBefore;
            public Action Dispatch = () => { };
        }

        private sealed class Sequence
        {
            public string Kind = string.Empty;
            public string Marker = string.Empty;
            public List<Step> Steps = new();
            public int NextStep;
        }

        private static readonly Queue<Sequence> PendingSequences = new();
        private static readonly Queue<Action> PendingApplies = new();
        private static Sequence? active;
        private static bool playModeHooked;
        private static bool beforeUpdateHooked;
        private static bool draining;
        private static long sequenceCounter;
        private static long updateCounter;
        private static long lastDispatchUpdate = -1_000_000L;
        private static PropertyInfo? currentUpdateTypeProperty;
        private static MethodInfo? changeStateMethod;

        public static int PendingSequenceCount => PendingSequences.Count + (active == null ? 0 : 1);

        public static string Schedule(string kind, IEnumerable<Step> steps)
        {
            var marker = "input-seq-" + (++sequenceCounter).ToString(CultureInfo.InvariantCulture) + "-" + kind;
            PendingSequences.Enqueue(new Sequence { Kind = kind, Marker = marker, Steps = steps.ToList() });
            EnsureHooked();
            return marker;
        }

        /// <summary>
        /// Runs <paramref name="apply"/> inside the next qualifying input update, where state
        /// reads/writes hit the buffers game code sees (editor-context reads use separate editor
        /// state buffers). Runs immediately when already inside one (scheduled sequence steps).
        /// </summary>
        public static void RunInInputUpdate(Action apply)
        {
            if (draining)
            {
                apply();
                return;
            }

            PendingApplies.Enqueue(apply);
            EnsureHooked();
        }

        /// <summary>
        /// Writes a full state snapshot to a device or control via InputState.Change. Must be
        /// called from inside an input update (see <see cref="RunInInputUpdate"/>).
        /// </summary>
        public static void ApplyState(object deviceOrControl, object state, Type stateType)
        {
            if (changeStateMethod == null)
            {
                var inputStateType = FindType("UnityEngine.InputSystem.LowLevel.InputState")
                    ?? throw new InvalidOperationException("UnityEngine.InputSystem.LowLevel.InputState is unavailable.");
                changeStateMethod = inputStateType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(method => method.Name == "Change"
                        && method.IsGenericMethodDefinition
                        && method.GetParameters().Length == 4
                        && !method.GetParameters()[1].ParameterType.IsByRef)
                    ?? throw new InvalidOperationException("InputState.Change<TState> is unavailable.");
            }

            var change = changeStateMethod.MakeGenericMethod(stateType);
            var parameters = change.GetParameters();
            change.Invoke(null, new[]
            {
                deviceOrControl,
                state,
                Activator.CreateInstance(parameters[2].ParameterType),
                Activator.CreateInstance(parameters[3].ParameterType),
            });
        }

        private static void EnsureHooked()
        {
            if (!playModeHooked)
            {
                playModeHooked = true;
                EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            }

            if (!beforeUpdateHooked)
            {
                beforeUpdateHooked = TryHookBeforeUpdate();
                if (!beforeUpdateHooked)
                {
                    Journal("hook-failed", "error", "Could not subscribe to InputSystem.onBeforeUpdate; injected input will not apply.");
                }
            }
        }

        private static bool TryHookBeforeUpdate()
        {
            try
            {
                var inputSystemType = FindType("UnityEngine.InputSystem.InputSystem");
                var beforeUpdateEvent = inputSystemType?.GetEvent("onBeforeUpdate", BindingFlags.Public | BindingFlags.Static);
                if (beforeUpdateEvent == null)
                {
                    return false;
                }

                beforeUpdateEvent.AddEventHandler(null, new Action(OnBeforeInputUpdate));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.ExitingPlayMode)
            {
                return;
            }

            PendingApplies.Clear();
            while (active != null || PendingSequences.Count > 0)
            {
                var sequence = active ?? PendingSequences.Dequeue();
                active = null;
                Journal(
                    "sequence-cancelled",
                    "warning",
                    $"Input sequence '{sequence.Kind}' cancelled: Play Mode exited before it finished.",
                    sequence.Marker,
                    sequence.Kind);
            }

            ChievfxMcpControlPointerCapture.EndSession(out _);
        }

        private static void OnBeforeInputUpdate()
        {
            if (PendingApplies.Count == 0 && active == null && PendingSequences.Count == 0)
            {
                return;
            }

            if (!IsQualifyingUpdate())
            {
                return;
            }

            updateCounter++;
            draining = true;
            try
            {
                while (PendingApplies.Count > 0)
                {
                    var apply = PendingApplies.Dequeue();
                    try
                    {
                        apply();
                    }
                    catch (Exception ex)
                    {
                        Journal("apply-failed", "error", "Injected input state apply failed: " + ex.Message);
                    }
                }

                DispatchNextSequenceStep();
            }
            finally
            {
                draining = false;
            }
        }

        private static void DispatchNextSequenceStep()
        {
            if (active == null && PendingSequences.Count > 0)
            {
                active = PendingSequences.Dequeue();
            }

            if (active == null)
            {
                return;
            }

            var step = active.Steps[active.NextStep];
            if (updateCounter - lastDispatchUpdate < step.FrameGapBefore)
            {
                return;
            }

            lastDispatchUpdate = updateCounter;
            try
            {
                step.Dispatch();
            }
            catch (Exception ex)
            {
                Journal(
                    "sequence-failed",
                    "error",
                    $"Input sequence '{active.Kind}' failed at step {active.NextStep + 1}/{active.Steps.Count}: {ex.Message}",
                    active.Marker,
                    active.Kind);
                active = null;
                return;
            }

            active.NextStep++;
            if (active.NextStep >= active.Steps.Count)
            {
                Journal(
                    "sequence-complete",
                    "info",
                    $"Input sequence '{active.Kind}' complete ({active.Steps.Count} steps).",
                    active.Marker,
                    active.Kind);
                active = null;
            }
        }

        private static bool IsQualifyingUpdate()
        {
            try
            {
                currentUpdateTypeProperty ??= FindType("UnityEngine.InputSystem.LowLevel.InputState")
                    ?.GetProperty("currentUpdateType", BindingFlags.Public | BindingFlags.Static);
                var updateType = currentUpdateTypeProperty?.GetValue(null)?.ToString();
                if (updateType is null or "None" or "BeforeRender")
                {
                    return false;
                }

                // In Play Mode only player-loop updates (Dynamic/Fixed/Manual) write the state
                // buffers game code reads; editor updates use separate buffers. Outside Play Mode
                // (test overrides), editor updates are the only ones running.
                return EditorApplication.isPlaying ? updateType != "Editor" : updateType == "Editor";
            }
            catch
            {
                return false;
            }
        }

        internal static Type? FindType(string fullName)
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(fullName, throwOnError: false))
                .FirstOrDefault(type => type != null);
        }

        internal static void Journal(string type, string level, string message, string? marker = null, string? kind = null)
        {
            global::Chievfx.Mcp.Editor.ChievfxMcpBridgeHost.EventJournal.Write(
                "input",
                type,
                level,
                message,
                marker: marker,
                data: kind == null ? null : new Dictionary<string, object?> { ["kind"] = kind });
        }
    }

    /// <summary>
    /// Last injected mouse state, recorded at apply time inside the player loop. Exposed for
    /// observability: editor-context reads (e.g. script-execute polling Mouse.current) use editor
    /// state buffers and cannot see player-loop input state.
    /// </summary>
    internal static class ChievfxMcpControlAppliedInputState
    {
        public static Vector2? MousePosition;
        public static Vector2? MouseDelta;
        public static long MouseApplyCount;

        public static void RecordMouse(Vector2 position, Vector2 delta)
        {
            MousePosition = position;
            MouseDelta = delta;
            MouseApplyCount++;
        }

        public static void Reset()
        {
            MousePosition = null;
            MouseDelta = null;
            MouseApplyCount = 0;
        }
    }

    /// <summary>
    /// While a capture session is active, injected mouse events drive a virtual mouse device and
    /// physical mice are disabled, so the editor's continuous OS pointer feed cannot overwrite
    /// injected positions each frame. The session also routes all device input to the Game view
    /// regardless of focus. Everything is restored on session end or Play Mode exit.
    /// </summary>
    internal static class ChievfxMcpControlPointerCapture
    {
        public const string VirtualMouseName = "ChievfxMcpVirtualMouse";

        private static readonly List<object> DisabledPhysicalMice = new();
        private static object? previousRoutingBehavior;
        private static bool routingOverridden;
        private static bool hooked;

        public static object? VirtualMouse { get; private set; }

        public static bool Active => VirtualMouse != null;

        public static bool RoutingOverridden => routingOverridden;

        public static string? CurrentRoutingBehavior()
        {
            try
            {
                var inputSystemType = FindType("UnityEngine.InputSystem.InputSystem");
                var settings = inputSystemType?.GetProperty("settings", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                return settings?.GetType().GetProperty("editorInputBehaviorInPlayMode", BindingFlags.Instance | BindingFlags.Public)
                    ?.GetValue(settings)?.ToString();
            }
            catch
            {
                return null;
            }
        }

        public static bool TryBegin(out bool created, out Vector2 seedPosition, out string error)
        {
            created = false;
            seedPosition = Vector2.zero;
            error = string.Empty;
            if (Active)
            {
                return true;
            }

            if (!EditorApplication.isPlaying)
            {
                error = "Pointer capture requires Play Mode.";
                return false;
            }

            var inputSystemType = FindType("UnityEngine.InputSystem.InputSystem");
            var mouseType = FindType("UnityEngine.InputSystem.Mouse");
            if (inputSystemType == null || mouseType == null)
            {
                error = "Input System types are not loaded.";
                return false;
            }

            EnsureHooked();
            EnsureInputRoutingOverride();
            seedPosition = ReadDevicePosition(
                mouseType.GetProperty("current", BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase)?.GetValue(null));

            try
            {
                foreach (var device in EnumerateDevices(inputSystemType))
                {
                    if (!mouseType.IsInstanceOfType(device)
                        || string.Equals(ReadProperty(device, "name") as string, VirtualMouseName, StringComparison.Ordinal)
                        || ReadProperty(device, "enabled") is not true)
                    {
                        continue;
                    }

                    InvokeDeviceMethod(inputSystemType, "DisableDevice", device);
                    DisabledPhysicalMice.Add(device);
                }

                var addDevice = inputSystemType.GetMethod(
                    "AddDevice",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(string), typeof(string), typeof(string) },
                    null);
                VirtualMouse = addDevice?.Invoke(null, new object?[] { "Mouse", VirtualMouseName, null });
                if (VirtualMouse == null)
                {
                    RestorePhysicalMice(inputSystemType);
                    error = addDevice == null
                        ? "InputSystem.AddDevice(layout, name, variants) is unavailable."
                        : "Could not create the virtual mouse device.";
                    return false;
                }

                created = true;
                return true;
            }
            catch (Exception ex)
            {
                RestorePhysicalMice(inputSystemType);
                error = "Pointer capture failed: " + RootMessage(ex);
                return false;
            }
        }

        public static bool EndSession(out string error)
        {
            error = string.Empty;
            RestoreInputRouting();
            var inputSystemType = FindType("UnityEngine.InputSystem.InputSystem");
            if (inputSystemType == null)
            {
                VirtualMouse = null;
                DisabledPhysicalMice.Clear();
                return true;
            }

            if (VirtualMouse != null)
            {
                try
                {
                    InvokeDeviceMethod(inputSystemType, "RemoveDevice", VirtualMouse);
                }
                catch (Exception ex)
                {
                    error = "Could not remove the virtual mouse device: " + RootMessage(ex);
                }

                VirtualMouse = null;
            }

            RestorePhysicalMice(inputSystemType);
            ChievfxMcpControlAppliedInputState.Reset();
            return error.Length == 0;
        }

        /// <summary>
        /// With the default editor input setting, pointer and keyboard events are muted while the
        /// Game view lacks focus, which silently drops injected events. Override to route all
        /// device input to the game while injection is in use; restored on Play Mode exit.
        /// </summary>
        public static void EnsureInputRoutingOverride()
        {
            if (routingOverridden || !EditorApplication.isPlaying)
            {
                return;
            }

            try
            {
                var inputSystemType = FindType("UnityEngine.InputSystem.InputSystem");
                var settings = inputSystemType?.GetProperty("settings", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                var property = settings?.GetType().GetProperty("editorInputBehaviorInPlayMode", BindingFlags.Instance | BindingFlags.Public);
                if (settings == null || property == null || !property.CanWrite
                    || !System.Enum.GetNames(property.PropertyType).Contains("AllDeviceInputAlwaysGoesToGameView"))
                {
                    return;
                }

                var target = System.Enum.Parse(property.PropertyType, "AllDeviceInputAlwaysGoesToGameView");
                var current = property.GetValue(settings);
                if (Equals(current, target))
                {
                    return;
                }

                previousRoutingBehavior = current;
                property.SetValue(settings, target);
                routingOverridden = true;
                EnsureHooked();
            }
            catch
            {
                // Injection still works with the default routing when the Game view has focus.
            }
        }

        public static Dictionary<string, object?> Status()
        {
            var lastPosition = ChievfxMcpControlAppliedInputState.MousePosition;
            var lastDelta = ChievfxMcpControlAppliedInputState.MouseDelta;
            return new Dictionary<string, object?>
            {
                ["active"] = Active,
                ["virtualMouseName"] = Active ? VirtualMouseName : null,
                ["disabledPhysicalMice"] = DisabledPhysicalMice.Count,
                ["inputRoutingOverridden"] = routingOverridden,
                ["appliedMousePosition"] = lastPosition.HasValue
                    ? new Dictionary<string, object?> { ["x"] = lastPosition.Value.x, ["y"] = lastPosition.Value.y }
                    : null,
                ["appliedMouseDelta"] = lastDelta.HasValue
                    ? new Dictionary<string, object?> { ["x"] = lastDelta.Value.x, ["y"] = lastDelta.Value.y }
                    : null,
                ["appliedMouseEventCount"] = ChievfxMcpControlAppliedInputState.MouseApplyCount,
                ["probeNote"] = "Injected state applies inside player-loop input updates. Editor-context reads (e.g. script-execute polling Mouse.current) use separate editor state buffers and may show stale values; verify via gameplay behavior, ui-runtime-probe, or appliedMousePosition here.",
            };
        }

        private static void EnsureHooked()
        {
            if (hooked)
            {
                return;
            }

            hooked = true;
            EditorApplication.playModeStateChanged += change =>
            {
                if (change == PlayModeStateChange.ExitingPlayMode)
                {
                    EndSession(out _);
                }
            };
        }

        private static void RestoreInputRouting()
        {
            if (!routingOverridden)
            {
                return;
            }

            routingOverridden = false;
            try
            {
                var inputSystemType = FindType("UnityEngine.InputSystem.InputSystem");
                var settings = inputSystemType?.GetProperty("settings", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                var property = settings?.GetType().GetProperty("editorInputBehaviorInPlayMode", BindingFlags.Instance | BindingFlags.Public);
                if (settings != null && property != null && property.CanWrite && previousRoutingBehavior != null)
                {
                    property.SetValue(settings, previousRoutingBehavior);
                }
            }
            catch
            {
            }

            previousRoutingBehavior = null;
        }

        private static void RestorePhysicalMice(Type inputSystemType)
        {
            foreach (var device in DisabledPhysicalMice)
            {
                try
                {
                    InvokeDeviceMethod(inputSystemType, "EnableDevice", device);
                }
                catch
                {
                    // The device may have been removed while disabled (e.g. test teardown).
                }
            }

            DisabledPhysicalMice.Clear();
        }

        private static object[] EnumerateDevices(Type inputSystemType)
        {
            return inputSystemType.GetProperty("devices", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                is System.Collections.IEnumerable devices
                ? devices.Cast<object>().ToArray()
                : Array.Empty<object>();
        }

        private static void InvokeDeviceMethod(Type inputSystemType, string methodName, object device)
        {
            var method = inputSystemType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(candidate => candidate.Name == methodName
                    && candidate.GetParameters().Length >= 1
                    && candidate.GetParameters()[0].ParameterType.IsInstanceOfType(device));
            if (method == null)
            {
                throw new InvalidOperationException($"InputSystem.{methodName} is unavailable.");
            }

            var parameters = method.GetParameters();
            var arguments = new object?[parameters.Length];
            arguments[0] = device;
            for (var i = 1; i < parameters.Length; i++)
            {
                arguments[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null;
            }

            method.Invoke(null, arguments);
        }

        private static object? ReadProperty(object target, string name)
        {
            return target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)?.GetValue(target);
        }

        private static Vector2 ReadDevicePosition(object? device)
        {
            var control = device == null ? null : ReadProperty(device, "position");
            var readValue = control?.GetType().GetMethod("ReadValue", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            return readValue?.ReturnType == typeof(Vector2) && readValue.Invoke(control, Array.Empty<object>()) is Vector2 value
                ? value
                : Vector2.zero;
        }

        private static string RootMessage(Exception ex)
        {
            while (ex is TargetInvocationException && ex.InnerException != null)
            {
                ex = ex.InnerException;
            }

            return string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
        }

        private static Type? FindType(string fullName)
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(fullName, throwOnError: false))
                .FirstOrDefault(type => type != null);
        }
    }
}
