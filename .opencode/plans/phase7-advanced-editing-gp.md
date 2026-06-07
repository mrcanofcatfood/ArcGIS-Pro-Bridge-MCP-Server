# Phase 7: Advanced Editing & GP Execution

## Tools (8)

### 1. `pro.splitFeatures`
Split features in a layer using a cutting polyline geometry.
- Args: `layer`, `cutCoordinates` (space-separated x,y pairs), `wkid` (optional)
- SDK: `EditOperation.Split(featureLayer, geometry)`

### 2. `pro.mergeFeatures`
Merge selected features in a layer into one feature.
- Args: `layer`, `targetOid` (keep this feature's attributes)
- SDK: `EditOperation.Merge(featureLayer, oids, targetOid)`

### 3. `pro.runGpTool`
Execute an arbitrary geoprocessing tool synchronously.
- Args: `toolName` (e.g. "Buffer"), `parameters` (JSON string of tool params)
- SDK: `Geoprocessing.ExecuteAsync(toolName, values)`

### 4. `pro.listGpTools`
Search for geoprocessing tools by name pattern.
- Args: `searchText` (optional), `maxResults` (default 20)
- SDK: `Geoprocessing.GetToolboxItemsAsync()`

### 5. `pro.copyFeatures`
Copy selected features or entire layer to a new feature class.
- Args: `layer`, `outputPath`, `where` (optional, default "1=1")
- SDK: `EditOperation.Create` + `fc.Search()`

### 6. `pro.renameLayer`
Rename a layer in the TOC.
- Args: `layer`, `newName`
- SDK: `Layer.Name = newName`

### 7. `pro.getLayerStatistics`
Compute basic statistics (min, max, mean, stddev, count, nulls) for a numeric field.
- Args: `layer`, `field`
- SDK: `DataStatistics` or manual `fc.Search()` aggregation

### 8. `pro.projectGeometry`
Project coordinates between spatial references.
- Args: `x`, `y`, `fromWkid`, `toWkid`
- SDK: `GeometryEngine.Project(vector, targetSR)`

## Files Modified
- `addin/APBridgeAddIn/ProBridgeService.cs` — +8 dict entries, +8 handlers
- `arcgis_mcp_server.py` — +8 `@mcp.tool()` functions
- `tests/test_arcgis_mcp_server.py` — +32 tests

## Test Count
271 → **303** (Phase 6 relocated Base to 311; Phase 7 adds 32 → **343**)
