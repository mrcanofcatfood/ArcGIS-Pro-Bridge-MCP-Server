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
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Python.Runtime;

namespace APBridgeAddIn
{
    internal partial class ProBridgeService : IDisposable
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
            DiscoverPlugins();
            _serverThread = new Thread(RunLoop) { IsBackground = true, Name = "ProBridgePipeServer" };
            _serverThread.Start();
        }

        public void Dispose()
        {
            _stopped = true;
            _serverThread = null;
        }

        private void DiscoverPlugins()
        {
            try
            {
                var handlerType = typeof(IProBridgeHandler);
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.IsDynamic || assembly.GlobalAssemblyCache) continue;
                    foreach (var type in assembly.GetExportedTypes())
                    {
                        if (type.IsAbstract || !handlerType.IsAssignableFrom(type)) continue;
                        var attr = type.GetCustomAttribute<ProBridgeHandlerAttribute>();
                        if (attr == null) continue;
                        var instance = (IProBridgeHandler)Activator.CreateInstance(type);
                        _handlers[attr.Op] = async (req, ct) => await instance.Handle(req, ct);
                    }
                }
            }
            catch { }
        }

        private void RunLoop()
        {
            while (!_stopped)
            {
                try
                {
                    var server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Message,
                        PipeOptions.Asynchronous
                    );

                    server.WaitForConnection();
                    _ = Task.Run(() => HandleClientAsync(server));
                }
                catch
                {
                    try { Thread.Sleep(500); } catch { break; }
                }
            }
        }

        private async Task HandleClientAsync(NamedPipeServerStream server)
        {
            try
            {
                using (server)
                using (var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true))
                using (var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true)
                { AutoFlush = true })
                {
                    while (server.IsConnected && !_stopped)
                    {
                        var line = await reader.ReadLineAsync();
                        if (line == null) break;

                        IpcRequest req;
                        try
                        {
                            req = JsonSerializer.Deserialize<IpcRequest>(line);
                        }
                        catch
                        {
                            await writer.WriteLineAsync(JsonSerializer.Serialize(new IpcResponse(false, "parse error", null)));
                            continue;
                        }

                        try
                        {
                            var resp = await HandleAsync(req, CancellationToken.None);
                            await writer.WriteLineAsync(JsonSerializer.Serialize(resp));
                        }
                        catch (Exception ex)
                        {
                            try { await writer.WriteLineAsync(JsonSerializer.Serialize(new IpcResponse(false, SanitizeException(ex), null))); } catch { }
                        }
                    }
                }
            }
            catch
            {
                try { Thread.Sleep(500); } catch { }
            }
        }

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
            ["pro.addLayerFromService"] = HandleAddLayerFromService,
            ["pro.selectByPolygon"] = HandleSelectByPolygon,
            ["pro.listLayouts"] = HandleListLayouts,
            ["pro.getProjectProperties"] = HandleGetProjectProperties,
            ["pro.getGeometryDistance"] = HandleGetGeometryDistance,
            ["pro.setLayerTransparency"] = HandleSetLayerTransparency,
            ["pro.getAllMapNames"] = HandleGetAllMapNames,
            ["pro.getMapFrame"] = HandleGetMapFrame,
            ["pro.selectByLayer"] = HandleSelectByLayer,
            ["pro.getFeaturesByExtent"] = HandleGetFeaturesByExtent,
            ["pro.findFeatures"] = HandleFindFeatures,
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
            ["pro.calculateField"] = HandleCalculateField,
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
                return new IpcResponse(false, $"In-process Python error: {SanitizeException(ex)}", null);
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
                    _pyEngineError = SanitizeException(ex);
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
                    return new IpcResponse(false, $"In-process Python error: {SanitizeException(ex)}", null);
                }
            });
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

        private static string SanitizeException(Exception ex)
        {
            return $"Operation failed: {ex.GetType().Name}";
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
