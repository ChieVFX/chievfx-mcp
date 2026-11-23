#nullable enable
using static Chievfx.Mcp.Editor.McpLimits;
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
    internal sealed partial class ScriptAsyncOperationService
    {
        private Assembly LoadRequiredAssembly(string assemblyName)
        {
            var assembly = UnityAssemblies.GetLoaded()
                .FirstOrDefault(candidate => string.Equals(candidate.GetName().Name, assemblyName, StringComparison.Ordinal));
            if (assembly != null)
            {
                return assembly;
            }

            try
            {
                return Assembly.Load(assemblyName);
            }
            catch (Exception loadByNameException)
            {
                // Some editors (e.g. Unity 2022.3) bundle their own
                // Microsoft.CodeAnalysis / System.Collections.Immutable copies
                // under MonoBleedingEdge. Because the assembly *names* collide,
                // Unity silently drops the package's precompiledReferences, so
                // our DLLs never enter the editor AppDomain and load-by-name
                // fails. Fall back to the exact copies we ship in the package.
                var shipped = TryLoadShippedRoslynAssembly(assemblyName);
                if (shipped != null)
                {
                    return shipped;
                }

                throw new NotSupportedException(
                    $"Roslyn assembly '{assemblyName}' is unavailable in this Unity editor. script-execute requires Microsoft.CodeAnalysis assemblies.",
                    loadByNameException);
            }
        }

        private Assembly? TryLoadShippedRoslynAssembly(string assemblyName)
        {
            var pathsByName = GetShippedRoslynAssemblyPaths();
            if (pathsByName.Count == 0)
            {
                return null;
            }

            EnsureShippedRoslynResolverInstalled();

            // Pre-load the whole shipped set so Roslyn's transitive references
            // (System.Collections.Immutable, System.Reflection.Metadata, ...)
            // bind to our internally-consistent versions instead of the
            // editor's bundled, possibly mismatched, copies. The first loaded
            // copy of a given version wins, so loading ours up front matters.
            foreach (var path in pathsByName.Values)
            {
                TryLoadAssemblyFrom(path);
            }

            return UnityAssemblies.GetLoaded()
                .FirstOrDefault(candidate => string.Equals(candidate.GetName().Name, assemblyName, StringComparison.Ordinal));
        }

        private static void EnsureShippedRoslynResolverInstalled()
        {
            lock (roslynLoadLock)
            {
                if (shippedRoslynResolverInstalled)
                {
                    return;
                }

                AppDomain.CurrentDomain.AssemblyResolve += ResolveShippedRoslynAssembly;
                shippedRoslynResolverInstalled = true;
            }
        }

        private static Assembly? ResolveShippedRoslynAssembly(object? sender, ResolveEventArgs args)
        {
            var pathsByName = shippedRoslynAssemblyPaths;
            if (pathsByName == null)
            {
                return null;
            }

            string simpleName;
            try
            {
                simpleName = new AssemblyName(args.Name).Name;
            }
            catch
            {
                return null;
            }

            var loaded = UnityAssemblies.GetLoaded()
                .FirstOrDefault(candidate => string.Equals(candidate.GetName().Name, simpleName, StringComparison.Ordinal));
            if (loaded != null)
            {
                return loaded;
            }

            return pathsByName.TryGetValue(simpleName, out var path) ? TryLoadAssemblyFrom(path) : null;
        }

        private static Assembly? TryLoadAssemblyFrom(string path)
        {
            try
            {
                return UnityAssemblies.LoadFromPath(path);
            }
            catch
            {
                return null;
            }
        }

        private static Dictionary<string, string> GetShippedRoslynAssemblyPaths()
        {
            lock (roslynLoadLock)
            {
                if (shippedRoslynAssemblyPaths != null)
                {
                    return shippedRoslynAssemblyPaths;
                }

                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var directory in EnumerateShippedRoslynDirectories())
                {
                    if (!Directory.Exists(directory))
                    {
                        continue;
                    }

                    string[] dlls;
                    try
                    {
                        dlls = Directory.GetFiles(directory, "*.dll", SearchOption.AllDirectories);
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (var dll in dlls)
                    {
                        var name = Path.GetFileNameWithoutExtension(dll);
                        if (!string.IsNullOrEmpty(name) && !map.ContainsKey(name))
                        {
                            map[name] = dll;
                        }
                    }
                }

                shippedRoslynAssemblyPaths = map;
                return map;
            }
        }

        private static IEnumerable<string> EnumerateShippedRoslynDirectories()
        {
            // Runs on a background worker thread, so resolve the package on disk
            // without the main-thread-only AssetDatabase / PackageManager APIs.
            // The current directory is the Unity project root.
            var relative = Path.Combine("Editor", "ThirdParty", "Roslyn");
            var projectRoot = Directory.GetCurrentDirectory();
            var searchRoots = new[]
            {
                Path.Combine(projectRoot, "Packages"),
                Path.Combine(projectRoot, "Library", "PackageCache"),
            };

            foreach (var searchRoot in searchRoots)
            {
                if (!Directory.Exists(searchRoot))
                {
                    continue;
                }

                string[] packageDirectories;
                try
                {
                    packageDirectories = Directory.GetDirectories(searchRoot, "com.chievfx.mcp*", SearchOption.TopDirectoryOnly);
                }
                catch
                {
                    continue;
                }

                foreach (var packageDirectory in packageDirectories)
                {
                    yield return Path.Combine(packageDirectory, relative);
                }
            }
        }

        private MethodInfo FindParseTextMethod(Type csharpSyntaxTreeType)
        {
            return csharpSyntaxTreeType
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => string.Equals(method.Name, "ParseText", StringComparison.Ordinal))
                .Where(method =>
                {
                    var parameters = method.GetParameters();
                    return parameters.Length > 0 && parameters[0].ParameterType == typeof(string);
                })
                .OrderBy(method => method.GetParameters().Length)
                .FirstOrDefault()
                ?? throw new NotSupportedException("Roslyn CSharpSyntaxTree.ParseText(string) is unavailable.");
        }

        private MethodInfo FindCompilationCreateMethod(Type compilationType)
        {
            return compilationType
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => string.Equals(method.Name, "Create", StringComparison.Ordinal))
                .Where(method =>
                {
                    var parameters = method.GetParameters();
                    return parameters.Length >= 4 && parameters[0].ParameterType == typeof(string);
                })
                .OrderBy(method => method.GetParameters().Length)
                .FirstOrDefault()
                ?? throw new NotSupportedException("Roslyn CSharpCompilation.Create is unavailable.");
        }

        private MethodInfo FindEmitMethod(Type compilationType)
        {
            return compilationType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(method => string.Equals(method.Name, "Emit", StringComparison.Ordinal))
                .Where(method =>
                {
                    var parameters = method.GetParameters();
                    return parameters.Length > 0 && parameters[0].ParameterType == typeof(Stream);
                })
                .OrderBy(method => method.GetParameters().Length)
                .FirstOrDefault()
                ?? throw new NotSupportedException("Roslyn Compilation.Emit(Stream) is unavailable.");
        }

        // Errors a missing using or an inaccessible member produces. Scripts probe project code, where most of what
        // a caller needs is internal or private, and forgetting System.Linq is the commonest compile error there is.
        private static readonly HashSet<string> MissingNameErrorIds = new(StringComparer.Ordinal)
        {
            "CS0103", "CS0117", "CS0122", "CS0234", "CS0246", "CS0271", "CS0272", "CS0426", "CS1061",
        };

        private static readonly string[] DefaultScriptUsings =
        {
            "System", "System.Collections.Generic", "System.IO", "System.Linq", "UnityEngine", "UnityEditor",
        };

        // Names both System and UnityEngine define: once the retry has imported the one the code lacked, a bare
        // Object or Random would be ambiguous (CS0104), so the retry pins it to the namespace the code already chose.
        private static readonly string[] SystemUnityEngineSharedNames = { "Object", "Random" };

        // The script-execute description names it without its namespace.
        private const string ScriptStateAliasDirective = "using ChievfxMcpScriptState = Chievfx.Mcp.Editor.ChievfxMcpScriptState;";

        private static readonly Regex ShortScriptStateUsingStatic = new(@"(?<![\w.])using\s+static\s+ChievfxMcpScriptState\s*;", RegexOptions.Compiled);

        // A bare OnCleanup(...) call, written as if it were a global function, gets the class's members imported.
        private const string ScriptStateStaticDirective = "using static Chievfx.Mcp.Editor.ChievfxMcpScriptState;";
    }
}
