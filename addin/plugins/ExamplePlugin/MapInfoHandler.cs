using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace APBridgeAddIn.Plugins;

[ProBridgeHandler("pro.plugin.mapInfo")]
public class MapInfoHandler : IProBridgeHandler
{
    public string Op => "pro.plugin.mapInfo";

    public Task<IpcResponse> Handle(IpcRequest request, CancellationToken cancellationToken)
    {
        return QueuedTask.Run<IpcResponse>(() =>
        {
            var mapView = MapView.Active;
            if (mapView == null)
                return new IpcResponse(false, "No active map view", null);

            var map = mapView.Map;
            var layers = map.GetLayersAsFlattenedList()
                .Select(l => new { name = l.Name, type = l.GetType().Name, visible = l.IsVisible })
                .ToList();

            var data = new
            {
                mapName = map.Name,
                mapType = map.MapType.ToString(),
                layerCount = layers.Count,
                layers,
                extent = new
                {
                    xmin = mapView.Extent.XMin,
                    ymin = mapView.Extent.YMin,
                    xmax = mapView.Extent.XMax,
                    ymax = mapView.Extent.YMax
                }
            };

            return new IpcResponse(true, null, data);
        });
    }
}
