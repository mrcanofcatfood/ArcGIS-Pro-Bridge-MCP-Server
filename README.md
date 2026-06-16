# ArcGIS Pro Bridge MCP Server

Connect AI agents (OpenCode, Claude, Cursor) to ArcGIS Pro for AI-assisted GIS workflows — with **144 real-time tools** and **21 file-based geoprocessing tools**.

## Features

- **Two modes, one server** — file-based tools (no Add-In needed) + real-time Add-In tools (live Pro session)
- **144 `pro.*` tools** — map navigation, feature editing, schema management, 3D scenes, layouts, geoprocessing, data exchange, plugins, workflow macros, edit safety net, Python micro-plugins, and more
- **Plugin system** — write custom C# handlers without modifying core code
- **Workflow macros** — run named multi-step sequences (`pro_run_macro`)
- **Natural language resolution** — fuzzy layer/field name matching (`pro_resolve_layer`)
- **In-process Python** — pythonnet POC for direct arcpy access from Add-In context
- **21 file-based tools** — project inspection, GDB schema, buffer/clip, raster suitability analysis

## Architecture

```
┌─────────────────────────────────────────────────────┐
│                  MCP Client (OpenCode)               │
└──────────────┬──────────────────────────┬────────────┘
               │                          │
    ┌──────────▼──────────┐    ┌──────────▼──────────┐
     │  File-Based Tools    │    │  Real-Time Add-In    │
     │  (arcpy subprocess)  │    │  (Named Pipe IPC)    │
     │  21 tools            │    │  144 `pro.*` tools   │
     │  No Add-In needed    │    │  Requires Add-In     │
    └──────────────────────┘    └──────────┬──────────┘
                                           │
                                    ┌──────▼──────┐
                                    │ C# Add-In   │
                                    │ APBridge    │
                                    │ (in Pro)    │
                                    └─────────────┘
```

| Mode | Tools | When to Use | Requires |
|------|-------|-------------|----------|
| **File-Based** | 21 tools (`inspect_project_context`, `buffer_features`, `execute_arcpy_code`, etc.) | Heavy geoprocessing, batch ops, automated CI/CD, Pro closed or under load | Nothing extra |
| **Real-Time (Add-In)** | 144 `pro.*` tools (map, edit, schema, 3D, layout, GP, plugins, macros) | Interactive selection, live map inspection, feature editing, navigation | Add-In install + Pro running + Named Pipe |

## Quickstart

```bash
# 1. Install dependencies
uv sync

# 2. Configure OpenCode (opencode.json)
# Set the command to ArcGIS Pro's python.exe + server script

# 3. Start
opencode
# Run /mcp to verify "arcgis-pro" shows Connected
```

### Configure `opencode.json`

```json
{
  "mcp": {
    "arcgis-pro": {
      "command": [
        "C:\\Program Files\\ArcGIS\\Pro\\bin\\Python\\envs\\arcgispro-py3\\python.exe",
        "C:\\path\\to\\arcgis-opencode-mcp\\arcgis_mcp_server.py"
      ],
      "enabled": true
    }
  },
  "permission": {
    "mcp": { "arcgis-pro": "ask" }
  },
  "instructions": ["./AGENTS.md"]
}
```

### Try It

```text
# File-Based (works immediately):
Run a health check, then summarize the parcels layer in my project.
Buffer the roads layer by 50 meters.

# Real-Time (requires Add-In):
List layers in the active map, then select all parcels with ZONE = 'Residential'.
Create a point feature at (1645400, 4857300) on the Monuments layer.
```

## Plugin System

Extend the Add-In with custom `pro.*` handlers in C# — no need to modify `ProBridgeService.cs`.

```csharp
[ProBridgeHandler("pro.myTool")]
public class MyHandler : IProBridgeHandler
{
    public string Op => "pro.myTool";
    public Task<IpcResponse> Handle(IpcRequest request, CancellationToken ct)
    {
        return Task.FromResult(new IpcResponse(true, null, new { result = "ok" }));
    }
}
```

1. Copy `addin/plugins/ExamplePlugin/` → rename
2. Implement `IProBridgeHandler` with `[ProBridgeHandler("pro.yourTool")]`
3. `dotnet build your-plugin.csproj`
4. `addin/APBridgeAddIn/package-addin.ps1 -Action install`

See [docs/plugin-handler-system.md](docs/plugin-handler-system.md) for the full guide.

## Install the Add-In (Real-Time Mode)

Build and install the C# Add-In for live Pro interaction:

