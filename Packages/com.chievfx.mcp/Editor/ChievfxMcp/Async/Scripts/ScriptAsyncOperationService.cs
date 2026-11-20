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
        // Async scripts whose reply already went out (timed out) but whose awaitable is still running: their end is logged.
        private readonly List<PendingScriptInvocationRequest> detachedAwaits = new();
        public const string AsyncEndLogPrefix = "[ChievFX MCP] script-execute async end ";
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
            FinishAwaitedScripts();
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

            var awaiting = false;
            try
            {
                var value = pending.Method!.Invoke(null, pending.Values);
                var declaredType = pending.Method!.ReturnType;
                var awaiter = ScriptAwaiter.TryCreate(value, declaredType);
                if (awaiter == null)
                {
                    StoreReturnValue(pending, value, value?.GetType() ?? declaredType);
                }
                else
                {
                    pending.Awaiter = awaiter;
                    pending.AwaitedType = DescribeReturnType(declaredType.IsInstanceOfType(value) && declaredType != typeof(object) ? declaredType : value!.GetType());
                    pending.AwaitStartedUtc = DateTime.UtcNow;
                    awaiting = !TryFinishAwait(pending);
                }
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
                    // The synchronous part ran. An awaited rest does not hold the main thread, so it does not count as a
                    // running invocation; the reply waits for Completion, which the tick sets when the awaitable ends.
                    pending.InvocationCompleted = true;
                    if (!awaiting)
                    {
                        pending.Completion.Set();
                    }

                    CleanupCompletedScriptRequestsLocked();
                }
            }
        }

        private void StoreReturnValue(PendingScriptInvocationRequest pending, object? value, Type describedType)
        {
            pending.Value = value;
            var truncated = false;
            pending.ReturnValueType = DescribeReturnType(describedType);
            pending.SerializedReturnValue = ReflectionBridgeService.SerializeReturnValue(value, ref truncated, MaxScriptReturnValueChars, out var fullText);
            pending.ReturnValueTruncated = truncated;
            pending.FullValuePath = fullText == null ? null : WriteFullReturnValue(fullText);
        }

        // Main thread only. True once the awaitable ended and its result or exception is stored.
        private bool TryFinishAwait(PendingScriptInvocationRequest pending)
        {
            var awaiter = pending.Awaiter;
            if (awaiter == null || pending.AwaitFinished)
            {
                return true;
            }

            try
            {
                if (!awaiter.IsCompleted)
                {
                    return false;
                }

                var result = awaiter.GetResult();
                StoreReturnValue(pending, result, awaiter.ReturnsValue ? result?.GetType() ?? typeof(object) : typeof(void));
            }
            catch (Exception ex)
            {
                pending.Exception = ex is TargetInvocationException invocation ? invocation.InnerException ?? invocation : ex;
            }

            pending.AwaitFinished = true;
            pending.AwaitedMs = (int)Math.Round((DateTime.UtcNow - pending.AwaitStartedUtc).TotalMilliseconds);
            return true;
        }

        private void FinishAwaitedScripts()
        {
            List<PendingScriptInvocationRequest> awaiting;
            lock (syncRoot)
            {
                if (detachedAwaits.Count == 0 && !pendingRequests.Any(candidate => candidate.Awaiter != null && !candidate.AwaitFinished))
                {
                    return;
                }

                awaiting = pendingRequests.Where(candidate => candidate.Awaiter != null && !candidate.AwaitFinished).Concat(detachedAwaits).ToList();
            }

            foreach (var pending in awaiting)
            {
                if (!TryFinishAwait(pending))
                {
                    continue;
                }

                lock (syncRoot)
                {
                    pending.Completion.Set();
                    if (detachedAwaits.Remove(pending))
                    {
                        // The exception's type tells an InvalidOperationException from a NullReferenceException, which
                        // the message alone often does not; a Task/UniTask without a value ends as plain "done".
                        var outcome = pending.Exception == null
                            ? pending.SerializedReturnValue == null ? "done" : "done: " + TrimForLog(pending.SerializedReturnValue)
                            : "failed: " + pending.Exception.GetBaseException().GetType().Name + ": " + pending.Exception.GetBaseException().Message;
                        Debug.Log($"{AsyncEndLogPrefix}{pending.MethodLabel} after {pending.AwaitedMs} ms {outcome}");
                    }
                }
            }
        }

        private static string TrimForLog(object? value)
        {
            var text = value == null ? "null" : value as string ?? JsonConvert.SerializeObject(value);
            return text.Length <= 300 ? text : text.Substring(0, 300) + "...";
        }

        // C#-style names: an anonymous object's FullName repeats every argument's assembly-qualified name and
        // costs more tokens than the value it describes.
        private static string DescribeReturnType(Type type)
        {
            if (type.Name.StartsWith("<>f__AnonymousType", StringComparison.Ordinal))
            {
                return "anonymous";
            }

            if (!type.IsGenericType)
            {
                return type.FullName ?? type.Name;
            }

            var name = type.Name;
            var tick = name.IndexOf('`');
            var arguments = string.Join(", ", type.GetGenericArguments().Select(DescribeReturnType));
            return (type.Namespace == null ? string.Empty : type.Namespace + ".") + (tick < 0 ? name : name.Substring(0, tick)) + "<" + arguments + ">";
        }

        // The whole return value of a call whose reply had to cut it; the agent opens the file with its own reader.
        private static string? WriteFullReturnValue(string fullText)
        {
            try
            {
                var directory = Path.Combine(ChievfxMcpToolPolicy.ProjectRoot, "Temp", "ChievfxMcp", "script-output");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".txt");
                File.WriteAllText(path, fullText);
                return path;
            }
            catch (Exception)
            {
                // The trimmed value still goes back; a missing file only loses the tail.
                return null;
            }
        }

        private void CleanupCompletedScriptRequestsLocked()
        {
            detachedAwaits.AddRange(pendingRequests.Where(pending =>
                pending.ResponseWritten && pending.Awaiter != null && !pending.AwaitFinished && !detachedAwaits.Contains(pending)));
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
            var csharpCode = ReadString(args, "csharpCode")
                ?? ReadString(args, "code")
                ?? ReadString(args, "source")
                ?? ReadString(args, "script");
            if (string.IsNullOrWhiteSpace(csharpCode))
            {
                throw new ArgumentException("script-execute requires non-empty csharpCode (the C# source text; aliases code/source/script are also accepted).");
            }

            if (csharpCode!.Length > MaxScriptCodeChars)
            {
                throw new ArgumentException($"script-execute csharpCode is too large. Maximum is {MaxScriptCodeChars} characters.");
            }

            var className = ReadString(args, "className");
            var methodName = ReadString(args, "methodName");
            var parameters = ReadScriptParameters(args);
            try
            {
                var notes = new List<string>();
                var assembly = CompileScript(csharpCode, notes, out var diagnostics, out var originalDiagnostics);

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
                        null,
                        notes: notes,
                        originalDiagnostics: originalDiagnostics);
                }

                var method = ResolveScriptMethod(assembly, className, methodName, parameters is JArray paramsArray ? paramsArray.Count : 0);
                ValidateScriptMethod(method);
                var methodParameters = method.GetParameters();
                var values = new object?[methodParameters.Length];
                for (var i = 0; i < methodParameters.Length; i++)
                {
                    try
                    {
                        values[i] = ReflectionBridgeService.ConvertJsonValue(parameters, i, methodParameters[i].ParameterType);
                    }
                    catch (Exception conversion) when (conversion is FormatException or InvalidCastException or OverflowException or JsonException or ArgumentException)
                    {
                        // The script did not run: name the value and the parameter it was for, not the converter's stack.
                        var supplied = parameters is JArray suppliedValues && i < suppliedValues.Count ? suppliedValues[i].ToString(Formatting.None) : "(none)";
                        throw new ArgumentException(
                            $"parameters[{i}] {supplied} does not convert to {methodParameters[i].ParameterType.Name} {methodParameters[i].Name} of "
                            + $"{DescribeSignature(method)}: {conversion.GetBaseException().Message}");
                    }
                }

                try
                {
                    var invocation = ExecuteScriptMethodWithTimeout(pending, method, values, timeoutMs);
                    if (invocation.TimedOut && pending.Awaiter != null)
                    {
                        var endMarker = AsyncEndLogPrefix + pending.MethodLabel + " ";
                        return CreateScriptExecutionFailure(
                            "timeout",
                            $"The {pending.AwaitedType} returned by {pending.MethodLabel} did not end within {timeoutMs} ms. It keeps running without "
                            + $"blocking the editor; its end is logged as '{endMarker}after <ms> ms done|failed: ...' (awaitMs or events-wait on waitFor).",
                            diagnostics,
                            logStartIndex,
                            includeLogs,
                            logType,
                            startedUtc,
                            timeoutMs,
                            null,
                            stillRunning: true,
                            notes: notes,
                            waitFor: endMarker);
                    }

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
                            invocation.Exception,
                            notes: notes);
                    }

                    if (invocation.AwaitedType != null)
                    {
                        notes.Add($"Awaited the {invocation.AwaitedType} it returned: {invocation.AwaitedMs} ms after the method returned, without blocking the editor.");
                    }

                    return CreateScriptExecutionSuccess(
                        method,
                        invocation,
                        diagnostics,
                        logStartIndex,
                        includeLogs,
                        logType,
                        startedUtc,
                        timeoutMs,
                        notes);
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
                            ReturnValueTruncated = pending.ReturnValueTruncated,
                            FullValuePath = pending.FullValuePath,
                            AwaitedType = pending.AwaitedType,
                            AwaitedMs = pending.AwaitedMs
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

        // A bare OnCleanup(...) call, written as if it were a global function, gets the class's members imported.
        private const string ScriptStateStaticDirective = "using static Chievfx.Mcp.Editor.ChievfxMcpScriptState;";

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

        private MethodInfo ResolveScriptMethod(Assembly assembly, string? className, string? methodName, int suppliedParameterCount)
        {
            var hasExplicitMethodName = !string.IsNullOrWhiteSpace(methodName);
            var resolvedMethodName = hasExplicitMethodName ? methodName!.Trim() : BridgeRuntimeState.DefaultScriptMethodName;
            try
            {
                return ResolveNamedScriptMethod(assembly, className, resolvedMethodName, suppliedParameterCount);
            }
            catch (InvalidOperationException ex) when (!hasExplicitMethodName)
            {
                // The default entry point convention is 'static Main' - but when the script defines
                // exactly one public static method there is nothing to disambiguate, so run it.
                var single = FindSinglePublicStaticMethod(assembly, className, suppliedParameterCount);
                if (single != null)
                {
                    return single;
                }

                throw new InvalidOperationException(
                    $"{ex.Message} script-execute runs 'static Main' by default; either name the method Main, pass methodName, or define exactly one public static method.");
            }
        }

        private MethodInfo ResolveNamedScriptMethod(Assembly assembly, string? className, string resolvedMethodName, int suppliedParameterCount)
        {
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
                $"Static method '{targetType.FullName}.{resolvedMethodName}' has no overload with {suppliedParameterCount} supplied parameters. "
                + "Overloads: " + string.Join("; ", methods.Select(DescribeSignature)) + ".");
        }

        private static string DescribeSignature(MethodInfo method)
        {
            return method.DeclaringType?.Name + "." + method.Name + "(" + string.Join(", ", method.GetParameters().Select(parameter => parameter.ParameterType.Name + " " + parameter.Name)) + ")";
        }

        private MethodInfo? FindSinglePublicStaticMethod(Assembly assembly, string? className, int suppliedParameterCount)
        {
            Type[] types;
            if (string.IsNullOrWhiteSpace(className))
            {
                types = assembly.GetTypes();
            }
            else
            {
                var explicitType = FindScriptType(assembly, className!);
                if (explicitType == null)
                {
                    return null;
                }

                types = new[] { explicitType };
            }

            var candidates = types
                .Where(type => !type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), inherit: false))
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                .Where(method => !method.IsSpecialName)
                .ToArray();
            return candidates.Length == 1 && candidates[0].GetParameters().Length == suppliedParameterCount
                ? candidates[0]
                : null;
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
                _ => throw new InvalidOperationException($"Multiple script classes define static method '{methodName}': "
                    + string.Join(", ", candidates.Select(type => type.FullName)) + ". Pass className with one of them.")
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
            int timeoutMs,
            List<string> notes)
        {
            return new
            {
                success = true,
                stage = "completed",
                method = $"{method.DeclaringType?.FullName}.{method.Name}",
                notes = notes.Count > 0 ? notes : null,
                result = new
                {
                    type = invocation.ReturnValueType ?? method.ReturnType.FullName ?? "System.Void",
                    // Ahead of the preview: a cut value is read from the file, and the path should not sit after 12 KB.
                    truncated = invocation.ReturnValueTruncated,
                    fullValuePath = invocation.FullValuePath,
                    value = invocation.SerializedReturnValue
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
            bool stillRunning = false,
            List<string>? notes = null,
            Dictionary<string, object?>[]? originalDiagnostics = null,
            string? waitFor = null)
        {
            return new
            {
                success = false,
                stage,
                waitFor,
                // The end line reads "... after <ms> ms done|failed: ...": awaitMs (and a tool-batch step) tells them apart by this.
                waitForFailed = waitFor == null ? null : " ms failed: ",
                error = new
                {
                    message,
                    type = exception?.GetType().FullName,
                    stack = exception == null ? null : TrimExceptionStack(exception)
                },
                notes = notes is { Count: > 0 } ? notes : null,
                diagnostics,
                originalDiagnostics,
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

            // An exception from an awaited script is rethrown by the bridge's awaiter: the frames after the rethrow
            // marker are the bridge's own, the script's are before it.
            var stack = exception.StackTrace;
            const string rethrowMarker = "--- End of stack trace from previous location";
            var rethrowAt = stack.IndexOf(rethrowMarker, StringComparison.Ordinal);
            if (rethrowAt > 0 && stack.IndexOf(nameof(ScriptAwaiter), rethrowAt, StringComparison.Ordinal) > 0)
            {
                stack = stack.Substring(0, rethrowAt).TrimEnd();
            }

            var truncated = false;
            return TrimText(stack, MaxStackTraceChars, ref truncated);
        }

    }
}
