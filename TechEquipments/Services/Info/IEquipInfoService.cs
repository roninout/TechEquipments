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

        Task<IReadOnlyList<EquipmentInfoFileDto>> GetLibraryAsync(InfoFileKind kind, CancellationToken ct = default);

        Task<EquipInfoLibraryAddResult> AddFilesToLibraryAsync(
            InfoFileKind kind,
            IEnumerable<string> filePaths,
            CancellationToken ct = default);
    }
}