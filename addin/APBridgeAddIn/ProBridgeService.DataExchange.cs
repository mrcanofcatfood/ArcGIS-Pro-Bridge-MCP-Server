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
        private static async Task<IpcResponse> HandleSearchAddress(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("address", out string address) || string.IsNullOrWhiteSpace(address))
                return new IpcResponse(false, "arg 'address' required", null);

            req.Args.TryGetValue("maxResults", out string maxStr);
            int.TryParse(maxStr, out int maxResults);
            if (maxResults <= 0) maxResults = 10;

            // Use arcpy subprocess for geocoding (works with locators set up in Pro)
            var pythonCode = "import arcpy, json\n"
                + $"address = {System.Text.Json.JsonSerializer.Serialize(address)}\n"
                + $"max_results = {maxResults}\n"
                + "results = []\n"
                + "try:\n"
                + "    locators = arcpy.geocoding.ListLocators()\n"
                + "    if locators:\n"
                + "        for loc in locators[:1]:\n"
                + "            geocode_result = arcpy.geocoding.GeocodeAddresses(\n"
                + "                [[address]], loc, 'SingleLine SingleLine')\n"
                + "            with arcpy.da.SearchCursor(geocode_result[0], ['Shape@', 'Status', 'Score', 'Match_addr']) as cur:\n"
                + "                for i, row in enumerate(cur):\n"
                + "                    if i >= max_results: break\n"
                + "                    pt = row[0]\n"
                + "                    results.append({'address': row[3] or address, 'score': row[2], 'status': row[1],\n"
                + "                        'x': pt.centroid.X if pt else 0, 'y': pt.centroid.Y if pt else 0})\n"
                + "    print(json.dumps({'locatorCount': len(locators), 'results': results, 'address': address}))\n"
                + "except Exception as ex:\n"
                + "    print(json.dumps({'error': str(ex), 'results': []}))\n";
            var pyResult = await RunProPythonAsync(pythonCode, 30, ct);
            object data = new { address, locatorCount = 0, results = new List<object>() };
            if (pyResult.Ok && pyResult.Data is System.Text.Json.JsonElement pe)
            {
                try { data = System.Text.Json.JsonSerializer.Deserialize<object>(pe.GetRawText()); } catch { }
            }
            return new IpcResponse(true, null, data);
        }

        private static async Task<IpcResponse> HandleOpenAttributeTable(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName))
                return new IpcResponse(false, "arg 'layer' required", null);

            await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return;
                var fl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) return;
                var pane = FrameworkApplication.DockPaneManager.Find("esri_core_tableWindow");
                pane?.Activate();
            });

            return new IpcResponse(true, null, new { done = true });
        }

        private static async Task<IpcResponse> HandleExportToCsv(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) || string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("outputPath", out string outputPath) || string.IsNullOrWhiteSpace(outputPath))
                return new IpcResponse(false, "args 'layer' & 'outputPath' required", null);

            string warning = null;
            int rowCount = 0;

            await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) { warning = "Layer not found"; return; }
                var fl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) { warning = "Layer not found"; return; }

                try
                {
                    using var fc = fl.GetFeatureClass();
                    var tableDef = fc.GetDefinition() as TableDefinition;
                    var fields = tableDef.GetFields().Where(f => !f.Name.Equals("SHAPE", StringComparison.OrdinalIgnoreCase) && f.Name != tableDef.GetObjectIDField()).ToList();

                    using var writer = new StreamWriter(outputPath);
                    writer.WriteLine(string.Join(",", fields.Select(f => EscapeCsvValue(f.Name))));

                    using var cursor = fc.Search(null, true);
                    while (cursor.MoveNext())
                    {
                        using var row = cursor.Current;
                        var values = fields.Select(f =>
                        {
                            var val = row[f.Name];
                            return val == null || val == DBNull.Value ? "" : EscapeCsvValue(val.ToString());
                        });
                        writer.WriteLine(string.Join(",", values));
                        rowCount++;
                    }
                }
                catch (Exception ex) { warning = $"Export failed: {SanitizeException(ex)}"; }
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, rowCount, outputPath });
        }

        private static string EscapeCsvValue(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (value.Length > 0 && (value[0] == '=' || value[0] == '+' || value[0] == '-' || value[0] == '@' || value[0] == '\t'))
                value = "'" + value;
            if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
                return $"\"{value.Replace("\"", "\"\"")}\"";
            return value;
        }

        private static async Task<IpcResponse> HandleExportToGeoJSON(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) || string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("outputPath", out string outputPath) || string.IsNullOrWhiteSpace(outputPath))
                return new IpcResponse(false, "args 'layer' & 'outputPath' required", null);

            string warning = null;

            await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) { warning = "Layer not found"; return; }
                var fl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) { warning = "Layer not found"; return; }

                try
                {
                    using var fc = fl.GetFeatureClass();
                    var tableDef = fc.GetDefinition() as TableDefinition;
                    var fields = tableDef.GetFields().Where(f => !f.Name.Equals("SHAPE", StringComparison.OrdinalIgnoreCase)).ToList();
                    var shapeField = tableDef.GetFields().FirstOrDefault(f => f.Name.Equals("SHAPE", StringComparison.OrdinalIgnoreCase));

                    var features = new List<object>();
                    using var cursor = fc.Search(null, true);
                    while (cursor.MoveNext())
                    {
                        using var row = cursor.Current;
                        var props = new Dictionary<string, object>();
                        foreach (var f in fields)
                        {
                            var val = row[f.Name];
                            props[f.Name] = (val == null || val == DBNull.Value) ? null : val;
                        }

                        var geom = shapeField != null ? row[shapeField.Name] as Geometry : null;
                        features.Add(new { type = "Feature", geometry = geom != null ? GeoJsonFromGeometry(geom) : null, properties = props });
                    }

                    var fcGeoJson = new { type = "FeatureCollection", features };
                    var options = new JsonSerializerOptions { WriteIndented = true };
                    File.WriteAllText(outputPath, JsonSerializer.Serialize(fcGeoJson, options));
                }
                catch (Exception ex) { warning = $"Export failed: {SanitizeException(ex)}"; }
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, outputPath });
        }

        private static object GeoJsonFromGeometry(Geometry geom)
        {
            if (geom is MapPoint pt)
                return new { type = "Point", coordinates = new[] { pt.X, pt.Y } };

            if (geom is Multipoint mpt)
            {
                var coords = mpt.Points.Select(p => new[] { p.X, p.Y }).ToArray();
                return new { type = "MultiPoint", coordinates = coords };
            }

            if (geom is Polygon polygon)
            {
                var rings = new List<double[][]>();
                var ringCoords = polygon.Points.Select(p => new[] { p.X, p.Y }).ToArray();
                rings.Add(ringCoords);
                return new { type = "Polygon", coordinates = rings };
            }

            if (geom is Polyline polyline)
            {
                var coords = polyline.Points.Select(p => new[] { p.X, p.Y }).ToArray();
                return new { type = "LineString", coordinates = coords };
            }

            return null;
        }

        private static async Task<IpcResponse> HandleImportCsv(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("csvPath", out string csvPath) || string.IsNullOrWhiteSpace(csvPath) ||
                !req.Args.TryGetValue("gdbPath", out string gdbPath) || string.IsNullOrWhiteSpace(gdbPath) ||
                !req.Args.TryGetValue("fcName", out string fcName) || string.IsNullOrWhiteSpace(fcName) ||
                !req.Args.TryGetValue("xField", out string xField) || string.IsNullOrWhiteSpace(xField) ||
                !req.Args.TryGetValue("yField", out string yField) || string.IsNullOrWhiteSpace(yField))
                return new IpcResponse(false, "args 'csvPath', 'gdbPath', 'fcName', 'xField', & 'yField' required", null);

            req.Args.TryGetValue("wkid", out string wkidStr);
            int.TryParse(wkidStr, out int wkid);
            if (wkid == 0) wkid = 4326;

            string warning = null;

            await QueuedTask.Run(async () =>
            {
                try
                {
                    if (!System.IO.File.Exists(csvPath)) { warning = "CSV file not found"; return; }

                    var fcPath = $"{gdbPath}/{fcName}";

                    var createParams = new List<object?> { fcPath, "POINT" };
                    if (wkid > 0)
                        createParams.Add(SpatialReferenceBuilder.CreateSpatialReference(wkid));
                    var r1 = await Geoprocessing.ExecuteToolAsync("CreateFeatureclass",
                        Geoprocessing.MakeValueArray(createParams.ToArray()));
                    if (r1.IsFailed) { warning = "CreateFeatureclass failed"; return; }

                    var lines = System.IO.File.ReadAllLines(csvPath);
                    if (lines.Length < 2) { warning = "CSV has no data rows"; return; }

                    var headers = ParseCsvLine(lines[0]);
                    int xIdx = -1, yIdx = -1;
                    for (int i = 0; i < headers.Length; i++)
                    {
                        if (headers[i].Equals(xField, StringComparison.OrdinalIgnoreCase)) xIdx = i;
                        if (headers[i].Equals(yField, StringComparison.OrdinalIgnoreCase)) yIdx = i;
                    }
                    if (xIdx < 0 || yIdx < 0) { warning = $"X/Y fields not found in CSV header"; return; }

                    foreach (var h in headers)
                    {
                        if (h.Equals(xField, StringComparison.OrdinalIgnoreCase) || h.Equals(yField, StringComparison.OrdinalIgnoreCase)) continue;
                        await Geoprocessing.ExecuteToolAsync("AddField",
                            Geoprocessing.MakeValueArray(fcPath, h, "TEXT", "#", "#", "255"));
                    }

                    // Import via arcpy subprocess (more reliable for bulk)
                    var pyCode = "import arcpy, json\n"
                        + $"csv_path = {System.Text.Json.JsonSerializer.Serialize(csvPath)}\n"
                        + $"fc_path = {System.Text.Json.JsonSerializer.Serialize(fcPath)}\n"
                        + $"x_f = {System.Text.Json.JsonSerializer.Serialize(xField)}\n"
                        + $"y_f = {System.Text.Json.JsonSerializer.Serialize(yField)}\n"
                        + $"wkid = {wkid}\n"
                        + "try:\n"
                        + "    sr = arcpy.SpatialReference(wkid) if wkid else None\n"
                        + "    arcpy.management.XYTableToPoint(csv_path, fc_path, x_f, y_f, sr)\n"
                        + "    with arcpy.da.SearchCursor(fc_path, ['OID@']) as cur:\n"
                        + "        count = sum(1 for _ in cur)\n"
                        + "    print(json.dumps({'rowCount': count}))\n"
                        + "except Exception as ex:\n"
                        + "    print(json.dumps({'error': str(ex)}))\n";
                    var pyResult = await RunProPythonAsync(pyCode, 60, ct);
                    if (pyResult.Ok && pyResult.Data is System.Text.Json.JsonElement pe)
                    {
                        string stdout = "";
                        if (pe.TryGetProperty("stdout", out var so)) stdout = so.GetString() ?? "";
                        if (stdout.Contains("\"error\""))
                            warning = "arcpy XYTableToPoint failed";
                    }
                }
                catch (Exception ex) { warning = $"CSV import failed: {SanitizeException(ex)}"; }
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, fcName });
        }

        private static string[] ParseCsvLine(string line)
        {
            var result = new List<string>();
            bool inQuotes = false;
            var current = new System.Text.StringBuilder();
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                    else inQuotes = !inQuotes;
                }
                else if (c == ',' && !inQuotes) { result.Add(current.ToString()); current.Clear(); }
                else current.Append(c);
            }
            result.Add(current.ToString());
            return result.ToArray();
        }

        private static async Task<IpcResponse> HandleExportToShapefile(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) || string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("outputPath", out string outputPath) || string.IsNullOrWhiteSpace(outputPath))
                return new IpcResponse(false, "args 'layer' & 'outputPath' required", null);

            string warning = null;

            await QueuedTask.Run(async () =>
            {
                var map = MapView.Active?.Map;
                if (map == null) { warning = "Layer not found"; return; }
                var fl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) { warning = "Layer not found"; return; }

                try
                {
                    using var fc = fl.GetFeatureClass();
                    var workspaceUri = fc.GetDatastore().GetPath();
                    var fcName = fc.GetName();
                    var fcPath = System.IO.Path.Combine(workspaceUri.LocalPath, fcName);

                    var result = await Geoprocessing.ExecuteToolAsync("CopyFeatures",
                        Geoprocessing.MakeValueArray(fcPath, outputPath));
                    if (result != null && result.IsFailed)
                        warning = "CopyFeatures failed";
                }
                catch (Exception ex) { warning = $"Export failed: {SanitizeException(ex)}"; }
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, outputPath });
        }

        private static async Task<IpcResponse> HandleExportToKml(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) || string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("outputPath", out string outputPath) || string.IsNullOrWhiteSpace(outputPath))
                return new IpcResponse(false, "args 'layer' & 'outputPath' required", null);

            string warning = null;

            await QueuedTask.Run(async () =>
            {
                var map = MapView.Active?.Map;
                if (map == null) { warning = "Layer not found"; return; }
                var fl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) { warning = "Layer not found"; return; }

                try
                {
                    using var fc = fl.GetFeatureClass();
                    var workspaceUri = fc.GetDatastore().GetPath();
                    var fcName = fc.GetName();
                    var fcPath = System.IO.Path.Combine(workspaceUri.LocalPath, fcName);

                    var result = await Geoprocessing.ExecuteToolAsync("LayerToKML",
                        Geoprocessing.MakeValueArray(fcPath, outputPath));
                    if (result != null && result.IsFailed)
                        warning = "LayerToKML failed";
                }
                catch (Exception ex) { warning = $"Export failed: {SanitizeException(ex)}"; }
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, outputPath });
        }

        private static async Task<IpcResponse> HandleImportGeoJSON(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("geojsonPath", out string geoJsonPath) || string.IsNullOrWhiteSpace(geoJsonPath) ||
                !req.Args.TryGetValue("gdbPath", out string gdbPath) || string.IsNullOrWhiteSpace(gdbPath) ||
                !req.Args.TryGetValue("fcName", out string fcName) || string.IsNullOrWhiteSpace(fcName))
                return new IpcResponse(false, "args 'geojsonPath', 'gdbPath', & 'fcName' required", null);

            string warning = null;

            await QueuedTask.Run(async () =>
            {
                try
                {
                    if (!System.IO.File.Exists(geoJsonPath)) { warning = "GeoJSON file not found"; return; }

                    // Use arcpy to import GeoJSON
                    var fcPath = $"{gdbPath}/{fcName}";
                    var pyCode = "import arcpy, json\n"
                        + $"gj_path = {System.Text.Json.JsonSerializer.Serialize(geoJsonPath)}\n"
                        + $"fc_path = {System.Text.Json.JsonSerializer.Serialize(fcPath)}\n"
                        + "try:\n"
                        + "    result = arcpy.conversion.JSONToFeatures(gj_path, fc_path)\n"
                        + "    print(json.dumps({'done': True, 'fc': fc_path}))\n"
                        + "except Exception as ex:\n"
                        + "    print(json.dumps({'error': str(ex)}))\n";
                    var pyResult = await RunProPythonAsync(pyCode, 60, ct);
                    if (pyResult.Ok && pyResult.Data is System.Text.Json.JsonElement pe)
                    {
                        string stdout = "";
                        if (pe.TryGetProperty("stdout", out var so)) stdout = so.GetString() ?? "";
                        if (stdout.Contains("\"error\""))
                            warning = "arcpy JSONToFeatures failed";
                    }
                }
                catch (Exception ex) { warning = $"GeoJSON import failed: {SanitizeException(ex)}"; }
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, fcName });
        }

        private static Geometry GeoJsonToGeometry(JsonElement geomEl, SpatialReference sr)
        {
            if (geomEl.ValueKind != JsonValueKind.Object) return null;
            if (!geomEl.TryGetProperty("type", out var typeEl)) return null;

            string type = typeEl.GetString();
            if (!geomEl.TryGetProperty("coordinates", out var coordsEl)) return null;

            if (type == "Point")
            {
                var arr = coordsEl.EnumerateArray().Select(e => e.GetDouble()).ToArray();
                return arr.Length >= 2 ? MapPointBuilderEx.CreateMapPoint(arr[0], arr[1], sr) : null;
            }

            if (type == "MultiPoint")
            {
                var pts = coordsEl.EnumerateArray().Select(p => { var a = p.EnumerateArray().Select(e => e.GetDouble()).ToArray(); return MapPointBuilderEx.CreateMapPoint(a[0], a[1], sr); }).ToList();
                return MultipointBuilderEx.CreateMultipoint(pts);
            }

            if (type == "LineString")
            {
                var builder = new PolylineBuilderEx(sr);
                var pts = coordsEl.EnumerateArray().Select(p => { var a = p.EnumerateArray().Select(e => e.GetDouble()).ToArray(); return MapPointBuilderEx.CreateMapPoint(a[0], a[1], sr); }).ToList();
                builder.AddPart(pts);
                return builder.ToGeometry();
            }

            if (type == "MultiLineString")
            {
                var builder = new PolylineBuilderEx(sr);
                foreach (var line in coordsEl.EnumerateArray())
                {
                    var pts = line.EnumerateArray().Select(p => { var a = p.EnumerateArray().Select(e => e.GetDouble()).ToArray(); return MapPointBuilderEx.CreateMapPoint(a[0], a[1], sr); }).ToList();
                    builder.AddPart(pts);
                }
                return builder.ToGeometry();
            }

            if (type == "Polygon")
            {
                var builder = new PolygonBuilderEx(sr);
                foreach (var ring in coordsEl.EnumerateArray())
                {
                    var pts = ring.EnumerateArray().Select(p => { var a = p.EnumerateArray().Select(e => e.GetDouble()).ToArray(); return MapPointBuilderEx.CreateMapPoint(a[0], a[1], sr); }).ToList();
                    builder.AddPart(pts);
                }
                return builder.ToGeometry();
            }

            return null;
        }
    }
}
