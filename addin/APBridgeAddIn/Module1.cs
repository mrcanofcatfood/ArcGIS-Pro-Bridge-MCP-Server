using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;

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
            return true;
        }

        protected override bool CanUnload()
        {
            return false;
        }

        protected override void Uninitialize()
        {
            _bridgeService?.Dispose();
        }

        public void RestartBridge()
        {
            _bridgeService?.Dispose();
            _bridgeService = new ProBridgeService("ArcGisProBridgePipe");
            _bridgeService.Start();
        }
    }
}
