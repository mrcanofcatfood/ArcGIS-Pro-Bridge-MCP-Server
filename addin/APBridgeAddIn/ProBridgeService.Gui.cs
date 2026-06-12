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
                return Task.FromResult(new IpcResponse(false, SanitizeException(ex), null));
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
                return Task.FromResult(new IpcResponse(false, SanitizeException(ex), null));
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
    }
}
