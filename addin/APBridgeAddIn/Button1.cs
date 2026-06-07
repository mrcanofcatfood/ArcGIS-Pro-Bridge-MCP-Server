using ArcGIS.Desktop.Framework.Contracts;
using ArcGIS.Desktop.Framework.Dialogs;

namespace APBridgeAddIn
{
    internal class Button1 : Button
    {
        protected override void OnClick()
        {
            Module1.Current.RestartBridge();
            MessageBox.Show("ArcGIS Pro Bridge server restarted.");
        }
    }
}
