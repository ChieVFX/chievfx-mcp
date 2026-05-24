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
    internal sealed partial class ScriptAsyncOperationService : BridgeDomainServiceBase
    {
        private readonly List<PendingScriptInvocationRequest> pendingRequests = new();
            private readonly object syncRoot = new();
            private Thread? activeThread;
            private DateTime activeStartedUtc;
            private string? activeMethod;

            public bool IsBusy()
            {
                lock (syncRoot)
                {
                    return activeThread != null && activeThread.IsAlive
                        || pendingRequests.Any(pending => !pending.ResponseWritten || (pending.InvocationStarted && !pending.InvocationCompleted));
                }
            }

        public bool IsScriptTool(string toolName)
        {
            return string.Equals(toolName, "script-execute", StringComparison.Ordinal);
        }

        public void StartScriptToolRequest(string id, JToken args)
        {
            var pending = new PendingScriptInvocationRequest
            {
                Id = id,
                Args = args.DeepClone(),
                StartedUtc = DateTime.UtcNow
            };
            var worker = new Thread(() => RunScriptToolRequest(pending))
            {
                IsBackground = true,
                Name = "ChievFX MCP Script Coordinator"
            };

            lock (syncRoot)
            {
                if (IsScriptExecutionActiveLocked())
                {
                    var activePending = pendingRequests.FirstOrDefault(candidate =>
                        !candidate.ResponseWritten || (candidate.InvocationStarted && !candidate.InvocationCompleted));
                    var activeSinceUtc = activePending != null && activePending.InvocationQueuedUtc != default
                        ? activePending.InvocationQueuedUtc
                        : activeStartedUtc;
                    var runningMs = (int)Math.Round((DateTime.UtcNow - activeSinceUtc).TotalMilliseconds);
                    var activeMethodLabel = activePending?.MethodLabel ?? activeMethod;
                    throw new InvalidOperationException(
                        $"A previous script-execute invocation ({activeMethodLabel}) is still running or queued after {runningMs} ms. Wait for it to finish or restart Unity before running another script.");
                }

                pending.WorkerThread = worker;
                pendingRequests.Add(pending);
                activeThread = worker;
                activeStartedUtc = pending.StartedUtc;
                activeMethod = "script-execute";
            }

            try
            {
                worker.Priority = System.Threading.ThreadPriority.BelowNormal;
                worker.Start();
                OperationStore.MarkWaiting(id, "Compiling script; user method will run on Unity main thread.", true);
            }
            catch
            {
                lock (syncRoot)
                {
                    pendingRequests.Remove(pending);
                    if (ReferenceEquals(activeThread, worker))
                    {
                        activeThread = null;
                        activeMethod = null;
                        activeStartedUtc = default;
                    }
                }

                throw;
            }
        }

        private bool IsScriptExecutionActiveLocked()
        {
            return activeThread != null && activeThread.IsAlive
                || pendingRequests.Any(pending => !pending.ResponseWritten || (pending.InvocationStarted && !pending.InvocationCompleted));
        }

        private void RunScriptToolRequest(PendingScriptInvocationRequest pending)
        {
            try
            {
                var result = ExecuteScript(pending.Args, pending);
                Transport.WriteResponse(pending.Id, new
                {
                    ok = true,
                    contentType = "json",
                    result
                });
                OperationStore.Complete(pending.Id, "completed", "script-execute completed.");
            }
            catch (Exception ex)
            {
                var message = ex.GetBaseException().Message;
                OperationStore.Complete(pending.Id, "failed", message);
                Transport.WriteResponse(pending.Id, new { ok = false, error = message });
            }
            finally
            {
                lock (syncRoot)
                {
                    pending.ResponseWritten = true;
                    if (ReferenceEquals(activeThread, Thread.CurrentThread))
                    {
                        activeThread = null;
                    }

                    CleanupCompletedScriptRequestsLocked();
                }
            }
        }

        public void ProcessPendingScriptInvocationRequests()
        {
            PendingScriptInvocationRequest? pending = null;
            lock (syncRoot)
            {
                CleanupCompletedScriptRequestsLocked();
                pending = pendingRequests.FirstOrDefault(candidate =>
                    candidate.Method != null
                    && candidate.Values != null
                    && !candidate.InvocationStarted
                    && !candidate.TimedOut
                    && !candidate.ResponseWritten);
                if (pending != null)
                {
                    pending.InvocationStarted = true;
                    activeMethod = pending.MethodLabel;
                }
            }

            if (pending == null)
            {
                return;
            }

            try
            {
                pending.Value = pending.Method!.Invoke(null, pending.Values);
                var truncated = false;
                pending.ReturnValueType = pending.Value?.GetType().FullName ?? pending.Method!.ReturnType.FullName ?? "System.Void";
                pending.SerializedReturnValue = ReflectionBridgeService.SerializeReturnValue(pending.Value, ref truncated);
                pending.ReturnValueTruncated = truncated;
            }
            catch (TargetInvocationException ex)
            {
                pending.Exception = ex.InnerException ?? ex;
            }
            catch (Exception ex)
            {
                pending.Exception = ex;
            }
            finally
            {
                lock (syncRoot)
                {
                    pending.InvocationCompleted = true;
                    pending.Completion.Set();
                    CleanupCompletedScriptRequestsLocked();
                }
            }
        }

        private void CleanupCompletedScriptRequestsLocked()
        {
            pendingRequests.RemoveAll(pending =>
                pending.ResponseWritten && (!pending.InvocationStarted || pending.InvocationCompleted));
            if ((activeThread == null || !activeThread.IsAlive)
                && pendingRequests.Count == 0)
            {
                activeMethod = null;
                activeStartedUtc = default;
            }
        }

        private object ExecuteScript(JToken args, PendingScriptInvocationRequest pending)
        {
            var startedUtc = DateTime.UtcNow;
            var logStartIndex = ConsoleLogBridgeService.GetLogEntryCount();
            var includeLogs = ReadBool(args, "includeLogs", true);
            var logType = ReadString(args, "logType");
            var timeoutMs = ClampInt(ReadInt(args, "timeoutMs", DefaultScriptTimeoutMs), 100, HardScriptTimeoutMs);
            var csharpCode = ReadString(args, "csharpCode");
            if (string.IsNullOrWhiteSpace(csharpCode))
            {
                throw new ArgumentException("script-execute requires non-empty csharpCode.", nameof(args));
            }

            if (csharpCode!.Length > MaxScriptCodeChars)
            {
                throw new ArgumentException($"script-execute csharpCode is too large. Maximum is {MaxScriptCodeChars} characters.", nameof(args));
            }

            var className = ReadString(args, "className");
            var methodName = ReadString(args, "methodName");
            var parameters = ReadScriptParameters(args);
            try
            {
                var assembly = CompileScriptAssembly(csharpCode, out var diagnostics);
                if (assembly == null)
                {
                    return CreateScriptExecutionFailure(
                        "compile",
                        "Script compilation failed.",
                        diagnostics,
                        logStartIndex,
                        includeLogs,
                        logType,
                        startedUtc,
                        timeoutMs,
                        null);
                }

                var method = ResolveScriptMethod(assembly, className, methodName, parameters is JArray paramsArray ? paramsArray.Count : 0);
                ValidateScriptMethod(method);
                var methodParameters = method.GetParameters();
                var values = new object?[methodParameters.Length];
                for (var i = 0; i < methodParameters.Length; i++)
                {
                    values[i] = ReflectionBridgeService.ConvertJsonValue(parameters, i, methodParameters[i].ParameterType);
                }

                try
                {
                    var invocation = ExecuteScriptMethodWithTimeout(pending, method, values, timeoutMs);
                    if (invocation.TimedOut)
                    {
                        return CreateScriptExecutionFailure(
                            "timeout",
                            $"script-execute timed out after {timeoutMs} ms. Caller code is queued or still running on the Unity main thread; further script-execute calls are blocked until it returns or Unity restarts.",
                            diagnostics,
                            logStartIndex,
                            includeLogs,
                            logType,
                            startedUtc,
                            timeoutMs,
                            null,
                            stillRunning: true);
                    }

                    if (invocation.Exception != null)
                    {
                        return CreateScriptExecutionFailure(
                            "runtime",
                            invocation.Exception.GetBaseException().Message,
                            diagnostics,
                            logStartIndex,
                            includeLogs,
                            logType,
                            startedUtc,
                            timeoutMs,
                            invocation.Exception);
                    }

                    return CreateScriptExecutionSuccess(
                        method,
                        invocation,
                        diagnostics,
                        logStartIndex,
                        includeLogs,
                        logType,
                        startedUtc,
                        timeoutMs);
                }
                catch (TargetInvocationException ex)
                {
                    return CreateScriptExecutionFailure(
                        "runtime",
                        ex.InnerException?.GetBaseException().Message ?? ex.GetBaseException().Message,
                        diagnostics,
                        logStartIndex,
                        includeLogs,
                        logType,
                        startedUtc,
                        timeoutMs,
                        ex.InnerException ?? ex);
                }
            }
            catch (Exception ex) when (!(ex is ArgumentException))
            {
                return CreateScriptExecutionFailure(
                    "runtime",
                    ex.GetBaseException().Message,
                    Array.Empty<Dictionary<string, object?>>(),
                    logStartIndex,
                    includeLogs,
                    logType,
                    startedUtc,
                    timeoutMs,
                    ex);
            }
        }

        private ScriptInvocationResult ExecuteScriptMethodWithTimeout(PendingScriptInvocationRequest pending, MethodInfo method, object?[] values, int timeoutMs)
        {
            var methodLabel = $"{method.DeclaringType?.FullName}.{method.Name}";
            lock (syncRoot)
            {
                pending.Method = method;
                pending.MethodLabel = methodLabel;
                pending.Values = values;
                pending.InvocationQueuedUtc = DateTime.UtcNow;
                activeMethod = methodLabel;
            }

            var deadlineUtc = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                var remainingMs = (int)Math.Ceiling((deadlineUtc - DateTime.UtcNow).TotalMilliseconds);
                if (remainingMs <= 0)
                {
                    lock (syncRoot)
                    {
                        pending.TimedOut = true;
                    }

                    return new ScriptInvocationResult { TimedOut = true };
                }

                if (pending.Completion.Wait(Math.Min(remainingMs, 100)))
                {
                    lock (syncRoot)
                    {
                        return new ScriptInvocationResult
                        {
                            Value = pending.Value,
                            Exception = pending.Exception,
                            ReturnValueType = pending.ReturnValueType,
                            SerializedReturnValue = pending.SerializedReturnValue,
                            ReturnValueTruncated = pending.ReturnValueTruncated
                        };
                    }
                }
            }
        }

        private JToken ReadScriptParameters(JToken args)
        {
            var parameters = ReadArray(args, "parameters");
            if (parameters is JArray parametersArray && parametersArray.Count > 0)
            {
                return parameters;
            }

            return ReadArray(args, "inputParameters");
        }

        private Assembly? CompileScriptAssembly(string csharpCode, out Dictionary<string, object?>[] diagnostics)
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

            var syntaxTree = ReflectionBridgeService.InvokeWithOptionalParameters(FindParseTextMethod(csharpSyntaxTreeType), null, csharpCode)
                ?? throw new InvalidOperationException("Roslyn returned no syntax tree.");
            var syntaxTrees = Array.CreateInstance(syntaxTreeType, 1);
            syntaxTrees.SetValue(syntaxTree, 0);

            var references = CreateMetadataReferences(metadataReferenceType);
            var outputKind = Enum.Parse(outputKindType, "DynamicallyLinkedLibrary");
            var optionsConstructor = compilationOptionsType.GetConstructors()
                .FirstOrDefault(ctor =>
                {
                    var parameters = ctor.GetParameters();
                    return parameters.Length > 0 && parameters[0].ParameterType == outputKindType;
                })
                ?? throw new NotSupportedException("Roslyn CSharpCompilationOptions constructor is unavailable.");
            var options = ReflectionBridgeService.InvokeWithOptionalParameters(optionsConstructor, null, outputKind);
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
            diagnostics = CreateDiagnosticDtos(emitResult);
            var success = Convert.ToBoolean(ReflectionBridgeService.ReadReflectedProperty(emitResult, "Success"), CultureInfo.InvariantCulture);
            return success ? Assembly.Load(stream.ToArray()) : null;
        }

        private Assembly LoadRequiredAssembly(string assemblyName)
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(candidate => string.Equals(candidate.GetName().Name, assemblyName, StringComparison.Ordinal));
            if (assembly != null)
            {
                return assembly;
            }

            try
            {
                return Assembly.Load(assemblyName);
            }
            catch (Exception ex)
            {
                throw new NotSupportedException(
                    $"Roslyn assembly '{assemblyName}' is unavailable in this Unity editor. script-execute requires Microsoft.CodeAnalysis assemblies.",
                    ex);
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
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string location;
                try
                {
                    if (assembly.IsDynamic || string.IsNullOrWhiteSpace(assembly.Location))
                    {
                        continue;
                    }

                    location = assembly.Location;
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

        private Dictionary<string, object?>[] CreateDiagnosticDtos(object emitResult)
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

                var message = ReflectionBridgeService.InvokeReflectedStringMethod(diagnostic, "GetMessage") ?? diagnostic.ToString() ?? string.Empty;
                var location = ReflectionBridgeService.ReadReflectedProperty(diagnostic, "Location");
                results.Add(new Dictionary<string, object?>
                {
                    ["id"] = ReflectionBridgeService.ReadReflectedStringProperty(diagnostic, "Id"),
                    ["severity"] = Convert.ToString(ReflectionBridgeService.ReadReflectedProperty(diagnostic, "Severity"), CultureInfo.InvariantCulture),
                    ["message"] = message,
                    ["location"] = location?.ToString()
                });

                if (results.Count >= MaxScriptDiagnostics)
                {
                    break;
                }
            }

            return results.ToArray();
        }

        private MethodInfo ResolveScriptMethod(Assembly assembly, string? className, string? methodName, int suppliedParameterCount)
        {
            var resolvedMethodName = string.IsNullOrWhiteSpace(methodName) ? BridgeRuntimeState.DefaultScriptMethodName : methodName!.Trim();
            var targetType = ResolveScriptType(assembly, className, resolvedMethodName);
            var methods = targetType
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Where(method => string.Equals(method.Name, resolvedMethodName, StringComparison.Ordinal))
                .ToArray();
            if (methods.Length == 0)
            {
                throw new InvalidOperationException($"Static method '{resolvedMethodName}' was not found on script class '{targetType.FullName}'.");
            }

            var methodWithMatchingParameters = methods.FirstOrDefault(method => method.GetParameters().Length == suppliedParameterCount);
            if (methodWithMatchingParameters != null)
            {
                return methodWithMatchingParameters;
            }

            throw new InvalidOperationException(
                $"Static method '{targetType.FullName}.{resolvedMethodName}' has no overload with {suppliedParameterCount} supplied parameters.");
        }

        private Type ResolveScriptType(Assembly assembly, string? className, string methodName)
        {
            if (!string.IsNullOrWhiteSpace(className))
            {
                var explicitType = FindScriptType(assembly, className!);
                if (explicitType == null)
                {
                    throw new InvalidOperationException($"Script class '{className}' was not found.");
                }

                return explicitType;
            }

            var defaultType = FindScriptType(assembly, BridgeRuntimeState.DefaultScriptClassName);
            if (defaultType != null)
            {
                return defaultType;
            }

            var candidates = assembly
                .GetTypes()
                .Where(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .Any(method => string.Equals(method.Name, methodName, StringComparison.Ordinal)))
                .ToArray();
            return candidates.Length switch
            {
                1 => candidates[0],
                0 => throw new InvalidOperationException($"No script class defines static method '{methodName}'. Provide className explicitly."),
                _ => throw new InvalidOperationException($"Multiple script classes define static method '{methodName}'. Provide className explicitly.")
            };
        }

        private Type? FindScriptType(Assembly assembly, string className)
        {
            return assembly.GetType(className, false)
                ?? assembly.GetTypes().FirstOrDefault(type =>
                    string.Equals(type.FullName, className, StringComparison.Ordinal)
                    || string.Equals(type.Name, className, StringComparison.Ordinal));
        }

        private void ValidateScriptMethod(MethodInfo method)
        {
            if (!method.IsStatic)
            {
                throw new NotSupportedException("script-execute requires a static method.");
            }

            if (method.ContainsGenericParameters || method.IsGenericMethodDefinition)
            {
                throw new NotSupportedException("script-execute does not support generic methods.");
            }

            foreach (var parameter in method.GetParameters())
            {
                var parameterType = parameter.ParameterType;
                if (parameter.IsOut || parameterType.IsByRef)
                {
                    throw new NotSupportedException("script-execute does not support ref/out parameters.");
                }

                if (ReflectionBridgeService.ContainsPointerType(parameterType))
                {
                    throw new NotSupportedException("script-execute does not support unsafe pointer parameters.");
                }
            }

            if (ReflectionBridgeService.ContainsPointerType(method.ReturnType))
            {
                throw new NotSupportedException("script-execute does not support unsafe pointer return values.");
            }
        }

        private object CreateScriptExecutionSuccess(
            MethodInfo method,
            ScriptInvocationResult invocation,
            Dictionary<string, object?>[] diagnostics,
            int logStartIndex,
            bool includeLogs,
            string? logType,
            DateTime startedUtc,
            int timeoutMs)
        {
            return new
            {
                success = true,
                stage = "completed",
                method = $"{method.DeclaringType?.FullName}.{method.Name}",
                result = new
                {
                    type = invocation.ReturnValueType ?? method.ReturnType.FullName ?? "System.Void",
                    value = invocation.SerializedReturnValue,
                    truncated = invocation.ReturnValueTruncated
                },
                diagnostics,
                logs = ConsoleLogBridgeService.CreateLogOutputs(logStartIndex, includeLogs, logType, includeStackTrace: false, DefaultScriptLogEntries),
                durationMs = (int)Math.Round((DateTime.UtcNow - startedUtc).TotalMilliseconds),
                timeoutMs
            };
        }

        private object CreateScriptExecutionFailure(
            string stage,
            string message,
            Dictionary<string, object?>[] diagnostics,
            int logStartIndex,
            bool includeLogs,
            string? logType,
            DateTime startedUtc,
            int timeoutMs,
            Exception? exception,
            bool stillRunning = false)
        {
            return new
            {
                success = false,
                stage,
                error = new
                {
                    message,
                    type = exception?.GetType().FullName,
                    stack = exception == null ? null : TrimExceptionStack(exception)
                },
                diagnostics,
                logs = ConsoleLogBridgeService.CreateLogOutputs(logStartIndex, includeLogs, logType, includeStackTrace: false, DefaultScriptLogEntries),
                durationMs = (int)Math.Round((DateTime.UtcNow - startedUtc).TotalMilliseconds),
                timeoutMs,
                stillRunning
            };
        }

        private string? TrimExceptionStack(Exception exception)
        {
            if (string.IsNullOrEmpty(exception.StackTrace))
            {
                return null;
            }

            var truncated = false;
            return TrimText(exception.StackTrace, MaxStackTraceChars, ref truncated);
        }

    }
}
