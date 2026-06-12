# ArcGIS Pro Agent Instructions

You are working with an ArcGIS Pro project through a local MCP server named `arcgis-pro`. Use the MCP tools described below to query map context, inspect GIS data, and generate ArcPy scripts.

## Important: Use MCP Tools, Not Shell

**DO NOT use shell commands** to explore ArcGIS projects or data. Use the MCP tools instead. The permission gate will prompt you before each tool call.

## Two Modes of Operation

The server has two modes. Choose based on whether the C# Add-In is available:

| Mode | Tools | When to Use | Requires |
|------|-------|-------------|----------|
| **File-Based** | 21 file-based tools total | Batch geoprocessing, automated scripts, when Pro is closed or you only need disk access | Nothing extra |
| **Real-Time (Add-In)** | `pro_ping`, `pro_list_layers`, `pro_select_by_attribute`, `pro_create_point_feature`, etc. (138 tools) | Live map interaction, editing, selection, navigation, dynamic visualization | APBridgeAddIn installed + Pro running + Named Pipe connected |

Check viability: `pro_ping` returns `"status": "ok"` if Add-In is available, `"status": "unavailable"` otherwise.

## Recommended Workflow

### 1. Start with Diagnostics

Always begin with these calls in order:

```
ping -> doctor -> detect_arcgis_environment
```

This confirms:
- MCP connectivity is working
- ArcGIS Pro Python and ArcPy are accessible
- The agent environment is properly configured

### 2. Inspect Project Context

After diagnostics, use these tools to understand the project:

```
inspect_project_context(aprx_path="path/to/project.aprx")
list_gis_layers(aprx_path="path/to/project.aprx")
inspect_gdb(gdb_path="path/to/data.gdb")
```

### 3. For Specific Tasks

- **Read layer schema:** `inspect_project_context` with `include_source_details=true`
- **Read GDB structure:** `inspect_gdb`
- **Generate buffer:** `buffer_features`
- **Generate clip:** `clip_features`
- **Execute ArcPy code:** `execute_arcpy_code` (requires user confirmation)

### 4. (Optional) Real-Time ArcGIS Pro Interaction

If the **APBridgeAddIn** is installed in ArcGIS Pro, use `pro.*` tools to interact with the live session. 138 tools are available across 14+ categories:

**Map & Selection (Base)**
- `pro_ping`, `pro_get_active_map_name`, `pro_list_layers`
- `pro_select_by_attribute(layer="Parcels", where="ZONE = 'Residential'")`
- `pro_zoom_to_layer(layer="Parcels")`, `pro_pan_to_extent(...)`
- `pro_get_current_extent()`, `pro_count_features(layer="Parcels")`

**Editing (Phase 0, 6)**
- `pro_split_features(layer, geometry)`, `pro_merge_features(layer, object_ids, target_oid)`
- `pro_create_point_feature(x, y, attributes?)`
- `pro_delete_features_by_oid(layer, "1,2,3")`, `pro_update_feature_attributes(layer, oid, attributes)`
- `pro_undo_edit()`, `pro_redo_edit()`

**Schema Management (Phase 3, 10, 13)**
- `pro_add_field(layer, field_name, field_type)`, `pro_delete_field(layer, field)`
- `pro_create_feature_class(gdb_path, name, geometry_type)`
- `pro_list_domains(gdb_path)`, `pro_create_domain(gdb_path, name, ...)`
- `pro_assign_domain_to_field(layer, field, domain_name)`
- `pro_enable_attachments(layer)`

**3D & Visualization (Phase 1, 2, 9)**
- `pro_get_camera()`, `pro_explore_3d(x, y, z, distance)`
- `pro_set_atmosphere(fog_density)`, `pro_set_sun_position(azimuth, altitude)`
- `pro_set_layer_elevation(layer, mode, z_offset)`, `pro_set_scene_background(r, g, b)`

