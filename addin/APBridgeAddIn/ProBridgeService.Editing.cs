using ArcGIS.Core.CIM;
using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Core.Geoprocessing;
using ArcGIS.Desktop.Editing;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Python.Runtime;

namespace APBridgeAddIn
{
    internal partial class ProBridgeService : IDisposable
    {
        private static async Task<IpcResponse> HandleGetFeatureByOid(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("oid", out string oidStr))
                return new IpcResponse(false, "args 'layer' & 'oid' required", null);

            long oid = long.Parse(oidStr);
            var result = await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return null;
                var fl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) return null;
                using var fc = fl.GetFeatureClass();
                var qf = new QueryFilter { ObjectIDs = new List<long> { oid } };
                using var cursor = fc.Search(qf);
                if (cursor.MoveNext())
                {
                    using var row = cursor.Current;
                    var attrs = new Dictionary<string, object>();
                    var tableDef = fc.GetDefinition() as TableDefinition;
                    var fields = tableDef?.GetFields() ?? Enumerable.Empty<Field>();
                    foreach (var field in fields)
                    {
                        attrs[field.Name] = row[field.Name] ?? "<null>";
                    }
                    return new { attributes = attrs };
                }
                return null;
            });
            if (result == null)
                return new IpcResponse(false, $"Feature OID {oid} not found in layer '{layerName}'", null);
            return new IpcResponse(true, null, result);
        }

        private static async Task<IpcResponse> HandleUndoEdit(IpcRequest req, CancellationToken ct)
        {
            bool performed = await QueuedTask.Run(async () =>
            {
                var op = new EditOperation();
                return await op.UndoAsync();
            });
            return new IpcResponse(true, null, new { undoPerformed = performed });
        }

        private static async Task<IpcResponse> HandleRedoEdit(IpcRequest req, CancellationToken ct)
        {
            bool performed = await QueuedTask.Run(async () =>
            {
                var op = new EditOperation();
                return await op.RedoAsync();
            });
            return new IpcResponse(true, null, new { redoPerformed = performed });
        }

        private static async Task<IpcResponse> HandleSetActiveTool(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("tool", out string toolDamlId) ||
                string.IsNullOrWhiteSpace(toolDamlId))
                return new IpcResponse(false, "arg 'tool' required", null);

            await QueuedTask.Run(async () =>
            {
                await FrameworkApplication.SetCurrentToolAsync(toolDamlId);
            });
            return new IpcResponse(true, null, new { done = true });
        }

        private static Task<IpcResponse> HandleGetActiveTool(IpcRequest req, CancellationToken ct)
        {
            var toolId = FrameworkApplication.CurrentTool;
            return Task.FromResult(new IpcResponse(true, null, new { activeTool = toolId ?? "<none>" }));
        }

        private static async Task<IpcResponse> HandleDeleteFeaturesByOid(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("oids", out string oidsStr) ||
                string.IsNullOrWhiteSpace(oidsStr))
                return new IpcResponse(false, "args 'layer' & 'oids' required", null);

            var oidList = oidsStr.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => long.Parse(s.Trim())).ToList();

            bool executed = await QueuedTask.Run(async () =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return false;
                var fl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) return false;

                using var fc = fl.GetFeatureClass();
                var qf = new QueryFilter { ObjectIDs = oidList };
                using var cursor = fc.Search(qf, false);

                var op = new EditOperation();
                op.Name = "Delete features by OID";
                while (cursor.MoveNext())
                {
                    using var row = cursor.Current;
                    op.Delete(row);
                }
                return await op.ExecuteAsync();
            });

            return new IpcResponse(true, null, new { done = executed, count = oidList.Count });
        }

        private static async Task<IpcResponse> HandleUpdateFeatureAttributes(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("oid", out string oidStr) ||
                !req.Args.TryGetValue("attributes", out string attrsJson) ||
                string.IsNullOrWhiteSpace(attrsJson))
                return new IpcResponse(false, "args 'layer', 'oid', & 'attributes' required", null);

            long oid = long.Parse(oidStr);
            var attrs = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(attrsJson);

            bool executed = await QueuedTask.Run(async () =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return false;
                var fl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) return false;

                using var fc = fl.GetFeatureClass();
                var qf = new QueryFilter { ObjectIDs = new List<long> { oid } };
                using var cursor = fc.Search(qf, false);
                if (!cursor.MoveNext()) return false;

                using var row = cursor.Current;
                var op = new EditOperation();
                op.Name = "Update feature attributes";
                foreach (var kvp in attrs)
                    op.Modify(row, kvp.Key, JsonElementToObject(kvp.Value));

                return await op.ExecuteAsync();
            });

            return new IpcResponse(true, null, new { done = executed });
        }

        private static async Task<IpcResponse> HandleCreatePointFeature(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("x", out string xStr) ||
                !req.Args.TryGetValue("y", out string yStr))
                return new IpcResponse(false, "args 'layer', 'x', & 'y' required", null);

            double x = double.Parse(xStr);
            double y = double.Parse(yStr);

            req.Args.TryGetValue("wkid", out string wkidStr);
            req.Args.TryGetValue("attributes", out string attrsJson);

            var result = await QueuedTask.Run(async () =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return null;
                var fl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) return null;

                using var fc = fl.GetFeatureClass();

                SpatialReference sr = null;
                if (!string.IsNullOrWhiteSpace(wkidStr) && int.TryParse(wkidStr, out int wkid))
                    sr = SpatialReferenceBuilder.CreateSpatialReference(wkid);

                var pt = MapPointBuilderEx.CreateMapPoint(x, y, sr);

                var op = new EditOperation();
                op.Name = "Create point feature";
                var token = op.Create(fl, pt);

                if (!string.IsNullOrWhiteSpace(attrsJson))
                {
                    var jsonAttrs = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(attrsJson);
                    var attrDict = jsonAttrs.ToDictionary(kvp => kvp.Key, kvp => JsonElementToObject(kvp.Value));
                    op.Modify(fl, token.ObjectID.Value, attrDict);
                }

                bool ok = await op.ExecuteAsync();
                return new
                {
                    done = ok,
                    objectId = ok ? token.ObjectID : (long?)null,
                };
            });

            if (result == null)
                return new IpcResponse(false, $"Layer '{layerName}' not found", null);
            return new IpcResponse(true, null, result);
        }

        private static async Task<IpcResponse> HandleCreatePolygonFeature(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("coordinates", out string coords) ||
                string.IsNullOrWhiteSpace(coords))
                return new IpcResponse(false, "args 'layer' & 'coordinates' required", null);

            req.Args.TryGetValue("wkid", out string wkidStr);
            req.Args.TryGetValue("attributes", out string attrsJson);

            var coordPairs = coords.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var pts = new List<MapPoint>();
            foreach (var pair in coordPairs)
            {
                var xy = pair.Split(',');
                if (xy.Length == 2 &&
                    double.TryParse(xy[0], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double x) &&
                    double.TryParse(xy[1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double y))
                {
                    pts.Add(MapPointBuilderEx.CreateMapPoint(x, y));
                }
            }

            if (pts.Count < 3)
                return new IpcResponse(false, "Need at least 3 valid coordinate pairs", null);

            // Close the ring if not already closed
            if (pts[0].X != pts[pts.Count - 1].X || pts[0].Y != pts[pts.Count - 1].Y)
                pts.Add(MapPointBuilderEx.CreateMapPoint(pts[0].X, pts[0].Y));

            SpatialReference sr = null;
            if (!string.IsNullOrWhiteSpace(wkidStr) && int.TryParse(wkidStr, out int wkid))
                sr = SpatialReferenceBuilder.CreateSpatialReference(wkid);

            var poly = PolygonBuilderEx.CreatePolygon(pts, sr);

            var result = await QueuedTask.Run(async () =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return null;
                var fl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) return null;

                var op = new EditOperation();
                op.Name = "Create polygon feature";
                var token = op.Create(fl, poly);

                if (!string.IsNullOrWhiteSpace(attrsJson))
                {
                    var jsonAttrs = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(attrsJson);
                    var attrDict = jsonAttrs.ToDictionary(kvp => kvp.Key, kvp => JsonElementToObject(kvp.Value));
                    op.Modify(fl, token.ObjectID.Value, attrDict);
                }

                bool ok = await op.ExecuteAsync();
                return new
                {
                    done = ok,
                    objectId = ok ? token.ObjectID : (long?)null,
                    vertexCount = pts.Count,
                };
            });

            if (result == null)
                return new IpcResponse(false, $"Layer '{layerName}' not found", null);
            return new IpcResponse(true, null, result);
        }

        private static async Task<IpcResponse> HandleCreateLineFeature(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("coordinates", out string coords) ||
                string.IsNullOrWhiteSpace(coords))
                return new IpcResponse(false, "args 'layer' & 'coordinates' required", null);

            req.Args.TryGetValue("wkid", out string wkidStr);
            req.Args.TryGetValue("attributes", out string attrsJson);

            var coordPairs = coords.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var pts = new List<MapPoint>();
            foreach (var pair in coordPairs)
            {
                var xy = pair.Split(',');
                if (xy.Length == 2 &&
                    double.TryParse(xy[0], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double x) &&
                    double.TryParse(xy[1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double y))
                {
                    pts.Add(MapPointBuilderEx.CreateMapPoint(x, y));
                }
            }

            if (pts.Count < 2)
                return new IpcResponse(false, "Need at least 2 valid coordinate pairs", null);

            SpatialReference sr = null;
            if (!string.IsNullOrWhiteSpace(wkidStr) && int.TryParse(wkidStr, out int wkid))
                sr = SpatialReferenceBuilder.CreateSpatialReference(wkid);

            var polyline = PolylineBuilderEx.CreatePolyline(pts, sr);

            var result = await QueuedTask.Run(async () =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return null;
                var fl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) return null;

                var op = new EditOperation();
                op.Name = "Create line feature";
                var token = op.Create(fl, polyline);

                if (!string.IsNullOrWhiteSpace(attrsJson))
                {
                    var jsonAttrs = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(attrsJson);
                    var attrDict = jsonAttrs.ToDictionary(kvp => kvp.Key, kvp => JsonElementToObject(kvp.Value));
                    op.Modify(fl, token.ObjectID.Value, attrDict);
                }

                bool ok = await op.ExecuteAsync();
                return new
                {
                    done = ok,
                    objectId = ok ? token.ObjectID : (long?)null,
                    vertexCount = pts.Count,
                };
            });

            if (result == null)
                return new IpcResponse(false, $"Layer '{layerName}' not found", null);
            return new IpcResponse(true, null, result);
        }

        private static Task<IpcResponse> HandleGetEditState(IpcRequest req, CancellationToken ct)
        {
            return Task.FromResult(new IpcResponse(true, null, new { undoCount = 0, redoCount = 0 }));
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

        private static async Task<IpcResponse> HandleSplitFeatures(IpcRequest req, CancellationToken ct)
        {
            return new IpcResponse(false, "Split features not accessible from AddIn SDK", null);
        }

        private static async Task<IpcResponse> HandleMergeFeatures(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) || string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("objectIds", out string objectIdsJson) || string.IsNullOrWhiteSpace(objectIdsJson) ||
                !req.Args.TryGetValue("targetOid", out string targetOidStr) || string.IsNullOrWhiteSpace(targetOidStr))
                return new IpcResponse(false, "args 'layer', 'objectIds', & 'targetOid' required", null);

            string warning = null;

            await QueuedTask.Run(async () =>
            {
                try
                {
                    var map = MapView.Active?.Map;
                    if (map == null) { warning = "No active map"; return; }
                    var fl = map.Layers.OfType<FeatureLayer>()
                        .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                    if (fl == null) { warning = $"Layer '{layerName}' not found"; return; }

                    using var fc = fl.GetFeatureClass();
                    var workspaceUri = fc.GetDatastore().GetPath();
                    var fcPath = System.IO.Path.Combine(workspaceUri.LocalPath, fc.GetName());

                    if (!int.TryParse(targetOidStr, out int targetOid))
                    { warning = "targetOid must be a valid integer"; return; }

                    var safeTargetOid = JsonSerializer.Serialize(targetOid);
                    var safeOidsJson = JsonSerializer.Serialize(objectIdsJson);
                    var safeFcPath = JsonSerializer.Serialize(fcPath);
                    var pyCode = $"import arcpy, json\n"
                        + $"fc_path = {safeFcPath}\n"
                        + $"oids_json = {safeOidsJson}\n"
                        + $"target_oid = {safeTargetOid}\n"
                        + "try:\n"
                        + "    oid_list = json.loads(oids_json)\n"
                        + "    if not isinstance(oid_list, list):\n"
                        + "        raise ValueError('objectIds must be a JSON array')\n"
                        + "    oid_list = [int(o) for o in oid_list]\n"
                        + "    desc = arcpy.Describe(fc_path)\n"
                        + "    oid_fld = desc.OIDFieldName\n"
                        + "    geoms = []\n"
                        + "    oid_str = ','.join(str(o) for o in oid_list)\n"
                        + "    with arcpy.da.SearchCursor(fc_path, ['SHAPE@'], f'{oid_fld} IN ({oid_str})') as cur:\n"
                        + "        for row in cur:\n"
                        + "            geoms.append(row[0])\n"
                        + "    if geoms:\n"
                        + "        merged = geoms[0]\n"
                        + "        for g in geoms[1:]:\n"
                        + "            merged = merged.union(g)\n"
                        + "        with arcpy.da.UpdateCursor(fc_path, ['SHAPE@'], f'{oid_fld} = {target_oid}') as cur:\n"
                        + "            for row in cur:\n"
                        + "                row[0] = merged\n"
                        + "                cur.updateRow(row)\n"
                        + "        for oid in oid_list:\n"
                        + "            if oid != target_oid:\n"
                        + "                with arcpy.da.UpdateCursor(fc_path, ['SHAPE@'], f'{oid_fld} = {oid}') as cur:\n"
                        + "                    for row in cur:\n"
                        + "                        cur.deleteRow()\n"
                        + "    print('ok')\n"
                        + "except Exception as ex:\n"
                        + "    print(f'error: {ex}')\n";

                    await RunProPythonAsync(pyCode, 30, ct);
                }
                catch (Exception ex)
                {
                    warning = ex.Message;
                }
            });

            if (warning != null)
                return new IpcResponse(false, warning, null);

            return new IpcResponse(true, null, new { done = true });
        }
    }
}
