# Known Limitations

This documents ArcGIS Pro SDK capabilities that are **not accessible** from the C# Add-In (`APBridgeAddIn`), and whether workarounds exist.

## Legend

| Icon | Meaning |
|------|---------|
| ❌ | Truly blocked — no SDK API or GP tool exists |
| ⚠️ | GP tool workaround exists via `Geoprocessing.ExecuteToolAsync()` |
| 🔶 | arcpy subprocess workaround exists via `RunProPythonAsync()` |
| ✅ | Fixed in a recent release |

---

## 3D & Scene Visualization (6)

| Handler | Status | Reason |
|---------|--------|--------|
| `pro.setAtmosphere` | ❌ Blocked | `CIMScene`, `CIMAtmosphereFog` types not in public `ArcGIS.Core.CIM` namespace |
| `pro.setSunPosition` | ❌ Blocked | `CIMSceneLight` not in public SDK |
| `pro.getSunPosition` | ❌ Blocked | `CIMSceneLight` not in public SDK |
| `pro.explore3D` | ❌ Blocked | Camera animation requires undocumented API |
| `pro.setLayerElevation` | ❌ Blocked | `CIMLayer` elevation properties not exposed |
| `pro.setSceneBackground` | ❌ Blocked | `CIMSceneBackground` not in public SDK |

## Layout Creation & Elements (0)

*(All 4 layout element handlers are now unblocked via arcpy subprocess — see "Previously Blocked" below)*

## Map Creation (0)

*(`pro.createMap` is now unblocked via Pro SDK `MapFactory.Instance.CreateMap()` — see "Previously Blocked" below)*

## Editing (1)

| Handler | Status | Reason |
|---------|--------|--------|
| `pro.splitFeatures` | ❌ Blocked | No `EditOperation.Split()` in Pro 3.6 SDK. arcpy geometry-based split possible but complex/fragile. |

## UI / GUI Automation (3)

| Handler | Status | Reason |
|---------|--------|--------|
| `pro.setStatusBarMessage` | ❌ Blocked | `FrameworkApplication.StatusBar` not available in Pro 3.6 SDK |
| `pro.setStatusBarProgress` | ❌ Blocked | StatusBar API not in public SDK |
| `pro.flashSelection` | ❌ Blocked | `MapView.FlashFeature()` not available |

## Time Slider (3)

| Handler | Status | Reason |
|---------|--------|--------|
| `pro.isTimeEnabled` | ❌ Blocked | Time slider state not exposed to Add-Ins |
| `pro.getTimeExtent` | ❌ Blocked | Time extent not readable from Add-In SDK |
| `pro.setTimeExtent` | ❌ Blocked | Time extent not settable from Add-In SDK |

## Data Import (2) — Workaround Exists

| Handler | Status | Workaround |
|---------|--------|------------|
| `pro.importCsv` | 🔶 arcpy workaround | `arcpy.management.XYTableToPoint()` via subprocess |
| `pro.importGeoJSON` | 🔶 arcpy workaround | `arcpy.conversion.JSONToFeatures()` via subprocess |

## Address Search (1) — Workaround Exists

| Handler | Status | Workaround |
|---------|--------|------------|
| `pro.searchAddress` | 🔶 arcpy workaround | `arcpy.geocoding.GeocodeAddresses()` via subprocess |

## Other Blocked (1)

| Handler | Status | Reason |
|---------|--------|--------|
| `pro.getEditState` | ❌ Blocked | `EditOperation.UndoCount`/`RedoCount` not available in SDK |

---

## Previously Blocked — Now Fixed

These handlers were previously blocked but have been fixed:

| Handler | Fixed In | Fix |
|---------|----------|-----|
| `pro.addField` | v0.5.0 | GP tool: `AddField` via `Geoprocessing.ExecuteToolAsync()` |
| `pro.deleteField` | v0.5.0 | GP tool: `DeleteField` |
| `pro.addAttributeIndex` | v0.5.0 | GP tool: `AddIndex` |
| `pro.createDomain` | v0.5.0 | GP tool: `CreateDomain` |
| `pro.listDomains` | v0.5.0 | SDK: `gdb.GetDomains()` |
| `pro.getGeoprocessingHistory` | v0.5.0 | XML: reads `GeoprocessingHistory.xml` |
| `pro.getLayerDescription` | v0.5.0 | SDK: `CIMBasicFeatureLayer.Description` |
| `pro.createLayout` | v0.5.1 | arcpy: `arcpy.management.CreateLayout()` via subprocess |
| `pro.addBasemap` | v0.5.1 | arcpy: `Map.addBasemap()` via subprocess |
| `pro.listStandaloneTables` | v0.5.3 | SDK: `MapView.Active.Map.StandaloneTables` |
| `pro.setLayerDescription` | v0.5.3 | SDK: `GetDefinition()`/`Clone()`/`SetDefinition()` CIM modification |
| `pro.createBookmark` | v0.5.3 | SDK: `CIMBookmark` + `Map.AddBookmark()` |
| `pro.openAttributeTable` | v0.5.3 | SDK: `FrameworkApplication.DockPaneManager.Find("esri_mapping_tableWindow")` |
| `pro.addLayoutText` | v0.5.3 | arcpy: `layout.createTextElement()` via subprocess |
| `pro.addLayoutPicture` | v0.5.3 | arcpy: `layout.createPictureElement()` via subprocess |
| `pro.addLayoutLegend` | v0.5.3 | arcpy: `layout.createMapSurroundElement(mf, 'LEGEND')` via subprocess |
| `pro.addLayoutNorthArrow` | v0.5.3 | arcpy: `layout.createMapSurroundElement(mf, 'NORTH_ARROW')` via subprocess |
| `pro.setEnvironment` | v0.5.2 | In-process Python: `arcpy.env.{key} = value` via `Py.GIL()` |
| `pro.getEnvironment` | v0.5.2 | In-process Python: `arcpy.env.{key}` via `Py.GIL()` |
| `pro.createMap` | v0.5.3 | SDK: `MapFactory.Instance.CreateMap()` on QueuedTask |
| `pro.mergeFeatures` | v0.5.3 | arcpy: `da.SearchCursor` + `geometry.union()` + `da.UpdateCursor` via subprocess |
| `pro.addBasemap` (CURRENT fix) | v0.5.3 | Fixed: `ArcGISProject('CURRENT')` → `ArcGISProject(proj_path)` with explicit path |

---

## Summary

| Category | Count |
|----------|-------|
| ❌ Truly blocked (no workaround) | ~14 |
| ⚠️ GP tool workaround exists | 0 remaining (all implemented) |
| 🔶 arcpy subprocess workaround | 8 (importCsv, importGeoJSON, searchAddress, mergeFeatures, addLayoutText, addLayoutPicture, addLayoutLegend, addLayoutNorthArrow) |
| ✅ Fixed via Pro SDK (no workaround needed) | 1 (createMap) |

The 14 truly blocked handlers represent genuine limitations of the ArcGIS Pro 3.6 public SDK. They cannot be implemented without either:
1. Esri adding the APIs to a future SDK release
2. Using undocumented/private APIs (not recommended — unstable across versions)
3. Automating the Pro GUI (fragile, slow)
