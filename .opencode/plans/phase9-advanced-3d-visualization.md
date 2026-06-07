# Phase 9: Advanced 3D & Visualization

## Tools (6)

### 1. `pro.setAtmosphere`
Set atmospheric effects in a scene (fog, haze).
- Args: `fogDensity` (0-100), `horizonFog` (bool, optional), `fogColor` (RGB, optional)
- SDK: Scene atmosphere via `CIMScene` definition → `map.SetDefinition()`

### 2. `pro.setSunPosition`
Set sun/lighting position in a scene.
- Args: `azimuth` (0-360), `altitude` (0-90)
- SDK: Scene lighting via `CIMSceneLighting` on the scene definition

### 3. `pro.getSunPosition`
Get current sun azimuth and altitude.
- Args: none
- SDK: Read `CIMSceneLighting` from scene definition

### 4. `pro.explore3D`
Orbit/navigate camera around a point in a scene.
- Args: `x`, `y`, `targetZ`, `distance`, `headingDelta` (optional), `pitchDelta` (optional)
- SDK: `Camera` manipulation with `ZoomToAsync()`

### 5. `pro.setLayerElevation`
Set elevation offset for a layer in a scene.
- Args: `layer`, `elevationMode` (absolute, relative), `zOffset` (meters)
- SDK: Layer elevation properties via `Layer.SetElevation(...)` or CIM

### 6. `pro.setSceneBackground`
Set scene background color/type.
- Args: `r`, `g`, `b` (0-255), `backgroundType` ("color", "sky", "none", optional)
- SDK: Scene background via `CIMSceneBackground`

## Implementation Notes
- Scene visual properties are stored in the `CIMScene` definition
- Pattern: `map.GetDefinition()` → cast to `CIMScene` → modify → `map.SetDefinition()`
- `CIMScene` has `Lighting`, `Background`, `Atmosphere` properties
- These tools only work when the active map is a scene (GlobalScene or LocalScene)

## Files Modified
- `addin/APBridgeAddIn/ProBridgeService.cs` — +6 dict entries, +6 handlers
- `arcgis_mcp_server.py` — +6 `@mcp.tool()` functions
- `tests/test_arcgis_mcp_server.py` — +24 tests

## Test Count
375 → **399**
