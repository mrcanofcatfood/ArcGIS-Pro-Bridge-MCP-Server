using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Core.Geoprocessing;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace APBridgeAddIn
{
    internal partial class ProBridgeService : IDisposable
    {
        private static string GetSnapshotsGdbPath()
        {
            var proj = Project.Current;
            if (proj == null) return null;
            return Path.Combine(proj.HomeFolderPath, "snapshots.gdb");
        }

        private static async Task EnsureSnapshotsGdb(string gdbPath)
        {
            if (Directory.Exists(gdbPath)) return;
            await Geoprocessing.ExecuteToolAsync("CreateFileGDB",
                Geoprocessing.MakeValueArray(
                    Path.GetDirectoryName(gdbPath),
                    Path.GetFileNameWithoutExtension(gdbPath)
                ));
        }

        private static async Task<IpcResponse> HandleCreateSnapshot(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("layer", out string layerName) || string.IsNullOrWhiteSpace(layerName))
                return new IpcResponse(false, "args 'layer' required", null);

            req.Args.TryGetValue("oids", out string oidsStr);
            req.Args.TryGetValue("description", out string description);

            string snapshotName = null;
            string error = null;

            await QueuedTask.Run(async () =>
            {
                var map = MapView.Active?.Map;
                if (map == null) { error = "No active map"; return; }
                var fl = map.Layers.OfType<FeatureLayer>()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (fl == null) { error = "Layer not found"; return; }

                var gdbPath = GetSnapshotsGdbPath();
                if (gdbPath == null) { error = "No project open"; return; }

                try { await EnsureSnapshotsGdb(gdbPath); }
                catch (Exception ex) { error = $"Failed to create snapshots GDB: {ex.Message}"; return; }

                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                var safeName = SanitizeName(layerName);
                snapshotName = $"snap_{safeName}_{timestamp}";

                using var fc = fl.GetFeatureClass();
                var workspacePath = fc.GetDatastore().GetPath().LocalPath;
                var fcName = fc.GetName();
                var fcPath = Path.Combine(workspacePath, fcName);
                var outputPath = Path.Combine(gdbPath, snapshotName);

                var argsList = new List<string> { fcPath, outputPath };
                if (!string.IsNullOrWhiteSpace(oidsStr))
                {
                    // Build a where clause from the OIDs
                    var oids = oidsStr.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (oids.Length > 0)
                    {
                        var where = "OBJECTID IN (" + string.Join(",", oids) + ")";
                        argsList.Add(where);
                    }
                }

                try
                {
                    var result = await Geoprocessing.ExecuteToolAsync("CopyFeatures",
                        Geoprocessing.MakeValueArray(argsList.ToArray()));
                    if (result != null && result.IsFailed)
                        error = "CopyFeatures completed with warnings";
                }
                catch (Exception ex)
                {
                    error = $"Snapshot failed: {ex.Message}";
                }
            });

            if (error != null)
                return new IpcResponse(false, error, null);

            return new IpcResponse(true, "ok", new
            {
                snapshotName,
                layer = layerName,
                description = description ?? "",
                timestamp = DateTime.Now.ToString("o"),
            });
        }

        private static async Task<IpcResponse> HandleRestoreSnapshot(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("snapshotName", out string snapName) || string.IsNullOrWhiteSpace(snapName))
                return new IpcResponse(false, "args 'snapshotName' required", null);

            req.Args.TryGetValue("targetLayer", out string targetLayer);

            string error = null;
            int restoredCount = 0;

            await QueuedTask.Run(async () =>
            {
                var gdbPath = GetSnapshotsGdbPath();
                if (gdbPath == null) { error = "No project open"; return; }
                if (!Directory.Exists(gdbPath)) { error = "Snapshots GDB not found"; return; }

                var snapPath = Path.Combine(gdbPath, snapName);
                if (!Directory.Exists(snapPath))
                {
                    error = $"Snapshot '{snapName}' not found";
                    return;
                }

                var map = MapView.Active?.Map;
                if (map == null) { error = "No active map"; return; }

                // Determine target layer
                foreach (var fl in map.Layers.OfType<FeatureLayer>())
                {
                    if (!fl.Name.Equals(targetLayer ?? "", StringComparison.OrdinalIgnoreCase))
                        continue;

                    using var fc = fl.GetFeatureClass();
                    var wsPath = fc.GetDatastore().GetPath().LocalPath;
                    var fcName = fc.GetName();
                    var targetPath = Path.Combine(wsPath, fcName);
                    var sourcePath = Path.Combine(gdbPath, snapName);

                    try
                    {
                        var result = await Geoprocessing.ExecuteToolAsync("Append",
                            Geoprocessing.MakeValueArray(sourcePath, targetPath, "NO_TEST", "", ""));
                        if (result != null && result.IsFailed)
                            error = "Append completed with warnings";
                        else
                            restoredCount = 1; // Append doesn't return count easily
                    }
                    catch (Exception ex)
                    {
                        error = $"Restore failed: {ex.Message}";
                    }
                    return;
                }

                if (error == null)
                    error = $"Target layer '{targetLayer ?? "(none)"}' not found in map";
            });

            if (error != null)
                return new IpcResponse(false, error, null);

            return new IpcResponse(true, "ok", new { snapshotName = snapName, restored = true, restoredCount });
        }

        private static async Task<IpcResponse> HandleListSnapshots(IpcRequest req, CancellationToken ct)
        {
            var snapshots = new List<object>();

            await QueuedTask.Run(() =>
            {
                var gdbPath = GetSnapshotsGdbPath();
                if (gdbPath == null || !Directory.Exists(gdbPath))
                    return;

                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(gdbPath, "snap_*"))
                    {
                        snapshots.Add(new
                        {
                            name = Path.GetFileName(dir),
                            datasetType = "FeatureClass",
                        });
                    }
                }
                catch { }
            });

            return new IpcResponse(true, "ok", new { snapshotCount = snapshots.Count, snapshots });
        }

        private static async Task<IpcResponse> HandleDeleteSnapshot(IpcRequest req, CancellationToken ct)
        {
            if (req.Args == null ||
                !req.Args.TryGetValue("snapshotName", out string snapName) || string.IsNullOrWhiteSpace(snapName))
                return new IpcResponse(false, "args 'snapshotName' required", null);

            string error = null;

            await QueuedTask.Run(async () =>
            {
                var gdbPath = GetSnapshotsGdbPath();
                if (gdbPath == null || !Directory.Exists(gdbPath))
                { error = "Snapshots GDB not found"; return; }

                var snapPath = Path.Combine(gdbPath, snapName);
                try
                {
                    var result = await Geoprocessing.ExecuteToolAsync("Delete",
                        Geoprocessing.MakeValueArray(snapPath));
                    if (result != null && result.IsFailed)
                        error = "Delete completed with warnings";
                }
                catch (Exception ex)
                {
                    error = $"Delete snapshot failed: {ex.Message}";
                }
            });

            if (error != null)
                return new IpcResponse(false, error, null);

            return new IpcResponse(true, "ok", new { snapshotName = snapName, deleted = true });
        }

        private static string SanitizeName(string name)
        {
            var chars = name.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray();
            var result = new string(chars);
            return string.IsNullOrWhiteSpace(result) ? "unnamed" : result;
        }
    }
}
