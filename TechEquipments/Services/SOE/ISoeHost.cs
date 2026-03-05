using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;

namespace TechEquipments
{
    /// <summary>
    /// Минимальный контракт хоста для SOE загрузчика.
    /// </summary>
    public interface ISoeHost
    {
        Window OwnerWindow { get; }
        Dispatcher Dispatcher { get; }

        // UI state для overlay/progress
        bool IsLoading { get; set; }
        int LoadedCount { get; set; }
        int CurrentCount { get; set; }
        int TotalTrends { get; set; }
        int CurrentTrendIndex { get; set; }
        string CurrentTrendName { get; set; }

        // лимиты
        int PerTrendMax { get; }
        int TotalMax { get; }

        // rows target
        ObservableCollection<EquipmentSOEDto> EquipmentSoeRows { get; }
    }
}