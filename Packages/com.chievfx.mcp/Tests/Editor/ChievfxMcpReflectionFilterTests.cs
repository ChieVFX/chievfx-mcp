#nullable enable
using System;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Chievfx.Mcp.Editor.Tests
{
    // reflection-method-find/-find-single/-call refuse filters that would silently match every loaded method.
    public sealed class ChievfxMcpReflectionFilterTests
    {
        private readonly ReflectionBridgeService service = new();

        [Test]
        public void UnknownFilterKeyIsRefusedByEveryReflectionTool()
        {
            var args = new JObject { ["filter"] = new JObject { ["name"] = "GetFirstUser" } };

            StringAssert.Contains("unknown filter key(s) 'name'", Assert.Throws<ArgumentException>(() => service.FindMethods(args))!.Message);
            StringAssert.Contains("reflection-method-find-single", Assert.Throws<ArgumentException>(() => service.FindSingleMethod(args))!.Message);
            StringAssert.Contains("reflection-method-call", Assert.Throws<ArgumentException>(() => service.CallMethod(args))!.Message);
        }

        [Test]
        public void FilterWithoutAnyNameIsRefused()
        {
            var empty = new JObject { ["filter"] = new JObject() };
            StringAssert.Contains("needs typeName, methodName or namespace", Assert.Throws<ArgumentException>(() => service.FindMethods(empty))!.Message);

            var parametersOnly = new JObject { ["filter"] = new JObject { ["inputParameters"] = new JArray() } };
            Assert.Throws<ArgumentException>(() => service.FindMethods(parametersOnly));

            Assert.Throws<ArgumentException>(() => service.FindMethods(new JObject()), "A missing filter is refused too.");
        }

        [Test]
        public void KnownKeysStillMatchIgnoringCase()
        {
            var result = service.FindMethods(new JObject
            {
                ["filter"] = new JObject
                {
                    ["TypeName"] = nameof(ConsoleLogBridgeService),
                    ["methodName"] = nameof(ConsoleLogBridgeService.ClassifyConsoleMode),
                },
            });

            Assert.AreEqual(1, result.GetType().GetProperty("count")!.GetValue(result));
        }

        [Test]
        public void ParameterTypeAcceptsTheShortNameFindPrints()
        {
            var result = service.CallMethod(AbsCall(new JObject { ["typeName"] = "int", ["value"] = -4 }, topLevel: null));
            Assert.AreEqual(4, ReturnValue(result), "typeName 'int' (as find prints it) matches System.Int32.");
        }

        [Test]
        public void CallTakesArgumentValuesFromTheFilterOrTheTopLevelList()
        {
            var fromFilter = service.CallMethod(AbsCall(new JObject { ["typeName"] = "System.Int32", ["value"] = -7 }, topLevel: null));
            Assert.AreEqual(7, ReturnValue(fromFilter), "filter.inputParameters[i].value is the argument.");

            var fromTopLevel = service.CallMethod(AbsCall(new JObject { ["typeName"] = "System.Int32" }, topLevel: new JArray(new JObject { ["value"] = -9 })));
            Assert.AreEqual(9, ReturnValue(fromTopLevel));
        }

        [Test]
        public void CallWithoutAValueIsRefusedInsteadOfPassingDefault()
        {
            var error = Assert.Throws<ArgumentException>(() => service.CallMethod(AbsCall(new JObject { ["typeName"] = "System.Int32" }, topLevel: null)))!;
            StringAssert.Contains("has no value", error.Message);
            StringAssert.Contains("filter.inputParameters[0].value", error.Message);
        }

        [Test]
        public void CallMatchesNamesExactlyAndNamesTheMethodItCalled()
        {
            var result = (System.Collections.Generic.Dictionary<string, object?>)service.CallMethod(AbsCall(new JObject { ["typeName"] = "int", ["value"] = -3 }, topLevel: null));
            Assert.AreEqual("System.Math.Abs(Int32 value)", result["called"]);

            // Mathf.Clamp has no one-float overload: a substring match used to run Clamp01 instead and say nothing.
            var clamp = new JObject
            {
                ["filter"] = new JObject
                {
                    ["typeName"] = "Mathf",
                    ["methodName"] = "Clamp",
                    ["inputParameters"] = new JArray(new JObject { ["typeName"] = "float", ["value"] = 5.5 }),
                },
            };
            var error = Assert.Throws<InvalidOperationException>(() => service.CallMethod(clamp))!;
            StringAssert.Contains("No overload takes the given inputParameters", error.Message);
            StringAssert.Contains("UnityEngine.Mathf.Clamp(Single value, Single min, Single max)", error.Message);
            StringAssert.DoesNotContain("Clamp01", error.Message);
        }

        [Test]
        public void MisspeltNameIsRefusedWithCloseNamesAndContainsStillCallsBySubstring()
        {
            var misspelt = AbsCall(new JObject { ["typeName"] = "int", ["value"] = -2 }, topLevel: null);
            misspelt["filter"]!["methodName"] = "Ab";
            var error = Assert.Throws<InvalidOperationException>(() => service.CallMethod(misspelt))!;
            StringAssert.Contains("Close names: ", error.Message);
            StringAssert.Contains(".Abs(", error.Message);

            misspelt["match"] = "contains";
            Assert.AreEqual(2, ReturnValue(service.CallMethod(misspelt)), "match:'contains' calls the first substring match.");
        }

        private static JObject AbsCall(JObject parameter, JArray? topLevel)
        {
            var args = new JObject
            {
                ["filter"] = new JObject { ["typeName"] = "System.Math", ["methodName"] = "Abs", ["inputParameters"] = new JArray(parameter) },
            };
            if (topLevel != null)
            {
                args["inputParameters"] = topLevel;
            }

            return args;
        }

        private static object? ReturnValue(object result)
        {
            return result is System.Collections.IDictionary dictionary ? dictionary["value"] : result.GetType().GetProperty("value")?.GetValue(result);
        }
    }
}
