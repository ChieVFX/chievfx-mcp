# Cinemachine And Timeline MCP Test Scene

Use scene `Assets/Scenes/TestCinemachineAndTimeline.unity` to smoke-test the
`cinemachine-and-timeline` MCP category. The scene contains a compact camera QA
setup under `MCP_Cinemachine_Timeline_TestRig`:

- `Floor_Runway_ScaledCube`: large cube floor with yellow side rails.
- `Targets_To_Track/Target_A_Hero_Blue_FollowLookAt`: main blue capsule target.
- `Targets_To_Track/Target_B_Rival_Orange_Cutaway`: orange cutaway target.
- `Targets_To_Track/Target_C_Green_Background_Depth`: green background target.
- `Timeline_Beat_Markers/*`: visible floor markers for shot beats.
- `Main Camera`: bound camera with `CinemachineBrain`.
- `Cinemachine_Cameras_For_MCP_Tests/*`: Cinemachine cameras and Timeline shot cameras.
- `Timeline_Director_MCP_Camera_ShotSequence`: playable director with 8.5s shot sequence.
- `Assets/MCPGenerated/CameraTimelineTest/MCP_Camera_ShotSequence.playable`: persistent Timeline asset.

Scenario: 0-2s wide establishing shot, 2-4.5s hero follow, 4.5-6.5s rival
cutaway, 6.5-8.5s return to group frame. Use `timeline-director-preview` and
`screenshot-game-view` or `screenshot-camera` to visually verify composition.

## Tools

### `brain-ensure`

Use it against `MCP_Cinemachine_Timeline_TestRig/Main Camera`.

Expected:

- With `dryRun:true`, result says it would create or find a brain without changing scene.
- With `dryRun:false`, result returns one `CinemachineBrain` on Main Camera.
- `chievfx://extensions/chievfx.cameras/cinemachine/brains` lists exactly that brain.

Suggested call:

```json
{
  "cameraPath": "MCP_Cinemachine_Timeline_TestRig/Main Camera",
  "createCameraIfMissing": false,
  "dryRun": false
}
```

### `cinemachine-create`

Create extra test cameras under
`MCP_Cinemachine_Timeline_TestRig/Cinemachine_Cameras_For_MCP_Tests`, targeting
blue or orange capsule.

Expected:

- New object has `CinemachineCamera`.
- Camera appears in `chievfx://extensions/chievfx.cameras/cinemachine/cameras`.
- Detail resource reports lens fields and enabled state.
- Target/priority should be set or clear warnings should explain any API mismatch.

Suggested target paths:

- `MCP_Cinemachine_Timeline_TestRig/Targets_To_Track/Target_A_Hero_Blue_FollowLookAt`
- `MCP_Cinemachine_Timeline_TestRig/Targets_To_Track/Target_B_Rival_Orange_Cutaway`

### `cinemachine-set`

Use it on `CM3_Hero_Follow_Blue_MCP` or `CM3_Rival_Cutaway_Orange_MCP` to mutate
lens, priority, target, or enabled state.

Expected:

- `dryRun:true` returns proposed changed fields only.
- `dryRun:false` changes the camera and marks the scene dirty.
- Camera detail resource reflects new `fieldOfView`, priority, target, and enabled state.
- Screenshot after change should show visible framing difference when FOV changes.

Good checks:

- Set hero FOV to `30`, verify tighter framing.
- Set rival priority above hero, verify active camera selection if Timeline is not driving.
- Disable a shot camera, verify resource reports `enabled:false`.

### `cinemachine-blender-settings-set`

Create custom blend settings asset under
`Assets/MCPGenerated/CameraTimelineTest/MCP_Camera_Blends.asset`.

Expected:

- Asset is created or updated.
- `chievfx://extensions/chievfx.cameras/cinemachine/blender-settings` lists the asset.
- If assigned to selected brain, brain detail reports custom blends.

Useful test:

- Add blend from `CM3_Wide_Establishing_MCP` to `CM3_Hero_Follow_Blue_MCP`.
- Scrub Timeline across 2s boundary and verify blend metadata/resource output.

### `cinemachine-confiner-set`

Use the floor collider as simple 3D confiner input:
`MCP_Cinemachine_Timeline_TestRig/Floor_Runway_ScaledCube`.

Expected:

- Tool adds or updates `CinemachineConfiner3D` on selected Cinemachine camera.
- `chievfx://extensions/chievfx.cameras/cinemachine/confiner-3d` lists the camera.
- Detail confirms collider path, damping, and cache invalidation flags when requested.

Best target:

- Camera: `MCP_Cinemachine_Timeline_TestRig/Cinemachine_Cameras_For_MCP_Tests/CM3_Wide_Establishing_MCP`
- Collider: `MCP_Cinemachine_Timeline_TestRig/Floor_Runway_ScaledCube`

### `cinemachine-spline-dolly-set`

This scene has Splines package available, but no spline path is authored by
default. Test flow should create or provide a `SplineContainer`, then attach
`CinemachineSplineDolly` to a camera.

Expected:

