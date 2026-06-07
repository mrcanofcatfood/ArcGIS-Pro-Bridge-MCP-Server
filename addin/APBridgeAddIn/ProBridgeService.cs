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
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace APBridgeAddIn
{
    internal class ProBridgeService : IDisposable
    {
        private readonly string _pipeName;
        private CancellationTokenSource _cts;
        private Task _serverLoop;

        private static readonly Dictionary<string, string> _knownDockPanes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Contents"] = "esri_mapping_contentsPane",
            ["Catalog"] = "esri_mapping_catalogPane",
            ["Attribute Table"] = "esri_mapping_tableWindow",
            ["Table"] = "esri_mapping_tableWindow",
            ["Search"] = "esri_core_searchDockPane",
            ["Geoprocessing"] = "esri_mapping_geoprocessingPane",
            ["Symbology"] = "esri_mapping_symbologyPane",
            ["Labeling"] = "esri_mapping_labelingPane",
            ["Bookmarks"] = "esri_mapping_bookmarksPane",
            ["Time"] = "esri_mapping_timeDockPane",
        };

        private static readonly Dictionary<string, string> _knownRibbonTabs = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Map"] = "esri_mapping_mapTab",
            ["Edit"] = "esri_mapping_editTab",
            ["Catalog"] = "esri_core_catalogTab",
            ["Insert"] = "esri_mapping_insertTab",
            ["Analysis"] = "esri_mapping_analysisTab",
            ["View"] = "esri_mapping_viewTab",
            ["Appearance"] = "esri_mapping_appearanceTab",
        };

        public ProBridgeService(string pipeName) => _pipeName = pipeName;

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _serverLoop = Task.Run(() => RunAsync(_cts.Token));
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); _serverLoop?.Wait(2000); }
            catch { }
        }

        private async Task RunAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Message,
                    PipeOptions.Asynchronous
                );
                await server.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true)
                { AutoFlush = true };

                while (server.IsConnected && !ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync();
                    if (line == null) break;

                    IpcRequest req;
                    try
                    {
                        req = JsonSerializer.Deserialize<IpcRequest>(line);
                    }
                    catch (Exception ex)
                    {
                        await SendAsync(writer, new IpcResponse(false, $"parse:{ex.Message}", null));
                        continue;
                    }

                    try
                    {
                        var resp = await HandleAsync(req, ct);
                        await SendAsync(writer, resp);
                    }
                    catch (Exception ex)
                    {
                        await SendAsync(writer, new IpcResponse(false, ex.Message, null));
                    }
                }
            }
        }

        private static Task SendAsync(StreamWriter w, IpcResponse resp)
            => w.WriteLineAsync(JsonSerializer.Serialize(resp));

        private static object JsonElementToObject(JsonElement el)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.String: return el.GetString();
                case JsonValueKind.Number:
                    if (el.TryGetInt64(out long l)) return l;
                    if (el.TryGetDouble(out double d)) return d;
                    return el.GetRawText();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                case JsonValueKind.Null: return null;
                default: return el.GetRawText();
            }
        }

        private static readonly Dictionary<string, Func<IpcRequest, CancellationToken, Task<IpcResponse>>> _handlers = new()
        {
            ["pro.ping"] = HandlePing,
            ["pro.getActiveMapName"] = HandleGetActiveMapName,
            ["pro.listLayers"] = HandleListLayers,
            ["pro.countFeatures"] = HandleCountFeatures,
            ["pro.getLayerSchema"] = HandleGetLayerSchema,
            ["pro.getSelectionCount"] = HandleGetSelectionCount,
            ["pro.selectByAttribute"] = HandleSelectByAttribute,
            ["pro.clearSelection"] = HandleClearSelection,
            ["pro.zoomToLayer"] = HandleZoomToLayer,
            ["pro.getCurrentExtent"] = HandleGetCurrentExtent,
            ["pro.panToExtent"] = HandlePanToExtent,
            ["pro.getCamera"] = HandleGetCamera,
            ["pro.setLayerVisibility"] = HandleSetLayerVisibility,
            ["pro.getLayerExtent"] = HandleGetLayerExtent,
            ["pro.selectByRectangle"] = HandleSelectByRectangle,
            ["pro.switchSelection"] = HandleSwitchSelection,
            ["pro.getFeatureByOid"] = HandleGetFeatureByOid,
            ["pro.undoEdit"] = HandleUndoEdit,
            ["pro.redoEdit"] = HandleRedoEdit,
            ["pro.setActiveTool"] = HandleSetActiveTool,
            ["pro.is3d"] = HandleIs3d,
            ["pro.getLayerRenderer"] = HandleGetLayerRenderer,
            ["pro.setLayerColor"] = HandleSetLayerColor,
            ["pro.removeLayer"] = HandleRemoveLayer,
            ["pro.addLayerFromFile"] = HandleAddLayerFromFile,
            ["pro.selectByPolygon"] = HandleSelectByPolygon,
            ["pro.listLayouts"] = HandleListLayouts,
            ["pro.getProjectProperties"] = HandleGetProjectProperties,
            ["pro.getGeometryDistance"] = HandleGetGeometryDistance,
            ["pro.setLayerTransparency"] = HandleSetLayerTransparency,
            ["pro.getAllMapNames"] = HandleGetAllMapNames,
            ["pro.getMapFrame"] = HandleGetMapFrame,
            ["pro.selectByLayer"] = HandleSelectByLayer,
            ["pro.getFeaturesByExtent"] = HandleGetFeaturesByExtent,
            ["pro.deleteFeaturesByOid"] = HandleDeleteFeaturesByOid,
            ["pro.updateFeatureAttributes"] = HandleUpdateFeatureAttributes,
            ["pro.createPointFeature"] = HandleCreatePointFeature,
            ["pro.listBookmarks"] = HandleListBookmarks,
            ["pro.zoomToBookmark"] = HandleZoomToBookmark,
            ["pro.createBookmark"] = HandleCreateBookmark,
            ["pro.reorderLayer"] = HandleReorderLayer,
            ["pro.setLabelsEnabled"] = HandleSetLabelsEnabled,
            ["pro.openDockpane"] = HandleOpenDockpane,
            ["pro.exportLayoutToFile"] = HandleExportLayoutToFile,
            ["pro.flyToLocation"] = HandleFlyToLocation,
            ["pro.applyUniqueValueRenderer"] = HandleApplyUniqueValueRenderer,
            ["pro.applyClassBreaksRenderer"] = HandleApplyClassBreaksRenderer,
            ["pro.getElevationSources"] = HandleGetElevationSources,
            ["pro.setGroundOpacity"] = HandleSetGroundOpacity,
            ["pro.getActiveTool"] = HandleGetActiveTool,
            ["pro.listFieldValues"] = HandleListFieldValues,
            ["pro.addField"] = HandleAddField,
            ["pro.deleteField"] = HandleDeleteField,
            ["pro.createPolygonFeature"] = HandleCreatePolygonFeature,
            ["pro.createLineFeature"] = HandleCreateLineFeature,
            ["pro.setMapScale"] = HandleSetMapScale,
            ["pro.getMapScale"] = HandleGetMapScale,
            ["pro.zoomToSelected"] = HandleZoomToSelected,
            ["pro.getEditState"] = HandleGetEditState,
            ["pro.setSnapping"] = HandleSetSnapping,
            ["pro.deleteBookmark"] = HandleDeleteBookmark,
            ["pro.flashSelection"] = HandleFlashSelection,
            ["pro.selectAll"] = HandleSelectAll,
            ["pro.setStatusBarMessage"] = HandleSetStatusBarMessage,
            ["pro.listStandaloneTables"] = HandleListStandaloneTables,
            ["pro.listGpHistory"] = HandleListGpHistory,
            ["pro.isTimeEnabled"] = HandleIsTimeEnabled,
            ["pro.getTimeExtent"] = HandleGetTimeExtent,
            ["pro.setTimeExtent"] = HandleSetTimeExtent,
            ["pro.listLayoutElements"] = HandleListLayoutElements,
            ["pro.renameField"] = HandleRenameField,
            ["pro.getLayerDescription"] = HandleGetLayerDescription,
            ["pro.setLayerDescription"] = HandleSetLayerDescription,
            ["pro.listSceneLayerTypes"] = HandleListSceneLayerTypes,
            ["pro.countFeaturesByExpression"] = HandleCountFeaturesByExpression,
            ["pro.splitFeatures"] = HandleSplitFeatures,
            ["pro.mergeFeatures"] = HandleMergeFeatures,
            ["pro.runGpTool"] = HandleRunGpTool,
            ["pro.listGpTools"] = HandleListGpTools,
            ["pro.copyFeatures"] = HandleCopyFeatures,
            ["pro.renameLayer"] = HandleRenameLayer,
            ["pro.getLayerStatistics"] = HandleGetLayerStatistics,
            ["pro.projectGeometry"] = HandleProjectGeometry,
            ["pro.addLayoutText"] = HandleAddLayoutText,
            ["pro.addLayoutPicture"] = HandleAddLayoutPicture,
            ["pro.addLayoutLegend"] = HandleAddLayoutLegend,
            ["pro.addLayoutNorthArrow"] = HandleAddLayoutNorthArrow,
            ["pro.removeLayoutElement"] = HandleRemoveLayoutElement,
            ["pro.createLayout"] = HandleCreateLayout,
            ["pro.createMap"] = HandleCreateMap,
            ["pro.addBasemap"] = HandleAddBasemap,
            ["pro.setAtmosphere"] = HandleSetAtmosphere,
            ["pro.setSunPosition"] = HandleSetSunPosition,
            ["pro.getSunPosition"] = HandleGetSunPosition,
            ["pro.explore3D"] = HandleExplore3D,
            ["pro.setLayerElevation"] = HandleSetLayerElevation,
            ["pro.setSceneBackground"] = HandleSetSceneBackground,
            ["pro.createFeatureClass"] = HandleCreateFeatureClass,
            ["pro.deleteFeatureClass"] = HandleDeleteFeatureClass,
            ["pro.saveProject"] = HandleSaveProject,
            ["pro.addAttributeIndex"] = HandleAddAttributeIndex,
            ["pro.searchAddress"] = HandleSearchAddress,
            ["pro.openAttributeTable"] = HandleOpenAttributeTable,
            ["pro.exportToCsv"] = HandleExportToCsv,
            ["pro.exportToGeoJSON"] = HandleExportToGeoJSON,
            ["pro.importCsv"] = HandleImportCsv,
            ["pro.exportToShapefile"] = HandleExportToShapefile,
            ["pro.exportToKml"] = HandleExportToKml,
            ["pro.importGeoJSON"] = HandleImportGeoJSON,
            ["pro.showMessage"] = HandleShowMessage,
            ["pro.showProgressDialog"] = HandleShowProgressDialog,
            ["pro.setStatusBarProgress"] = HandleSetStatusBarProgress,
            ["pro.listDockpanes"] = HandleListDockpanes,
            ["pro.activateRibbonTab"] = HandleActivateRibbonTab,
            ["pro.listDomains"] = HandleListDomains,
            ["pro.createDomain"] = HandleCreateDomain,
            ["pro.assignDomainToField"] = HandleAssignDomainToField,
            ["pro.listSubtypes"] = HandleListSubtypes,
            ["pro.setSubtypeField"] = HandleSetSubtypeField,
            ["pro.enableAttachments"] = HandleEnableAttachments,
            ["pro.listToolboxes"] = HandleListToolboxes,
            ["pro.describeTool"] = HandleDescribeTool,
            ["pro.getGeoprocessingHistory"] = HandleGetGeoprocessingHistory,
            ["pro.runPythonScript"] = HandleRunPythonScript,
            ["pro.setEnvironment"] = HandleSetEnvironment,
            ["pro.getEnvironment"] = HandleGetEnvironment,
        };

        private static async Task<IpcResponse> HandleAsync(IpcRequest req, CancellationToken ct)
        {
            if (_handlers.TryGetValue(req.Op, out var handler))
                return await handler(req, ct);
            return new IpcResponse(false, $"op not found: {req.Op}", null);
        }

        private static Task<IpcResponse> HandlePing(IpcRequest req, CancellationToken ct)
            => Task.FromResult(new IpcResponse(true, null, new { pong = "addin", timestamp = DateTime.UtcNow.ToString("O") }));

        private static Task<IpcResponse> HandleGetActiveMapName(IpcRequest req, CancellationToken ct)
        {
            var name = MapView.Active?.Map?.Name ?? "<none>";
            return Task.FromResult(new IpcResponse(true, null, new { name }));
        }

        private static async Task<IpcResponse> HandleListLayers(IpcRequest req, CancellationToken ct)
        {
            var layers = await QueuedTask.Run(() =>
                MapView.Active?.Map?.Layers
                    .Select(l => new { l.Name, l.IsVisible, Type = l.GetType().Name })
                    .ToList() ?? new List<object>()
            );
            return new IpcResponse(true, null, layers);
        }

        private static async Task<IpcResponse> HandleCountFeatures(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName))
                return new IpcResponse(false, "arg 'layer' required", null);

            int count = await QueuedTask.Run(() =>
            {
                var fl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) return 0;
                using var fc = fl.GetFeatureClass();
                return (int)fc.GetCount();
            });

            return new IpcResponse(true, null, new { count });
        }

        private static async Task<IpcResponse> HandleGetLayerSchema(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName))
                return new IpcResponse(false, "arg 'layer' required", null);

            var schema = await QueuedTask.Run(() =>
            {
                var fl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) return null;

                var fc = fl.GetFeatureClass();
                var def = fc.GetDefinition();
                var fields = def.GetFields();
                var fieldInfos = new List<object>();
                foreach (var f in fields)
                {
                    fieldInfos.Add(new
                    {
                        f.Name,
                        f.AliasName,
                        f.FieldType,
                        Length = f.Length,
                        Precision = f.Precision,
                        Scale = f.Scale,
                        IsNullable = f.IsNullable,
                        IsEditable = f.IsEditable
                    });
                }
                return new
                {
                    layerName,
                    shapeType = fl.ShapeType.ToString(),
                    fieldCount = fieldInfos.Count,
                    fields = fieldInfos
                };
            });

            return new IpcResponse(true, null, schema);
        }

        private static async Task<IpcResponse> HandleGetSelectionCount(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName))
                return new IpcResponse(false, "arg 'layer' required", null);

            int selCount = await QueuedTask.Run(() =>
            {
                var fl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                return fl?.GetSelectionCount() ?? 0;
            });

            return new IpcResponse(true, null, new { count = selCount });
        }

        private static async Task<IpcResponse> HandleSelectByAttribute(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("where", out string where) ||
                string.IsNullOrWhiteSpace(where))
                return new IpcResponse(false, "args 'layer' & 'where' required", null);

            await QueuedTask.Run(() =>
            {
                var fl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl != null)
                {
                    fl.Select(new QueryFilter { WhereClause = where });
                }
            });

            return new IpcResponse(true, null, new { done = true });
        }

        private static async Task<IpcResponse> HandleClearSelection(IpcRequest req, CancellationToken ct)
        {
            string layerName = null;
            req.Args?.TryGetValue("layer", out layerName);

            await QueuedTask.Run(() =>
            {
                if (!string.IsNullOrWhiteSpace(layerName))
                {
                    var fl = MapView.Active?.Map?.Layers
                        .OfType<FeatureLayer>()
                        .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                    fl?.ClearSelection();
                }
                else
                {
                    foreach (var fl in MapView.Active?.Map?.Layers.OfType<FeatureLayer>() ?? Enumerable.Empty<FeatureLayer>())
                        fl.ClearSelection();
                }
            });

            return new IpcResponse(true, null, new { done = true });
        }

        private static async Task<IpcResponse> HandleZoomToLayer(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName))
                return new IpcResponse(false, "arg 'layer' required", null);

            await QueuedTask.Run(async () =>
            {
                var fl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl != null)
                    await MapView.Active.ZoomToAsync(fl);
            });

            return new IpcResponse(true, null, new { done = true });
        }

        private static async Task<IpcResponse> HandleGetCurrentExtent(IpcRequest req, CancellationToken ct)
        {
            var extent = await QueuedTask.Run(() =>
            {
                var cam = MapView.Active?.Camera;
                if (cam == null) return null;
                var env = cam.Extent;
                return new
                {
                    xmin = env.XMin,
                    ymin = env.YMin,
                    xmax = env.XMax,
                    ymax = env.YMax,
                    spatialReference = env.SpatialReference?.Name ?? "Unknown",
                    wkid = env.SpatialReference?.WKID ?? 0
                };
            });

            return new IpcResponse(true, null, extent);
        }

        private static async Task<IpcResponse> HandlePanToExtent(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("xmin", out string xminStr) ||
                !req.Args.TryGetValue("ymin", out string yminStr) ||
                !req.Args.TryGetValue("xmax", out string xmaxStr) ||
                !req.Args.TryGetValue("ymax", out string ymaxStr))
                return new IpcResponse(false, "args 'xmin', 'ymin', 'xmax', 'ymax' required", null);

            double xmin = double.Parse(xminStr);
            double ymin = double.Parse(yminStr);
            double xmax = double.Parse(xmaxStr);
            double ymax = double.Parse(ymaxStr);

            await QueuedTask.Run(async () =>
            {
                var sr = MapView.Active?.Camera?.SpatialReference;
                var envelope = EnvelopeBuilder.CreateEnvelope(xmin, ymin, xmax, ymax, sr);
                await MapView.Active.ZoomToAsync(envelope);
            });

            return new IpcResponse(true, null, new { done = true });
        }

        private static async Task<IpcResponse> HandleGetCamera(IpcRequest req, CancellationToken ct)
        {
            var cam = await QueuedTask.Run(() => MapView.Active?.Camera);
            if (cam == null)
                return new IpcResponse(false, "No active map view", null);
            return new IpcResponse(true, null, new
            {
                x = cam.X,
                y = cam.Y,
                z = cam.Z,
                heading = cam.Heading,
                pitch = cam.Pitch,
                roll = cam.Roll,
                spatialReference = cam.SpatialReference?.Name ?? "Unknown",
                wkid = cam.SpatialReference?.WKID ?? 0
            });
        }

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

        private static async Task<IpcResponse> HandleGetLayerExtent(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName))
                return new IpcResponse(false, "arg 'layer' required", null);

            var extent = await QueuedTask.Run(() =>
            {
                var fl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) return null;
                using var fc = fl.GetFeatureClass();
                var env = fc.GetExtent();
                return new
                {
                    xmin = env.XMin,
                    ymin = env.YMin,
                    xmax = env.XMax,
                    ymax = env.YMax,
                    spatialReference = env.SpatialReference?.Name ?? "Unknown",
                    wkid = env.SpatialReference?.WKID ?? 0
                };
            });
            if (extent == null)
                return new IpcResponse(false, $"Layer '{layerName}' not found", null);
            return new IpcResponse(true, null, extent);
        }

        private static async Task<IpcResponse> HandleSelectByRectangle(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("xmin", out string xminStr) ||
                !req.Args.TryGetValue("ymin", out string yminStr) ||
                !req.Args.TryGetValue("xmax", out string xmaxStr) ||
                !req.Args.TryGetValue("ymax", out string ymaxStr))
                return new IpcResponse(false, "args 'layer', 'xmin', 'ymin', 'xmax', 'ymax' required", null);

            double xmin = double.Parse(xminStr);
            double ymin = double.Parse(yminStr);
            double xmax = double.Parse(xmaxStr);
            double ymax = double.Parse(ymaxStr);
            req.Args.TryGetValue("selectionType", out string selectionTypeStr);
            var selType = string.IsNullOrWhiteSpace(selectionTypeStr)
                ? SelectionType.New
                : (SelectionType)Enum.Parse(typeof(SelectionType), selectionTypeStr, ignoreCase: true);

            await QueuedTask.Run(() =>
            {
                var fl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl != null)
                {
                    var sr = MapView.Active.Camera.SpatialReference;
                    var envelope = EnvelopeBuilder.CreateEnvelope(xmin, ymin, xmax, ymax, sr);
                    fl.Select(envelope, selType);
                }
            });
            return new IpcResponse(true, null, new { done = true });
        }

        private static async Task<IpcResponse> HandleSwitchSelection(IpcRequest req, CancellationToken ct)
        {
            string layerName = null;
            req.Args?.TryGetValue("layer", out layerName);

            await QueuedTask.Run(() =>
            {
                if (!string.IsNullOrWhiteSpace(layerName))
                {
                    MapView.Active?.Map?.Layers
                        .OfType<FeatureLayer>()
                        .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase))
                        ?.SwitchSelection();
                }
                else
                {
                    foreach (var fl in MapView.Active?.Map?.Layers.OfType<FeatureLayer>() ?? Enumerable.Empty<FeatureLayer>())
                        fl.SwitchSelection();
                }
            });
            return new IpcResponse(true, null, new { done = true });
        }

        private static async Task<IpcResponse> HandleGetFeatureByOid(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("oid", out string oidStr))
                return new IpcResponse(false, "args 'layer' & 'oid' required", null);

            long oid = long.Parse(oidStr);
            var result = await QueuedTask.Run(() =>
            {
                var fl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) return null;
                using var fc = fl.GetFeatureClass();
                var qf = new QueryFilter { ObjectIDs = new List<long> { oid } };
                using var cursor = fc.Search(qf);
                if (cursor.MoveNext())
                {
                    using var row = cursor.Current;
                    var attrs = new Dictionary<string, object>();
                    foreach (var field in row.Fields)
                    {
                        attrs[field.Name] = row[field.Name] ?? "<null>";
                    }
                    return new { attributes = attrs };
                }
                return null;
            });
            if (result == null)
                return new IpcResponse(false, $"Feature OID {oid} not found in layer '{layerName}'", null);
            return new IpcResponse(true, null, result);
        }

        private static async Task<IpcResponse> HandleUndoEdit(IpcRequest req, CancellationToken ct)
        {
            bool performed = await QueuedTask.Run(async () =>
            {
                return await EditOperation.UndoAsync();
            });
            return new IpcResponse(true, null, new { undoPerformed = performed });
        }

        private static async Task<IpcResponse> HandleRedoEdit(IpcRequest req, CancellationToken ct)
        {
            bool performed = await QueuedTask.Run(async () =>
            {
                return await EditOperation.RedoAsync();
            });
            return new IpcResponse(true, null, new { redoPerformed = performed });
        }

        private static async Task<IpcResponse> HandleSetActiveTool(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("tool", out string toolDamlId) ||
                string.IsNullOrWhiteSpace(toolDamlId))
                return new IpcResponse(false, "arg 'tool' required", null);

            await QueuedTask.Run(async () =>
            {
                await FrameworkApplication.SetCurrentToolAsync(toolDamlId);
            });
            return new IpcResponse(true, null, new { done = true });
        }

        private static async Task<IpcResponse> HandleIs3d(IpcRequest req, CancellationToken ct)
        {
            var result = await QueuedTask.Run(() =>
            {
                var mode = MapView.Active?.ViewingMode;
                if (mode == null) return new { is3d = false, viewingMode = "NoActiveView" };
                bool is3d = mode == MapViewingMode.GlobalScene || mode == MapViewingMode.LocalScene;
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
                var fl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) return null;

                var renderer = fl.Renderer;
                if (renderer == null)
                    return new { rendererType = "None", field = (string)null, layerName };

                string field = null;
                if (renderer is CIMUniqueValueRenderer uv)
                    field = uv.Fields?.FirstOrDefault();
                else if (renderer is CIMClassBreaksRenderer cb)
                    field = cb.Fields?.FirstOrDefault();

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
                var fl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) return;

                if (fl.Renderer is CIMSimpleRenderer simpleRenderer)
                {
                    var newRenderer = simpleRenderer.Clone() as CIMSimpleRenderer;
                    if (newRenderer?.Symbol?.Clone() is CIMPolygonSymbol newPoly)
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
                            newRenderer.Symbol = newPoly;
                            fl.Renderer = newRenderer;
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
                var layer = MapView.Active?.Map?.Layers
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
                createdLayer = LayerFactory.CreateLayer(new Uri(filePath), map);
            });

            if (createdLayer == null)
                return new IpcResponse(false, "Failed to add layer. Verify the path is valid.", null);
            return new IpcResponse(true, null, new { done = true, layerName = createdLayer.Name });
        }

        private static async Task<IpcResponse> HandleSelectByPolygon(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("coordinates", out string coords) ||
                string.IsNullOrWhiteSpace(coords))
                return new IpcResponse(false, "args 'layer' & 'coordinates' required", null);

            req.Args.TryGetValue("selectionType", out string selTypeStr);
            var selType = string.IsNullOrWhiteSpace(selTypeStr)
                ? SelectionType.New
                : (SelectionType)Enum.Parse(typeof(SelectionType), selTypeStr, ignoreCase: true);

            var coordPairs = coords.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var pts = new List<MapPoint>();
            foreach (var pair in coordPairs)
            {
                var xy = pair.Split(',');
                if (xy.Length == 2 &&
                    double.TryParse(xy[0], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double x) &&
                    double.TryParse(xy[1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double y))
                {
                    pts.Add(MapPointBuilderEx.CreateMapPoint(x, y));
                }
            }

            if (pts.Count < 3)
                return new IpcResponse(false, "Need at least 3 valid coordinate pairs", null);

            if (pts[0].X != pts[pts.Count - 1].X || pts[0].Y != pts[pts.Count - 1].Y)
                pts.Add(MapPointBuilderEx.CreateMapPoint(pts[0].X, pts[0].Y));

            var poly = PolygonBuilderEx.CreatePolygon(pts);
            int count = 0;

            await QueuedTask.Run(() =>
            {
                var fl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl != null)
                {
                    fl.Select(poly, selType);
                    count = fl.GetSelectionCount();
                }
            });

            return new IpcResponse(true, null, new { done = true, selectionCount = count });
        }

        private static async Task<IpcResponse> HandleListLayouts(IpcRequest req, CancellationToken ct)
        {
            var layouts = await QueuedTask.Run(() =>
                Project.Current.GetItems<LayoutProjectItem>()
                    .Select(l => new { l.Name, l.Path })
                    .ToList());
            return new IpcResponse(true, null, layouts);
        }

        private static async Task<IpcResponse> HandleGetProjectProperties(IpcRequest req, CancellationToken ct)
        {
            var props = await QueuedTask.Run(() =>
            {
                var p = Project.Current;
                return new
                {
                    name = p.Name,
                    path = p.Path,
                    defaultGdb = p.DefaultGeodatabasePath,
                    defaultToolbox = p.DefaultToolboxPath,
                    summary = p.Summary,
                    tags = p.Tags,
                };
            });
            return new IpcResponse(true, null, props);
        }

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
                var fl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                fl?.SetTransparency(transparency);
            });

            return new IpcResponse(true, null, new { done = true });
        }

        private static async Task<IpcResponse> HandleGetAllMapNames(IpcRequest req, CancellationToken ct)
        {
            var maps = await QueuedTask.Run(() =>
                Project.Current.GetItems<MapProjectItem>()
                    .Select(m => new { m.Name })
                    .ToList());
            return new IpcResponse(true, null, maps);
        }

        private static async Task<IpcResponse> HandleGetMapFrame(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layoutName", out string layoutName) ||
                string.IsNullOrWhiteSpace(layoutName))
                return new IpcResponse(false, "arg 'layoutName' required", null);

            req.Args.TryGetValue("mapFrameName", out string mapFrameName);

            var result = await QueuedTask.Run(() =>
            {
                var layoutItem = Project.Current.GetItems<LayoutProjectItem>()
                    .FirstOrDefault(l => l.Name.Equals(layoutName, StringComparison.OrdinalIgnoreCase));
                if (layoutItem == null) return null;

                using var layout = layoutItem.GetLayout();
                var mfs = layout.GetElementsOfType<MapFrame>();
                if (!string.IsNullOrWhiteSpace(mapFrameName))
                    mfs = mfs.Where(mf => mf.Name.Equals(mapFrameName, StringComparison.OrdinalIgnoreCase));

                return mfs.Select(mf => new
                {
                    name = mf.Name,
                    mapName = mf.MapView?.Map?.Name,
                    width = mf.GetGraphicBounds().Width,
                    height = mf.GetGraphicBounds().Height,
                    cameraX = mf.Camera?.X,
                    cameraY = mf.Camera?.Y,
                    cameraScale = mf.Camera?.Scale,
                }).ToList();
            });

            if (result == null)
                return new IpcResponse(false, $"Layout '{layoutName}' not found", null);
            return new IpcResponse(true, null, result);
        }

        private static async Task<IpcResponse> HandleSelectByLayer(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("targetLayer", out string targetName) ||
                string.IsNullOrWhiteSpace(targetName) ||
                !req.Args.TryGetValue("sourceLayer", out string sourceName) ||
                string.IsNullOrWhiteSpace(sourceName) ||
                !req.Args.TryGetValue("spatialRelationship", out string relStr) ||
                string.IsNullOrWhiteSpace(relStr))
                return new IpcResponse(false, "args 'targetLayer', 'sourceLayer', & 'spatialRelationship' required", null);

            req.Args.TryGetValue("selectionType", out string selTypeStr);
            var selType = string.IsNullOrWhiteSpace(selTypeStr)
                ? SelectionType.New
                : (SelectionType)Enum.Parse(typeof(SelectionType), selTypeStr, ignoreCase: true);

            var rel = (SpatialRelationship)Enum.Parse(typeof(SpatialRelationship), relStr, ignoreCase: true);
            int count = 0;

            await QueuedTask.Run(() =>
            {
                var srcFl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(sourceName, StringComparison.OrdinalIgnoreCase));
                var tgtFl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));
                if (srcFl == null || tgtFl == null) return;

                using var srcFc = srcFl.GetFeatureClass();
                var srcExtent = srcFc.GetExtent();

                var sqf = new SpatialQueryFilter
                {
                    SpatialRelationship = rel,
                    FilterGeometry = srcExtent,
                };
                tgtFl.Select(sqf, selType);
                count = tgtFl.GetSelectionCount();
            });

            return new IpcResponse(true, null, new { done = true, selectionCount = count });
        }

        private static async Task<IpcResponse> HandleGetFeaturesByExtent(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("xmin", out string xminStr) ||
                !req.Args.TryGetValue("ymin", out string yminStr) ||
                !req.Args.TryGetValue("xmax", out string xmaxStr) ||
                !req.Args.TryGetValue("ymax", out string ymaxStr))
                return new IpcResponse(false, "args 'layer', 'xmin', 'ymin', 'xmax', 'ymax' required", null);

            req.Args.TryGetValue("fields", out string fields);
            req.Args.TryGetValue("maxFeatures", out string maxFcStr);
            int maxFeatures = string.IsNullOrWhiteSpace(maxFcStr) ? 100 : int.Parse(maxFcStr);

            double xmin = double.Parse(xminStr);
            double ymin = double.Parse(yminStr);
            double xmax = double.Parse(xmaxStr);
            double ymax = double.Parse(ymaxStr);

            var fieldList = string.IsNullOrWhiteSpace(fields)
                ? null
                : fields.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(f => f.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var result = await QueuedTask.Run(() =>
            {
                var fl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) return null;

                using var fc = fl.GetFeatureClass();
                var sr = fc.GetSpatialReference();
                var envelope = EnvelopeBuilder.CreateEnvelope(xmin, ymin, xmax, ymax, sr);

                var sqf = new SpatialQueryFilter
                {
                    SpatialRelationship = SpatialRelationship.Intersects,
                    FilterGeometry = envelope,
                };
                if (!string.IsNullOrWhiteSpace(fields))
                    sqf.SubFields = fields;

                var features = new List<object>();
                using var cursor = fc.Search(sqf);
                while (cursor.MoveNext() && features.Count < maxFeatures)
                {
                    using var row = cursor.Current;
                    var attrs = new Dictionary<string, object>();
                    foreach (var f in row.Fields)
                    {
                        if (fieldList == null || fieldList.Contains(f.Name))
                            attrs[f.Name] = row[f.Name] ?? "<null>";
                    }
                    features.Add(attrs);
                }
                return new { features, count = features.Count, totalExtent = new { xmin, ymin, xmax, ymax } };
            });

            if (result == null)
                return new IpcResponse(false, $"Layer '{layerName}' not found", null);
            return new IpcResponse(true, null, result);
        }

        private static async Task<IpcResponse> HandleDeleteFeaturesByOid(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("oids", out string oidsStr) ||
                string.IsNullOrWhiteSpace(oidsStr))
                return new IpcResponse(false, "args 'layer' & 'oids' required", null);

            var oidList = oidsStr.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => long.Parse(s.Trim())).ToList();

            bool executed = await QueuedTask.Run(async () =>
            {
                var fl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) return false;

                using var fc = fl.GetFeatureClass();
                var qf = new QueryFilter { ObjectIDs = oidList };
                using var cursor = fc.Search(qf, false);

                var op = new EditOperation();
                op.Name = "Delete features by OID";
                while (cursor.MoveNext())
                {
                    using var row = cursor.Current;
                    op.Delete(row);
                }
                return await op.ExecuteAsync();
            });

            return new IpcResponse(true, null, new { done = executed, count = oidList.Count });
        }

        private static async Task<IpcResponse> HandleUpdateFeatureAttributes(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("oid", out string oidStr) ||
                !req.Args.TryGetValue("attributes", out string attrsJson) ||
                string.IsNullOrWhiteSpace(attrsJson))
                return new IpcResponse(false, "args 'layer', 'oid', & 'attributes' required", null);

            long oid = long.Parse(oidStr);
            var attrs = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(attrsJson);

            bool executed = await QueuedTask.Run(async () =>
            {
                var fl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) return false;

                using var fc = fl.GetFeatureClass();
                var qf = new QueryFilter { ObjectIDs = new List<long> { oid } };
                using var cursor = fc.Search(qf, false);
                if (!cursor.MoveNext()) return false;

                using var row = cursor.Current;
                var op = new EditOperation();
                op.Name = "Update feature attributes";
                foreach (var kvp in attrs)
                    op.Modify(row, kvp.Key, JsonElementToObject(kvp.Value));

                return await op.ExecuteAsync();
            });

            return new IpcResponse(true, null, new { done = executed });
        }

        private static async Task<IpcResponse> HandleCreatePointFeature(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("x", out string xStr) ||
                !req.Args.TryGetValue("y", out string yStr))
                return new IpcResponse(false, "args 'layer', 'x', & 'y' required", null);

            double x = double.Parse(xStr);
            double y = double.Parse(yStr);

            req.Args.TryGetValue("wkid", out string wkidStr);
            req.Args.TryGetValue("attributes", out string attrsJson);

            var result = await QueuedTask.Run(async () =>
            {
                var fl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) return null;

                using var fc = fl.GetFeatureClass();

                SpatialReference sr = null;
                if (!string.IsNullOrWhiteSpace(wkidStr) && int.TryParse(wkidStr, out int wkid))
                    sr = SpatialReferenceBuilder.CreateSpatialReference(wkid);

                var pt = MapPointBuilderEx.CreateMapPoint(x, y, sr);

                var op = new EditOperation();
                op.Name = "Create point feature";
                var token = op.Create(fc, pt);

                if (!string.IsNullOrWhiteSpace(attrsJson))
                {
                    var jsonAttrs = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(attrsJson);
                    foreach (var kvp in jsonAttrs)
                        op.Modify(token, kvp.Key, JsonElementToObject(kvp.Value));
                }

                bool ok = await op.ExecuteAsync();
                return new
                {
                    done = ok,
                    objectId = ok ? token.ObjectID : (long?)null,
                };
            });

            if (result == null)
                return new IpcResponse(false, $"Layer '{layerName}' not found", null);
            return new IpcResponse(true, null, result);
        }

        private static async Task<IpcResponse> HandleListBookmarks(IpcRequest req, CancellationToken ct)
        {
            var bookmarks = await QueuedTask.Run(() =>
                MapView.Active?.Map?.GetBookmarks()
                    .Select(b => new {
                        b.Name,
                        xmin = b.Camera?.Extent?.XMin,
                        ymin = b.Camera?.Extent?.YMin,
                        xmax = b.Camera?.Extent?.XMax,
                        ymax = b.Camera?.Extent?.YMax,
                    })
                    .ToList() ?? new List<object>()
            );
            return new IpcResponse(true, null, bookmarks ?? new List<object>());
        }

        private static async Task<IpcResponse> HandleZoomToBookmark(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("name", out string bmName) ||
                string.IsNullOrWhiteSpace(bmName))
                return new IpcResponse(false, "arg 'name' required", null);

            await QueuedTask.Run(async () =>
            {
                var bkmk = MapView.Active?.Map?.GetBookmarks()
                    .FirstOrDefault(b => b.Name.Equals(bmName, StringComparison.OrdinalIgnoreCase));
                if (bkmk != null)
                    await bkmk.ZoomToAsync();
            });

            return new IpcResponse(true, null, new { done = true });
        }

        private static async Task<IpcResponse> HandleCreateBookmark(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("name", out string bmName) ||
                string.IsNullOrWhiteSpace(bmName))
                return new IpcResponse(false, "arg 'name' required", null);

            var bookmark = await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                var camera = MapView.Active?.Camera;
                if (map == null || camera == null) return null;
                return map.CreateBookmark(bmName, camera);
            });

            if (bookmark == null)
                return new IpcResponse(false, "Failed to create bookmark. Ensure a map view is active.", null);
            return new IpcResponse(true, null, new { name = bookmark.Name, created = true });
        }

        private static async Task<IpcResponse> HandleReorderLayer(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("index", out string indexStr))
                return new IpcResponse(false, "args 'layer' & 'index' required", null);

            int index = int.Parse(indexStr);

            await QueuedTask.Run(() =>
            {
                var layer = MapView.Active?.Map?.Layers
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (layer != null)
                    MapView.Active.Map.SetLayerIndex(layer, index);
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
                var fl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl != null)
                    fl.ShowLabels = enabled;
            });

            return new IpcResponse(true, null, new { done = true });
        }

        private static async Task<IpcResponse> HandleOpenDockpane(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("dockpaneId", out string damlId) ||
                string.IsNullOrWhiteSpace(damlId))
                return new IpcResponse(false, "arg 'dockpaneId' required", null);

            bool found = false;
            await QueuedTask.Run(() =>
            {
                var pane = FrameworkApplication.DockPaneManager.Find(damlId);
                if (pane == null && _knownDockPanes.TryGetValue(damlId, out string resolvedId))
                    pane = FrameworkApplication.DockPaneManager.Find(resolvedId);
                if (pane != null) { pane.Activate(); found = true; }
            });

            return new IpcResponse(found, found ? null : $"Dockpane not found: {damlId}", new { done = found, dockpaneId = damlId });
        }

        private static async Task<IpcResponse> HandleExportLayoutToFile(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layoutName", out string layoutName) ||
                string.IsNullOrWhiteSpace(layoutName) ||
                !req.Args.TryGetValue("outputPath", out string outputPath) ||
                string.IsNullOrWhiteSpace(outputPath))
                return new IpcResponse(false, "args 'layoutName' & 'outputPath' required", null);

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
                    layout.ExportToPDF(outputPath, new PDFExportOptions { Resolution = dpi });
                }
                else
                {
                    layout.ExportToPNG(outputPath, new PNGExportOptions { Resolution = dpi });
                }
            });

            return new IpcResponse(true, null, new { done = true, outputPath });
        }

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

        // --- Phase 4: Advanced Symbology ---

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

        private static CIMSymbol CreateSymbolForGeometry(byte r, byte g, byte b, esriGeometryType shapeType)
        {
            var color = ColorFactory.Instance.CreateRGBColor(r, g, b);
            switch (shapeType)
            {
                case esriGeometryType.esriGeometryPolygon:
                    return SymbolFactory.Instance.ConstructPolygonSymbol(color, 0.4,
                        ColorFactory.Instance.CreateRGBColor(0, 0, 0));
                case esriGeometryType.esriGeometryPolyline:
                    return SymbolFactory.Instance.ConstructLineSymbol(color, 1.0);
                case esriGeometryType.esriGeometryPoint:
                case esriGeometryType.esriGeometryMultipoint:
                    return SymbolFactory.Instance.ConstructMarkerSymbol(color, 8.0);
                default:
                    return SymbolFactory.Instance.ConstructPolygonSymbol(color);
            }
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
                var fl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) { warning = "Layer not found"; return; }

                using var fc = fl.GetFeatureClass();
                var shapeType = fc.GetDefinition().GetFields().First(f => f.Name == fc.GetDefinition().GetShapeField()).ShapeType;

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
                        Symbol = CreateSymbolForGeometry(r, g, b, shapeType),
                    };
                }).ToArray();

                var defaultColor = ColorFactory.Instance.CreateRGBColor(220, 220, 220);
                CIMSymbol defaultSymbol = shapeType switch
                {
                    esriGeometryType.esriGeometryPolygon => SymbolFactory.Instance.ConstructPolygonSymbol(defaultColor),
                    esriGeometryType.esriGeometryPolyline => SymbolFactory.Instance.ConstructLineSymbol(defaultColor, 0.5),
                    _ => SymbolFactory.Instance.ConstructMarkerSymbol(defaultColor, 6.0),
                };

                var renderer = new CIMUniqueValueRenderer
                {
                    Fields = new[] { field },
                    DefaultLabel = "Other",
                    DefaultSymbol = defaultSymbol,
                    Groups = new[]
                    {
                        new CIMUniqueValueGroup
                        {
                            Heading = field,
                            Classes = classes,
                        }
                    },
                };

                fl.Renderer = renderer;
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
                var fl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) { warning = "Layer not found"; return; }

                using var fc = fl.GetFeatureClass();
                var shapeType = fc.GetDefinition().GetFields().First(f => f.Name == fc.GetDefinition().GetShapeField()).ShapeType;

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
                        Symbol = CreateSymbolForGeometry(r, g, b, shapeType),
                    };
                }

                var renderer = new CIMClassBreaksRenderer
                {
                    Fields = new[] { field },
                    ClassificationField = field,
                    BreakCount = breakCount,
                    Breaks = breaks,
                };

                fl.Renderer = renderer;
            });

            return new IpcResponse(true, null, new { done = true, breakCount, warning });
        }

        // --- Phase 4: Scene / 3D ---

        private static async Task<IpcResponse> HandleGetElevationSources(IpcRequest req, CancellationToken ct)
        {
            var result = await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return null;

                var ground = map.GetGround();
                if (ground == null)
                    return new { isScene = false, elevationSources = new List<object>() };

                var sources = ground.GetElevationSources()
                    .Select(s => new
                    {
                        name = s.Name,
                        sourceType = s.GetType().Name,
                        isVisible = s.IsVisible,
                    })
                    .ToList();

                return new
                {
                    isScene = true,
                    viewingMode = MapView.Active?.ViewingMode.ToString(),
                    elevationSources = sources,
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
                if (map == null) return;
                var ground = map.GetGround();
                if (ground == null) return;
                ground.SetVisibility(100 - opacity); // Pro uses visibility not transparency
            });

            return new IpcResponse(true, null, new { done = true });
        }

        // --- Phase 4: UI / Tool ---

        private static Task<IpcResponse> HandleGetActiveTool(IpcRequest req, CancellationToken ct)
        {
            var toolId = FrameworkApplication.CurrentTool;
            return Task.FromResult(new IpcResponse(true, null, new { activeTool = toolId ?? "<none>" }));
        }

        // --- Phase 4: Data Inspection ---

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
                var fl = MapView.Active?.Map?.Layers
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

        // --- Phase 4: Schema Editing ---

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

            bool executed = false;
            string warning = null;

            await QueuedTask.Run(() =>
            {
                var fl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) { warning = "Layer not found"; return; }

                using var fc = fl.GetFeatureClass();
                var tableDef = fc.GetDefinition() as TableDefinition;
                if (tableDef == null) { warning = "Cannot get table definition"; return; }

                var existingFields = tableDef.GetFields();
                if (existingFields.Any(f => f.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase)))
                { warning = $"Field '{fieldName}' already exists"; return; }

                var fieldDesc = new FieldDescription(
                    fieldName,
                    (FieldType)Enum.Parse(typeof(FieldType), fieldType, ignoreCase: true)
                );

                if (!string.IsNullOrWhiteSpace(precStr) && int.TryParse(precStr, out int precision))
                    fieldDesc.Precision = precision;
                if (!string.IsNullOrWhiteSpace(scaleStr) && int.TryParse(scaleStr, out int scale))
                    fieldDesc.Scale = scale;
                if (!string.IsNullOrWhiteSpace(lenStr) && int.TryParse(lenStr, out int length))
                    fieldDesc.Length = length;

                tableDef.AddField(fieldDesc);
                executed = true;
            });

            return new IpcResponse(true, null, new { done = executed, fieldName, warning });
        }

        private static async Task<IpcResponse> HandleDeleteField(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("fieldName", out string fieldName) ||
                string.IsNullOrWhiteSpace(fieldName))
                return new IpcResponse(false, "args 'layer' & 'fieldName' required", null);

            bool executed = false;
            string warning = null;

            await QueuedTask.Run(() =>
            {
                var fl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) { warning = "Layer not found"; return; }

                using var fc = fl.GetFeatureClass();
                var tableDef = fc.GetDefinition() as TableDefinition;
                if (tableDef == null) { warning = "Cannot get table definition"; return; }

                var existingFields = tableDef.GetFields();
                var targetField = existingFields.FirstOrDefault(f =>
                    f.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase));
                if (targetField == null)
                { warning = $"Field '{fieldName}' not found"; return; }
                if (!targetField.IsEditable)
                { warning = $"Field '{fieldName}' is not deletable"; return; }

                tableDef.DeleteField(targetField);
                executed = true;
            });

            return new IpcResponse(true, null, new { done = executed, fieldName, warning });
        }

        // --- Phase 4: Feature Creation ---

        private static async Task<IpcResponse> HandleCreatePolygonFeature(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("coordinates", out string coords) ||
                string.IsNullOrWhiteSpace(coords))
                return new IpcResponse(false, "args 'layer' & 'coordinates' required", null);

            req.Args.TryGetValue("wkid", out string wkidStr);
            req.Args.TryGetValue("attributes", out string attrsJson);

            var coordPairs = coords.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var pts = new List<MapPoint>();
            foreach (var pair in coordPairs)
            {
                var xy = pair.Split(',');
                if (xy.Length == 2 &&
                    double.TryParse(xy[0], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double x) &&
                    double.TryParse(xy[1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double y))
                {
                    pts.Add(MapPointBuilderEx.CreateMapPoint(x, y));
                }
            }

            if (pts.Count < 3)
                return new IpcResponse(false, "Need at least 3 valid coordinate pairs", null);

            // Close the ring if not already closed
            if (pts[0].X != pts[pts.Count - 1].X || pts[0].Y != pts[pts.Count - 1].Y)
                pts.Add(MapPointBuilderEx.CreateMapPoint(pts[0].X, pts[0].Y));

            SpatialReference sr = null;
            if (!string.IsNullOrWhiteSpace(wkidStr) && int.TryParse(wkidStr, out int wkid))
                sr = SpatialReferenceBuilder.CreateSpatialReference(wkid);

            var poly = PolygonBuilderEx.CreatePolygon(pts, sr);

            var result = await QueuedTask.Run(async () =>
            {
                var fl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) return null;

                using var fc = fl.GetFeatureClass();

                var op = new EditOperation();
                op.Name = "Create polygon feature";
                var token = op.Create(fc, poly);

                if (!string.IsNullOrWhiteSpace(attrsJson))
                {
                    var jsonAttrs = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(attrsJson);
                    foreach (var kvp in jsonAttrs)
                        op.Modify(token, kvp.Key, JsonElementToObject(kvp.Value));
                }

                bool ok = await op.ExecuteAsync();
                return new
                {
                    done = ok,
                    objectId = ok ? token.ObjectID : (long?)null,
                    vertexCount = pts.Count,
                };
            });

            if (result == null)
                return new IpcResponse(false, $"Layer '{layerName}' not found", null);
            return new IpcResponse(true, null, result);
        }

        private static async Task<IpcResponse> HandleCreateLineFeature(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("coordinates", out string coords) ||
                string.IsNullOrWhiteSpace(coords))
                return new IpcResponse(false, "args 'layer' & 'coordinates' required", null);

            req.Args.TryGetValue("wkid", out string wkidStr);
            req.Args.TryGetValue("attributes", out string attrsJson);

            var coordPairs = coords.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var pts = new List<MapPoint>();
            foreach (var pair in coordPairs)
            {
                var xy = pair.Split(',');
                if (xy.Length == 2 &&
                    double.TryParse(xy[0], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double x) &&
                    double.TryParse(xy[1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double y))
                {
                    pts.Add(MapPointBuilderEx.CreateMapPoint(x, y));
                }
            }

            if (pts.Count < 2)
                return new IpcResponse(false, "Need at least 2 valid coordinate pairs", null);

            SpatialReference sr = null;
            if (!string.IsNullOrWhiteSpace(wkidStr) && int.TryParse(wkidStr, out int wkid))
                sr = SpatialReferenceBuilder.CreateSpatialReference(wkid);

            var polyline = PolylineBuilderEx.CreatePolyline(pts, sr);

            var result = await QueuedTask.Run(async () =>
            {
                var fl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) return null;

                using var fc = fl.GetFeatureClass();

                var op = new EditOperation();
                op.Name = "Create line feature";
                var token = op.Create(fc, polyline);

                if (!string.IsNullOrWhiteSpace(attrsJson))
                {
                    var jsonAttrs = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(attrsJson);
                    foreach (var kvp in jsonAttrs)
                        op.Modify(token, kvp.Key, JsonElementToObject(kvp.Value));
                }

                bool ok = await op.ExecuteAsync();
                return new
                {
                    done = ok,
                    objectId = ok ? token.ObjectID : (long?)null,
                    vertexCount = pts.Count,
                };
            });

            if (result == null)
                return new IpcResponse(false, $"Layer '{layerName}' not found", null);
            return new IpcResponse(true, null, result);
        }

        // --- Phase 5: Map Navigation ---

        private static async Task<IpcResponse> HandleSetMapScale(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("scale", out string scaleStr))
                return new IpcResponse(false, "arg 'scale' required", null);

            double scale = double.Parse(scaleStr);
            if (scale <= 0) return new IpcResponse(false, "scale must be positive", null);

            await QueuedTask.Run(() =>
            {
                var cam = MapView.Active?.Camera;
                if (cam != null) cam.Scale = scale;
            });

            return new IpcResponse(true, null, new { done = true, scale });
        }

        private static async Task<IpcResponse> HandleGetMapScale(IpcRequest req, CancellationToken ct)
        {
            var scale = await QueuedTask.Run(() =>
                MapView.Active?.Camera?.Scale
            );

            if (scale == null)
                return new IpcResponse(false, "No active map view", null);
            return new IpcResponse(true, null, new { scale });
        }

        private static async Task<IpcResponse> HandleZoomToSelected(IpcRequest req, CancellationToken ct)
        {
            string layerName = null;
            req.Args?.TryGetValue("layer", out layerName);

            await QueuedTask.Run(async () =>
            {
                if (!string.IsNullOrWhiteSpace(layerName))
                {
                    var fl = MapView.Active?.Map?.Layers
                        .OfType<FeatureLayer>()
                        .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                    if (fl != null && fl.GetSelectionCount() > 0)
                        await MapView.Active.ZoomToSelectedAsync(fl);
                }
                else
                {
                    await MapView.Active.ZoomToSelectedAsync();
                }
            });

            return new IpcResponse(true, null, new { done = true });
        }

        // --- Phase 5: Editing State ---

        private static Task<IpcResponse> HandleGetEditState(IpcRequest req, CancellationToken ct)
        {
            int undoCount = EditOperation.UndoCount;
            int redoCount = EditOperation.RedoCount;
            return Task.FromResult(new IpcResponse(true, null, new { undoCount, redoCount }));
        }

        private static async Task<IpcResponse> HandleSetSnapping(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("enabled", out string enabledStr))
                return new IpcResponse(false, "arg 'enabled' required", null);

            bool enabled = bool.Parse(enabledStr);

            await QueuedTask.Run(() =>
            {
                Snapping.IsEnabled = enabled;
            });

            return new IpcResponse(true, null, new { done = true, snappingEnabled = enabled });
        }

        // --- Phase 5: Bookmark Management ---

        private static async Task<IpcResponse> HandleDeleteBookmark(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("name", out string bmName) ||
                string.IsNullOrWhiteSpace(bmName))
                return new IpcResponse(false, "arg 'name' required", null);

            bool found = false;

            await QueuedTask.Run(() =>
            {
                var bkmk = MapView.Active?.Map?.GetBookmarks()
                    .FirstOrDefault(b => b.Name.Equals(bmName, StringComparison.OrdinalIgnoreCase));
                if (bkmk != null)
                {
                    MapView.Active.Map.RemoveBookmark(bkmk);
                    found = true;
                }
            });

            if (!found)
                return new IpcResponse(false, $"Bookmark '{bmName}' not found", null);
            return new IpcResponse(true, null, new { done = true, name = bmName });
        }

        // --- Phase 5: Selection UX ---

        private static async Task<IpcResponse> HandleFlashSelection(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName))
                return new IpcResponse(false, "arg 'layer' required", null);

            int count = 0;

            await QueuedTask.Run(async () =>
            {
                var fl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) return;

                using var fc = fl.GetFeatureClass();
                var def = fc.GetDefinition();
                var shapeField = def.GetShapeField();
                var selectionSet = fl.GetSelection();
                var oids = selectionSet.GetObjectIDs().ToList();
                if (oids.Count == 0) return;

                var qf = new QueryFilter { ObjectIDs = oids };
                using var cursor = fc.Search(qf);
                while (cursor.MoveNext())
                {
                    using var row = cursor.Current;
                    if (row[shapeField] is ArcGIS.Core.Geometry.Geometry geom)
                    {
                        await MapView.Active.FlashGeometry(geom);
                        count++;
                    }
                }
            });

            return new IpcResponse(true, null, new { done = true, flashedCount = count });
        }

        private static async Task<IpcResponse> HandleSelectAll(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName))
                return new IpcResponse(false, "arg 'layer' required", null);

            int count = 0;

            await QueuedTask.Run(() =>
            {
                var fl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl != null)
                {
                    fl.Select(new QueryFilter());
                    count = fl.GetSelectionCount();
                }
            });

            return new IpcResponse(true, null, new { done = true, count });
        }

        // --- Phase 5: UI Feedback ---

        private static Task<IpcResponse> HandleSetStatusBarMessage(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("message", out string message) ||
                string.IsNullOrWhiteSpace(message))
                return Task.FromResult(new IpcResponse(false, "arg 'message' required", null));

            Application.StatusBar?.SetMessage(message, 0);
            return Task.FromResult(new IpcResponse(true, null, new { done = true, message }));
        }

        // --- Phase 5: Data Discovery ---

        private static async Task<IpcResponse> HandleListStandaloneTables(IpcRequest req, CancellationToken ct)
        {
            var tables = await QueuedTask.Run(() =>
                Project.Current.GetItems<TableProjectItem>()
                    .Select(t => new { t.Name, t.Path })
                    .ToList() ?? new List<object>()
            );
            return new IpcResponse(true, null, tables);
        }

        // --- Phase 6: Geoprocessing History ---

        private static async Task<IpcResponse> HandleListGpHistory(IpcRequest req, CancellationToken ct)
        {
            req.Args?.TryGetValue("maxItems", out string maxStr);
            int maxItems = string.IsNullOrWhiteSpace(maxStr) ? 20 : int.Parse(maxStr);

            var items = await QueuedTask.Run(async () =>
            {
                var history = await Geoprocessing.GetHistoryAsync();
                return history
                    .OrderByDescending(h => h.DateTime)
                    .Take(maxItems)
                    .Select(h => new
                    {
                        toolName = h.ToolName,
                        toolboxName = h.ToolboxName,
                        status = h.Status.ToString(),
                        dateTime = h.DateTime.ToString("O"),
                        durationMs = h.Duration,
                    })
                    .ToList();
            });
            return new IpcResponse(true, null, items ?? new List<object>());
        }

        // --- Phase 6: Time Slider ---

        private static async Task<IpcResponse> HandleIsTimeEnabled(IpcRequest req, CancellationToken ct)
        {
            var result = await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return null;
                return new { isTimeEnabled = map.IsTimeEnabled };
            });

            if (result == null) return new IpcResponse(false, "No active map view", null);
            return new IpcResponse(true, null, result);
        }

        private static async Task<IpcResponse> HandleGetTimeExtent(IpcRequest req, CancellationToken ct)
        {
            var result = await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return null;
                var te = map.GetTimeExtent();
                if (te == null) return new { hasTimeExtent = false, start = (string)null, end = (string)null };
                return new
                {
                    hasTimeExtent = true,
                    start = te.StartTime?.ToString("O"),
                    end = te.EndTime?.ToString("O"),
                };
            });

            if (result == null) return new IpcResponse(false, "No active map view", null);
            return new IpcResponse(true, null, result);
        }

        private static async Task<IpcResponse> HandleSetTimeExtent(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("start", out string startStr) ||
                !req.Args.TryGetValue("end", out string endStr))
                return new IpcResponse(false, "args 'start' & 'end' required", null);

            DateTime start = DateTime.Parse(startStr);
            DateTime end = DateTime.Parse(endStr);

            await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map != null)
                    map.SetTimeExtent(new TimeExtent(start, end));
            });

            return new IpcResponse(true, null, new { done = true });
        }

        // --- Phase 6: Layout Elements ---

        private static async Task<IpcResponse> HandleListLayoutElements(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layoutName", out string layoutName) ||
                string.IsNullOrWhiteSpace(layoutName))
                return new IpcResponse(false, "arg 'layoutName' required", null);

            var result = await QueuedTask.Run(() =>
            {
                var layoutItem = Project.Current.GetItems<LayoutProjectItem>()
                    .FirstOrDefault(l => l.Name.Equals(layoutName, StringComparison.OrdinalIgnoreCase));
                if (layoutItem == null) return null;

                using var layout = layoutItem.GetLayout();
                var elements = new List<object>();

                var graphicsElements = layout.GetElementsOfType<GraphicsElement>();
                foreach (var el in graphicsElements)
                    elements.Add(new { name = el.Name ?? "", type = el.GetType().Name, elementType = "GraphicsElement", visible = el.IsVisible });

                var mapFrames = layout.GetElementsOfType<MapFrame>();
                foreach (var mf in mapFrames)
                    elements.Add(new { name = mf.Name ?? "", type = mf.GetType().Name, elementType = "MapFrame", mapName = mf.MapView?.Map?.Name, visible = mf.IsVisible });

                var mapSurrounds = layout.GetElementsOfType<MapSurround>();
                foreach (var ms in mapSurrounds)
                    elements.Add(new { name = ms.Name ?? "", type = ms.GetType().Name, elementType = "MapSurround", visible = ms.IsVisible });

                return new { layoutName, elementCount = elements.Count, elements };
            });

            if (result == null) return new IpcResponse(false, $"Layout '{layoutName}' not found", null);
            return new IpcResponse(true, null, result);
        }

        // --- Phase 6: Field Management ---

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
                var fl = MapView.Active?.Map?.Layers
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

                tableDef.RenameField(oldName, newName);
                executed = true;
            });

            return new IpcResponse(true, null, new { done = executed, oldName, newName, warning });
        }

        // --- Phase 6: Layer Description ---

        private static async Task<IpcResponse> HandleGetLayerDescription(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName))
                return new IpcResponse(false, "arg 'layer' required", null);

            var result = await QueuedTask.Run(() =>
            {
                var layer = MapView.Active?.Map?.Layers
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (layer == null) return null;
                return new { layerName, description = layer.Description ?? "" };
            });

            if (result == null) return new IpcResponse(false, $"Layer '{layerName}' not found", null);
            return new IpcResponse(true, null, result);
        }

        private static async Task<IpcResponse> HandleSetLayerDescription(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("description", out string description) ||
                description == null)
                return new IpcResponse(false, "args 'layer' & 'description' required", null);

            await QueuedTask.Run(() =>
            {
                var layer = MapView.Active?.Map?.Layers
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (layer != null)
                    layer.Description = description;
            });

            return new IpcResponse(true, null, new { done = true });
        }

        // --- Phase 6: Scene Layer Types ---

        private static async Task<IpcResponse> HandleListSceneLayerTypes(IpcRequest req, CancellationToken ct)
        {
            var result = await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return null;

                bool isScene = MapView.Active.ViewingMode == MapViewingMode.GlobalScene ||
                               MapView.Active.ViewingMode == MapViewingMode.LocalScene;

                var layers = map.Layers.Select(l =>
                {
                    string layerType;
                    if (l is PointCloudLayer) layerType = "PointCloudLayer";
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
                    viewingMode = MapView.Active.ViewingMode.ToString(),
                    layerCount = layers.Count,
                    layers,
                };
            });

            if (result == null) return new IpcResponse(false, "No active map view", null);
            return new IpcResponse(true, null, result);
        }

        // --- Phase 6: Query ---

        private static async Task<IpcResponse> HandleCountFeaturesByExpression(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) ||
                string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("where", out string where) ||
                string.IsNullOrWhiteSpace(where))
                return new IpcResponse(false, "args 'layer' & 'where' required", null);

            int count = await QueuedTask.Run(() =>
            {
                var fl = MapView.Active?.Map?.Layers
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

        // --- Phase 7: Advanced Editing ---

        private static async Task<IpcResponse> HandleSplitFeatures(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) || string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("cutGeometry", out string cutJson) || string.IsNullOrWhiteSpace(cutJson))
                return new IpcResponse(false, "args 'layer' & 'cutGeometry' (JSON) required", null);

            string warning = null;
            int splitCount = 0;

            await QueuedTask.Run(async () =>
            {
                var fl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) { warning = "Layer not found"; return; }

                var cutGeom = ParseGeometryFromJson(cutJson);
                if (cutGeom == null) { warning = "Invalid cut geometry JSON"; return; }

                var op = new EditOperation();
                op.Name = "Split features";
                op.SelectNewFeatures = false;
                splitCount = op.Split(fl, cutGeom);
                if (splitCount == 0) { warning = "No features intersected cut geometry"; return; }
                await op.ExecuteAsync();
            });

            if (warning != null) return new IpcResponse(false, warning, null);
            return new IpcResponse(true, null, new { done = true, splitCount });
        }

        private static async Task<IpcResponse> HandleMergeFeatures(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) || string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("objectIds", out string oidsJson) || string.IsNullOrWhiteSpace(oidsJson) ||
                !req.Args.TryGetValue("targetOid", out string targetOidStr) || string.IsNullOrWhiteSpace(targetOidStr))
                return new IpcResponse(false, "args 'layer', 'objectIds' (JSON array), & 'targetOid' required", null);

            string warning = null;

            await QueuedTask.Run(async () =>
            {
                var fl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) { warning = "Layer not found"; return; }

                var oids = JsonSerializer.Deserialize<List<long>>(oidsJson);
                if (oids == null || oids.Count < 2) { warning = "Need at least 2 object IDs to merge"; return; }
                if (!long.TryParse(targetOidStr, out long targetOid)) { warning = "Invalid targetOid"; return; }

                var op = new EditOperation();
                op.Name = "Merge features";
                op.Merge(fl, oids, targetOid);
                await op.ExecuteAsync();
            });

            if (warning != null) return new IpcResponse(false, warning, null);
            return new IpcResponse(true, null, new { done = true });
        }

        // --- Phase 7: Geoprocessing ---

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

                var gpValues = new List<IGPValue>();
                foreach (var v in values)
                {
                    if (v.ValueKind == JsonValueKind.String) gpValues.Add(GPValue.Create(v.GetString()));
                    else if (v.ValueKind == JsonValueKind.Number) gpValues.Add(GPValue.Create(v.GetDouble()));
                    else if (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) gpValues.Add(GPValue.Create(v.GetBoolean()));
                    else gpValues.Add(GPValue.Create(v.GetRawText()));
                }

                try
                {
                    var results = await Geoprocessing.ExecuteAsync(toolName, gpValues.ToArray());
                    outputs = results?.Select(r => new
                    {
                        name = r.Name,
                        data = r.Data?.ToString() ?? "",
                        isFailed = r.IsFailed,
                        messages = r.Messages?.Select(m => new { type = m.Type.ToString(), text = m.Text }).ToList()
                    }).ToList();
                }
                catch (Exception ex)
                {
                    warning = $"GP tool execution failed: {ex.Message}";
                }
            });

            return new IpcResponse(warning == null, warning, outputs);
        }

        private static async Task<IpcResponse> HandleListGpTools(IpcRequest req, CancellationToken ct)
        {
            req.Args?.TryGetValue("searchText", out string searchText);
            req.Args?.TryGetValue("maxResults", out string maxStr);
            if (!int.TryParse(maxStr, out int maxResults)) maxResults = 50;

            var result = await QueuedTask.Run(() =>
            {
                var toolboxes = Project.Current.GetItems<ToolboxProjectItem>();
                var tools = new List<object>();
                foreach (var tb in toolboxes)
                {
                    foreach (var tool in tb.GetToolboxItems())
                    {
                        if (string.IsNullOrWhiteSpace(searchText) ||
                            tool.Name.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            (tool.DisplayName?.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0)
                        {
                            tools.Add(new
                            {
                                name = tool.Name,
                                displayName = tool.DisplayName ?? tool.Name,
                                toolboxName = tb.Name,
                                category = tool.Category ?? ""
                            });
                            if (tools.Count >= maxResults) break;
                        }
                    }
                    if (tools.Count >= maxResults) break;
                }
                return new { toolCount = tools.Count, searchText = searchText ?? "", tools };
            });

            return new IpcResponse(true, null, result);
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
                var fl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) { warning = "Layer not found"; return; }

                using var fc = fl.GetFeatureClass();
                var workspacePath = fc.GetDatastore().GetPath();
                var fcName = fc.GetName();
                var fcPath = System.IO.Path.Combine(workspacePath, fcName);

                var results = await Geoprocessing.ExecuteAsync("CopyFeatures",
                    GPValue.Create(fcPath),
                    GPValue.Create(outputPath));

                if (results != null && results.Any(r => r.IsFailed))
                    warning = "CopyFeatures tool completed with warnings/errors";
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, outputPath });
        }

        // --- Phase 7: Layer Management ---

        private static async Task<IpcResponse> HandleRenameLayer(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) || string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("newName", out string newName) || string.IsNullOrWhiteSpace(newName))
                return new IpcResponse(false, "args 'layer' & 'newName' required", null);

            await QueuedTask.Run(() =>
            {
                var layer = MapView.Active?.Map?.Layers
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (layer != null)
                    layer.Name = newName;
            });

            return new IpcResponse(true, null, new { done = true, oldName = layerName, newName });
        }

        // --- Phase 7: Statistics ---

        private static async Task<IpcResponse> HandleGetLayerStatistics(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) || string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("field", out string field) || string.IsNullOrWhiteSpace(field))
                return new IpcResponse(false, "args 'layer' & 'field' required", null);

            var result = await QueuedTask.Run(() =>
            {
                var fl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) return null;

                using var fc = fl.GetFeatureClass();
                var qf = new QueryFilter { SubFields = field };
                long count = 0, nullCount = 0;
                double sum = 0, sumSq = 0;
                double minVal = double.MaxValue, maxVal = double.MinValue;

                using (var cursor = fc.Search(qf, true))
                {
                    while (cursor.MoveNext())
                    {
                        using var row = cursor.Current;
                        var val = row[field];
                        count++;
                        if (val == null || val == DBNull.Value) { nullCount++; continue; }
                        double d = Convert.ToDouble(val);
                        sum += d;
                        sumSq += d * d;
                        if (d < minVal) minVal = d;
                        if (d > maxVal) maxVal = d;
                    }
                }

                long validCount = count - nullCount;
                double mean = validCount > 0 ? sum / validCount : 0;
                double variance = validCount > 0 ? (sumSq / validCount) - (mean * mean) : 0;
                double stddev = Math.Sqrt(Math.Max(0, variance));

                return new
                {
                    field,
                    count,
                    nullCount,
                    validCount,
                    min = validCount > 0 ? minVal : (double?)null,
                    max = validCount > 0 ? maxVal : (double?)null,
                    mean = validCount > 0 ? Math.Round(mean, 4) : (double?)null,
                    stddev = validCount > 0 ? Math.Round(stddev, 4) : (double?)null,
                };
            });

            if (result == null) return new IpcResponse(false, $"Layer '{layerName}' not found", null);
            return new IpcResponse(true, null, result);
        }

        // --- Phase 7: Geometry ---

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
            return Task.FromResult(new IpcResponse(false, $"Projection failed: {ex.Message}", null));
        }

        // --- Phase 8: Layout & Map Automation ---

        private static async Task<IpcResponse> HandleAddLayoutText(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layoutName", out string layoutName) || string.IsNullOrWhiteSpace(layoutName) ||
                !req.Args.TryGetValue("text", out string text) || text == null ||
                !req.Args.TryGetValue("x", out string xStr) || !double.TryParse(xStr, out double x) ||
                !req.Args.TryGetValue("y", out string yStr) || !double.TryParse(yStr, out double y))
                return new IpcResponse(false, "args 'layoutName', 'text', 'x', & 'y' required", null);

            req.Args.TryGetValue("fontSize", out string fontSizeStr);
            if (!double.TryParse(fontSizeStr, out double fontSize)) fontSize = 12;
            req.Args.TryGetValue("colorRgb", out string colorRgb);

            string warning = null;
            string elementName = null;

            await QueuedTask.Run(() =>
            {
                var layoutItem = Project.Current.GetItems<LayoutProjectItem>()
                    .FirstOrDefault(l => l.Name.Equals(layoutName, StringComparison.OrdinalIgnoreCase));
                if (layoutItem == null) { warning = "Layout not found"; return; }
                var layout = layoutItem.GetLayout();

                byte r = 0, g = 0, b = 0;
                if (!string.IsNullOrWhiteSpace(colorRgb))
                {
                    var parts = colorRgb.Split(',');
                    if (parts.Length == 3)
                    {
                        byte.TryParse(parts[0].Trim(), out r);
                        byte.TryParse(parts[1].Trim(), out g);
                        byte.TryParse(parts[2].Trim(), out b);
                    }
                }

                var cimText = new CIMTextGraphic
                {
                    Text = text,
                    Symbol = new CIMTextSymbol
                    {
                        FontSize = fontSize,
                        FontFamilyName = "Arial",
                        Color = new CIMRGBColor { R = r, G = g, B = b, Alpha = 100 },
                        BackgroundColor = new CIMRGBColor { Alpha = 0 },
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Middle,
                    },
                    Anchor = new AnchorPoint { X = x, Y = y },
                    Name = $"Text_{Guid.NewGuid():N}",
                };

                var element = new GraphicElement(cimText, layout);
                layout.AddElement(element);
                elementName = cimText.Name;
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, elementName, text });
        }

        private static async Task<IpcResponse> HandleAddLayoutPicture(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layoutName", out string layoutName) || string.IsNullOrWhiteSpace(layoutName) ||
                !req.Args.TryGetValue("imagePath", out string imagePath) || string.IsNullOrWhiteSpace(imagePath) ||
                !req.Args.TryGetValue("x", out string xStr) || !double.TryParse(xStr, out double x) ||
                !req.Args.TryGetValue("y", out string yStr) || !double.TryParse(yStr, out double y) ||
                !req.Args.TryGetValue("width", out string wStr) || !double.TryParse(wStr, out double width) ||
                !req.Args.TryGetValue("height", out string hStr) || !double.TryParse(hStr, out double height))
                return new IpcResponse(false, "args 'layoutName', 'imagePath', 'x', 'y', 'width', & 'height' required", null);

            if (!File.Exists(imagePath))
                return new IpcResponse(false, $"Image file not found: {imagePath}", null);

            string warning = null;

            await QueuedTask.Run(() =>
            {
                var layoutItem = Project.Current.GetItems<LayoutProjectItem>()
                    .FirstOrDefault(l => l.Name.Equals(layoutName, StringComparison.OrdinalIgnoreCase));
                if (layoutItem == null) { warning = "Layout not found"; return; }
                var layout = layoutItem.GetLayout();

                var bytes = File.ReadAllBytes(imagePath);
                var cimPicture = new CIMPictureElement
                {
                    Media = new CIMRasterData
                    {
                        SourceMediaType = MediaType.Image,
                        Data = bytes,
                        Size = new Size { Width = width, Height = height },
                    },
                    Anchor = new AnchorPoint { X = x, Y = y },
                    Name = $"Picture_{Guid.NewGuid():N}",
                };

                var element = new GraphicElement(cimPicture, layout);
                layout.AddElement(element);
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, imagePath });
        }

        private static async Task<IpcResponse> HandleAddLayoutLegend(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layoutName", out string layoutName) || string.IsNullOrWhiteSpace(layoutName) ||
                !req.Args.TryGetValue("x", out string xStr) || !double.TryParse(xStr, out double x) ||
                !req.Args.TryGetValue("y", out string yStr) || !double.TryParse(yStr, out double y))
                return new IpcResponse(false, "args 'layoutName', 'x', & 'y' required", null);

            req.Args.TryGetValue("mapFrameName", out string mapFrameName);

            string warning = null;

            await QueuedTask.Run(() =>
            {
                var layoutItem = Project.Current.GetItems<LayoutProjectItem>()
                    .FirstOrDefault(l => l.Name.Equals(layoutName, StringComparison.OrdinalIgnoreCase));
                if (layoutItem == null) { warning = "Layout not found"; return; }
                var layout = layoutItem.GetLayout();

                var mapFrame = string.IsNullOrWhiteSpace(mapFrameName)
                    ? layout.GetElementsOfType<MapFrame>().FirstOrDefault()
                    : layout.GetElementsOfType<MapFrame>()
                        .FirstOrDefault(mf => mf.Name.Equals(mapFrameName, StringComparison.OrdinalIgnoreCase));

                if (mapFrame == null) { warning = "MapFrame not found in layout"; return; }

                try
                {
                    mapFrame.AddMapSurround(MapSurroundType.Legend);
                }
                catch (Exception ex)
                {
                    warning = $"Failed to add legend: {ex.Message}";
                }
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, layoutName });
        }

        private static async Task<IpcResponse> HandleAddLayoutNorthArrow(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layoutName", out string layoutName) || string.IsNullOrWhiteSpace(layoutName) ||
                !req.Args.TryGetValue("mapFrameName", out string mapFrameName) || string.IsNullOrWhiteSpace(mapFrameName) ||
                !req.Args.TryGetValue("x", out string xStr) || !double.TryParse(xStr, out double x) ||
                !req.Args.TryGetValue("y", out string yStr) || !double.TryParse(yStr, out double y))
                return new IpcResponse(false, "args 'layoutName', 'mapFrameName', 'x', & 'y' required", null);

            string warning = null;

            await QueuedTask.Run(() =>
            {
                var layoutItem = Project.Current.GetItems<LayoutProjectItem>()
                    .FirstOrDefault(l => l.Name.Equals(layoutName, StringComparison.OrdinalIgnoreCase));
                if (layoutItem == null) { warning = "Layout not found"; return; }
                var layout = layoutItem.GetLayout();

                var mapFrame = layout.GetElementsOfType<MapFrame>()
                    .FirstOrDefault(mf => mf.Name.Equals(mapFrameName, StringComparison.OrdinalIgnoreCase));
                if (mapFrame == null) { warning = "MapFrame not found"; return; }

                try
                {
                    mapFrame.AddMapSurround(MapSurroundType.NorthArrow);
                }
                catch (Exception ex)
                {
                    warning = $"Failed to add north arrow: {ex.Message}";
                }
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null });
        }

        private static async Task<IpcResponse> HandleRemoveLayoutElement(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layoutName", out string layoutName) || string.IsNullOrWhiteSpace(layoutName) ||
                !req.Args.TryGetValue("elementName", out string elementName) || string.IsNullOrWhiteSpace(elementName))
                return new IpcResponse(false, "args 'layoutName' & 'elementName' required", null);

            string warning = null;

            await QueuedTask.Run(() =>
            {
                var layoutItem = Project.Current.GetItems<LayoutProjectItem>()
                    .FirstOrDefault(l => l.Name.Equals(layoutName, StringComparison.OrdinalIgnoreCase));
                if (layoutItem == null) { warning = "Layout not found"; return; }
                var layout = layoutItem.GetLayout();

                var element = layout.GetElementsOfType<Element>()
                    .FirstOrDefault(e => e.Name.Equals(elementName, StringComparison.OrdinalIgnoreCase));
                if (element == null) { warning = "Element not found"; return; }

                layout.DeleteElement(element);
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, elementName });
        }

        private static async Task<IpcResponse> HandleCreateLayout(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layoutName", out string layoutName) || string.IsNullOrWhiteSpace(layoutName) ||
                !req.Args.TryGetValue("width", out string wStr) || !double.TryParse(wStr, out double width) ||
                !req.Args.TryGetValue("height", out string hStr) || !double.TryParse(hStr, out double height))
                return new IpcResponse(false, "args 'layoutName', 'width', & 'height' required", null);

            req.Args.TryGetValue("units", out string units);
            if (string.IsNullOrWhiteSpace(units)) units = "MM";

            string warning = null;

            await QueuedTask.Run(async () =>
            {
                try
                {
                    var layout = LayoutFactory.CreateLayout(Project.Current, width, height, units);
                    layout.SetName(layoutName);
                    await Project.Current.SaveAsync();
                }
                catch (Exception ex)
                {
                    warning = $"Failed to create layout: {ex.Message}";
                }
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, layoutName, width, height, units });
        }

        private static async Task<IpcResponse> HandleCreateMap(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("mapName", out string mapName) || string.IsNullOrWhiteSpace(mapName) ||
                !req.Args.TryGetValue("mapType", out string mapTypeStr) || string.IsNullOrWhiteSpace(mapTypeStr))
                return new IpcResponse(false, "args 'mapName' & 'mapType' required", null);

            req.Args.TryGetValue("basemap", out string basemapName);

            string warning = null;

            await QueuedTask.Run(async () =>
            {
                MapType mapType;
                MapViewingMode viewingMode;
                switch (mapTypeStr.ToLowerInvariant())
                {
                    case "map":
                        mapType = MapType.Map;
                        viewingMode = MapViewingMode.Map;
                        break;
                    case "localscene":
                        mapType = MapType.Scene;
                        viewingMode = MapViewingMode.LocalScene;
                        break;
                    case "globalscene":
                        mapType = MapType.Scene;
                        viewingMode = MapViewingMode.GlobalScene;
                        break;
                    default:
                        warning = $"Unknown mapType '{mapTypeStr}'. Use Map, LocalScene, or GlobalScene.";
                        return;
                }

                try
                {
                    var map = MapFactory.CreateMap(mapName, mapType, viewingMode);

                    if (!string.IsNullOrWhiteSpace(basemapName))
                    {
                        var basemapItem = Project.Current.GetItems<BasemapProjectItem>()
                            .FirstOrDefault(b => b.Name.Equals(basemapName, StringComparison.OrdinalIgnoreCase));
                        if (basemapItem != null)
                            map.SetBasemap(basemapItem.Basemap);
                    }

                    try
                    {
                        var mapPane = await FrameworkApplication.Panes.CreateMapPaneAsync(map);
                        mapPane?.Activate();
                    }
                    catch
                    {
                        // Map created but pane could not be opened
                    }

                    await Project.Current.SaveAsync();
                }
                catch (Exception ex)
                {
                    warning = $"Failed to create map: {ex.Message}";
                }
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, mapName, mapType = mapTypeStr });
        }

        private static async Task<IpcResponse> HandleAddBasemap(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("basemapName", out string basemapName) || string.IsNullOrWhiteSpace(basemapName))
                return new IpcResponse(false, "arg 'basemapName' required", null);

            string warning = null;

            await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) { warning = "No active map"; return; }

                var basemapItem = Project.Current.GetItems<BasemapProjectItem>()
                    .FirstOrDefault(b => b.Name.Equals(basemapName, StringComparison.OrdinalIgnoreCase));
                if (basemapItem == null) { warning = $"Basemap '{basemapName}' not found. Available: Streets, Imagery, Topographic, Dark Gray Canvas, Light Gray Canvas, National Geographic, Ocean Basemap, OpenStreetMap"; return; }

                map.SetBasemap(basemapItem.Basemap);
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, basemapName });
        }

        // --- Phase 9: Advanced 3D & Visualization ---

        private static async Task<IpcResponse> HandleSetAtmosphere(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("fogDensity", out string fogStr) || !double.TryParse(fogStr, out double fogDensity))
                return new IpcResponse(false, "arg 'fogDensity' required", null);

            req.Args.TryGetValue("horizonFog", out string horizonFogStr);
            req.Args.TryGetValue("fogColor", out string fogColorStr);
            bool horizonFog = horizonFogStr?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;

            string warning = null;

            await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) { warning = "No active map"; return; }
                if (MapView.Active.ViewingMode != MapViewingMode.GlobalScene &&
                    MapView.Active.ViewingMode != MapViewingMode.LocalScene)
                { warning = "Active map is not a scene"; return; }

                var mapDef = map.GetDefinition();
                if (mapDef is not CIMScene cimScene) { warning = "Map definition is not a CIMScene"; return; }

                byte r = 200, g = 200, b = 200;
                if (!string.IsNullOrWhiteSpace(fogColorStr))
                {
                    var parts = fogColorStr.Split(',');
                    if (parts.Length == 3)
                    {
                        byte.TryParse(parts[0].Trim(), out r);
                        byte.TryParse(parts[1].Trim(), out g);
                        byte.TryParse(parts[2].Trim(), out b);
                    }
                }

                cimScene.Atmosphere = new CIMSceneAtmosphere
                {
                    FogDensity = fogDensity,
                    HorizonFog = horizonFog,
                    FogColor = new CIMRGBColor { R = r, G = g, B = b, Alpha = 100 },
                };

                map.SetDefinition(cimScene);
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, fogDensity });
        }

        private static async Task<IpcResponse> HandleSetSunPosition(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("azimuth", out string azStr) || !double.TryParse(azStr, out double azimuth) ||
                !req.Args.TryGetValue("altitude", out string altStr) || !double.TryParse(altStr, out double altitude))
                return new IpcResponse(false, "args 'azimuth' & 'altitude' required", null);

            string warning = null;

            await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) { warning = "No active map"; return; }
                if (MapView.Active.ViewingMode != MapViewingMode.GlobalScene &&
                    MapView.Active.ViewingMode != MapViewingMode.LocalScene)
                { warning = "Active map is not a scene"; return; }

                var mapDef = map.GetDefinition();
                if (mapDef is not CIMScene cimScene) { warning = "Map definition is not a CIMScene"; return; }

                cimScene.Lighting = new CIMSceneLighting
                {
                    SunAzimuth = azimuth,
                    SunAltitude = altitude,
                    SunEnabled = true,
                };

                map.SetDefinition(cimScene);
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, azimuth, altitude });
        }

        private static async Task<IpcResponse> HandleGetSunPosition(IpcRequest req, CancellationToken ct)
        {
            string warning = null;
            double? azimuth = null, altitude = null;

            await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) { warning = "No active map"; return; }
                if (MapView.Active.ViewingMode != MapViewingMode.GlobalScene &&
                    MapView.Active.ViewingMode != MapViewingMode.LocalScene)
                { warning = "Active map is not a scene"; return; }

                var mapDef = map.GetDefinition();
                if (mapDef is CIMScene cimScene && cimScene.Lighting != null)
                {
                    azimuth = cimScene.Lighting.SunAzimuth;
                    altitude = cimScene.Lighting.SunAltitude;
                }
            });

            if (warning != null) return new IpcResponse(false, warning, null);
            return new IpcResponse(true, null, new { azimuth, altitude, sunEnabled = true });
        }

        private static async Task<IpcResponse> HandleExplore3D(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("x", out string xStr) || !double.TryParse(xStr, out double x) ||
                !req.Args.TryGetValue("y", out string yStr) || !double.TryParse(yStr, out double y) ||
                !req.Args.TryGetValue("targetZ", out string tzStr) || !double.TryParse(tzStr, out double targetZ) ||
                !req.Args.TryGetValue("distance", out string distStr) || !double.TryParse(distStr, out double distance))
                return new IpcResponse(false, "args 'x', 'y', 'targetZ', & 'distance' required", null);

            req.Args.TryGetValue("headingDelta", out string hdStr);
            req.Args.TryGetValue("pitchDelta", out string pdStr);

            string warning = null;

            await QueuedTask.Run(async () =>
            {
                var mapView = MapView.Active;
                if (mapView == null) { warning = "No active map view"; return; }

                var camera = mapView.Camera;
                var target = MapPointBuilderEx.CreateMapPoint(x, y, targetZ);
                camera.SetLookAt(target);
                camera.Distance = distance;

                if (double.TryParse(hdStr, out double headingDelta))
                    camera.Heading += headingDelta;
                if (double.TryParse(pdStr, out double pitchDelta))
                    camera.Pitch += pitchDelta;

                await mapView.ZoomToAsync(camera, TimeSpan.FromMilliseconds(500));
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, x, y, targetZ, distance });
        }

        private static async Task<IpcResponse> HandleSetLayerElevation(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) || string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("elevationMode", out string modeStr) || string.IsNullOrWhiteSpace(modeStr) ||
                !req.Args.TryGetValue("zOffset", out string zStr) || !double.TryParse(zStr, out double zOffset))
                return new IpcResponse(false, "args 'layer', 'elevationMode', & 'zOffset' required", null);

            string warning = null;

            await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) { warning = "No active map"; return; }
                if (MapView.Active.ViewingMode != MapViewingMode.GlobalScene &&
                    MapView.Active.ViewingMode != MapViewingMode.LocalScene)
                { warning = "Active map is not a scene"; return; }

                int modeValue = modeStr.ToLowerInvariant() switch
                {
                    "absolute" => 2,
                    "relative" => 1,
                    "dra" => 0,
                    _ => -1,
                };
                if (modeValue < 0) { warning = "elevationMode must be 'absolute', 'relative', or 'dra'"; return; }

                var layer = map.Layers
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (layer == null) { warning = "Layer not found"; return; }

                var cimDef = layer.GetDefinition();
                if (cimDef is CIMFeatureLayer cimFl)
                {
                    cimFl.ElevationMode = modeValue;
                    cimFl.ElevationOffset = zOffset;
                    layer.SetDefinition(cimFl);
                }
                else
                {
                    warning = "Layer does not support elevation settings";
                }
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, layerName, elevationMode = modeStr, zOffset });
        }

        private static async Task<IpcResponse> HandleSetSceneBackground(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("r", out string rStr) || !byte.TryParse(rStr, out byte r) ||
                !req.Args.TryGetValue("g", out string gStr) || !byte.TryParse(gStr, out byte g) ||
                !req.Args.TryGetValue("b", out string bStr) || !byte.TryParse(bStr, out byte b))
                return new IpcResponse(false, "args 'r', 'g', & 'b' (0-255) required", null);

            req.Args.TryGetValue("backgroundType", out string bgType);

            string warning = null;

            await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) { warning = "No active map"; return; }
                if (MapView.Active.ViewingMode != MapViewingMode.GlobalScene &&
                    MapView.Active.ViewingMode != MapViewingMode.LocalScene)
                { warning = "Active map is not a scene"; return; }

                var mapDef = map.GetDefinition();
                if (mapDef is not CIMScene cimScene) { warning = "Map definition is not a CIMScene"; return; }

                string bgTypeLower = (bgType ?? "color").ToLowerInvariant();
                if (bgTypeLower == "none")
                {
                    cimScene.Background = null;
                }
                else
                {
                    cimScene.Background = new CIMSceneBackground
                    {
                        BackgroundType = SceneBackgroundType.Color,
                        Color = new CIMRGBColor { R = r, G = g, B = b, Alpha = 100 },
                    };
                }

                map.SetDefinition(cimScene);
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, backgroundType = bgType ?? "color" });
        }

        // --- Phase 10: Project & Data Management ---

        private static async Task<IpcResponse> HandleCreateFeatureClass(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("gdbPath", out string gdbPath) || string.IsNullOrWhiteSpace(gdbPath) ||
                !req.Args.TryGetValue("name", out string name) || string.IsNullOrWhiteSpace(name) ||
                !req.Args.TryGetValue("geometryType", out string geometryType) || string.IsNullOrWhiteSpace(geometryType))
                return new IpcResponse(false, "args 'gdbPath', 'name', & 'geometryType' required", null);

            req.Args.TryGetValue("wkid", out string wkidStr);
            int.TryParse(wkidStr, out int wkid);
            req.Args.TryGetValue("fieldsJson", out string fieldsJson);

            string warning = null;

            await QueuedTask.Run(async () =>
            {
                try
                {
                    var gpParams = new List<IGPValue> { GPValue.Create(gdbPath), GPValue.Create(name), GPValue.Create(geometryType) };
                    if (wkid > 0)
                        gpParams.Add(GPValue.Create(wkid));

                    var results = await Geoprocessing.ExecuteAsync("CreateFeatureclass", gpParams.ToArray());
                    if (results != null && results.Any(r => r.IsFailed))
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
                            var afParams = new List<IGPValue> { GPValue.Create(fcPath), GPValue.Create(fn), GPValue.Create(ft) };

                            if (f.TryGetProperty("fieldLength", out var fl) && fl.ValueKind == JsonValueKind.Number)
                                afParams.Add(GPValue.Create(fl.GetDouble()));

                            var afResults = await Geoprocessing.ExecuteAsync("AddField", afParams.ToArray());
                            if (afResults != null && afResults.Any(r => r.IsFailed))
                                warning = $"Failed to add field '{fn}'";
                        }
                    }
                }
                catch (Exception ex) { warning = $"CreateFeatureClass failed: {ex.Message}"; }
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
                    var results = await Geoprocessing.ExecuteAsync("Delete", GPValue.Create(path));
                    if (results != null && results.Any(r => r.IsFailed))
                        warning = "Delete operation failed";
                }
                catch (Exception ex) { warning = $"Delete failed: {ex.Message}"; }
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, path });
        }

        private static async Task<IpcResponse> HandleSaveProject(IpcRequest req, CancellationToken ct)
        {
            string warning = null;
            string savedPath = null;

            try
            {
                await Project.Current.SaveAsync();
                savedPath = Project.Current.Path;
            }
            catch (Exception ex) { warning = $"Save failed: {ex.Message}"; }

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, path = savedPath });
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

            await QueuedTask.Run(() =>
            {
                var fl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) { warning = "Layer not found"; return; }

                try
                {
                    using var fc = fl.GetFeatureClass();
                    var tableDef = fc.GetDefinition() as TableDefinition;
                    tableDef?.AddIndex(field, indexName, unique);
                }
                catch (Exception ex) { warning = $"Failed to add index: {ex.Message}"; }
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, field, indexName, unique });
        }

        private static async Task<IpcResponse> HandleSearchAddress(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("address", out string address) || string.IsNullOrWhiteSpace(address))
                return new IpcResponse(false, "arg 'address' required", null);

            req.Args.TryGetValue("maxResults", out string maxStr);
            if (!int.TryParse(maxStr, out int maxResults) || maxResults < 1) maxResults = 10;

            string warning = null;
            List<object> resultsList = null;

            await QueuedTask.Run(async () =>
            {
                var map = MapView.Active?.Map;
                if (map == null) { warning = "No active map"; return; }

                try
                {
                    var finder = new Finder(map);
                    var results = await finder.FindAsync(address);
                    if (results == null) { resultsList = new List<object>(); return; }

                    resultsList = results
                        .Take(maxResults)
                        .Select(r => new
                        {
                            name = r.Name,
                            type = r.Type.ToString(),
                            layer = r.LayerName,
                            score = r.Score,
                            x = r.Point?.X,
                            y = r.Point?.Y,
                        } as object)
                        .ToList();
                }
                catch (Exception ex) { warning = $"Search failed: {ex.Message}"; }
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { address, maxResults, resultCount = resultsList?.Count ?? 0, results = resultsList ?? new List<object>() });
        }

        private static async Task<IpcResponse> HandleOpenAttributeTable(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) || string.IsNullOrWhiteSpace(layerName))
                return new IpcResponse(false, "arg 'layer' required", null);

            string warning = null;

            await QueuedTask.Run(() =>
            {
                var fl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) { warning = "Layer not found"; return; }

                try
                {
                    var dockPane = FrameworkApplication.DockPaneManager.Find("esri_mapping_tableWindow");
                    if (dockPane == null) { warning = "Table dockpane not found"; return; }
                    dockPane.Activate();

                    var oidField = fl.GetFeatureClass().GetDefinition().GetObjectIDField();
                    var qf = new QueryFilter { WhereClause = $"{oidField} IS NOT NULL", SubFields = oidField };
                    using var cursor = fl.Search(qf, true);
                    if (cursor.MoveNext())
                    {
                        long oid = (long)cursor.Current[oidField];
                        MapView.Active.SelectFeatures(fl, new List<long> { oid });
                    }
                }
                catch (Exception ex) { warning = $"Failed to open attribute table: {ex.Message}"; }
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, layerName });
        }

        // --- Phase 11: Data Exchange ---

        private static async Task<IpcResponse> HandleExportToCsv(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) || string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("outputPath", out string outputPath) || string.IsNullOrWhiteSpace(outputPath))
                return new IpcResponse(false, "args 'layer' & 'outputPath' required", null);

            string warning = null;
            int rowCount = 0;

            await QueuedTask.Run(() =>
            {
                var fl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) { warning = "Layer not found"; return; }

                try
                {
                    using var fc = fl.GetFeatureClass();
                    var tableDef = fc.GetDefinition() as TableDefinition;
                    var fields = tableDef.GetFields().Where(f => !f.Name.Equals("SHAPE", StringComparison.OrdinalIgnoreCase) && f.Name != tableDef.GetObjectIDField()).ToList();

                    using var writer = new StreamWriter(outputPath);
                    writer.WriteLine(string.Join(",", fields.Select(f => EscapeCsvValue(f.Name))));

                    using var cursor = fc.Search(null, true);
                    while (cursor.MoveNext())
                    {
                        using var row = cursor.Current;
                        var values = fields.Select(f =>
                        {
                            var val = row[f.Name];
                            return val == null || val == DBNull.Value ? "" : EscapeCsvValue(val.ToString());
                        });
                        writer.WriteLine(string.Join(",", values));
                        rowCount++;
                    }
                }
                catch (Exception ex) { warning = $"Export failed: {ex.Message}"; }
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, rowCount, outputPath });
        }

        private static string EscapeCsvValue(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
                return $"\"{value.Replace("\"", "\"\"")}\"";
            return value;
        }

        private static async Task<IpcResponse> HandleExportToGeoJSON(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) || string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("outputPath", out string outputPath) || string.IsNullOrWhiteSpace(outputPath))
                return new IpcResponse(false, "args 'layer' & 'outputPath' required", null);

            string warning = null;

            await QueuedTask.Run(() =>
            {
                var fl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) { warning = "Layer not found"; return; }

                try
                {
                    using var fc = fl.GetFeatureClass();
                    var tableDef = fc.GetDefinition() as TableDefinition;
                    var fields = tableDef.GetFields().Where(f => !f.Name.Equals("SHAPE", StringComparison.OrdinalIgnoreCase)).ToList();
                    var shapeField = tableDef.GetFields().FirstOrDefault(f => f.Name.Equals("SHAPE", StringComparison.OrdinalIgnoreCase));

                    var features = new List<object>();
                    using var cursor = fc.Search(null, true);
                    while (cursor.MoveNext())
                    {
                        using var row = cursor.Current;
                        var props = new Dictionary<string, object>();
                        foreach (var f in fields)
                        {
                            var val = row[f.Name];
                            props[f.Name] = (val == null || val == DBNull.Value) ? null : val;
                        }

                        var geom = shapeField != null ? row[shapeField.Name] as Geometry : null;
                        features.Add(new { type = "Feature", geometry = geom != null ? GeoJsonFromGeometry(geom) : null, properties = props });
                    }

                    var fcGeoJson = new { type = "FeatureCollection", features };
                    var options = new JsonSerializerOptions { WriteIndented = true };
                    File.WriteAllText(outputPath, JsonSerializer.Serialize(fcGeoJson, options));
                }
                catch (Exception ex) { warning = $"Export failed: {ex.Message}"; }
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, outputPath });
        }

        private static object GeoJsonFromGeometry(Geometry geom)
        {
            if (geom is MapPoint pt)
                return new { type = "Point", coordinates = new[] { pt.X, pt.Y } };

            if (geom is Multipoint mpt)
            {
                var coords = mpt.Points.Select(p => new[] { p.X, p.Y }).ToArray();
                return new { type = "MultiPoint", coordinates = coords };
            }

            if (geom is Polygon polygon)
            {
                var rings = new List<double[][]>();
                for (int i = 0; i < polygon.PartCount; i++)
                {
                    using var part = polygon.GetPart(i);
                    var coords = part.Select(p => new[] { p.X, p.Y }).ToArray();
                    rings.Add(coords);
                }
                return new { type = "Polygon", coordinates = rings };
            }

            if (geom is Polyline polyline)
            {
                if (polyline.PartCount == 1)
                {
                    using var part = polyline.GetPart(0);
                    var coords = part.Select(p => new[] { p.X, p.Y }).ToArray();
                    return new { type = "LineString", coordinates = coords };
                }

                var lines = new List<double[][]>();
                for (int i = 0; i < polyline.PartCount; i++)
                {
                    using var part = polyline.GetPart(i);
                    var coords = part.Select(p => new[] { p.X, p.Y }).ToArray();
                    lines.Add(coords);
                }
                return new { type = "MultiLineString", coordinates = lines };
            }

            return null;
        }

        private static async Task<IpcResponse> HandleImportCsv(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("csvPath", out string csvPath) || string.IsNullOrWhiteSpace(csvPath) ||
                !req.Args.TryGetValue("gdbPath", out string gdbPath) || string.IsNullOrWhiteSpace(gdbPath) ||
                !req.Args.TryGetValue("fcName", out string fcName) || string.IsNullOrWhiteSpace(fcName) ||
                !req.Args.TryGetValue("xField", out string xField) || string.IsNullOrWhiteSpace(xField) ||
                !req.Args.TryGetValue("yField", out string yField) || string.IsNullOrWhiteSpace(yField))
                return new IpcResponse(false, "args 'csvPath', 'gdbPath', 'fcName', 'xField', & 'yField' required", null);

            req.Args.TryGetValue("wkid", out string wkidStr);
            int.TryParse(wkidStr, out int wkid);
            if (wkid == 0) wkid = 4326;

            if (!File.Exists(csvPath))
                return new IpcResponse(false, $"CSV file not found: {csvPath}", null);

            string warning = null;
            int rowCount = 0;

            await QueuedTask.Run(async () =>
            {
                var fcPath = System.IO.Path.Combine(gdbPath, fcName);
                bool fcExists = false;

                try
                {
                    using var geodb = new Geodatabase(new FileGeodatabaseConnectionPath(new Uri(gdbPath)));
                    fcExists = geodb.GetDefinitions<FeatureClassDefinition>().Any(d => d.GetName().Equals(fcName, StringComparison.OrdinalIgnoreCase));
                }
                catch { }

                if (!fcExists)
                {
                    var gpResult = await Geoprocessing.ExecuteAsync("CreateFeatureclass",
                        GPValue.Create(gdbPath), GPValue.Create(fcName), GPValue.Create("POINT"));
                    if (gpResult != null && gpResult.Any(r => r.IsFailed))
                    { warning = "Failed to create feature class"; return; }
                }

                try
                {
                    var sr = SpatialReferenceBuilder.CreateSpatialReference(wkid);
                    using var geodb = new Geodatabase(new FileGeodatabaseConnectionPath(new Uri(gdbPath)));
                    using var fc = geodb.OpenDataset<FeatureClass>(fcName);
                    using var insertCursor = fc.Insert();
                    var buffer = fc.CreateRowBuffer();

                    var lines = File.ReadAllLines(csvPath);
                    if (lines.Length < 2) { rowCount = 0; return; }

                    var headers = ParseCsvLine(lines[0]);
                    int xIdx = -1, yIdx = -1;
                    for (int i = 0; i < headers.Length; i++)
                    {
                        if (headers[i].Trim().Equals(xField, StringComparison.OrdinalIgnoreCase)) xIdx = i;
                        if (headers[i].Trim().Equals(yField, StringComparison.OrdinalIgnoreCase)) yIdx = i;
                    }
                    if (xIdx < 0) { warning = $"X field '{xField}' not found in CSV headers"; return; }
                    if (yIdx < 0) { warning = $"Y field '{yField}' not found in CSV headers"; return; }

                    for (int r = 1; r < lines.Length; r++)
                    {
                        if (string.IsNullOrWhiteSpace(lines[r])) continue;
                        var cols = ParseCsvLine(lines[r]);
                        if (!double.TryParse(cols[xIdx], out double x) || !double.TryParse(cols[yIdx], out double y))
                            continue;

                        var pt = MapPointBuilderEx.CreateMapPoint(x, y, sr);
                        buffer["SHAPE"] = pt;

                        for (int i = 0; i < headers.Length; i++)
                        {
                            if (i == xIdx || i == yIdx || i >= cols.Length) continue;
                            try { buffer[headers[i].Trim()] = cols[i]; } catch { }
                        }

                        insertCursor.Insert(buffer);
                        rowCount++;
                    }

                    insertCursor.Flush();
                }
                catch (Exception ex) { warning = $"Import failed: {ex.Message}"; }
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, rowCount, fcName });
        }

        private static string[] ParseCsvLine(string line)
        {
            var result = new List<string>();
            bool inQuotes = false;
            var current = new System.Text.StringBuilder();
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                    else inQuotes = !inQuotes;
                }
                else if (c == ',' && !inQuotes) { result.Add(current.ToString()); current.Clear(); }
                else current.Append(c);
            }
            result.Add(current.ToString());
            return result.ToArray();
        }

        private static async Task<IpcResponse> HandleExportToShapefile(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) || string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("outputPath", out string outputPath) || string.IsNullOrWhiteSpace(outputPath))
                return new IpcResponse(false, "args 'layer' & 'outputPath' required", null);

            string warning = null;

            await QueuedTask.Run(async () =>
            {
                var fl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) { warning = "Layer not found"; return; }

                try
                {
                    using var fc = fl.GetFeatureClass();
                    var workspacePath = fc.GetDatastore().GetPath();
                    var fcName = fc.GetName();
                    var fcPath = System.IO.Path.Combine(workspacePath, fcName);

                    var results = await Geoprocessing.ExecuteAsync("CopyFeatures",
                        GPValue.Create(fcPath), GPValue.Create(outputPath));
                    if (results != null && results.Any(r => r.IsFailed))
                        warning = "CopyFeatures failed";
                }
                catch (Exception ex) { warning = $"Export failed: {ex.Message}"; }
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, outputPath });
        }

        private static async Task<IpcResponse> HandleExportToKml(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) || string.IsNullOrWhiteSpace(layerName) ||
                !req.Args.TryGetValue("outputPath", out string outputPath) || string.IsNullOrWhiteSpace(outputPath))
                return new IpcResponse(false, "args 'layer' & 'outputPath' required", null);

            string warning = null;

            await QueuedTask.Run(async () =>
            {
                var fl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) { warning = "Layer not found"; return; }

                try
                {
                    using var fc = fl.GetFeatureClass();
                    var workspacePath = fc.GetDatastore().GetPath();
                    var fcName = fc.GetName();
                    var fcPath = System.IO.Path.Combine(workspacePath, fcName);

                    var results = await Geoprocessing.ExecuteAsync("LayerToKML",
                        GPValue.Create(fcPath), GPValue.Create(outputPath));
                    if (results != null && results.Any(r => r.IsFailed))
                        warning = "LayerToKML failed";
                }
                catch (Exception ex) { warning = $"Export failed: {ex.Message}"; }
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, outputPath });
        }

        private static async Task<IpcResponse> HandleImportGeoJSON(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("geojsonPath", out string geojsonPath) || string.IsNullOrWhiteSpace(geojsonPath) ||
                !req.Args.TryGetValue("gdbPath", out string gdbPath) || string.IsNullOrWhiteSpace(gdbPath) ||
                !req.Args.TryGetValue("fcName", out string fcName) || string.IsNullOrWhiteSpace(fcName))
                return new IpcResponse(false, "args 'geojsonPath', 'gdbPath', & 'fcName' required", null);

            if (!File.Exists(geojsonPath))
                return new IpcResponse(false, $"GeoJSON file not found: {geojsonPath}", null);

            string warning = null;
            int rowCount = 0;

            await QueuedTask.Run(async () =>
            {
                var fcPath = System.IO.Path.Combine(gdbPath, fcName);
                bool fcExists = false;

                try
                {
                    using var geodb = new Geodatabase(new FileGeodatabaseConnectionPath(new Uri(gdbPath)));
                    fcExists = geodb.GetDefinitions<FeatureClassDefinition>().Any(d => d.GetName().Equals(fcName, StringComparison.OrdinalIgnoreCase));
                }
                catch { }

                if (!fcExists)
                {
                    var gpResult = await Geoprocessing.ExecuteAsync("CreateFeatureclass",
                        GPValue.Create(gdbPath), GPValue.Create(fcName), GPValue.Create("POINT"));
                    if (gpResult != null && gpResult.Any(r => r.IsFailed))
                    { warning = "Failed to create feature class"; return; }
                }

                try
                {
                    using var geodb = new Geodatabase(new FileGeodatabaseConnectionPath(new Uri(gdbPath)));
                    using var fc = geodb.OpenDataset<FeatureClass>(fcName);
                    using var insertCursor = fc.Insert();
                    var buffer = fc.CreateRowBuffer();
                    var fcFields = (fc.GetDefinition() as TableDefinition).GetFields().Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

                    using var doc = JsonDocument.Parse(File.ReadAllText(geojsonPath));
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("features", out var featuresEl))
                    { warning = "GeoJSON has no 'features' array"; return; }

                    var sr = SpatialReferenceBuilder.CreateSpatialReference(4326);

                    foreach (var featureEl in featuresEl.EnumerateArray())
                    {
                        var geom = GeoJsonToGeometry(featureEl.TryGetProperty("geometry", out var g) ? g : default, sr);
                        if (geom == null) continue;

                        buffer["SHAPE"] = geom;

                        if (featureEl.TryGetProperty("properties", out var propsEl))
                        {
                            foreach (var prop in propsEl.EnumerateObject())
                            {
                                if (fcFields.Contains(prop.Name) && prop.Value.ValueKind != JsonValueKind.Object && prop.Value.ValueKind != JsonValueKind.Array)
                                {
                                    try
                                    {
                                        if (prop.Value.ValueKind == JsonValueKind.String) buffer[prop.Name] = prop.Value.GetString();
                                        else if (prop.Value.ValueKind == JsonValueKind.Number) buffer[prop.Name] = prop.Value.GetDouble();
                                        else if (prop.Value.ValueKind == JsonValueKind.True || prop.Value.ValueKind == JsonValueKind.False) buffer[prop.Name] = prop.Value.GetBoolean() ? 1 : 0;
                                    }
                                    catch { }
                                }
                            }
                        }

                        insertCursor.Insert(buffer);
                        rowCount++;
                    }

                    insertCursor.Flush();
                }
                catch (Exception ex) { warning = $"Import failed: {ex.Message}"; }
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, rowCount, fcName });
        }

        private static Geometry GeoJsonToGeometry(JsonElement geomEl, SpatialReference sr)
        {
            if (geomEl.ValueKind != JsonValueKind.Object) return null;
            if (!geomEl.TryGetProperty("type", out var typeEl)) return null;

            string type = typeEl.GetString();
            if (!geomEl.TryGetProperty("coordinates", out var coordsEl)) return null;

            if (type == "Point")
            {
                var arr = coordsEl.EnumerateArray().Select(e => e.GetDouble()).ToArray();
                return arr.Length >= 2 ? MapPointBuilderEx.CreateMapPoint(arr[0], arr[1], sr) : null;
            }

            if (type == "MultiPoint")
            {
                var pts = coordsEl.EnumerateArray().Select(p => { var a = p.EnumerateArray().Select(e => e.GetDouble()).ToArray(); return MapPointBuilderEx.CreateMapPoint(a[0], a[1], sr); }).ToList();
                return MultipointBuilderEx.CreateMultipoint(pts);
            }

            if (type == "LineString")
            {
                var builder = new PolylineBuilderEx(sr);
                var pts = coordsEl.EnumerateArray().Select(p => { var a = p.EnumerateArray().Select(e => e.GetDouble()).ToArray(); return MapPointBuilderEx.CreateMapPoint(a[0], a[1], sr); }).ToList();
                builder.AddPart(pts);
                return builder.ToGeometry();
            }

            if (type == "MultiLineString")
            {
                var builder = new PolylineBuilderEx(sr);
                foreach (var line in coordsEl.EnumerateArray())
                {
                    var pts = line.EnumerateArray().Select(p => { var a = p.EnumerateArray().Select(e => e.GetDouble()).ToArray(); return MapPointBuilderEx.CreateMapPoint(a[0], a[1], sr); }).ToList();
                    builder.AddPart(pts);
                }
                return builder.ToGeometry();
            }

            if (type == "Polygon")
            {
                var builder = new PolygonBuilderEx(sr);
                foreach (var ring in coordsEl.EnumerateArray())
                {
                    var pts = ring.EnumerateArray().Select(p => { var a = p.EnumerateArray().Select(e => e.GetDouble()).ToArray(); return MapPointBuilderEx.CreateMapPoint(a[0], a[1], sr); }).ToList();
                    builder.AddPart(pts);
                }
                return builder.ToGeometry();
            }

            return null;
        }

        // --- Phase 12: Pro GUI Automation ---

        private static Task<IpcResponse> HandleShowMessage(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("message", out string message) || string.IsNullOrWhiteSpace(message))
                return Task.FromResult(new IpcResponse(false, "arg 'message' required", null));

            req.Args.TryGetValue("type", out string type);
            req.Args.TryGetValue("title", out string title);
            if (string.IsNullOrWhiteSpace(title)) title = "ArcGIS Pro";

            try
            {
                var icon = type?.Equals("error", StringComparison.OrdinalIgnoreCase) == true ? MessageBoxImage.Error
                         : type?.Equals("warning", StringComparison.OrdinalIgnoreCase) == true ? MessageBoxImage.Warning
                         : MessageBoxImage.Information;
                System.Windows.MessageBox.Show(message, title, MessageBoxButton.OK, icon);
                return Task.FromResult(new IpcResponse(true, null, new { done = true, type = type ?? "info" }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(new IpcResponse(false, $"Failed to show message: {ex.Message}", null));
            }
        }

        private static Task<IpcResponse> HandleShowProgressDialog(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("title", out string title) || string.IsNullOrWhiteSpace(title) ||
                !req.Args.TryGetValue("message", out string message) || string.IsNullOrWhiteSpace(message))
                return Task.FromResult(new IpcResponse(false, "args 'title' & 'message' required", null));

            try
            {
                System.Windows.MessageBox.Show(message, $"⏳ {title}", MessageBoxButton.OK, MessageBoxImage.Information);
                return Task.FromResult(new IpcResponse(true, null, new { done = true }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(new IpcResponse(false, $"Failed to show progress dialog: {ex.Message}", null));
            }
        }

        private static async Task<IpcResponse> HandleSetStatusBarProgress(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("percent", out string pctStr) || !int.TryParse(pctStr, out int percent) ||
                !req.Args.TryGetValue("message", out string message))
                return new IpcResponse(false, "args 'percent' & 'message' required", null);

            await QueuedTask.Run(() =>
            {
                try
                {
                    FrameworkApplication.StatusBar.SetProgressBar(percent, 100, message);
                }
                catch
                {
                    var prefix = percent >= 0 && percent <= 100 ? $"[{percent}%] " : "";
                    FrameworkApplication.StatusBar.SetStatusBarText($"{prefix}{message}");
                }
            });

            return new IpcResponse(true, null, new { done = true, percent, message });
        }

        private static Task<IpcResponse> HandleListDockpanes(IpcRequest req, CancellationToken ct)
        {
            var dockpanes = _knownDockPanes.Select(kvp => new { name = kvp.Key, damlId = kvp.Value }).ToList();
            return Task.FromResult(new IpcResponse(true, null, new { dockpaneCount = dockpanes.Count, dockpanes }));
        }

        private static async Task<IpcResponse> HandleActivateRibbonTab(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("tabId", out string tabId) || string.IsNullOrWhiteSpace(tabId))
                return new IpcResponse(false, "arg 'tabId' required", null);

            bool found = false;
            await QueuedTask.Run(() =>
            {
                string resolvedId = _knownRibbonTabs.TryGetValue(tabId, out string rid) ? rid : tabId;
                try
                {
                    FrameworkApplication.ActivateTab(resolvedId);
                    found = true;
                }
                catch { }
            });

            return new IpcResponse(found, found ? null : $"Ribbon tab not found: {tabId}", new { done = found, tabId });
        }

        // --- Phase 13: Schema Management ---

        private static async Task<IpcResponse> HandleListDomains(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("gdbPath", out string gdbPath) || string.IsNullOrWhiteSpace(gdbPath))
                return new IpcResponse(false, "arg 'gdbPath' required", null);

            string warning = null;
            List<object> domains = null;

            await QueuedTask.Run(() =>
            {
                try
                {
                    using var geodb = new Geodatabase(new FileGeodatabaseConnectionPath(new Uri(gdbPath)));
                    domains = geodb.GetDomains().Select(d =>
                    {
                        if (d is CodedValueDomain cv)
                            return (object)new { name = d.Name, type = "CodedValue", fieldType = d.FieldType.ToString(), description = d.Description ?? "", codedValues = cv.CodedValues };
                        else if (d is RangeDomain rd)
                            return (object)new { name = d.Name, type = "Range", fieldType = d.FieldType.ToString(), description = d.Description ?? "", minValue = rd.MinValue?.ToString(), maxValue = rd.MaxValue?.ToString() };
                        else
                            return (object)new { name = d.Name, type = d.DomainType.ToString(), fieldType = d.FieldType.ToString(), description = d.Description ?? "" };
                    }).ToList();
                }
                catch (Exception ex) { warning = $"Failed to list domains: {ex.Message}"; }
            });

            if (warning != null) return new IpcResponse(false, warning, null);
            return new IpcResponse(true, null, new { domainCount = domains?.Count ?? 0, domains = domains ?? new List<object>() });
        }

        private static async Task<IpcResponse> HandleCreateDomain(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("gdbPath", out string gdbPath) || string.IsNullOrWhiteSpace(gdbPath) ||
                !req.Args.TryGetValue("name", out string name) || string.IsNullOrWhiteSpace(name) ||
                !req.Args.TryGetValue("description", out string description) || description == null ||
                !req.Args.TryGetValue("fieldType", out string fieldType) || string.IsNullOrWhiteSpace(fieldType))
                return new IpcResponse(false, "args 'gdbPath', 'name', 'description', & 'fieldType' required", null);

            req.Args.TryGetValue("codedValues", out string codedValuesJson);

            string warning = null;

            await QueuedTask.Run(() =>
            {
                try
                {
                    using var geodb = new Geodatabase(new FileGeodatabaseConnectionPath(new Uri(gdbPath)));
                    var schemaBuilder = new SchemaBuilder(geodb);

                    if (!string.IsNullOrWhiteSpace(codedValuesJson))
                    {
                        var cvDict = JsonSerializer.Deserialize<Dictionary<string, string>>(codedValuesJson);
                        var domain = new CodedValueDomain
                        {
                            Name = name,
                            Description = description,
                            FieldType = (FieldType)Enum.Parse(typeof(FieldType), fieldType, ignoreCase: true),
                            CodedValues = cvDict,
                        };
                        schemaBuilder.AddDomain(domain);
                    }
                    else
                    {
                        var domain = new RangeDomain
                        {
                            Name = name,
                            Description = description,
                            FieldType = (FieldType)Enum.Parse(typeof(FieldType), fieldType, ignoreCase: true),
                        };
                        schemaBuilder.AddDomain(domain);
                    }

                    schemaBuilder.Build();
                }
                catch (Exception ex) { warning = $"Failed to create domain: {ex.Message}"; }
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, name, fieldType });
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
                var fl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) { warning = "Layer not found"; return; }

                try
                {
                    using var fc = fl.GetFeatureClass();
                    var workspacePath = fc.GetDatastore().GetPath();
                    var fcPath = System.IO.Path.Combine(workspacePath, fc.GetName());

                    var results = await Geoprocessing.ExecuteAsync("AssignDomainToField",
                        GPValue.Create(fcPath), GPValue.Create(fieldName), GPValue.Create(domainName));
                    if (results != null && results.Any(r => r.IsFailed))
                        warning = "AssignDomainToField failed";
                }
                catch (Exception ex) { warning = $"Failed to assign domain: {ex.Message}"; }
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
                var fl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) { warning = "Layer not found"; return; }

                try
                {
                    using var fc = fl.GetFeatureClass();
                    var tableDef = fc.GetDefinition() as TableDefinition;
                    if (tableDef == null) { warning = "Cannot get table definition"; return; }

                    var subtypeField = tableDef.SubtypeField;
                    var subtypes = tableDef.Subtypes?.Select(s => new
                    {
                        code = s.Key,
                        description = s.Value.Description,
                        defaultValueCount = s.Value.DefaultValues?.Count ?? 0,
                    }).ToList();

                    result = new { subtypeField = subtypeField ?? "", subtypeCount = subtypes?.Count ?? 0, subtypes = subtypes ?? new List<object>() };
                }
                catch (Exception ex) { warning = $"Failed to list subtypes: {ex.Message}"; }
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
                var fl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) { warning = "Layer not found"; return; }

                try
                {
                    using var fc = fl.GetFeatureClass();
                    var workspacePath = fc.GetDatastore().GetPath();
                    var fcPath = System.IO.Path.Combine(workspacePath, fc.GetName());

                    var results = await Geoprocessing.ExecuteAsync("SetSubtypeField",
                        GPValue.Create(fcPath), GPValue.Create(fieldName));
                    if (results != null && results.Any(r => r.IsFailed))
                        warning = "SetSubtypeField failed";
                }
                catch (Exception ex) { warning = $"Failed to set subtype field: {ex.Message}"; }
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
                var fl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) { warning = "Layer not found"; return; }

                try
                {
                    using var fc = fl.GetFeatureClass();
                    var workspacePath = fc.GetDatastore().GetPath();
                    var fcPath = System.IO.Path.Combine(workspacePath, fc.GetName());

                    var results = await Geoprocessing.ExecuteAsync("EnableAttachments",
                        GPValue.Create(fcPath));
                    if (results != null && results.Any(r => r.IsFailed))
                        warning = "EnableAttachments failed";
                }
                catch (Exception ex) { warning = $"Failed to enable attachments: {ex.Message}"; }
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, layerName });
        }

        // --- Phase 14: Advanced Geoprocessing ---

        private static async Task<IpcResponse> HandleListToolboxes(IpcRequest req, CancellationToken ct)
        {
            object result = null;
            await QueuedTask.Run(() =>
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var toolboxes = new List<object>();

                foreach (var tb in Project.Current.GetItems<ToolboxProjectItem>())
                {
                    seen.Add(tb.Path);
                    toolboxes.Add(new
                    {
                        name = tb.Name,
                        path = tb.Path,
                        type = "Project",
                        toolCount = tb.GetToolboxItems().Count(),
                    });
                }

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

            if (!pyResult.success) return pyResult;
            return new IpcResponse(true, null, new { toolName, parameters = pyResult.data });
        }

        private static async Task<IpcResponse> HandleGetGeoprocessingHistory(IpcRequest req, CancellationToken ct)
        {
            req.Args?.TryGetValue("count", out string countStr);
            int.TryParse(countStr, out int count);
            if (count <= 0) count = 20;

            object result = null;
            await QueuedTask.Run(async () =>
            {
                var history = await Geoprocessing.GetHistoryAsync();
                var items = history?
                    .OrderByDescending(h => h.StartTime)
                    .Take(count)
                    .Select(h => new
                    {
                        toolName = h.ToolName,
                        status = h.Status.ToString(),
                        startTime = h.StartTime.ToString("O"),
                        duration = h.Duration?.ToString(),
                        messageCount = h.Messages?.Count ?? 0,
                    })
                    .ToList();

                result = new { totalCount = history?.Count ?? 0, items = items ?? new List<object>() };
            });

            return new IpcResponse(true, null, result);
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

        private static async Task<IpcResponse> HandleSetEnvironment(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("key", out string key) || string.IsNullOrWhiteSpace(key) ||
                !req.Args.TryGetValue("value", out string valueStr))
                return new IpcResponse(false, "args 'key' & 'value' required", null);

            string warning = null;
            await QueuedTask.Run(() =>
            {
                try { Geoprocessing.SetEnvironmentValue(key, valueStr); }
                catch (Exception ex) { warning = $"Failed to set environment: {ex.Message}"; }
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { key, value = valueStr });
        }

        private static async Task<IpcResponse> HandleGetEnvironment(IpcRequest req, CancellationToken ct)
        {
            req.Args?.TryGetValue("key", out string key);

            object result = null;
            await QueuedTask.Run(() =>
            {
                if (!string.IsNullOrWhiteSpace(key))
                {
                    var val = Geoprocessing.GetEnvironmentValue(key);
                    result = new { key, value = val?.ToString() ?? "" };
                }
                else
                {
                    var envKeys = new[] { "workspace", "scratchWorkspace", "extent", "cellSize", "mask", "outputCoordinateSystem", "overwriteOutput", "snapRaster", "maintainSpatialIndex", "parallelProcessingFactor", "pyramidLevel", "tileSize" };
                    var values = envKeys.Select(k => new { key = k, value = Geoprocessing.GetEnvironmentValue(k)?.ToString() ?? "" }).ToList();
                    result = new { count = values.Count, environments = values };
                }
            });

            return new IpcResponse(true, null, result);
        }

        private static string FindProPythonExe()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\ESRI\ArcGISPro");
                if (key?.GetValue("InstallDir") is string installDir)
                {
                    var exe = System.IO.Path.Combine(installDir, "bin", "Python", "envs", "arcgispro-py3", "python.exe");
                    if (System.IO.File.Exists(exe)) return exe;
                }
            }
            catch { }

            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var fallback = System.IO.Path.Combine(pf, @"ArcGIS\Pro\bin\Python\envs\arcgispro-py3\python.exe");
            if (System.IO.File.Exists(fallback)) return fallback;

            return null;
        }

        private static async Task<IpcResponse> RunProPythonAsync(string code, int timeoutSeconds, CancellationToken ct)
        {
            var pythonExe = FindProPythonExe();
            if (pythonExe == null)
                return new IpcResponse(false, "ArcGIS Pro Python interpreter not found", null);

            var tempFile = System.IO.Path.GetTempFileName() + ".py";
            await System.IO.File.WriteAllTextAsync(tempFile, code, ct);

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = pythonExe,
                    Arguments = $"\"{tempFile}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };

                using var process = new System.Diagnostics.Process { StartInfo = psi };
                process.Start();

                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                if (process.WaitForExit(timeoutSeconds * 1000))
                {
                    var stdout = await stdoutTask;
                    var stderr = await stderrTask;
                    return new IpcResponse(true, null, new { stdout, stderr, exitCode = process.ExitCode });
                }
                else
                {
                    process.Kill();
                    return new IpcResponse(false, "Python script timed out", null);
                }
            }
            finally
            {
                System.IO.File.Delete(tempFile);
            }
        }

        private static Geometry ParseGeometryFromJson(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeEl)) return null;
            string geomType = typeEl.GetString();

            if (geomType == "polyline" || geomType == "Polyline")
            {
                if (!root.TryGetProperty("paths", out var pathsEl)) return null;
                var builder = new PolylineBuilderEx();
                var sr = SpatialReferenceBuilder.CreateSpatialReference(28356);
                foreach (var path in pathsEl.EnumerateArray())
                {
                    var pts = new List<MapPoint>();
                    foreach (var pt in path.EnumerateArray())
                    {
                        var arr = pt.EnumerateArray().Select(e => e.GetDouble()).ToArray();
                        pts.Add(MapPointBuilderEx.CreateMapPoint(arr[0], arr[1], sr));
                    }
                    builder.AddPart(pts);
                }
                return builder.ToGeometry();
            }

            if (geomType == "polygon" || geomType == "Polygon")
            {
                if (!root.TryGetProperty("rings", out var ringsEl)) return null;
                var builder = new PolygonBuilderEx();
                var sr = SpatialReferenceBuilder.CreateSpatialReference(28356);
                foreach (var ring in ringsEl.EnumerateArray())
                {
                    var pts = new List<MapPoint>();
                    foreach (var pt in ring.EnumerateArray())
                    {
                        var arr = pt.EnumerateArray().Select(e => e.GetDouble()).ToArray();
                        pts.Add(MapPointBuilderEx.CreateMapPoint(arr[0], arr[1], sr));
                    }
                    builder.AddPart(pts);
                }
                return builder.ToGeometry();
            }

            return null;
        }
    }
}
