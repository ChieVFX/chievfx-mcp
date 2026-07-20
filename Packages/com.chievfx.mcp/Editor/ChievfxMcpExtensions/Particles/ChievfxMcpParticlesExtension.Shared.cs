#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Chievfx.Mcp.Editor;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using PackageManagerPackageInfo = UnityEditor.PackageManager.PackageInfo;
using static Chievfx.Mcp.Extensions.Particles.ChievfxMcpParticlesExtension;
using static Chievfx.Mcp.Extensions.Particles.ParticlesResources;
using static Chievfx.Mcp.Extensions.Particles.ParticlesTools;
using static Chievfx.Mcp.Extensions.Particles.ParticlesModules;
using static Chievfx.Mcp.Extensions.Particles.ParticlesSchemas;
using static Chievfx.Mcp.Extensions.Particles.ParticlesRows;

namespace Chievfx.Mcp.Extensions.Particles
{
    internal static class ParticlesShared
    {
#if CHIEVFX_MCP_HAS_PARTICLESYSTEM
        internal static ParticleSystem RequireSystem(JToken args)
        {
            ParticleSystem? system = null;
            if (TryInstanceId(args, "instanceId", out var instanceId))
            {
                system = UnityObjectIdentity.LegacyInstanceIdToObject(instanceId) as ParticleSystem;
            }

            if (system == null && TryString(args, "targetPath", out var targetPath))
            {
                system = ResolveSystem(targetPath);
            }

            if (system == null)
            {
                throw new ArgumentException("Expected targetPath or instanceId for an existing ParticleSystem.");
            }

            return system;
        }

        internal static ParticleSystem? ResolveSystem(string pathOrInstanceId)
        {
            if (TryInstanceId(pathOrInstanceId, out var instanceId))
            {
                return UnityObjectIdentity.LegacyInstanceIdToObject(instanceId) as ParticleSystem;
            }

            var path = pathOrInstanceId.Trim('/');
            return EnumerateParticleSystems()
                .FirstOrDefault(system => string.Equals(GetTransformPath(system.transform), path, StringComparison.Ordinal));
        }

        internal static Transform? ResolveTransform(string path)
        {
            var normalizedPath = path.Trim('/');
            foreach (var root in GetCurrentScene().GetRootGameObjects())
            {
                if (string.Equals(root.name, normalizedPath, StringComparison.Ordinal))
                {
                    return root.transform;
                }

                if (normalizedPath.StartsWith(root.name + "/", StringComparison.Ordinal))
                {
                    var childPath = normalizedPath.Substring(root.name.Length + 1);
                    var child = root.transform.Find(childPath);
                    if (child != null)
                    {
                        return child;
                    }
                }
            }

            return null;
        }

        internal static string GetTransformPath(Transform transform)
        {
            var parts = new Stack<string>();
            var current = transform;
            while (current != null)
            {
                parts.Push(current.name);
                current = current.parent;
            }

            return string.Join("/", parts);
        }

        internal static string DetailUri(ParticleSystem system)
        {
            return SystemDetailPrefix + Uri.EscapeDataString(UnityObjectIdentity.GetLegacyInstanceId(system).ToString(CultureInfo.InvariantCulture));
        }

        internal static T LoadAsset<T>(string assetPath, string fieldName)
            where T : UnityEngine.Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(assetPath);
            if (asset == null)
            {
                throw new ArgumentException($"{fieldName} '{assetPath}' could not be loaded as {typeof(T).Name}.");
            }

            return asset;
        }

        internal static void MarkChanged(UnityEngine.Object obj)
        {
            // The mutation already applied to the live object. Skip MarkSceneDirty in play mode: it is
            // meaningless there and throws "This cannot be used during play mode." for prefab-instance
            // objects, which would block particles edits during a legitimate (non-persisting) play test.
            EditorUtility.SetDirty(obj);
            if (obj is Component component)
            {
                EditorUtility.SetDirty(component.gameObject);
                if (!EditorApplication.isPlayingOrWillChangePlaymode && component.gameObject.scene.IsValid())
                {
                    EditorSceneManager.MarkSceneDirty(component.gameObject.scene);
                }
            }
            else if (obj is GameObject gameObject)
            {
                if (!EditorApplication.isPlayingOrWillChangePlaymode && gameObject.scene.IsValid())
                {
                    EditorSceneManager.MarkSceneDirty(gameObject.scene);
                }
            }
        }

        internal static string RequiredString(JToken args, string name)
        {
            if (!TryString(args, name, out var value) || string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException($"Expected non-empty string field '{name}'.");
            }

            return value;
        }

        internal static string OptionalString(JToken args, string name, string fallback)
        {
            return TryString(args, name, out var value) ? value : fallback;
        }

        internal static bool TryString(JToken args, string name, out string value)
        {
            value = string.Empty;
            var token = args[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                return false;
            }

            value = token.Value<string>() ?? string.Empty;
            return true;
        }

        internal static bool TryInstanceId(JToken args, string name, out int instanceId)
        {
            instanceId = 0;
            if (!TryString(args, name, out var value))
            {
                return false;
            }

            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out instanceId))
            {
                return true;
            }

            return false;
        }

        internal static bool TryInstanceId(string value, out int instanceId)
        {
            instanceId = 0;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out instanceId);
        }

        internal static bool TryBool(JToken args, string name, out bool value)
        {
            value = false;
            var token = args[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                return false;
            }

            value = token.Value<bool>();
            return true;
        }

        internal static bool TryInt(JToken args, string name, out int value)
        {
            value = 0;
            var token = args[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                return false;
            }

            value = token.Value<int>();
            return true;
        }

        internal static bool OptionalBool(JToken args, string name, bool fallback)
        {
            return TryBool(args, name, out var value) ? value : fallback;
        }

        internal static float OptionalFloat(JToken args, string name, float fallback)
        {
            var token = args[name];
            return token == null || token.Type == JTokenType.Null ? fallback : token.Value<float>();
        }

        internal static float PositiveFloat(JToken token, string fieldName, float min, float max)
        {
            return FloatRange(token, fieldName, min, max);
        }

        internal static float FloatRange(JToken token, string fieldName, float min, float max)
        {
            var value = token.Value<float>();
            if (value < min || value > max)
            {
                throw new ArgumentOutOfRangeException(fieldName, $"{fieldName} must be between {min} and {max}.");
            }

            return value;
        }

        internal static int IntRange(JToken token, string fieldName, int min, int max)
        {
            var value = token.Value<int>();
            if (value < min || value > max)
            {
                throw new ArgumentOutOfRangeException(fieldName, $"{fieldName} must be between {min} and {max}.");
            }

            return value;
        }

        internal static Vector3 ReadVector3(JToken? token, Vector3 fallback)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return fallback;
            }

            if (token is not JObject obj)
            {
                throw new ArgumentException("Vector fields must be objects with x/y/z numbers.");
            }

            return new Vector3(
                obj["x"]?.Value<float>() ?? fallback.x,
                obj["y"]?.Value<float>() ?? fallback.y,
                obj["z"]?.Value<float>() ?? fallback.z);
        }

        internal static T ParseEnum<T>(string value, string fieldName)
            where T : struct
        {
            if (Enum.TryParse<T>(value, ignoreCase: true, out var parsed))
            {
                return parsed;
            }

            throw new ArgumentException($"{fieldName} value '{value}' is not a valid {typeof(T).Name}.");
        }

        internal static double Round(float value)
        {
            return Math.Round(value, 4);
        }
#endif
    }
}
