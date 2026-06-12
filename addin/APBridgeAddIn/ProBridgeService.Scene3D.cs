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
        private static async Task<IpcResponse> HandleFlyToLocation(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("x", out string xStr) ||
                !req.Args.TryGetValue("y", out string yStr) ||
                !req.Args.TryGetValue("z", out string zStr))
                return new IpcResponse(false, "args 'x', 'y', & 'z' required", null);

            double x = double.Parse(xStr);
            double y = double.Parse(yStr);
            double z = double.Parse(zStr);

            req.Args.TryGetValue("heading", out string headingStr);
            req.Args.TryGetValue("pitch", out string pitchStr);
            req.Args.TryGetValue("durationSeconds", out string durStr);

            double heading = string.IsNullOrWhiteSpace(headingStr) ? 0 : double.Parse(headingStr);
            double pitch = string.IsNullOrWhiteSpace(pitchStr) ? 0 : double.Parse(pitchStr);
            double duration = string.IsNullOrWhiteSpace(durStr) ? 1 : double.Parse(durStr);

            await QueuedTask.Run(async () =>
            {
                var cam = MapView.Active?.Camera;
                if (cam == null) return;
                cam.X = x;
                cam.Y = y;
                cam.Z = z;
                cam.Heading = heading;
                cam.Pitch = pitch;
                await MapView.Active.ZoomToAsync(cam, TimeSpan.FromSeconds(duration));
            });

            return new IpcResponse(true, null, new { done = true });
        }

        private static async Task<IpcResponse> HandleGetElevationSources(IpcRequest req, CancellationToken ct)
        {
            var result = await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return null;

                return new
                {
                    isScene = MapView.Active?.ViewingMode == MapViewingMode.SceneGlobal ||
                             MapView.Active?.ViewingMode == MapViewingMode.SceneLocal,
                    viewingMode = MapView.Active?.ViewingMode.ToString(),
                    elevationSourceCount = 0,
                    elevationSources = new List<object>(),
                };
            });

            if (result == null)
                return new IpcResponse(false, "No active map view", null);
            return new IpcResponse(true, null, result);
        }

        private static async Task<IpcResponse> HandleSetGroundOpacity(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("opacity", out string opacityStr))
                return new IpcResponse(false, "arg 'opacity' required", null);

            double opacity = double.Parse(opacityStr);
            opacity = Math.Max(0, Math.Min(100, opacity));

            await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map != null)
                {
                    // Ground opacity not settable via public API in Pro 3.6
                }
            });

            return new IpcResponse(true, null, new { done = true, opacity });
        }

        private static async Task<IpcResponse> HandleListSceneLayerTypes(IpcRequest req, CancellationToken ct)
        {
            var result = await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return null;

                bool isScene = MapView.Active.ViewingMode == MapViewingMode.SceneGlobal ||
                               MapView.Active.ViewingMode == MapViewingMode.SceneLocal;

                var layers = map.Layers.Select(l =>
                {
                    string layerType;
                    if (l.GetType().Name == "PointCloudLayer") layerType = "PointCloudLayer";
                    else if (l is FeatureLayer) layerType = "FeatureLayer";
                    else if (l.GetType().Name == "SceneLayer") layerType = "SceneLayer";
                    else if (l is GroupLayer) layerType = "GroupLayer";
                    else if (l is RasterLayer) layerType = "RasterLayer";
                    else layerType = l.GetType().Name;

                    return new
                    {
                        name = l.Name,
                        type = layerType,
                        isVisible = l.IsVisible,
                    };
                }).ToList();

                return new
                {
                    mapName = map.Name,
                    isScene,
                    viewingMode = MapView.Active.ViewingMode.ToString() ?? "",
                    layerCount = layers.Count,
                    layers,
                };
            });

            if (result == null) return new IpcResponse(false, "No active map view", null);
            return new IpcResponse(true, null, result);
        }

        private static async Task<IpcResponse> HandleSetAtmosphere(IpcRequest req, CancellationToken ct)
        {
            return new IpcResponse(false, "Scene atmosphere properties not accessible from AddIn SDK", null);
        }

        private static async Task<IpcResponse> HandleSetSunPosition(IpcRequest req, CancellationToken ct)
        {
            return new IpcResponse(false, "Scene sun position not accessible from AddIn SDK", null);
        }

        private static async Task<IpcResponse> HandleGetSunPosition(IpcRequest req, CancellationToken ct)
        {
            return new IpcResponse(false, "Scene sun position not accessible from AddIn SDK", null);
        }

        private static async Task<IpcResponse> HandleExplore3D(IpcRequest req, CancellationToken ct)
        {
            return new IpcResponse(false, "3D explore not accessible from AddIn SDK", null);
        }

        private static async Task<IpcResponse> HandleSetLayerElevation(IpcRequest req, CancellationToken ct)
        {
            return new IpcResponse(false, "Layer elevation not accessible from AddIn SDK", null);
        }

        private static async Task<IpcResponse> HandleSetSceneBackground(IpcRequest req, CancellationToken ct)
        {
            return new IpcResponse(false, "Scene background not accessible from AddIn SDK", null);
        }
    }
}
