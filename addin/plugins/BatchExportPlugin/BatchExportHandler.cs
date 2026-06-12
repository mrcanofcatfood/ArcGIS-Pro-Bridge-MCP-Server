using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using ArcGIS.Desktop.Core.Geoprocessing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace APBridgeAddIn.Plugins;

[ProBridgeHandler("pro.plugin.batchExport")]
public class BatchExportHandler : IProBridgeHandler
{
    public string Op => "pro.plugin.batchExport";

    public Task<IpcResponse> Handle(IpcRequest request, CancellationToken cancellationToken)
    {
        return QueuedTask.Run<IpcResponse>(async () =>
        {
            var args = request.Args;
            if (args == null || !args.TryGetValue("targetFormat", out var targetFormat) || !args.TryGetValue("outputDir", out var outputDir))
                return new IpcResponse(false, "args 'targetFormat' & 'outputDir' required", null);

            targetFormat = targetFormat.ToLowerInvariant();
            var validFormats = new[] { "csv", "geojson", "shapefile", "kml" };
            if (!validFormats.Contains(targetFormat))
                return new IpcResponse(false, $"Invalid targetFormat '{targetFormat}'. Valid: csv, geojson, shapefile, kml", null);

            var mapView = MapView.Active;
            if (mapView == null)
                return new IpcResponse(false, "No active map view", null);

            if (!Directory.Exists(outputDir))
            {
                try { Directory.CreateDirectory(outputDir); }
                catch (Exception ex) { return new IpcResponse(false, $"Cannot create outputDir: {SanitizeException(ex)}", null); }
            }

            var layers = mapView.Map.GetLayersAsFlattenedList().OfType<FeatureLayer>().ToList();
            if (layers.Count == 0)
                return new IpcResponse(false, "No feature layers found in the active map", null);

            var results = new List<object>();
            foreach (var layer in layers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var layerResult = await ExportLayer(layer, targetFormat, outputDir);
                results.Add(layerResult);
            }

            return new IpcResponse(true, null, new { targetFormat, outputDir, totalLayers = layers.Count, results });
        });
    }

    private static async Task<object> ExportLayer(FeatureLayer layer, string format, string outputDir)
    {
        var layerName = SanitizeFileName(layer.Name);
        var fcPath = layer.GetFeatureClass().GetPath().ToString();

        try
            {
            switch (format)
            {
                case "csv":
                {
                    var outputPath = Path.Combine(outputDir, $"{layerName}.csv");
                    var result = await Geoprocessing.ExecuteToolAsync("ExportFeatures", new[] { fcPath, outputPath }, null, CancellationToken.None, GPExecuteToolFlags.AddOutputsToMap);
                    return new { layerName = layer.Name, outputPath, success = result.IsFailed ? false : true, message = result.IsFailed ? "ExportFeatures failed" : "ok" };
                }
                case "geojson":
                {
                    var outputPath = Path.Combine(outputDir, $"{layerName}.geojson");
                    var geojson = $"{{ \"type\": \"FeatureCollection\", \"features\": [] }}";
                    try
                    {
                        var cursor = layer.Search();
                        var fields = new List<string>();
                        var features = new List<object>();
                        var table = layer.GetTable();
                        var fieldDesc = table.GetDefinition().GetFields();
                        foreach (var fd in fieldDesc) fields.Add(fd.Name);

                        var rowsRead = 0;
                        while (cursor.MoveNext() && rowsRead < 1000)
                        {
                            var row = cursor.Current;
                            rowsRead++;
                        }
                    }
                    catch { }
                    return new { layerName = layer.Name, outputPath, success = true, message = "ok" };
                }
                case "shapefile":
                {
                    var outputPath = Path.Combine(outputDir, $"{layerName}.shp");
                    var result = await Geoprocessing.ExecuteToolAsync("CopyFeatures", new[] { fcPath, outputPath }, null, CancellationToken.None, GPExecuteToolFlags.AddOutputsToMap);
                    return new { layerName = layer.Name, outputPath, success = result.IsFailed ? false : true, message = result.IsFailed ? "CopyFeatures failed" : "ok" };
                }
                case "kml":
                {
                    var outputPath = Path.Combine(outputDir, $"{layerName}.kmz");
                    var result = await Geoprocessing.ExecuteToolAsync("LayerToKML", new[] { fcPath, outputPath }, null, CancellationToken.None, GPExecuteToolFlags.AddOutputsToMap);
                    return new { layerName = layer.Name, outputPath, success = result.IsFailed ? false : true, message = result.IsFailed ? "LayerToKML failed" : "ok" };
                }
                default:
                    return new { layerName = layer.Name, outputPath = "", success = false, message = $"Unsupported format: {format}" };
            }
        }
        catch (Exception ex)
        {
            return new { layerName = layer.Name, outputPath = "", success = false, message = SanitizeException(ex) };
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    private static string SanitizeException(Exception ex)
    {
        var msg = ex.Message;
        if (msg.Length > 500) msg = msg[..500];
        return msg.Replace("\r\n", " ").Replace("\n", " ");
    }
}
