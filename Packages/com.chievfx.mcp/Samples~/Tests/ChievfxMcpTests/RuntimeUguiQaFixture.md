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

Normalized coordinates map to pixels against the size the Game View *renders* at — the value the tools
report as `screenSize` and screenshots report as `screenWidth`/`screenHeight`:

```text
pixel.x = normalized.x * screenSize.width
pixel.y = normalized.y * screenSize.height
```

That is not `Screen.width`/`Screen.height`: read from editor code, Unity resolves `Screen.*` against the
Game View *window*, so a Game View locked to a fixed resolution reports the window size instead. See the
fixed-resolution check below.

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
{"tool":"ui-runtime-probe","arguments":{"x":0.5,"y":0.5,"isNormalized":true}}
```

Expected proof:

- Screenshot center crop around pixel `(400, 300)` shows the blue `TOP HIT` button.
- Probe `probe.origin` is `bottom-left`.
- `ugui.hits` is top-to-bottom at the probed screen position.
- Probe `ugui.hits[0].path` ends with `QaOverlayTopCanvas/TopHitPanel/TopButton`.
- Probe `ugui.hits[0].sortingOrder` is `100`.

4. Probe disabled control marker:

```json
{"tool":"ui-runtime-probe","arguments":{"x":0.75,"y":0.25,"isNormalized":true}}
```

Expected proof:

- Screenshot crop around pixel `(600, 150)` shows grey `DISABLED` button.
- Probe `ugui.hits[0].path` ends with `QaOverlayCanvas/DisabledButton`.
- Probe `ugui.hits[0].interactable` is `false`.

5. Probe outside bounds:

```json
{"tool":"ui-runtime-probe","arguments":{"x":1.2,"y":0.5,"isNormalized":true}}
```

Expected proof:

- Probe warnings include `outside current screen/game-view bounds`.
- Probe `ugui.count` is `0`.

## Fixed-Resolution Check (coordinate regression)

Every normalized coordinate is divided by the Game View render size, so the case worth re-testing is a Game
View whose render size differs from its window size. Set the Game View to a fixed resolution that does not
match the window aspect — e.g. add a `2340x1080` Fixed Resolution size and select it — then, in Play Mode:

```json
{"tool":"ui-runtime-probe","arguments":{"x":0.5,"y":0.5,"isNormalized":true}}
```

Expected proof:

- Probe `screenSize` is `2340x1080` (the render size), not the Game View window size.
- Probe `screenSizeSource` is `gameView.targetSize`, and the text output says `screen size from
  gameView.targetSize`.
- Probe `screen` is `1170, 540` and `ugui.hits[0].path` ends with `TopHitPanel/TopButton` — the center
  control, not a control to its left.
- `screenshot-game-view` reports `screenWidth` `2340` and omits `pixelMappingReliable`, so reading a pixel
  off the PNG and passing it back with `space:"screenshot"` lands on the control that was rendered there.

A regression here shows up as a horizontal shift proportional to `x`: targets near `x=0` still hit, and
everything on the right half misses silently.

## Screenshot-Space Click Check (what the handler receives)

`space:"screenshot"` takes top-left-origin coordinates and the tools Y-flip them. The flip has to reach the
dispatched `PointerEventData`, not just the echoed numbers — a handler that reads `eventData.pressPosition`
(`OnPointerClick`, `OnBeginDrag`, `OnDrag`) acts on that field, so a half-applied flip produces a click that
reports success at the right coordinates and acts on the vertically mirrored point.

In Play Mode, click the lower half of the screen in screenshot space:

```json
{"tool":"ui-runtime-click","arguments":{"x":0.5,"y":0.75,"space":"screenshot","framework":"ugui"}}
```

Expected proof:

- The echoed `pos px` y is `0.25 * screenHeight` (top-left `0.75` flips to bottom-left `0.25`).
- The handler that runs sees `pressPosition == position ==` that same echoed point — compare against an
  equivalent `isNormalized` call at `y = 0.25`; both must act identically, not mirror each other.
- `pointerCurrentRaycast.gameObject`, `pointerPressRaycast.gameObject`, `pointerPress` and `rawPointerPress`
  are the clicked object, not null.

If `screenshot-game-view` misses Screen Space Overlay UI, capture `screenshot-editor-window` for the visible Game View and include the same center/disabled marker coordinates in notes.
