#nullable enable
using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Chievfx.Mcp.Editor.Tests
{
    // asset-find label: Unity's l: matches the start of a label, the tool keeps whole labels only and says what it left out.
    public sealed class ChievfxMcpAssetLabelSearchTests
    {
        private string folder = string.Empty;

        [SetUp]
        public void CreateAssets()
        {
            folder = "Assets/ChievfxMcpLabelTest_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
            CreateLabelled("Whole", "ChievfxLbl");
            CreateLabelled("Prefixed", "ChievfxLblGenerated");
        }

        [TearDown]
        public void DeleteAssets()
        {
            AssetDatabase.DeleteAsset(folder);
        }

        [Test]
        public void ALabelMatchesWholeLabelsAndNamesThoseItLeftOut()
        {
            var result = BridgeResourcePayloadService.FindAssets("test:asset-find", new ResourceAssetFilter { Labels = new[] { "chievfxlbl" }, Folders = new[] { folder } });

            var paths = ((System.Collections.Generic.Dictionary<string, object?>[])result["assets"]!).Select(row => (string)row["path"]!).ToArray();
            CollectionAssert.AreEqual(new[] { folder + "/Whole.anim" }, paths);
            Assert.AreEqual(1, result["totalAssetGuids"]);
            StringAssert.Contains("ChievfxLblGenerated", (string)result["note"]!);
        }

        private void CreateLabelled(string name, string label)
        {
            var path = folder + "/" + name + ".anim";
            AssetDatabase.CreateAsset(new AnimationClip(), path);
            AssetDatabase.SetLabels(AssetDatabase.LoadMainAssetAtPath(path), new[] { label });
        }
    }
}
