# ArcGIS Pro Bridge MCP Server

A local MCP server that connects AI agents (OpenCode, Trae, Cursor, Claude Desktop) to ArcGIS Pro, enabling AI-assisted GIS workflows.

## What It Does

- Enables AI clients to read ArcGIS Pro project information (maps, layers, layouts)
- Allows AI to execute ArcPy geoprocessing operations (Buffer, Clip, Merge)
- Provides structured access to `.aprx` projects and `.gdb` databases
- Generates and executes ArcPy scripts with user confirmation
- **Real-time ArcGIS Pro interaction** via C# Add-In (zoom to layers, select features, inspect live map state)

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

### 4. Test

Ask OpenCode:
> Run a health check, then summarize the parcels layer in my project.

### 5. (Optional) Install the ArcGIS Pro Add-In for Real-Time Access

Build and install the C# Add-In to enable live interaction with the active ArcGIS Pro session:

1. Open `addin/APBridgeAddIn/APBridgeAddIn.csproj` in **Visual Studio 2022** with ArcGIS Pro SDK installed
2. Build the solution (produces `APBridgeAddIn.esriAddInX`)
3. Double-click the `.esriAddInX` file to install into ArcGIS Pro
4. Start (or restart) ArcGIS Pro — the Named Pipe bridge starts automatically

