#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Chievfx.Mcp.Editor;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using PackageManagerPackageInfo = UnityEditor.PackageManager.PackageInfo;
using static Chievfx.Mcp.Extensions.Particles.ChievfxMcpParticlesExtension;
using static Chievfx.Mcp.Extensions.Particles.ParticlesResources;
using static Chievfx.Mcp.Extensions.Particles.ParticlesTools;
using static Chievfx.Mcp.Extensions.Particles.ParticlesModules;
using static Chievfx.Mcp.Extensions.Particles.ParticlesSchemas;
using static Chievfx.Mcp.Extensions.Particles.ParticlesRows;
using static Chievfx.Mcp.Extensions.Particles.ParticlesShared;

namespace Chievfx.Mcp.Extensions.Particles
{
    internal readonly struct DependencyStatus
    {
        public DependencyStatus(bool available, bool packageInstalled, bool typesLoaded, bool versionDefineActive)
        {
            Available = available;
            PackageInstalled = packageInstalled;
            TypesLoaded = typesLoaded;
            VersionDefineActive = versionDefineActive;
        }

        public bool Available { get; }

        public bool PackageInstalled { get; }

        public bool TypesLoaded { get; }

        public bool VersionDefineActive { get; }

        public Dictionary<string, object?> ToDictionary()
        {
            return new Dictionary<string, object?>
            {
                ["available"] = Available,
                ["packageInstalled"] = PackageInstalled,
                ["particleSystemTypesLoaded"] = TypesLoaded,
                ["versionDefineActive"] = VersionDefineActive,
            };
        }
    }
}
