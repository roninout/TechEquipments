using System.Collections.ObjectModel;
using System.Threading;
using System.Windows.Threading;

namespace TechEquipments
{
    /// <summary>
    /// Контракт между ParamRefsController и MainWindow.
    /// 
    /// MainWindow остаётся владельцем bindable-состояния:
    /// - ObservableCollection для PLC / DI / DO
    /// - CurrentParamSettingsPage
    /// - DryRunEquipName / DryRunModel
    /// - выбранное оборудование / фильтры / вкладка
    /// 
    /// А вся логика refs/navigation/refresh уходит в отдельный контроллер.
    /// </summary>
    public interface IParamRefsHost
    {
        Dispatcher Dispatcher { get; }

        // Вкладки / навигация
        MainTabKind SelectedMainTab { get; }
        int SelectedMainTabIndex { get; set; }

        // Param gate
        SemaphoreSlim ParamRwGate { get; }

        // Основные данные
        ObservableCollection<EquipListBoxItem> Equipments { get; }
        ObservableCollection<DiDoRefRow> ParamDiRows { get; }
        ObservableCollection<DiDoRefRow> ParamDoRows { get; }
        ObservableCollection<PlcRefRow> ParamPlcRows { get; }

        // Состояние Param settings
        ParamSettingsPage CurrentParamSettingsPage { get; set; }

        // Выбор оборудования / фильтры
        string EquipName { get; set; }
        string SelectedStation { get; set; }
        EquipTypeGroup SelectedTypeFilter { get; set; }

        // Текущее состояние DryRun
        void SetDryRunState(string? equipName, DryRunMotor? model);

        // Текущее linked-ATV состояние для Motor -> __EquipmentSic
        void SetLinkedAtvState(string? equipName, AtvModel? model);

        // Уведомление о завершении загрузки секции
        void NotifySectionLoaded(string equipName, ParamSettingsPage page, ParamLoadState state);

        // Helpers из MainWindow
        (string equipName, string equipType) ResolveSelectedEquipForParam();
        bool IsEquipmentVisible(EquipListBoxItem item);
        void ApplyFilters();
        void DoIncrementalSearch(string text);
        void ShowParamChart(bool reset = false);
        void StartParamPolling();

        /// <summary>
        /// Блокирует Param write-события на время программного обновления UI.
        /// Нужно для DryRun / PLC / DI_DO refresh, чтобы EditValueChanged не писал теги.
        /// </summary>
        void BeginSuppressParamWritesFromRefresh();

        /// <summary>
        /// Снимает блокировку Param write-событий после программного обновления UI.
        /// </summary>
        void EndSuppressParamWritesFromRefresh();
    }
}