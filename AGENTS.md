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

If the **APBridgeAddIn** is installed in ArcGIS Pro, use `pro.*` tools to interact with the live session:

- **Check connectivity:** `pro_ping`
- **Get active map:** `pro_get_active_map_name`
- **List layers:** `pro_list_layers`
- **Count features:** `pro_count_features(layer="Parcels")`
- **Get layer schema:** `pro_get_layer_schema(layer="Parcels")`
- **Select features:** `pro_select_by_attribute(layer="Parcels", where="ZONE = 'Residential'")`
- **Zoom to layer:** `pro_zoom_to_layer(layer="Parcels")`
- **Get extent:** `pro_get_current_extent()`
- **Pan to extent:** `pro_pan_to_extent(xmin=500000, ymin=6900000, xmax=510000, ymax=6910000)`
- **Clear selection:** `pro_clear_selection(layer="Parcels")`
- **Get selection count:** `pro_get_selection_count(layer="Parcels")`

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
| `pro_ping` | Ping Add-In pipe | None |
| `pro_get_active_map_name` | Get active map name | None |
| `pro_list_layers` | List all layers | None |
| `pro_count_features` | Count features in layer | `layer` |
| `pro_get_layer_schema` | Get field schema | `layer` |
| `pro_select_by_attribute` | Select by SQL | `layer`, `where` |
| `pro_zoom_to_layer` | Zoom to layer extent | `layer` |
| `pro_get_current_extent` | Get map view extent | None |
| `pro_pan_to_extent` | Pan to bounding box | `xmin`, `ymin`, `xmax`, `ymax` |
| `pro_clear_selection` | Clear layer selection | `layer` (optional) |
| `pro_get_selection_count` | Count selected features | `layer` |

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

### Working with Projects

| Scenario | Approach |
|----------|----------|
| ArcGIS Pro is **closed** | Use `project_path="C:\\path\\to\\project.aprx"` — reads from disk |
| ArcGIS Pro is **open** (read-only) | Use `.aprx` archive reader — no live session access |
| ArcGIS Pro is **open** (write) | Save project first, close Pro, then use `project_path` |
| Need **real-time** access | Requires C# Add-In (see `addin/` directory) |