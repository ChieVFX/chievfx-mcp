#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Chievfx.Mcp.Editor.Tests
{
    // script-execute: code without a class runs as TopLevelScript.Main, and a returned awaitable is awaited by polling.
    public sealed class ChievfxMcpScriptTopLevelAndAsyncTests
    {
        private readonly ScriptAsyncOperationService service = new();

        private MethodInfo CompileMain(string code, List<string> notes, out Dictionary<string, object?>[] diagnostics)
        {
            var assembly = service.CompileScript(code, notes, out diagnostics, out _);
            Assert.IsNotNull(assembly, string.Join(" | ", notes.Concat(diagnostics.Select(diagnostic => diagnostic["id"] + " " + diagnostic["message"]))));
            var type = assembly!.GetType(ScriptAsyncOperationService.TopLevelScriptClassName);
            Assert.IsNotNull(type, "TopLevelScript class");
            return type!.GetMethod("Main")!;
        }

        [Test]
        public void StatementsWithReturnBecomeAnObjectMain()
        {
            var notes = new List<string>();
            var main = CompileMain("using System.Linq;\nvar total = new[] { 1, 2, 3 }.Sum();\nreturn total * 2;", notes, out var diagnostics);

            Assert.AreEqual(12, main.Invoke(null, null));
            StringAssert.Contains("Top-level statements ran as TopLevelScript.Main", notes[0]);
            Assert.IsEmpty(diagnostics, "A final return gets no unreachable trailing return.");
        }

        [Test]
        public void ConditionalReturnStillGetsTheFallbackReturn()
        {
            var notes = new List<string>();
            var main = CompileMain("if (System.DateTime.Now.Year > 3000) { return 1; }\nUnityEngine.Debug.Log(\"x\");", notes, out _);

            Assert.IsNull(main.Invoke(null, null));
        }

        [Test]
        public void StatementsWithoutReturnBecomeAVoidMainAndTypesAfterThemStayTopLevel()
        {
            var notes = new List<string>();
            var main = CompileMain("Helper.Count++;\npublic static class Helper { public static int Count; }", notes, out _);

            Assert.AreEqual(typeof(void), main.ReturnType);
        }

        [Test]
        public void CompileErrorsKeepTheCallersLineNumbers()
        {
            var notes = new List<string>();
            service.CompileScript("using System;\n\nvar a = 1;\nvar b = NoSuchName;\nreturn a;", notes, out var diagnostics, out _);

            var error = diagnostics.First(diagnostic => (string?)diagnostic["id"] == "CS0103");
            StringAssert.StartsWith("4:", (string?)error["location"]);
        }

        [Test]
        public void AwaitMakesTheWrapperAsync()
        {
            var notes = new List<string>();
            var main = CompileMain("await System.Threading.Tasks.Task.Yield();\nreturn \"after await\";", notes, out _);

            Assert.IsTrue(typeof(Task).IsAssignableFrom(main.ReturnType));
        }

        [Test]
        public void ClassScriptsAreNotWrapped()
        {
            var notes = new List<string>();
            var assembly = service.CompileScript("public static class Plain { public static int Main() { return 1; } }", notes, out _, out _);

            Assert.IsNotNull(assembly);
            Assert.IsEmpty(notes);
        }

        [Test]
        public void AwaiterReadsTasksAndPassesTheirExceptionThrough()
        {
            var completed = ScriptAwaiter.TryCreate(Task.FromResult(42));
            Assert.IsNotNull(completed);
            Assert.IsTrue(completed!.IsCompleted);
            Assert.IsTrue(completed.ReturnsValue);
            Assert.AreEqual(42, completed.GetResult());

            var faulted = ScriptAwaiter.TryCreate(Task.FromException(new System.InvalidOperationException("boom")), typeof(Task));
            Assert.IsFalse(faulted!.ReturnsValue);
            Assert.Throws<System.InvalidOperationException>(() => faulted.GetResult());

            var pending = new TaskCompletionSource<int>();
            Assert.IsFalse(ScriptAwaiter.TryCreate(pending.Task)!.IsCompleted);
            Assert.IsNull(ScriptAwaiter.TryCreate("not awaitable"));
        }
    }
}
