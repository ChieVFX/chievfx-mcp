# Camera/Cutscene MCP QA Fixture

Current project keeps `com.unity.cinemachine` absent and `com.unity.timeline` present. Default CI should validate the Cinemachine unavailable path plus Timeline resources/tools. Package-present QA is an isolated matrix:

- Unity `6000.3.10f1`
- `com.unity.timeline` `1.8.10`
- `com.unity.cinemachine` `3.x`
- Optional CM3 helper checks: add `com.unity.splines` plus `UnityEngine.Splines.SplineContainer` for Splines Dolly rows, and `com.unity.inputsystem` plus `InputActionReference`/`PlayerInput` only for Input System-specific InputAxisController rows.

Do not commit `Packages/manifest.json` or `Packages/packages-lock.json` churn from the Cinemachine-positive matrix unless the project intentionally adopts Cinemachine.

## Fixture Setup

1. Add `com.unity.cinemachine` in an isolated worktree or package-present QA run.
2. Rebuild or open scene with `ChievFX/MCP/Cameras QA/Rebuild Fixture Scene`.
3. For Sequencer Camera-only QA, rebuild or open `ChievFX/MCP/Cameras QA/Rebuild Sequencer Camera Fixture Scene`.

The scene builder creates:

- `QaEndingTarget`, a simple subject framed for camera review.
- `QaGameplayCamera`, tagged `MainCamera`, with solid background for `screenshot-camera`.
- `QaEndingTimelineDirector` and `Assets/Editor/ChievfxMcpTests/GeneratedCameras/EndingSessionSlowMoZoom.playable` when Timeline is available.
- Cinemachine Brain, two Cinemachine cameras, CinemachineTrack, and overlapping shot clips when Cinemachine is available.

The Sequencer Camera fixture creates only scene objects and no Timeline asset:

- `QaSequencerTarget`, `QaCameraGround`, and `QaCameraKeyLight` for visible composition.
- `QaSequencerGameplayCamera`, tagged `MainCamera`, with CinemachineBrain when CM3 Sequencer Camera is available.
- `QaSequencerCamera` with child Cinemachine cameras `QaSequencerWide`, `QaSequencerTight`, and `QaSequencerBlendCheck`.
- Non-looping instructions with holds `1.25`, `0.8`, and `1.1` seconds plus cut/ease blend values.

## Automated Gates

Run focused EditMode tests:

```json
{"tool":"tests-run","arguments":{"testMode":"EditMode","testClass":"ChievfxMcpCamerasExtensionTests"}}
```

Expected package-absent proof:

- Status reports Timeline available and Cinemachine unavailable.
- Cinemachine resources/tools return unavailable envelopes.
- Timeline director creation supports `dryRun`, registers Undo on real create, dirties scene only on mutation, and preview evaluate restores/reports prior time/state.
- Timeline director/assets resources stay compact/capped and do not dirty scenes during reads.

Expected package-present proof:

- Fixture exposes camera targets, lens, priority, brain blend data, Timeline directors/assets/tracks/clips/signals/bindings.
- Sequencer Camera fixture exposes capped read-only list/detail resources, loop=false, instruction hold/blend summaries, and warning-only handling for missing instruction camera references.
- Advanced helper resources expose capped read-only envelopes for Splines Dolly, InputAxisController, Blender Settings/custom blends, Impulse, and Confiner2D/3D; unavailable envelopes clearly name missing CM3/versionDefine/type or optional Splines/Input System gates.
- Shot sequence response includes slow-mo runtime note and `screenshot-camera` visual QA guidance.

## Visual Validation

Use camera-composed capture:

```json
{"tool":"screenshot-camera","arguments":{"cameraName":"QaGameplayCamera","width":640,"height":360}}
```

For Sequencer Camera QA use:

```json
{"tool":"screenshot-camera","arguments":{"cameraName":"QaSequencerGameplayCamera","width":640,"height":360}}
```

Expected Sequencer frames:

- At start: `QaSequencerWide` frames target capsule, ground, and directional light.
- After first hold (`>1.25s`): `QaSequencerTight` shows tighter target framing.
- During deterministic transition windows, blend/transition should move smoothly without black frames; if exact frame timing is unstable, record start and post-hold frames as required evidence and note blend sanity as best-effort.

Do not rely on Game View as sole evidence; prior QA found black Game View captures in this project.