1. **Open `addin/APBridgeAddIn/APBridgeAddIn.csproj`** in Visual Studio 2022 with [ArcGIS Pro SDK](https://github.com/Esri/arcgis-pro-sdk) installed
2. **Build** — produces `APBridgeAddIn.esriAddInX`
3. **Double-click** the `.esriAddInX` to install
4. **Start (or restart) ArcGIS Pro** — the Named Pipe bridge starts automatically

The `pro_ping` tool returns `"unavailable"` with setup instructions if the Add-In is not reachable.

**One-time build only** — no Visual Studio required for daily use.

## Tool Categories

Full API reference at [API_REFERENCE.md](API_REFERENCE.md) (auto-generated).

| Category | Count | Examples |
|----------|:-----:|----------|
| **Map & Selection** | 23 | `pro_ping`, `pro_list_layers`, `pro_select_by_attribute`, `pro_zoom_to_layer`, `pro_get_current_extent` |
| **Editing** | 14 | `pro_create_point_feature`, `pro_split_features`, `pro_merge_features`, `pro_undo_edit`, `pro_delete_features_by_oid` |
| **Schema** | 10 | `pro_add_field`, `pro_delete_field`, `pro_create_feature_class`, `pro_list_subtypes`, `pro_calculate_field` |
| **Domains** | 3 | `pro_list_domains`, `pro_create_domain`, `pro_assign_domain_to_field` |
| **Layer Management** | 17 | `pro_set_layer_visibility`, `pro_set_layer_color`, `pro_set_layer_transparency`, `pro_rename_layer`, `pro_copy_features` |
| **3D / Viz** | 13 | `pro_get_camera`, `pro_fly_to_location`, `pro_set_atmosphere`, `pro_set_sun_position`, `pro_apply_class_breaks_renderer` |
| **Layouts** | 10 | `pro_create_layout`, `pro_add_layout_text`, `pro_export_layout_to_file`, `pro_list_layouts`, `pro_add_layout_legend` |
| **Map Management** | 3 | `pro_get_all_map_names`, `pro_create_map`, `pro_add_basemap` |
| **Geoprocessing** | 9 | `pro_run_gp_tool`, `pro_run_python_script`, `pro_list_toolboxes`, `pro_set_environment`, `pro_describe_tool` |
| **Data Exchange** | 8 | `pro_export_to_csv`, `pro_export_to_geo_json`, `pro_import_csv`, `pro_search_address`, `pro_open_attribute_table` |
| **UI / GUI** | 7 | `pro_show_message`, `pro_activate_ribbon_tab`, `pro_open_dockpane`, `pro_list_dockpanes` |
| **Time Slider** | 3 | `pro_is_time_enabled`, `pro_get_time_extent`, `pro_set_time_extent` |
| **Bookmarks** | 4 | `pro_list_bookmarks`, `pro_zoom_to_bookmark`, `pro_create_bookmark` |
| **Attachments** | 1 | `pro_enable_attachments` |
| **Standalone Tables** | 1 | `pro_list_standalone_tables` |
| **In-Process Python** | 1 | `pro_ping_python_runtime` |
| **Plugin Tools** | 5 | `pro_plugin_batch_export`, `pro_plugin_feature_inspector`, `pro_plugin_query_builder` |
| **Workflow Macros** | 2 | `pro_run_macro`, `pro_list_macros` |
| **Layer Resolution** | 1 | `pro_resolve_layer` |
| **Utility** | 4 | `pro_get_project_properties`, `pro_save_project`, `pro_project_geometry`, `pro_get_geometry_distance` |
| **Find Features** | 1 | `pro_find_features` |
| **Selection UX** | 3 | `pro_flash_selection`, `pro_select_all`, `pro_switch_selection` |

### File-Based Tools

| Tool | Description |
|------|-------------|
| `ping`, `health_check`, `doctor`, `detect_arcgis_environment` | Diagnostics |
| `inspect_project_context`, `list_gis_layers`, `inspect_gdb` | Project & data inspection |
| `buffer_features`, `clip_features` | Geoprocessing |
| `execute_arcpy_code` | Run arbitrary ArcPy |
| `validate_project_data`, `prepare_analysis_inputs`, `reclassify_criteria` | Suitability analysis prep |
| `weighted_suitability`, `conflict_analysis`, `raster_area_summary`, `sensitivity_check`, `export_suitability_map` | Suitability modeling |
| `generate_sync_plan`, `build_gis_resource_uri`, `debug_runtime_context` | Utilities |

## MCP Resources

| URI | Description |
|-----|-------------|
| `arcgis://server/status` | Server and ArcGIS status |
| `arcgis://project/current/layers` | Current project layers |
| `arcgis://project/{project_ref}/layers` | Specific project layers |
| `arcgis://project/current/context` | Current project context |
| `arcgis://gdb/{gdb_ref}/schema` | GDB schema |

## Configuration Examples

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
  "mcpServers": {
    "arcgis-pro": {
      "command": ["uv", "run", "--directory", "path/to/arcgis-opencode-mcp", "python", "arcgis_mcp_server.py"]
    }
  }
}
```

### Claude Desktop
See [examples/claude-desktop-mcp-config.json](examples/claude-desktop-mcp-config.json)

## Safety

- `execute_arcpy_code` requires explicit user confirmation
- Read-only by default for inspection tools
- Do not expose to public networks
- Always backup data before running geoprocessing

## Documentation

| File | Contents |
|------|----------|
| [AGENTS.md](AGENTS.md) | Agent instructions and workflow guide |
| [API_REFERENCE.md](API_REFERENCE.md) | Full 144-tool API reference |
| [LIMITATIONS.md](LIMITATIONS.md) | Known limitations and blocked APIs |
| [CHANGELOG.md](CHANGELOG.md) | Version history |
| [docs/plugin-handler-system.md](docs/plugin-handler-system.md) | Plugin development guide |
| [examples/](examples/) | MCP config examples and prompt templates |

## License

MIT - See [LICENSE](LICENSE)
