# Phase 5: GUI Interaction & Editing Workflow

## Overview

Add 10 new `pro.*` tools focusing on map navigation, editing workflow,
bookmark management, selection UX, and UI feedback.

## Tools

| # | Tool | C# Handler | Description |
|---|------|------------|-------------|
| 1 | `pro.setMapScale` | `HandleSetMapScale` | Set exact map scale |
| 2 | `pro.getMapScale` | `HandleGetMapScale` | Get current map scale |
| 3 | `pro.zoomToSelected` | `HandleZoomToSelected` | Zoom to selected features |
| 4 | `pro.getEditState` | `HandleGetEditState` | Get undo/redo counts |
| 5 | `pro.setSnapping` | `HandleSetSnapping` | Enable/disable snapping |
| 6 | `pro.deleteBookmark` | `HandleDeleteBookmark` | Delete bookmark by name |
| 7 | `pro.flashSelection` | `HandleFlashSelection` | Flash selected features |
| 8 | `pro.selectAll` | `HandleSelectAll` | Select all features in layer |
| 9 | `pro.setStatusBarMessage` | `HandleSetStatusBarMessage` | Set status bar text |
| 10 | `pro.listStandaloneTables` | `HandleListStandaloneTables` | List non-spatial tables |

## C# Implementation

### Dictionary entries (after line 162)

```csharp
["pro.setMapScale"] = HandleSetMapScale,
["pro.getMapScale"] = HandleGetMapScale,
["pro.zoomToSelected"] = HandleZoomToSelected,
["pro.getEditState"] = HandleGetEditState,
["pro.setSnapping"] = HandleSetSnapping,
["pro.deleteBookmark"] = HandleDeleteBookmark,
["pro.flashSelection"] = HandleFlashSelection,
["pro.selectAll"] = HandleSelectAll,
["pro.setStatusBarMessage"] = HandleSetStatusBarMessage,
["pro.listStandaloneTables"] = HandleListStandaloneTables,
```

### Handler methods (before class closing brace)

