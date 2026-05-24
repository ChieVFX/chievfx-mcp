# ParticleSystem QA Fixture

Scene builder: `ChievFX/MCP/ParticleSystem QA/Rebuild Fixture Scene`.

Generated scene path: `Assets/Scenes/ChievfxMcpParticlesQaFixture.unity`.

## Fixture Coverage

- `QaParticleSystems/MagicGlowLoop` uses the `magic-glow` preset near camera center.
- `QaParticleSystems/SparkBurst` uses the `spark-burst` preset on camera left.
- `QaParticleSystems/SmokePuff` uses the `smoke-puff` preset on camera right.
- `QaParticlesCamera` is orthographic, tagged `MainCamera`, and frames all fixture systems for camera and Game View screenshots.

## Manual / Agent QA Script

1. Rebuild or open the fixture scene with `ChievFX/MCP/ParticleSystem QA/Rebuild Fixture Scene`.
2. Refresh the ParticleSystem manifest if descriptors look stale:

```json
{"tool":"script-execute","arguments":{"csharpCode":"using Chievfx.Mcp.Editor; public static class ExportParticlesManifest { public static void Run() { ChievfxMcpExtensionRegistry.ExportManifest(); } }","className":"ExportParticlesManifest","methodName":"Run"}}
```

3. Simulate the center effect:

```json
{"tool":"particles-preview-control","arguments":{"targetPath":"QaParticleSystems/MagicGlowLoop","action":"simulate","seconds":0.6,"restart":true}}
```

Expected proof:

- Tool response `preview.particleCount` is greater than `0`.
- `screenshot-editor-window` targeting Scene View shows particles around scene center after simulate.
- `screenshot-camera` with `cameraName: "QaParticlesCamera"` should show particles against a dark background.
- `screenshot-game-view` may be black if the Game View is not rendering the fixture camera in Edit Mode; capture camera or Scene View evidence in that case and note the limitation.

4. Confirm resources are useful for review:

```json
{"resource":"chievfx://extensions/chievfx.particles/systems"}
```

Expected proof:

- Summary includes all three fixture paths.
- Detail resource for `QaParticleSystems/MagicGlowLoop` includes `main`, `emission`, `shape`, `renderer`, and `preview` state.
