using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TechEquipments
{
    public interface IEquipmentService
    {
        public sealed record LoadingProgress(int TotalTrends, int CurrentTrendIndex, string CurrentTrendName, int CurrentTrendCount, int TotalLoaded);

        Task<string> GetTrnName(string sEquipName, string sEquipItem);

        Task<List<EquipmentSOEDto>> GetTrnByEquipment(EquipRefModel equipment, IProgress<int>? progress = null, CancellationToken ct = default, int maxRows = 2000);

        Task<List<string>> GetEquipRef(string sEquipName, string sCategory, string sEquipItem ="STW");
        Task<EquipRefModel> GetEquipData(string sEquipName, string sEquipItem = "STW");
        Task<EquipModel> GetEquipModelWithRef(string sEquipName, string sEquipItem = "STW");

        Task<List<EquipmentSOEDto>> GetDataFromEquipAsync(string equipName, IProgress<LoadingProgress>? progress = null, CancellationToken ct = default, int perTrendMax = 2000, int totalMax = 10000);
        Task<List<EquipListBoxItem>> GetAllEquipmentsAsync(IProgress<(int done, int total)>? progress = null, CancellationToken ct = default);

        Task<string> GetExternalTagAsync(CancellationToken ct = default);
    }
}
