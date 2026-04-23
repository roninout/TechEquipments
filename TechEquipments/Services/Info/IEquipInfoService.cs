using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TechEquipments
{
    /// <summary>
    /// Сервис карточки Info по оборудованию.
    /// </summary>
    public interface IEquipInfoService
    {
        Task EnsureTableAsync(CancellationToken ct = default);

        Task<EquipmentInfoDto> GetAsync(string equipName, CancellationToken ct = default);

        Task SaveAsync(EquipmentInfoDto model, CancellationToken ct = default);

        Task<IReadOnlyList<EquipmentInfoFileDto>> GetLibraryAsync(InfoFileKind kind, string equipTypeGroupKey, CancellationToken ct = default);

        Task<EquipInfoLibraryAddResult> AddFilesToLibraryAsync(InfoFileKind kind, string equipTypeGroupKey, IEnumerable<string> filePaths, CancellationToken ct = default);

        Task<EquipmentInfoFileDto?> GetLibraryFileByIdAsync(InfoFileKind kind, long id, CancellationToken ct = default);

        /// <summary>
        /// Получить сохранённую позицию просмотра PDF.
        /// </summary>
        Task<EquipmentInfoDocumentViewStateDto?> GetDocumentViewStateAsync(string equipName, InfoPageKind pageKind, long fileId, CancellationToken ct = default);

        /// <summary>
        /// Сохранить/обновить позицию просмотра PDF.
        /// </summary>
        Task SaveDocumentViewStateAsync(EquipmentInfoDocumentViewStateDto model, CancellationToken ct = default);

        /// <summary>
        /// Полностью удалить файл из shared library.
        /// ВАЖНО: из-за ON DELETE CASCADE файл автоматически отвяжется от всех equipment.
        /// </summary>
        Task<bool> DeleteLibraryFileAsync(InfoFileKind kind, long id, CancellationToken ct = default);

        /// <summary>
        /// Получить список equipment, отмеченных как favorite для текущего устройства.
        /// </summary>
        Task<IReadOnlyCollection<string>> GetFavoriteEquipNamesAsync(CancellationToken ct = default);

        /// <summary>
        /// Установить/снять favorite для текущего устройства.
        /// </summary>
        Task SetFavoriteAsync(string equipName, bool isFavorite, CancellationToken ct = default);

        Task EnsureDatabaseAndTablesAsync(CancellationToken ct = default);
    }
}