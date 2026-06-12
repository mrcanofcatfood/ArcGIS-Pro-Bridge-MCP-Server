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
        private static async Task<IpcResponse> HandleListFieldValues(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("field", out string field) ||
                string.IsNullOrWhiteSpace(field))
                return new IpcResponse(false, "args 'layer' & 'field' required", null);

            req.Args.TryGetValue("maxValues", out string maxValuesStr);
            int maxValues = string.IsNullOrWhiteSpace(maxValuesStr) ? 100 : int.Parse(maxValuesStr);

            var result = await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return null;
                var fl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) return null;

                using var fc = fl.GetFeatureClass();
                var qf = new QueryFilter { SubFields = field };
                var values = new HashSet<string>();
                using (var cursor = fc.Search(qf, true))
                {
                    while (cursor.MoveNext() && values.Count < maxValues)
                    {
                        using var row = cursor.Current;
                        var val = row[field];
                        values.Add(val?.ToString() ?? "<null>");
                    }
                }

                return new
                {
                    field,
                    distinctCount = values.Count,
                    values = values.OrderBy(v => v).ToList(),
                };
            });

            if (result == null)
                return new IpcResponse(false, $"Layer '{layerName}' not found", null);
            return new IpcResponse(true, null, result);
        }

        private static async Task<IpcResponse> HandleAddField(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("fieldName", out string fieldName) ||
                string.IsNullOrWhiteSpace(fieldName) ||
                !req.Args.TryGetValue("fieldType", out string fieldType) ||
                string.IsNullOrWhiteSpace(fieldType))
                return new IpcResponse(false, "args 'layer', 'fieldName', & 'fieldType' required", null);

            req.Args.TryGetValue("precision", out string precStr);
            req.Args.TryGetValue("scale", out string scaleStr);
            req.Args.TryGetValue("length", out string lenStr);

            string warning = null;

            await QueuedTask.Run(async () =>
            {
                var map = MapView.Active?.Map;
                if (map == null) { warning = "Layer not found"; return; }
                var fl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) { warning = "Layer not found"; return; }

                try
                {
                    using var fc = fl.GetFeatureClass();
                    var workspaceUri = fc.GetDatastore().GetPath();
                    var fcPath = System.IO.Path.Combine(workspaceUri.LocalPath, fc.GetName());

                    object[] gisParams = string.IsNullOrWhiteSpace(lenStr)
                        ? new object[] { fcPath, fieldName, fieldType }
                        : new object[] { fcPath, fieldName, fieldType, "#", "#", lenStr };

                    var result = await Geoprocessing.ExecuteToolAsync("AddField", Geoprocessing.MakeValueArray(gisParams));
                    if (result != null && result.IsFailed)
                        warning = "AddField failed";
                }
                catch (Exception ex) { warning = $"AddField error: {SanitizeException(ex)}"; }
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, fieldName });
        }

        private static async Task<IpcResponse> HandleDeleteField(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("fieldName", out string fieldName) ||
                string.IsNullOrWhiteSpace(fieldName))
                return new IpcResponse(false, "args 'layer' & 'fieldName' required", null);

            string warning = null;

            await QueuedTask.Run(async () =>
            {
                var map = MapView.Active?.Map;
                if (map == null) { warning = "Layer not found"; return; }
                var fl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) { warning = "Layer not found"; return; }

                try
                {
                    using var fc = fl.GetFeatureClass();
                    var workspaceUri = fc.GetDatastore().GetPath();
                    var fcPath = System.IO.Path.Combine(workspaceUri.LocalPath, fc.GetName());

                    var result = await Geoprocessing.ExecuteToolAsync("DeleteField",
                        Geoprocessing.MakeValueArray(fcPath, fieldName));
                    if (result != null && result.IsFailed)
                        warning = "DeleteField failed";
                }
                catch (Exception ex) { warning = $"DeleteField error: {SanitizeException(ex)}"; }
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, fieldName });
        }

        private static async Task<IpcResponse> HandleCalculateField(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("field", out string fieldName) ||
                string.IsNullOrWhiteSpace(fieldName) ||
                !req.Args.TryGetValue("expression", out string expression) ||
                string.IsNullOrWhiteSpace(expression))
                return new IpcResponse(false, "args 'layer', 'field', & 'expression' required", null);

            req.Args.TryGetValue("expressionType", out string expressionType);
            req.Args.TryGetValue("codeBlock", out string codeBlock);

            string warning = null;

            await QueuedTask.Run(async () =>
            {
                var map = MapView.Active?.Map;
                if (map == null) { warning = "Layer not found"; return; }
                var fl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) { warning = "Layer not found"; return; }

                try
                {
                    using var fc = fl.GetFeatureClass();
                    var workspaceUri = fc.GetDatastore().GetPath();
                    var fcPath = System.IO.Path.Combine(workspaceUri.LocalPath, fc.GetName());
                    var type = string.IsNullOrWhiteSpace(expressionType) ? "PYTHON3" : expressionType;

                    var gisParams = string.IsNullOrWhiteSpace(codeBlock)
                        ? new object[] { fcPath, fieldName, expression, type }
                        : new object[] { fcPath, fieldName, expression, type, codeBlock };

                    var result = await Geoprocessing.ExecuteToolAsync(
                        "CalculateField", Geoprocessing.MakeValueArray(gisParams));
                    if (result != null && result.IsFailed)
                        warning = "CalculateField failed";
                }
                catch (Exception ex) { warning = $"CalculateField error: {SanitizeException(ex)}"; }
            });

            return new IpcResponse(warning == null, warning ?? "ok",
                new { done = warning == null, field = fieldName, expression });
        }

        private static async Task<IpcResponse> HandleRenameField(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("oldName", out string oldName) ||
                string.IsNullOrWhiteSpace(oldName) ||
                !req.Args.TryGetValue("newName", out string newName) ||
                string.IsNullOrWhiteSpace(newName))
                return new IpcResponse(false, "args 'layer', 'oldName', & 'newName' required", null);

            bool executed = false;
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
                var tableDef = fc.GetDefinition() as TableDefinition;
                if (tableDef == null) { warning = "Cannot get table definition"; return; }

                var existing = tableDef.GetFields().FirstOrDefault(f =>
                    f.Name.Equals(oldName, StringComparison.OrdinalIgnoreCase));
                if (existing == null) { warning = $"Field '{oldName}' not found"; return; }
                if (!existing.IsEditable) { warning = $"Field '{oldName}' is not editable"; return; }

                var fcPath = $"{fc.GetDatastore().GetPath()}/{fc.GetName()}";
                Geoprocessing.ExecuteToolAsync("AlterField", Geoprocessing.MakeValueArray(fcPath, oldName, newName));
                executed = true;
            });

            return new IpcResponse(true, null, new { done = executed, oldName, newName, warning });
        }

        private static async Task<IpcResponse> HandleCreateFeatureClass(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("gdbPath", out string gdbPath) || string.IsNullOrWhiteSpace(gdbPath) ||
                !req.Args.TryGetValue("name", out string name) || string.IsNullOrWhiteSpace(name) ||
                !req.Args.TryGetValue("geometryType", out string geometryType) || string.IsNullOrWhiteSpace(geometryType))
                return new IpcResponse(false, "args 'gdbPath', 'name', & 'geometryType' required", null);

            // Fall back to project default geodatabase if specified GDB does not exist
            if (!System.IO.Directory.Exists(gdbPath) && Project.Current != null)
                gdbPath = Project.Current.DefaultGeodatabasePath;

            req.Args.TryGetValue("wkid", out string wkidStr);
            int.TryParse(wkidStr, out int wkid);
            req.Args.TryGetValue("fieldsJson", out string fieldsJson);

            string warning = null;

            await QueuedTask.Run(async () =>
            {
                try
                {
                    var rawParams = new List<object?> { gdbPath, name, geometryType };
                    if (wkid > 0)
                        rawParams.Add(wkid);

                    var result = await Geoprocessing.ExecuteToolAsync("CreateFeatureclass", Geoprocessing.MakeValueArray(rawParams.ToArray()));
                    if (result != null && result.IsFailed)
                    { warning = "CreateFeatureclass failed"; return; }

                    if (!string.IsNullOrWhiteSpace(fieldsJson))
                    {
                        var fcPath = System.IO.Path.Combine(gdbPath, name);
                        var fields = JsonSerializer.Deserialize<List<JsonElement>>(fieldsJson);
                        foreach (var f in fields)
                        {
                            string fn = f.TryGetProperty("fieldName", out var jfn) ? jfn.GetString() : "";
                            string ft = f.TryGetProperty("fieldType", out var jft) ? jft.GetString() : "TEXT";
                            if (string.IsNullOrWhiteSpace(fn)) continue;
                            var afRawParams = new List<object?> { fcPath, fn, ft };

                            if (f.TryGetProperty("fieldLength", out var fl) && fl.ValueKind == JsonValueKind.Number)
                                afRawParams.Add(fl.GetDouble());

                            var afResult = await Geoprocessing.ExecuteToolAsync("AddField", Geoprocessing.MakeValueArray(afRawParams.ToArray()));
                            if (afResult != null && afResult.IsFailed)
                                warning = $"Failed to add field '{fn}'";
                        }
                    }
                }
                catch (Exception ex) { warning = $"CreateFeatureClass failed: {SanitizeException(ex)}"; }
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, name, geometryType, gdbPath });
        }

        private static async Task<IpcResponse> HandleDeleteFeatureClass(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("path", out string path) || string.IsNullOrWhiteSpace(path))
                return new IpcResponse(false, "arg 'path' required", null);

            string warning = null;

            await QueuedTask.Run(async () =>
            {
                try
                {
                    var result = await Geoprocessing.ExecuteToolAsync("Delete", Geoprocessing.MakeValueArray(path));
                    if (result != null && result.IsFailed)
                        warning = "Delete operation failed";
                }
                catch (Exception ex) { warning = $"Delete failed: {SanitizeException(ex)}"; }
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, path });
        }

        private static async Task<IpcResponse> HandleAddAttributeIndex(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) || string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("field", out string field) || string.IsNullOrWhiteSpace(field))
                return new IpcResponse(false, "args 'layer' & 'field' required", null);

            req.Args.TryGetValue("indexName", out string indexName);
            if (string.IsNullOrWhiteSpace(indexName)) indexName = $"idx_{field}";
            req.Args.TryGetValue("unique", out string uniqueStr);
            bool unique = uniqueStr?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;

            string warning = null;

            await QueuedTask.Run(async () =>
            {
                var map = MapView.Active?.Map;
                if (map == null) { warning = "Layer not found"; return; }
                var fl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) { warning = "Layer not found"; return; }

                try
                {
                    using var fc = fl.GetFeatureClass();
                    var workspaceUri = fc.GetDatastore().GetPath();
                    var fcPath = System.IO.Path.Combine(workspaceUri.LocalPath, fc.GetName());

                    var gpParams = new List<object?> { fcPath, field, indexName };
                    if (unique) gpParams.Add("#"); // skip "ascending" param
                    if (unique) gpParams.Add("UNIQUE");
                    else gpParams.Add("NON_UNIQUE");

                    var result = await Geoprocessing.ExecuteToolAsync("AddIndex",
                        Geoprocessing.MakeValueArray(gpParams.ToArray()));
                    if (result != null && result.IsFailed)
                        warning = "AddIndex failed";
                }
                catch (Exception ex) { warning = $"AddIndex error: {SanitizeException(ex)}"; }
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, field, indexName, unique });
        }

        private static async Task<IpcResponse> HandleListDomains(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("gdbPath", out string gdbPath) || string.IsNullOrWhiteSpace(gdbPath))
                return new IpcResponse(false, "arg 'gdbPath' required", null);

            object result = null;
            await QueuedTask.Run(() =>
            {
                try
                {
                    var gdb = new ArcGIS.Core.Data.Geodatabase(
                        new ArcGIS.Core.Data.FileGeodatabaseConnectionPath(new Uri(gdbPath)));
                    var domains = gdb.GetDomains();
                    var list = new List<object>();
                    foreach (var d in domains)
                    {
                        list.Add(new
                        {
                            name = d.GetName(),
                            type = d.GetType().Name,
                            fieldType = d.GetFieldType().ToString(),
                        });
                    }
                    result = new { domainCount = list.Count, domains = list };
                }
                catch { }
            });

            return new IpcResponse(true, null, result ?? new { domainCount = 0, domains = new List<object>() });
        }

        private static async Task<IpcResponse> HandleCreateDomain(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("gdbPath", out string gdbPath) || string.IsNullOrWhiteSpace(gdbPath) ||
                !req.Args.TryGetValue("name", out string name) || string.IsNullOrWhiteSpace(name) ||
                !req.Args.TryGetValue("fieldType", out string fieldType) || string.IsNullOrWhiteSpace(fieldType))
                return new IpcResponse(false, "args 'gdbPath', 'name', & 'fieldType' required", null);

            req.Args.TryGetValue("description", out string description);
            req.Args.TryGetValue("codedValues", out string codedValues);
            if (string.IsNullOrWhiteSpace(description)) description = name;

            string warning = null;

            await QueuedTask.Run(async () =>
            {
                try
                {
                    var gpParams = new List<object?> { gdbPath, name, description, fieldType };
                    if (!string.IsNullOrWhiteSpace(codedValues))
                    {
                        // codedValues comes as JSON dict like {"R":"Residential","C":"Commercial"}
                        // GP CreateDomain expects format: "R Residential;C Commercial"
                        try
                        {
                            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(codedValues);
                            if (dict != null)
                            {
                                var parts = dict.Select(kvp => $"{kvp.Key} {kvp.Value}");
                                gpParams.Add(string.Join(";", parts));
                            }
                        }
                        catch { gpParams.Add(codedValues); }
                    }

                    var result = await Geoprocessing.ExecuteToolAsync("CreateDomain",
                        Geoprocessing.MakeValueArray(gpParams.ToArray()));
                    if (result != null && result.IsFailed)
                        warning = "CreateDomain failed";
                }
                catch (Exception ex) { warning = $"CreateDomain error: {SanitizeException(ex)}"; }
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, name, domain = name });
        }

        private static async Task<IpcResponse> HandleAssignDomainToField(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) || string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("field", out string fieldName) || string.IsNullOrWhiteSpace(fieldName) ||
                !req.Args.TryGetValue("domainName", out string domainName) || string.IsNullOrWhiteSpace(domainName))
                return new IpcResponse(false, "args 'layer', 'field', & 'domainName' required", null);

            string warning = null;

            await QueuedTask.Run(async () =>
            {
                var map = MapView.Active?.Map;
                if (map == null) { warning = "Layer not found"; return; }
                var fl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) { warning = "Layer not found"; return; }

                try
                {
                    using var fc = fl.GetFeatureClass();
                    var workspaceUri = fc.GetDatastore().GetPath();
                    var fcPath = System.IO.Path.Combine(workspaceUri.LocalPath, fc.GetName());

                    var result = await Geoprocessing.ExecuteToolAsync("AssignDomainToField",
                        Geoprocessing.MakeValueArray(fcPath, fieldName, domainName));
                    if (result != null && result.IsFailed)
                        warning = "AssignDomainToField failed";
                }
                catch (Exception ex) { warning = $"Failed to assign domain: {SanitizeException(ex)}"; }
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, fieldName, domainName });
        }

        private static async Task<IpcResponse> HandleListSubtypes(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) || string.IsNullOrWhiteSpace(layerName))
                return new IpcResponse(false, "arg 'layer' required", null);

            string warning = null;
            object result = null;

            await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) { warning = "Layer not found"; return; }
                var fl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) { warning = "Layer not found"; return; }

                try
                {
                    var fcDef = fl.GetFeatureClass().GetDefinition();
                    string subtypeField = fcDef.GetSubtypeField();
                    var subtypes = new List<object>();
                    try
                    {
                        var subObj = fcDef.GetSubtypes();
                        var dict = subObj as System.Collections.IDictionary;
                        if (dict != null)
                        {
                            foreach (System.Collections.DictionaryEntry entry in dict)
                                subtypes.Add(new { code = Convert.ToInt32(entry.Key), name = Convert.ToString(entry.Value) ?? "" });
                        }
                    }
                    catch { }
                    result = new { subtypeField = subtypeField ?? "", subtypeCount = subtypes.Count, subtypes };
                }
                catch (Exception ex) { warning = $"Failed to list subtypes: {SanitizeException(ex)}"; }
            });

            if (warning != null) return new IpcResponse(false, warning, null);
            return new IpcResponse(true, null, result);
        }

        private static async Task<IpcResponse> HandleSetSubtypeField(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) || string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("field", out string fieldName) || string.IsNullOrWhiteSpace(fieldName))
                return new IpcResponse(false, "args 'layer' & 'field' required", null);

            string warning = null;

            await QueuedTask.Run(async () =>
            {
                var map = MapView.Active?.Map;
                if (map == null) { warning = "Layer not found"; return; }
                var fl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) { warning = "Layer not found"; return; }

                try
                {
                    using var fc = fl.GetFeatureClass();
                    var workspaceUri = fc.GetDatastore().GetPath();
                    var fcPath = System.IO.Path.Combine(workspaceUri.LocalPath, fc.GetName());

                    var result = await Geoprocessing.ExecuteToolAsync("SetSubtypeField",
                        Geoprocessing.MakeValueArray(fcPath, fieldName));
                    if (result != null && result.IsFailed)
                        warning = "SetSubtypeField failed";
                }
                catch (Exception ex) { warning = $"Failed to set subtype field: {SanitizeException(ex)}"; }
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, fieldName });
        }

        private static async Task<IpcResponse> HandleEnableAttachments(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) || string.IsNullOrWhiteSpace(layerName))
                return new IpcResponse(false, "arg 'layer' required", null);

            string warning = null;

            await QueuedTask.Run(async () =>
            {
                var map = MapView.Active?.Map;
                if (map == null) { warning = "Layer not found"; return; }
                var fl = map.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) { warning = "Layer not found"; return; }

                try
                {
                    using var fc = fl.GetFeatureClass();
                    var workspaceUri = fc.GetDatastore().GetPath();
                    var fcPath = System.IO.Path.Combine(workspaceUri.LocalPath, fc.GetName());

                    var result = await Geoprocessing.ExecuteToolAsync("EnableAttachments",
                        Geoprocessing.MakeValueArray(fcPath));
                    if (result != null && result.IsFailed)
                        warning = "EnableAttachments failed";
                }
                catch (Exception ex) { warning = $"Failed to enable attachments: {SanitizeException(ex)}"; }
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, layerName });
        }
    }
}
