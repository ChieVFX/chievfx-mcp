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
        private Assembly? CompileScriptAssembly(string csharpCode, bool allowNonPublic, int lineOffset, out Dictionary<string, object?>[] diagnostics)
        {
            var assemblyName = "ChievfxMcpScript_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            var codeAnalysisAssembly = LoadRequiredAssembly(BridgeRuntimeState.RoslynAssemblyName);
            var csharpAssembly = LoadRequiredAssembly(BridgeRuntimeState.RoslynCSharpAssemblyName);
            var syntaxTreeType = codeAnalysisAssembly.GetType("Microsoft.CodeAnalysis.SyntaxTree")
                ?? throw new NotSupportedException("Roslyn SyntaxTree type is unavailable.");
            var metadataReferenceType = codeAnalysisAssembly.GetType("Microsoft.CodeAnalysis.MetadataReference")
                ?? throw new NotSupportedException("Roslyn MetadataReference type is unavailable.");
            var csharpSyntaxTreeType = csharpAssembly.GetType("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree")
                ?? throw new NotSupportedException("Roslyn CSharpSyntaxTree type is unavailable.");
            var compilationType = csharpAssembly.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilation")
                ?? throw new NotSupportedException("Roslyn CSharpCompilation type is unavailable.");
            var compilationOptionsType = csharpAssembly.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions")
                ?? throw new NotSupportedException("Roslyn CSharpCompilationOptions type is unavailable.");
            var outputKindType = codeAnalysisAssembly.GetType("Microsoft.CodeAnalysis.OutputKind")
                ?? throw new NotSupportedException("Roslyn OutputKind type is unavailable.");

            var parseText = FindParseTextMethod(csharpSyntaxTreeType);
            var syntaxTree = ReflectionBridgeService.InvokeWithOptionalParameters(parseText, null, csharpCode)
                ?? throw new InvalidOperationException("Roslyn returned no syntax tree.");
            var syntaxTrees = Array.CreateInstance(syntaxTreeType, allowNonPublic ? 2 : 1);
            syntaxTrees.SetValue(syntaxTree, 0);
            if (allowNonPublic)
            {
                // A tree of its own: in the caller's file an assembly attribute would have to sit after its usings.
                syntaxTrees.SetValue(
                    ReflectionBridgeService.InvokeWithOptionalParameters(parseText, null, SkipVerificationSource)
                    ?? throw new InvalidOperationException("Roslyn returned no syntax tree."),
                    1);
            }

            var references = CreateMetadataReferences(metadataReferenceType);
            var outputKind = Enum.Parse(outputKindType, "DynamicallyLinkedLibrary");
            var optionsConstructor = compilationOptionsType.GetConstructors()
                .FirstOrDefault(ctor =>
                {
                    var parameters = ctor.GetParameters();
                    return parameters.Length > 0 && parameters[0].ParameterType == outputKindType;
                })
                ?? throw new NotSupportedException("Roslyn CSharpCompilationOptions constructor is unavailable.");
            var options = ReflectionBridgeService.InvokeWithOptionalParameters(optionsConstructor, null, outputKind)
                ?? throw new InvalidOperationException("Roslyn returned no compilation options.");
            if (allowNonPublic)
            {
                options = AllowNonPublicAccess(options, compilationOptionsType, codeAnalysisAssembly, csharpAssembly);
            }

            var compilation = ReflectionBridgeService.InvokeWithOptionalParameters(
                    FindCompilationCreateMethod(compilationType),
                    null,
                    assemblyName,
                    syntaxTrees,
                    references,
                    options)
                ?? throw new InvalidOperationException("Roslyn returned no compilation.");

            using var stream = new MemoryStream();
            var emitResult = ReflectionBridgeService.InvokeWithOptionalParameters(FindEmitMethod(compilationType), compilation, stream)
                ?? throw new InvalidOperationException("Roslyn returned no emit result.");
            diagnostics = CreateDiagnosticDtos(emitResult, lineOffset);
            var success = Convert.ToBoolean(ReflectionBridgeService.ReadReflectedProperty(emitResult, "Success"), CultureInfo.InvariantCulture);
            return success ? UnityAssemblies.LoadFromBytes(stream.ToArray()) : null;
        }

        private static readonly object roslynLoadLock = new();
        private static Dictionary<string, string>? shippedRoslynAssemblyPaths;
        private static bool shippedRoslynResolverInstalled;

        private static bool ImportsNamespace(string csharpCode, string name)
        {
            return Regex.IsMatch(csharpCode, @"(?<![\w.])using\s+" + Regex.Escape(name) + @"\s*;");
        }

        private static bool DeclaresAlias(string csharpCode, string alias)
        {
            return Regex.IsMatch(csharpCode, @"(?<![\w.])using\s+" + Regex.Escape(alias) + @"\s*=");
        }

        private static bool MentionsName(string csharpCode, string name)
        {
            return Regex.IsMatch(csharpCode, @"(?<![\w.])" + Regex.Escape(name) + @"\b");
        }

        private static List<string> RetryDirectives(string csharpCode)
        {
            var directives = DefaultScriptUsings
                .Where(name => !ImportsNamespace(csharpCode, name))
                .Select(name => "using " + name + ";")
                .ToList();
            var importsSystem = ImportsNamespace(csharpCode, "System");
            var importsUnityEngine = ImportsNamespace(csharpCode, "UnityEngine");
            if (!(importsSystem && importsUnityEngine))
            {
                var owner = importsSystem ? "System" : "UnityEngine";
                directives.AddRange(SystemUnityEngineSharedNames
                    .Where(name => MentionsName(csharpCode, name) && !DeclaresAlias(csharpCode, name))
                    .Select(name => "using " + name + " = " + owner + "." + name + ";"));
            }

            if (MentionsName(csharpCode, "ChievfxMcpScriptState")
                && !ImportsNamespace(csharpCode, "Chievfx.Mcp.Editor")
                && !DeclaresAlias(csharpCode, "ChievfxMcpScriptState"))
            {
                directives.Add(ScriptStateAliasDirective);
            }

            if (Regex.IsMatch(csharpCode, @"(?<![\w.])OnCleanup\s*\(") && !ImportsNamespace(csharpCode, "static Chievfx.Mcp.Editor.ChievfxMcpScriptState"))
            {
                directives.Add(ScriptStateStaticDirective);
            }

            return directives;
        }

        private static bool HasMissingNameErrors(Dictionary<string, object?>[] diagnostics)
        {
            return diagnostics.Any(diagnostic =>
                string.Equals(diagnostic["severity"] as string, "Error", StringComparison.Ordinal)
                && diagnostic["id"] is string id
                && MissingNameErrorIds.Contains(id));
        }

        internal Assembly? CompileScript(
            string csharpCode,
            List<string> notes,
            out Dictionary<string, object?>[] diagnostics,
            out Dictionary<string, object?>[]? originalDiagnostics)
        {
            originalDiagnostics = null;
            // "using static ChievfxMcpScriptState;" as the description names the class: a using directive does not see an alias
            // the retry adds, so the short name would fail twice (CS0246). Same line on line one, line numbers stay.
            csharpCode = ShortScriptStateUsingStatic.Replace(csharpCode, "using static Chievfx.Mcp.Editor.ChievfxMcpScriptState;");
            var wrapped = WrapTopLevelStatements(csharpCode);
            // Bare statements are a supported form: the reply names TopLevelScript.Main as the method, a note on every call was noise.
            if (wrapped != null)
            {
                csharpCode = wrapped;
            }

            var assembly = CompileScriptAssembly(csharpCode, allowNonPublic: false, lineOffset: 0, out diagnostics);
            if (assembly == null && HasMissingNameErrors(diagnostics))
            {
                assembly = CompileWithProjectAccess(csharpCode, notes, ref diagnostics, out originalDiagnostics);
            }

            return assembly;
        }

        // A retry with internal/private members bound, then, if that is not enough, with the usings the code lacks
        // as well. It runs only after a failure, so code that already compiles never changes meaning (an added using
        // can make a name ambiguous), and the usings are only added when they turn out to be needed, so the note can
        // say which of the two the code was missing.
        private Assembly? CompileWithProjectAccess(
            string csharpCode,
            List<string> notes,
            ref Dictionary<string, object?>[] diagnostics,
            out Dictionary<string, object?>[]? originalDiagnostics)
        {
            originalDiagnostics = null;
            var addedDirectives = RetryDirectives(csharpCode);
            Dictionary<string, object?>[] retryDiagnostics;
            if (addedDirectives.Count > 0)
            {
                var accessOnly = CompileScriptAssembly(csharpCode, allowNonPublic: true, lineOffset: 0, out retryDiagnostics);
                if (accessOnly != null)
                {
                    diagnostics = retryDiagnostics;
                    notes.Add("Compiled with internal/private member access: non-public members of loaded assemblies are reachable directly.");
                    return accessOnly;
                }

                // The aliases name exactly what the code mentions (ChievfxMcpScriptState, Object/Random), while the
                // default namespaces bring in everything: with internal access System.IO adds Mono's internal ToArray
                // extension, and every LINQ ToArray() became ambiguous (CS0121). The aliases alone come first.
                var aliases = addedDirectives.Where(directive => directive.Contains(" = ") || directive.StartsWith("using static ", StringComparison.Ordinal)).ToList();
                if (aliases.Count > 0 && aliases.Count < addedDirectives.Count)
                {
                    var aliasPrefix = string.Concat(aliases.Select(directive => directive + "\n"));
                    var withAliases = CompileScriptAssembly(aliasPrefix + csharpCode, allowNonPublic: true, lineOffset: aliases.Count, out retryDiagnostics);
                    if (withAliases != null)
                    {
                        diagnostics = retryDiagnostics;
                        notes.Add("Compiled after adding '" + string.Join(" ", aliases) + "' and internal/private member access. Next time write the usings yourself; non-public members of loaded assemblies are reachable directly.");
                        return withAliases;
                    }
                }
            }

            var prefix = string.Concat(addedDirectives.Select(directive => directive + "\n"));
            var assembly = CompileScriptAssembly(prefix + csharpCode, allowNonPublic: true, lineOffset: addedDirectives.Count, out retryDiagnostics);
            const string systemIoDirective = "using System.IO;";
            if (assembly == null && addedDirectives.Contains(systemIoDirective) && retryDiagnostics.Any(IsSystemIoAmbiguity))
            {
                // Same clash when the code also lacks System.Linq: without System.IO the LINQ call is unambiguous again.
                var withoutIo = addedDirectives.Where(directive => directive != systemIoDirective).ToList();
                var withoutIoPrefix = string.Concat(withoutIo.Select(directive => directive + "\n"));
                var withoutIoAssembly = CompileScriptAssembly(withoutIoPrefix + csharpCode, allowNonPublic: true, lineOffset: withoutIo.Count, out var withoutIoDiagnostics);
                if (withoutIoAssembly != null || ErrorTexts(withoutIoDiagnostics).Count < ErrorTexts(retryDiagnostics).Count)
                {
                    addedDirectives = withoutIo;
                    assembly = withoutIoAssembly;
                    retryDiagnostics = withoutIoDiagnostics;
                }
            }

            var tried = addedDirectives.Count > 0
                ? "'" + string.Join(" ", addedDirectives) + "' and internal/private member access"
                : "internal/private member access";
            if (assembly != null)
            {
                diagnostics = retryDiagnostics;
                notes.Add(addedDirectives.Count > 0
                    ? "Compiled after adding " + tried + ". Next time write the usings yourself; non-public members of loaded assemblies are reachable directly."
                    : "Compiled with internal/private member access: non-public members of loaded assemblies are reachable directly.");
                return assembly;
            }

            var originalErrors = ErrorTexts(diagnostics);
            var retryErrors = ErrorTexts(retryDiagnostics);
            var resolved = originalErrors.Where(error => !retryErrors.Contains(error)).ToArray();
            if (resolved.Length > 0)
            {
                // The retry got further: the errors that still stop it are the ones to fix, while the originals it
                // cleared are what the caller's code lacked. Leading with the originals would bury the error that still blocks it.
                originalDiagnostics = diagnostics;
                diagnostics = retryDiagnostics;
                notes.Add("With " + tried + " your code got past " + string.Join("; ", resolved.Take(3))
                    + (resolved.Length > 3 ? $" (+{resolved.Length - 3} more)" : string.Empty)
                    + ", and failed on the errors below. originalDiagnostics are the errors of the code as sent.");
                return null;
            }

            // The retry cleared nothing, so the code as sent is the better report; an added using may still have
            // broken a name the code relied on, which is worth a mention.
            var newErrors = retryErrors.Where(error => !originalErrors.Contains(error)).Take(2).ToArray();
            notes.Add("Also tried " + tried + ": that did not compile either"
                + (newErrors.Length > 0 ? " (" + string.Join("; ", newErrors) + ")" : string.Empty)
                + ". Errors below are from your code as sent.");
            return null;
        }

        private static bool IsSystemIoAmbiguity(Dictionary<string, object?> diagnostic)
        {
            return diagnostic["id"] as string == "CS0121" && diagnostic["message"] is string message && message.Contains("System.IO.");
        }

        private static HashSet<string> ErrorTexts(IEnumerable<Dictionary<string, object?>> diagnostics)
        {
            return new HashSet<string>(
                diagnostics
                    .Where(diagnostic => string.Equals(diagnostic["severity"] as string, "Error", StringComparison.Ordinal))
                    .Select(diagnostic => diagnostic["id"] + " " + diagnostic["message"]),
                StringComparer.Ordinal);
        }

        // Roslyn binding internal/private members is half of it: Mono's JIT checks access again and throws
        // FieldAccessException/MethodAccessException, ignoring IgnoresAccessChecksTo. It skips those checks for an
        // assembly that requests SkipVerification, the permission unsafe code asks for.
        private const string SkipVerificationSource =
            "#pragma warning disable 618\n"
            + "[assembly: System.Security.Permissions.SecurityPermission(System.Security.Permissions.SecurityAction.RequestMinimum, SkipVerification = true)]\n";

        // IgnoreAccessibility and MetadataImportOptions.All are what the C# REPL uses to see private members; both
        // live behind Roslyn internals, so a Roslyn without them just keeps normal accessibility.
        private static object AllowNonPublicAccess(object options, Type compilationOptionsType, Assembly codeAnalysisAssembly, Assembly csharpAssembly)
        {
            try
            {
                var importOptionsType = codeAnalysisAssembly.GetType("Microsoft.CodeAnalysis.MetadataImportOptions");
                var withImportOptions = importOptionsType == null ? null : compilationOptionsType.GetMethod("WithMetadataImportOptions", new[] { importOptionsType });
                if (importOptionsType != null && withImportOptions != null)
                {
                    options = withImportOptions.Invoke(options, new[] { Enum.Parse(importOptionsType, "All") }) ?? options;
                }

                var binderFlagsType = csharpAssembly.GetType("Microsoft.CodeAnalysis.CSharp.BinderFlags");
                var withBinderFlags = binderFlagsType == null
                    ? null
                    : compilationOptionsType.GetMethod("WithTopLevelBinderFlags", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { binderFlagsType }, null);
                if (binderFlagsType != null && withBinderFlags != null)
                {
                    options = withBinderFlags.Invoke(options, new[] { Enum.Parse(binderFlagsType, "IgnoreAccessibility") }) ?? options;
                }
            }
            catch (Exception)
            {
                // Normal accessibility: the retry still adds the missing usings.
            }

            return options;
        }

        // GetMessage takes an optional IFormatProvider, so a parameterless lookup misses it and ToString() would
        // repeat the position ("(5,1): error CS0103: ...") counted with the retry's added lines.
        private static string ReadDiagnosticMessage(object diagnostic)
        {
            var getMessage = diagnostic.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(method => method.Name == "GetMessage" && method.GetParameters().Length <= 1);
            var message = getMessage == null ? null : ReflectionBridgeService.InvokeWithOptionalParameters(getMessage, diagnostic) as string;
            return message ?? diagnostic.ToString() ?? string.Empty;
        }

        // "line:column" in the caller's own code; lines of usings added by the retry are not counted.
        private static string? DescribeDiagnosticLocation(object? location, int lineOffset, (int Line, int Column, int Length)? topLevelHeader)
        {
            if (location == null)
            {
                return null;
            }

            try
            {
                var lineSpan = location.GetType().GetMethod("GetLineSpan", Type.EmptyTypes)?.Invoke(location, null);
                var start = lineSpan == null ? null : ReflectionBridgeService.ReadReflectedProperty(lineSpan, "StartLinePosition");
                if (start != null)
                {
                    var line = Convert.ToInt32(ReflectionBridgeService.ReadReflectedProperty(start, "Line"), CultureInfo.InvariantCulture) - lineOffset + 1;
                    var character = Convert.ToInt32(ReflectionBridgeService.ReadReflectedProperty(start, "Character"), CultureInfo.InvariantCulture);
                    if (topLevelHeader is { } header && line - 1 == header.Line && character >= header.Column + header.Length)
                    {
                        character -= header.Length;
                    }

                    var column = character + 1;
                    return line >= 1 ? line + ":" + column : "added using";
                }
            }
            catch (Exception)
            {
                // Fall through to Roslyn's own text.
            }

            return location.ToString();
        }

        private Array CreateMetadataReferences(Type metadataReferenceType)
        {
            var createFromFile = metadataReferenceType
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => string.Equals(method.Name, "CreateFromFile", StringComparison.Ordinal))
                .Where(method =>
                {
                    var parameters = method.GetParameters();
                    return parameters.Length > 0 && parameters[0].ParameterType == typeof(string);
                })
                .OrderBy(method => method.GetParameters().Length)
                .FirstOrDefault()
                ?? throw new NotSupportedException("Roslyn MetadataReference.CreateFromFile is unavailable.");

            var references = new List<object>();
            var seenLocations = new HashSet<string>(StringComparer.Ordinal);
            foreach (var assembly in UnityAssemblies.GetLoaded())
            {
                string location;
                try
                {
                    var assemblyLocation = assembly.IsDynamic ? null : UnityAssemblies.GetLocation(assembly);
                    if (string.IsNullOrWhiteSpace(assemblyLocation))
                    {
                        continue;
                    }

                    location = assemblyLocation!;
                }
                catch (NotSupportedException)
                {
                    continue;
                }

                if (!File.Exists(location) || !seenLocations.Add(location))
                {
                    continue;
                }

                references.Add(ReflectionBridgeService.InvokeWithOptionalParameters(createFromFile, null, location)
                    ?? throw new InvalidOperationException($"Roslyn could not reference '{location}'."));
            }

            var array = Array.CreateInstance(metadataReferenceType, references.Count);
            for (var i = 0; i < references.Count; i++)
            {
                array.SetValue(references[i], i);
            }

            return array;
        }

        private Dictionary<string, object?>[] CreateDiagnosticDtos(object emitResult, int lineOffset)
        {
            var diagnostics = ReflectionBridgeService.ReadReflectedProperty(emitResult, "Diagnostics") as IEnumerable;
            if (diagnostics == null)
            {
                return Array.Empty<Dictionary<string, object?>>();
            }

            var results = new List<Dictionary<string, object?>>();
            foreach (var diagnostic in diagnostics)
            {
                if (diagnostic == null)
                {
                    continue;
                }

                var severity = Convert.ToString(ReflectionBridgeService.ReadReflectedProperty(diagnostic, "Severity"), CultureInfo.InvariantCulture);
                // Hidden ones ("unnecessary using") are IDE fades, not something a caller acts on.
                if (string.Equals(severity, "Hidden", StringComparison.Ordinal))
                {
                    continue;
                }

                var message = ReadDiagnosticMessage(diagnostic);
                var location = ReflectionBridgeService.ReadReflectedProperty(diagnostic, "Location");
                results.Add(new Dictionary<string, object?>
                {
                    ["id"] = ReflectionBridgeService.ReadReflectedStringProperty(diagnostic, "Id"),
                    ["severity"] = severity,
                    ["message"] = message,
                    ["location"] = DescribeDiagnosticLocation(location, lineOffset, _topLevelHeader)
                });

                if (results.Count >= MaxScriptDiagnostics)
                {
                    break;
                }
            }

            return results.ToArray();
        }
    }
}
