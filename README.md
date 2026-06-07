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