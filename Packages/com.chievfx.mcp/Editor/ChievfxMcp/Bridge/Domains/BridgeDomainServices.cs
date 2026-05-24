#nullable enable
using System.Globalization;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Chievfx.Mcp.Editor
{
    internal abstract class BridgeDomainServiceBase
    {
        protected static BridgeRuntimeState RuntimeState => ChievfxMcpBridgeHost.RuntimeState;

        protected static BridgeEventJournal EventJournal => ChievfxMcpBridgeHost.EventJournal;

        protected static BridgeOperationStore OperationStore => ChievfxMcpBridgeHost.OperationStore;

        protected static BridgeFileTransport Transport => ChievfxMcpBridgeHost.Transport;

        protected static JToken ReadObject(JToken? element, string name) => McpArgumentReader.ReadObject(element, name);

        protected static JToken ReadArray(JToken? element, string name) => McpArgumentReader.ReadArray(element, name);

        protected static JToken? ReadProperty(JToken? element, string name) => McpArgumentReader.ReadProperty(element, name);

        protected static bool HasProperty(JToken? element, string name) => McpArgumentReader.HasProperty(element, name);

        protected static string? ReadString(JToken? element, string name) => McpArgumentReader.ReadString(element, name);

        protected static int ReadInt(JToken? element, string name, int defaultValue) => McpArgumentReader.ReadInt(element, name, defaultValue);

        protected static int? ReadNullableInt(JToken? element, string name) => McpArgumentReader.ReadNullableInt(element, name);

        protected static bool ReadBool(JToken? element, string name, bool defaultValue) => McpArgumentReader.ReadBool(element, name, defaultValue);

        protected static TEnum ReadEnum<TEnum>(JToken element, string name, TEnum defaultValue)
            where TEnum : struct => McpArgumentReader.ReadEnum(element, name, defaultValue);

        protected static int ClampInt(int value, int min, int max) => McpArgumentReader.ClampInt(value, min, max);

        protected static string TrimText(string text, int maxChars, ref bool truncated) => McpArgumentReader.TrimText(text, maxChars, ref truncated);

        protected static int GetLegacyInstanceId(UnityEngine.Object? unityObject) => UnityObjectIdentity.GetLegacyInstanceId(unityObject);

        protected static string GetEntityIdText(UnityEngine.Object unityObject) => UnityObjectIdentity.GetEntityIdText(unityObject);
    }

    internal static class UnityObjectIdentity
    {
        private static readonly MethodInfo? GetEntityIdMethod = typeof(UnityEngine.Object).GetMethod(
            "GetEntityId",
            BindingFlags.Instance | BindingFlags.Public);

        private static readonly MethodInfo? GetInstanceIdMethod = typeof(UnityEngine.Object).GetMethod(
            "GetInstanceID",
            BindingFlags.Instance | BindingFlags.Public);

        private static readonly MethodInfo? EntityIdToObjectMethod = typeof(EditorUtility).GetMethod(
            "EntityIdToObject",
            BindingFlags.Static | BindingFlags.Public);

        private static readonly MethodInfo? EntityIdFromULongMethod = EntityIdToObjectMethod?
            .GetParameters()[0]
            .ParameterType
            .GetMethod("FromULong", BindingFlags.Static | BindingFlags.Public);

        public static int GetLegacyInstanceId(UnityEngine.Object? unityObject)
        {
            if (unityObject == null)
            {
                return 0;
            }

            var entityIdText = GetEntityIdText(unityObject);
            return int.TryParse(entityIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var instanceId)
                ? instanceId
                : entityIdText.GetHashCode();
        }

        public static string GetEntityIdText(UnityEngine.Object unityObject)
        {
            return GetEntityIdMethod?.Invoke(unityObject, null)?.ToString()
                ?? (GetInstanceIdMethod?.Invoke(unityObject, null) as int?)?.ToString(CultureInfo.InvariantCulture)
                ?? "0";
        }

        public static UnityEngine.Object? LegacyInstanceIdToObject(int instanceId)
        {
            if (instanceId == 0)
            {
                return null;
            }

            if (EntityIdToObjectMethod != null && EntityIdFromULongMethod != null)
            {
                var entityId = EntityIdFromULongMethod.Invoke(null, new object[] { unchecked((ulong)instanceId) });
                return entityId == null ? null : EntityIdToObjectMethod.Invoke(null, new[] { entityId }) as UnityEngine.Object;
            }

#pragma warning disable CS0618
            return EditorUtility.InstanceIDToObject(instanceId);
#pragma warning restore CS0618
        }
    }
}
