# API Reference

Auto-generated from `@mcp.tool()` function signatures and docstrings.

<!-- GENERATED: do not edit manually. Run `python scripts/generate_api_docs.py` to regenerate -->

---
## GIS Data Tools (File-Based)

These tools work without the C# Add-In. They read `.aprx` files from disk or execute ArcPy in a subprocess.

| Tool | Description | Key Parameters |
|------|-------------|----------------|
| `ping` | MCP connectivity check | — |
| `health_check` | ArcGIS environment status | — |
| `doctor` | Comprehensive diagnostic report | — |
| `detect_arcgis_environment` | Discover ArcGIS Pro Python | — |
| `inspect_project_context` | Full project overview | aprx_path |
| `list_gis_layers` | List all layers in project | aprx_path |
| `inspect_gdb` | GDB schema inspection | gdb_path |
| `buffer_features` | Buffer analysis | in_features, out_feature_class, buffer_distance |
| `clip_features` | Clip analysis | in_features, clip_features, out_feature_class |
| `execute_arcpy_code` | Run arbitrary ArcPy code | code |
| `validate_project_data` | Pre-flight data validation | project_gdb |
| `prepare_analysis_inputs` | One-shot data preparation | project_gdb, study_area_fc |
| `reclassify_criteria` | Batch reclassify rasters | reclass_table, output_gdb |
| `weighted_suitability` | Weighted Linear Combination | model_name, criteria, output_raster |
| `conflict_analysis` | Conflict zones and allocation | conservation_raster, urban_raster |
| `raster_area_summary` | Area statistics by class | raster_path |
| `sensitivity_check` | Weight perturbation sensitivity | 5 rasters + weights params |
| `export_suitability_map` | Layout creation and export | project_path, raster_path |
| `build_gis_resource_uri` | Build readable ArcGIS resource URI | resource_kind |
| `generate_sync_plan` | Generate data sync plan | source_description |
| `debug_runtime_context` | Debug runtime environment | — |

---
## Real-Time Add-In Tools (require APBridgeAddIn)

**144 tools** across 23 categories.

> Tip: Use `pro_ping` to check if the Add-In is available. Returns `{"status": "ok"}` when connected, `{"status": "unavailable"}` otherwise.

### Map & Selection

