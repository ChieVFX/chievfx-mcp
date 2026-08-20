#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;

namespace Chievfx.Mcp.Editor.Tests
{
    // Secondary projects are other Unity projects whose MCP server this project's client configs expose
    // alongside our own. Two things have to hold or the injected entry is worse than useless: the name has
    // to be the one that project's own server reports at handshake, and "added successfully" has to mean
    // the entry can actually start a server.
    public sealed class ChievfxMcpSecondaryProjectsTests
    {
        private string? savedSecondaryProjects;
        private string? tempRoot;

        // TryAdd/Remove persist to the one UserSettings file, so put back whatever the editor had.
        [SetUp]
        public void SetUp()
        {
            savedSecondaryProjects = File.Exists(ChievfxMcpToolPolicy.SecondaryProjectsPath)
                ? File.ReadAllText(ChievfxMcpToolPolicy.SecondaryProjectsPath)
                : null;
            tempRoot = Path.Combine(Path.GetTempPath(), "chievfx-mcp-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
        }

        [TearDown]
        public void TearDown()
        {
            if (savedSecondaryProjects != null)
            {
                File.WriteAllText(ChievfxMcpToolPolicy.SecondaryProjectsPath, savedSecondaryProjects);
            }
            else if (File.Exists(ChievfxMcpToolPolicy.SecondaryProjectsPath))
            {
                File.Delete(ChievfxMcpToolPolicy.SecondaryProjectsPath);
            }

            if (tempRoot != null && Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, true);
            }
        }

        // The Python server names itself unity-<sha1(--project-root)[:8]> from the exact string the editor
        // hands it. If the editor's key were derived any other way, the config would register the server
        // under a name that does not match its handshake identity.
        [Test]
        public void ServerName_MatchesTheSha1FormulaTheServerUses()
        {
            var root = MakeUnityProject("urp-sample", withPackage: true);
            var project = new ChievfxMcpSecondaryProject(root, string.Empty);

            using var sha1 = SHA1.Create();
            var hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(root));
            var builder = new StringBuilder(8);
            for (var i = 0; i < 4; i++)
            {
                builder.Append(hash[i].ToString("x2"));
            }

            Assert.AreEqual($"unity-{builder}", project.ServerName);
        }

        [Test]
        public void ServerName_ForThisProjectRootIsThisProjectsOwnKey()
        {
            Assert.AreEqual(
                ChievfxMcpToolPolicy.CursorServerName,
                ChievfxMcpToolPolicy.ServerNameForProjectRoot(ChievfxMcpToolPolicy.ProjectRoot));
        }

        [Test]
        public void TryResolveProjectRoot_WalksUpFromAnyFolderInsideTheProject()
        {
            var root = MakeUnityProject("builtin-sample", withPackage: true);
            var nested = Path.Combine(root, "Assets", "Art", "Materials");
            Directory.CreateDirectory(nested);

            Assert.IsTrue(ChievfxMcpSecondaryProjects.TryResolveProjectRoot(nested, out var resolved, out var error), error);
            Assert.IsTrue(ChievfxMcpSecondaryProjects.IsSamePath(root, resolved));
        }

        // No trailing separator, so the string hashed here and the string handed to --project-root are the
        // same one Path.GetFullPath produces for this project's own root.
        [Test]
        public void TryResolveProjectRoot_TrimsATrailingSeparator()
        {
            var root = MakeUnityProject("trailing", withPackage: true);

            Assert.IsTrue(ChievfxMcpSecondaryProjects.TryResolveProjectRoot(root + Path.DirectorySeparatorChar, out var resolved, out var error), error);
            Assert.AreEqual(root, resolved);
        }

        [Test]
        public void TryResolveProjectRoot_RejectsAFolderThatIsNotInAUnityProject()
        {
            var plain = Path.Combine(tempRoot!, "not-a-project");
            Directory.CreateDirectory(plain);

            Assert.IsFalse(ChievfxMcpSecondaryProjects.TryResolveProjectRoot(plain, out _, out var error));
            Assert.That(error, Does.Contain("not inside a Unity project"));
        }

        [Test]
        public void TryAdd_RejectsThisProject()
        {
            Assert.IsFalse(ChievfxMcpSecondaryProjects.TryAdd(ChievfxMcpToolPolicy.ProjectRoot, out var error, out _));
            Assert.That(error, Does.Contain("this project"));
        }

        // A project without the package resolves a launcher path that does not exist; the client would spawn
        // it, fail, and report a dead MCP server with no explanation of why.
        [Test]
        public void TryAdd_RejectsAProjectWithoutThePackage()
        {
            var root = MakeUnityProject("no-package", withPackage: false);

            Assert.IsFalse(ChievfxMcpSecondaryProjects.TryAdd(root, out var error, out _));
            Assert.That(error, Does.Contain(ChievfxMcpToolPolicy.PackageName));
        }

