# Future Development

## Option 2: C# Add-In (Real-time Project Access)

This document describes real-time, in-process access to ArcGIS Pro through a C# Add-In.

**Status:** Implemented in `addin/APBridgeAddIn/` — communicates with the Python MCP server via `arcgis_mcp_named_pipe.py` over Named Pipes.

---

## Why This Matters

The current Python MCP server (this repo) accesses ArcGIS Pro projects via `.aprx` file paths. This means:

| Approach | Pros | Cons |
|----------|------|------|
| Current (file-based) | Works without Pro running, simple | No real-time state, Pro must be closed for writes |
| Option 2 (Add-In) | Real-time `MapView.Active`, live state | Requires Pro running with Add-In, C# development |

---

## Architecture Overview

```
OpenCode / MCP Client
    |
    v
Python MCP Server (arcgis_mcp_server.py)
    |
    ├── geoprocessing tools → ArcPy subprocess (existing)
    |
    └── pro.* tools → Named Pipe (pywin32) → APBridgeAddIn (in-process Pro SDK)
                                                    |
                                                    v
                                             ArcGIS Pro SDK
                                        (MapView.Active, layers, etc.)
```

---

## Prerequisites

- Visual Studio 2022 (with ArcGIS Pro SDK for .NET) — for one-time Add-In build
- ArcGIS Pro installed
- Python 3.11+ with `pywin32`

---

## Implementation Reference

This approach was inspired by [nicogis/MCP-Server-ArcGIS-Pro-AddIn](https://github.com/nicogis/MCP-Server-ArcGIS-Pro-AddIn), adapted to use the Python MCP server directly as the Named Pipe client instead of a separate .NET MCP server.

Key features integrated:
- Named Pipe IPC for in-process communication
- `MapView.Active` access for real-time map state
- 10 tools covering map, layer, selection, and extent operations

---

## Implemented Tools

| Tool | Description |
|------|-------------|
| `pro_ping` | Ping Add-In pipe connectivity |
| `pro_get_active_map_name` | Get name of active map |
| `pro_list_layers` | List all layers with visibility and type |
| `pro_count_features` | Count features in a layer |
| `pro_get_layer_schema` | Get field schema of a layer |
| `pro_get_selection_count` | Count selected features |
| `pro_select_by_attribute` | Select features by SQL |
| `pro_clear_selection` | Clear selection on layer or all |
| `pro_zoom_to_layer` | Zoom to layer extent |
| `pro_get_current_extent` | Get current map extent |
| `pro_pan_to_extent` | Pan to bounding box |

---

## When to Use Add-In Tools

Use `pro.*` tools (requires Add-In) when you need:
- Real-time access to currently open ArcGIS Pro project
- Live layer selections and map state
- Zoom/pan operations from AI
- Integration with Pro's active editing session

Stick with existing `arcgis.*` tools when:
- You need to work without ArcGIS Pro running
- You prefer not to install the Add-In
- You need geoprocessing/raster analysis operations