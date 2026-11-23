#nullable enable
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Chievfx.Mcp.Editor.Tests
{
    // A script's or method's return value: Unity math structs used to fail with "self referencing loop" after the code ran.
    public sealed class ChievfxMcpReturnValueTests
    {
        [Test]
        public void UnityStructsComeBackAsTheirFields()
        {
            var truncated = false;
            Assert.AreEqual("{\"x\":1.0,\"y\":2.0,\"z\":3.0}", ReflectionBridgeService.SerializeReturnValue(new Vector3(1, 2, 3), ref truncated));
            StringAssert.Contains("\"w\":1.0", (string)ReflectionBridgeService.SerializeReturnValue(Quaternion.identity, ref truncated)!);
            StringAssert.Contains("\"a\":1.0", (string)ReflectionBridgeService.SerializeReturnValue(Color.red, ref truncated)!);
            Assert.AreEqual("{\"center\":{\"x\":1.0,\"y\":2.0,\"z\":3.0},\"size\":{\"x\":2.0,\"y\":2.0,\"z\":2.0}}",
                ReflectionBridgeService.SerializeReturnValue(new Bounds(new Vector3(1, 2, 3), Vector3.one * 2), ref truncated));
            StringAssert.Contains("\"x\":0.0", (string)ReflectionBridgeService.SerializeReturnValue(new[] { Vector3.up }, ref truncated)!);
        }

        [Test]
        public void UnityStructsWithoutPublicFieldsComeBackAsTheirProperties()
        {
            var truncated = false;
            var ray = (string)ReflectionBridgeService.SerializeReturnValue(new Ray(new Vector3(1.234567f, 0, 0), Vector3.forward), ref truncated)!;
            StringAssert.Contains("\"origin\":{\"x\":1.234567", ray);
            StringAssert.Contains("\"direction\":{\"x\":0.0,\"y\":0.0,\"z\":1.0}", ray);

            var probe = new GameObject("RaycastProbe", typeof(BoxCollider));
            try
            {
                probe.transform.position = new Vector3(0f, 0f, 5f);
                Physics.SyncTransforms();
                var hits = Physics.RaycastAll(Vector3.zero, Vector3.forward, 20f);
                Assert.IsNotEmpty(hits);
                var json = (string)ReflectionBridgeService.SerializeReturnValue(hits, ref truncated)!;
                StringAssert.Contains("\"distance\":4.5", json);
                StringAssert.Contains("\"name\":\"RaycastProbe\"", json);
                StringAssert.DoesNotContain("\"UnityEngine.RaycastHit\"", json);
            }
            finally
            {
                Object.DestroyImmediate(probe);
            }
        }

        [Test]
        public void AUnityObjectInsideAValueIsAReference()
        {
            var probe = new GameObject("ReturnValueProbe");
            try
            {
                var truncated = false;
                var json = (string)ReflectionBridgeService.SerializeReturnValue(new { pos = Vector3.up, probe }, ref truncated)!;
                StringAssert.Contains("\"pos\":{\"x\":0.0,\"y\":1.0,\"z\":0.0}", json);
                StringAssert.Contains("\"name\":\"ReturnValueProbe\"", json);
                StringAssert.Contains("\"type\":\"GameObject\"", json);
            }
            finally
            {
                Object.DestroyImmediate(probe);
            }
        }
    }
}
