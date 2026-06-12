#!/usr/bin/env python3
"""Generate API_REFERENCE.md from @mcp.tool() docstrings and signatures."""

from __future__ import annotations

import ast
import inspect
import re
import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(PROJECT_ROOT))

# Import all pro_* tools for signature inspection
from arcgis_mcp_server import _call_addin

import arcgis_mcp_server as server

TOOLS_CATEGORIES = {
    "Map & Selection": [
        "pro_ping", "pro_get_active_map_name", "pro_list_layers",
        "pro_count_features", "pro_count_features_by_expression",
        "pro_get_layer_schema", "pro_get_selection_count",
        "pro_select_by_attribute", "pro_clear_selection", "pro_zoom_to_layer",
        "pro_get_current_extent", "pro_pan_to_extent", "pro_get_camera",
        "pro_set_layer_visibility", "pro_get_layer_extent", "pro_select_by_rectangle",
        "pro_switch_selection", "pro_get_feature_by_oid", "pro_get_active_tool",
        "pro_select_by_polygon", "pro_select_by_layer", "pro_get_features_by_extent",
        "pro_find_features",
    ],
    "Editing": [
        "pro_undo_edit", "pro_redo_edit", "pro_get_edit_state",
        "pro_set_active_tool", "pro_delete_features_by_oid",
        "pro_update_feature_attributes", "pro_create_point_feature",
        "pro_create_polygon_feature", "pro_create_line_feature",
        "pro_split_features", "pro_merge_features", "pro_set_snapping",
        "pro_select_all", "pro_flash_selection",
    ],
    "Layer Management": [
        "pro_reorder_layer", "pro_remove_layer", "pro_add_layer_from_file",
        "pro_add_layer_from_service", "pro_rename_layer", "pro_set_layer_visibility",
        "pro_set_layer_transparency", "pro_set_layer_color",
        "pro_set_labels_enabled", "pro_get_layer_renderer",
        "pro_get_layer_extent", "pro_get_layer_description",
        "pro_set_layer_description", "pro_get_layer_statistics",
        "pro_list_scene_layer_types", "pro_copy_features",
        "pro_list_field_values",
    ],
    "Map Navigation": [
        "pro_get_map_scale", "pro_set_map_scale", "pro_zoom_to_selected",
        "pro_get_current_extent", "pro_pan_to_extent",
    ],
    "Bookmarks": [
        "pro_list_bookmarks", "pro_zoom_to_bookmark", "pro_create_bookmark",
        "pro_delete_bookmark",
    ],
    "Schema Management": [
        "pro_get_layer_schema", "pro_add_field", "pro_delete_field",
        "pro_calculate_field", "pro_rename_field", "pro_add_attribute_index",
        "pro_create_feature_class", "pro_delete_feature_class",
        "pro_list_subtypes", "pro_set_subtype_field",
    ],
    "Domains": [
        "pro_list_domains", "pro_create_domain", "pro_assign_domain_to_field",
    ],
    "3D & Visualization": [
        "pro_is_3d", "pro_get_camera", "pro_fly_to_location",
        "pro_get_elevation_sources", "pro_set_ground_opacity",
        "pro_set_atmosphere", "pro_set_sun_position", "pro_get_sun_position",
        "pro_explore_3d", "pro_set_layer_elevation", "pro_set_scene_background",
        "pro_apply_unique_value_renderer", "pro_apply_class_breaks_renderer",
    ],
    "Layouts": [
        "pro_list_layouts", "pro_get_map_frame", "pro_list_layout_elements",
        "pro_remove_layout_element", "pro_export_layout_to_file",
        "pro_create_layout", "pro_add_layout_text", "pro_add_layout_picture",
        "pro_add_layout_legend", "pro_add_layout_north_arrow",
    ],
    "Map Management": [
        "pro_get_all_map_names", "pro_create_map", "pro_add_basemap",
    ],
    "Geoprocessing": [
        "pro_list_toolboxes", "pro_describe_tool", "pro_list_gp_tools",
        "pro_list_gp_history", "pro_get_geoprocessing_history",
        "pro_run_gp_tool", "pro_run_python_script", "pro_set_environment",
        "pro_get_environment",
    ],
    "Data Exchange": [
        "pro_export_to_csv", "pro_export_to_geo_json",
        "pro_export_to_shapefile", "pro_export_to_kml",
        "pro_import_csv", "pro_import_geo_json",
        "pro_search_address", "pro_open_attribute_table",
    ],
    "UI / GUI Automation": [
        "pro_show_message", "pro_show_progress_dialog",
        "pro_set_status_bar_message", "pro_set_status_bar_progress",
        "pro_list_dockpanes", "pro_activate_ribbon_tab", "pro_open_dockpane",
    ],
    "Time Slider": [
        "pro_is_time_enabled", "pro_get_time_extent", "pro_set_time_extent",
    ],
    "Attachments": [
        "pro_enable_attachments",
    ],
    "Standalone Tables": [
        "pro_list_standalone_tables",
    ],
    "Project & Utility": [
        "pro_get_project_properties", "pro_get_geometry_distance",
        "pro_project_geometry", "pro_save_project",
    ],
    "In-Process Python": [
        "pro_ping_python_runtime",
    ],
    "Plugin Tools": [
        "pro_plugin_batch_export", "pro_plugin_coordinate_capture",
        "pro_plugin_feature_inspector", "pro_plugin_query_builder",
        "pro_plugin_field_calculator",
    ],
    "Workflow Macros": [
        "pro_run_macro", "pro_list_macros",
    ],
    "Layer Resolution": [
        "pro_resolve_layer",
    ],
}

