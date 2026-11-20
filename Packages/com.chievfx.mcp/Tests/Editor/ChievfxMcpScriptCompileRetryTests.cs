#nullable enable
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Chievfx.Mcp.Editor.Tests
{
    // script-execute's compile retry: which diagnostics lead when the retry gets further, and the usings advice
    // only when usings were what the code lacked.
    public sealed class ChievfxMcpScriptCompileRetryTests
    {
        private readonly ScriptAsyncOperationService service = new();

        [Test]
        public void InternalAccessAloneDoesNotAdviseAboutUsings()
        {
            var notes = new List<string>();
            var assembly = service.CompileScript(
                "using System;\nusing System.Linq;\nusing UnityEngine;\nusing Chievfx.Mcp.Editor;\n"
                + "public static class RetryInternalOnly { public static object Main() { return ConsoleLogBridgeService.ClassifyConsoleMode(4); } }",
                notes,
                out _,
                out var originalDiagnostics);

            Assert.IsNotNull(assembly, string.Join(" | ", notes));
            Assert.IsNull(originalDiagnostics);
            Assert.AreEqual(1, notes.Count);
            StringAssert.Contains("internal/private member access", notes[0]);
            StringAssert.DoesNotContain("usings", notes[0]);
        }

        [Test]
        public void MissingUsingsAreNamedWhenTheyWereNeeded()
        {
            var notes = new List<string>();
            var assembly = service.CompileScript(
                "public static class RetryNeedsLinq { public static object Main() { return new[] { 1, 2 }.Select(x => x).Count(); } }",
                notes,
                out _,
                out _);

            Assert.IsNotNull(assembly, string.Join(" | ", notes));
            StringAssert.Contains("using System.Linq;", notes.Single());
            StringAssert.Contains("Next time write the usings yourself", notes.Single());
        }

        [Test]
        public void RetryThatGetsFurtherLeadsWithItsOwnErrors()
        {
            var notes = new List<string>();
            var assembly = service.CompileScript(
                "using UnityEngine;\npublic static class RetryGetsFurther { public static object Main() {\n"
                + "var mode = Chievfx.Mcp.Editor.ConsoleLogBridgeService.ClassifyConsoleMode(4);\n"
                + "ChievfxMcpScriptState.OnCleanup(() => Debug.Log(\"x\"));\nreturn mode; } }",
                notes,
                out var diagnostics,
                out var originalDiagnostics);

            Assert.IsNull(assembly);
            CollectionAssert.AreEqual(new[] { "CS7036" }, diagnostics.Select(diagnostic => diagnostic["id"]).ToArray(), "The error still blocking the retry leads.");
            Assert.IsNotNull(originalDiagnostics);
            CollectionAssert.Contains(originalDiagnostics!.Select(diagnostic => diagnostic["id"]).ToArray(), "CS0103");
            StringAssert.Contains("got past", notes.Single());
        }

        [Test]
        public void ScriptStateWithLinqToArrayCompilesWithTheAliasAlone()
        {
            // With internal access, the default System.IO using brings Mono's internal ToArray extension, and
            // every LINQ ToArray() was ambiguous (CS0121) once ChievfxMcpScriptState made the retry run.
            var notes = new List<string>();
            var assembly = service.CompileScript(
                "using System.Linq;\nChievfxMcpScriptState.Set(\"retry.alias\", 5);\nvar items = new[] { 3, 1, 2 }.Where(x => x > 1).ToArray();\nreturn items.Length;",
                notes,
                out var diagnostics,
                out _);

            Assert.IsNotNull(assembly, string.Join(" | ", notes) + " | " + string.Join(" | ", diagnostics.Select(diagnostic => diagnostic["message"])));
            StringAssert.Contains("using ChievfxMcpScriptState = Chievfx.Mcp.Editor.ChievfxMcpScriptState;", notes.Last());
            StringAssert.DoesNotContain("System.IO", notes.Last());
        }

        [Test]
        public void ColumnOnTheFirstStatementLineIsTheCallersOwn()
        {
            // The TopLevelScript.Main header shares the first statement's line; its length is not the caller's column.
            var notes = new List<string>();
            var assembly = service.CompileScript("var value = NoSuchNameHere;\nreturn value;", notes, out var diagnostics, out _);

            Assert.IsNull(assembly);
            Assert.AreEqual("1:13", diagnostics.First(diagnostic => (string?)diagnostic["id"] == "CS0103")["location"]);
        }

        [Test]
        public void RetryThatClearsNothingKeepsTheOriginalErrors()
        {
            var notes = new List<string>();
            var assembly = service.CompileScript(
                "public static class RetryClearsNothing { public static object Main() { return NoSuchThingAnywhere; } }",
                notes,
                out var diagnostics,
                out var originalDiagnostics);

            Assert.IsNull(assembly);
            Assert.IsNull(originalDiagnostics);
            Assert.AreEqual("CS0103", diagnostics.Single()["id"]);
            StringAssert.Contains("Errors below are from your code as sent", notes.Single());
        }
    }
}
