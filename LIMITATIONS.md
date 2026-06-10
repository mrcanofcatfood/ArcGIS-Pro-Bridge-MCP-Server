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

## Layout Creation & Elements (5)

| Handler | Status | Reason |
|---------|--------|--------|
| `pro.createLayout` | ❌ Blocked | No `LayoutFactory` or GP tool for layout creation |
| `pro.addLayoutText` | ❌ Blocked | No `LayoutElementFactory` for creating text elements |
| `pro.addLayoutPicture` | ❌ Blocked | No API for creating picture elements |
| `pro.addLayoutLegend` | ❌ Blocked | No API for creating legend elements |
| `pro.addLayoutNorthArrow` | ❌ Blocked | No API for creating north arrow elements |

## Map & Basemap (2)

| Handler | Status | Reason |
|---------|--------|--------|
| `pro.createMap` | ❌ Blocked | `MapFactory.CreateMap()` requires undocumented parameters |
| `pro.addBasemap` | ❌ Blocked | `BasemapFactory` not available; `Map.SetBasemap()` doesn't exist |

## Editing (2)

| Handler | Status | Reason |
|---------|--------|--------|
| `pro.splitFeatures` | ❌ Blocked | No `EditOperation.Split()` in Pro 3.6 SDK |
| `pro.mergeFeatures` | ❌ Blocked | No `EditOperation.Merge()` in Pro 3.6 SDK |

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

## Other Blocked (5)

| Handler | Status | Reason |
|---------|--------|--------|
| `pro.openAttributeTable` | ❌ Blocked | No SDK API to open table views programmatically |
| `pro.listStandaloneTables` | ❌ Blocked | `Map.StandaloneTables` not exposed to Add-Ins |
| `pro.createBookmark` | ❌ Blocked | `Map.AddBookmark()` or `BookmarkFactory` not available |
| `pro.setLayerDescription` | ❌ Blocked | Layer description not settable via public API (get works via CIM) |
| `pro.getEditState` | ❌ Blocked | `EditOperation.UndoCount`/`RedoCount` not available in SDK |

## Environment Settings (2)

| Handler | Status | Reason |
|---------|--------|--------|
| `pro.setEnvironment` | ❌ Blocked | `Geoprocessing.SetEnvironmentValue()` doesn't exist in SDK |
| `pro.getEnvironment` | ❌ Blocked | `Geoprocessing.GetEnvironmentValues()` doesn't exist in SDK |

---

## Previously Blocked — Now Fixed

These handlers were previously blocked but have been fixed in v0.5.0+:

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

---

## Summary

| Category | Count |
|----------|-------|
| ❌ Truly blocked (no workaround) | ~24 |
| ⚠️ GP tool workaround exists | 0 remaining (all implemented) |
| 🔶 arcpy subprocess workaround | 3 (importCsv, importGeoJSON, searchAddress) |

The 24 truly blocked handlers represent genuine limitations of the ArcGIS Pro 3.6 public SDK. They cannot be implemented without either:
1. Esri adding the APIs to a future SDK release
2. Using undocumented/private APIs (not recommended — unstable across versions)
3. Automating the Pro GUI (fragile, slow)
