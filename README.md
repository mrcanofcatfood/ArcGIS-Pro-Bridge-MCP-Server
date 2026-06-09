# ArcGIS Pro Bridge MCP Server

A local MCP server that connects AI agents (OpenCode, Trae, Cursor, Claude Desktop) to ArcGIS Pro, enabling AI-assisted GIS workflows in **two modes**.

## Architecture: Two Modes, One Server

### The Challenge with the Core Architecture

The server's primary method for executing code — the `execute_arcpy_code` tool — operates on a **file-based approach**. It reads `.aprx` project files directly from disk and launches ArcPy Python code in a new subprocess for execution. This design is why write operations require ArcGIS Pro to be closed: the server cannot easily interact with a live, running instance of the software.

### The Solution: The C# Add-In for Real-Time Control

To overcome this limitation, the project includes a separate, optional component: a **C# Add-In named APBridgeAddIn**. This Add-In acts as a direct communication bridge, allowing the MCP server to send commands to an active ArcGIS Pro session.

**How it connects:** The Add-In runs inside ArcGIS Pro and creates a Named Pipe server. The MCP server connects to this pipe, enabling instant communication — similar to how CLI-Anything-ArcGIS-Pro exposes an open project over a local socket.

**What it enables:** Once installed, the MCP server gains **126 `pro.*` tools** for interactive capabilities:

- **Map & Selection:** Query live map state, select features, zoom/pan to layers
- **Editing:** Create, split, merge, and delete features; undo/redo edits
- **Schema Management:** Add/delete fields, create feature classes, manage domains
- **3D & Visualization:** Control cameras, 3D exploration, scene backgrounds
- **Layout Automation:** Create and manage map layouts
- **Data Exchange:** Export to CSV, GeoJSON, shapefile, KML; import via arcpy

### Switching Between Modes

| Mode | Tools | When to Use | Limitations |
|------|-------|-------------|-------------|
| **File-Based** | `inspect_project_context`, `execute_arcpy_code`, `buffer_features`, `clip_features`, etc. | Heavy geoprocessing, batch operations, automated CI/CD, when Pro is closed or under load | Write ops require Pro closed; no live map state |
| **Real-Time (Add-In)** | All 126 `pro.*` tools | Interactive selection, feature editing, live map inspection, dynamic navigation | Requires Add-In installation + Pro running + Named Pipe connection |

Use the **file-based** tools when you need to process data without the Pro GUI open. Use the **Add-In** tools when you need real-time feedback from a live Pro session.

## Prerequisites

- Windows with ArcGIS Pro installed
- Python 3.11 or higher
- `uv` package manager (recommended) or `pip`
- For real-time Add-In features: Visual Studio 2022 with ArcGIS Pro SDK for .NET (one-time build)

## Quickstart

### 1. Install Dependencies

```bash
uv sync
# Or with pip:
pip install -e .
```

### 2. Configure OpenCode

Edit `opencode.json` and set your Python path:

```json
{
  "mcp": {
    "arcgis-pro": {
      "command": [
        "C:\\Program Files\\ArcGIS\\Pro\\bin\\Python\\envs\\arcgispro-py3\\python.exe",
        "C:\\path\\to\\arcgis-opencode-mcp\\arcgis_mcp_server.py"
      ]
    }
  }
}
```

### 3. Start OpenCode

```bash
opencode
```

Run `/mcp` to verify `arcgis-pro` shows Connected.

### 4. Mode 1: File-Based Tools (No Add-In Needed)

Ask OpenCode anything that reads `.aprx` files from disk:
> Run a health check, then summarize the parcels layer in my project.

For geoprocessing, use `execute_arcpy_code` with explicit `.aprx` paths:
> Buffer the parcels layer by 100 meters using the project at C:\data\project.aprx.

**Note:** Write operations via this mode require ArcGIS Pro to be closed (the `.aprx` file must not be locked).

### 5. Mode 2: Real-Time Add-In Tools (Optional, for Live Access)

Build and install the C# Add-In to enable live interaction with the active ArcGIS Pro session:

1. Open `addin/APBridgeAddIn/APBridgeAddIn.csproj` in **Visual Studio 2022** with ArcGIS Pro SDK installed
2. Build the solution (produces `APBridgeAddIn.esriAddInX`)
3. Double-click the `.esriAddInX` file to install into ArcGIS Pro
4. Start (or restart) ArcGIS Pro — the Named Pipe bridge starts automatically

After installation, all `pro.*` tools work against the live Pro session. Try:
> List the layers in the active map, then select all parcels with ZONE = 'Residential'.

The `pro_ping` tool returns `"unavailable"` with setup instructions if the Add-In is not reachable.

