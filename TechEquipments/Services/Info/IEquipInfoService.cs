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
    }
}