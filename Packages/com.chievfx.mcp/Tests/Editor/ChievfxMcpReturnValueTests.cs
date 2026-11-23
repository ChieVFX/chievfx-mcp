#nullable enable
using System.Runtime.InteropServices;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
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

        [StructLayout(LayoutKind.Sequential)]
        private struct HalfUvVertex
        {
            public Vector3 Position;
            public ushort U;
            public ushort V;
        }

        // Terrain tiles store UVs in half precision: RaycastHit.textureCoord on them logged an error and an assert per hit.
        [Test]
        public void ARaycastHitOnAMeshWithHalfPrecisionUvsLogsNothing()
        {
            var mesh = new Mesh();
            mesh.SetVertexBufferParams(3,
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float16, 2));
            var half = Mathf.FloatToHalf(0.5f);
            mesh.SetVertexBufferData(new[]
            {
                new HalfUvVertex { Position = new Vector3(-1f, -1f, 0f), U = half, V = half },
                new HalfUvVertex { Position = new Vector3(0f, 1f, 0f), U = half, V = half },
                new HalfUvVertex { Position = new Vector3(1f, -1f, 0f), U = half, V = half },
            }, 0, 0, 3);
            mesh.SetIndexBufferParams(3, IndexFormat.UInt16);
            mesh.SetIndexBufferData(new ushort[] { 0, 1, 2 }, 0, 0, 3);
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, 3));
            mesh.RecalculateBounds();
            var probe = new GameObject("HalfUvProbe");
            try
            {
                probe.transform.position = new Vector3(0f, 0f, 5f);
                probe.AddComponent<MeshCollider>().sharedMesh = mesh;
                Physics.SyncTransforms();
                var hits = Physics.RaycastAll(Vector3.zero, Vector3.forward, 20f);
                Assert.IsNotEmpty(hits);
                var truncated = false;
                var json = (string)ReflectionBridgeService.SerializeReturnValue(hits, ref truncated)!;
                StringAssert.Contains("\"name\":\"HalfUvProbe\"", json);
                StringAssert.DoesNotContain("textureCoord", json);
                StringAssert.DoesNotContain("lightmapCoord", json);
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                Object.DestroyImmediate(probe);
                Object.DestroyImmediate(mesh);
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
