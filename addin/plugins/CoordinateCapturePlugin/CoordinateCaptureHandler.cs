using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using System.Threading;
using System.Threading.Tasks;

namespace APBridgeAddIn.Plugins;

[ProBridgeHandler("pro.plugin.coordinateCapture")]
public class CoordinateCaptureHandler : IProBridgeHandler
{
    public string Op => "pro.plugin.coordinateCapture";

    public Task<IpcResponse> Handle(IpcRequest request, CancellationToken cancellationToken)
    {
        return QueuedTask.Run<IpcResponse>(() =>
        {
            var mapView = MapView.Active;
            if (mapView == null)
                return new IpcResponse(false, "No active map view", null);

            var extent = mapView.Extent;
            var centerX = (extent.XMin + extent.XMax) / 2.0;
            var centerY = (extent.YMin + extent.YMax) / 2.0;
            var sr = extent.SpatialReference;
            var wkid = sr.Wkid;

            var args = request.Args;
            object projectedX = null;
            object projectedY = null;

            if (args != null && args.TryGetValue("targetWkid", out var targetWkidStr) && int.TryParse(targetWkidStr, out var targetWkid))
            {
                try
                {
                    var targetSr = SpatialReferenceBuilder.CreateSpatialReference(targetWkid);
                    var mapPoint = MapPointBuilder.CreateMapPoint(centerX, centerY, sr);
                    var projected = GeometryEngine.Instance.Project(mapPoint, targetSr) as MapPoint;
                    if (projected != null)
                    {
                        projectedX = projected.X;
                        projectedY = projected.Y;
                    }
                }
                catch { }
            }

            var data = new
            {
                centerX,
                centerY,
                wkid,
                spatialReference = sr.Name,
                projectedX,
                projectedY,
                targetWkid = projectedX != null ? targetWkidStr : null
            };

            return new IpcResponse(true, null, data);
        });
    }
}
