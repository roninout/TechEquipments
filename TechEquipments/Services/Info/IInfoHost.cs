using System.Collections.Generic;
using System.Collections.ObjectModel;
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

        EquipmentInfoFileDto? SelectedInfoPhotoFile { get; set; }
        EquipmentInfoFileDto? SelectedInfoInstructionFile { get; set; }
        EquipmentInfoFileDto? SelectedInfoSchemeFile { get; set; }

        ObservableCollection<EquipmentInfoFileDto> AvailableInfoPhotoLibrary { get; }
        ObservableCollection<EquipmentInfoFileDto> AvailableInfoInstructionLibrary { get; }
        ObservableCollection<EquipmentInfoFileDto> AvailableInfoSchemeLibrary { get; }

        List<object>? SelectedInfoPhotoLibraryIds { get; set; }
        List<object>? SelectedInfoInstructionLibraryIds { get; set; }
        List<object>? SelectedInfoSchemeLibraryIds { get; set; }

        bool IsInfoLoading { get; set; }

        bool IsInfoEditMode { get; set; }

        string InfoStatusText { get; set; }

        InfoPageKind CurrentInfoPage { get; set; }

        bool IsInfoDocumentPage { get; }

        string? CurrentInfoDocumentPreviewPath { get; set; }

        string InfoDocumentMessage { get; set; }

        bool IsInfoDocumentExportVisible { get; set; }
    }
}