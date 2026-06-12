#!/usr/bin/env python3
"""Live validation script for pro.* Add-In tools.

Runs every pro.* tool against a running ArcGIS Pro instance
and reports pass/fail/skip status for each.

Usage:
    python test_live_addin.py                          # test with default layer names
    python test_live_addin.py --fixture                # use TestFixture.aprx layer names
    python test_live_addin.py --layer "MyLayer"        # custom layer name
    python test_live_addin.py --fixture --gen-fixture  # generate fixture first (needs Pro Python)
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

# Ensure the project root is on sys.path
PROJECT_ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(PROJECT_ROOT))

from arcgis_mcp_server import (
    _call_addin,
    pro_ping,
    pro_get_active_map_name,
    pro_list_layers,
    pro_count_features,
    pro_get_layer_schema,
    pro_get_selection_count,
    pro_select_by_attribute,
    pro_clear_selection,
    pro_zoom_to_layer,
    pro_get_current_extent,
    pro_pan_to_extent,
    pro_get_camera,
    pro_set_layer_visibility,
    pro_get_layer_extent,
    pro_select_by_rectangle,
    pro_switch_selection,
    pro_get_feature_by_oid,
    pro_undo_edit,
    pro_redo_edit,
    pro_set_active_tool,
    pro_is_3d,
    pro_get_layer_renderer,
    pro_set_layer_color,
    pro_remove_layer,
    pro_add_layer_from_file,
    pro_add_layer_from_service,
    pro_select_by_polygon,
    pro_list_layouts,
    pro_get_project_properties,
    pro_get_geometry_distance,
    pro_set_layer_transparency,
    pro_get_all_map_names,
    pro_get_map_frame,
    pro_select_by_layer,
    pro_get_features_by_extent,
    pro_delete_features_by_oid,
    pro_update_feature_attributes,
    pro_create_point_feature,
    pro_apply_unique_value_renderer,
    pro_apply_class_breaks_renderer,
    pro_get_elevation_sources,
    pro_set_ground_opacity,
    pro_get_active_tool,
    pro_list_field_values,
    pro_add_field,
    pro_delete_field,
    pro_create_polygon_feature,
    pro_create_line_feature,
    pro_set_map_scale,
    pro_get_map_scale,
    pro_zoom_to_selected,
    pro_get_edit_state,
    pro_set_snapping,
    pro_delete_bookmark,
    pro_flash_selection,
    pro_select_all,
    pro_set_status_bar_message,
    pro_list_standalone_tables,
    pro_list_gp_history,
    pro_is_time_enabled,
    pro_get_time_extent,
    pro_set_time_extent,
    pro_list_layout_elements,
    pro_rename_field,
    pro_get_layer_description,
    pro_set_layer_description,
    pro_list_scene_layer_types,
    pro_count_features_by_expression,
    pro_find_features,
    pro_calculate_field,
    pro_split_features,
    pro_merge_features,
    pro_run_gp_tool,
    pro_list_gp_tools,
    pro_copy_features,
    pro_rename_layer,
    pro_get_layer_statistics,
    pro_project_geometry,
    pro_add_layout_text,
    pro_add_layout_picture,
    pro_add_layout_legend,
    pro_add_layout_north_arrow,
    pro_remove_layout_element,
    pro_create_layout,
    pro_create_map,
    pro_add_basemap,
    pro_set_atmosphere,
    pro_set_sun_position,
    pro_get_sun_position,
    pro_explore_3d,
    pro_set_layer_elevation,
    pro_set_scene_background,
    pro_create_feature_class,
    pro_delete_feature_class,
    pro_save_project,
    pro_add_attribute_index,
    pro_search_address,
    pro_open_attribute_table,
    pro_export_to_csv,
    pro_export_to_geo_json,
    pro_import_csv,
    pro_export_to_shapefile,
    pro_export_to_kml,
    pro_import_geo_json,
    pro_show_message,
    pro_show_progress_dialog,
    pro_set_status_bar_progress,
    pro_list_dockpanes,
    pro_activate_ribbon_tab,
    pro_list_domains,
    pro_create_domain,
    pro_assign_domain_to_field,
    pro_list_subtypes,
    pro_set_subtype_field,
    pro_enable_attachments,
    pro_list_toolboxes,
    pro_describe_tool,
    pro_get_geoprocessing_history,
    pro_run_python_script,
    pro_set_environment,
    pro_get_environment,
    pro_list_bookmarks,
    pro_zoom_to_bookmark,
    pro_reorder_layer,
    pro_set_labels_enabled,
    pro_open_dockpane,
    pro_export_layout_to_file,
    pro_fly_to_location,
    pro_ping_python_runtime,
    pro_plugin_batch_export,
    pro_plugin_coordinate_capture,
    pro_plugin_feature_inspector,
    pro_plugin_query_builder,
    pro_plugin_field_calculator,
    pro_run_macro,
    pro_list_macros,
    pro_resolve_layer,
    pro_create_snapshot,
    pro_restore_snapshot,
    pro_list_snapshots,
    pro_delete_snapshot,
)

from arcgis_mcp_named_pipe import is_addin_available, call_addin, AddInNotAvailableError

FIXTURE_DIR = PROJECT_ROOT / "test_project"
PRO_EXE = r"C:\Program Files\ArcGIS\Pro\bin\ArcGISPro.exe"


ToolFn = Any


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Live validate pro.* Add-In tools")
    parser.add_argument("--fixture", action="store_true", help="Use TestFixture.aprx layer names")
    parser.add_argument("--layer", default=None, help="Default layer name for tests")
    parser.add_argument("--bookmark", default="Default", help="Default bookmark name")
    parser.add_argument("--gdb", default=None, help="Default geodatabase path")
    parser.add_argument("--gen-fixture", action="store_true", help="Generate fixture project first (needs Pro Python)")
    return parser.parse_args()


def classify_result(op: str, result: dict[str, Any] | Exception) -> tuple[str, str]:
    """Classify a tool result as pass/fail/skip with a reason."""
    if isinstance(result, AddInNotAvailableError):
        return "SKIP", f"AddIn not available: {result}"
    if isinstance(result, Exception):
        return "FAIL", f"Exception: {type(result).__name__}: {result}"

    status = result.get("status", "unknown")
    if status == "ok":
        return "PASS", "ok"
    if status == "unavailable":
        return "SKIP", "AddIn unavailable (should not happen during live test)"
    if status == "error":
        msg = result.get("message", "")
        known_not_supported = [
            "not accessible from AddIn SDK",
            "not accessible from AddIn context",
            "not supported from AddIn",
            "not supported in AddIn",
        ]
        if any(k in msg for k in known_not_supported):
            return "SKIP", f"API limitation: {msg}"
        return "FAIL", f"Error: {msg}"
    return "FAIL", f"Unknown status: {status}"


def build_tool_tests(layer: str, bookmark: str, gdb: str) -> list[tuple[str, ToolFn, dict[str, Any], str]]:
    """Build tool test cases with the given parameter values."""

    return [
        # --- Base: ping ---
        ("pro.ping", pro_ping, {}, "Minimal connectivity check"),

        # --- Map & Selection ---
        ("pro.getActiveMapName", pro_get_active_map_name, {}, "Get active map name"),
        ("pro.listLayers", pro_list_layers, {}, "List layers in active map"),
        ("pro.countFeatures", pro_count_features, {"layer": layer}, "Count features in a layer"),
        ("pro.getLayerSchema", pro_get_layer_schema, {"layer": layer}, "Get layer field schema"),
        ("pro.getSelectionCount", pro_get_selection_count, {"layer": layer}, "Get selection count"),
        ("pro.selectByAttribute", pro_select_by_attribute, {"layer": layer, "where": "OBJECTID >= 0"}, "Select by attribute query"),
        ("pro.clearSelection", pro_clear_selection, {"layer": layer}, "Clear selection on layer"),
        ("pro.zoomToLayer", pro_zoom_to_layer, {"layer": layer}, "Zoom to layer extent"),
        ("pro.getCurrentExtent", pro_get_current_extent, {}, "Get current map extent"),
        ("pro.panToExtent", pro_pan_to_extent, {"xmin": -180, "ymin": -90, "xmax": 180, "ymax": 90}, "Pan to extent"),
        ("pro.getCamera", pro_get_camera, {}, "Get camera position"),
        ("pro.setLayerVisibility", pro_set_layer_visibility, {"layer": layer, "visible": True}, "Set layer visibility"),
        ("pro.getLayerExtent", pro_get_layer_extent, {"layer": layer}, "Get layer full extent"),
        ("pro.selectByRectangle", pro_select_by_rectangle, {"layer": layer, "xmin": -30, "ymin": -30, "xmax": 0, "ymax": 0}, "Select by rectangle"),
        ("pro.switchSelection", pro_switch_selection, {"layer": layer}, "Switch selection"),
        ("pro.getFeatureByOid", pro_get_feature_by_oid, {"layer": layer, "oid": 1}, "Get feature by OID"),

        # --- Bookmarks ---
        ("pro.listBookmarks", pro_list_bookmarks, {}, "List bookmarks"),
        ("pro.zoomToBookmark", pro_zoom_to_bookmark, {"name": bookmark}, "Zoom to bookmark"),

        # --- Layer Management ---
        ("pro.reorderLayer", pro_reorder_layer, {"layer": layer, "index": 0}, "Reorder layer"),
        ("pro.setLabelsEnabled", pro_set_labels_enabled, {"layer": layer, "enabled": True}, "Toggle labels"),
        ("pro.getLayerRenderer", pro_get_layer_renderer, {"layer": layer}, "Get layer renderer type"),
        ("pro.setLayerColor", pro_set_layer_color, {"layer": layer, "r": 255, "g": 0, "b": 0}, "Set layer color"),
        ("pro.setLayerTransparency", pro_set_layer_transparency, {"layer": layer, "transparency": 30}, "Set transparency"),
        ("pro.getLayerDescription", pro_get_layer_description, {"layer": layer}, "Get layer description"),
        ("pro.setLayerDescription", pro_set_layer_description, {"layer": layer, "description": "Test"}, "Set layer description"),
        ("pro.listSceneLayerTypes", pro_list_scene_layer_types, {}, "List scene layer types"),
        ("pro.countFeaturesByExpression", pro_count_features_by_expression, {"layer": layer, "where": "OBJECTID >= 0"}, "Count by expression"),
        ("pro.addLayerFromService", pro_add_layer_from_service, {"url": "https://sampleserver.arcgisonline.com/arcgis/rest/services/World/MapServer"}, "Add layer from service"),

        # --- Editing ---
        ("pro.undoEdit", pro_undo_edit, {}, "Undo edit"),
        ("pro.redoEdit", pro_redo_edit, {}, "Redo edit"),
        ("pro.getEditState", pro_get_edit_state, {}, "Get edit state"),
        ("pro.setSnapping", pro_set_snapping, {"enabled": True}, "Toggle snapping"),
        ("pro.deleteBookmark", pro_delete_bookmark, {"name": "_test_temp"}, "Delete bookmark"),
        ("pro.flashSelection", pro_flash_selection, {"layer": layer}, "Flash selection"),
        ("pro.selectAll", pro_select_all, {"layer": layer}, "Select all features"),
        ("pro.getActiveTool", pro_get_active_tool, {}, "Get active tool ID"),

        # --- Map Navigation ---
        ("pro.setMapScale", pro_set_map_scale, {"scale": 50000000}, "Set map scale"),
        ("pro.getMapScale", pro_get_map_scale, {}, "Get map scale"),
        ("pro.zoomToSelected", pro_zoom_to_selected, {"layer": layer}, "Zoom to selected"),
        ("pro.getAllMapNames", pro_get_all_map_names, {}, "Get all map names"),

        # --- Field / Schema ---
        ("pro.getLayerStatistics", pro_get_layer_statistics, {"layer": layer, "field": "VALUE"}, "Get field statistics"),
        ("pro.listFieldValues", pro_list_field_values, {"layer": layer, "field": "NAME"}, "List distinct field values"),
        ("pro.addField", pro_add_field, {"layer": layer, "field_name": "TEST_FLD", "field_type": "Text", "length": 50}, "Add field"),
        ("pro.deleteField", pro_delete_field, {"layer": layer, "field_name": "TEST_FLD"}, "Delete field"),
        ("pro.calculateField", pro_calculate_field, {"layer": layer, "field": "VALUE", "expression": "!VALUE! * 2"}, "Calculate field"),
        ("pro.renameField", pro_rename_field, {"layer": layer, "old_name": "OLD_FLD", "new_name": "NEW_FLD"}, "Rename field"),
        ("pro.addAttributeIndex", pro_add_attribute_index, {"layer": layer, "field": "OBJECTID"}, "Add attribute index"),

        # --- Feature Creation ---
        ("pro.createPointFeature", pro_create_point_feature, {"layer": layer, "x": 0, "y": 0}, "Create point feature"),
        ("pro.createPolygonFeature", pro_create_polygon_feature, {"layer": layer, "coordinates": "0,0 0,1 1,1 1,0 0,0"}, "Create polygon feature"),
        ("pro.createLineFeature", pro_create_line_feature, {"layer": layer, "coordinates": "0,0 1,1"}, "Create line feature"),

        # --- Delete / Update ---
        ("pro.deleteFeaturesByOid", pro_delete_features_by_oid, {"layer": layer, "oids": "1"}, "Delete features by OID"),
        ("pro.updateFeatureAttributes", pro_update_feature_attributes, {"layer": layer, "oid": 1, "attributes": '{"NAME": "Updated"}'}, "Update feature attributes"),

        # --- Split / Merge ---
        ("pro.splitFeatures", pro_split_features, {"layer": layer, "cut_geometry": '{"type":"Polyline","paths":[[[0,0],[1,1]]]}'}, "Split features"),
        ("pro.mergeFeatures", pro_merge_features, {"layer": layer, "object_ids": "[1,2]", "target_oid": 1}, "Merge features"),

        # --- Selection by geometry ---
        ("pro.selectByPolygon", pro_select_by_polygon, {"layer": layer, "coordinates": "-30,-30 0,-30 0,0 -30,0 -30,-30"}, "Select by polygon"),
        ("pro.selectByLayer", pro_select_by_layer, {"target_layer": layer, "source_layer": layer}, "Select by layer (spatial)"),
        ("pro.getFeaturesByExtent", pro_get_features_by_extent, {"layer": layer, "xmin": -30, "ymin": -30, "xmax": 30, "ymax": 30}, "Get features by extent"),
        ("pro.findFeatures", pro_find_features, {"layer": layer, "where": "OBJECTID >= 0"}, "Find features by attribute"),

        # --- 3D ---
        ("pro.is3d", pro_is_3d, {}, "Check if 3D scene"),
        ("pro.getElevationSources", pro_get_elevation_sources, {}, "Get elevation sources"),
        ("pro.setGroundOpacity", pro_set_ground_opacity, {"opacity": 50}, "Set ground opacity"),
        ("pro.setAtmosphere", pro_set_atmosphere, {"fog_density": 50}, "Set atmosphere"),
        ("pro.setSunPosition", pro_set_sun_position, {"azimuth": 180, "altitude": 45}, "Set sun position"),
        ("pro.getSunPosition", pro_get_sun_position, {}, "Get sun position"),
        ("pro.explore3D", pro_explore_3d, {"x": 0, "y": 0, "target_z": 100, "distance": 500}, "Explore 3D"),
        ("pro.setLayerElevation", pro_set_layer_elevation, {"layer": layer, "elevation_mode": "absolute", "z_offset": 0}, "Set layer elevation"),
        ("pro.setSceneBackground", pro_set_scene_background, {"r": 255, "g": 128, "b": 0}, "Set scene background"),
        ("pro.flyToLocation", pro_fly_to_location, {"x": 0, "y": 0, "z": 100}, "Fly to 3D location"),

        # --- Rendering ---
        ("pro.applyUniqueValueRenderer", pro_apply_unique_value_renderer, {"layer": layer, "field": "NAME"}, "Apply unique value renderer"),
        ("pro.applyClassBreaksRenderer", pro_apply_class_breaks_renderer, {"layer": layer, "field": "VALUE", "break_count": 5}, "Apply class breaks renderer"),

        # --- Layout ---
        ("pro.listLayouts", pro_list_layouts, {}, "List layouts"),
        ("pro.getMapFrame", pro_get_map_frame, {"layout_name": "Layout"}, "Get map frame"),
        ("pro.listLayoutElements", pro_list_layout_elements, {"layout_name": "Layout"}, "List layout elements"),
        ("pro.removeLayoutElement", pro_remove_layout_element, {"layout_name": "Layout", "element_name": "Text1"}, "Remove layout element"),
        ("pro.createLayout", pro_create_layout, {"layout_name": "TestLayout", "width": 297, "height": 210}, "Create layout"),
        ("pro.addLayoutText", pro_add_layout_text, {"layout_name": "TestLayout", "text": "Hello", "x": 10, "y": 20}, "Add layout text"),
        ("pro.addLayoutPicture", pro_add_layout_picture, {"layout_name": "TestLayout", "image_path": "C:\\nonexistent.png", "x": 10, "y": 10, "width": 50, "height": 50}, "Add layout picture"),
        ("pro.addLayoutLegend", pro_add_layout_legend, {"layout_name": "TestLayout", "x": 10, "y": 50}, "Add layout legend"),
        ("pro.addLayoutNorthArrow", pro_add_layout_north_arrow, {"layout_name": "TestLayout", "map_frame_name": "Map Frame", "x": 10, "y": 80}, "Add north arrow"),
        ("pro.exportLayoutToFile", pro_export_layout_to_file, {"layout_name": "TestLayout", "output_path": str(PROJECT_ROOT / "temp_test_layout.pdf")}, "Export layout to PDF"),

        # --- Map/Basemap ---
        ("pro.createMap", pro_create_map, {"map_name": "TestMap"}, "Create new map"),
        ("pro.addBasemap", pro_add_basemap, {"basemap_name": "Streets"}, "Add basemap"),

        # --- Geoprocessing ---
        ("pro.listToolboxes", pro_list_toolboxes, {}, "List geoprocessing toolboxes"),
        ("pro.describeTool", pro_describe_tool, {"tool_name": "Buffer_analysis"}, "Describe geoprocessing tool"),
        ("pro.listGpTools", pro_list_gp_tools, {}, "List GP tools"),
        ("pro.listGpHistory", pro_list_gp_history, {}, "List GP history"),
        ("pro.getGeoprocessingHistory", pro_get_geoprocessing_history, {}, "Get geoprocessing history"),
        ("pro.runPythonScript", pro_run_python_script, {"code": "print('hello from arcpy')"}, "Run Python script"),
        ("pro.setEnvironment", pro_set_environment, {"key": "workspace", "value": "C:\\temp"}, "Set GP environment"),
        ("pro.getEnvironment", pro_get_environment, {}, "Get GP environment"),

        # --- GP Tool wrappers ---
        ("pro.copyFeatures", pro_copy_features, {"layer": layer, "output_path": str(PROJECT_ROOT / "temp_test_copy")}, "Copy features"),
        ("pro.renameLayer", pro_rename_layer, {"layer": layer, "new_name": f"{layer} Renamed"}, "Rename layer"),
        ("pro.projectGeometry", pro_project_geometry, {"x": 500000, "y": 6900000, "from_wkid": 28356, "to_wkid": 4326}, "Project geometry"),
        ("pro.getGeometryDistance", pro_get_geometry_distance, {"x1": 0, "y1": 0, "x2": 10, "y2": 10}, "Get geometry distance"),
        ("pro.getProjectProperties", pro_get_project_properties, {}, "Get project properties"),

        # --- Data Exchange ---
        ("pro.exportToCsv", pro_export_to_csv, {"layer": layer, "output_path": str(PROJECT_ROOT / "temp_test.csv")}, "Export to CSV"),
        ("pro.exportToGeoJSON", pro_export_to_geo_json, {"layer": layer, "output_path": str(PROJECT_ROOT / "temp_test.geojson")}, "Export to GeoJSON"),
        ("pro.exportToShapefile", pro_export_to_shapefile, {"layer": layer, "output_path": str(PROJECT_ROOT / "temp_test.shp")}, "Export to shapefile"),
        ("pro.exportToKml", pro_export_to_kml, {"layer": layer, "output_path": str(PROJECT_ROOT / "temp_test.kmz")}, "Export to KML"),
        ("pro.importCsv", pro_import_csv, {"csv_path": str(PROJECT_ROOT / "temp_test.csv"), "gdb_path": gdb, "fc_name": "TestImport", "x_field": "X", "y_field": "Y"}, "Import CSV"),
        ("pro.importGeoJSON", pro_import_geo_json, {"geojson_path": str(PROJECT_ROOT / "temp_test.geojson"), "gdb_path": gdb, "fc_name": "TestImport"}, "Import GeoJSON"),
        ("pro.searchAddress", pro_search_address, {"address": "123 Main St"}, "Search address"),
        ("pro.openAttributeTable", pro_open_attribute_table, {"layer": layer}, "Open attribute table"),

        # --- Schema Management ---
        ("pro.createFeatureClass", pro_create_feature_class, {"gdb_path": gdb, "name": "TestFC", "geometry_type": "Point"}, "Create feature class"),
        ("pro.deleteFeatureClass", pro_delete_feature_class, {"path": f"{gdb}\\TestFC"}, "Delete feature class"),
        ("pro.saveProject", pro_save_project, {}, "Save project"),
        ("pro.listDomains", pro_list_domains, {"gdb_path": gdb}, "List domains"),
        ("pro.createDomain", pro_create_domain, {"gdb_path": gdb, "name": "TestDomain", "description": "Test", "field_type": "Text"}, "Create domain"),
        ("pro.assignDomainToField", pro_assign_domain_to_field, {"layer": layer, "field": "NAME", "domain_name": "TestDomain"}, "Assign domain to field"),
        ("pro.listSubtypes", pro_list_subtypes, {"layer": layer}, "List subtypes"),
        ("pro.setSubtypeField", pro_set_subtype_field, {"layer": layer, "field": "NAME"}, "Set subtype field"),
        ("pro.enableAttachments", pro_enable_attachments, {"layer": layer}, "Enable attachments"),

        # --- UI ---
        ("pro.showMessage", pro_show_message, {"message": "Test from live validator", "type": "info"}, "Show message dialog"),
        ("pro.showProgressDialog", pro_show_progress_dialog, {"title": "Working", "message": "Please wait"}, "Show progress dialog"),
        ("pro.setStatusBarMessage", pro_set_status_bar_message, {"message": "Live validation in progress"}, "Set status bar message"),
        ("pro.setStatusBarProgress", pro_set_status_bar_progress, {"percent": 50, "message": "Halfway"}, "Set status bar progress"),
        ("pro.listDockpanes", pro_list_dockpanes, {}, "List dockpanes"),
        ("pro.activateRibbonTab", pro_activate_ribbon_tab, {"tab_id": "Map"}, "Activate ribbon tab"),
        ("pro.openDockpane", pro_open_dockpane, {"dockpane_id": "Contents"}, "Open dockpane"),

        # --- Time Slider ---
        ("pro.isTimeEnabled", pro_is_time_enabled, {}, "Check if time enabled"),
        ("pro.getTimeExtent", pro_get_time_extent, {}, "Get time extent"),
        ("pro.setTimeExtent", pro_set_time_extent, {"start": "2020-01-01", "end": "2025-12-31"}, "Set time extent"),

        # --- Standalone Tables ---
        ("pro.listStandaloneTables", pro_list_standalone_tables, {}, "List standalone tables"),

        # --- GP Tool ---
        ("pro.runGpTool", pro_run_gp_tool, {"tool_name": "GetCount", "parameters": json.dumps([layer])}, "Run GP tool"),

        # --- In-Process Python ---
        ("pro.pingPythonRuntime", pro_ping_python_runtime, {}, "Ping Python runtime"),

        # --- Plugin Tools ---
        ("pro.plugin.batchExport", pro_plugin_batch_export, {"target_format": "csv", "output_dir": str(PROJECT_ROOT / "temp_export")}, "Batch export CSV"),
        ("pro.plugin.coordinateCapture", pro_plugin_coordinate_capture, {"target_wkid": 4326}, "Capture coordinates"),
        ("pro.plugin.featureInspector", pro_plugin_feature_inspector, {"layer": layer, "oid": 1}, "Inspect feature"),
        ("pro.plugin.queryBuilder", pro_plugin_query_builder, {"layer": layer, "field": "NAME", "value": "Test", "operator_name": "contains"}, "Query builder"),
        ("pro.plugin.fieldCalculator", pro_plugin_field_calculator, {"layer": layer, "field": "VALUE", "expression": "!VALUE! * 2"}, "Field calculator"),

        # --- Workflow Macros ---
        ("pro.listMacros", pro_list_macros, {}, "List built-in macros"),
        ("pro.runMacro", pro_run_macro, {"macro": "Select and Zoom"}, "Run built-in macro"),

        # --- Layer Resolution ---
        ("pro.resolveLayer", pro_resolve_layer, {"layer_hint": layer}, "Resolve layer name"),

        # --- Safety Net (Snapshots) ---
        ("pro.listSnapshots", pro_list_snapshots, {}, "List snapshots"),
        ("pro.createSnapshot", pro_create_snapshot, {"layer": layer}, "Create snapshot"),
        ("pro.deleteSnapshot", pro_delete_snapshot, {"snapshot_name": "snap_Test_001"}, "Delete snapshot"),
    ]


def run_tool(name: str, fn: ToolFn, kwargs: dict[str, Any]) -> dict[str, Any]:
    """Execute a tool function, catching all exceptions."""
    try:
        result = fn(**kwargs)
        status, reason = classify_result(name, result)
        return {"name": name, "status": status, "reason": reason, "result": result}
    except Exception as e:
        status, reason = classify_result(name, e)
        return {"name": name, "status": status, "reason": reason, "result": str(e)}


def main() -> int:
    args = parse_args()

    # Generate fixture if requested
    if args.gen_fixture:
        pro_python = r"C:\Program Files\ArcGIS\Pro\bin\Python\envs\arcgispro-py3\python.exe"

        gdb_script = PROJECT_ROOT / "scripts" / "create_test_fixture.py"
        aprx_script = PROJECT_ROOT / "scripts" / "create_test_aprx.py"

        for script, label in [(gdb_script, "GDB"), (aprx_script, ".aprx")]:
            if not script.exists():
                print(f"  {label} script not found: {script}")
                return 1
            print(f"  Generating {label} with: {pro_python}")
            result = subprocess.run(
                [pro_python, str(script)], capture_output=True, text=True, timeout=120
            )
            print(result.stdout)
            if result.returncode != 0:
                print(f"  {label} generation failed:\n{result.stderr}")
                return 1

    # Resolve layer, bookmark, and GDB
    if args.fixture:
        fixture_aprx = FIXTURE_DIR / "TestFixture.aprx"
        fixture_gdb = FIXTURE_DIR / "TestFixture" / "TestFixture.gdb"
        if not fixture_gdb.exists():
            print(f"Fixture GDB not found at {fixture_gdb}")
            print("Run with --gen-fixture to generate it, or use --layer / --gdb directly.")
            return 1
        layer = args.layer or "TestPoints"
        bookmark = args.bookmark or "FullExtent"
        gdb = args.gdb or str(fixture_gdb)
        print(f"Fixture mode: layer={layer}, bookmark={bookmark}, gdb={gdb}")
        if fixture_aprx.exists():
            print(f"  Open this project in Pro: {fixture_aprx}")
    else:
        layer = args.layer or "World Cities"
        bookmark = args.bookmark or "Default"
        gdb = args.gdb or r"C:\temp.gdb"

    TOOL_TESTS = build_tool_tests(layer, bookmark, gdb)

    timestamp = datetime.now(timezone.utc).isoformat()
    print(f"\n=== ArcGIS Pro Add-In Live Validation ===\nStarted: {timestamp}\n")

    # Check if add-in is available
    avail = is_addin_available()
    if not avail:
        print("Add-In NOT available. Attempting to launch Pro...")
        try:
            subprocess.Popen([PRO_EXE])
            print("Waiting up to 60s for pipe...")
            deadline = time.monotonic() + 60
            while time.monotonic() < deadline:
                time.sleep(2)
                if is_addin_available():
                    print("Pro started and Add-In connected.")
                    avail = True
                    break
            if not avail:
                print("Timed out waiting for Pro. Starting tests anyway (all will SKIP).")
        except FileNotFoundError:
            print(f"Pro executable not found at {PRO_EXE}. Starting tests anyway.")
    else:
        print("Add-In is available.")

    results: list[dict[str, Any]] = []
    total = len(TOOL_TESTS)
    passed = skipped = failed = 0

    for i, (name, fn, kwargs, desc) in enumerate(TOOL_TESTS, 1):
        print(f"  [{i}/{total}] {name}: ", end="", flush=True)
        r = run_tool(name, fn, kwargs)
        results.append(r)
        if r["status"] == "PASS":
            passed += 1
            print("PASS")
        elif r["status"] == "SKIP":
            skipped += 1
            print(f"SKIP ({r['reason']})")
        else:
            failed += 1
            print(f"FAIL ({r['reason']})")

    # Summary
    print(f"\n=== Summary ===")
    print(f"  Total:  {total}")
    print(f"  Passed: {passed}")
    print(f"  Skipped: {skipped}")
    print(f"  Failed: {failed}")
    print(f"  Pass rate: {100 * passed / total:.1f}%")

    # Save report
    report = {
        "timestamp": timestamp,
        "addin_available": avail,
        "total": total,
        "passed": passed,
        "skipped": skipped,
        "failed": failed,
        "fixture_mode": args.fixture,
        "layer": layer,
        "gdb": gdb,
        "results": results,
    }
    report_path = Path("live_test_report.json")
    report_path.write_text(json.dumps(report, indent=2, default=str))
    print(f"\nReport saved to: {report_path.resolve()}")

    return 1 if failed > 0 else 0


if __name__ == "__main__":
    sys.exit(main())
