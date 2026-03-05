using System.Collections.ObjectModel;
using System.Windows;

namespace TechEquipments
{
    /// <summary>
    /// Мінімальний контракт "хоста" для QR-контролера.
    /// </summary>
    public interface IQrHost
    {
        Window OwnerWindow { get; }

        ObservableCollection<EquipListBoxItem> Equipments { get; }

        EquipListBoxItem? SelectedListBoxEquipment { get; }

        string EquipName { get; set; }

        string SelectedStation { get; set; }

        EquipTypeGroup SelectedTypeFilter { get; set; }

        MainTabKind SelectedMainTab { get; }

        int SelectedMainTabIndex { get; set; }

        /// <summary>Посимвольний пошук/виділення в ListBox.</summary>
        void DoIncrementalSearch(string text);

        /// <summary>Запуск Param polling після переходу/скану QR.</summary>
        void StartParamPolling();

        /// <summary>Оновити видимість кнопки Generate QR (OnPropertyChanged).</summary>
        void NotifyParamQrUiChanged();

        /// <summary>Текст статусу Param (в шапці вкладки Param).</summary>
        void SetParamStatusText(string text);
    }
}