        [Test]
        public void TryAdd_AcceptsAProjectWithAnEmbeddedPackageAndKeepsItAcrossALoad()
        {
            var root = MakeUnityProject("urp-copy", withPackage: true);

            Assert.IsTrue(ChievfxMcpSecondaryProjects.TryAdd(root, out var error, out var warning), error);
            Assert.IsEmpty(warning);

            ChievfxMcpSecondaryProjects.SetNote(root, "URP copy");
            var projects = ChievfxMcpSecondaryProjects.Load();
            Assert.AreEqual(1, projects.Count);
            Assert.AreEqual(root, projects[0].ProjectRoot);
            Assert.AreEqual("URP copy", projects[0].Note);

            ChievfxMcpSecondaryProjects.Remove(root);
            Assert.IsEmpty(ChievfxMcpSecondaryProjects.Load());
        }

        [Test]
        public void TryAdd_RejectsTheSameProjectTwice()
        {
            var root = MakeUnityProject("dupe", withPackage: true);
            Assert.IsTrue(ChievfxMcpSecondaryProjects.TryAdd(root, out var error, out _), error);

            Assert.IsFalse(ChievfxMcpSecondaryProjects.TryAdd(root, out var secondError, out _));
            Assert.That(secondError, Does.Contain("already in the list"));
        }

        // A manifest entry means the package will be there, but only after Unity resolves it — so the add
        // goes through and says what still has to happen instead of failing.
        [Test]
        public void TryAdd_WarnsWhenThePackageIsDeclaredButNotResolvedYet()
        {
            var root = MakeUnityProject("declared-only", withPackage: false);
            File.WriteAllText(
                Path.Combine(root, "Packages", "manifest.json"),
                "{\"dependencies\":{\"" + ChievfxMcpToolPolicy.PackageName + "\":\"1.0.0\"}}");

            Assert.IsTrue(ChievfxMcpSecondaryProjects.TryAdd(root, out var error, out var warning), error);
            Assert.That(warning, Does.Contain("has not resolved it yet"));
        }

        // The label is the whole marking mechanism: it is what the agent reads at the top of that server's
        // instructions, so it has to name the project, its path, and that it is not the primary one.
        [Test]
        public void Label_NamesTheProjectItsPathAndThatItIsSecondary()
        {
            var root = MakeUnityProject("urp-sample", withPackage: true);
            var label = new ChievfxMcpSecondaryProject(root, "URP copy").Label;

            Assert.That(label, Does.Contain("SECONDARY"));
            Assert.That(label, Does.Contain("urp-sample"));
            Assert.That(label, Does.Contain(root));
            Assert.That(label, Does.Contain("URP copy"));
            Assert.That(label, Does.Contain(ChievfxMcpSecondaryProjects.FolderNameOf(ChievfxMcpToolPolicy.ProjectRoot)));
            Assert.That(label, Does.Not.Contain("\n"), "The label is one line at the top of initialize.instructions.");
        }

        [Test]
        public void Label_OmitsTheNoteSeparatorWhenThereIsNoNote()
        {
            var root = MakeUnityProject("plain", withPackage: true);

            Assert.That(new ChievfxMcpSecondaryProject(root, string.Empty).Label, Does.Not.Contain("—"));
        }

        // The launcher derives its own project root from where it sits, so this project's copy of the
        // content works for a project whose Unity has not written one yet.
        [Test]
        public void EnsureLauncherWritten_CreatesTheLauncherAndLeavesAnExistingOneAlone()
        {
            var root = MakeUnityProject("needs-launcher", withPackage: true);
            var project = new ChievfxMcpSecondaryProject(root, string.Empty);

            ChievfxMcpSecondaryProjects.EnsureLauncherWritten(project);
            Assert.IsTrue(File.Exists(project.LauncherScriptPath));
            Assert.That(File.ReadAllText(project.LauncherScriptPath), Does.Contain("chievfx_mcp_server.py"));

            // That project's own package version owns its launcher; ours must not fight it.
            File.WriteAllText(project.LauncherScriptPath, "# their launcher");
            ChievfxMcpSecondaryProjects.EnsureLauncherWritten(project);
            Assert.AreEqual("# their launcher", File.ReadAllText(project.LauncherScriptPath));
        }

        private string MakeUnityProject(string name, bool withPackage)
        {
            var root = Path.Combine(tempRoot!, name);
            Directory.CreateDirectory(Path.Combine(root, "Assets"));
            Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
            Directory.CreateDirectory(Path.Combine(root, "Packages"));
            if (withPackage)
            {
                var serverDirectory = Path.Combine(root, "Packages", ChievfxMcpToolPolicy.PackageName, "Tools~", "ChievfxMcp");
                Directory.CreateDirectory(serverDirectory);
                File.WriteAllText(Path.Combine(serverDirectory, "chievfx_mcp_server.py"), "# stub");
            }

            return Path.GetFullPath(root);
        }
    }
}
