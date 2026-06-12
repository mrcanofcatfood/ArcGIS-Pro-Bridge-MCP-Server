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
        private static async Task<IpcResponse> HandleGetSelectionCount(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName))
                return new IpcResponse(false, "arg 'layer' required", null);

            int selCount = await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return 0;
                var fl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                return fl?.SelectionCount ?? 0;
            });

            return new IpcResponse(true, null, new { count = selCount });
        }

        private static async Task<IpcResponse> HandleSelectByAttribute(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("where", out string where) ||
                string.IsNullOrWhiteSpace(where))
                return new IpcResponse(false, "args 'layer' & 'where' required", null);

            await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return;
                var fl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl != null)
                {
                    fl.Select(new QueryFilter { WhereClause = where });
                }
            });

            return new IpcResponse(true, null, new { done = true });
        }

        private static async Task<IpcResponse> HandleClearSelection(IpcRequest req, CancellationToken ct)
        {
            string layerName = null;
            req.Args?.TryGetValue("layer", out layerName);

            await QueuedTask.Run(() =>
            {
                if (!string.IsNullOrWhiteSpace(layerName))
                {
                    var map = MapView.Active?.Map;
                    if (map == null) return;
                    var fl = map.Layers
                        .OfType<FeatureLayer>()
                        .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                    fl?.ClearSelection();
                }
                else
                {
                    var map = MapView.Active?.Map;
                    if (map == null) return;
                    foreach (var fl in map.Layers.OfType<FeatureLayer>() ?? Enumerable.Empty<FeatureLayer>())
                        fl.ClearSelection();
                }
            });

            return new IpcResponse(true, null, new { done = true });
        }

        private static async Task<IpcResponse> HandleSelectByRectangle(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("xmin", out string xminStr) ||
                !req.Args.TryGetValue("ymin", out string yminStr) ||
                !req.Args.TryGetValue("xmax", out string xmaxStr) ||
                !req.Args.TryGetValue("ymax", out string ymaxStr))
                return new IpcResponse(false, "args 'layer', 'xmin', 'ymin', 'xmax', 'ymax' required", null);

            double xmin = double.Parse(xminStr);
            double ymin = double.Parse(yminStr);
            double xmax = double.Parse(xmaxStr);
            double ymax = double.Parse(ymaxStr);
            req.Args.TryGetValue("selectionType", out string selectionTypeStr);
            var selType = string.IsNullOrWhiteSpace(selectionTypeStr)
                ? SelectionCombinationMethod.New
                : Enum.Parse<SelectionCombinationMethod>(selectionTypeStr, ignoreCase: true);

            await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return;
                var fl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl != null)
                {
                    var sr = MapView.Active?.Camera?.SpatialReference;
                    var envelope = EnvelopeBuilderEx.CreateEnvelope(xmin, ymin, xmax, ymax, sr);
                    var sqf = new SpatialQueryFilter
                    {
                        SpatialRelationship = SpatialRelationship.Intersects,
                        FilterGeometry = envelope
                    };
                    fl.Select(sqf, selType);
                }
            });
            return new IpcResponse(true, null, new { done = true });
        }

        private static async Task<IpcResponse> HandleSwitchSelection(IpcRequest req, CancellationToken ct)
        {
            string layerName = null;
            req.Args?.TryGetValue("layer", out layerName);

            await QueuedTask.Run(() =>
            {
                if (!string.IsNullOrWhiteSpace(layerName))
                {
                    var map = MapView.Active?.Map;
                    if (map == null) return;
                    var fl = map.Layers
                        .OfType<FeatureLayer>()
                        .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                    if (fl != null)
                        fl.Select(new QueryFilter());
                }
                else
                {
                    var map = MapView.Active?.Map;
                    if (map == null) return;
                    foreach (var fl in map.Layers.OfType<FeatureLayer>() ?? Enumerable.Empty<FeatureLayer>())
                        fl.Select(new QueryFilter());
                }
            });
            return new IpcResponse(true, null, new { done = true });
        }

        private static async Task<IpcResponse> HandleSelectByPolygon(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("coordinates", out string coords) ||
                string.IsNullOrWhiteSpace(coords))
                return new IpcResponse(false, "args 'layer' & 'coordinates' required", null);

            req.Args.TryGetValue("selectionType", out string selTypeStr);
            var selType = string.IsNullOrWhiteSpace(selTypeStr)
                ? SelectionCombinationMethod.New
                : Enum.Parse<SelectionCombinationMethod>(selTypeStr, ignoreCase: true);

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

            if (pts[0].X != pts[pts.Count - 1].X || pts[0].Y != pts[pts.Count - 1].Y)
                pts.Add(MapPointBuilderEx.CreateMapPoint(pts[0].X, pts[0].Y));

            var poly = PolygonBuilderEx.CreatePolygon(pts);
            int count = 0;

            await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return;
                var fl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl != null)
                {
                    var sqf = new SpatialQueryFilter
                    {
                        SpatialRelationship = SpatialRelationship.Intersects,
                        FilterGeometry = poly
                    };
                    fl.Select(sqf, selType);
                    count = fl.SelectionCount;
                }
            });

            return new IpcResponse(true, null, new { done = true, selectionCount = count });
        }

        private static async Task<IpcResponse> HandleSelectByLayer(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("targetLayer", out string targetName) ||
                string.IsNullOrWhiteSpace(targetName) ||
                !req.Args.TryGetValue("sourceLayer", out string sourceName) ||
                string.IsNullOrWhiteSpace(sourceName) ||
                !req.Args.TryGetValue("spatialRelationship", out string relStr) ||
                string.IsNullOrWhiteSpace(relStr))
                return new IpcResponse(false, "args 'targetLayer', 'sourceLayer', & 'spatialRelationship' required", null);

            req.Args.TryGetValue("selectionType", out string selTypeStr);
            var selType = string.IsNullOrWhiteSpace(selTypeStr)
                ? SelectionCombinationMethod.New
                : Enum.Parse<SelectionCombinationMethod>(selTypeStr, ignoreCase: true);

            var rel = (SpatialRelationship)Enum.Parse(typeof(SpatialRelationship), relStr, ignoreCase: true);
            int count = 0;

            await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return;
                var srcFl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(sourceName, StringComparison.OrdinalIgnoreCase));
                var tgtFl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));
                if (srcFl == null || tgtFl == null) return;

                using var srcFc = srcFl.GetFeatureClass();
                var srcExtent = srcFc.GetExtent();

                var sqf = new SpatialQueryFilter
                {
                    SpatialRelationship = rel,
                    FilterGeometry = srcExtent,
                };
                tgtFl.Select(sqf, selType);
                count = tgtFl.SelectionCount;
            });

            return new IpcResponse(true, null, new { done = true, selectionCount = count });
        }

        private static async Task<IpcResponse> HandleGetFeaturesByExtent(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("xmin", out string xminStr) ||
                !req.Args.TryGetValue("ymin", out string yminStr) ||
                !req.Args.TryGetValue("xmax", out string xmaxStr) ||
                !req.Args.TryGetValue("ymax", out string ymaxStr))
                return new IpcResponse(false, "args 'layer', 'xmin', 'ymin', 'xmax', 'ymax' required", null);

            req.Args.TryGetValue("fields", out string fields);
            req.Args.TryGetValue("maxFeatures", out string maxFcStr);
            int maxFeatures = string.IsNullOrWhiteSpace(maxFcStr) ? 100 : int.Parse(maxFcStr);

            double xmin = double.Parse(xminStr);
            double ymin = double.Parse(yminStr);
            double xmax = double.Parse(xmaxStr);
            double ymax = double.Parse(ymaxStr);

            var fieldList = string.IsNullOrWhiteSpace(fields)
                ? null
                : fields.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(f => f.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var result = await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return null;
                var fl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) return null;

                using var fc = fl.GetFeatureClass();
                var sr = fc.GetDefinition().GetSpatialReference();
                var envelope = EnvelopeBuilderEx.CreateEnvelope(xmin, ymin, xmax, ymax, sr);

                var sqf = new SpatialQueryFilter
                {
                    SpatialRelationship = SpatialRelationship.Intersects,
                    FilterGeometry = envelope,
                };
                if (!string.IsNullOrWhiteSpace(fields))
                    sqf.SubFields = fields;

                var features = new List<object>();
                using var cursor = fc.Search(sqf);
                var tableDef = fc.GetDefinition() as TableDefinition;
                var allFields = tableDef?.GetFields() ?? Enumerable.Empty<Field>();
                while (cursor.MoveNext() && features.Count < maxFeatures)
                {
                    using var row = cursor.Current;
                    var attrs = new Dictionary<string, object>();
                    foreach (var f in allFields)
                    {
                        if (fieldList == null || fieldList.Contains(f.Name))
                            attrs[f.Name] = row[f.Name] ?? "<null>";
                    }
                    features.Add(attrs);
                }
                return new { features, count = features.Count, totalExtent = new { xmin, ymin, xmax, ymax } };
            });

            if (result == null)
                return new IpcResponse(false, $"Layer '{layerName}' not found", null);
            return new IpcResponse(true, null, result);
        }

        private static async Task<IpcResponse> HandleFindFeatures(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName))
                return new IpcResponse(false, "args 'layer' required", null);

            req.Args.TryGetValue("where", out string where);
            req.Args.TryGetValue("fields", out string fields);
            req.Args.TryGetValue("maxFeatures", out string maxFcStr);
            int maxFeatures = string.IsNullOrWhiteSpace(maxFcStr) ? 1000 : int.Parse(maxFcStr);

            var fieldList = string.IsNullOrWhiteSpace(fields)
                ? null
                : fields.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(f => f.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var result = await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return null;
                var fl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) return null;

                using var fc = fl.GetFeatureClass();
                var tableDef = fc.GetDefinition() as TableDefinition;
                var allFields = tableDef?.GetFields() ?? Enumerable.Empty<Field>();

                var qf = new QueryFilter
                {
                    WhereClause = where ?? "",
                };
                if (!string.IsNullOrWhiteSpace(fields))
                    qf.SubFields = fields;

                var features = new List<object>();
                using var cursor = fc.Search(qf);
                while (cursor.MoveNext() && features.Count < maxFeatures)
                {
                    using var row = cursor.Current;
                    var attrs = new Dictionary<string, object>();
                    foreach (var f in allFields)
                    {
                        if (fieldList == null || fieldList.Contains(f.Name))
                            attrs[f.Name] = row[f.Name] ?? "<null>";
                    }
                    features.Add(attrs);
                }
                return new { features, count = features.Count };
            });

            if (result == null)
                return new IpcResponse(false, $"Layer '{layerName}' not found", null);
            return new IpcResponse(true, null, result);
        }

        private static async Task<IpcResponse> HandleFlashSelection(IpcRequest req, CancellationToken ct)
        {
            return new IpcResponse(false, "Flash selection not accessible from AddIn SDK", null);
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
                var map = MapView.Active?.Map;
                if (map == null) return;
                var fl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl != null)
                {
                    fl.Select(new QueryFilter());
                    count = fl.SelectionCount;
                }
            });

            return new IpcResponse(true, null, new { done = true, count });
        }
    }
}