```csharp
// --- Phase 5: Map Navigation ---

private static async Task<IpcResponse> HandleSetMapScale(IpcRequest req, CancellationToken ct)
{
    if (req.Args == null ||
        !req.Args.TryGetValue("scale", out string scaleStr))
        return new IpcResponse(false, "arg 'scale' required", null);

    double scale = double.Parse(scaleStr);
    if (scale <= 0) return new IpcResponse(false, "scale must be positive", null);

    await QueuedTask.Run(async () =>
    {
        var cam = MapView.Active?.Camera;
        if (cam != null) cam.Scale = scale;
    });

    return new IpcResponse(true, null, new { done = true, scale });
}

private static async Task<IpcResponse> HandleGetMapScale(IpcRequest req, CancellationToken ct)
{
    var scale = await QueuedTask.Run(() =>
        MapView.Active?.Camera?.Scale
    );

    if (scale == null)
        return new IpcResponse(false, "No active map view", null);
    return new IpcResponse(true, null, new { scale });
}

private static async Task<IpcResponse> HandleZoomToSelected(IpcRequest req, CancellationToken ct)
{
    string layerName = null;
    req.Args?.TryGetValue("layer", out layerName);

    await QueuedTask.Run(async () =>
    {
        if (!string.IsNullOrWhiteSpace(layerName))
        {
            var fl = MapView.Active?.Map?.Layers
                .OfType<FeatureLayer>()
                .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
            if (fl != null && fl.GetSelectionCount() > 0)
                await MapView.Active.ZoomToSelectedAsync(fl);
        }
        else
        {
            await MapView.Active.ZoomToSelectedAsync();
        }
    });

    return new IpcResponse(true, null, new { done = true });
}

// --- Phase 5: Editing State ---

private static Task<IpcResponse> HandleGetEditState(IpcRequest req, CancellationToken ct)
{
    int undoCount = EditOperation.UndoCount;
    int redoCount = EditOperation.RedoCount;
    return Task.FromResult(new IpcResponse(true, null, new { undoCount, redoCount }));
}

private static async Task<IpcResponse> HandleSetSnapping(IpcRequest req, CancellationToken ct)
{
    if (req.Args == null ||
        !req.Args.TryGetValue("enabled", out string enabledStr))
        return new IpcResponse(false, "arg 'enabled' required", null);

    bool enabled = bool.Parse(enabledStr);

    await QueuedTask.Run(() =>
    {
        Snapping.IsEnabled = enabled;
    });

    return new IpcResponse(true, null, new { done = true, snappingEnabled = enabled });
}

// --- Phase 5: Bookmark Management ---

private static async Task<IpcResponse> HandleDeleteBookmark(IpcRequest req, CancellationToken ct)
{
    if (req.Args == null ||
        !req.Args.TryGetValue("name", out string bmName) ||
        string.IsNullOrWhiteSpace(bmName))
        return new IpcResponse(false, "arg 'name' required", null);

    bool found = false;

    await QueuedTask.Run(() =>
    {
        var bkmk = MapView.Active?.Map?.GetBookmarks()
            .FirstOrDefault(b => b.Name.Equals(bmName, StringComparison.OrdinalIgnoreCase));
        if (bkmk != null)
        {
            MapView.Active.Map.RemoveBookmark(bkmk);
            found = true;
        }
    });

    if (!found)
        return new IpcResponse(false, $"Bookmark '{bmName}' not found", null);
    return new IpcResponse(true, null, new { done = true, name = bmName });
}

// --- Phase 5: Selection UX ---

private static async Task<IpcResponse> HandleFlashSelection(IpcRequest req, CancellationToken ct)
{
    if (req.Args == null ||
        !req.Args.TryGetValue("layer", out string layerName) ||
        string.IsNullOrWhiteSpace(layerName))
        return new IpcResponse(false, "arg 'layer' required", null);

    int count = 0;

    await QueuedTask.Run(async () =>
    {
        var fl = MapView.Active?.Map?.Layers
            .OfType<FeatureLayer>()
            .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
        if (fl == null) return;

        using var fc = fl.GetFeatureClass();
        var def = fc.GetDefinition();
        var shapeField = def.GetShapeField();
        var selectionSet = fl.GetSelection();
        var oids = selectionSet.GetObjectIDs().ToList();
        if (oids.Count == 0) return;

        var qf = new QueryFilter { ObjectIDs = oids };
        using var cursor = fc.Search(qf);
        while (cursor.MoveNext())
        {
            using var row = cursor.Current;
            if (row[shapeField] is ArcGIS.Core.Geometry.Geometry geom)
            {
                await MapView.Active.FlashGeometry(geom);
                count++;
            }
        }
    });

    return new IpcResponse(true, null, new { done = true, flashedCount = count });
}

private static async Task<IpcResponse> HandleSelectAll(IpcRequest req, CancellationToken ct)
{
    if (req.Args == null ||
        !req.Args.TryGetValue("layer", out string layerName) ||
        string.IsNullOrWhiteSpace(layerName))
        return new IpcResponse(false, "arg 'layer' required", null);

    int count = 0;

    await QueuedTask.Run(() =>
    {
        var fl = MapView.Active?.Map?.Layers
            .OfType<FeatureLayer>()
            .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
        if (fl != null)
        {
            fl.Select(new QueryFilter());
            count = fl.GetSelectionCount();
        }
    });

    return new IpcResponse(true, null, new { done = true, count });
}

// --- Phase 5: UI Feedback ---

private static Task<IpcResponse> HandleSetStatusBarMessage(IpcRequest req, CancellationToken ct)
{
    if (req.Args == null ||
        !req.Args.TryGetValue("message", out string message) ||
        string.IsNullOrWhiteSpace(message))
        return Task.FromResult(new IpcResponse(false, "arg 'message' required", null));

    Application.StatusBar?.SetMessage(message, 0);
    return Task.FromResult(new IpcResponse(true, null, new { done = true, message }));
}

// --- Phase 5: Data Discovery ---

private static async Task<IpcResponse> HandleListStandaloneTables(IpcRequest req, CancellationToken ct)
{
    var tables = await QueuedTask.Run(() =>
        Project.Current.GetItems<TableProjectItem>()
            .Select(t => new { t.Name, t.Path })
            .ToList() ?? new List<object>()
    );
    return new IpcResponse(true, null, tables);
}
```

