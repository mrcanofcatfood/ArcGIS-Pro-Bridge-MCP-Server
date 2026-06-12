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
        private static async Task<IpcResponse> HandleSetLayerVisibility(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("visible", out string visibleStr))
                return new IpcResponse(false, "args 'layer' & 'visible' required", null);

            bool visible = bool.Parse(visibleStr);
            await QueuedTask.Run(() =>
            {
                MapView.Active?.Map?.Layers
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase))
                    ?.SetVisibility(visible);
            });
            return new IpcResponse(true, null, new { done = true });
        }

        private static async Task<IpcResponse> HandleIs3d(IpcRequest req, CancellationToken ct)
        {
            var result = await QueuedTask.Run(() =>
            {
                var mode = MapView.Active?.ViewingMode;
                if (mode == null) return new { is3d = false, viewingMode = "NoActiveView" };
                bool is3d = mode == MapViewingMode.SceneGlobal || mode == MapViewingMode.SceneLocal;
                return new { is3d, viewingMode = mode.ToString() };
            });
            return new IpcResponse(true, null, result);
        }

        private static async Task<IpcResponse> HandleGetLayerRenderer(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName))
                return new IpcResponse(false, "arg 'layer' required", null);

            var info = await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return null;
                var fl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) return null;

                var renderer = fl.GetRenderer();
                if (renderer == null)
                    return new { rendererType = "None", field = (string)null, layerName };

                string field = null;
                if (renderer is CIMUniqueValueRenderer uv)
                    field = uv.Fields?.FirstOrDefault();
                else if (renderer is CIMClassBreaksRenderer cb)
                    field = cb?.Field?.ToString();

                return new
                {
                    rendererType = renderer.GetType().Name,
                    field,
                    layerName,
                };
            });

            if (info == null)
                return new IpcResponse(false, $"Layer '{layerName}' not found", null);
            return new IpcResponse(true, null, info);
        }

        private static async Task<IpcResponse> HandleSetLayerColor(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("r", out string rStr) ||
                !req.Args.TryGetValue("g", out string gStr) ||
                !req.Args.TryGetValue("b", out string bStr))
                return new IpcResponse(false, "args 'layer', 'r', 'g', 'b' required", null);

            byte r = byte.Parse(rStr);
            byte g = byte.Parse(gStr);
            byte b = byte.Parse(bStr);
            string warning = null;

            await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return;
                var fl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) return;

                var rendererRef = fl.GetRenderer();
                if (rendererRef is CIMSimpleRenderer simpleRenderer)
                {
                    var newRenderer = simpleRenderer.Clone() as CIMSimpleRenderer;
                    if (newRenderer?.Symbol?.Symbol is CIMPolygonSymbol newPoly)
                    {
                        bool colorSet = false;
                        if (newPoly.SymbolLayers != null)
                        {
                            foreach (var sl in newPoly.SymbolLayers)
                            {
                                if (sl is CIMSolidFill fill)
                                {
                                    fill.Color = new CIMRGBColor { R = r, G = g, B = b, Alpha = 100 };
                                    colorSet = true;
                                }
                            }
                        }
                        if (colorSet)
                        {
                            var newRef = new CIMSymbolReference { Symbol = newPoly };
                            newRenderer.Symbol = newRef;
                            fl.SetRenderer(newRenderer);
                            return;
                        }
                    }
                    warning = "Layer symbol type is not polygon";
                }
                else
                {
                    warning = "Layer does not use a simple renderer";
                }
            });

            return new IpcResponse(true, null, new { done = true, warning });
        }

        private static async Task<IpcResponse> HandleRemoveLayer(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName))
                return new IpcResponse(false, "arg 'layer' required", null);

            await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return;
                var layer = map.Layers
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (layer != null)
                    MapView.Active.Map.RemoveLayer(layer);
            });

            return new IpcResponse(true, null, new { done = true });
        }

        private static async Task<IpcResponse> HandleAddLayerFromFile(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("path", out string filePath) ||
                string.IsNullOrWhiteSpace(filePath))
                return new IpcResponse(false, "arg 'path' required", null);

            Layer createdLayer = null;
            await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return;
                var uri = new Uri(filePath);
                createdLayer = LayerFactory.Instance.CreateLayer(uri, map, 0);
            });

            if (createdLayer == null)
                return new IpcResponse(false, "Failed to add layer. Verify the path is valid.", null);
            return new IpcResponse(true, null, new { done = true, layerName = createdLayer.Name });
        }

        private static async Task<IpcResponse> HandleAddLayerFromService(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("url", out string serviceUrl) ||
                string.IsNullOrWhiteSpace(serviceUrl))
                return new IpcResponse(false, "arg 'url' required", null);

            req.Args.TryGetValue("serviceType", out string serviceType);

            Layer createdLayer = null;
            await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return;
                try
                {
                    var uri = new Uri(serviceUrl);
                    createdLayer = LayerFactory.Instance.CreateLayer(uri, map, 0);
                }
                catch { }
            });

            if (createdLayer == null)
                return new IpcResponse(false, "Failed to add layer from service URL.", null);
            return new IpcResponse(true, null, new { done = true, layerName = createdLayer.Name, url = serviceUrl });
        }

        private static async Task<IpcResponse> HandleSetLayerTransparency(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("transparency", out string transpStr))
                return new IpcResponse(false, "args 'layer' & 'transparency' required", null);

            double transparency = double.Parse(transpStr);
            transparency = Math.Max(0, Math.Min(100, transparency));

            await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return;
                var fl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                fl?.SetTransparency(transparency);
            });

            return new IpcResponse(true, null, new { done = true });
        }

        private static async Task<IpcResponse> HandleSetLabelsEnabled(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("enabled", out string enabledStr))
                return new IpcResponse(false, "args 'layer' & 'enabled' required", null);

            bool enabled = bool.Parse(enabledStr);

            await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return;
                var fl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl != null)
                    fl.SetLabelVisibility(enabled);
            });

            return new IpcResponse(true, null, new { done = true });
        }

        private static (byte r, byte g, byte b) HsvToRgb(double h, double s, double v)
        {
            int hi = (int)Math.Floor(h / 60) % 6;
            double f = h / 60 - Math.Floor(h / 60);
            double p = v * (1 - s);
            double q = v * (1 - f * s);
            double t = v * (1 - (1 - f) * s);
            return hi switch
            {
                0 => ((byte)(v * 255), (byte)(t * 255), (byte)(p * 255)),
                1 => ((byte)(q * 255), (byte)(v * 255), (byte)(p * 255)),
                2 => ((byte)(p * 255), (byte)(v * 255), (byte)(t * 255)),
                3 => ((byte)(p * 255), (byte)(q * 255), (byte)(v * 255)),
                4 => ((byte)(t * 255), (byte)(p * 255), (byte)(v * 255)),
                5 => ((byte)(v * 255), (byte)(p * 255), (byte)(q * 255)),
                _ => ((byte)0, (byte)0, (byte)0),
            };
        }

        private static CIMSymbolReference CreateSymbolRefForGeometry(byte r, byte g, byte b, esriGeometryType shapeType)
        {
            var color = ColorFactory.Instance.CreateRGBColor(r, g, b);
            CIMSymbol symbol;
            switch (shapeType)
            {
                case esriGeometryType.esriGeometryPolygon:
                    symbol = SymbolFactory.Instance.ConstructPolygonSymbol(color, SimpleFillStyle.Solid);
                    break;
                case esriGeometryType.esriGeometryPolyline:
                    symbol = SymbolFactory.Instance.ConstructLineSymbol(color, 1.0, SimpleLineStyle.Solid);
                    break;
                case esriGeometryType.esriGeometryPoint:
                case esriGeometryType.esriGeometryMultipoint:
                    symbol = SymbolFactory.Instance.ConstructPointSymbol(color, 8.0, SimpleMarkerStyle.Circle);
                    break;
                default:
                    symbol = SymbolFactory.Instance.ConstructPolygonSymbol(color);
                    break;
            }
            return new CIMSymbolReference { Symbol = symbol };
        }

        private static async Task<IpcResponse> HandleApplyUniqueValueRenderer(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("field", out string field) ||
                string.IsNullOrWhiteSpace(field))
                return new IpcResponse(false, "args 'layer' & 'field' required", null);

            req.Args.TryGetValue("colorRamp", out string colorRampJson);

            string warning = null;
            int classCount = 0;

            await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) { warning = "Layer not found"; return; }
                var fl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) { warning = "Layer not found"; return; }

                using var fc = fl.GetFeatureClass();
                var shapeType = fl.ShapeType;

                var qf = new QueryFilter { SubFields = field };
                var uniqueValues = new HashSet<string>();
                using (var cursor = fc.Search(qf, true))
                {
                    while (cursor.MoveNext())
                    {
                        using var row = cursor.Current;
                        var val = row[field];
                        if (val != null && val != DBNull.Value)
                            uniqueValues.Add(val.ToString());
                    }
                }

                var ordered = uniqueValues.OrderBy(v => v).ToList();
                classCount = ordered.Count;

                // Parse custom color ramp or generate evenly-spaced hues
                List<(byte r, byte g, byte b)> colors;
                if (!string.IsNullOrWhiteSpace(colorRampJson))
                {
                    try
                    {
                        var parsed = JsonSerializer.Deserialize<List<List<byte>>>(colorRampJson);
                        colors = parsed.Select(c => (c[0], c[1], c[2])).ToList();
                    }
                    catch
                    {
                        colors = new List<(byte, byte, byte)>();
                        warning = "Invalid colorRamp JSON, using auto-generated colors";
                    }
                }
                else
                {
                    colors = new List<(byte, byte, byte)>();
                }

                while (colors.Count < ordered.Count)
                {
                    int idx = colors.Count;
                    double hue = (idx * 360.0 / Math.Max(ordered.Count, 1)) % 360;
                    var (r, g, b) = HsvToRgb(hue, 0.7, 0.9);
                    colors.Add((r, g, b));
                }

                var classes = ordered.Select((val, idx) =>
                {
                    var (r, g, b) = colors[idx % colors.Count];
                    return new CIMUniqueValueClass
                    {
                        Label = val,
                        Values = new[] { new CIMUniqueValue { FieldValues = new[] { val } } },
                        Symbol = CreateSymbolRefForGeometry(r, g, b, shapeType),
                    };
                }).ToArray();

                var defaultColor = ColorFactory.Instance.CreateRGBColor(220, 220, 220);
                CIMSymbol defaultSymbol;
                switch (shapeType)
                {
                    case esriGeometryType.esriGeometryPolygon:
                        defaultSymbol = SymbolFactory.Instance.ConstructPolygonSymbol(defaultColor);
                        break;
                    case esriGeometryType.esriGeometryPolyline:
                        defaultSymbol = SymbolFactory.Instance.ConstructLineSymbol(defaultColor, 0.5, SimpleLineStyle.Solid);
                        break;
                    default:
                        defaultSymbol = SymbolFactory.Instance.ConstructPointSymbol(defaultColor, 6.0, SimpleMarkerStyle.Circle);
                        break;
                }

                var renderer = new CIMUniqueValueRenderer
                {
                    Fields = new[] { field },
                    DefaultLabel = "Other",
                    DefaultSymbol = new CIMSymbolReference { Symbol = defaultSymbol },
                    Groups = new[]
                    {
                        new CIMUniqueValueGroup
                        {
                            Heading = field,
                            Classes = classes,
                        }
                    },
                };

                fl.SetRenderer(renderer);
            });

            return new IpcResponse(true, null, new { done = true, classCount, warning });
        }

        private static async Task<IpcResponse> HandleApplyClassBreaksRenderer(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("field", out string field) ||
                string.IsNullOrWhiteSpace(field))
                return new IpcResponse(false, "args 'layer' & 'field' required", null);

            req.Args.TryGetValue("breakCount", out string breakCountStr);
            int breakCount = string.IsNullOrWhiteSpace(breakCountStr) ? 5 : int.Parse(breakCountStr);
            breakCount = Math.Max(2, Math.Min(20, breakCount));

            string warning = null;

            await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) { warning = "Layer not found"; return; }
                var fl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) { warning = "Layer not found"; return; }

                using var fc = fl.GetFeatureClass();
                var shapeType = fl.ShapeType;

                // Collect values
                var values = new List<double>();
                using (var cursor = fc.Search(new QueryFilter { SubFields = field }, true))
                {
                    while (cursor.MoveNext())
                    {
                        using var row = cursor.Current;
                        var val = row[field];
                        if (val != null && val != DBNull.Value)
                        {
                            if (double.TryParse(val.ToString(), out double d))
                                values.Add(d);
                        }
                    }
                }

                if (values.Count == 0)
                { warning = "No numeric values found"; return; }

                double minVal = values.Min();
                double maxVal = values.Max();
                double range = maxVal - minVal;
                double interval = range / breakCount;

                var breaks = new CIMClassBreak[breakCount];
                for (int i = 0; i < breakCount; i++)
                {
                    double upper = (i == breakCount - 1) ? maxVal : minVal + (i + 1) * interval;
                    double lower = minVal + i * interval;
                    double hue = (i * 360.0 / breakCount) % 360;
                    var (r, g, b) = HsvToRgb(hue, 0.7, 0.9);

                    breaks[i] = new CIMClassBreak
                    {
                        Label = $"{lower:F2} - {upper:F2}",
                        UpperBound = upper,
                        Symbol = CreateSymbolRefForGeometry(r, g, b, shapeType),
                    };
                }

                var renderer = new CIMClassBreaksRenderer
                {
                    Field = field,
                    Breaks = breaks,
                    ClassificationMethod = ClassificationMethod.EqualInterval,
                };

                fl.SetRenderer(renderer);
            });

            return new IpcResponse(true, null, new { done = true, breakCount, warning });
        }

        private static async Task<IpcResponse> HandleGetLayerDescription(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName))
                return new IpcResponse(false, "arg 'layer' required", null);

            var result = await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return null;
                var layer = map.Layers
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (layer == null) return null;
                string desc = "";
                try { var cim = layer.GetDefinition(); if (cim is ArcGIS.Core.CIM.CIMBasicFeatureLayer bfl) desc = bfl.Description ?? ""; } catch { }
                return new { layerName, description = desc };
            });

            if (result == null) return new IpcResponse(false, $"Layer '{layerName}' not found", null);
            return new IpcResponse(true, null, result);
        }

        private static async Task<IpcResponse> HandleSetLayerDescription(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("description", out string description))
                return new IpcResponse(false, "args 'layer' & 'description' required", null);

            await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return;
                var layer = map.Layers
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (layer == null) return;
                var cimDef = layer.GetDefinition() as CIMFeatureLayer;
                if (cimDef == null) return;
                var editable = cimDef.Clone() as CIMFeatureLayer;
                if (editable == null) return;
                editable.Description = description;
                layer.SetDefinition(editable);
            });

            return new IpcResponse(true, null, new { done = true });
        }
    }
}