**Layout Automation (Phase 8)**
- `pro_create_layout(name, width, height)`, `pro_create_map(name, map_type)`
- `pro_add_layout_text(layout, text, x, y)`, `pro_add_layout_legend(layout, map_frame)`
- `pro_export_layout(layout, path, format)`

**Data Exchange (Phase 11)**
- `pro_export_to_csv(layer, path)`, `pro_export_to_geo_json(layer, path)`
- `pro_import_csv(path, layer_name)`, `pro_import_geo_json(path, layer_name)`

**GP & Python (Phase 7, 14)**
- `pro_run_gp_tool(tool_name, parameters)`, `pro_describe_tool(tool_name)`
- `pro_set_environment(key, value)`, `pro_get_environment(key?)`
- `pro_run_python_script(code)`, `pro_get_geoprocessing_history(count?)`

**GUI Automation (Phase 12)**
- `pro_show_message(message, type)`, `pro_activate_ribbon_tab(tab_id)`
- `pro_open_dockpane(name)`, `pro_list_dockpanes()`

> The `pro_ping` tool will return `"status": "unavailable"` with setup instructions if the Add-In is not reachable.

## Tool Reference

| Tool | Purpose | Key Parameters |
|------|---------|----------------|
| `ping` | MCP connectivity check | None |
| `doctor` | Full diagnostic report | None |
| `detect_arcgis_environment` | ArcGIS Python discovery | None |
| `inspect_project_context` | Full project overview | `aprx_path`, `include_source_details` |
| `list_gis_layers` | List all layers | `aprx_path` |
| `inspect_gdb` | GDB schema inspection | `gdb_path` |
| `buffer_features` | Buffer GP tool | `input_features`, `output_path`, `buffer_distance` |
| `clip_features` | Clip GP tool | `input_features`, `clip_features`, `output_path` |
| `execute_arcpy_code` | Run arbitrary ArcPy | `code`, `timeout_seconds` |

**138 `pro.*` Add-In tools across 18 categories** — see the `README.md` for the full table. Quick reference by category:

| Category | Phase | Example Tools |
|----------|-------|---------------|
| Map & Selection | Base | `pro_ping`, `pro_list_layers`, `pro_select_by_attribute`, `pro_zoom_to_layer` |
| Editing | 0, 6 | `pro_undo_edit`, `pro_split_features`, `pro_create_point_feature`, `pro_merge_features` |
| Schema | 3, 10, 13 | `pro_add_field`, `pro_create_feature_class`, `pro_list_domains`, `pro_enable_attachments` |
| 3D & Viz | 1, 2, 9 | `pro_get_camera`, `pro_set_atmosphere`, `pro_explore_3d`, `pro_set_sun_position` |
| Layouts | 8 | `pro_create_layout`, `pro_add_layout_text`, `pro_export_layout` |
| Data Exchange | 11 | `pro_export_to_csv`, `pro_import_geo_json`, `pro_export_to_shapefile` |
| GP & Python | 7, 14 | `pro_run_gp_tool`, `pro_run_python_script`, `pro_set_environment` |
| GUI Automation | 12 | `pro_show_message`, `pro_activate_ribbon_tab`, `pro_open_dockpane` |
| Plugin Tools | — | `pro_plugin_batch_export`, `pro_plugin_feature_inspector`, `pro_plugin_query_builder` |
| Workflow Macros | — | `pro_run_macro`, `pro_list_macros` |
| Layer Resolution | — | `pro_resolve_layer` |

## Resources

The server also exposes these MCP Resources:

| URI | Purpose |
|-----|---------|
| `arcgis://server/status` | Server and ArcGIS status |
| `arcgis://project/current/layers` | Current project layers |
| `arcgis://project/{project_ref}/layers` | Specific project layers |
| `arcgis://gdb/{gdb_ref}/schema` | GDB schema |

## Safety Rules

