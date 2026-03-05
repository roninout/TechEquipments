using System;
using System.Windows.Threading;

namespace TechEquipments
{
    /// <summary>
    /// Минимальный контракт хоста для контролера UI state.
    /// </summary>
    public interface IUiStateHost
    {
        Dispatcher Dispatcher { get; }

        // UI поля, которые сохраняем/восстанавливаем
        string EquipName { get; set; }
        DateTime DbDate { get; set; }
        string SelectedStation { get; set; }
        EquipTypeGroup SelectedTypeFilter { get; set; }
        int SelectedMainTabIndex { get; set; }
    }
}