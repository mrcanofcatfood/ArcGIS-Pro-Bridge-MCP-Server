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
- 126 tools covering map, layer, selection, editing, layout, 3D, data management, import/export, GUI automation, schema management, and advanced geoprocessing

---

## Implemented Tools

126 `pro.*` tools across 14 phases — see `README.md` for the full table.

| Phase | Tools | Category |
|-------|-------|----------|
| Base | 11 | Map query, selection, extent |
| 0 | 10 | Camera, layer properties, selection operations |
| 1 | 8 | Rendering, layer management, layout query |
| 2 | 8 | 3D detection, transparency, bookmarks, time |
| 3 | 8 | Field CRUD, attachments |
| 4 | 10 | Snapping, bookmarks, time, layout export |
| 5 | 10 | Descriptions, standalone tables, relates |
| 6 | 10 | Spatial selection, feature CRUD, renderers |
| 7 | 8 | Split/merge, GP execution, statistics, projection |
| 8 | 8 | Layout text/pictures/legends, map creation |
| 9 | 6 | Atmosphere, sun, elevation, 3D camera |
| 10 | 6 | Feature class CRUD, save, indexes, address search |
| 11 | 6 | CSV, GeoJSON, shapefile, KML exchange |
| 12 | 6 | Message dialogs, progress bar, dockpanes, ribbon |
| 13 | 6 | Domains, subtypes, attachments |
| 14 | 6 | Toolbox browsing, Python execution, GP history, environments |

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