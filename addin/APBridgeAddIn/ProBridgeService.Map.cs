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
        private static Task<IpcResponse> HandlePing(IpcRequest req, CancellationToken ct)
            => Task.FromResult(new IpcResponse(true, null, new { pong = "addin", timestamp = DateTime.UtcNow.ToString("O") }));

        private static Task<IpcResponse> HandleGetActiveMapName(IpcRequest req, CancellationToken ct)
        {
            var name = MapView.Active?.Map?.Name ?? "<none>";
            return Task.FromResult(new IpcResponse(true, null, new { name }));
        }

        private static async Task<IpcResponse> HandleListLayers(IpcRequest req, CancellationToken ct)
        {
            var layers = await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return null;
                return map.Layers
                    .Select(l => new { l.Name, l.IsVisible, Type = l.GetType().Name })
                    .ToList();
            });
            return new IpcResponse(true, null, layers);
        }

        private static async Task<IpcResponse> HandleCountFeatures(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName))
                return new IpcResponse(false, "arg 'layer' required", null);

            int count = await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return 0;
                var fl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) return 0;
                using var fc = fl.GetFeatureClass();
                return (int)fc.GetCount();
            });

            return new IpcResponse(true, null, new { count });
        }

        private static async Task<IpcResponse> HandleGetLayerSchema(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName))
                return new IpcResponse(false, "arg 'layer' required", null);

            var schema = await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return null;
                var fl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) return null;

                using var fc = fl.GetFeatureClass();
                var def = fc.GetDefinition();
                var fields = def.GetFields();
                var fieldInfos = new List<object>();
                foreach (var f in fields)
                {
                    fieldInfos.Add(new
                    {
                        f.Name,
                        f.AliasName,
                        f.FieldType,
                        Length = f.Length,
                        Precision = f.Precision,
                        Scale = f.Scale,
                        IsNullable = f.IsNullable,
                        IsEditable = f.IsEditable
                    });
                }
                return new
                {
                    layerName,
                    shapeType = fl.ShapeType.ToString(),
                    fieldCount = fieldInfos.Count,
                    fields = fieldInfos
                };
            });

            return new IpcResponse(true, null, schema);
        }

        private static async Task<IpcResponse> HandleZoomToLayer(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName))
                return new IpcResponse(false, "arg 'layer' required", null);

            await QueuedTask.Run(async () =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return;
                var fl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl != null)
                {
                    var ext = fl.QueryExtent();
                    if (ext != null)
                        await MapView.Active.ZoomToAsync(ext);
                }
            });

            return new IpcResponse(true, null, new { done = true });
        }

        private static async Task<IpcResponse> HandleGetCurrentExtent(IpcRequest req, CancellationToken ct)
        {
            var extent = await QueuedTask.Run(() =>
            {
                var mapView = MapView.Active;
                if (mapView == null) return null;
                var env = mapView.Extent;
                return new
                {
                    xmin = env.XMin,
                    ymin = env.YMin,
                    xmax = env.XMax,
                    ymax = env.YMax,
                    spatialReference = env.SpatialReference?.Name ?? "Unknown",
                    wkid = env.SpatialReference?.Wkid ?? 0
                };
            });

            return new IpcResponse(true, null, extent);
        }

        private static async Task<IpcResponse> HandlePanToExtent(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("xmin", out string xminStr) ||
                !req.Args.TryGetValue("ymin", out string yminStr) ||
                !req.Args.TryGetValue("xmax", out string xmaxStr) ||
                !req.Args.TryGetValue("ymax", out string ymaxStr))
                return new IpcResponse(false, "args 'xmin', 'ymin', 'xmax', 'ymax' required", null);

            double xmin = double.Parse(xminStr);
            double ymin = double.Parse(yminStr);
            double xmax = double.Parse(xmaxStr);
            double ymax = double.Parse(ymaxStr);

            var view = MapView.Active;
            if (view == null)
                return new IpcResponse(false, "No active map view", null);

            await QueuedTask.Run(async () =>
            {
                var sr = view.Camera?.SpatialReference ?? view.Map?.SpatialReference;
                var envelope = EnvelopeBuilderEx.CreateEnvelope(xmin, ymin, xmax, ymax, sr);
                await view.ZoomToAsync(envelope);
            });

            return new IpcResponse(true, null, new { done = true });
        }

        private static async Task<IpcResponse> HandleGetCamera(IpcRequest req, CancellationToken ct)
        {
            var cam = await QueuedTask.Run(() => MapView.Active?.Camera);
            if (cam == null)
                return new IpcResponse(false, "No active map view", null);
            return new IpcResponse(true, null, new
            {
                x = cam.X,
                y = cam.Y,
                z = cam.Z,
                heading = cam.Heading,
                pitch = cam.Pitch,
                roll = cam.Roll,
                spatialReference = cam.SpatialReference?.Name ?? "Unknown",
                wkid = cam.SpatialReference?.Wkid ?? 0
            });
        }

        private static async Task<IpcResponse> HandleGetLayerExtent(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName))
                return new IpcResponse(false, "arg 'layer' required", null);

            var extent = await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return null;
                var fl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) return null;
                using var fc = fl.GetFeatureClass();
                var env = fc.GetExtent();
                return new
                {
                    xmin = env.XMin,
                    ymin = env.YMin,
                    xmax = env.XMax,
                    ymax = env.YMax,
                    spatialReference = env.SpatialReference?.Name ?? "Unknown",
                    wkid = env.SpatialReference?.Wkid ?? 0
                };
            });
            if (extent == null)
                return new IpcResponse(false, $"Layer '{layerName}' not found", null);
            return new IpcResponse(true, null, extent);
        }

        private static async Task<IpcResponse> HandleGetProjectProperties(IpcRequest req, CancellationToken ct)
        {
            if (Project.Current == null)
                return new IpcResponse(false, "No project open", null);
            var props = await QueuedTask.Run(() =>
            {
                var p = Project.Current;
                return new
                {
                    name = p.Name,
                    path = p.Path,
                    defaultGdb = p.DefaultGeodatabasePath,
                    defaultToolbox = p.DefaultToolboxPath,
                    summary = p.Summary,
                    tags = p.Tags,
                };
            });
            return new IpcResponse(true, null, props);
        }

        private static async Task<IpcResponse> HandleGetAllMapNames(IpcRequest req, CancellationToken ct)
        {
            if (Project.Current == null)
                return new IpcResponse(false, "No project open", null);
            var maps = await QueuedTask.Run(() =>
                Project.Current.GetItems<MapProjectItem>()
                    .Select(m => new { m.Name })
                    .ToList());
            return new IpcResponse(true, null, maps);
        }

        private static async Task<IpcResponse> HandleGetMapFrame(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layoutName", out string layoutName) ||
                string.IsNullOrWhiteSpace(layoutName))
                return new IpcResponse(false, "arg 'layoutName' required", null);
            if (Project.Current == null)
                return new IpcResponse(false, "No project open", null);

            req.Args.TryGetValue("mapFrameName", out string mapFrameName);

            var result = await QueuedTask.Run(() =>
            {
                var layoutItem = Project.Current.GetItems<LayoutProjectItem>()
                    .FirstOrDefault(l => l.Name.Equals(layoutName, StringComparison.OrdinalIgnoreCase));
                if (layoutItem == null) return null;

                using var layout = layoutItem.GetLayout();
                var mfs = layout.FindElements(Enumerable.Empty<string>()).OfType<MapFrame>();
                if (!string.IsNullOrWhiteSpace(mapFrameName))
                    mfs = mfs.Where(mf => mf.Name.Equals(mapFrameName, StringComparison.OrdinalIgnoreCase));

                return mfs.Select(mf => new
                {
                    name = mf.Name,
                    mapName = mf.Map?.Name,
                    width = 0,
                    height = 0,
                    cameraX = mf.Camera?.X,
                    cameraY = mf.Camera?.Y,
                    cameraScale = mf.Camera?.Scale,
                }).ToList();
            });

            if (result == null)
                return new IpcResponse(false, $"Layout '{layoutName}' not found", null);
            return new IpcResponse(true, null, result);
        }

        private static async Task<IpcResponse> HandleListBookmarks(IpcRequest req, CancellationToken ct)
        {
            var bookmarks = await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return null;
                return map.GetBookmarks()
                    .Select(b => new {
                        name = b.Name,
                    })
                    .ToList();
            });
            return new IpcResponse(true, null, bookmarks);
        }

        private static async Task<IpcResponse> HandleZoomToBookmark(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("name", out string bmName) ||
                string.IsNullOrWhiteSpace(bmName))
                return new IpcResponse(false, "arg 'name' required", null);

            await QueuedTask.Run(async () =>
            {
                var map = MapView.Active?.Map;
                var bkmk = map?.GetBookmarks()
                    .FirstOrDefault(b => b.Name.Equals(bmName, StringComparison.OrdinalIgnoreCase));
                if (bkmk != null)
                    await MapView.Active.ZoomToAsync(bkmk);
            });

            return new IpcResponse(true, null, new { done = true });
        }

        private static async Task<IpcResponse> HandleCreateBookmark(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("name", out string bmName) ||
                string.IsNullOrWhiteSpace(bmName))
                return new IpcResponse(false, "arg 'name' required", null);

            string resultName = null;
            await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return;
                var bm = new CIMBookmark { Name = bmName };
                map.AddBookmark(bm);
                resultName = bm.Name;
            });

            if (resultName == null)
                return new IpcResponse(false, "No active map", null);
            return new IpcResponse(true, null, new { done = true, name = resultName });
        }

        private static async Task<IpcResponse> HandleReorderLayer(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("index", out string indexStr))
                return new IpcResponse(false, "args 'layer' & 'index' required", null);

            int index = int.Parse(indexStr);

            await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return;
                var layer = map.Layers
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (layer != null)
                    MapView.Active.Map.MoveLayer(layer, index);
            });

            return new IpcResponse(true, null, new { done = true });
        }

        private static async Task<IpcResponse> HandleSetMapScale(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("scale", out string scaleStr))
                return new IpcResponse(false, "arg 'scale' required", null);

            double scale = double.Parse(scaleStr);
            if (scale <= 0) return new IpcResponse(false, "scale must be positive", null);

            await QueuedTask.Run(() =>
            {
                var cam = MapView.Active?.Camera;
                if (cam != null) cam.Scale = scale;
            });

            return new IpcResponse(true, null, new { done = true, scale });
        }

        private static async Task<IpcResponse> HandleGetMapScale(IpcRequest req, CancellationToken ct)
        {
            var scale = await QueuedTask.Run(() =>
            {
                var cam = MapView.Active?.Camera;
                return cam?.Scale;
            });

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
                    var map = MapView.Active?.Map;
                    if (map == null) return;
                    var fl = map.Layers
                        .OfType<FeatureLayer>()
                        .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                    if (fl != null && fl.SelectionCount > 0) await MapView.Active.ZoomToAsync(fl);
                }
                else
                {
                    await MapView.Active.ZoomToSelectedAsync();
                }
            });

            return new IpcResponse(true, null, new { done = true });
        }

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

        private static async Task<IpcResponse> HandleRenameLayer(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) || string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("newName", out string newName) || string.IsNullOrWhiteSpace(newName))
                return new IpcResponse(false, "args 'layer' & 'newName' required", null);

            await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return;
                var layer = map.Layers
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (layer != null)
                {
                    var def = layer.GetDefinition();
                    def.Name = newName;
                    layer.SetDefinition(def);
                }
            });

            return new IpcResponse(true, null, new { done = true, oldName = layerName, newName });
        }

        private static async Task<IpcResponse> HandleGetLayerStatistics(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) || string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("field", out string field) || string.IsNullOrWhiteSpace(field))
                return new IpcResponse(false, "args 'layer' & 'field' required", null);

            var result = await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return null;
                var fl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) return null;

                using var fc = fl.GetFeatureClass();
                var qf = new QueryFilter { SubFields = field };
                long count = 0, nullCount = 0;
                double sum = 0, sumSq = 0;
                double minVal = double.MaxValue, maxVal = double.MinValue;

                using (var cursor = fc.Search(qf, true))
                {
                    while (cursor.MoveNext())
                    {
                        using var row = cursor.Current;
                        var val = row[field];
                        count++;
                        if (val == null || val == DBNull.Value) { nullCount++; continue; }
                        double d = Convert.ToDouble(val);
                        sum += d;
                        sumSq += d * d;
                        if (d < minVal) minVal = d;
                        if (d > maxVal) maxVal = d;
                    }
                }

                long validCount = count - nullCount;
                double mean = validCount > 0 ? sum / validCount : 0;
                double variance = validCount > 0 ? (sumSq / validCount) - (mean * mean) : 0;
                double stddev = Math.Sqrt(Math.Max(0, variance));

                return new
                {
                    field,
                    count,
                    nullCount,
                    validCount,
                    min = validCount > 0 ? minVal : (double?)null,
                    max = validCount > 0 ? maxVal : (double?)null,
                    mean = validCount > 0 ? Math.Round(mean, 4) : (double?)null,
                    stddev = validCount > 0 ? Math.Round(stddev, 4) : (double?)null,
                };
            });

            if (result == null) return new IpcResponse(false, $"Layer '{layerName}' not found", null);
            return new IpcResponse(true, null, result);
        }

        private static async Task<IpcResponse> HandleCreateMap(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("mapName", out string mapName) || string.IsNullOrWhiteSpace(mapName) ||
                !req.Args.TryGetValue("mapType", out string mapTypeStr) || string.IsNullOrWhiteSpace(mapTypeStr))
                return new IpcResponse(false, "args 'mapName' and 'mapType' required", null);

            MapViewingMode viewingMode = mapTypeStr.ToLowerInvariant() switch
            {
                "globalscene" => MapViewingMode.SceneGlobal,
                "localscene" => MapViewingMode.SceneLocal,
                _ => MapViewingMode.Map,
            };

            await QueuedTask.Run(() =>
            {
                bool isScene = viewingMode != MapViewingMode.Map;
                MapFactory.Instance.CreateMap(mapName, isScene ? MapType.Scene : MapType.Map, viewingMode);
            });

            return new IpcResponse(true, null, new { mapName });
        }

        private static async Task<IpcResponse> HandleAddBasemap(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("basemapName", out string basemapName) || string.IsNullOrWhiteSpace(basemapName))
                return new IpcResponse(false, "arg 'basemapName' required", null);

            await QueuedTask.Run(async () =>
            {
                try
                {
                    var map = MapView.Active?.Map;
                    if (map == null || Project.Current == null) return;
                    var mapName = map.Name;
                    var projPath = Project.Current.Path;
                    var pyCode = "import arcpy\n"
                        + $"proj_path = {System.Text.Json.JsonSerializer.Serialize(projPath)}\n"
                        + $"map_name = {System.Text.Json.JsonSerializer.Serialize(mapName)}\n"
                        + $"basemap = {System.Text.Json.JsonSerializer.Serialize(basemapName)}\n"
                        + "try:\n"
                        + "    aprx = arcpy.mp.ArcGISProject(proj_path)\n"
                        + "    m = aprx.listMaps(map_name)[0]\n"
                        + "    m.addBasemap(basemap)\n"
                        + "    aprx.save()\n"
                        + "    print('ok')\n"
                        + "except Exception as ex:\n"
                        + "    print(f'error: {ex}')\n";
                    await RunProPythonAsync(pyCode, 30, ct);
                }
                catch { }
            });

            return new IpcResponse(true, null, new { done = true, basemapName });
        }

        private static async Task<IpcResponse> HandleSaveProject(IpcRequest req, CancellationToken ct)
        {
            if (Project.Current == null)
                return new IpcResponse(false, "No project open", null);

            string warning = null;
            string savedPath = null;

            try
            {
                await Project.Current.SaveAsync();
                savedPath = Project.Current.Path;
            }
            catch (Exception ex) { warning = $"Save failed: {SanitizeException(ex)}"; }

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, path = savedPath });
        }

        private static async Task<IpcResponse> HandleListStandaloneTables(IpcRequest req, CancellationToken ct)
        {
            var result = await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return null;
                var tables = map.StandaloneTables.Select(t => new
                {
                    name = t.Name,
                    type = t.GetType().Name,
                }).ToList();
                return tables;
            });

            if (result == null)
                return new IpcResponse(false, "No active map", null);
            return new IpcResponse(true, null, result);
        }
    }
}
