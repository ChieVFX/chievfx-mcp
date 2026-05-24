namespace Chievfx.Mcp.Editor.Tests.Auxillary
{
    public class ForReflectionTests
    {
        public class TestClass
        {
            public int TestProperty { get; set; }

            public void TestMethod()
            {
                UnityEngine.Debug.Log("TestMethod");
            }
        }
    }
}