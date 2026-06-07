# Phase 10: Project & Data Management

## Tools (6)

### 1. `pro.createFeatureClass`
Create a new feature class in a geodatabase.
- Args: `gdbPath`, `name`, `geometryType` (Point, Polyline, Polygon), `wkid` (optional), `fieldsJson` (optional field definitions)
- SDK: `SchemaBuilder.CreateFeatureClassAsync(workspace, name, fields, ...)`

### 2. `pro.deleteFeatureClass`
Delete a feature class or table.
- Args: `path` (full path to feature class)
- SDK: `Geoprocessing.ExecuteAsync("Delete", path)` or `workspace.Delete()`

### 3. `pro.saveProject`
Save the current ArcGIS Pro project.
- Args: none
- SDK: `Project.Current.Save()`

### 4. `pro.addAttributeIndex`
Add an attribute index on a field for faster queries.
- Args: `layer`, `field`, `indexName` (optional), `unique` (bool, optional)
- SDK: `TableDefinition.AddIndex(field, name, unique)`

### 5. `pro.searchAddress`
Search for an address or place using a locator.
- Args: `address`, `locatorPath` (optional, uses project default if omitted), `maxResults` (optional)
- SDK: `LocatorManager.FindAddressCandidates(address, locator)`

### 6. `pro.openAttributeTable`
Open the attribute table view for a layer.
- Args: `layer`
- SDK: Activate table dockpane via `DockPaneManager.Find("esri_mapping_tableWindow")` and associate with layer

## Implementation Notes
- `SchemaBuilder` is in `ArcGIS.Core.Data` namespace (already imported)
- Locator usage requires `ArcGIS.Desktop.Core.LocatorManager` or `ArcGIS.Location`
- `openAttributeTable` may require selecting features from the layer first, or using internal DAML activation
- `saveProject` should warn user to close Pro if they plan to use `.aprx` write tools

## Files Modified
- `addin/APBridgeAddIn/ProBridgeService.cs` — +6 dict entries, +6 handlers
- `arcgis_mcp_server.py` — +6 `@mcp.tool()` functions
- `tests/test_arcgis_mcp_server.py` — +24 tests

## Test Count
399 → **423**
