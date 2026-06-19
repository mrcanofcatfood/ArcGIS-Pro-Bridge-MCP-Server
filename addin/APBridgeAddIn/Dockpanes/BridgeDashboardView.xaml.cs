using ArcGIS.Desktop.Framework.Dialogs;
using System.Windows;
using System.Windows.Controls;
using MsgBox = ArcGIS.Desktop.Framework.Dialogs.MessageBox;

namespace APBridgeAddIn.Dockpanes
{
    public partial class BridgeDashboardView : UserControl
    {
        public BridgeDashboardView()
        {
            InitializeComponent();
        }

        private void OnPingClick(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as BridgeDashboardViewModel;
            if (vm == null) return;

            try
            {
                var bridge = Module1.Current?.GetBridgeService();
                if (bridge == null)
                {
                    MsgBox.Show("Bridge service not available");
                    return;
                }

                var ok = bridge.PingSync();
                vm.SetHeartbeat(ok, ok ? "Ping OK" : "No response");
            }
            catch (System.Exception ex)
            {
                MsgBox.Show($"Ping failed: {ex.Message}");
                vm.SetHeartbeat(false, ex.Message);
            }
        }

        private void OnRestartClick(object sender, RoutedEventArgs e)
        {
            Module1.Current.RestartBridge();
            MsgBox.Show("Bridge server restarted");
        }

        private void OnClearClick(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as BridgeDashboardViewModel;
            if (vm == null) return;

            var bridge = Module1.Current?.GetBridgeService();
            bridge?.ClearActivity();
            vm.RecentActivity.Clear();
            vm.ErrorCount = 0;
        }
    }
}
