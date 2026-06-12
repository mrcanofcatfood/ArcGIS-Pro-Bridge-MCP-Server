using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace APBridgeAddIn.Plugins;

[ProBridgeHandler("pro.plugin.featureInspector")]
public class FeatureInspectorHandler : IProBridgeHandler
{
    public string Op => "pro.plugin.featureInspector";

    public Task<IpcResponse> Handle(IpcRequest request, CancellationToken cancellationToken)
    {
        return QueuedTask.Run<IpcResponse>(() =>
        {
            var args = request.Args;
            if (args == null || !args.TryGetValue("layer", out var layerName) || !args.TryGetValue("oid", out var oidStr) || !long.TryParse(oidStr, out var oid))
                return new IpcResponse(false, "args 'layer' (string) & 'oid' (integer) required", null);

            var mapView = MapView.Active;
            if (mapView == null)
                return new IpcResponse(false, "No active map view", null);

            var layer = mapView.Map.GetLayersAsFlattenedList()
                .OfType<FeatureLayer>()
                .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));

            if (layer == null)
                return new IpcResponse(false, $"Layer '{layerName}' not found", null);

            try
            {
                var table = layer.GetTable();
                var queryFilter = new QueryFilter { WhereClause = $"OBJECTID = {oid}" };
                using var cursor = table.Search(queryFilter, false);
                if (!cursor.MoveNext())
                    return new IpcResponse(false, $"Feature with OID {oid} not found in layer '{layerName}'", null);

                using var row = cursor.Current;
                var attributes = new Dictionary<string, object>();
                foreach (var field in row.GetFields())
                {
                    var val = row[field];
                    attributes[field] = val ?? DBNull.Value;
                }

                var geom = row.GetGeometry();
                string geometryType = null;
                int? vertexCount = null;
                double[] coordinates = null;

                if (geom != null)
                {
                    geometryType = geom.GeometryType.ToString();

                    if (geom is Polygon poly)
                    {
                        var pts = poly.Points;
                        vertexCount = pts.Count;
                    }
                    else if (geom is Polyline line)
                    {
                        var pts = line.Points;
                        vertexCount = pts.Count;
                    }
                    else if (geom is MapPoint pt)
                    {
                        coordinates = new[] { pt.X, pt.Y };
                    }
                }

                var data = new
                {
                    oid,
                    attributes,
                    geometryType,
                    vertexCount,
                    coordinates,
                    layerName = layer.Name
                };

                return new IpcResponse(true, null, data);
            }
            catch (Exception ex)
            {
                return new IpcResponse(false, $"Error inspecting feature: {SanitizeException(ex)}", null);
            }
        });
    }

    private static string SanitizeException(Exception ex)
    {
        var msg = ex.Message;
        if (msg.Length > 500) msg = msg[..500];
        return msg.Replace("\r\n", " ").Replace("\n", " ");
    }
}
