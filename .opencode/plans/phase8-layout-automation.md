# Phase 8: Layout & Map Automation

## Tools (8)

### 1. `pro.addLayoutText`
Add a text element to a layout at a position.
- Args: `layoutName`, `text`, `x`, `y` (page units), `fontSize` (optional), `colorRgb` (optional)
- SDK: `CIMTextGraphic` → `layout.AddElement()`

### 2. `pro.addLayoutPicture`
Add a picture/image element from a file path.
- Args: `layoutName`, `imagePath`, `x`, `y`, `width`, `height`
- SDK: `CIMMarker` or `CIMPICTURE` → picture element construction

### 3. `pro.addLayoutLegend`
Add a legend element for a map frame.
- Args: `layoutName`, `mapFrameName` (optional), `x`, `y`
- SDK: `Legend` creation → `layout.AddElement()`

### 4. `pro.addLayoutNorthArrow`
Add a north arrow surround next to a map frame.
- Args: `layoutName`, `mapFrameName`, `x`, `y`, `style` (optional)
- SDK: `MapSurround` (north arrow) creation → `layout.AddElement()`

### 5. `pro.removeLayoutElement`
Remove an element from a layout by name.
- Args: `layoutName`, `elementName`
- SDK: `layout.DeleteElement(element)`

### 6. `pro.createLayout` 
Create a new layout in the project.
- Args: `layoutName`, `width`, `height`, `units` (optional, default "MM")
- SDK: `LayoutFactory.CreateLayout(project, width, height, units)`

### 7. `pro.createMap`
Create a new map in the project.
- Args: `mapName`, `mapType` (Map, LocalScene, GlobalScene), `basemap` (optional)
- SDK: `MapFactory.CreateMap(mapName, mapType)`

### 8. `pro.addBasemap`
Add a basemap layer to the active map.
- Args: `basemapName` (e.g. "Streets", "Imagery", "Topographic")
- SDK: `LayerFactory.CreateBasemapLayer(basemapName, map)`

## Implementation Notes
- Adding layout elements requires CIM manipulation (`layout.GetDefinition()` → modify → `layout.SetDefinition()`)
- For `addLayoutText`, the CIM route: `CIMTextGraphic` with `CIMTextSymbol`
- For `addLayoutPicture`, use `CIMPictureElement` with `CIMRasterData` for file-based images
- `MapFactory` and `LayoutFactory` are in `ArcGIS.Desktop.Mapping`

## Files Modified
- `addin/APBridgeAddIn/ProBridgeService.cs` — +8 dict entries, +8 handlers
- `arcgis_mcp_server.py` — +8 `@mcp.tool()` functions
- `tests/test_arcgis_mcp_server.py` — +32 tests

## Test Count
343 → **375**
