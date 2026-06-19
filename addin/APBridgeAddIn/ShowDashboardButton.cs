using ArcGIS.Desktop.Framework.Contracts;
using APBridgeAddIn.Dockpanes;

namespace APBridgeAddIn
{
    internal class ShowDashboardButton : Button
    {
        protected override void OnClick()
        {
            BridgeDashboardDockpane.Show();
        }
    }
}
