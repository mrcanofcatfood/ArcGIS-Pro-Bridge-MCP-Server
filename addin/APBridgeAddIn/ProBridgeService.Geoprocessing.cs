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
        private static async Task<IpcResponse> HandleRunGpTool(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("toolName", out string toolName) || string.IsNullOrWhiteSpace(toolName) ||
                !req.Args.TryGetValue("parameters", out string paramsJson) || string.IsNullOrWhiteSpace(paramsJson))
                return new IpcResponse(false, "args 'toolName' & 'parameters' (JSON array) required", null);

            string warning = null;
            object outputs = null;

            await QueuedTask.Run(async () =>
            {
                var values = JsonSerializer.Deserialize<List<JsonElement>>(paramsJson);
                if (values == null) { warning = "parameters must be a JSON array"; return; }

                var rawValues = new List<object?>();
                foreach (var v in values)
                {
                    if (v.ValueKind == JsonValueKind.String) rawValues.Add(v.GetString());
                    else if (v.ValueKind == JsonValueKind.Number) rawValues.Add(v.GetDouble());
                    else if (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) rawValues.Add(v.GetBoolean());
                    else rawValues.Add(v.GetRawText());
                }

                try
                {
                    var result = await Geoprocessing.ExecuteToolAsync(toolName, Geoprocessing.MakeValueArray(rawValues.ToArray()));
                    outputs = new[]
                    {
                        new
                        {
                            name = toolName,
                            data = result.ReturnValue?.ToString() ?? "",
                            isFailed = result.IsFailed,
                            messages = result.Messages?.Select(m => new { type = m.Type.ToString(), text = m.Text }).ToList()
                        }
                    };
                }
                catch (Exception ex)
                {
                    warning = $"GP tool execution failed: {SanitizeException(ex)}";
                }
            });

            return new IpcResponse(warning == null, warning, outputs);
        }

        private static async Task<IpcResponse> HandleListGpTools(IpcRequest req, CancellationToken ct)
        {
            string searchText = "";
            req.Args?.TryGetValue("searchText", out searchText);
            string maxStr = "50";
            req.Args?.TryGetValue("maxResults", out maxStr);
            if (!int.TryParse(maxStr, out int maxResults)) maxResults = 50;
            if (searchText == null) searchText = "";

            var tools = new List<object>();
            await QueuedTask.Run(() =>
            {
                try
                {
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                    var tbDirs = new[]
                    {
                        System.IO.Path.Combine(pf, @"ArcGIS\Pro\Resources\ArcToolbox\toolboxes"),
                    };
                    foreach (var dir in tbDirs)
                    {
                        if (!Directory.Exists(dir)) continue;
                        foreach (var tbFile in Directory.EnumerateFiles(dir, "*.tbx")
                            .Concat(Directory.EnumerateFiles(dir, "*.atbx")))
                        {
                            var tbName = System.IO.Path.GetFileNameWithoutExtension(tbFile);
                            // Try to read tool names from the XML .tbx structure
                            try
                            {
                                var doc = System.Xml.Linq.XDocument.Load(tbFile);
                                foreach (var toolEl in doc.Descendants("Tool"))
                                {
                                    var toolName = (string)toolEl.Attribute("name") ?? (string)toolEl.Attribute("displayname") ?? "";
                                    if (string.IsNullOrWhiteSpace(toolName)) continue;
                                    var full = tbName + "." + toolName;
                                    if (seen.Add(full) &&
                                        (string.IsNullOrWhiteSpace(searchText) ||
                                         full.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         toolName.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0))
                                    {
                                        tools.Add(new { toolbox = tbName, name = toolName, full });
                                    }
                                }
                            }
                            catch
                            {
                                // If XML parsing fails, just list the toolbox name
                                if (seen.Add(tbName))
                                    tools.Add(new { toolbox = tbName, name = "", full = tbName });
                            }
                        }
                    }
                }
                catch { }
            });

            var result = tools.Take(maxResults).ToList();
            return new IpcResponse(true, null, new { toolCount = result.Count, searchText, tools = result });
        }

        private static async Task<IpcResponse> HandleCopyFeatures(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) || string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("outputPath", out string outputPath) || string.IsNullOrWhiteSpace(outputPath))
                return new IpcResponse(false, "args 'layer' & 'outputPath' required", null);

            string warning = null;

            await QueuedTask.Run(async () =>
            {
                var map = MapView.Active?.Map;
                if (map == null) { warning = "Layer not found"; return; }
                var fl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) { warning = "Layer not found"; return; }

                using var fc = fl.GetFeatureClass();
                var workspacePath = fc.GetDatastore().GetPath().LocalPath;
                var fcName = fc.GetName();
                var fcPath = System.IO.Path.Combine(workspacePath, fcName);

                var result = await Geoprocessing.ExecuteToolAsync("CopyFeatures",
                    Geoprocessing.MakeValueArray(fcPath, outputPath));

                if (result != null && result.IsFailed)
                    warning = "CopyFeatures tool completed with warnings/errors";
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, outputPath });
        }

        private static async Task<IpcResponse> HandleListToolboxes(IpcRequest req, CancellationToken ct)
        {
            object result = null;
            await QueuedTask.Run(() =>
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var toolboxes = new List<object>();

                var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                var systemDirs = new[]
                {
                    System.IO.Path.Combine(pf, @"ArcGIS\Pro\Resources\ArcToolbox\toolboxes"),
                };
                foreach (var dir in systemDirs)
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var f in Directory.EnumerateFiles(dir, "*.tbx")
                        .Concat(Directory.EnumerateFiles(dir, "*.atbx")))
                    {
                        if (seen.Add(f))
                            toolboxes.Add(new { name = System.IO.Path.GetFileNameWithoutExtension(f), path = f, type = "System", toolCount = 0 });
                    }
                }

                result = new { toolboxCount = toolboxes.Count, toolboxes };
            });

            return new IpcResponse(true, null, result);
        }

        private static async Task<IpcResponse> HandleDescribeTool(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("toolName", out string toolName) || string.IsNullOrWhiteSpace(toolName))
                return new IpcResponse(false, "arg 'toolName' required", null);

            var pyResult = await RunProPythonAsync($@"
import arcpy, json
try:
    params = arcpy.GetParameterInfo('{toolName.Replace("'", "\\'")}')
    result = []
    for p in params:
        result.append({{
            'name': p.name, 'displayName': p.displayName,
            'datatype': p.datatype, 'direction': p.direction,
            'required': p.required, 'parameterType': p.parameterType,
            'category': p.category or '', 'defaultValue': str(p.defaultValue) if p.defaultValue is not None else ''
        }})
    print(json.dumps(result, default=str))
except Exception as e:
    print(f'{{{{""error"": ""{{e}}""}}}}')", 30, ct);

            if (!pyResult.Ok) return pyResult;
            return new IpcResponse(true, null, new { toolName, parameters = pyResult.Data });
        }

        private static Task<IpcResponse> HandleGetGeoprocessingHistory(IpcRequest req, CancellationToken ct)
        {
            var items = new List<object>();
            try
            {
                var historyPaths = new[]
                {
                    System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "ESRI", "ArcGISPro", "arcgispro-History.xml"
                    ),
                };
                if (Project.Current != null)
                {
                    var projDir = System.IO.Path.GetDirectoryName(Project.Current.Path);
                    if (projDir != null)
                        historyPaths = new[] { System.IO.Path.Combine(projDir, "GeoprocessingHistory.xml") };
                }

                foreach (var path in historyPaths)
                {
                    if (!System.IO.File.Exists(path)) continue;
                    var xml = System.Xml.Linq.XDocument.Load(path);
                    foreach (var entry in xml.Descendants("HistoryEntry")
                        .Take(200))
                    {
                        items.Add(new
                        {
                            tool = entry.Element("ToolName")?.Value ?? "",
                            start = entry.Element("StartTime")?.Value ?? "",
                            end = entry.Element("EndTime")?.Value ?? "",
                            status = entry.Element("Status")?.Value ?? "",
                            duration = entry.Element("Duration")?.Value ?? "",
                        });
                    }
                    break;
                }
            }
            catch { }

            return Task.FromResult(new IpcResponse(true, null, new { totalCount = items.Count, items }));
        }

        private static async Task<IpcResponse> HandleRunPythonScript(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("code", out string code) || string.IsNullOrWhiteSpace(code))
                return new IpcResponse(false, "arg 'code' required", null);

            req.Args.TryGetValue("timeoutSeconds", out string timeoutStr);
            int.TryParse(timeoutStr, out int timeout);
            if (timeout <= 0 || timeout > 300) timeout = 60;

            return await RunProPythonAsync(code, timeout, ct);
        }

        private static Task<IpcResponse> HandleSetEnvironment(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("key", out string key) || string.IsNullOrWhiteSpace(key) ||
                !req.Args.TryGetValue("value", out string valueStr))
                return Task.FromResult(new IpcResponse(false, "args 'key' & 'value' required", null));

            return Task.FromResult(new IpcResponse(false, "Set environment not supported from AddIn - use arcpy.env in runPythonScript", null));
        }

        private static Task<IpcResponse> HandleGetEnvironment(IpcRequest req, CancellationToken ct)
        {
            return Task.FromResult(new IpcResponse(false, "Get environment not supported from AddIn - use arcpy.env in runPythonScript", null));
        }

        private static Task<IpcResponse> HandleListGpHistory(IpcRequest req, CancellationToken ct)
        {
            var items = new List<object>();
            try
            {
                var historyPaths = new[]
                {
                    System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "ESRI", "ArcGISPro", "arcgispro-History.xml"
                    ),
                };
                if (Project.Current != null)
                {
                    var projDir = System.IO.Path.GetDirectoryName(Project.Current.Path);
                    if (projDir != null)
                        historyPaths = new[] { System.IO.Path.Combine(projDir, "GeoprocessingHistory.xml") };
                }

                foreach (var path in historyPaths)
                {
                    if (!System.IO.File.Exists(path)) continue;
                    var xml = System.Xml.Linq.XDocument.Load(path);
                    foreach (var entry in xml.Descendants("HistoryEntry")
                        .Take(100))
                    {
                        items.Add(new
                        {
                            tool = entry.Element("ToolName")?.Value ?? "",
                            start = entry.Element("StartTime")?.Value ?? "",
                            end = entry.Element("EndTime")?.Value ?? "",
                            status = entry.Element("Status")?.Value ?? "",
                        });
                    }
                    break;
                }
            }
            catch { }

            return Task.FromResult(new IpcResponse(true, null, new { totalCount = items.Count, items }));
        }
    }
}
