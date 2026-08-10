using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Chievfx.Mcp.Extensions.Cameras")]
[assembly: InternalsVisibleTo("Chievfx.Mcp.Extensions.Ecs")]
[assembly: InternalsVisibleTo("Chievfx.Mcp.Extensions.Particles")]
[assembly: InternalsVisibleTo("Chievfx.Mcp.Extensions.Ugui")]
[assembly: InternalsVisibleTo("Chievfx.Mcp.Extensions.UiToolkit")]
// Tests/Editor: compiled with the package, so these run in any project that lists com.chievfx.mcp
// under "testables".
[assembly: InternalsVisibleTo("Chievfx.Mcp.Editor.PackageTests")]

// Samples~/Tests: shipped as the optional "Editor Tests" sample, so this assembly only exists once a
// user imports it. Kept separate from the name above so importing the sample cannot collide.
[assembly: InternalsVisibleTo("Chievfx.Mcp.Editor.Tests")]