- Without a spline path/id, tool returns validation error.
- With a valid spline, tool adds or updates `CinemachineSplineDolly`.
- `chievfx://extensions/chievfx.cameras/cinemachine/splines-dolly` lists the camera.
- Detail reports spline reference, position units, position, and auto-dolly state.

Suggested setup:

- Create a spline from wide marker to hero marker to rival marker.
- Apply dolly to `CM3_Wide_Establishing_MCP`.
- Scrub position values and verify camera movement visually.

### `cinemachine-sequencer-create`

Create an alternate CM3 sequencer camera under
`MCP_Cinemachine_Timeline_TestRig/Cinemachine_Cameras_For_MCP_Tests`.

Expected:

- Tool creates a `CinemachineSequencerCamera`.
- Child shot cameras are created.
- `chievfx://extensions/chievfx.cameras/cinemachine/sequencers` lists the sequencer.
- Sequencer detail template reports child shots and loop state.

Suggested use:

- Target blue hero capsule.
- Add three shots: wide, hero, rival.
- Use `ensureBrain:true` against Main Camera.

### `timeline-director-create`

Create an additional director and Timeline asset under
`Assets/MCPGenerated/CameraTimelineTest`.

Expected:

- Scene gets a new `PlayableDirector`.
- Asset appears in `chievfx://extensions/chievfx.cameras/timeline/assets`.
- Director appears in `chievfx://extensions/chievfx.cameras/timeline/directors`.
- Detail resource reports time, duration, state, asset path, tracks, clips, and bindings.

Known-good reference:

- Director: `MCP_Cinemachine_Timeline_TestRig/Timeline_Director_MCP_Camera_ShotSequence`
- Asset: `Assets/MCPGenerated/CameraTimelineTest/MCP_Camera_ShotSequence.playable`

### `timeline-shot-sequence-create`

Use it to populate a Timeline with a Cinemachine track and camera shots.

Expected:

- Timeline has one `CinemachineTrack`.
- Track is bound to Main Camera's `CinemachineBrain`.
- Director detail reports clips for each shot with expected start/duration/end.
- Scrubbing shot times changes active camera composition.

Reference shot sequence:

- `Shot_00_Wide_Establishing`: `0.0`, duration `2.0`
- `Shot_01_Hero_Follow`: `2.0`, duration `2.5`
- `Shot_02_Rival_Cutaway`: `4.5`, duration `2.0`
- `Shot_03_Return_Wide_Group`: `6.5`, duration `2.0`

Use this scene to verify CM3 compatibility. If tool output reports missing clips
or target/priority warnings, resource assertions should catch the regression.

### `timeline-director-preview`

Scrub the existing director:
`MCP_Cinemachine_Timeline_TestRig/Timeline_Director_MCP_Camera_ShotSequence`.

Expected:

- `Evaluate` at `1.0` shows wide/group composition.
- `Evaluate` at `3.0` shows blue hero-focused composition.
- `Evaluate` at `5.0` shows orange rival cutaway.
- `Evaluate` at `7.0` returns to wide/group composition.
- Result includes clips, track binding, current state, and visual QA hint.

Follow preview with `screenshot-game-view` or `screenshot-camera` to verify the
framing. In this scene, target bodies, floor, rails, and beat markers should be
visible in wide view.

## Resources

### `chievfx://extensions/chievfx.cameras/status`

Use first in any test run.

Expected:

- `cinemachine: ok` with `com.unity.cinemachine@3.1.7` or newer.
- `timeline: ok` with installed Timeline package.
- Sequencer, Splines Dolly, Input System, Blender Settings, Impulse, and Confiner
  capability gates report available when package types load.

### `chievfx://extensions/chievfx.cameras/cinemachine/brains`

Expected:

- Lists one brain on `MCP_Cinemachine_Timeline_TestRig/Main Camera`.
- Provides detail URI for brain drill-down.

Use after `brain-ensure`, `timeline-shot-sequence-create`, and blend assignment.

### `chievfx://extensions/chievfx.cameras/cinemachine/brain/{pathOrInstanceId}`

Use detail URI from brains index or encoded path:
`MCP_Cinemachine_Timeline_TestRig/Main Camera`.

Expected:

- Reports `CinemachineBrain`.
- Reports target Unity camera identity.
- Reports default blend and custom blend fallback/assignment data.

### `chievfx://extensions/chievfx.cameras/cinemachine/cameras`

Expected:

- Lists authored cameras:
  - `CM3_Wide_Establishing_MCP`
  - `CM3_Hero_Follow_Blue_MCP`
  - `CM3_Rival_Cutaway_Orange_MCP`
  - Timeline shot cameras under `Cinemachine_Cameras_For_MCP_Tests`
- Reports priority and lens summary.
- Provides detail URIs for each camera.

Use before and after `cinemachine-create`, `cinemachine-set`, sequencer, and
Timeline shot sequence tests.

### `chievfx://extensions/chievfx.cameras/cinemachine/camera/{pathOrInstanceId}`

Use detail URI from cameras index or encoded camera path.