After installation, all `pro.*` tools in the table below will work against the live Pro session.

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
| **Base (11)** | |
| `pro_ping` | Ping the Add-In to verify Named Pipe connectivity |
| `pro_get_active_map_name` | Get the active map name |
| `pro_list_layers` | List all layers with visibility and type |
| `pro_count_features` | Count features in a named layer |
| `pro_get_layer_schema` | Get field schema of a layer |
| `pro_get_selection_count` | Count selected features in a layer |
| `pro_select_by_attribute` | Select features by SQL where clause |
| `pro_clear_selection` | Clear selection on a layer or all layers |
| `pro_zoom_to_layer` | Zoom to a layer's extent |
| `pro_get_current_extent` | Get current map view extent |
| `pro_pan_to_extent` | Pan to a specified bounding box |
| **Phase 0 (10)** | |
| `pro_get_camera` | Get current camera position |
| `pro_set_camera` | Set camera position by x, y, z coordinates |
| `pro_set_layer_visibility` | Show/hide a layer |
| `pro_get_layer_extent` | Get full extent of a layer |
| `pro_select_by_rectangle` | Select features within a rectangle |
| `pro_switch_selection` | Invert selection on a layer |
| `pro_get_feature_by_oid` | Get attributes by ObjectID |
| `pro_undo_edit` | Undo last edit operation |
| `pro_redo_edit` | Redo last undone edit |
| `pro_set_active_tool` | Activate a map tool by DAML ID |
| **Phase 1 (8)** | |
| `pro_get_layer_renderer` | Get renderer type and classification field |
| `pro_set_layer_color` | Set fill color via RGB for simple renderers |
| `pro_remove_layer` | Remove a layer from the map |
| `pro_add_layer_from_file` | Add a .lyrx or feature class to the map |
| `pro_select_by_polygon` | Select by polygon coordinates |
| `pro_list_layouts` | List all layouts in the project |
| `pro_get_project_properties` | Get project metadata (name, path, gdb, tags) |
| `pro_get_geometry_distance` | Euclidean distance between two points |
| **Phase 2 (8)** | |
| `pro_is_3d` | Check if active view is a 3D scene |
| `pro_set_layer_transparency` | Set transparency percentage (0-100) |
| `pro_get_all_map_names` | List all maps in the project |
| `pro_get_map_frame` | Get map frame properties from a layout |
| `pro_zoom_to_selected` | Zoom to selected features |
| `pro_flash_layer` | Flash a layer on the map |
| `pro_get_elevation_sources` | List elevation surfaces in a scene |
| `pro_get_elevation_at_point` | Get elevation at a map point |
| **Phase 3 (8)** | |
| `pro_get_active_tool` | Get the currently active tool DAML ID |
| `pro_list_field_values` | List distinct field values |
| `pro_add_field` | Add a new field to a feature class |
| `pro_delete_field` | Delete a field |
| `pro_alter_field` | Alter field properties (alias, type) |
| `pro_list_attachments` | List attachments for a feature |
| `pro_get_attachment` | Download an attachment to file |
| `pro_add_attachment` | Attach a file to a feature |
| **Phase 4 (10)** | |
| `pro_delete_attachment` | Delete an attachment |
| `pro_set_snapping` | Enable/disable snapping |
| `pro_get_bookmarks` | List map bookmarks |
| `pro_zoom_to_bookmark` | Zoom to a named bookmark |
| `pro_create_bookmark` | Create a new bookmark |
| `pro_get_time_extent` | Get map time slider extent |
| `pro_set_time_extent` | Set map time extent |
| `pro_export_layout` | Export layout to PDF/PNG |
| `pro_list_layout_elements` | List elements in a layout |
| `pro_set_layout_element_visibility` | Show/hide a layout element |
| **Phase 5 (10)** | |
| `pro_get_layer_description` | Get layer TOC description |
| `pro_set_layer_description` | Set layer TOC description |
| `pro_get_map_scale` | Get current map scale |
| `pro_set_map_scale` | Set map scale |
| `pro_get_map_description` | Get map description |
| `pro_set_map_description` | Set map description |
| `pro_add_standalone_table` | Add a standalone table to the map |
| `pro_remove_standalone_table` | Remove a standalone table |
| `pro_get_all_standalone_tables` | List all standalone tables |
| `pro_add_relate` | Create a relate between two layers |
| **Phase 6 (10)** | |
| `pro_select_by_layer` | Select by spatial relationship to another layer |
| `pro_get_features_by_extent` | Get feature attributes within a bounding box |
| `pro_delete_features_by_oid` | Delete features by OIDs |
| `pro_update_feature_attributes` | Update attributes by OID |
| `pro_create_point_feature` | Create a point feature |
| `pro_apply_unique_value_renderer` | Apply unique value renderer |
| `pro_apply_class_breaks_renderer` | Apply equal interval renderer |
| `pro_create_polygon_feature` | Create a polygon feature |
| `pro_create_line_feature` | Create a line feature |
| `pro_set_ground_opacity` | Set ground surface opacity in a scene |
| **Phase 7 (8)** | |
| `pro_split_features` | Split features by cutting geometry |
| `pro_merge_features` | Merge multiple features |
| `pro_run_gp_tool` | Execute any GP tool with typed parameters |
| `pro_list_gp_tools` | List GP tools from project toolboxes |
| `pro_copy_features` | Copy features via CopyFeatures GP tool |
| `pro_rename_layer` | Rename a layer in the map |
| `pro_get_layer_statistics` | Compute field statistics (min, max, mean, stddev) |
| `pro_project_geometry` | Project a point between spatial references |
| **Phase 8 (8)** | |
| `pro_add_layout_text` | Add text element to a layout |
| `pro_add_layout_picture` | Add picture to a layout from file |
| `pro_add_layout_legend` | Add legend to a layout map frame |
| `pro_add_layout_north_arrow` | Add north arrow to a layout |
| `pro_remove_layout_element` | Remove an element by name |
| `pro_create_layout` | Create a new layout |
| `pro_create_map` | Create a new map (2D or 3D scene) |
| `pro_add_basemap` | Set basemap (Streets, Imagery, etc.) |
| **Phase 9 (6)** | |
| `pro_set_atmosphere` | Set fog density and horizon fog |
| `pro_set_sun_position` | Set sun azimuth and altitude |
| `pro_get_sun_position` | Get current sun position |
| `pro_explore_3d` | Orbit camera to look at a 3D point |
| `pro_set_layer_elevation` | Set elevation mode (absolute/relative/DRA) |
| `pro_set_scene_background` | Set scene background color |
| **Phase 10 (6)** | |
| `pro_create_feature_class` | Create a feature class in a geodatabase |
| `pro_delete_feature_class` | Delete a feature class or table |
| `pro_save_project` | Save the current project |
| `pro_add_attribute_index` | Create an attribute index on a field |
| `pro_search_address` | Search using the map's locators |
| `pro_open_attribute_table` | Open the attribute table view |
| **Phase 11 (6)** | |
| `pro_export_to_csv` | Export layer to CSV |
| `pro_export_to_geo_json` | Export layer to GeoJSON |
| `pro_import_csv` | Import CSV as point feature class |
| `pro_export_to_shapefile` | Export layer to shapefile |
| `pro_export_to_kml` | Export layer to KML |
| `pro_import_geo_json` | Import GeoJSON as feature class |
| **Phase 12 (6)** | |
| `pro_show_message` | Show a message dialog (info/warning/error) |
| `pro_show_progress_dialog` | Show a progress dialog |
| `pro_set_status_bar_progress` | Set status bar percentage and message |
| `pro_list_dockpanes` | List known dockpanes |
| `pro_activate_ribbon_tab` | Activate a ribbon tab by name |
| `pro_open_dockpane` | Open a dockpane by name or DAML ID |
| **Phase 13 (6)** | |
| `pro_list_domains` | List coded-value and range domains |
| `pro_create_domain` | Create a coded-value or range domain |
| `pro_assign_domain_to_field` | Assign a domain to a field |
| `pro_list_subtypes` | List subtypes for a feature layer |
| `pro_set_subtype_field` | Set the subtype field |
| `pro_enable_attachments` | Enable attachments on a layer |
| **Phase 14 (6)** | |
| `pro_list_toolboxes` | List all available geoprocessing toolboxes |
| `pro_describe_tool` | Describe a GP tool and its parameters |
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