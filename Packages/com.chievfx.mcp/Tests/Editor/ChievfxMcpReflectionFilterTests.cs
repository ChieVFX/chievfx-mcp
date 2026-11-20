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
