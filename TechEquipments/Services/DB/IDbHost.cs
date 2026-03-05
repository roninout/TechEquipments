using System;
using System.Collections.ObjectModel;
using System.Windows.Threading;

namespace TechEquipments
{
    /// <summary>
    /// Мінімальний контракт "хоста" для DB-контролера.
    /// Контролер НЕ знає про MainWindow напряму — тільки про цей інтерфейс.
    /// </summary>
    public interface IDbHost
    {
        /// <summary>UI Dispatcher (щоб безпечно оновлювати колекції/властивості).</summary>
        Dispatcher Dispatcher { get; }

        /// <summary>Чи є з'єднання з БД (стан для UI).</summary>
        bool IsDbConnected { get; }
        void SetDbConnected(bool value);

        /// <summary>Чи йде зараз DB-завантаження (стан для UI).</summary>
        bool IsDbLoading { get; }
        void SetDbLoading(bool value);

        /// <summary>Текст внизу (BottomText).</summary>
        string BottomText { get; set; }

        /// <summary>Поточна вкладка.</summary>
        MainTabKind SelectedMainTab { get; }

        /// <summary>Чи вибрана DB-вкладка (OperationActions/AlarmHistory).</summary>
        bool IsDbTabSelected { get; }

        /// <summary>Дата для DB-запиту.</summary>
        DateTime DbDate { get; }

        /// <summary>Фільтр для DB-запиту (звичайно це EquipName.Trim()).</summary>
        string DbFilter { get; }

        /// <summary>Колекції для UI (оновлюємо всередині Dispatcher).</summary>
        ObservableCollection<OperatorActDTO> OperatorActRows { get; }
        ObservableCollection<AlarmHistoryDTO> AlarmHistoryRows { get; }
    }
}