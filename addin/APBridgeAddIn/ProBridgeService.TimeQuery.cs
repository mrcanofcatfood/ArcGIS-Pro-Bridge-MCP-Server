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
        private static Task<IpcResponse> HandleGetGeometryDistance(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("x1", out string x1Str) ||
                !req.Args.TryGetValue("y1", out string y1Str) ||
                !req.Args.TryGetValue("x2", out string x2Str) ||
                !req.Args.TryGetValue("y2", out string y2Str))
                return Task.FromResult(new IpcResponse(false, "args 'x1', 'y1', 'x2', 'y2' required", null));

            double x1 = double.Parse(x1Str);
            double y1 = double.Parse(y1Str);
            double x2 = double.Parse(x2Str);
            double y2 = double.Parse(y2Str);

            var pt1 = MapPointBuilderEx.CreateMapPoint(x1, y1);
            var pt2 = MapPointBuilderEx.CreateMapPoint(x2, y2);
            double distance = GeometryEngine.Instance.Distance(pt1, pt2);

            return Task.FromResult(new IpcResponse(true, null, new
            {
                distance,
                from = new { x = x1, y = y1 },
                to = new { x = x2, y = y2 }
            }));
        }

        private static async Task<IpcResponse> HandleCountFeaturesByExpression(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("where", out string where) ||
                string.IsNullOrWhiteSpace(where))
                return new IpcResponse(false, "args 'layer' & 'where' required", null);

            if (!IsValidWhereClause(where))
                return new IpcResponse(false, "Invalid characters in where clause", null);

            int count = await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return -1;
                var fl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) return -1;

                using var fc = fl.GetFeatureClass();
                using var cursor = fc.Search(new QueryFilter { WhereClause = where }, true);
                int cnt = 0;
                while (cursor.MoveNext()) cnt++;
                return cnt;
            });

            if (count < 0)
                return new IpcResponse(false, $"Layer '{layerName}' not found", null);
            return new IpcResponse(true, null, new { count, where });
        }

        private static async Task<IpcResponse> HandleIsTimeEnabled(IpcRequest req, CancellationToken ct)
        {
            return new IpcResponse(false, "Time extent not accessible from AddIn SDK", null);
        }

        private static async Task<IpcResponse> HandleGetTimeExtent(IpcRequest req, CancellationToken ct)
        {
            return new IpcResponse(false, "Time extent not accessible from AddIn SDK", null);
        }

        private static async Task<IpcResponse> HandleSetTimeExtent(IpcRequest req, CancellationToken ct)
        {
            return new IpcResponse(false, "Time extent not accessible from AddIn SDK", null);
        }

        private static Task<IpcResponse> HandleProjectGeometry(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("x", out string xStr) || !double.TryParse(xStr, out double x) ||
                !req.Args.TryGetValue("y", out string yStr) || !double.TryParse(yStr, out double y) ||
                !req.Args.TryGetValue("fromWkid", out string fromStr) || !int.TryParse(fromStr, out int fromWkid) ||
                !req.Args.TryGetValue("toWkid", out string toStr) || !int.TryParse(toStr, out int toWkid))
                return Task.FromResult(new IpcResponse(false, "args 'x', 'y', 'fromWkid', & 'toWkid' required", null));

            try
            {
                var fromSr = SpatialReferenceBuilder.CreateSpatialReference(fromWkid);
                var toSr = SpatialReferenceBuilder.CreateSpatialReference(toWkid);
                var pt = MapPointBuilderEx.CreateMapPoint(x, y, fromSr);
                var projected = GeometryEngine.Instance.Project(pt, toSr) as MapPoint;
                if (projected == null)
                    return Task.FromResult(new IpcResponse(false, "Projection returned null", null));

                return Task.FromResult(new IpcResponse(true, null, new
                {
                    x = projected.X,
                    y = projected.Y,
                    fromWkid,
                    toWkid,
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(new IpcResponse(false, $"Projection failed: {SanitizeException(ex)}", null));
            }
        }

        private static Task<IpcResponse> HandleSetStatusBarMessage(IpcRequest req, CancellationToken ct)
        {
            return Task.FromResult(new IpcResponse(false, "StatusBar not accessible from AddIn context", null));
        }
    }
}
