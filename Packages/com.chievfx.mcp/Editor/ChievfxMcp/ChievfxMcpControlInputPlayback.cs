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
    /// Dispatches injected input steps aligned to player-loop frames. Queued events must be
    /// consumed by the player loop's own input update for wasPressedThisFrame/wasReleasedThisFrame
    /// edges to be visible to game code polling in MonoBehaviour.Update(); flushing with a manual
    /// InputSystem.Update() consumes the edge outside any rendered frame.
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
        private static Sequence? active;
        private static bool hooked;
        private static long sequenceCounter;
        private static long lastDispatchFrame = -1_000_000L;

        public static int PendingSequenceCount => PendingSequences.Count + (active == null ? 0 : 1);

        public static string Schedule(string kind, IEnumerable<Step> steps)
        {
            var marker = "input-seq-" + (++sequenceCounter).ToString(CultureInfo.InvariantCulture) + "-" + kind;
            PendingSequences.Enqueue(new Sequence { Kind = kind, Marker = marker, Steps = steps.ToList() });
            EnsureHooked();
            return marker;
        }

        private static void EnsureHooked()
        {
            if (hooked)
            {
                return;
            }

            hooked = true;
            EditorApplication.update += OnEditorUpdate;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.ExitingPlayMode)
            {
                return;
            }

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

        private static void OnEditorUpdate()
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
            if (EditorApplication.isPlaying)
            {
                // Space steps by rendered frames so each event batch lands in a distinct
                // player-loop input update. Outside Play Mode (test overrides), flush per tick.
                long frame = Time.frameCount;
                if (frame - lastDispatchFrame < step.FrameGapBefore)
                {
                    return;
                }

                lastDispatchFrame = frame;
            }

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
            return new Dictionary<string, object?>
            {
                ["active"] = Active,
                ["virtualMouseName"] = Active ? VirtualMouseName : null,
                ["disabledPhysicalMice"] = DisabledPhysicalMice.Count,
                ["inputRoutingOverridden"] = routingOverridden,
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
