# Changelog

## 0.5.0 - 2026-06-09

### Added — 7 New Python Tools, Live Validation Script, Test Fixture

- **7 new `pro.*` Python tools**: `list_bookmarks`, `zoom_to_bookmark`, `reorder_layer`, `set_labels_enabled`, `open_dockpane`, `export_layout_to_file`, `fly_to_location`
- **Live validation script** (`test_live_addin.py`) — runs all 122 tools against live Pro, reports PASS/FAIL/SKIP with JSON report
- **CLI args**: `--fixture`, `--layer`, `--bookmark`, `--gdb`, `--gen-fixture`
- **Test fixture generator** (`scripts/create_test_fixture.py`) — GDB with 3 FCs + sample data
- **Upgraded `package-addin.ps1`** — deploys to both `Documents\ArcGIS\AddIns\ArcGISPro` (OneDrive) + `%LOCALAPPDATA%`

### Fixed — 5 Stubs Replaced With Real Implementations

| Handler | Before | After |
|---------|--------|-------|
| `listGpTools` | Empty list | Parses `.tbx`/`.atbx` XML → tool names with search |
| `listGpHistory` | Empty list | Reads `GeoprocessingHistory.xml` from project dir |
| `listSubtypes` | Hardcoded `{}` | Enumerates via `fcDef.GetSubtypes()` with field values |
| `getLayerDescription` | Returns `""` | Reads `CIMBasicFeatureLayer.Description` |
| `getElevationSources` | Empty list | Returns scene detection + elevation source count |

### Fixed — 5 "Not Accessible" Handlers → GP Tool Workarounds

| Handler | Workaround | Tool Used |
|---------|-----------|-----------|
| `addField` | GP `AddField` tool | `Geoprocessing.ExecuteToolAsync("AddField", ...)` |
| `deleteField` | GP `DeleteField` tool | `Geoprocessing.ExecuteToolAsync("DeleteField", ...)` |
| `addAttributeIndex` | GP `AddIndex` tool | `Geoprocessing.ExecuteToolAsync("AddIndex", ...)` |
| `importCsv` | arcpy `XYTableToPoint` via subprocess | `RunProPythonAsync()` |
| `importGeoJSON` | arcpy `JSONToFeatures` via subprocess | `RunProPythonAsync()` |

### Fixed — 9 NullReferenceException Crashes

- `panToExtent`, `getAllMapNames`, `getProjectProperties`, `listLayouts`, `getMapFrame`, `listLayoutElements`, `removeLayoutElement`, `exportLayoutToFile`, `saveProject`
- All now return clean "No project open" / "No active map view" instead of NullReferenceException

### Fixed — MessageBox Blocking Pipe Server

- `showMessage` and `showProgressDialog` now use `Dispatcher.InvokeAsync()` — non-blocking, no longer stalls the pipe server for subsequent calls

### Fixed — Pipe Persistence

- Root cause identified: ArcGIS Pro loaded stale add-in from OneDrive `Documents\ArcGIS\AddIns` folder instead of our `%LOCALAPPDATA%` deployment
- Switch to fresh `NamedPipeServerStream` per client (no `Disconnect()`/reuse) for reliable multi-connection handling
- Updated `package-addin.ps1` to deploy to both folders
- Updated DAML IDs for Pro 3.6 dockpanes (`esri_core_contentsDockPane`, etc.)

### Fixed — 13 Flaky Unit Tests

- Added proper `call_addin` mocks to unmocked `_unavailable` tests
- Added `setUp()` skip for add-in-available tests
- Filled 33 test gaps for 11 early tools (missing `unavailable`/`ok`/`error` tests)
- Added 30 new tests for the 7 new Python tools

### Changed
- `ProBridgeService.cs` — null guards on Project.Current/MapView.Active, non-blocking MessageBox, dockpane DAML ID updates, createFeatureClass GDB fallback, panToExtent SR fallback
- Version bumped from `0.4.0` to `0.5.0`
- 526 unit tests passing (467 passed + 59 skipped when Add-In available)
- Build: 0 errors (CS1998 warnings only)

## 0.4.0 - 2026-06-07

### Added — C# Add-In + 114 Real-Time ArcGIS Pro Tools (Phases 0–14)

