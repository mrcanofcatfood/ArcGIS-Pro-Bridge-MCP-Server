using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;
using System;
using System.Timers;

namespace APBridgeAddIn.Dockpanes
{
    internal class BridgeDashboardDockpane : DockPane
    {
        private const string DOCKPANE_ID = "APBridgeAddIn_BridgeDashboard";
        private Timer _refreshTimer;
        private BridgeDashboardViewModel _viewModel;

        protected BridgeDashboardDockpane()
        {
            _viewModel = new BridgeDashboardViewModel();
        }

        public BridgeDashboardViewModel ViewModel => _viewModel;

        protected override void OnShow(bool isNew)
        {
            StartRefreshTimer();
            RefreshNow();
        }

        private void StartRefreshTimer()
        {
            StopRefreshTimer();
            _refreshTimer = new Timer(5000);
            _refreshTimer.Elapsed += (s, e) =>
            {
                try { RefreshNow(); }
                catch { }
            };
            _refreshTimer.AutoReset = true;
            _refreshTimer.Start();
        }

        private void StopRefreshTimer()
        {
            if (_refreshTimer != null)
            {
                _refreshTimer.Stop();
                _refreshTimer.Dispose();
                _refreshTimer = null;
            }
        }

        private void RefreshNow()
        {
            var bridge = Module1.Current?.GetBridgeService();
            if (bridge == null)
            {
                _viewModel.SetHeartbeat(false, "No bridge service");
                return;
            }

            var uptime = Module1.Current.GetUptime();
            _viewModel.Uptime = uptime;

            _viewModel.RefreshFromBridge(bridge);
        }

        public static void Show()
        {
            var pane = FrameworkApplication.DockPaneManager.Find(DOCKPANE_ID);
            pane?.Activate();
        }

        internal static bool IsPingable()
        {
            var bridge = Module1.Current?.GetBridgeService();
            return bridge?.PingSync() ?? false;
        }
    }
}
