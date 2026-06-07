# ArcGIS Pro Agent Instructions

You are working with an ArcGIS Pro project through a local MCP server named `arcgis-pro`. Use the MCP tools described below to query map context, inspect GIS data, and generate ArcPy scripts.

## Important: Use MCP Tools, Not Shell

**DO NOT use shell commands** to explore ArcGIS projects or data. Use the MCP tools instead. The permission gate will prompt you before each tool call.

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

If the **APBridgeAddIn** is installed in ArcGIS Pro, use `pro.*` tools to interact with the live session. 126 tools are available across 14 phases:

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
- `pro_get_camera()`, `pro_set_camera(x, y, z)`, `pro_explore_3d(x, y, z, distance)`
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

**126 `pro.*` Add-In tools across 14 phases** — see the `README.md` for the full table. Quick reference by category:

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

## Limitations

- The server cannot click around the ArcGIS Pro GUI
- `arcpy.mp.ArcGISProject("CURRENT")` is unavailable from outside Pro's Python window
- `open_current_project=True` will fail with a clear error — always provide an explicit `.aprx` path
- If Pro is open on the same `.aprx`, write operations will fail with a lock error
- The `pro.*` tools require the APBridgeAddIn to be installed and ArcGIS Pro to be running
- `test_project/` is excluded from git — create your own test .aprx and .gdb locally

### Working with Projects

| Scenario | Approach |
|----------|----------|
| ArcGIS Pro is **closed** | Use `project_path="C:\\path\\to\\project.aprx"` — reads from disk |
| ArcGIS Pro is **open** (read-only) | Use `.aprx` archive reader — no live session access |
| ArcGIS Pro is **open** (write) | Save project first, close Pro, then use `project_path` |
| Need **real-time** access | Requires C# Add-In (see `addin/` directory) |