1. **Read-only by default.** The `execute_arcpy_code` tool requires explicit user confirmation.
2. **Use `.aprx` paths.** `ArcGISProject("CURRENT")` only works inside ArcGIS Pro's Python window. Pass explicit paths.
3. **Close ArcGIS Pro before write operations.** `arcpy.mp.ArcGISProject` will not modify an `.aprx` locked by an open Pro session.
4. **Scratch workspace.** The server creates `scratch.gdb` in the project home folder to avoid temp directory issues.

## Conventions

- Always prefer project-relative paths via `project.homeFolder` and `project.defaultGeodatabase`
- Set `arcpy.env.overwriteOutput = True` in generated scripts unless user objects
- For destructive operations, warn user before executing
- Use `inspect_project_context` to get real layer/field names before generating ArcPy code

## Plugin System

Custom `pro.*` handlers can be added as separate C# projects under `addin/plugins/` without modifying `ProBridgeService.cs`.

**5 cookbook plugins** in `addin/plugins/`:

| Plugin | Tool | Description |
|--------|------|-------------|
| `BatchExportPlugin` | `pro.plugin.batchExport` | Export all layers to CSV/GeoJSON/SHP/KML |
| `CoordinateCapturePlugin` | `pro.plugin.coordinateCapture` | Capture map center coords + reproject |
| `FeatureInspectorPlugin` | `pro.plugin.featureInspector` | All attributes + geometry by OID |
| `QueryBuilderPlugin` | `pro.plugin.queryBuilder` | SQL query builder (equals/contains/gt/lt) |
| `FieldCalculatorPlugin` | `pro.plugin.fieldCalculator` | Calculate field via arcpy subprocess |
| `ExamplePlugin` | `pro.plugin.hello`, `pro.plugin.mapInfo` | Starter template |

**Workflow:**

1. Copy `addin/plugins/ExamplePlugin/` → rename
2. Write handler class implementing `IProBridgeHandler` with `[ProBridgeHandler("pro.yourTool")]`
3. `dotnet build your-plugin.csproj`
4. `package-addin.ps1 -Action install` — auto-discovers and bundles all plugins

See `docs/plugin-handler-system.md` for the full guide.

## Workflow Macros

Run named multi-step sequences with `pro_run_macro`. Built-in macros in `macros/`:

| Macro | Steps |
|-------|-------|
| `Select and Zoom` | Select by attribute → Zoom to selected |
| `Export All Layers` | Batch export all layers to CSV |
| `Inspect Feature` | Select by OID → Zoom → Inspect attributes |
| `Capture and Project` | Capture map center → Reproject |

You can also pass inline JSON or a file path to `pro_run_macro`.

## Limitations

- The server cannot click around the ArcGIS Pro GUI
- `arcpy.mp.ArcGISProject("CURRENT")` is unavailable from outside Pro's Python window
- `open_current_project=True` will fail with a clear error — always provide an explicit `.aprx` path
- If Pro is open on the same `.aprx`, write operations will fail with a lock error
- The `pro.*` tools require the APBridgeAddIn to be installed and ArcGIS Pro to be running
- `test_project/` is excluded from git — run `python test_live_addin.py --gen-fixture` to auto-build the test fixture (GDB + `.aprx`) using Pro's Python. Requires ArcGIS Pro installed and **closed** (file locked if open).

### Working with Projects

| Scenario | Approach |
|----------|----------|
| ArcGIS Pro is **closed**, need data | **File-Based mode:** `inspect_project_context(project_path="C:\\path\\to\\project.aprx")` |
| ArcGIS Pro is **open**, need to **edit data** | **File-Based mode:** Save project first, close Pro, then use `execute_arcpy_code` or `project_path=` |
| ArcGIS Pro is **open**, need **live interaction** | **Real-Time mode:** `pro.*` tools via Named Pipe (Add-In required) |
| Heavy geoprocessing / batch | **File-Based mode:** `execute_arcpy_code` with timeout — no Add-In needed |
| Need **real-time** access | Requires C# Add-In (see `addin/` directory) |