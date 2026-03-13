using System;
using System.Collections.Generic;
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

        /// <summary>
        /// Экспортирует карту "Station+Type -> Equipment" для сохранения в user-state.json.
        /// Хост сам должен вернуть уже очищенные/валидные данные.
        /// </summary>
        Dictionary<string, string> ExportRememberedEquipmentsByFilter();

        /// <summary>
        /// Импортирует карту "Station+Type -> Equipment" из user-state.json.
        /// На этом этапе список Equipments может быть ещё не загружен,
        /// поэтому здесь только принимаем и нормализуем входные данные.
        /// </summary>
        void ImportRememberedEquipmentsByFilter(Dictionary<string, string>? state);
    }
}