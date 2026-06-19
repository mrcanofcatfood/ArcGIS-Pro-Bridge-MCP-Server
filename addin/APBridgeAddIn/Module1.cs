using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;
using System;
using System.Timers;

namespace APBridgeAddIn
{
    internal class Module1 : Module
    {
        private static Module1 _this = null;
        private ProBridgeService _bridgeService;
        private Timer _heartbeatTimer;
        private DateTime _startTime;

        public static Module1 Current =>
            _this ??= (Module1)FrameworkApplication.FindModule("APBridgeAddIn_Module");

        protected override bool Initialize()
        {
            _startTime = DateTime.UtcNow;
            _bridgeService = new ProBridgeService("ArcGisProBridgePipe");
            _bridgeService.Start();
            StartHeartbeat();
            return true;
        }

        protected override bool CanUnload()
        {
            return false;
        }

        protected override void Uninitialize()
        {
            StopHeartbeat();
            _bridgeService?.Dispose();
        }

        public void RestartBridge()
        {
            _bridgeService?.Dispose();
            _bridgeService = new ProBridgeService("ArcGisProBridgePipe");
            _bridgeService.Start();
            FrameworkApplication.DockPaneManager.Find("APBridgeAddIn_BridgeDashboard")?.Activate();
        }

        public ProBridgeService GetBridgeService() => _bridgeService;

        public string GetUptime()
        {
            var elapsed = DateTime.UtcNow - _startTime;
            if (elapsed.TotalDays >= 1)
                return $"{(int)elapsed.TotalDays}d {elapsed.Hours}h {elapsed.Minutes}m";
            if (elapsed.TotalHours >= 1)
                return $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m";
            return $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s";
        }

        private void StartHeartbeat()
        {
            StopHeartbeat();
            _heartbeatTimer = new Timer(30000);
            _heartbeatTimer.Elapsed += OnHeartbeat;
            _heartbeatTimer.AutoReset = true;
            _heartbeatTimer.Start();
        }

        private void StopHeartbeat()
        {
            if (_heartbeatTimer != null)
            {
                _heartbeatTimer.Stop();
                _heartbeatTimer.Dispose();
                _heartbeatTimer = null;
            }
        }

        private void OnHeartbeat(object sender, ElapsedEventArgs e)
        {
            try
            {
                var ok = _bridgeService?.PingSync() ?? false;
                var dockpane = FrameworkApplication.DockPaneManager.Find("APBridgeAddIn_BridgeDashboard") as Dockpanes.BridgeDashboardDockpane;
                if (dockpane?.ViewModel != null)
                {
                    dockpane.ViewModel.SetHeartbeat(ok, ok ? "Heartbeat OK" : "No response");
                }
            }
            catch
            {
            }
        }
    }
}
