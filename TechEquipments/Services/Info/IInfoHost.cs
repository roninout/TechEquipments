using System.Windows;

namespace TechEquipments
{
    /// <summary>
    /// Минимальный контракт "хоста" для InfoController.
    /// Контроллер не знает про MainWindow напрямую.
    /// </summary>
    public interface IInfoHost
    {
        Window OwnerWindow { get; }

        EquipListBoxItem? SelectedListBoxEquipment { get; }

        string EquipName { get; set; }

        bool IsDbConnected { get; }

        EquipmentInfoDto? CurrentEquipInfo { get; set; }

        bool IsInfoLoading { get; set; }

        bool IsInfoEditMode { get; set; }

        string InfoStatusText { get; set; }

        // ===== Document/page state =====

        InfoPageKind CurrentInfoPage { get; set; }

        bool IsInfoDocumentPage { get; }

        string InfoDocumentMessage { get; set; }

        bool IsInfoDocumentExportVisible { get; set; }
    }
}