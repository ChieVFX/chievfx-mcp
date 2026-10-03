#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;

namespace Chievfx.Mcp.Editor
{
    // Unity 6.4+ ships CoreCLR-ready assembly APIs and flags the System.Reflection equivalents
    // with UAC analyzer warnings; older editors only have the latter.
    internal static class UnityAssemblies
    {
        public static IReadOnlyList<Assembly> GetLoaded()
        {
#if UNITY_6000_4_OR_NEWER
            return UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies();
#else
            return AppDomain.CurrentDomain.GetAssemblies();
#endif
        }

        public static string? GetLocation(Assembly assembly)
        {
#if UNITY_6000_4_OR_NEWER
            return UnityEngine.AssemblyExtension.GetLoadedAssemblyPath(assembly);
#else
            return assembly.Location;
#endif
        }

        public static Assembly LoadFromBytes(byte[] rawAssembly)
        {
#if UNITY_6000_5_OR_NEWER
            return UnityEngine.Assemblies.CurrentAssemblies.LoadFromBytes(rawAssembly);
#else
            return Assembly.Load(rawAssembly);
#endif
        }

        public static Assembly LoadFromPath(string assemblyPath)
        {
#if UNITY_6000_5_OR_NEWER
            return UnityEngine.Assemblies.CurrentAssemblies.LoadFromPath(assemblyPath);
#else
            return Assembly.LoadFrom(assemblyPath);
#endif
        }
    }
}