## Python Implementation

Add before `def main()` in `arcgis_mcp_server.py`:

```python
@mcp.tool()
def pro_set_map_scale(scale: float) -> dict[str, Any]:
    """Set the active map view to a specific scale."""
    return _call_addin("pro.setMapScale", {"scale": str(scale)})


@mcp.tool()
def pro_get_map_scale() -> dict[str, Any]:
    """Get the current scale of the active map view."""
    return _call_addin("pro.getMapScale")


@mcp.tool()
def pro_zoom_to_selected(layer: str | None = None) -> dict[str, Any]:
    """Zoom to selected features. Optionally scope to a specific layer."""
    args = {}
    if layer:
        args["layer"] = layer
    return _call_addin("pro.zoomToSelected", args)


@mcp.tool()
def pro_get_edit_state() -> dict[str, Any]:
    """Get undo and redo operation counts."""
    return _call_addin("pro.getEditState")


@mcp.tool()
def pro_set_snapping(enabled: bool) -> dict[str, Any]:
    """Enable or disable map snapping."""
    return _call_addin("pro.setSnapping", {"enabled": str(enabled).lower()})


@mcp.tool()
def pro_delete_bookmark(name: str) -> dict[str, Any]:
    """Delete a bookmark by name from the active map."""
    return _call_addin("pro.deleteBookmark", {"name": name})


@mcp.tool()
def pro_flash_selection(layer: str) -> dict[str, Any]:
    """Visually flash selected features in a layer on the map."""
    return _call_addin("pro.flashSelection", {"layer": layer})


@mcp.tool()
def pro_select_all(layer: str) -> dict[str, Any]:
    """Select all features in a layer."""
    return _call_addin("pro.selectAll", {"layer": layer})


@mcp.tool()
def pro_set_status_bar_message(message: str) -> dict[str, Any]:
    """Set the ArcGIS Pro status bar message."""
    return _call_addin("pro.setStatusBarMessage", {"message": message})


@mcp.tool()
def pro_list_standalone_tables() -> dict[str, Any]:
    """List non-spatial standalone tables in the current project."""
    return _call_addin("pro.listStandaloneTables")
```

## Test Implementation

Add to `ProToolsTests` class in `tests/test_arcgis_mcp_server.py` before the
`if __name__` block. Follow the existing pattern (4 tests per tool: unavailable,
ok, error, forwards):

- `pro_set_map_scale` — 4 tests
- `pro_get_map_scale` — 4 tests
- `pro_zoom_to_selected` — 5 tests (with/without layer)
- `pro_get_edit_state` — 4 tests
- `pro_set_snapping` — 4 tests
- `pro_delete_bookmark` — 5 tests (includes not-found case)
- `pro_flash_selection` — 4 tests
- `pro_select_all` — 4 tests
- `pro_set_status_bar_message` — 4 tests
- `pro_list_standalone_tables` — 4 tests

**Total: ~42 new tests, bringing the suite to ~272 tests.**

## Files Modified

| File | Changes |
|------|---------|
| `addin/APBridgeAddIn/ProBridgeService.cs` | +10 dictionary entries, +10 handler methods |
| `arcgis_mcp_server.py` | +10 `@mcp.tool()` functions |
| `tests/test_arcgis_mcp_server.py` | +42 test methods |