# Non-pro tools
GIS_TOOLS = [
    ("ping", "MCP connectivity check", None),
    ("health_check", "ArcGIS environment status", None),
    ("doctor", "Comprehensive diagnostic report", None),
    ("detect_arcgis_environment", "Discover ArcGIS Pro Python", None),
    ("inspect_project_context", "Full project overview", "aprx_path"),
    ("list_gis_layers", "List all layers in project", "aprx_path"),
    ("inspect_gdb", "GDB schema inspection", "gdb_path"),
    ("buffer_features", "Buffer analysis", "in_features, out_feature_class, buffer_distance"),
    ("clip_features", "Clip analysis", "in_features, clip_features, out_feature_class"),
    ("execute_arcpy_code", "Run arbitrary ArcPy code", "code"),
    ("validate_project_data", "Pre-flight data validation", "project_gdb"),
    ("prepare_analysis_inputs", "One-shot data preparation", "project_gdb, study_area_fc"),
    ("reclassify_criteria", "Batch reclassify rasters", "reclass_table, output_gdb"),
    ("weighted_suitability", "Weighted Linear Combination", "model_name, criteria, output_raster"),
    ("conflict_analysis", "Conflict zones and allocation", "conservation_raster, urban_raster"),
    ("raster_area_summary", "Area statistics by class", "raster_path"),
    ("sensitivity_check", "Weight perturbation sensitivity", "5 rasters + weights params"),
    ("export_suitability_map", "Layout creation and export", "project_path, raster_path"),
    ("build_gis_resource_uri", "Build readable ArcGIS resource URI", "resource_kind"),
    ("generate_sync_plan", "Generate data sync plan", "source_description"),
    ("debug_runtime_context", "Debug runtime environment", None),
]


def get_tool_docstring(name: str) -> str:
    fn = getattr(server, name, None)
    if fn is None:
        return ""
    return inspect.getdoc(fn) or ""


def get_tool_signature(name: str) -> str:
    fn = getattr(server, name, None)
    if fn is None:
        return ""
    try:
        sig = inspect.signature(fn)
        params = []
        for p in sig.parameters.values():
            if p.default is inspect.Parameter.empty:
                params.append(str(p))
            else:
                params.append(f"{p.name}?")
        return ", ".join(params)
    except (ValueError, TypeError):
        return ""


def main() -> int:
    lines = []

    lines.append("# API Reference")
    lines.append("")
    lines.append("Auto-generated from `@mcp.tool()` function signatures and docstrings.")
    lines.append("")
    lines.append("<!-- GENERATED: do not edit manually. Run `python scripts/generate_api_docs.py` to regenerate -->")
    lines.append("")

    lines.append("---")
    lines.append("## GIS Data Tools (File-Based)")
    lines.append("")
    lines.append("These tools work without the C# Add-In. They read `.aprx` files from disk or execute ArcPy in a subprocess.")
    lines.append("")
    lines.append("| Tool | Description | Key Parameters |")
    lines.append("|------|-------------|----------------|")
    for name, desc, params in GIS_TOOLS:
        lines.append(f"| `{name}` | {desc} | {params or '—'} |")
    lines.append("")

    lines.append("---")
    lines.append("## Real-Time Add-In Tools (require APBridgeAddIn)")
    lines.append("")
    unique_tools = len({t for tools in TOOLS_CATEGORIES.values() for t in tools})
    lines.append(f"**{unique_tools} tools** across {len(TOOLS_CATEGORIES)} categories.")
    lines.append("")
    lines.append("> Tip: Use `pro_ping` to check if the Add-In is available. Returns `{\"status\": \"ok\"}` when connected, `{\"status\": \"unavailable\"}` otherwise.")
    lines.append("")

    for category, tool_names in TOOLS_CATEGORIES.items():
        lines.append(f"### {category}")
        lines.append("")
        lines.append("| Tool | Signature | Description |")
        lines.append("|------|-----------|-------------|")
        for name in tool_names:
            sig = get_tool_signature(name)
            doc = get_tool_docstring(name)
            # Truncate long descriptions to first sentence
            doc_short = doc.split(".")[0] + "." if doc else ""
            lines.append(f"| `{name}` | `({sig})` | {doc_short} |")
        lines.append("")

    lines.append("---")
    lines.append("## Return Values")
    lines.append("")
    lines.append("All `pro.*` tools return a consistent JSON structure:")
    lines.append("")
    lines.append("| Field | Type | Description |")
    lines.append("|-------|------|-------------|")
    lines.append("| `status` | string | `\"ok\"` on success, `\"error\"` on failure, `\"unavailable\"` when Add-In not reachable |")
    lines.append("| `data` | any | The tool's response payload (present when `status == \"ok\"`) |")
    lines.append("| `message` | string | Error description (present when `status != \"ok\"`) |")
    lines.append("| `next_step` | string | Recovery instructions (present when `status == \"unavailable\"`) |")
    lines.append("")

    out_path = PROJECT_ROOT / "API_REFERENCE.md"
    out_path.write_text("\n".join(lines), encoding="utf-8")
    print(f"Written: {out_path} ({len(lines)} lines)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