| Tool | Signature | Description |
|------|-----------|-------------|
| `pro_ping` | `()` | Ping the ArcGIS Pro Add-In to verify Named Pipe connectivity. |
| `pro_get_active_map_name` | `()` | Get the name of the active map in ArcGIS Pro. |
| `pro_list_layers` | `()` | List all layers in the active ArcGIS Pro map with visibility and type. |
| `pro_count_features` | `(layer: 'str')` | Count features in a layer by name in the active ArcGIS Pro map. |
| `pro_count_features_by_expression` | `(layer: 'str', where: 'str')` | Count features in a layer matching a SQL where clause. |
| `pro_get_layer_schema` | `(layer: 'str')` | Get field schema (name, type, alias, length, precision, etc. |
| `pro_get_selection_count` | `(layer: 'str')` | Count selected features in a layer by name. |
| `pro_select_by_attribute` | `(layer: 'str', where: 'str')` | Select features in a layer using a SQL where clause. |
| `pro_clear_selection` | `(layer?)` | Clear selection on a specific layer, or all layers if no layer specified. |
| `pro_zoom_to_layer` | `(layer: 'str')` | Zoom the active map view to a layer's extent. |
| `pro_get_current_extent` | `()` | Get the current map view extent (xmin, ymin, xmax, ymax, spatial reference). |
| `pro_pan_to_extent` | `(xmin: 'float', ymin: 'float', xmax: 'float', ymax: 'float')` | Pan the active map view to a specified bounding box extent. |
| `pro_get_camera` | `()` | Get the current camera position from the active ArcGIS Pro map view. |
| `pro_set_layer_visibility` | `(layer: 'str', visible: 'bool')` | Set the visibility of a layer by name in the active ArcGIS Pro map. |
| `pro_get_layer_extent` | `(layer: 'str')` | Get the full spatial extent (xmin, ymin, xmax, ymax) of a feature layer. |
| `pro_select_by_rectangle` | `(layer: 'str', xmin: 'float', ymin: 'float', xmax: 'float', ymax: 'float', selection_type?)` | Select features in a layer within a rectangle. |
| `pro_switch_selection` | `(layer?)` | Invert the selection on a specific layer, or all layers if none specified. |
| `pro_get_feature_by_oid` | `(layer: 'str', oid: 'int')` | Get all attribute values for a feature by its ObjectID. |
| `pro_get_active_tool` | `()` | Get the DAML ID of the currently active map tool. |
| `pro_select_by_polygon` | `(layer: 'str', coordinates: 'str', selection_type?)` | Select features in a layer by polygon coordinates. |
| `pro_select_by_layer` | `(target_layer: 'str', source_layer: 'str', spatial_relationship?, selection_type?)` | Select features by spatial relationship to another layer. |
| `pro_get_features_by_extent` | `(layer: 'str', xmin: 'float', ymin: 'float', xmax: 'float', ymax: 'float', fields?, max_features?)` | Get feature attributes within a bounding box extent. |
| `pro_find_features` | `(layer: 'str', where?, fields?, max_features?)` | Query features in a layer by attribute with optional field projection. |

### Editing

| Tool | Signature | Description |
|------|-----------|-------------|
| `pro_undo_edit` | `()` | Undo the last edit operation in ArcGIS Pro. |
| `pro_redo_edit` | `()` | Redo the last undone edit operation in ArcGIS Pro. |
| `pro_get_edit_state` | `()` | Get undo and redo operation counts. |
| `pro_set_active_tool` | `(tool: 'str')` | Set the active map tool by DAML ID (e. |
| `pro_delete_features_by_oid` | `(layer: 'str', oids: 'str', auto_snapshot?)` | Delete features by comma-separated OIDs in a layer (e. |
| `pro_update_feature_attributes` | `(layer: 'str', oid: 'int', attributes: 'str')` | Update attributes of a feature by OID. |
| `pro_create_point_feature` | `(layer: 'str', x: 'float', y: 'float', wkid?, attributes?)` | Create a point feature at (x, y) with optional WKID and JSON attributes. |
| `pro_create_polygon_feature` | `(layer: 'str', coordinates: 'str', wkid?, attributes?)` | Create a polygon feature from space-separated 'x,y' coordinates (min 3 pairs). |
| `pro_create_line_feature` | `(layer: 'str', coordinates: 'str', wkid?, attributes?)` | Create a line/polyline feature from space-separated 'x,y' coordinates (min 2 pairs). |
| `pro_split_features` | `(layer: 'str', cut_geometry: 'str')` | Split features intersecting a cutting geometry (GeoJSON polyline/polygon). |
| `pro_merge_features` | `(layer: 'str', object_ids: 'str', target_oid: 'int', auto_snapshot?)` | Merge multiple features. |
| `pro_set_snapping` | `(enabled: 'bool')` | Enable or disable map snapping. |
| `pro_select_all` | `(layer: 'str')` | Select all features in a layer. |
| `pro_flash_selection` | `(layer: 'str')` | Visually flash selected features in a layer on the map. |

### Layer Management

| Tool | Signature | Description |
|------|-----------|-------------|
| `pro_reorder_layer` | `(layer: 'str', index: 'int')` | Move a layer to the specified index in the table of contents. |
| `pro_remove_layer` | `(layer: 'str')` | Remove a layer from the active ArcGIS Pro map by name. |
| `pro_add_layer_from_file` | `(path: 'str')` | Add a layer from a . |
| `pro_add_layer_from_service` | `(url: 'str', service_type?)` | Add a web layer from a service URL to the active ArcGIS Pro map (ArcGIS Server, WMS, etc. |
| `pro_rename_layer` | `(layer: 'str', new_name: 'str')` | Rename a layer in the current map. |
| `pro_set_layer_visibility` | `(layer: 'str', visible: 'bool')` | Set the visibility of a layer by name in the active ArcGIS Pro map. |
| `pro_set_layer_transparency` | `(layer: 'str', transparency: 'float')` | Set layer transparency percentage (0 = opaque, 100 = fully transparent). |
| `pro_set_layer_color` | `(layer: 'str', r: 'int', g: 'int', b: 'int')` | Set the fill color for layers with a simple renderer using RGB values (0-255 each). |
| `pro_set_labels_enabled` | `(layer: 'str', enabled: 'bool')` | Enable or disable labels on a feature layer. |
| `pro_get_layer_renderer` | `(layer: 'str')` | Get the renderer type and classification field for a layer. |
| `pro_get_layer_extent` | `(layer: 'str')` | Get the full spatial extent (xmin, ymin, xmax, ymax) of a feature layer. |
| `pro_get_layer_description` | `(layer: 'str')` | Get the description text for a layer (shown in TOC tooltips). |
| `pro_set_layer_description` | `(layer: 'str', description: 'str')` | Set the description text for a layer. |
| `pro_get_layer_statistics` | `(layer: 'str', field: 'str')` | Compute min, max, mean, stddev, count, and null count for a numeric field. |
| `pro_list_scene_layer_types` | `()` | List layers with scene/3D type info (FeatureLayer, PointCloudLayer, SceneLayer, etc. |
| `pro_copy_features` | `(layer: 'str', output_path: 'str')` | Copy features to a new feature class using the CopyFeatures GP tool. |
| `pro_list_field_values` | `(layer: 'str', field: 'str', max_values?)` | List distinct field values for a layer. |

### Map Navigation

| Tool | Signature | Description |
|------|-----------|-------------|
| `pro_get_map_scale` | `()` | Get the current scale of the active map view. |
| `pro_set_map_scale` | `(scale: 'float')` | Set the active map view to a specific scale. |
| `pro_zoom_to_selected` | `(layer?)` | Zoom to selected features. |
| `pro_get_current_extent` | `()` | Get the current map view extent (xmin, ymin, xmax, ymax, spatial reference). |
| `pro_pan_to_extent` | `(xmin: 'float', ymin: 'float', xmax: 'float', ymax: 'float')` | Pan the active map view to a specified bounding box extent. |

### Bookmarks

| Tool | Signature | Description |
|------|-----------|-------------|
| `pro_list_bookmarks` | `()` | List bookmarks in the active map. |
| `pro_zoom_to_bookmark` | `(name: 'str')` | Zoom the active map view to a named bookmark. |
| `pro_create_bookmark` | `(name: 'str')` | Create a new bookmark in the active map (not accessible from AddIn SDK). |
| `pro_delete_bookmark` | `(name: 'str')` | Delete a bookmark by name from the active map. |

### Schema Management

| Tool | Signature | Description |
|------|-----------|-------------|
| `pro_get_layer_schema` | `(layer: 'str')` | Get field schema (name, type, alias, length, precision, etc. |
| `pro_add_field` | `(layer: 'str', field_name: 'str', field_type: 'str', precision?, scale?, length?)` | Add a new field to a layer's feature class. |
| `pro_delete_field` | `(layer: 'str', field_name: 'str', auto_snapshot?)` | Delete a field from a layer's feature class. |
| `pro_calculate_field` | `(layer: 'str', field: 'str', expression: 'str', expression_type?, code_block?)` | Calculate field values using an expression (Python, SQL, etc. |
| `pro_rename_field` | `(layer: 'str', old_name: 'str', new_name: 'str')` | Rename a field on a feature layer. |
| `pro_add_attribute_index` | `(layer: 'str', field: 'str', index_name?, unique?)` | Add an attribute index on a field for faster queries. |
| `pro_create_feature_class` | `(gdb_path: 'str', name: 'str', geometry_type: 'str', wkid?, fields_json?)` | Create a feature class in a geodatabase (geometry_type: Point/Polyline/Polygon). |
| `pro_delete_feature_class` | `(path: 'str', auto_snapshot?)` | Delete a feature class or table by full path. |
| `pro_list_subtypes` | `(layer: 'str')` | List subtypes for a feature layer. |
| `pro_set_subtype_field` | `(layer: 'str', field: 'str')` | Set the subtype field for a feature layer. |

### Domains

| Tool | Signature | Description |
|------|-----------|-------------|
| `pro_list_domains` | `(gdb_path: 'str')` | List coded-value and range domains in a geodatabase. |
| `pro_create_domain` | `(gdb_path: 'str', name: 'str', description: 'str', field_type: 'str', coded_values?)` | Create a coded-value domain (coded_values as JSON dict) or range domain. |
| `pro_assign_domain_to_field` | `(layer: 'str', field: 'str', domain_name: 'str')` | Assign a domain to a field on a layer. |

### 3D & Visualization

| Tool | Signature | Description |
|------|-----------|-------------|
| `pro_is_3d` | `()` | Check if the active map view is a 3D scene (GlobalScene or LocalScene). |
| `pro_get_camera` | `()` | Get the current camera position from the active ArcGIS Pro map view. |
| `pro_fly_to_location` | `(x: 'float', y: 'float', z: 'float', heading?, pitch?, duration_seconds?)` | Fly the camera to a 3D location (x, y, z) in a scene view. |
| `pro_get_elevation_sources` | `()` | List elevation surface sources in the active scene (ground). |
| `pro_set_ground_opacity` | `(opacity: 'float')` | Set the ground surface opacity (0=transparent, 100=opaque) in the active scene. |
| `pro_set_atmosphere` | `(fog_density: 'float', horizon_fog?, fog_color?)` | Set atmospheric effects in a scene (fog density 0-100, optional horizon fog and RGB color). |
| `pro_set_sun_position` | `(azimuth: 'float', altitude: 'float')` | Set the sun position in a scene (azimuth 0-360, altitude 0-90). |
| `pro_get_sun_position` | `()` | Get the current sun azimuth and altitude in a scene. |
| `pro_explore_3d` | `(x: 'float', y: 'float', target_z: 'float', distance: 'float', heading_delta?, pitch_delta?)` | Orbit/navigate camera to look at a 3D point from a given distance. |
| `pro_set_layer_elevation` | `(layer: 'str', elevation_mode: 'str', z_offset: 'float')` | Set elevation mode (absolute/relative/dra) and Z offset for a layer in a scene. |
| `pro_set_scene_background` | `(r: 'int', g: 'int', b: 'int', background_type?)` | Set the scene background color (r,g,b 0-255) and type (color/none). |
| `pro_apply_unique_value_renderer` | `(layer: 'str', field: 'str', color_ramp?)` | Apply a unique value renderer to a layer based on a field's distinct values. |
| `pro_apply_class_breaks_renderer` | `(layer: 'str', field: 'str', break_count?)` | Apply a class breaks renderer using equal interval classification. |

### Layouts

| Tool | Signature | Description |
|------|-----------|-------------|
| `pro_list_layouts` | `()` | List all layouts in the current ArcGIS Pro project. |
| `pro_get_map_frame` | `(layout_name: 'str', map_frame_name?)` | Get map frame properties (camera, map name, dimensions) from a layout. |
| `pro_list_layout_elements` | `(layout_name: 'str')` | List all elements (graphics, map frames, surrounds) in a layout. |
| `pro_remove_layout_element` | `(layout_name: 'str', element_name: 'str')` | Remove an element from a layout by name. |
| `pro_export_layout_to_file` | `(layout_name: 'str', output_path: 'str', format?, dpi?)` | Export a layout to PDF or PNG file. |
| `pro_create_layout` | `(layout_name: 'str', width: 'float', height: 'float', units?)` | Create a new layout in the project and auto-save. |
| `pro_add_layout_text` | `(layout_name: 'str', text: 'str', x: 'float', y: 'float', font_size?, color_rgb?)` | Add a text element to a layout. |
| `pro_add_layout_picture` | `(layout_name: 'str', image_path: 'str', x: 'float', y: 'float', width: 'float', height: 'float')` | Add a picture/image element to a layout from a file path. |
| `pro_add_layout_legend` | `(layout_name: 'str', x: 'float', y: 'float', map_frame_name?)` | Add a legend to a layout's map frame. |
| `pro_add_layout_north_arrow` | `(layout_name: 'str', map_frame_name: 'str', x: 'float', y: 'float')` | Add a north arrow to a layout's map frame. |

### Map Management

| Tool | Signature | Description |
|------|-----------|-------------|
| `pro_get_all_map_names` | `()` | List all maps in the current ArcGIS Pro project. |
| `pro_create_map` | `(map_name: 'str', map_type?, basemap?)` | Create a new map (Map/LocalScene/GlobalScene), activate it, and auto-save. |
| `pro_add_basemap` | `(basemap_name: 'str')` | Set the basemap of the active map (Streets, Imagery, Topographic, etc. |

### Geoprocessing

| Tool | Signature | Description |
|------|-----------|-------------|
| `pro_list_toolboxes` | `()` | List all available geoprocessing toolboxes (project + system). |
| `pro_describe_tool` | `(tool_name: 'str')` | Describe a geoprocessing tool and its parameters. |
| `pro_list_gp_tools` | `(search_text?, max_results?)` | List GP tools from project toolboxes, optionally filtered by search_text. |
| `pro_list_gp_history` | `(max_items?)` | List recent geoprocessing history items from the project. |
| `pro_get_geoprocessing_history` | `(count?)` | Return recent geoprocessing execution history. |
| `pro_run_gp_tool` | `(tool_name: 'str', parameters: 'str')` | Execute an ArcGIS Geoprocessing tool by name. |
| `pro_run_python_script` | `(code: 'str', timeout_seconds?)` | Execute a Python script in ArcGIS Pro's Python environment. |
| `pro_set_environment` | `(key: 'str', value: 'str')` | Set a geoprocessing environment setting (e. |
| `pro_get_environment` | `(key?)` | Get geoprocessing environment settings. |

### Data Exchange

| Tool | Signature | Description |
|------|-----------|-------------|
| `pro_export_to_csv` | `(layer: 'str', output_path: 'str')` | Export layer attribute table to a CSV file. |
| `pro_export_to_geo_json` | `(layer: 'str', output_path: 'str')` | Export layer features to a GeoJSON file. |
| `pro_export_to_shapefile` | `(layer: 'str', output_path: 'str')` | Export a layer to a shapefile. |
| `pro_export_to_kml` | `(layer: 'str', output_path: 'str')` | Export a layer to a KML file. |
| `pro_import_csv` | `(csv_path: 'str', gdb_path: 'str', fc_name: 'str', x_field: 'str', y_field: 'str', wkid?)` | Import a CSV file as a point feature class (creates FC if needed). |
| `pro_import_geo_json` | `(geojson_path: 'str', gdb_path: 'str', fc_name: 'str')` | Import a GeoJSON file as a feature class (point, line, or polygon). |
| `pro_search_address` | `(address: 'str', max_results?)` | Search for an address or place using the map's locators. |
| `pro_open_attribute_table` | `(layer: 'str')` | Open the attribute table view for a layer. |

### UI / GUI Automation

| Tool | Signature | Description |
|------|-----------|-------------|
| `pro_show_message` | `(message: 'str', type?, title?)` | Show a message dialog in ArcGIS Pro (type: info/warning/error). |
| `pro_show_progress_dialog` | `(title: 'str', message: 'str')` | Show a progress/info dialog in ArcGIS Pro. |
| `pro_set_status_bar_message` | `(message: 'str')` | Set the ArcGIS Pro status bar message. |
| `pro_set_status_bar_progress` | `(percent: 'int', message: 'str')` | Set the status bar progress percentage and message (0-100). |
| `pro_list_dockpanes` | `()` | List known dockpanes available in ArcGIS Pro. |
| `pro_activate_ribbon_tab` | `(tab_id: 'str')` | Activate a ribbon tab by name (Map, Edit, Catalog, Insert, Analysis, View) or DAML ID. |
| `pro_open_dockpane` | `(dockpane_id: 'str')` | Open a dockpane by DAML ID or friendly name (Contents, Catalog, Geoprocessing, etc. |

### Time Slider

| Tool | Signature | Description |
|------|-----------|-------------|
| `pro_is_time_enabled` | `()` | Check if the time slider is enabled on the active map. |
| `pro_get_time_extent` | `()` | Get the current time extent of the active map (start/end). |
| `pro_set_time_extent` | `(start: 'str', end: 'str')` | Set the map time extent. |

### Attachments

| Tool | Signature | Description |
|------|-----------|-------------|
| `pro_enable_attachments` | `(layer: 'str')` | Enable attachments on a feature layer. |

### Standalone Tables

| Tool | Signature | Description |
|------|-----------|-------------|
| `pro_list_standalone_tables` | `()` | List non-spatial standalone tables in the current project. |

### Project & Utility

| Tool | Signature | Description |
|------|-----------|-------------|
| `pro_get_project_properties` | `()` | Get project metadata including name, path, default geodatabase, summary, and tags. |
| `pro_get_geometry_distance` | `(x1: 'float', y1: 'float', x2: 'float', y2: 'float')` | Calculate Euclidean distance between two map coordinates. |
| `pro_project_geometry` | `(x: 'float', y: 'float', from_wkid: 'int', to_wkid: 'int')` | Project a point from one spatial reference to another using GeometryEngine. |
| `pro_save_project` | `()` | Save the current ArcGIS Pro project. |

### In-Process Python

| Tool | Signature | Description |
|------|-----------|-------------|
| `pro_ping_python_runtime` | `()` | Ping the in-process Python runtime (Python. |

### Plugin Tools

| Tool | Signature | Description |
|------|-----------|-------------|
| `pro_plugin_batch_export` | `(target_format: 'str', output_dir: 'str')` | Export all feature layers in the active map to a target format (csv, geojson, shapefile, kml). |
| `pro_plugin_coordinate_capture` | `(target_wkid?)` | Capture the center coordinates of the current map view, with optional CRS reprojection. |
| `pro_plugin_feature_inspector` | `(layer: 'str', oid: 'int')` | Inspect a single feature: return all attributes and geometry summary for a given ObjectID. |
| `pro_plugin_query_builder` | `(layer: 'str', field: 'str', value: 'str', operator_name?)` | Query features in a layer by field value. |
| `pro_plugin_field_calculator` | `(layer: 'str', field: 'str', expression: 'str')` | Calculate a field using a Python expression across all features in a layer. |

### Python Micro-Plugins

| Tool | Signature | Description |
|------|-----------|-------------|
| `pro_micro_list` | `()` | List all available Python micro-plugins from the microplugins/ directory. |
| `pro_micro_run` | `(plugin: 'str', args?)` | Execute a Python micro-plugin by name with JSON args. |

### Workflow Macros

| Tool | Signature | Description |
|------|-----------|-------------|
| `pro_run_macro` | `(macro: 'str', timeout_per_step?, variables?)` | Execute a workflow macro - a named sequence of pro. |
| `pro_list_macros` | `()` | List all built-in workflow macros available from the macros/ directory. |

### Layer Resolution

| Tool | Signature | Description |
|------|-----------|-------------|
| `pro_resolve_layer` | `(layer_hint: 'str', cutoff?)` | Resolve an approximate layer name to the exact layer name in the active map. |

### Safety Net / Snapshots

| Tool | Signature | Description |
|------|-----------|-------------|
| `pro_create_snapshot` | `(layer: 'str', oids?, description?)` | Create a snapshot backup of features in a layer before destructive edits. |
| `pro_restore_snapshot` | `(snapshot_name: 'str', target_layer: 'str')` | Restore features from a snapshot back to the original layer. |
| `pro_list_snapshots` | `()` | List all snapshots available in the snapshots. |
| `pro_delete_snapshot` | `(snapshot_name: 'str')` | Delete a snapshot from the snapshots. |

---
## Return Values

All `pro.*` tools return a consistent JSON structure:

| Field | Type | Description |
|-------|------|-------------|
| `status` | string | `"ok"` on success, `"error"` on failure, `"unavailable"` when Add-In not reachable |
| `data` | any | The tool's response payload (present when `status == "ok"`) |
| `message` | string | Error description (present when `status != "ok"`) |
| `next_step` | string | Recovery instructions (present when `status == "unavailable"`) |
