using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;
using System;
using System.Threading.Tasks;

namespace APBridgeAddIn
{
    internal class Module1 : Module
    {
        private static Module1 _this = null;
        private ProBridgeService _bridgeService;

        public static Module1 Current =>
            _this ??= (Module1)FrameworkApplication.FindModule("APBridgeAddIn_Module");

        protected override bool Initialize()
        {
            _bridgeService = new ProBridgeService("ArcGisProBridgePipe");
            _bridgeService.Start();
            System.Diagnostics.Debug.WriteLine("[APBridgeAddIn] Named pipe server started on ArcGisProBridgePipe");
            return true;
        }

        protected override bool CanUnload()
        {
            _bridgeService?.Dispose();
            return true;
        }

        public void RestartBridge()
        {
            _bridgeService?.Dispose();
            _bridgeService = new ProBridgeService("ArcGisProBridgePipe");
            _bridgeService.Start();
        }
    }
}