## Live Validation

A `test_live_addin.py` script runs all 126 `pro.*` tools against a live Pro session and reports PASS/FAIL/SKIP:

```bash
# With a test fixture project:
python test_live_addin.py --fixture

# Or with custom layer names:
python test_live_addin.py --layer "TestPoints" --bookmark "FullExtent" --gdb "test_project/TestFixture.gdb"

# Generate the test fixture GDB first:
"C:\Program Files\ArcGIS\Pro\bin\Python\envs\arcgispro-py3\python.exe" scripts/create_test_fixture.py
```

The latest live validation report is at `live_test_report.json`.

## Available Tools

### GIS Data Tools

| Tool | Description |
|------|-------------|
| `ping` | MCP connectivity check |
| `health_check` | ArcGIS environment status |
| `doctor` | Comprehensive diagnostic report |
| `detect_arcgis_environment` | Discover ArcGIS Pro Python |
| `inspect_project_context` | Full project overview |
| `list_gis_layers` | List all layers in project |
| `inspect_gdb` | GDB schema inspection |
| `buffer_features` | Buffer geoprocessing tool |
| `clip_features` | Clip geoprocessing tool |
| `execute_arcpy_code` | Run arbitrary ArcPy code |
| `generate_sync_plan` | Generate sync plan |

### Raster Suitability Analysis Tools

| Tool | Description |
|------|-------------|
| `validate_project_data` | Pre-flight data validation |
| `prepare_analysis_inputs` | Clip, resample, slope, distance rasters |
| `reclassify_criteria` | Batch reclassify rasters to 1-5 scale |
| `weighted_suitability` | Weighted Linear Combination (WLC) |
| `conflict_analysis` | Conflict zones and allocation maps |
| `raster_area_summary` | Area statistics by class |
| `sensitivity_check` | Weight perturbation sensitivity analysis |
| `export_suitability_map` | Layout creation and PDF/PNG export |

### Real-Time Add-In Tools (requires APBridgeAddIn)

| Tool | Description |
|------|-------------|
| **Map & Selection** | 20 | `pro_ping`, `pro_get_active_map_name`, `pro_list_layers`, `pro_count_features`, `pro_get_layer_schema`, `pro_get_selection_count`, `pro_select_by_attribute`, `pro_clear_selection`, `pro_zoom_to_layer`, `pro_get_current_extent`, `pro_pan_to_extent`, `pro_get_camera`, `pro_set_layer_visibility`, `pro_get_layer_extent`, `pro_select_by_rectangle`, `pro_switch_selection`, `pro_get_feature_by_oid`, `pro_get_active_tool`, `pro_select_by_polygon`, `pro_select_by_layer` |
| **Editing** | 12 | `pro_undo_edit`, `pro_redo_edit`, `pro_get_edit_state`, `pro_set_active_tool`, `pro_delete_features_by_oid`, `pro_update_feature_attributes`, `pro_create_point_feature`, `pro_create_polygon_feature`, `pro_create_line_feature`, `pro_split_features`, `pro_merge_features`, `pro_set_snapping` |
| **Layer Management** | 15 | `pro_reorder_layer`, `pro_remove_layer`, `pro_add_layer_from_file`, `pro_rename_layer`, `pro_set_layer_visibility`, `pro_set_layer_transparency`, `pro_set_layer_color`, `pro_set_labels_enabled`, `pro_get_layer_renderer`, `pro_get_layer_extent`, `pro_get_layer_description`, `pro_set_layer_description`, `pro_get_layer_statistics`, `pro_list_scene_layer_types`, `pro_copy_features` |
| **Map Navigation** | 5 | `pro_get_map_scale`, `pro_set_map_scale`, `pro_zoom_to_selected`, `pro_get_current_extent`, `pro_pan_to_extent` |
| **Bookmarks** | 3 | `pro_list_bookmarks`, `pro_zoom_to_bookmark`, `pro_delete_bookmark` |
| **Selection UX** | 4 | `pro_select_by_rectangle`, `pro_select_by_polygon`, `pro_select_by_layer`, `pro_switch_selection`, `pro_select_all`, `pro_flash_selection` |
| **Schema** | 9 | `pro_get_layer_schema`, `pro_add_field`, `pro_delete_field`, `pro_rename_field`, `pro_add_attribute_index`, `pro_create_feature_class`, `pro_delete_feature_class`, `pro_list_subtypes`, `pro_set_subtype_field` |
| **Domains** | 3 | `pro_list_domains`, `pro_create_domain`, `pro_assign_domain_to_field` |
| **3D / Viz** | 13 | `pro_is_3d`, `pro_get_camera`, `pro_fly_to_location`, `pro_get_elevation_sources`, `pro_set_ground_opacity`, `pro_set_atmosphere`, `pro_set_sun_position`, `pro_get_sun_position`, `pro_explore_3d`, `pro_set_layer_elevation`, `pro_set_scene_background`, `pro_apply_unique_value_renderer`, `pro_apply_class_breaks_renderer` |
| **Layouts** | 10 | `pro_list_layouts`, `pro_get_map_frame`, `pro_list_layout_elements`, `pro_remove_layout_element`, `pro_export_layout_to_file`, `pro_create_layout`, `pro_add_layout_text`, `pro_add_layout_picture`, `pro_add_layout_legend`, `pro_add_layout_north_arrow` |
| **Map Management** | 3 | `pro_get_all_map_names`, `pro_create_map`, `pro_add_basemap` |
| **Geoprocessing** | 9 | `pro_list_toolboxes`, `pro_describe_tool`, `pro_list_gp_tools`, `pro_list_gp_history`, `pro_get_geoprocessing_history`, `pro_run_gp_tool`, `pro_run_python_script`, `pro_set_environment`, `pro_get_environment` |
| **Data Exchange** | 8 | `pro_export_to_csv`, `pro_export_to_geo_json`, `pro_export_to_shapefile`, `pro_export_to_kml`, `pro_import_csv`, `pro_import_geo_json`, `pro_search_address`, `pro_open_attribute_table` |
| **UI / GUI** | 7 | `pro_show_message`, `pro_show_progress_dialog`, `pro_set_status_bar_message`, `pro_set_status_bar_progress`, `pro_list_dockpanes`, `pro_activate_ribbon_tab`, `pro_open_dockpane` |
| **Time Slider** | 3 | `pro_is_time_enabled`, `pro_get_time_extent`, `pro_set_time_extent` |
| **Utility** | 5 | `pro_get_project_properties`, `pro_get_geometry_distance`, `pro_project_geometry`, `pro_save_project`, `pro_get_all_map_names` |
| **Attachments** | 1 | `pro_enable_attachments` |
| **Standalone Tables** | 1 | `pro_list_standalone_tables` |
| `pro_get_geoprocessing_history` | Get recent GP execution history |
| `pro_run_python_script` | Execute Python in Pro's environment |
| `pro_set_environment` | Set a GP environment setting |
| `pro_get_environment` | Get GP environment settings |

