#nullable enable
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Chievfx.Mcp.Editor
{
    internal sealed partial class ScriptAsyncOperationService
    {
        internal const string TopLevelScriptClassName = "TopLevelScript";

        private static readonly Regex ReturnsValuePattern = new(@"\breturn\s+[^;\s]", RegexOptions.Compiled);
        private static readonly Regex AwaitPattern = new(@"\bawait\b", RegexOptions.Compiled);

        // Where the wrapper's header sits in the caller's code (0-based line, column it starts at, its length): it shares
        // the first statement's line, so columns after it on that line are moved back by its length in diagnostics.
        private (int Line, int Column, int Length)? _topLevelHeader;

        // A script compiles to a library, and a library cannot have top-level statements (CS8805). Statements written
        // at the top of the file become the body of TopLevelScript.Main; usings stay above it and types declared after
        // the statements stay where they are. The wrapper is spliced into the statements' own lines, so compile errors
        // still point at the caller's line numbers.
        private string? WrapTopLevelStatements(string csharpCode)
        {
            _topLevelHeader = null;
            if (!TryFindTopLevelStatements(csharpCode, out var start, out var end, out var endsWithReturn))
            {
                return null;
            }

            var body = csharpCode.Substring(start, end - start);
            var returnsValue = ReturnsValuePattern.IsMatch(body);
            var isAsync = AwaitPattern.IsMatch(body);
            var returnType = (isAsync, returnsValue) switch
            {
                (true, true) => "async System.Threading.Tasks.Task<object>",
                (true, false) => "async System.Threading.Tasks.Task",
                (false, true) => "object",
                _ => "void",
            };
            var header = $"public static class {TopLevelScriptClassName} {{ public static {returnType} Main() {{ ";
            var lineStart = start == 0 ? 0 : csharpCode.LastIndexOf('\n', start - 1) + 1;
            _topLevelHeader = (csharpCode.Take(start).Count(character => character == '\n'), start - lineStart, header.Length);
            // A trailing return null after the code's own final return would only add an unreachable-code warning.
            var footer = returnsValue && !endsWithReturn ? " return null; } }" : " } }";
            return csharpCode.Substring(0, start) + header + body + footer + csharpCode.Substring(end);
        }

        private bool TryFindTopLevelStatements(string csharpCode, out int start, out int end, out bool endsWithReturn)
        {
            start = -1;
            end = -1;
            endsWithReturn = false;
            try
            {
                var csharpAssembly = LoadRequiredAssembly(BridgeRuntimeState.RoslynCSharpAssemblyName);
                var syntaxTreeType = csharpAssembly.GetType("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree");
                if (syntaxTreeType == null)
                {
                    return false;
                }

                var tree = ReflectionBridgeService.InvokeWithOptionalParameters(FindParseTextMethod(syntaxTreeType), null, csharpCode);
                var getRoot = tree?.GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(method => method.Name == "GetRoot" && method.GetParameters().Length <= 1);
                var root = getRoot == null ? null : ReflectionBridgeService.InvokeWithOptionalParameters(getRoot, tree);
                if (root == null || ReflectionBridgeService.ReadReflectedProperty(root, "Members") is not IEnumerable members)
                {
                    return false;
                }

                foreach (var member in members)
                {
                    if (member == null || member.GetType().Name != "GlobalStatementSyntax")
                    {
                        continue;
                    }

                    var span = ReflectionBridgeService.ReadReflectedProperty(member, "Span");
                    if (span == null)
                    {
                        return false;
                    }

                    var spanStart = Convert.ToInt32(ReflectionBridgeService.ReadReflectedProperty(span, "Start"));
                    var spanEnd = Convert.ToInt32(ReflectionBridgeService.ReadReflectedProperty(span, "End"));
                    start = start < 0 ? spanStart : Math.Min(start, spanStart);
                    if (spanEnd > end)
                    {
                        end = spanEnd;
                        endsWithReturn = ReflectionBridgeService.ReadReflectedProperty(member, "Statement")?.GetType().Name == "ReturnStatementSyntax";
                    }
                }

                return start >= 0 && end > start;
            }
            catch (Exception)
            {
                // Without a syntax tree the code compiles as sent, and CS8805 tells the caller what is wrong.
                return false;
            }
        }
    }
}
