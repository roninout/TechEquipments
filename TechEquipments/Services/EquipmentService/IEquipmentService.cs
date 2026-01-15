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
        Task<string> GetTrnName(string sEquipName, string sEquipItem);

        Task<List<EquipmentSOEDto>> GetTrnByEquipment(EquipRefModel equipment, IProgress<int>? progress = null, CancellationToken ct = default, int maxRows = 2000);

        Task<List<string>> GetEquipRef(string sEquipName, string sCategory, string sEquipItem ="STW");
        Task<EquipRefModel> GetEquipData(string sEquipName, string sEquipItem = "STW");
        Task<EquipModel> GetEquipModelWithRef(string sEquipName, string sEquipItem = "STW");
    }
}
