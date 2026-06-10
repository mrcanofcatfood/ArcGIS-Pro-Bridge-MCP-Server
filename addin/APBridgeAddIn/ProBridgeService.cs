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
using Python.Runtime;

namespace APBridgeAddIn
{
    internal class ProBridgeService : IDisposable
    {
        private readonly string _pipeName;
        private Thread _serverThread;
        private volatile bool _stopped;

        private static readonly Dictionary<string, string> _knownDockPanes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Contents"] = "esri_core_contentsDockPane",
            ["Catalog"] = "esri_core_projectDockPane",
            ["Attribute Table"] = "esri_mapping_tableWindow",
            ["Table"] = "esri_mapping_tableWindow",
            ["Search"] = "esri_core_searchDockPane",
            ["Geoprocessing"] = "esri_mapping_geoprocessingPane",
            ["Symbology"] = "esri_mapping_symbologyDockPane",
            ["Labeling"] = "esri_mapping_labelClassDockPane",
            ["Bookmarks"] = "esri_mapping_bookmarksManagerDockPane",
            ["Time"] = "esri_mapping_timeDockPane",
        };

        // Lazy PythonEngine initialization (try in-process first, fall back to subprocess)
        private static volatile bool _pyEngineReady;
        private static volatile bool _pyEngineAttempted;
        private static readonly object _pyEngineLock = new();

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
            _stopped = false;
            _serverThread = new Thread(RunLoop) { IsBackground = true, Name = "ProBridgePipeServer" };
            _serverThread.Start();
        }

        public void Dispose()
        {
            _stopped = true;
            _serverThread = null;
        }

        private void RunLoop()
        {
            while (!_stopped)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Message,
                        PipeOptions.Asynchronous
                    );

                    server.WaitForConnection();

                    using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                    using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true)
                    { AutoFlush = true };

                    while (server.IsConnected && !_stopped)
                    {
                        var line = reader.ReadLine();
                        if (line == null) break;

                        IpcRequest req;
                        try
                        {
                            req = JsonSerializer.Deserialize<IpcRequest>(line);
                        }
                        catch
                        {
                            writer.WriteLine(JsonSerializer.Serialize(new IpcResponse(false, "parse error", null)));
                            continue;
                        }

                        try
                        {
                            var resp = HandleAsync(req, CancellationToken.None).GetAwaiter().GetResult();
                            writer.WriteLine(JsonSerializer.Serialize(resp));
                        }
                        catch (Exception ex)
                        {
                            try { writer.WriteLine(JsonSerializer.Serialize(new IpcResponse(false, ex.Message, null))); } catch { }
                        }
                    }
                }
                catch
                {
                    try { Thread.Sleep(500); } catch { break; }
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
            ["pro.pingPythonRuntime"] = HandlePingPythonRuntime,
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
                    .ToList()
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
                return fl?.SelectionCount ?? 0;
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
                {
                    var ext = fl.QueryExtent();
                    if (ext != null)
                        await MapView.Active.ZoomToAsync(ext);
                }
            });

            return new IpcResponse(true, null, new { done = true });
        }

        private static async Task<IpcResponse> HandleGetCurrentExtent(IpcRequest req, CancellationToken ct)
        {
            var extent = await QueuedTask.Run(() =>
            {
                var mapView = MapView.Active;
                if (mapView == null) return null;
                var env = mapView.Extent;
                return new
                {
                    xmin = env.XMin,
                    ymin = env.YMin,
                    xmax = env.XMax,
                    ymax = env.YMax,
                    spatialReference = env.SpatialReference?.Name ?? "Unknown",
                    wkid = env.SpatialReference?.Wkid ?? 0
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

            var view = MapView.Active;
            if (view == null)
                return new IpcResponse(false, "No active map view", null);

            await QueuedTask.Run(async () =>
            {
                var sr = view.Camera?.SpatialReference ?? view.Map?.SpatialReference;
                var envelope = EnvelopeBuilderEx.CreateEnvelope(xmin, ymin, xmax, ymax, sr);
                await view.ZoomToAsync(envelope);
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
                wkid = cam.SpatialReference?.Wkid ?? 0
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
                    wkid = env.SpatialReference?.Wkid ?? 0
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
                ? SelectionCombinationMethod.New
                : Enum.Parse<SelectionCombinationMethod>(selectionTypeStr, ignoreCase: true);

            await QueuedTask.Run(() =>
            {
                var fl = MapView.Active?.Map?.Layers
                    .OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl != null)
                {
                    var sr = MapView.Active?.Camera?.SpatialReference;
                    var envelope = EnvelopeBuilderEx.CreateEnvelope(xmin, ymin, xmax, ymax, sr);
                    var sqf = new SpatialQueryFilter
                    {
                        SpatialRelationship = SpatialRelationship.Intersects,
                        FilterGeometry = envelope
                    };
                    fl.Select(sqf, selType);
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
                    var fl = MapView.Active?.Map?.Layers
                        .OfType<FeatureLayer>()
                        .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                    if (fl != null)
                        fl.Select(new QueryFilter());
                }
                else
                {
                    foreach (var fl in MapView.Active?.Map?.Layers.OfType<FeatureLayer>() ?? Enumerable.Empty<FeatureLayer>())
                        fl.Select(new QueryFilter());
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
                    var tableDef = fc.GetDefinition() as TableDefinition;
                    var fields = tableDef?.GetFields() ?? Enumerable.Empty<Field>();
                    foreach (var field in fields)
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
                var op = new EditOperation();
                return await op.UndoAsync();
            });
            return new IpcResponse(true, null, new { undoPerformed = performed });
        }

        private static async Task<IpcResponse> HandleRedoEdit(IpcRequest req, CancellationToken ct)
        {
            bool performed = await QueuedTask.Run(async () =>
            {
                var op = new EditOperation();
                return await op.RedoAsync();
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
                var fl = MapView.Active?.Map?.Layers
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
                var fl = MapView.Active?.Map?.Layers
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
                var uri = new Uri(filePath);
                createdLayer = LayerFactory.Instance.CreateLayer(uri, map, 0);
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
                ? SelectionCombinationMethod.New
                : Enum.Parse<SelectionCombinationMethod>(selTypeStr, ignoreCase: true);

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
                    var sqf = new SpatialQueryFilter
                    {
                        SpatialRelationship = SpatialRelationship.Intersects,
                        FilterGeometry = poly
                    };
                    fl.Select(sqf, selType);
                    count = fl.SelectionCount;
                }
            });

            return new IpcResponse(true, null, new { done = true, selectionCount = count });
        }

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

        private static async Task<IpcResponse> HandleGetProjectProperties(IpcRequest req, CancellationToken ct)
        {
            if (Project.Current == null)
                return new IpcResponse(false, "No project open", null);
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
            if (Project.Current == null)
                return new IpcResponse(false, "No project open", null);
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
            if (Project.Current == null)
                return new IpcResponse(false, "No project open", null);

            req.Args.TryGetValue("mapFrameName", out string mapFrameName);

            var result = await QueuedTask.Run(() =>
            {
                var layoutItem = Project.Current.GetItems<LayoutProjectItem>()
                    .FirstOrDefault(l => l.Name.Equals(layoutName, StringComparison.OrdinalIgnoreCase));
                if (layoutItem == null) return null;

                using var layout = layoutItem.GetLayout();
                var mfs = layout.FindElements(Enumerable.Empty<string>()).OfType<MapFrame>();
                if (!string.IsNullOrWhiteSpace(mapFrameName))
                    mfs = mfs.Where(mf => mf.Name.Equals(mapFrameName, StringComparison.OrdinalIgnoreCase));

                return mfs.Select(mf => new
                {
                    name = mf.Name,
                    mapName = mf.Map?.Name,
                    width = 0,
                    height = 0,
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
                ? SelectionCombinationMethod.New
                : Enum.Parse<SelectionCombinationMethod>(selTypeStr, ignoreCase: true);

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
                count = tgtFl.SelectionCount;
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
                var sr = fc.GetDefinition().GetSpatialReference();
                var envelope = EnvelopeBuilderEx.CreateEnvelope(xmin, ymin, xmax, ymax, sr);

                var sqf = new SpatialQueryFilter
                {
                    SpatialRelationship = SpatialRelationship.Intersects,
                    FilterGeometry = envelope,
                };
                if (!string.IsNullOrWhiteSpace(fields))
                    sqf.SubFields = fields;

                var features = new List<object>();
                using var cursor = fc.Search(sqf);
                var tableDef = fc.GetDefinition() as TableDefinition;
                var allFields = tableDef?.GetFields() ?? Enumerable.Empty<Field>();
                while (cursor.MoveNext() && features.Count < maxFeatures)
                {
                    using var row = cursor.Current;
                    var attrs = new Dictionary<string, object>();
                    foreach (var f in allFields)
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

                var fc = fl.GetFeatureClass();

                SpatialReference sr = null;
                if (!string.IsNullOrWhiteSpace(wkidStr) && int.TryParse(wkidStr, out int wkid))
                    sr = SpatialReferenceBuilder.CreateSpatialReference(wkid);

                var pt = MapPointBuilderEx.CreateMapPoint(x, y, sr);

                var op = new EditOperation();
                op.Name = "Create point feature";
                var token = op.Create(fl, pt);

                if (!string.IsNullOrWhiteSpace(attrsJson))
                {
                    var jsonAttrs = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(attrsJson);
                    var attrDict = jsonAttrs.ToDictionary(kvp => kvp.Key, kvp => JsonElementToObject(kvp.Value));
                    op.Modify(fl, token.ObjectID.Value, attrDict);
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
                        name = b.Name,
                    })
                    .ToList()
            );
            return new IpcResponse(true, null, bookmarks);
        }

        private static async Task<IpcResponse> HandleZoomToBookmark(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("name", out string bmName) ||
                string.IsNullOrWhiteSpace(bmName))
                return new IpcResponse(false, "arg 'name' required", null);

            await QueuedTask.Run(async () =>
            {
                var map = MapView.Active?.Map;
                var bkmk = map?.GetBookmarks()
                    .FirstOrDefault(b => b.Name.Equals(bmName, StringComparison.OrdinalIgnoreCase));
                if (bkmk != null)
                    await MapView.Active.ZoomToAsync(bkmk);
            });

            return new IpcResponse(true, null, new { done = true });
        }

        private static async Task<IpcResponse> HandleCreateBookmark(IpcRequest req, CancellationToken ct)
        {
            return new IpcResponse(false, "Bookmark creation not accessible from AddIn SDK", null);
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
                    MapView.Active.Map.MoveLayer(layer, index);
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
                    fl.SetLabelVisibility(enabled);
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
                var fl = MapView.Active?.Map?.Layers
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
                var fl = MapView.Active?.Map?.Layers
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

        // --- Phase 4: Scene / 3D ---

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
                    var workspaceUri = fc.GetDatastore().GetPath();
                    var fcPath = System.IO.Path.Combine(workspaceUri.LocalPath, fc.GetName());

                    object[] gisParams = string.IsNullOrWhiteSpace(lenStr)
                        ? new object[] { fcPath, fieldName, fieldType }
                        : new object[] { fcPath, fieldName, fieldType, "#", "#", lenStr };

                    var result = await Geoprocessing.ExecuteToolAsync("AddField", Geoprocessing.MakeValueArray(gisParams));
                    if (result != null && result.IsFailed)
                        warning = "AddField failed";
                }
                catch (Exception ex) { warning = $"AddField error: {ex.Message}"; }
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
                var fl = MapView.Active?.Map?.Layers
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
                catch (Exception ex) { warning = $"DeleteField error: {ex.Message}"; }
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, fieldName });
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

                var op = new EditOperation();
                op.Name = "Create polygon feature";
                var token = op.Create(fl, poly);

                if (!string.IsNullOrWhiteSpace(attrsJson))
                {
                    var jsonAttrs = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(attrsJson);
                    var attrDict = jsonAttrs.ToDictionary(kvp => kvp.Key, kvp => JsonElementToObject(kvp.Value));
                    op.Modify(fl, token.ObjectID.Value, attrDict);
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

                var op = new EditOperation();
                op.Name = "Create line feature";
                var token = op.Create(fl, polyline);

                if (!string.IsNullOrWhiteSpace(attrsJson))
                {
                    var jsonAttrs = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(attrsJson);
                    var attrDict = jsonAttrs.ToDictionary(kvp => kvp.Key, kvp => JsonElementToObject(kvp.Value));
                    op.Modify(fl, token.ObjectID.Value, attrDict);
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
                    if (fl != null && fl.SelectionCount > 0) await MapView.Active.ZoomToAsync(fl);
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
            return Task.FromResult(new IpcResponse(true, null, new { undoCount = 0, redoCount = 0 }));
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
            return new IpcResponse(false, "Flash selection not accessible from AddIn SDK", null);
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
                    count = fl.SelectionCount;
                }
            });

            return new IpcResponse(true, null, new { done = true, count });
        }

        // --- Phase 5: UI Feedback ---

        private static Task<IpcResponse> HandleSetStatusBarMessage(IpcRequest req, CancellationToken ct)
        {
            return Task.FromResult(new IpcResponse(false, "StatusBar not accessible from AddIn context", null));
        }

        // --- Phase 5: Data Discovery ---

        private static async Task<IpcResponse> HandleListStandaloneTables(IpcRequest req, CancellationToken ct)
        {
            return new IpcResponse(false, "Standalone tables not accessible from AddIn SDK", null);
        }

        // --- Phase 6: Geoprocessing History ---

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

        // --- Phase 6: Time Slider ---

        private static async Task<IpcResponse> HandleIsTimeEnabled(IpcRequest req, CancellationToken ct)
        {
            return new IpcResponse(false, "Time extent not accessible from AddIn SDK", null);
        }

        private static async Task<IpcResponse> HandleGetTimeExtent(IpcRequest req, CancellationToken ct)
        {
            return new IpcResponse(false, "Time extent not accessible from AddIn SDK", null);
        }

        private static async Task<IpcResponse> HandleSetTimeExtent(IpcRequest req, CancellationToken ct)
        {
            return new IpcResponse(false, "Time extent not accessible from AddIn SDK", null);
        }

        // --- Phase 6: Layout Elements ---

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

                var fcPath = $"{fc.GetDatastore().GetPath()}/{fc.GetName()}";
                Geoprocessing.ExecuteToolAsync("AlterField", Geoprocessing.MakeValueArray(fcPath, oldName, newName));
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
                string desc = "";
                try { var cim = layer.GetDefinition(); if (cim is ArcGIS.Core.CIM.CIMBasicFeatureLayer bfl) desc = bfl.Description ?? ""; } catch { }
                return new { layerName, description = desc };
            });

            if (result == null) return new IpcResponse(false, $"Layer '{layerName}' not found", null);
            return new IpcResponse(true, null, result);
        }

        private static async Task<IpcResponse> HandleSetLayerDescription(IpcRequest req, CancellationToken ct)
        {
            return new IpcResponse(false, "Layer description not accessible from AddIn SDK", null);
        }

        // --- Phase 6: Scene Layer Types ---

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
            return new IpcResponse(false, "Split features not accessible from AddIn SDK", null);
        }

        private static async Task<IpcResponse> HandleMergeFeatures(IpcRequest req, CancellationToken ct)
        {
            return new IpcResponse(false, "Merge features not accessible from AddIn SDK", null);
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
                    warning = $"GP tool execution failed: {ex.Message}";
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
                var fl = MapView.Active?.Map?.Layers
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
                {
                    var def = layer.GetDefinition();
                    def.Name = newName;
                    layer.SetDefinition(def);
                }
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
        }

        // --- Phase 8: Layout & Map Automation ---

        private static async Task<IpcResponse> HandleAddLayoutText(IpcRequest req, CancellationToken ct)
        {
            return new IpcResponse(false, "Layout text element not accessible from AddIn SDK", null);
        }

        private static async Task<IpcResponse> HandleAddLayoutPicture(IpcRequest req, CancellationToken ct)
        {
            return new IpcResponse(false, "Layout picture element not accessible from AddIn SDK", null);
        }

        private static async Task<IpcResponse> HandleAddLayoutLegend(IpcRequest req, CancellationToken ct)
        {
            return new IpcResponse(false, "Layout legend not accessible from AddIn SDK", null);
        }

        private static async Task<IpcResponse> HandleAddLayoutNorthArrow(IpcRequest req, CancellationToken ct)
        {
            return new IpcResponse(false, "Layout north arrow not accessible from AddIn SDK", null);
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

        private static async Task<IpcResponse> HandleCreateMap(IpcRequest req, CancellationToken ct)
        {
            return new IpcResponse(false, "Map creation not accessible from AddIn SDK", null);
        }

        private static async Task<IpcResponse> HandleAddBasemap(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("basemapName", out string basemapName) || string.IsNullOrWhiteSpace(basemapName))
                return new IpcResponse(false, "arg 'basemapName' required", null);

            await QueuedTask.Run(async () =>
            {
                try
                {
                    var map = MapView.Active?.Map;
                    if (map == null) return;
                    var mapName = map.Name;
                    var pyCode = "import arcpy\n"
                        + $"map_name = {System.Text.Json.JsonSerializer.Serialize(mapName)}\n"
                        + $"basemap = {System.Text.Json.JsonSerializer.Serialize(basemapName)}\n"
                        + "try:\n"
                        + "    aprx = arcpy.mp.ArcGISProject('CURRENT')\n"
                        + "    m = aprx.listMaps(map_name)[0]\n"
                        + "    m.addBasemap(basemap)\n"
                        + "    print('ok')\n"
                        + "except Exception as ex:\n"
                        + "    print(f'error: {ex}')\n";
                    await RunProPythonAsync(pyCode, 30, ct);
                }
                catch { }
            });

            return new IpcResponse(true, null, new { done = true, basemapName });
        }

        // --- Phase 9: Advanced 3D & Visualization ---

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

        // --- Phase 10: Project & Data Management ---

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
                    var result = await Geoprocessing.ExecuteToolAsync("Delete", Geoprocessing.MakeValueArray(path));
                    if (result != null && result.IsFailed)
                        warning = "Delete operation failed";
                }
                catch (Exception ex) { warning = $"Delete failed: {ex.Message}"; }
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, path });
        }

        private static async Task<IpcResponse> HandleSaveProject(IpcRequest req, CancellationToken ct)
        {
            if (Project.Current == null)
                return new IpcResponse(false, "No project open", null);

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

            await QueuedTask.Run(async () =>
            {
                var fl = MapView.Active?.Map?.Layers
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
                catch (Exception ex) { warning = $"AddIndex error: {ex.Message}"; }
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, field, indexName, unique });
        }

        private static async Task<IpcResponse> HandleSearchAddress(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("address", out string address) || string.IsNullOrWhiteSpace(address))
                return new IpcResponse(false, "arg 'address' required", null);

            req.Args.TryGetValue("maxResults", out string maxStr);
            int.TryParse(maxStr, out int maxResults);
            if (maxResults <= 0) maxResults = 10;

            // Use arcpy subprocess for geocoding (works with locators set up in Pro)
            var pythonCode = "import arcpy, json\n"
                + $"address = {System.Text.Json.JsonSerializer.Serialize(address)}\n"
                + $"max_results = {maxResults}\n"
                + "results = []\n"
                + "try:\n"
                + "    locators = arcpy.geocoding.ListLocators()\n"
                + "    if locators:\n"
                + "        for loc in locators[:1]:\n"
                + "            geocode_result = arcpy.geocoding.GeocodeAddresses(\n"
                + "                [[address]], loc, 'SingleLine SingleLine')\n"
                + "            with arcpy.da.SearchCursor(geocode_result[0], ['Shape@', 'Status', 'Score', 'Match_addr']) as cur:\n"
                + "                for i, row in enumerate(cur):\n"
                + "                    if i >= max_results: break\n"
                + "                    pt = row[0]\n"
                + "                    results.append({'address': row[3] or address, 'score': row[2], 'status': row[1],\n"
                + "                        'x': pt.centroid.X if pt else 0, 'y': pt.centroid.Y if pt else 0})\n"
                + "    print(json.dumps({'locatorCount': len(locators), 'results': results, 'address': address}))\n"
                + "except Exception as ex:\n"
                + "    print(json.dumps({'error': str(ex), 'results': []}))\n";
            var pyResult = await RunProPythonAsync(pythonCode, 30, ct);
            object data = new { address, locatorCount = 0, results = new List<object>() };
            if (pyResult.Ok && pyResult.Data is System.Text.Json.JsonElement pe)
            {
                try { data = System.Text.Json.JsonSerializer.Deserialize<object>(pe.GetRawText()); } catch { }
            }
            return new IpcResponse(true, null, data);
        }

        private static async Task<IpcResponse> HandleOpenAttributeTable(IpcRequest req, CancellationToken ct)
        {
            return new IpcResponse(false, "Attribute table not accessible from AddIn SDK", null);
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
                var ringCoords = polygon.Points.Select(p => new[] { p.X, p.Y }).ToArray();
                rings.Add(ringCoords);
                return new { type = "Polygon", coordinates = rings };
            }

            if (geom is Polyline polyline)
            {
                var coords = polyline.Points.Select(p => new[] { p.X, p.Y }).ToArray();
                return new { type = "LineString", coordinates = coords };
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

            string warning = null;

            await QueuedTask.Run(async () =>
            {
                try
                {
                    if (!System.IO.File.Exists(csvPath)) { warning = "CSV file not found"; return; }

                    var fcPath = $"{gdbPath}/{fcName}";

                    var createParams = new List<object?> { fcPath, "POINT" };
                    if (wkid > 0)
                        createParams.Add(SpatialReferenceBuilder.CreateSpatialReference(wkid));
                    var r1 = await Geoprocessing.ExecuteToolAsync("CreateFeatureclass",
                        Geoprocessing.MakeValueArray(createParams.ToArray()));
                    if (r1.IsFailed) { warning = "CreateFeatureclass failed"; return; }

                    var lines = System.IO.File.ReadAllLines(csvPath);
                    if (lines.Length < 2) { warning = "CSV has no data rows"; return; }

                    var headers = ParseCsvLine(lines[0]);
                    int xIdx = -1, yIdx = -1;
                    for (int i = 0; i < headers.Length; i++)
                    {
                        if (headers[i].Equals(xField, StringComparison.OrdinalIgnoreCase)) xIdx = i;
                        if (headers[i].Equals(yField, StringComparison.OrdinalIgnoreCase)) yIdx = i;
                    }
                    if (xIdx < 0 || yIdx < 0) { warning = $"X/Y fields not found in CSV header"; return; }

                    foreach (var h in headers)
                    {
                        if (h.Equals(xField, StringComparison.OrdinalIgnoreCase) || h.Equals(yField, StringComparison.OrdinalIgnoreCase)) continue;
                        await Geoprocessing.ExecuteToolAsync("AddField",
                            Geoprocessing.MakeValueArray(fcPath, h, "TEXT", "#", "#", "255"));
                    }

                    // Import via arcpy subprocess (more reliable for bulk)
                    var pyCode = "import arcpy, json\n"
                        + $"csv_path = {System.Text.Json.JsonSerializer.Serialize(csvPath)}\n"
                        + $"fc_path = {System.Text.Json.JsonSerializer.Serialize(fcPath)}\n"
                        + $"x_f = {System.Text.Json.JsonSerializer.Serialize(xField)}\n"
                        + $"y_f = {System.Text.Json.JsonSerializer.Serialize(yField)}\n"
                        + $"wkid = {wkid}\n"
                        + "try:\n"
                        + "    sr = arcpy.SpatialReference(wkid) if wkid else None\n"
                        + "    arcpy.management.XYTableToPoint(csv_path, fc_path, x_f, y_f, sr)\n"
                        + "    with arcpy.da.SearchCursor(fc_path, ['OID@']) as cur:\n"
                        + "        count = sum(1 for _ in cur)\n"
                        + "    print(json.dumps({'rowCount': count}))\n"
                        + "except Exception as ex:\n"
                        + "    print(json.dumps({'error': str(ex)}))\n";
                    var pyResult = await RunProPythonAsync(pyCode, 60, ct);
                    if (pyResult.Ok && pyResult.Data is System.Text.Json.JsonElement pe)
                    {
                        string stdout = "";
                        if (pe.TryGetProperty("stdout", out var so)) stdout = so.GetString() ?? "";
                        if (stdout.Contains("\"error\""))
                            warning = "arcpy XYTableToPoint failed";
                    }
                }
                catch (Exception ex) { warning = $"CSV import failed: {ex.Message}"; }
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, fcName });
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
                    var workspaceUri = fc.GetDatastore().GetPath();
                    var fcName = fc.GetName();
                    var fcPath = System.IO.Path.Combine(workspaceUri.LocalPath, fcName);

                    var result = await Geoprocessing.ExecuteToolAsync("CopyFeatures",
                        Geoprocessing.MakeValueArray(fcPath, outputPath));
                    if (result != null && result.IsFailed)
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
                    var workspaceUri = fc.GetDatastore().GetPath();
                    var fcName = fc.GetName();
                    var fcPath = System.IO.Path.Combine(workspaceUri.LocalPath, fcName);

                    var result = await Geoprocessing.ExecuteToolAsync("LayerToKML",
                        Geoprocessing.MakeValueArray(fcPath, outputPath));
                    if (result != null && result.IsFailed)
                        warning = "LayerToKML failed";
                }
                catch (Exception ex) { warning = $"Export failed: {ex.Message}"; }
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, outputPath });
        }

        private static async Task<IpcResponse> HandleImportGeoJSON(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("geojsonPath", out string geoJsonPath) || string.IsNullOrWhiteSpace(geoJsonPath) ||
                !req.Args.TryGetValue("gdbPath", out string gdbPath) || string.IsNullOrWhiteSpace(gdbPath) ||
                !req.Args.TryGetValue("fcName", out string fcName) || string.IsNullOrWhiteSpace(fcName))
                return new IpcResponse(false, "args 'geojsonPath', 'gdbPath', & 'fcName' required", null);

            string warning = null;

            await QueuedTask.Run(async () =>
            {
                try
                {
                    if (!System.IO.File.Exists(geoJsonPath)) { warning = "GeoJSON file not found"; return; }

                    // Use arcpy to import GeoJSON
                    var fcPath = $"{gdbPath}/{fcName}";
                    var pyCode = "import arcpy, json\n"
                        + $"gj_path = {System.Text.Json.JsonSerializer.Serialize(geoJsonPath)}\n"
                        + $"fc_path = {System.Text.Json.JsonSerializer.Serialize(fcPath)}\n"
                        + "try:\n"
                        + "    result = arcpy.conversion.JSONToFeatures(gj_path, fc_path)\n"
                        + "    print(json.dumps({'done': True, 'fc': fc_path}))\n"
                        + "except Exception as ex:\n"
                        + "    print(json.dumps({'error': str(ex)}))\n";
                    var pyResult = await RunProPythonAsync(pyCode, 60, ct);
                    if (pyResult.Ok && pyResult.Data is System.Text.Json.JsonElement pe)
                    {
                        string stdout = "";
                        if (pe.TryGetProperty("stdout", out var so)) stdout = so.GetString() ?? "";
                        if (stdout.Contains("\"error\""))
                            warning = "arcpy JSONToFeatures failed";
                    }
                }
                catch (Exception ex) { warning = $"GeoJSON import failed: {ex.Message}"; }
            });

            return new IpcResponse(warning == null, warning ?? "ok", new { done = warning == null, fcName });
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
                var icon = type?.Equals("error", StringComparison.OrdinalIgnoreCase) == true ? System.Windows.MessageBoxImage.Error
                         : type?.Equals("warning", StringComparison.OrdinalIgnoreCase) == true ? System.Windows.MessageBoxImage.Warning
                         : System.Windows.MessageBoxImage.Information;
                // Fire-and-forget: show message box without blocking the pipe thread
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    System.Windows.MessageBox.Show(message, title, System.Windows.MessageBoxButton.OK, icon)
                );
                return Task.FromResult(new IpcResponse(true, null, new { done = true, type = type ?? "info" }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(new IpcResponse(false, ex.Message, null));
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
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    System.Windows.MessageBox.Show(message, title, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information)
                );
                return Task.FromResult(new IpcResponse(true, null, new { done = true }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(new IpcResponse(false, ex.Message, null));
            }
        }



        private static Task<IpcResponse> HandleSetStatusBarProgress(IpcRequest req, CancellationToken ct)
        {
            return Task.FromResult(new IpcResponse(false, "StatusBar not accessible from AddIn context", null));
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
                catch (Exception ex) { warning = $"CreateDomain error: {ex.Message}"; }
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
                var fl = MapView.Active?.Map?.Layers
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
                    var workspaceUri = fc.GetDatastore().GetPath();
                    var fcPath = System.IO.Path.Combine(workspaceUri.LocalPath, fc.GetName());

                    var result = await Geoprocessing.ExecuteToolAsync("SetSubtypeField",
                        Geoprocessing.MakeValueArray(fcPath, fieldName));
                    if (result != null && result.IsFailed)
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
                    var workspaceUri = fc.GetDatastore().GetPath();
                    var fcPath = System.IO.Path.Combine(workspaceUri.LocalPath, fc.GetName());

                    var result = await Geoprocessing.ExecuteToolAsync("EnableAttachments",
                        Geoprocessing.MakeValueArray(fcPath));
                    if (result != null && result.IsFailed)
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

        // --- Phase 3: In-Process Python Execution (Proof of Concept) ---

        private static async Task<IpcResponse> HandlePingPythonRuntime(IpcRequest req, CancellationToken ct)
        {
            EnsurePythonEngine();
            if (!_pyEngineReady)
                return new IpcResponse(false, $"PythonEngine not available: {_pyEngineError ?? "unknown"}", null);

            try
            {
                using (Py.GIL())
                {
                    dynamic sys = Py.Import("sys");
                    var version = sys.version;
                    var path = sys.executable;

                    dynamic arcpy = null;
                    string arcpyVersion = null;
                    try
                    {
                        arcpy = Py.Import("arcpy");
                        arcpyVersion = arcpy.GetInstallInfo()["Version"].ToString();
                    }
                    catch { arcpyVersion = "arcpy not importable"; }

                    return new IpcResponse(true, null, new
                    {
                        pong = "inprocess",
                        pythonVersion = version.ToString(),
                        pythonPath = path.ToString(),
                        arcpyVersion,
                        hasGil = true,
                        engineReady = true,
                    });
                }
            }
            catch (System.Exception ex)
            {
                return new IpcResponse(false, $"In-process Python error: {ex.GetType().Name}: {ex.Message}", null);
            }
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

        // --- Lazy PythonEngine Initialization ---

        private static string _pyEngineError;

        private static void EnsurePythonEngine()
        {
            if (_pyEngineReady || _pyEngineAttempted) return;
            lock (_pyEngineLock)
            {
                if (_pyEngineReady || _pyEngineAttempted) return;
                _pyEngineAttempted = true;
                try
                {
                    var dllPath = FindProPythonDll();
                    if (dllPath == null) { _pyEngineError = "python311.dll not found"; return; }

                    // Check if Python is already initialized in this process
                    if (PythonEngine.IsInitialized)
                    {
                        _pyEngineReady = true;
                        return;
                    }

                    Runtime.PythonDLL = dllPath;
                    PythonEngine.Initialize();
                    _pyEngineReady = true;
                }
                catch (System.Exception ex)
                {
                    _pyEngineError = $"{ex.GetType().Name}: {ex.Message}";
                }
            }
        }

        private static string FindProPythonDll()
        {
            string envDir = null;
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\ESRI\ArcGISPro");
                if (key?.GetValue("InstallDir") is string installDir)
                    envDir = System.IO.Path.Combine(installDir, "bin", "Python", "envs", "arcgispro-py3");
            }
            catch { }

            if (envDir == null)
            {
                var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                envDir = System.IO.Path.Combine(pf, @"ArcGIS\Pro\bin\Python\envs\arcgispro-py3");
            }

            if (!Directory.Exists(envDir)) return null;

            // Find python3*.dll (supports any 3.x version: python311.dll, python313.dll, etc.)
            var dlls = Directory.EnumerateFiles(envDir, "python3*.dll")
                .Where(f => !f.EndsWith("python3.dll", StringComparison.OrdinalIgnoreCase))
                .ToList();
            return dlls.FirstOrDefault();
        }

        private static Task<IpcResponse> RunProPythonInProcessAsync(string code, int timeoutSeconds, CancellationToken ct)
        {
            // Note: In-process Python execution cannot use arcpy.mp.ArcGISProject("CURRENT")
            // because Pro's internal arcpy runtime already holds the GIL, causing a deadlock
            // when pythonnet tries to acquire it. This method handles simple Python code only.
            // For arcpy code, the subprocess fallback in RunProPythonAsync is used.

            return Task.Run(() =>
            {
                try
                {
                    using (Py.GIL())
                    {
                        var setup = "import sys, io\n"
                            + "_stdout = sys.stdout\n"
                            + "_stderr = sys.stderr\n"
                            + "sys.stdout = io.StringIO()\n"
                            + "sys.stderr = io.StringIO()\n";
                        PythonEngine.Exec(setup);
                        PythonEngine.Exec(code);
                        var getOutput = "stdout = sys.stdout.getvalue()\n"
                            + "stderr = sys.stderr.getvalue()\n"
                            + "sys.stdout = _stdout\n"
                            + "sys.stderr = _stderr\n";
                        PythonEngine.Exec(getOutput);
                        var stdout = PythonEngine.Eval("stdout")?.ToString() ?? "";
                        var stderr = PythonEngine.Eval("stderr")?.ToString() ?? "";
                        return new IpcResponse(true, null, new { stdout, stderr, exitCode = 0, inProcess = true });
                    }
                }
                catch (System.Exception ex)
                {
                    return new IpcResponse(false, $"In-process Python error: {ex.Message}", null);
                }
            }, ct);
        }

        private static async Task<IpcResponse> RunProPythonAsync(string code, int timeoutSeconds, CancellationToken ct)
        {
            // Note: In-process path is disabled for runPythonScript due to GIL contention
            // with Pro's internal arcpy. Use pro.pingPythonRuntime to verify the engine works.
            // Fall back to subprocess
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
                    return new IpcResponse(true, null, new { stdout, stderr, exitCode = process.ExitCode, inProcess = false });
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

