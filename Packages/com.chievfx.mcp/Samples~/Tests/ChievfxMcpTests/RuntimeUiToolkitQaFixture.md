# Runtime UI Toolkit QA Fixture

Scene builder: `ChievFX/MCP/UI Toolkit Runtime QA/Rebuild Fixture Scene`.

Generated scene path: `Assets/Scenes/ChievfxMcpUiToolkitRuntimeQaFixture.unity`.

## Fixture Coverage

- `QaUiToolkitBottomDocument`, `QaUiToolkitTopDocument`, and `QaUiToolkitSecondaryDisplayDocument` create separate runtime panels through separate `PanelSettings` assets.
- Panel settings cover sorting order `0`, `40`, and `100`; secondary display metadata uses `targetDisplay` `1` where Unity reports it.
- Center marker overlaps `BottomUiToolkitHit` and `TopUiToolkitHit`; top panel should win the probe stack.
- Controls include disabled, display-none hidden, visibility-hidden, picking-ignored, `TextField`, `Toggle`, and a visible-tree cap container with more than 256 rows.

## Coordinate Convention

Runtime probe input uses Game View screen pixels with origin at bottom-left. UI Toolkit panel picking converts this to top-left screen coordinates before calling `RuntimePanelUtils.ScreenToPanel`.

Expected marker points with the default `PanelSettings` scale:

- Center overlap marker: normalized `(0.25, 0.75)`. On an `800x600` capture this is input pixel `(200, 450)` and UI Toolkit screen pixel `(200, 150)`.
- Disabled marker is layout-anchored near the lower-right panel area; prefer probe output plus screenshot crop over fixed pixels when Game View scaling changes.

## Manual / Agent QA Script

1. Rebuild or open the fixture scene with `ChievFX/MCP/UI Toolkit Runtime QA/Rebuild Fixture Scene`.
2. Enter Play Mode and let the test fixture populate the UIDocument roots, or run the Play Mode tests in `ChievfxMcpUiToolkitExtensionTests`.
3. Capture visual evidence. Prefer camera-specific proof when validating camera/target-display behavior:

```json
{"tool":"screenshot-camera","arguments":{"cameraName":"QaUiToolkitRuntimeCamera","width":800,"height":600}}
```

4. Capture Game View evidence when verifying UIDocument overlay layout:

```json
{"tool":"screenshot-game-view","arguments":{"width":800,"height":600}}
```

5. Probe the center marker:

```json
{"tool":"ui-runtime-probe","arguments":{"x":0.25,"y":0.75,"isNormalized":true}}
```

Expected proof:

- Probe `probe.origin` is `bottom-left`.
- `uitoolkit.yInverted` is `true`.
- `uitoolkit.panelScreen` uses top-left panel coordinates.
- `uitoolkit.hits[0].path` contains `TopUiToolkitHit`.
- `uitoolkit.hits[0].sortingOrder` is `100` when multiple panels overlap.
- `uitoolkit.hits` includes `BottomUiToolkitHit` below the top hit.
- `ui-runtime-probe` returns separate `ugui` and `uitoolkit` sections with compact `hits` rows.

6. Dry-run a value mutation; it should report a plan and keep the control value unchanged:

```json
{"tool":"uitoolkit-runtime-interact","arguments":{"action":"setValue","name":"FocusableUiToolkitTextField","value":"dry run only"}}
```

7. For real interaction, stay in Play Mode and pass both `dryRun:false` and `allowStateMutation:true`. Real dispatch can invoke game callbacks and mutate state.