## Available Resources

| URI | Description |
|-----|-------------|
| `arcgis://server/status` | Server and ArcGIS status |
| `arcgis://project/current/layers` | Current project layers |
| `arcgis://project/{project_ref}/layers` | Specific project layers |
| `arcgis://project/current/context` | Current project context |
| `arcgis://gdb/{gdb_ref}/schema` | GDB schema |

## MCP Configuration Examples

### OpenCode
```json
{
  "mcp": {
    "arcgis-pro": {
      "command": ["path/to/python.exe", "path/to/arcgis_mcp_server.py"]
    }
  }
}
```

### Cursor
```json
{
  "mcp": {
    "arcgis-pro": {
      "command": ["uv", "run", "arcgis-mcp-server"]
    }
  }
}
```

### Claude Desktop
See `examples/claude-desktop-mcp-config.json`

## Environment Variables

| Variable | Description |
|----------|-------------|
| `ARCGIS_PRO_PYTHON` | Path to ArcGIS Pro Python executable |
| `ARCGIS_PRO_INSTALL_DIR` | Path to ArcGIS Pro installation |
| `ARCGIS_MCP_ALLOWED_PATHS` | Colon-separated allowed paths (optional) |

## Safety

- `execute_arcpy_code` requires explicit user confirmation
- Read-only by default for project inspection tools
- Do not expose to public networks
- Always backup data before running geoprocessing

## Troubleshooting

### ArcGIS Pro Python not found

Set environment variables:
```powershell
$env:ARCGIS_PRO_PYTHON = "C:\Program Files\ArcGIS\Pro\bin\Python\envs\arcgispro-py3\python.exe"
```

### Project locked by ArcGIS Pro

Close ArcGIS Pro or use a copy of the `.aprx` file.

### Data source errors

Use `list_gis_layers` or `inspect_project_context` to identify broken data sources.

## Local Testing

```bash
# Run tests
uv run pytest tests/

# Lint
uv run ruff check .

# Format check
uv run ruff format --check .
```

## Documentation

- [AGENTS.md](AGENTS.md) - Agent instructions
- [FUTURE_WORK.md](FUTURE_WORK.md) - Future development plans
- [examples/](examples/) - MCP configuration examples

## License

MIT - See [LICENSE](LICENSE)