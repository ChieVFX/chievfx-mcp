# Runtime uGUI QA Fixture

Scene builder: `ChievFX/MCP/uGUI Runtime QA/Rebuild Fixture Scene`.

Generated scene path: `Assets/Scenes/ChievfxMcpUguiRuntimeQaFixture.unity`.

## Fixture Coverage

- `QaOverlayCanvas` uses Screen Space Overlay at sorting order `0`.
- `QaOverlayTopCanvas` uses Screen Space Overlay at sorting order `100` and overlaps the center marker.
- `QaCameraCanvas` uses Screen Space Camera with `QaRuntimeCamera`.
- `QaWorldCanvas` uses World Space with `QaRuntimeCamera`.
- Controls include overlapping buttons, text labels, slider, toggle, disabled button, inactive hidden button, and scroll view.

## Coordinate Convention

Runtime probe coordinates use Game View screen pixels with origin at bottom-left.

Normalized coordinates map to pixels as:

```text
pixel.x = normalized.x * Screen.width
pixel.y = normalized.y * Screen.height
```

Expected marker points:

- Center marker: normalized `(0.5, 0.5)`. On an `800x600` capture this is pixel `(400, 300)`.
- Disabled button marker: normalized `(0.75, 0.25)`. On an `800x600` capture this is pixel `(600, 150)`.
- Outside-bounds marker: normalized `(1.2, 0.5)`. On an `800x600` capture this is pixel `(960, 300)` and should warn as outside current Game View bounds.

## Manual / Agent QA Script

1. Rebuild or open the fixture scene with `ChievFX/MCP/uGUI Runtime QA/Rebuild Fixture Scene`.
2. Enter Play Mode and capture Game View evidence:

```json
{"tool":"screenshot-game-view","arguments":{"width":800,"height":600}}
```

3. Probe the center marker:

```json
{"tool":"ugui-runtime-probe-screen-position","arguments":{"normalized":{"x":0.5,"y":0.5}}}
```

Expected proof:

- Screenshot center crop around pixel `(400, 300)` shows the blue `TOP HIT` button.
- Probe `coordinateConvention.origin` is `bottom-left`.
- Probe `top.path` ends with `QaOverlayTopCanvas/TopHitPanel/TopButton`.
- Probe `top.sorting.sortingOrder` is `100`.
- Probe stack includes `QaOverlayCanvas/BottomHitPanel/BottomButton` below the top hit.

4. Probe disabled control marker:

```json
{"tool":"ugui-runtime-probe-screen-position","arguments":{"normalized":{"x":0.75,"y":0.25}}}
```

Expected proof:

- Screenshot crop around pixel `(600, 150)` shows grey `DISABLED` button.
- Probe `top.path` ends with `QaOverlayCanvas/DisabledButton`.
- Probe `top.interactable` is `false`.

5. Probe outside bounds:

```json
{"tool":"ugui-runtime-probe-screen-position","arguments":{"normalized":{"x":1.2,"y":0.5}}}
```

Expected proof:

- Probe warnings include `outside current screen/game-view bounds`.
- Probe `count` is `0`.

If `screenshot-game-view` misses Screen Space Overlay UI, capture `screenshot-editor-window` for the visible Game View and include the same center/disabled marker coordinates in notes.
