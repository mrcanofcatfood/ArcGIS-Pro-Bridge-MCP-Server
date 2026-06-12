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
        private static async Task<IpcResponse> HandleListLayouts(IpcRequest req, CancellationToken ct)
        {
            if (Project.Current == null)
                return new IpcResponse(false, "No project open", null);
            var layouts = await QueuedTask.Run(() =>
                Project.Current.GetItems<LayoutProjectItem>()
                    .Select(l => new { l.Name, l.Path })
                    .ToList());
            return new IpcResponse(true, null, layouts);
        }

        private static async Task<IpcResponse> HandleExportLayoutToFile(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layoutName", out string layoutName) ||
                string.IsNullOrWhiteSpace(layoutName) ||
                !req.Args.TryGetValue("outputPath", out string outputPath) ||
                string.IsNullOrWhiteSpace(outputPath))
                return new IpcResponse(false, "args 'layoutName' & 'outputPath' required", null);
            if (Project.Current == null)
                return new IpcResponse(false, "No project open", null);

            req.Args.TryGetValue("format", out string format);
            req.Args.TryGetValue("dpi", out string dpiStr);
            int dpi = string.IsNullOrWhiteSpace(dpiStr) ? 300 : int.Parse(dpiStr);

            await QueuedTask.Run(() =>
            {
                var layoutItem = Project.Current.GetItems<LayoutProjectItem>()
                    .FirstOrDefault(l => l.Name.Equals(layoutName, StringComparison.OrdinalIgnoreCase));
                if (layoutItem == null) return;

                using var layout = layoutItem.GetLayout();
                var isPdf = string.IsNullOrWhiteSpace(format) ||
                    format.Equals("PDF", StringComparison.OrdinalIgnoreCase);

                if (isPdf)
                {
                    layout.Export(new PDFFormat { OutputFileName = outputPath, Resolution = dpi });
                }
                else
                {
                    layout.Export(new PNGFormat { OutputFileName = outputPath, Resolution = dpi });
                }
            });

            return new IpcResponse(true, null, new { done = true, outputPath });
        }

        private static async Task<IpcResponse> HandleListLayoutElements(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layoutName", out string layoutName) ||
                string.IsNullOrWhiteSpace(layoutName))
                return new IpcResponse(false, "arg 'layoutName' required", null);
            if (Project.Current == null)
                return new IpcResponse(false, "No project open", null);

            var result = await QueuedTask.Run(() =>
            {
                var layoutItem = Project.Current.GetItems<LayoutProjectItem>()
                    .FirstOrDefault(l => l.Name.Equals(layoutName, StringComparison.OrdinalIgnoreCase));
                if (layoutItem == null) return null;

                using var layout = layoutItem.GetLayout();
                var elements = new List<object>();

                var layoutElements = layout.FindElements(Enumerable.Empty<string>());
                foreach (var el in layoutElements)
                    elements.Add(new { name = el.Name ?? "", type = el.GetType().Name, elementType = "GraphicsElement", visible = el.IsVisible });

                var mapFrames = layout.FindElements(Enumerable.Empty<string>()).OfType<MapFrame>();
                foreach (var mf in mapFrames)
                    elements.Add(new { name = mf.Name ?? "", type = mf.GetType().Name, elementType = "MapFrame", mapName = mf.Map?.Name, visible = mf.IsVisible });

                var mapSurrounds = layout.FindElements(Enumerable.Empty<string>()).OfType<MapSurround>();
                foreach (var ms in mapSurrounds)
                    elements.Add(new { name = ms.Name ?? "", type = ms.GetType().Name, elementType = "MapSurround", visible = ms.IsVisible });

                return new { layoutName, elementCount = elements.Count, elements };
            });

            if (result == null) return new IpcResponse(false, $"Layout '{layoutName}' not found", null);
            return new IpcResponse(true, null, result);
        }

        private static async Task<IpcResponse> HandleAddLayoutText(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layoutName", out string layoutName) || string.IsNullOrWhiteSpace(layoutName) ||
                !req.Args.TryGetValue("text", out string text) || string.IsNullOrWhiteSpace(text) ||
                !req.Args.TryGetValue("x", out string xStr) || !double.TryParse(xStr, out double x) ||
                !req.Args.TryGetValue("y", out string yStr) || !double.TryParse(yStr, out double y))
                return new IpcResponse(false, "args 'layoutName', 'text', 'x', & 'y' required", null);

            if (Project.Current == null)
                return new IpcResponse(false, "No project open", null);

            await QueuedTask.Run(async () =>
            {
                try
                {
                    var projPath = Project.Current.Path;
                    var pyCode = "import arcpy\n"
                        + $"proj = {System.Text.Json.JsonSerializer.Serialize(projPath)}\n"
                        + $"lay_name = {System.Text.Json.JsonSerializer.Serialize(layoutName)}\n"
                        + $"txt = {System.Text.Json.JsonSerializer.Serialize(text)}\n"
                        + $"px = {x}\npy = {y}\n"
                        + "try:\n"
                        + "    aprx = arcpy.mp.ArcGISProject(proj)\n"
                        + "    lyt = aprx.listLayouts(lay_name)[0]\n"
                        + "    el = lyt.createTextElement(txt, 'TEXT', arcpy.Point(px, py))\n"
                        + "    aprx.save()\n"
                        + "    print('ok')\n"
                        + "except Exception as ex:\n"
                        + "    print(f'error: {ex}')\n";
                    await RunProPythonAsync(pyCode, 30, ct);
                }
                catch { }
            });

            return new IpcResponse(true, null, new { done = true, layoutName });
        }

        private static async Task<IpcResponse> HandleAddLayoutPicture(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layoutName", out string layoutName) || string.IsNullOrWhiteSpace(layoutName) ||
                !req.Args.TryGetValue("imagePath", out string imagePath) || string.IsNullOrWhiteSpace(imagePath) ||
                !req.Args.TryGetValue("x", out string xStr) || !double.TryParse(xStr, out double x) ||
                !req.Args.TryGetValue("y", out string yStr) || !double.TryParse(yStr, out double y))
                return new IpcResponse(false, "args 'layoutName', 'imagePath', 'x', & 'y' required", null);

            if (Project.Current == null)
                return new IpcResponse(false, "No project open", null);

            await QueuedTask.Run(async () =>
            {
                try
                {
                    var projPath = Project.Current.Path;
                    var pyCode = "import arcpy\n"
                        + $"proj = {System.Text.Json.JsonSerializer.Serialize(projPath)}\n"
                        + $"lay_name = {System.Text.Json.JsonSerializer.Serialize(layoutName)}\n"
                        + $"img_path = {System.Text.Json.JsonSerializer.Serialize(imagePath)}\n"
                        + $"px = {x}\npy = {y}\n"
                        + "try:\n"
                        + "    aprx = arcpy.mp.ArcGISProject(proj)\n"
                        + "    lyt = aprx.listLayouts(lay_name)[0]\n"
                        + "    el = lyt.createPictureElement(img_path, arcpy.Point(px, py))\n"
                        + "    aprx.save()\n"
                        + "    print('ok')\n"
                        + "except Exception as ex:\n"
                        + "    print(f'error: {ex}')\n";
                    await RunProPythonAsync(pyCode, 30, ct);
                }
                catch { }
            });

            return new IpcResponse(true, null, new { done = true, layoutName });
        }

        private static async Task<IpcResponse> HandleAddLayoutLegend(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layoutName", out string layoutName) || string.IsNullOrWhiteSpace(layoutName) ||
                !req.Args.TryGetValue("mapFrameName", out string mapFrameName) || string.IsNullOrWhiteSpace(mapFrameName) ||
                !req.Args.TryGetValue("x", out string xStr) || !double.TryParse(xStr, out double x) ||
                !req.Args.TryGetValue("y", out string yStr) || !double.TryParse(yStr, out double y))
                return new IpcResponse(false, "args 'layoutName', 'mapFrameName', 'x', & 'y' required", null);

            if (Project.Current == null)
                return new IpcResponse(false, "No project open", null);

            await QueuedTask.Run(async () =>
            {
                try
                {
                    var projPath = Project.Current.Path;
                    var pyCode = "import arcpy\n"
                        + $"proj = {System.Text.Json.JsonSerializer.Serialize(projPath)}\n"
                        + $"lay_name = {System.Text.Json.JsonSerializer.Serialize(layoutName)}\n"
                        + $"mf_name = {System.Text.Json.JsonSerializer.Serialize(mapFrameName)}\n"
                        + $"px = {x}\npy = {y}\n"
                        + "try:\n"
                        + "    aprx = arcpy.mp.ArcGISProject(proj)\n"
                        + "    lyt = aprx.listLayouts(lay_name)[0]\n"
                        + "    mf = lyt.listElements('MAPFRAME_ELEMENT', mf_name)[0]\n"
                        + "    el = lyt.createMapSurroundElement(mf, 'LEGEND', arcpy.Point(px, py))\n"
                        + "    aprx.save()\n"
                        + "    print('ok')\n"
                        + "except Exception as ex:\n"
                        + "    print(f'error: {ex}')\n";
                    await RunProPythonAsync(pyCode, 30, ct);
                }
                catch { }
            });

            return new IpcResponse(true, null, new { done = true, layoutName });
        }

        private static async Task<IpcResponse> HandleAddLayoutNorthArrow(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layoutName", out string layoutName) || string.IsNullOrWhiteSpace(layoutName) ||
                !req.Args.TryGetValue("mapFrameName", out string mapFrameName) || string.IsNullOrWhiteSpace(mapFrameName) ||
                !req.Args.TryGetValue("x", out string xStr) || !double.TryParse(xStr, out double x) ||
                !req.Args.TryGetValue("y", out string yStr) || !double.TryParse(yStr, out double y))
                return new IpcResponse(false, "args 'layoutName', 'mapFrameName', 'x', & 'y' required", null);

            if (Project.Current == null)
                return new IpcResponse(false, "No project open", null);

            await QueuedTask.Run(async () =>
            {
                try
                {
                    var projPath = Project.Current.Path;
                    var pyCode = "import arcpy\n"
                        + $"proj = {System.Text.Json.JsonSerializer.Serialize(projPath)}\n"
                        + $"lay_name = {System.Text.Json.JsonSerializer.Serialize(layoutName)}\n"
                        + $"mf_name = {System.Text.Json.JsonSerializer.Serialize(mapFrameName)}\n"
                        + $"px = {x}\npy = {y}\n"
                        + "try:\n"
                        + "    aprx = arcpy.mp.ArcGISProject(proj)\n"
                        + "    lyt = aprx.listLayouts(lay_name)[0]\n"
                        + "    mf = lyt.listElements('MAPFRAME_ELEMENT', mf_name)[0]\n"
                        + "    el = lyt.createMapSurroundElement(mf, 'NORTH_ARROW', arcpy.Point(px, py))\n"
                        + "    aprx.save()\n"
                        + "    print('ok')\n"
                        + "except Exception as ex:\n"
                        + "    print(f'error: {ex}')\n";
                    await RunProPythonAsync(pyCode, 30, ct);
                }
                catch { }
            });

            return new IpcResponse(true, null, new { done = true, layoutName });
        }

        private static async Task<IpcResponse> HandleRemoveLayoutElement(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layoutName", out string layoutName) || string.IsNullOrWhiteSpace(layoutName) ||
                !req.Args.TryGetValue("elementName", out string elementName) || string.IsNullOrWhiteSpace(elementName))
                return new IpcResponse(false, "args 'layoutName' & 'elementName' required", null);
            if (Project.Current == null)
                return new IpcResponse(false, "No project open", null);

            string warning = null;

            await QueuedTask.Run(() =>
            {
                var layoutItem = Project.Current.GetItems<LayoutProjectItem>()
                    .FirstOrDefault(l => l.Name.Equals(layoutName, StringComparison.OrdinalIgnoreCase));
                if (layoutItem == null) { warning = "Layout not found"; return; }
                var layout = layoutItem.GetLayout();

                var element = layout.FindElements(Enumerable.Empty<string>()).OfType<Element>()
                    .FirstOrDefault(e => e.Name.Equals(elementName, StringComparison.OrdinalIgnoreCase));
                if (element == null) { warning = "Element not found"; return; }

                layout.DeleteElement(element);
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, elementName });
        }

        private static async Task<IpcResponse> HandleCreateLayout(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layoutName", out string layoutName) || string.IsNullOrWhiteSpace(layoutName))
                return new IpcResponse(false, "arg 'layoutName' required", null);

            req.Args.TryGetValue("width", out string widthStr);
            req.Args.TryGetValue("height", out string heightStr);
            double.TryParse(widthStr, out double width);
            double.TryParse(heightStr, out double height);
            if (width <= 0) width = 297;
            if (height <= 0) height = 210;
            req.Args.TryGetValue("units", out string units);
            if (string.IsNullOrWhiteSpace(units)) units = "MILLIMETERS";

            await QueuedTask.Run(async () =>
            {
                try
                {
                    if (Project.Current == null) return;
                    var projPath = Project.Current.Path;
                    var pyCode = "import arcpy\n"
                        + $"proj = {System.Text.Json.JsonSerializer.Serialize(projPath)}\n"
                        + $"name = {System.Text.Json.JsonSerializer.Serialize(layoutName)}\n"
                        + $"w = {width}\nh = {height}\n"
                        + $"u = {System.Text.Json.JsonSerializer.Serialize(units)}\n"
                        + "try:\n"
                        + "    arcpy.management.CreateLayout(proj, name, w, h, u)\n"
                        + "    print('ok')\n"
                        + "except Exception as ex:\n"
                        + "    print(f'error: {ex}')\n";
                    await RunProPythonAsync(pyCode, 30, ct);
                }
                catch { }
            });

            return new IpcResponse(true, null, new { done = true, layoutName });
        }

        private static async Task<IpcResponse> HandleOpenDockpane(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("dockpaneId", out string damlId) ||
                string.IsNullOrWhiteSpace(damlId))
                return new IpcResponse(false, "arg 'dockpaneId' required", null);

            bool found = false;
            try
            {
                await QueuedTask.Run(() =>
                {
                    // 1. Try direct DAML ID
                    var pane = FrameworkApplication.DockPaneManager.Find(damlId);
                    if (pane != null) { pane.Activate(); found = true; return; }

                    // 2. Try friendly name → resolved DAML ID
                    if (_knownDockPanes.TryGetValue(damlId, out string resolved))
                    {
                        pane = FrameworkApplication.DockPaneManager.Find(resolved);
                        if (pane != null) { pane.Activate(); found = true; }
                    }
                });
            }
            catch { }

            var known = string.Join(", ", _knownDockPanes.Keys);
            return new IpcResponse(found,
                found ? null : $"Dockpane not found: '{damlId}'. Open the dockpane manually in Pro first, then it becomes findable by DAML ID or friendly name. Known names: {known}",
                new { done = found, dockpaneId = damlId });
        }
    }
}
