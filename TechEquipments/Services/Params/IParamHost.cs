using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace TechEquipments
{
    /// <summary>
    /// Контракт між ParamController і UI (MainWindow).
    /// Контролер НЕ знає про XAML/DevExpress, він працює через цей інтерфейс.
    /// </summary>
    public interface IParamHost
    {
        // UI
        Dispatcher Dispatcher { get; }
        MainTabKind SelectedMainTab { get; }
        bool TrendIsChartVisible { get; }
        bool IsEditingField { get; }

        // Param state
        SemaphoreSlim ParamRwGate { get; }
        DateTime ParamReadResumeAtUtc { get; set; }
        bool SuppressParamWritesFromPolling { get; set; }
        int ParamReadCycles { get; set; }

        // UI data
        string ParamStatusText { get; set; }
        string BottomText { get; set; }
        object CurrentParamModel { get; set; }
        ObservableCollection<ParamItem> ParamItems { get; }

        string CurrentCtUserName { get; set; }

        // Main Param model loading notifications
        void NotifyMainParamLoaded(string equipName, ParamLoadState state);

        // Core callbacks
        (string equipName, string equipType) ResolveSelectedEquipForParam();
        void Param_ResetAreaIfTypeGroupChanged(EquipTypeGroup newGroup);

        // Extra periodic updates
        Task RefreshActiveParamSectionAsync(CancellationToken ct);
        Task PollTrendOnceSafeAsync(CancellationToken ct);
    }
}