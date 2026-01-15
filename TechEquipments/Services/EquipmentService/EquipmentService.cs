using CtApi;
using DevExpress.Pdf;
using DevExpress.Text.Fonts;
using DevExpress.XtraEditors.Filtering;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TechEquipments
{
    public sealed class EquipmentService : IEquipmentService
    {
        private readonly ICtApiService _ctApiService;
        private const int windowMinutes = 60;

        public EquipmentService(ICtApiService ctApiService)
        {
            _ctApiService = ctApiService;
        }

        #region Equipment

        // возвращает данные эквипмента
        public async Task<EquipRefModel> GetEquipData(string sEquipName, string sEquipItem = "STW")
        {
            var sTagName = await _ctApiService.CicodeAsync($"TagInfo(\"{sEquipName}.{sEquipItem}\", 0)");
            var sEquipType = await _ctApiService.CicodeAsync($"EquipGetProperty(\"{sEquipName}\",\"Type\", 1)");
            var sEquipDescription = await _ctApiService.CicodeAsync($"EquipGetProperty(\"{sEquipName}\",\"Comment\", 1)");
            var sTrnName = await GetTrnName(sEquipName, sEquipItem);

            return new EquipRefModel
            {
                Name = sEquipName,
                TagName = sTagName,
                Type = sEquipType,
                Description = sEquipDescription,
                TrnName = sTrnName
            };
        }

        // возвращает моделт эквипмента с сылками
        public async Task<EquipModel> GetEquipModelWithRef(string sEquipName, string sEquipItem = "STW")
        {
            if (string.IsNullOrWhiteSpace(sEquipName))
                throw new ArgumentException("Equipment name is empty.", nameof(sEquipName));

            // главный эквип
            var main = await GetEquipData(sEquipName, sEquipItem);
            var model = new EquipModel{MainModel = main};

            // refs
            var equipRefNames = await GetEquipRef(sEquipName, "TabDIDO", sEquipItem) ?? new List<string>();

            // убираем мусор/дубликаты
            equipRefNames = equipRefNames
                .Where(n => !string.IsNullOrWhiteSpace(n) && !string.Equals(n, "Unknown", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (equipRefNames.Count == 0)
                return model;

            // грузим refs параллельно
            var tasks = equipRefNames.Select(n => GetEquipData(n, sEquipItem)).ToArray();
            var refs = await Task.WhenAll(tasks);

            model.RefEquipments.AddRange(refs.Where(r => r != null));

            return model;
        }

        // возвращает список названий EquipRef
        public async Task<List<string>> GetEquipRef(string sEquipName, string sCategory, string sEquipItem)
        {
            var sField = "REFEQUIP";
            var listEquip = new List<string>();

            var sCluster = await _ctApiService.CicodeAsync($"TagInfo(\"{sEquipName}.{sEquipItem}\", 17)");
            var sConnect = "CLUSTER=" + sCluster + ";EQUIP=" + sEquipName + ";REFCAT=" + sCategory;

            var hSession = await _ctApiService.CicodeAsync($"EquipRefBrowseOpen(\"{sConnect}\",\"{sField}\")");

            if (Convert.ToInt32(hSession) != -1)
            {
                var nNumRecords = await _ctApiService.CicodeAsync($"EquipRefBrowseNumRecords({hSession})");

                if (Convert.ToInt32(nNumRecords) > 0)
                {
                    var nReturn = await _ctApiService.CicodeAsync($"EquipRefBrowseFirst({hSession})");

                    while (Convert.ToInt32(nReturn) == 0)
                    {
                        var sEquip = await _ctApiService.CicodeAsync($"EquipRefBrowseGetField(\"{hSession}\", \"{sField}\")");
                        if (sEquip != null && sEquip != "Unknown")
                            listEquip.Add(sEquip);

                        nReturn = await _ctApiService.CicodeAsync($"EquipRefBrowseNext({hSession})");
                    }

                }
            }

            return listEquip;
        }


        #endregion

        #region Trend
        public async Task<string> GetTrnName(string sEquipName, string sEquipItem)
        {
            var sCluster = await _ctApiService.CicodeAsync($"TagInfo(\"{sEquipName}.{sEquipItem}\", 17)");
            var sTrnName = await _ctApiService.CicodeAsync($"_SATrend_GetTrendTag(\"{sCluster}\", \"{sEquipName}\", \"{sEquipItem}\")");

            return sTrnName;
        }

        public async Task<List<EquipmentSOEDto>> GetTrnByEquipment(EquipRefModel equipment, IProgress<int> progress = null, CancellationToken ct = default, int maxRows = 2000)
        {
            if (equipment == null) throw new ArgumentNullException(nameof(equipment));
            if (string.IsNullOrWhiteSpace(equipment.TrnName)) return new List<EquipmentSOEDto>();

            var dayStartUtc = DateTime.SpecifyKind(DateTime.Now.Date, DateTimeKind.Local).ToUniversalTime();
            var endUtc = DateTime.UtcNow;

            var result = new List<EquipmentSOEDto>(capacity: maxRows);

            long? lastWord = null; // исходное слово для сравнения
            bool hasLast = false;

            while (result.Count < maxRows && endUtc > dayStartUtc)
            {
                ct.ThrowIfCancellationRequested();

                var startUtc = endUtc.AddMinutes(-windowMinutes);
                if (startUtc < dayStartUtc)
                    startUtc = dayStartUtc;

                var trnData = await _ctApiService.GetTrnData(equipment.TrnName, startUtc, endUtc);

                if (trnData != null)
                {
                    foreach (var x in trnData)
                    {
                        ct.ThrowIfCancellationRequested();

                        if (x.ValueQuality.ToString() == "Bad")
                            break;

                        long curWord = ToLongWord(x.Value);

                        if (!hasLast)
                        {
                            // первую точку НЕ добавляем, только запоминаем базовое состояние
                            lastWord = curWord;
                            hasLast = true;
                            continue;
                        }

                        // "сжатие" по исходному слову
                        if (curWord == lastWord.Value)
                            continue;

                        ushort last16 = (ushort)(lastWord.Value & 0xFFFF);
                        ushort cur16 = (ushort)(curWord & 0xFFFF);

                        long bitCode = GetChangedBitCode(last16, cur16); // 1..32 или -99

                        result.Add(MapToDto(equipment, x, bitCode));

                        lastWord = curWord;

                        progress?.Report(result.Count);
                        if (result.Count >= maxRows)
                            break;
                    }
                }

                endUtc = startUtc.AddMilliseconds(-1);
            }

            //return result.OrderByDescending(x => x.TimeUtc).ToList();
            return result;
        }

        private static EquipmentSOEDto MapToDto(EquipRefModel equipment, TrnData x, long bitCode)
        {
            return new EquipmentSOEDto
            {
                TimeUtc = x.DateTime,
                Type = equipment.Type ?? "",
                Equipment = equipment.Name ?? "",
                Event = SoeEventMapper.GetEventText(equipment.Type, (int)bitCode),
                BitCode = bitCode,
                TrnValue = x.Value,

                ValueQuality = x.ValueQuality.ToString(),
            };
        }
        #endregion

        #region BitLogic
        public static int GetChangedBitCode(ushort last, ushort cur)
        {
            ushort diff = (ushort)(cur ^ last);

            if (diff == 0)
                return -99;

            int bitPos = 0; // 1..16

            for (int i = 0; i < 16; i++)
            {
                if ((diff & (1 << i)) != 0)
                {
                    bitPos = i + 1; // первый изменившийся бит
                    break;
                }
            }

            // направление (появился/пропал)
            bool nowSet = (cur & (1 << (bitPos - 1))) != 0;
            return nowSet ? bitPos : (bitPos + 16); // 1..16 = появился, 17..32 = пропал
        }

        private static long ToLongWord(double value)
        {
            // если тренд возвращает целое в double (2313.0), то округление норм
            return Convert.ToInt64(Math.Round(value, MidpointRounding.AwayFromZero));
        }
        #endregion
    }

}