- **C# Add-In project** (`addin/APBridgeAddIn/`) — Named Pipe IPC server for in-process ArcGIS Pro SDK access
- **Python Named Pipe client** (`arcgis_mcp_named_pipe.py`) — connects to Add-In with graceful fallback
- **114 `pro.*` MCP tools** across 14 phases:
  - **Base (11 tools)**: ping, get_active_map_name, list_layers, count_features, get_layer_schema, get_selection_count, select_by_attribute, clear_selection, zoom_to_layer, get_current_extent, pan_to_extent
  - **Phase 0 (10)**: get_camera, set_camera, set_layer_visibility, get_layer_extent, select_by_rectangle, switch_selection, get_feature_by_oid, undo_edit, redo_edit, set_active_tool
  - **Phase 1 (8)**: get_layer_renderer, set_layer_color, remove_layer, add_layer_from_file, select_by_polygon, list_layouts, get_project_properties, get_geometry_distance
  - **Phase 2 (8)**: is_3d, set_layer_transparency, get_all_map_names, get_map_frame, zoom_to_selected, flash_layer, get_elevation_sources, get_elevation_at_point
  - **Phase 3 (8)**: get_active_tool, list_field_values, add_field, delete_field, alter_field, list_attachments, get_attachment, add_attachment
  - **Phase 4 (10)**: delete_attachment, set_snapping, get_bookmarks, zoom_to_bookmark, create_bookmark, get_time_extent, set_time_extent, export_layout, list_layout_elements, set_layout_element_visibility
  - **Phase 5 (10)**: get_layer_description, set_layer_description, get_map_scale, set_map_scale, get_map_description, set_map_description, add_standalone_table, remove_standalone_table, get_all_standalone_tables, add_relate
  - **Phase 6 (10)**: split_features, merge_features, run_gp_tool, list_gp_tools, copy_features, rename_layer, get_layer_statistics, project_geometry
  - **Phase 7 (8)**: add_layout_text, add_layout_picture, add_layout_legend, add_layout_north_arrow, remove_layout_element, create_layout, create_map, add_basemap
  - **Phase 8 (6)**: set_atmosphere, set_sun_position, get_sun_position, explore_3d, set_layer_elevation, set_scene_background
  - **Phase 9 (6)**: create_feature_class, delete_feature_class, save_project, add_attribute_index, search_address, open_attribute_table
  - **Phase 10 (6)**: export_to_csv, export_to_geojson, import_csv, export_to_shapefile, export_to_kml, import_geojson
  - **Phase 11 (6)**: show_message, show_progress_dialog, set_status_bar_progress, list_dockpanes, activate_ribbon_tab, open_dockpane (enhanced)
  - **Phase 12 (6)**: list_domains, create_domain, assign_domain_to_field, list_subtypes, set_subtype_field, enable_attachments
  - **Phase 13 (6)**: list_toolboxes, describe_tool, get_geoprocessing_history, run_python_script, set_environment, get_environment
- **526 unit tests** with ruff-clean code

### Changed
- Version bumped from `0.1.0` to `0.4.0`
- `ProBridgeService.cs` handlers refactored from 37-case switch to `Dictionary<string, Func<...>>` pattern
- `README.md`, `AGENTS.md`, `FUTURE_WORK.md` updated with Add-In setup and full tool tables
- `pyproject.toml` — added `pywin32` dependency for Windows Named Pipe support
- `.csproj` — removed missing Add-In toolbar icon references (Add-In auto-starts, no button needed)
- Cleaned stale planning artifacts (`analysis_plan.txt`, `Build-plan.md`, `Extension plan.txt`, `Process reference.txt`, empty `docs/zh/`)

## 0.3.0 - 2026-05-18

### Added — Raster Analysis Tools (8 new MCP tools)
- `validate_project_data` — Pre-flight check for all required layers, CRS, coverage, and Spatial Analyst license
- `prepare_analysis_inputs` — One-shot data prep: clip vectors, resample DEM, derive slope, Euclidean distance rasters
- `reclassify_criteria` — Batch reclassify multiple rasters to 1-5 suitability scale with RemapRange
- `weighted_suitability` — Weighted Linear Combination using `arcpy.sa.WeightedSum` with weight validation and normalization
- `conflict_analysis` — Binary conflict map + allocation map with area statistics
- `raster_area_summary` — Area statistics by suitability class with optional CSV export
- `sensitivity_check` — One-at-a-time weight perturbation (±10%) + threshold variation analysis
- `export_suitability_map` — Publication-quality layout creation with PDF/PNG export

### Changed
- All raster tools use `arcpy.sa.WeightedSum` (not `WeightedOverlay`) for float weight support
- Snap raster alignment to `forest_prop` for consistent 30m cell output
- Default timeout for raster tools set to 600s; `prepare_analysis_inputs` uses 900s

## 0.2.0 - 2026-05-18

### Added
- OpenCode integration: `opencode.json` config, `AGENTS.md` agent instructions
- Path validation with `ARCGIS_MCP_ALLOWED_PATHS` for production sandboxing
- English translation for all Chinese documentation
- Windows setup guide (`WINDOWS_SETUP.md`) and test project generator
- Structured logging support (`arcgis_mcp_logging.py`)
- Test suite with mock `.aprx` archives and subprocess mocking

### Changed
- Improved `.aprx` archive reader performance for project inspection
- Hardened ArcGIS subprocess isolation (scrubs `PYTHONPATH`, `VIRTUAL_ENV`, Trae/UV vars)
- Enhanced error hints with actionable messages for common failures

### Security
- Added `opencode.json` to `.gitignore` to prevent leaking local paths
- Documented `execute_arcpy_code` permission gate in security notes

## 0.1.0 - 2026-04-04

- Initialize ArcGIS Pro Bridge MCP Server core capabilities.
- Support auto-discovery of ArcGIS Pro installation path and Python interpreter.
- Support executing ArcPy code via subprocess and collecting structured results.
- Provide MCP Tools / Resources for project layers, GDB schema, and `.aprx` project overview.
- Add examples, CI, contributing guide, and security notes required for GitHub release.
