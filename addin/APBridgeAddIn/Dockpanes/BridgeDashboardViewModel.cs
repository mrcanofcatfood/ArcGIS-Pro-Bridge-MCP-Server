using APBridgeAddIn.Models;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace APBridgeAddIn.Dockpanes
{
    internal class BridgeDashboardViewModel : INotifyPropertyChanged
    {
        private readonly ObservableCollection<BridgeActivity> _recentActivity;
        private string _statusText = "Initializing...";
        private bool _isConnected;
        private string _uptime = "-";
        private int _errorCount;
        private string _lastHeartbeat = "-";
        private string _activitySummary = "0 calls";

        public event PropertyChangedEventHandler PropertyChanged;

        public BridgeDashboardViewModel()
        {
            _recentActivity = new ObservableCollection<BridgeActivity>();
        }

        public ObservableCollection<BridgeActivity> RecentActivity => _recentActivity;

        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusColor)); }
        }

        public bool IsConnected
        {
            get => _isConnected;
            set { _isConnected = value; OnPropertyChanged(); }
        }

        public string Uptime
        {
            get => _uptime;
            set { _uptime = value; OnPropertyChanged(); }
        }

        public int ErrorCount
        {
            get => _errorCount;
            set { _errorCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(ErrorDisplay)); }
        }

        public string ErrorDisplay => ErrorCount > 0 ? $"{ErrorCount} errors" : "No errors";

        public string LastHeartbeat
        {
            get => _lastHeartbeat;
            set { _lastHeartbeat = value; OnPropertyChanged(); }
        }

        public string ActivitySummary
        {
            get => _activitySummary;
            set { _activitySummary = value; OnPropertyChanged(); }
        }

        public string StatusColor => IsConnected ? "#4CAF50" : "#F44336";
        public string StatusDot => IsConnected ? "\u25CF" : "\u25CB";

        public void AddActivity(BridgeActivity activity)
        {
            _recentActivity.Insert(0, activity);
            while (_recentActivity.Count > 50)
                _recentActivity.RemoveAt(_recentActivity.Count - 1);

            ActivitySummary = $"{_recentActivity.Count} calls shown";
            if (!activity.Ok) ErrorCount++;
        }

        public void RefreshFromBridge(ProBridgeService bridge)
        {
            if (bridge == null) return;

            var recent = bridge.GetRecentActivity(20);
            if (recent != null)
            {
                var existingOps = _recentActivity.Select(a => a.Timestamp).ToHashSet();
                foreach (var act in recent)
                {
                    if (!existingOps.Contains(act.Timestamp))
                    {
                        _recentActivity.Insert(0, act);
                        if (!act.Ok) ErrorCount++;
                    }
                }
                while (_recentActivity.Count > 50)
                    _recentActivity.RemoveAt(_recentActivity.Count - 1);

                ActivitySummary = $"{_recentActivity.Count} calls shown";
            }
        }

        public void SetHeartbeat(bool ok, string message)
        {
            IsConnected = ok;
            StatusText = ok ? "Connected" : $"Disconnected — {message}";
            LastHeartbeat = DateTime.Now.ToLocalTime().ToString("HH:mm:ss");
        }

        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
