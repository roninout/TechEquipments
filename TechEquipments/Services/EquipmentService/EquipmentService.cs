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
using static TechEquipments.IEquipmentService;

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

        // формируем EquipmentSOEDto с данными по Equipment для отображение в таблице
        public async Task<List<EquipmentSOEDto>> GetDataFromEquipAsync(string equipName, IProgress<LoadingProgress>? progress = null, CancellationToken ct = default, int perTrendMax = 2000, int totalMax = 10000)
        {
            var model = await GetEquipModelWithRef(equipName);
            if (model?.MainModel == null)
                return new List<EquipmentSOEDto>();

            ct.ThrowIfCancellationRequested();

            var equipList = new List<EquipRefModel> { model.MainModel };
            if (model.RefEquipments != null && model.RefEquipments.Count > 0)
                equipList.AddRange(model.RefEquipments);

            equipList = equipList
                .Where(e => e != null && !string.IsNullOrWhiteSpace(e.TrnName))
                .GroupBy(e => e.TrnName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            int totalTrends = equipList.Count;

            var allRows = new List<EquipmentSOEDto>(capacity: Math.Min(totalMax, 10000));

            for (int i = 0; i < totalTrends; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (allRows.Count >= totalMax) break;

                var equip = equipList[i];

                int localCount = 0;
                progress?.Report(new LoadingProgress(
                    TotalTrends: totalTrends,
                    CurrentTrendIndex: i + 1,
                    CurrentTrendName: equip.TrnName,
                    CurrentTrendCount: 0,
                    TotalLoaded: allRows.Count));

                var localProgress = new Progress<int>(c =>
                {
                    localCount = c;
                    progress?.Report(new LoadingProgress(
                        TotalTrends: totalTrends,
                        CurrentTrendIndex: i + 1,
                        CurrentTrendName: equip.TrnName,
                        CurrentTrendCount: c,
                        TotalLoaded: allRows.Count + c));
                });

                var rows = await GetTrnByEquipment(equip, localProgress, ct, maxRows: perTrendMax);

                if (rows != null && rows.Count > 0)
                {
                    int remaining = totalMax - allRows.Count;
                    if (rows.Count > remaining)
                        allRows.AddRange(rows.Take(remaining));
                    else
                        allRows.AddRange(rows);
                }

                progress?.Report(new LoadingProgress(
                    TotalTrends: totalTrends,
                    CurrentTrendIndex: i + 1,
                    CurrentTrendName: equip.TrnName,
                    CurrentTrendCount: localCount,
                    TotalLoaded: allRows.Count));
            }

            ct.ThrowIfCancellationRequested();

            return allRows.OrderByDescending(r => r.TimeUtc).ToList();
        }

        #region Equipment

        // возвращает данные эквипмента
        public async Task<EquipRefModel> GetEquipData(string sEquipName, string sEquipItem = "STW")
        {
            var sTagName = await _ctApiService.CicodeAsync($"TagInfo(\"{sEquipName}.{sEquipItem}\", 0)");
            var sEquipType = await _ctApiService.CicodeAsync($"EquipGetProperty(\"{sEquipName}\",\"Type\", 3)");
            //if (sEquipType == "Unknown")
            //    sEquipType = await _ctApiService.CicodeAsync($"EquipGetProperty(\"{sEquipName}\",\"Type\", 1)");

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

        // проверка на существования тега
        private async Task<bool> IsTagExistAsync(string tagName)
        {
            if (string.IsNullOrWhiteSpace(tagName))
                return false;

            var result = await _ctApiService.CicodeAsync($"TagCheckIfExists({tagName})");

            return int.TryParse(result, out var exists) && exists == 1;
        }

        // Возвращает список названий всех Equipment
        public async Task<List<EquipListBoxItem>> GetAllEquipmentsAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            //var findHashTags = await _ctApiService.FindAsync("Tag","Tag=*_HASHCODE","","EQUIPMENT","TAG");
            var findHashTags = await _ctApiService.FindAsync("Tag", "Tag=*STW", "", "EQUIPMENT", "TAG");

            var items = findHashTags
                .Where(d =>
                    d.TryGetValue("EQUIPMENT", out var eq) && !string.IsNullOrWhiteSpace(eq) &&
                    d.TryGetValue("TAG", out var tag) && !string.IsNullOrWhiteSpace(tag))
                .Select(d => new EquipListBoxItem
                                    {                                       
                                        Equipment = d["EQUIPMENT"].Trim(),
                                        Tag = d["TAG"].Trim()
                })
                // уникальность по имени оборудования
                .GroupBy(x => x.Equipment, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(x => x.Equipment, StringComparer.OrdinalIgnoreCase)
                .ToList();


            //// 2) Добираем Type (ограничим параллелизм, иначе будет очень долго/тяжело)
            //const int parallelism = 8;
            //using var sem = new SemaphoreSlim(parallelism);

            //var tasks = items.Select(async x =>
            //{
            //    ct.ThrowIfCancellationRequested();
            //    await sem.WaitAsync(ct);
            //    try
            //    {
            //        // если вдруг в имени будут кавычки — экранируем
            //        var equipNameEsc = x.Equipment.Replace("\"", "\\\"");

            //        var type = await _ctApiService.CicodeAsync(
            //            $"EquipGetProperty(\"{equipNameEsc}\",\"Type\", 1)");

            //        type = (type ?? "").Trim();
            //        if (string.Equals(type, "Unknown", StringComparison.OrdinalIgnoreCase))
            //            type = "";

            //        return new EquipListBoxItem
            //        {
            //            Equipment = x.Equipment,
            //            Tag = x.Tag,
            //            Type = type
            //        };
            //    }
            //    finally
            //    {
            //        sem.Release();
            //    }
            //});

            //var itemss = (await Task.WhenAll(tasks))
            //    .OrderBy(x => x.Equipment, StringComparer.OrdinalIgnoreCase)
            //    .ToList();



            return items;
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