Expected:

- Reports camera path, enabled state, priority, lens, target, and transform.
- For `CM3_Hero_Follow_Blue_MCP`, target should point to blue hero capsule.
- For `CM3_Rival_Cutaway_Orange_MCP`, target should point to orange rival capsule.

### `chievfx://extensions/chievfx.cameras/cinemachine/blender-settings`

Expected before blend tests:

- May be empty if no blend asset has been created.

Expected after `cinemachine-blender-settings-set`:

- Lists blend asset under `Assets/MCPGenerated/CameraTimelineTest`.
- Reports custom blend rows and whether assigned brain uses them.

### `chievfx://extensions/chievfx.cameras/cinemachine/confiner-2d`

Expected in this 3D scene:

- Empty until a 2D collider and Confiner2D are explicitly created.
- Still useful to verify empty inventory is clean and capped, not an error.

### `chievfx://extensions/chievfx.cameras/cinemachine/confiner-3d`

Expected before confiner test:

- Empty or no rows for scene cameras.

Expected after `cinemachine-confiner-set`:

- Lists the camera with `CinemachineConfiner3D`.
- Reports floor collider reference when using `Floor_Runway_ScaledCube`.

### `chievfx://extensions/chievfx.cameras/cinemachine/impulse`

Expected by default:

- Empty inventory.

Suggested extended test:

- Add `CinemachineImpulseSource` to a beat marker or target.
- Add listener to Main Camera/brain object if needed.
- Resource should report source/listener rows.

### `chievfx://extensions/chievfx.cameras/cinemachine/input-axis-controllers`

Expected by default:

- Empty inventory unless an input controller has been added.
- Resource should still report Input System availability when types are loaded.

Suggested extended test:

- Add `CinemachineInputAxisController` to an interactive camera.
- Resource should list controller and Input System-backed rows.

### `chievfx://extensions/chievfx.cameras/cinemachine/sequencers`

Expected before `cinemachine-sequencer-create`:

- Empty inventory.

Expected after sequencer creation:

- Lists sequencer camera under `Cinemachine_Cameras_For_MCP_Tests`.
- Provides sequencer detail URI.

### `chievfx://extensions/chievfx.cameras/cinemachine/sequencer/{pathOrInstanceId}`

Use after `cinemachine-sequencer-create`.

Expected:

- Reports sequencer path, loop state, child shot list, target, and camera rows.
- Should distinguish sequencer camera from regular `CinemachineCamera` rows.

### `chievfx://extensions/chievfx.cameras/cinemachine/splines-dolly`

Expected before spline dolly test:

- Empty inventory.

Expected after `cinemachine-spline-dolly-set`:

- Lists camera with `CinemachineSplineDolly`.
- Reports spline reference and position fields.

### `chievfx://extensions/chievfx.cameras/timeline/directors`

Expected:

- Lists `MCP_Cinemachine_Timeline_TestRig/Timeline_Director_MCP_Camera_ShotSequence`.
- Reports duration `8.5`.
- Reports asset path `Assets/MCPGenerated/CameraTimelineTest/MCP_Camera_ShotSequence.playable`.
- May also list simple setup director `Timeline_Director_Camera_Beat_Test`.

Use after every Timeline tool call.

### `chievfx://extensions/chievfx.cameras/timeline/director/{pathOrInstanceId}`

Use detail URI from directors index or encoded path.

Expected for `Timeline_Director_MCP_Camera_ShotSequence`:

- Reports state `Paused` after preview/evaluate.
- Reports `MCP Cinemachine Shot Track`.
- Reports 4 clips with starts/durations matching scenario.
- Reports binding from Cinemachine track to Main Camera's `CinemachineBrain`.

### `chievfx://extensions/chievfx.cameras/timeline/assets`

Expected:

- Lists `Assets/MCPGenerated/CameraTimelineTest/MCP_Camera_ShotSequence.playable`.
- Provides GUID-based asset detail URI.

Use after `timeline-director-create` and `timeline-shot-sequence-create`.

### `chievfx://extensions/chievfx.cameras/timeline/asset/{guidOrPath}`

Use GUID from Timeline assets index, or URL-encoded asset path.

Expected:

- Reports Timeline asset tracks and clips.
- Clip count should be 4 for the reference asset.
- Clip start/end values should match the scenario.

## Visual QA

Use screenshots after structural checks:

- `timeline-director-preview` at `1.0`, then `screenshot-game-view`.
- `timeline-director-preview` at `3.0`, then `screenshot-game-view`.
- `timeline-director-preview` at `5.0`, then `screenshot-game-view`.
- `timeline-director-preview` at `7.0`, then `screenshot-game-view`.

Expected visual result:

- Wide shots show all capsule targets, floor runway, rails, and beat markers.
- Hero shot emphasizes blue capsule.
- Rival shot emphasizes orange capsule.
- No shot should be mostly sky or crop every target out of frame.

Finish test pass with `console-get-logs` for `Error`, `Exception`, `Assert`, and
`Warning`. Expected count is `0`.
