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
        public object State()
        {
            return new
            {
                enabled = GetProfilerEnabled(),
                targets = GetProfilerTargets()
            };
        }

        public object Start(JToken args)
        {
            var connectionId = ReadNullableInt(args, "connectionId");
            if (connectionId.HasValue)
            {
                SelectProfilerConnection(connectionId.Value);
            }

            SetProfilerEnabled(true);
            return new
            {
                started = true,
                enabled = GetProfilerEnabled(),
                connectionId,
                targets = GetProfilerTargets()
            };
        }

        public object Stop(JToken args)
        {
            SetProfilerEnabled(false);

            var savePath = ReadString(args, "savePath");
            if (string.IsNullOrWhiteSpace(savePath))
            {
                var fileName = "profile-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".data";
                savePath = Path.Combine(ChievfxMcpToolPolicy.ProjectRoot, "Library", "ChievfxMcpBridge", "profiles", fileName);
            }
            else if (!Path.IsPathRooted(savePath))
            {
                savePath = Path.Combine(ChievfxMcpToolPolicy.ProjectRoot, savePath);
            }

            var saved = SaveProfilerCapture(savePath!);
            return new
            {
                stopped = true,
                enabled = GetProfilerEnabled(),
                saved,
                path = savePath,
                exists = File.Exists(savePath)
            };
        }

        public object Counters()
        {
            return new
            {
                enabled = GetProfilerEnabled(),
                totalAllocatedMemory = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong(),
                totalReservedMemory = UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong(),
                monoUsedSize = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong(),
                monoHeapSize = UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong()
            };
        }

        private static Type GetProfilerDriverType()
        {
            return typeof(EditorWindow).Assembly.GetType("UnityEditorInternal.ProfilerDriver")
                ?? throw new NotSupportedException("UnityEditorInternal.ProfilerDriver is unavailable in this Unity version.");
        }

        private static bool GetProfilerEnabled()
        {
            var driverType = GetProfilerDriverType();
            var property = driverType.GetProperty("IsProfilingEnabled", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null)
            {
                return Convert.ToBoolean(property.GetValue(null), CultureInfo.InvariantCulture);
            }

            var method = driverType.GetMethod("get_IsProfilingEnabled", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (method != null)
            {
                return Convert.ToBoolean(method.Invoke(null, null), CultureInfo.InvariantCulture);
            }

            method = driverType.GetMethod("IsProfilingEnabled", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (method != null)
            {
                return Convert.ToBoolean(method.Invoke(null, null), CultureInfo.InvariantCulture);
            }

            throw new NotSupportedException("Profiler enabled state is unsupported in this Unity version.");
        }

        private static void SetProfilerEnabled(bool enabled)
        {
            var driverType = GetProfilerDriverType();
            var method = driverType.GetMethod("SetProfilingEnabled", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(bool) }, null)
                ?? throw new NotSupportedException("Profiler recording controls are unsupported in this Unity version.");
            method.Invoke(null, new object[] { enabled });
        }

        private static void SelectProfilerConnection(int connectionId)
        {
            var driverType = GetProfilerDriverType();
            var method = driverType.GetMethod("SetRemoteEditorConnection", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(int) }, null)
                ?? throw new NotSupportedException("Profiler connection selection is unsupported in this Unity version.");
            method.Invoke(null, new object[] { connectionId });
        }

        private static bool SaveProfilerCapture(string savePath)
        {
            var driverType = GetProfilerDriverType();
            var method = driverType.GetMethod("SaveProfile", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null)
                ?? throw new NotSupportedException("Profiler capture saving is unsupported in this Unity version.");

            Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);
            method.Invoke(null, new object[] { savePath });
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            return true;
        }

        private static object[] GetProfilerTargets()
        {
            var driverType = GetProfilerDriverType();
            var method = driverType.GetMethod("GetAvailableProfilers", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (method == null)
            {
                return Array.Empty<object>();
            }

            if (!(method.Invoke(null, null) is Array rawTargets))
            {
                return Array.Empty<object>();
            }

            var targets = new List<object>();
            foreach (var rawTarget in rawTargets)
            {
                if (rawTarget == null)
                {
                    continue;
                }

                var id = Convert.ToInt32(rawTarget, CultureInfo.InvariantCulture);
                targets.Add(new
                {
                    id,
                    name = InvokeProfilerString(driverType, "GetConnectionName", id),
                    identifier = InvokeProfilerString(driverType, "GetConnectionIdentifier", id)
                });
            }

            return targets.ToArray();
        }

        private static string? InvokeProfilerString(Type driverType, string methodName, int connectionId)
        {
            var method = driverType.GetMethod(methodName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(int) }, null);
            if (method == null)
            {
                return null;
            }

            try
            {
                return method.Invoke(null, new object[] { connectionId }) as string;
            }
            catch (TargetInvocationException)
            {
                return null;
            }
        }

    }
}
