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
            var logs = ConsoleLogBridgeService.CreateLogOutputs(logStartIndex, includeLogs, logType, includeStackTrace: false, DefaultScriptLogEntries, out var omittedLogsNote);
            if (omittedLogsNote != null)
            {
                notes.Add(omittedLogsNote);
            }

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
                logs,
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
            var logs = ConsoleLogBridgeService.CreateLogOutputs(logStartIndex, includeLogs, logType, includeStackTrace: false, DefaultScriptLogEntries, out var omittedLogsNote);
            if (omittedLogsNote != null)
            {
                notes = notes == null ? new List<string>() : new List<string>(notes);
                notes.Add(omittedLogsNote);
            }

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
                logs,
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
