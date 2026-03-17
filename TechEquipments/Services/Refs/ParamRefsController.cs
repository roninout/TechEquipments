using CtApi;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TechEquipments
{
    /// <summary>
    /// Вынесенная логика Ref-секций Param:
    /// - PLC / DI_DO / DryRun refresh
    /// - переход по связанному оборудованию
    /// - сброс области Param при смене группы типа
    /// - синхронизация ObservableCollection без мигания
    /// </summary>
    public sealed class ParamRefsController
    {
        private readonly IEquipmentService _equipmentService;
        private readonly ICtApiService _ctApiService;
        private readonly IConfiguration _config;
        private readonly IParamRefsHost _host;

        // Последняя группа параметров, которую показывали на вкладке Param
        private EquipTypeGroup _lastParamTypeGroup = EquipTypeGroup.All;
        private bool _hasLastParamTypeGroup;

        public ParamRefsController(IEquipmentService equipmentService,ICtApiService ctApiService,IConfiguration config,IParamRefsHost host)
        {
            _equipmentService = equipmentService ?? throw new ArgumentNullException(nameof(equipmentService));
            _ctApiService = ctApiService ?? throw new ArgumentNullException(nameof(ctApiService));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        /// <summary>
        /// Устанавливает активную страницу Param settings.
        /// </summary>
        public void SetParamSettingsPage(ParamSettingsPage page)
        {
            _host.CurrentParamSettingsPage = page;
        }

        /// <summary>
        /// Обновляет только ту секцию Settings, которая сейчас активна.
        /// Вызывается из Param polling.
        /// </summary>
        public async Task RefreshActiveParamSectionAsync(CancellationToken ct)
        {
            // Обновляем секции только на вкладке Param
            if (_host.SelectedMainTab != MainTabKind.Param)
                return;

            var page = _host.CurrentParamSettingsPage;

            // Если открыт Chart, а не settings-page — ничего не делаем
            if (page == ParamSettingsPage.None)
                return;

            var (equipName, _) = _host.ResolveSelectedEquipForParam();
            equipName = (equipName ?? "").Trim();

            // Нет выбранного оборудования -> секция недоступна
            if (string.IsNullOrWhiteSpace(equipName))
            {
                _host.NotifySectionLoaded("", page, ParamLoadState.Unavailable);
                return;
            }

            // Список оборудования ещё не загружен -> секция пока недоступна
            if (_host.Equipments.Count == 0)
            {
                _host.NotifySectionLoaded(equipName, page, ParamLoadState.Unavailable);
                return;
            }

            try
            {
                switch (page)
                {
                    case ParamSettingsPage.DiDo:
                        await RefreshDiDoSectionAsync(ct);
                        break;

                    case ParamSettingsPage.Plc:
                        await RefreshPlcSectionAsync(ct);
                        break;

                    case ParamSettingsPage.DryRun:
                        await RefreshDryRunSectionAsync(ct);
                        break;

                    case ParamSettingsPage.Atv:
                        await RefreshAtvSectionAsync(ct);
                        break;

                    default:
                        // Для неподдерживаемых/пустых страниц сразу отдаём финальный статус
                        _host.NotifySectionLoaded(equipName, page, ParamLoadState.Unavailable);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                // Отмену не считаем ошибкой состояния UI
                throw;
            }
            catch
            {
                // Любая ошибка должна дать финальный сигнал,
                // иначе loading overlay может повиснуть
                _host.NotifySectionLoaded(equipName, page, ParamLoadState.Error);
                throw;
            }
        }

        /// <summary>
        /// Переход по клику из DI/DO списка:
        /// - гарантируем видимость в ListBox (если фильтры прячут — подстроим)
        /// - подставим EquipName
        /// - выделим в ListBox
        /// - откроем вкладку Param
        /// </summary>
        public void NavigateToLinkedEquip(DiDoRefRow? row)
        {
            if (row == null)
                return;

            var it = row.EquipItem;
            if (it == null)
                return;

            var targetName = (it.Equipment ?? "").Trim();
            if (string.IsNullOrWhiteSpace(targetName))
                return;

            // Если текущие фильтры прячут элемент — подстраиваем фильтры
            EnsureEquipmentVisibleInList(it);

            // Если прыгаем на оборудование другого типа — сбрасываем область Param
            var newGroup = EquipTypeRegistry.GetGroup(row.EquipItem?.Type ?? "");
            ResetAreaIfTypeGroupChanged(newGroup);

            // Выставляем имя оборудования
            _host.EquipName = targetName;

            // Выделяем слева
            _host.DoIncrementalSearch(targetName);

            // Открываем вкладку Param
            if (_host.SelectedMainTab != MainTabKind.Param)
            {
                _host.SelectedMainTabIndex = (int)MainTabKind.Param;
                return;
            }

            // Если уже на Param — обновляем polling
            _host.StartParamPolling();
        }

        /// <summary>
        /// Переход к оборудованию по имени (используется из PLC settings / ссылок).
        /// </summary>
        public void NavigateToLinkedEquip(string? equipName)
        {
            var key = (equipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(key))
                return;

            // Пытаемся найти оборудование в полном списке, чтобы корректно подстроить фильтры
            var it =
                _host.Equipments.FirstOrDefault(x => string.Equals(x.Equipment, key, StringComparison.OrdinalIgnoreCase)) ??
                _host.Equipments.FirstOrDefault(x => string.Equals(x.Tag, key, StringComparison.OrdinalIgnoreCase));

            if (it != null)
            {
                EnsureEquipmentVisibleInList(it);

                // Если прыгаем на оборудование другой группы — сбрасываем область Param
                var newGroup = EquipTypeRegistry.GetGroup(it.Type ?? "");
                ResetAreaIfTypeGroupChanged(newGroup);

                // Нормализуем имя на реальное Equipment
                key = (it.Equipment ?? key).Trim();
            }

            _host.EquipName = key;
            _host.DoIncrementalSearch(key);

            if (_host.SelectedMainTab != MainTabKind.Param)
            {
                _host.SelectedMainTabIndex = (int)MainTabKind.Param;
                return;
            }

            _host.StartParamPolling();
        }

        /// <summary>
        /// Если группа оборудования изменилась (например VGD -> DI), сбрасываем UI Param на дефолт:
        /// - показываем Chart
        /// - сбрасываем активную секцию настроек = None
        /// - очищаем ref-списки, чтобы не светились чужие данные
        /// </summary>
        public void ResetAreaIfTypeGroupChanged(EquipTypeGroup newGroup)
        {
            if (!_hasLastParamTypeGroup)
            {
                _hasLastParamTypeGroup = true;
                _lastParamTypeGroup = newGroup;
                return;
            }

            if (_lastParamTypeGroup == newGroup)
                return;

            _lastParamTypeGroup = newGroup;

            // Сбрасываем выбранную секцию Settings
            SetParamSettingsPage(ParamSettingsPage.None);

            // Показываем Chart по умолчанию
            _host.ShowParamChart(reset: false);

            // Чистим ref-списки
            _host.ParamDiRows.Clear();
            _host.ParamDoRows.Clear();
            _host.ParamPlcRows.Clear();
        }

        /// <summary>
        /// Обновляет DI/DO секции:
        /// - читает EquipRef(category="TabDIDO")
        /// - находит DI/DO в общем Equipments
        /// - перечитывает DIParam/DOParam (Value может меняться)
        /// - синхронизирует ObservableCollection без мигания
        /// </summary>
        private async Task RefreshDiDoSectionAsync(CancellationToken ct)
        {
            var (equipName, _) = _host.ResolveSelectedEquipForParam();
            equipName = (equipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(equipName))
                return;

            // сериализуем с Param чтением/записью
            await _host.ParamRwGate.WaitAsync(ct);
            try
            {
                // 1) refs
                var refs = await _equipmentService.GetEquipRef(equipName, "TabDIDO", "State") ?? new List<string>();

                var refNames = refs
                    .Where(s => !string.IsNullOrWhiteSpace(s) && !s.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                    .Select(s => s.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (refNames.Count == 0)
                {
                    await _host.Dispatcher.InvokeAsync(() =>
                    {
                        _host.ParamDiRows.Clear();
                        _host.ParamDoRows.Clear();
                    });

                    _host.NotifySectionLoaded(equipName, ParamSettingsPage.DiDo, ParamLoadState.Unavailable);
                    return;
                }

                // 2) разложим refs на DI и DO по EquipTypeGroup
                var diEquip = new List<EquipListBoxItem>();
                var doEquip = new List<EquipListBoxItem>();

                foreach (var refName in refNames)
                {
                    ct.ThrowIfCancellationRequested();

                    var equip = _host.Equipments.FirstOrDefault(x =>
                        string.Equals((x.Equipment ?? "").Trim(), refName, StringComparison.OrdinalIgnoreCase));

                    if (equip == null)
                        continue;

                    var grp = EquipTypeRegistry.GetGroup(equip.Type ?? "");

                    if (grp == EquipTypeGroup.DI)
                        diEquip.Add(equip);
                    else if (grp == EquipTypeGroup.DO)
                        doEquip.Add(equip);
                }

                var maxPar = _config.GetValue<int>("CtApi:TagReadParallelism", 1);
                if (maxPar < 1) maxPar = 1;

                // 3) читаем DI/DO параллельно, но с лимитом
                var diRows = await RunLimitedAsync(diEquip, maxPar, async equip =>
                {
                    var model = await _equipmentService.ReadEquipParamsAsync<DIParam>(equip.Equipment.Trim(), ct);
                    return model != null ? new DiDoRefRow(equip, model) : null;
                }, ct);

                var doRows = await RunLimitedAsync(doEquip, maxPar, async equip =>
                {
                    var model = await _equipmentService.ReadEquipParamsAsync<DOParam>(equip.Equipment.Trim(), ct);
                    return model != null ? new DiDoRefRow(equip, model) : null;
                }, ct);

                // null убрать
                var diNew = diRows
                    .Where(x => x != null)
                    .Select(x => x!)
                    .ToDictionary(x => x.EquipName, StringComparer.OrdinalIgnoreCase);

                var doNew = doRows
                    .Where(x => x != null)
                    .Select(x => x!)
                    .ToDictionary(x => x.EquipName, StringComparer.OrdinalIgnoreCase);

                // 4) sync collections on UI thread
                await _host.Dispatcher.InvokeAsync(() =>
                {
                    SyncRows(_host.ParamDiRows, diNew);
                    SortRowsByChanel(_host.ParamDiRows);

                    SyncRows(_host.ParamDoRows, doNew);
                    SortRowsByChanel(_host.ParamDoRows);
                });

                // ВАЖНО:
                // DI/DO секция успешно дочитана и синхронизирована.
                // Без этого overlay будет висеть, хотя данные уже на экране.
                _host.NotifySectionLoaded(equipName, ParamSettingsPage.DiDo, ParamLoadState.Ready);
            }
            finally
            {
                _host.ParamRwGate.Release();
            }
        }

        /// <summary>
        /// Обновляет PLC секцию:
        /// - читает REFEQUIP category=TabPLC
        /// - кеширует TagName/Unit/ForcedTagName
        /// - читает значения параллельно с лимитом
        /// - обновляет ObservableCollection без пересоздания
        /// </summary>
        private async Task RefreshPlcSectionAsync(CancellationToken ct)
        {
            var (equipName, _) = _host.ResolveSelectedEquipForParam();
            equipName = (equipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(equipName))
                return;

            const string category = "TabPLC";
            const string clusterEquipItem = "State";

            await _host.ParamRwGate.WaitAsync(ct);
            try
            {
                // 1) refs
                var fresh = await _equipmentService.GetEquipRef(equipName, category, clusterEquipItem, "CUSTOM1") ?? new List<PlcRefRow>();

                if (fresh.Count == 0)
                {
                    await _host.Dispatcher.InvokeAsync(() => _host.ParamPlcRows.Clear());
                    _host.NotifySectionLoaded(equipName, ParamSettingsPage.Plc, ParamLoadState.Unavailable);
                    return;
                }

                // 2) sync списка
                await _host.Dispatcher.InvokeAsync(() => SyncPlcRows(_host.ParamPlcRows, fresh));

                // 3) snapshot
                var snapshot = await _host.Dispatcher.InvokeAsync(() => _host.ParamPlcRows.ToList());

                // 4) I/O: TagInfo -> TagRead пакетно
                var meta = new List<(PlcRefRow row, string tagName, string unit, string forcedTag)>(snapshot.Count);
                var tagsToRead = new List<string>(snapshot.Count * 2);

                foreach (var row in snapshot)
                {
                    ct.ThrowIfCancellationRequested();

                    // resolve TagName (кэш в row.TagName)
                    var tagName = (row.TagName ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(tagName))
                    {
                        var equipItem = GetPlcEquipItemForTagInfo(row);
                        try
                        {
                            tagName = (await _ctApiService.CicodeAsync($"TagInfo(\"{row.EquipName}.{equipItem}\", 0)") ?? "").Trim();
                        }
                        catch
                        {
                            tagName = "";
                        }
                    }

                    // resolve Unit (кэш в row.Unit)
                    var unit = (row.Unit ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(unit) &&
                        !string.IsNullOrWhiteSpace(tagName) &&
                        !tagName.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            unit = (await _ctApiService.CicodeAsync($"TagInfo(\"{tagName}\", 1)") ?? "").Trim();
                        }
                        catch
                        {
                            unit = "";
                        }
                    }

                    // resolve ForcedTagName (кэш в row.ForcedTagName)
                    var forcedTag = "";
                    if (row.Type is PlcTypeCustom.EqDigital or PlcTypeCustom.EqDigitalInOut)
                    {
                        forcedTag = (row.ForcedTagName ?? "").Trim();
                        if (string.IsNullOrWhiteSpace(forcedTag))
                        {
                            try
                            {
                                forcedTag = (await _ctApiService.CicodeAsync($"TagInfo(\"{row.EquipName}.ValueForced\", 0)") ?? "").Trim();
                            }
                            catch
                            {
                                forcedTag = "";
                            }
                        }
                    }

                    meta.Add((row, tagName, unit, forcedTag));

                    if (!string.IsNullOrWhiteSpace(tagName) && !tagName.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                        tagsToRead.Add(tagName);

                    if (!string.IsNullOrWhiteSpace(forcedTag) && !forcedTag.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                        tagsToRead.Add(forcedTag);
                }

                tagsToRead = tagsToRead.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var rawMap = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

                if (tagsToRead.Count > 0)
                {
                    var maxPar = _config.GetValue<int>("CtApi:TagReadParallelism", 1);
                    if (maxPar < 1) maxPar = 1;

                    rawMap = await TagReadManyAsync(tagsToRead, maxPar, ct);
                }

                // 5) подготовка updates
                var updates = new List<(PlcRefRow row, string tagName, double? value, string unit, bool? forced, string forcedTag)>(meta.Count);

                foreach (var m in meta)
                {
                    ct.ThrowIfCancellationRequested();

                    double? val = null;
                    if (!string.IsNullOrWhiteSpace(m.tagName) &&
                        rawMap.TryGetValue(m.tagName, out var raw) &&
                        raw != null)
                    {
                        val = TryParseDouble(raw);
                    }

                    bool? forced = null;
                    if (m.row.Type is PlcTypeCustom.EqDigital or PlcTypeCustom.EqDigitalInOut)
                    {
                        if (!string.IsNullOrWhiteSpace(m.forcedTag) &&
                            rawMap.TryGetValue(m.forcedTag, out var fraw) &&
                            fraw != null)
                        {
                            var s = fraw.Trim();
                            forced = s == "1" || s.Equals("True", StringComparison.OrdinalIgnoreCase);
                        }
                        else
                        {
                            forced = false;
                        }
                    }

                    updates.Add((m.row, m.tagName, val, m.unit, forced, m.forcedTag));
                }

                // 6) apply UI одним заходом
                await _host.Dispatcher.InvokeAsync(() =>
                {
                    foreach (var u in updates)
                    {
                        if (!string.IsNullOrWhiteSpace(u.tagName))
                            u.row.TagName = u.tagName;

                        if (!string.IsNullOrWhiteSpace(u.unit) && !u.unit.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                            u.row.Unit = u.unit;

                        if (!string.IsNullOrWhiteSpace(u.forcedTag) && !u.forcedTag.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                            u.row.ForcedTagName = u.forcedTag;

                        u.row.UpdateValue(u.value);

                        if (u.forced.HasValue)
                            u.row.ValueForced = u.forced.Value;
                        else if (u.row.Type is PlcTypeCustom.EqDigital or PlcTypeCustom.EqDigitalInOut)
                            u.row.ValueForced = false;
                    }
                });

                _host.NotifySectionLoaded(equipName, ParamSettingsPage.Plc, ParamLoadState.Ready);
            }
            finally
            {
                _host.ParamRwGate.Release();
            }
        }

        /// <summary>
        /// Обновляет DryRun секцию:
        /// 1) находим ref equipment через WinOpened
        /// 2) читаем DryRunMotor с найденного оборудования
        /// 3) дополнительно ищем linked DI/AI через ASSOC:
        ///    - _dryRunDI
        ///    - _dryRunAI
        /// 4) если ссылок нет или linked equipment нет в нашем списке -> оставляем null
        /// </summary>
        private async Task RefreshDryRunSectionAsync(CancellationToken ct)
        {
            var (equipName, _) = _host.ResolveSelectedEquipForParam();
            equipName = (equipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(equipName))
            {
                _host.NotifySectionLoaded("", ParamSettingsPage.DryRun, ParamLoadState.Unavailable);
                return;
            }

            // 1) Ищем базовый DryRun equipment через WinOpened от мотора
            var winRef = await _equipmentService.GetWinOpenedRefAsync(
                sEquipName: equipName,
                sEquipItem: "State",
                sCategory: "WinOpened",
                assocExpected: "__EquipmentPump");

            if (winRef == null || string.IsNullOrWhiteSpace(winRef.RefEquip))
            {
                await _host.Dispatcher.InvokeAsync(() =>
                {
                    _host.BeginSuppressParamWritesFromRefresh();
                    try
                    {
                        _host.SetDryRunState(null, null);
                    }
                    finally
                    {
                        _host.EndSuppressParamWritesFromRefresh();
                    }
                });

                _host.NotifySectionLoaded(equipName, ParamSettingsPage.DryRun, ParamLoadState.Unavailable);
                return;
            }

            var dryRunEquipName = winRef.RefEquip.Trim();

            // 2) Читаем основные DryRun теги уже с найденного оборудования
            var model = await _equipmentService.ReadEquipParamsAsync<DryRunMotor>(dryRunEquipName, ct)
                       ?? new DryRunMotor();

            // 3) Ищем linked DI и linked AI уже от DryRunEquipName
            var (diEquipName, diModel, diRow) = await TryResolveDryRunDiAsync(dryRunEquipName, ct);
            var (aiEquipName, aiModel, aiTitle) = await TryResolveDryRunAiAsync(dryRunEquipName, ct);

            // 4) Подключаем ссылки к DryRun model
            model.DryRunDiEquipName = diEquipName;
            model.DryRunDiModel = diModel;
            model.DryRunDiRow = diRow;

            model.DryRunAiEquipName = aiEquipName;
            model.DryRunAiModel = aiModel;
            model.DryRunAiTitle = aiTitle;

            await _host.Dispatcher.InvokeAsync(() =>
            {
                _host.BeginSuppressParamWritesFromRefresh();
                try
                {
                    _host.SetDryRunState(dryRunEquipName, model);
                }
                finally
                {
                    _host.EndSuppressParamWritesFromRefresh();
                }
            });

            _host.NotifySectionLoaded(equipName, ParamSettingsPage.DryRun, ParamLoadState.Ready);
        }

        /// <summary>
        /// Если элемент скрыт фильтрами Station/Type — меняем фильтры так, чтобы элемент был видим.
        /// </summary>
        private void EnsureEquipmentVisibleInList(EquipListBoxItem it)
        {
            try
            {
                if (_host.IsEquipmentVisible(it))
                    return;

                if (!string.IsNullOrWhiteSpace(it.Station))
                    _host.SelectedStation = it.Station.Trim();
                else
                    _host.SelectedStation = "All";

                var grp = EquipTypeRegistry.GetGroup(it.Type ?? "");
                _host.SelectedTypeFilter = grp != EquipTypeGroup.All ? grp : EquipTypeGroup.All;

                _host.ApplyFilters();
            }
            catch
            {
                // best-effort
            }
        }

        /// <summary>
        /// Параллельный helper с ограничением степени параллелизма.
        /// </summary>
        private static async Task<List<TResult>> RunLimitedAsync<TItem, TResult>(List<TItem> items,int maxConcurrency,Func<TItem, Task<TResult>> work,CancellationToken token)
        {
            var results = new ConcurrentBag<TResult>();
            using var sem = new SemaphoreSlim(Math.Max(1, maxConcurrency), Math.Max(1, maxConcurrency));

            var tasks = items.Select(async it =>
            {
                await sem.WaitAsync(token);
                try
                {
                    var r = await work(it);
                    results.Add(r);
                }
                finally
                {
                    sem.Release();
                }
            });

            await Task.WhenAll(tasks);
            return results.ToList();
        }

        /// <summary>
        /// Параллельный TagRead с лимитом.
        /// </summary>
        private async Task<Dictionary<string, string?>> TagReadManyAsync(List<string> tags, int maxConcurrency, CancellationToken token)
        {
            var result = new ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            using var sem = new SemaphoreSlim(Math.Max(1, maxConcurrency), Math.Max(1, maxConcurrency));

            var tasks = tags.Select(async tag =>
            {
                await sem.WaitAsync(token);
                try
                {
                    result[tag] = await _ctApiService.TagReadAsync(tag);
                }
                catch
                {
                    result[tag] = null;
                }
                finally
                {
                    sem.Release();
                }
            });

            await Task.WhenAll(tasks);
            return new Dictionary<string, string?>(result, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Синхронизация DI/DO коллекции без полного Clear/Add.
        /// </summary>
        private static void SyncRows(ObservableCollection<DiDoRefRow> target, Dictionary<string, DiDoRefRow> newMap)
        {
            for (int i = target.Count - 1; i >= 0; i--)
            {
                var key = target[i].EquipName;
                if (!newMap.ContainsKey(key))
                    target.RemoveAt(i);
            }

            var existing = target.ToDictionary(x => x.EquipName, StringComparer.OrdinalIgnoreCase);

            foreach (var kv in newMap)
            {
                if (existing.TryGetValue(kv.Key, out var row))
                {
                    row.Update(kv.Value.EquipItem, kv.Value.ParamModel);
                }
                else
                {
                    target.Add(kv.Value);
                }
            }
        }

        /// <summary>
        /// Возвращает числовой ключ сортировки по ChanelShort:
        /// "6.3.4" -> 006_003_004
        /// "6.3"   -> 006_003_000
        /// Если канал пустой/непарсится -> уходит в конец.
        /// </summary>
        private static long GetChanelSortKey(DiDoRefRow row)
        {
            if (row == null) return long.MaxValue;

            var raw = (row.ChanelShort ?? "").Trim();
            if (raw.Length == 0) return long.MaxValue;

            var parts = raw.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            static int ParsePart(string? s)
                => int.TryParse(s, out var v) ? v : int.MaxValue;

            var a = parts.Length > 0 ? ParsePart(parts[0]) : int.MaxValue;
            var b = parts.Length > 1 ? ParsePart(parts[1]) : int.MaxValue;
            var c = parts.Length > 2 ? ParsePart(parts[2]) : 0;

            return (long)a * 1_000_000L + (long)b * 1_000L + (long)c;
        }

        /// <summary>
        /// Сортировка ObservableCollection через Move без мигания UI.
        /// </summary>
        private static void SortRowsByChanel(ObservableCollection<DiDoRefRow> rows)
        {
            if (rows == null || rows.Count <= 1)
                return;

            var sorted = rows
                .OrderBy(GetChanelSortKey)
                .ThenBy(r => r.EquipName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (int targetIndex = 0; targetIndex < sorted.Count; targetIndex++)
            {
                var item = sorted[targetIndex];
                var currentIndex = rows.IndexOf(item);
                if (currentIndex >= 0 && currentIndex != targetIndex)
                    rows.Move(currentIndex, targetIndex);
            }
        }

        /// <summary>
        /// Синхронизация PLC rows без затирания кэша TagName/Value.
        /// Поддерживает несколько PLC-строк с одинаковым EquipName,
        /// если у них разный Type.
        /// </summary>
        private static void SyncPlcRows(ObservableCollection<PlcRefRow> target, List<PlcRefRow> fresh)
        {
            // 1) Нормализуем fresh и убираем точные дубли по ключу EquipName+Type.
            var freshMap = fresh
                .Where(x => !string.IsNullOrWhiteSpace(x.EquipName))
                .GroupBy(GetPlcRowKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var freshKeys = new HashSet<string>(freshMap.Keys, StringComparer.OrdinalIgnoreCase);

            // 2) Удаляем из target строки, которых уже нет в fresh.
            for (int i = target.Count - 1; i >= 0; i--)
            {
                var key = GetPlcRowKey(target[i]);
                if (!freshKeys.Contains(key))
                    target.RemoveAt(i);
            }

            // 3) Дополнительно убираем возможные дубли уже внутри target,
            //    чтобы existing.ToDictionary никогда не падал.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = target.Count - 1; i >= 0; i--)
            {
                var key = GetPlcRowKey(target[i]);
                if (!seen.Add(key))
                    target.RemoveAt(i);
            }

            // 4) Строим карту уже существующих UI-строк.
            var existing = target.ToDictionary(
                x => GetPlcRowKey(x),
                x => x,
                StringComparer.OrdinalIgnoreCase);

            // 5) Обновляем существующие строки или добавляем новые.
            foreach (var kv in freshMap)
            {
                var freshRow = kv.Value;

                if (existing.TryGetValue(kv.Key, out var row))
                {
                    // Обновляем только метаданные.
                    row.UpdateMeta(freshRow.RefItem, freshRow.Type, freshRow.Comment);

                    // Не затираем TagName пустым/Unknown значением.
                    if (!string.IsNullOrWhiteSpace(freshRow.TagName) &&
                        !freshRow.TagName.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                    {
                        row.TagName = freshRow.TagName;
                    }
                }
                else
                {
                    target.Add(freshRow);
                }
            }
        }

        /// <summary>
        /// Уникальный ключ PLC-строки.
        /// Для одной equipment может существовать несколько PLC refs
        /// с одинаковым Type, но разным REFITEM.
        /// Поэтому берём:
        /// EquipName + RefItem + Type.
        /// Comment в ключ не включаем, потому что он может меняться
        /// по языку/формулировке и ломать стабильную идентификацию строки.
        /// </summary>
        private static string GetPlcRowKey(PlcRefRow row)
        {
            var equip = (row?.EquipName ?? "").Trim();
            var refItem = (row?.RefItem ?? "").Trim();
            return $"{equip}\u001F{refItem}\u001F{(int)row.Type}";
        }

        /// <summary>
        /// Parse double из разных форматов.
        /// </summary>
        private static double? TryParseDouble(string? s)
        {
            s = (s ?? "").Trim();
            if (s.Length == 0 || s.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                return null;

            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                return d;

            if (double.TryParse(s, NumberStyles.Float, CultureInfo.GetCultureInfo("ru-RU"), out d))
                return d;

            return null;
        }

        /// <summary>
        /// Определяет, какой equip item использовать для TagInfo/TagWrite.
        /// 
        /// Приоритет:
        /// 1) REFITEM из EquipRefBrowse (если он есть)
        /// 2) legacy-fallback по типу строки
        /// </summary>
        private static string GetPlcEquipItemForTagInfo(PlcRefRow row)
        {
            var refItem = (row?.RefItem ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(refItem) &&
                !refItem.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            {
                return refItem;
            }

            if (row.Type is PlcTypeCustom.EqMotorStatus or PlcTypeCustom.EqValveStatus)
                return "State";

            return "Value";
        }

        /// <summary>
        /// Ищет equipment в уже загруженном списке.
        /// Сначала по Equipment, потом по Tag.
        /// </summary>
        private EquipListBoxItem? FindLoadedEquipment(string? key)
        {
            var name = (key ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name))
                return null;

            return _host.Equipments.FirstOrDefault(x => string.Equals((x.Equipment ?? "").Trim(), name, StringComparison.OrdinalIgnoreCase))
                ?? _host.Equipments.FirstOrDefault(x => string.Equals((x.Tag ?? "").Trim(), name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Ищет linked DI для DryRun через WinOpened/ASSOC="_dryRunDI".
        /// Ищем уже не от мотора, а от DryRunEquipName (например S17.P01.P01).
        /// 
        /// Возвращаем:
        /// - linked equipment name
        /// - DiModel
        /// - готовую DiDoRefRow для UI (как в обычной секции DI/DO)
        /// </summary>
        private async Task<(string? equipName, DiModel? model, DiDoRefRow? row)> TryResolveDryRunDiAsync(string dryRunEquipName, CancellationToken ct)
        {
            var winRef = await _equipmentService.GetWinOpenedRefAsync(
                sEquipName: dryRunEquipName,
                sEquipItem: "DryRunA",
                sCategory: "WinOpened",
                assocExpected: "_dryRunDI");

            if (winRef == null || string.IsNullOrWhiteSpace(winRef.RefEquip))
                return (null, null, null);

            var equip = FindLoadedEquipment(winRef.RefEquip);
            if (equip == null)
                return (null, null, null);

            var group = EquipTypeRegistry.GetGroup(equip.Type ?? "");
            if (group != EquipTypeGroup.DI)
                return (null, null, null);

            var linkedEquipName = (equip.Equipment ?? winRef.RefEquip).Trim();
            if (string.IsNullOrWhiteSpace(linkedEquipName))
                return (null, null, null);

            var param = await _equipmentService.ReadEquipParamsAsync<DIParam>(linkedEquipName, ct);
            if (param == null)
                return (null, null, null);

            var diModel = new DiModel(param);

            // UI для DryRun DI хотим показать тем же форматом, что и обычный DI/DO list.
            // Поэтому сразу собираем DiDoRefRow.
            var row = new DiDoRefRow(equip, diModel.Param);

            return (linkedEquipName, diModel, row);
        }


        /// <summary>
        /// Ищет linked AI для DryRun через WinOpened/ASSOC="_dryRunAI".
        /// Ищем уже не от мотора, а от DryRunEquipName.
        /// 
        /// Возвращаем:
        /// - linked equipment name
        /// - AiModel
        /// - UI title ("Equipment: Description")
        /// </summary>
        private async Task<(string? equipName, AiModel? model, string? title)> TryResolveDryRunAiAsync(string dryRunEquipName, CancellationToken ct)
        {
            var winRef = await _equipmentService.GetWinOpenedRefAsync(
                sEquipName: dryRunEquipName,
                sEquipItem: "DryRunA",
                sCategory: "WinOpened",
                assocExpected: "_dryRunAI");

            if (winRef == null || string.IsNullOrWhiteSpace(winRef.RefEquip))
                return (null, null, null);

            var equip = FindLoadedEquipment(winRef.RefEquip);
            if (equip == null)
                return (null, null, null);

            var group = EquipTypeRegistry.GetGroup(equip.Type ?? "");
            if (group != EquipTypeGroup.AI)
                return (null, null, null);

            var linkedEquipName = (equip.Equipment ?? winRef.RefEquip).Trim();
            if (string.IsNullOrWhiteSpace(linkedEquipName))
                return (null, null, null);

            var param = await _equipmentService.ReadEquipParamsAsync<AIParam>(linkedEquipName, ct);
            if (param == null)
                return (null, null, null);

            // Title как у ref-строк:
            // если есть Description -> "Equipment:    Description"
            // если нет -> просто Equipment
            var title = string.IsNullOrWhiteSpace(equip.Description)
                ? linkedEquipName
                : $"{linkedEquipName}:    {equip.Description}";

            return (linkedEquipName, new AiModel(param), title);
        }

        /// <summary>
        /// Обновляет ATV-секцию:
        /// - для Motor ищет linked ATV через WinOpened / __EquipmentSic
        /// - читает AtvParam с найденного equipment
        /// - кладёт результат в host.LinkedAtvModel
        /// </summary>
        private async Task RefreshAtvSectionAsync(CancellationToken ct)
        {
            var (equipName, equipType) = _host.ResolveSelectedEquipForParam();
            equipName = (equipName ?? "").Trim();
            equipType = (equipType ?? "").Trim();

            // Если текущее оборудование не выбрано — очищаем linked ATV
            if (string.IsNullOrWhiteSpace(equipName))
            {
                await _host.Dispatcher.InvokeAsync(() =>
                {
                    _host.BeginSuppressParamWritesFromRefresh();
                    try
                    {
                        _host.SetLinkedAtvState(null, null);
                    }
                    finally
                    {
                        _host.EndSuppressParamWritesFromRefresh();
                    }
                });

                _host.NotifySectionLoaded("", ParamSettingsPage.Atv, ParamLoadState.Unavailable);
                return;
            }

            var group = EquipTypeRegistry.GetGroup(equipType);

            // ATV-секция внутри мотора нужна только для Motor.
            if (group != EquipTypeGroup.Motor)
            {
                await _host.Dispatcher.InvokeAsync(() =>
                {
                    _host.BeginSuppressParamWritesFromRefresh();
                    try
                    {
                        _host.SetLinkedAtvState(null, null);
                    }
                    finally
                    {
                        _host.EndSuppressParamWritesFromRefresh();
                    }
                });

                _host.NotifySectionLoaded(equipName, ParamSettingsPage.Atv, ParamLoadState.Unavailable);
                return;
            }

            var (linkedEquipName, linkedModel) = await TryResolveMotorLinkedAtvAsync(equipName, ct);

            ct.ThrowIfCancellationRequested();

            await _host.Dispatcher.InvokeAsync(() =>
            {
                _host.BeginSuppressParamWritesFromRefresh();
                try
                {
                    _host.SetLinkedAtvState(linkedEquipName, linkedModel);
                }
                finally
                {
                    _host.EndSuppressParamWritesFromRefresh();
                }
            });

            _host.NotifySectionLoaded(
                equipName,
                ParamSettingsPage.Atv,
                linkedModel != null ? ParamLoadState.Ready : ParamLoadState.Unavailable);
        }

        /// <summary>
        /// Ищет linked ATV для мотора через:
        /// WinOpened / ASSOC="__EquipmentSic"
        /// 
        /// Возвращает:
        /// - equipment name
        /// - AtvModel
        /// </summary>
        private async Task<(string? equipName, AtvModel? model)> TryResolveMotorLinkedAtvAsync(string motorEquipName, CancellationToken ct)
        {
            var winRef = await _equipmentService.GetWinOpenedRefAsync(
                sEquipName: motorEquipName,
                sEquipItem: "State",
                sCategory: "WinOpened",
                assocExpected: "__EquipmentSic");

            if (winRef == null || string.IsNullOrWhiteSpace(winRef.RefEquip))
                return (null, null);

            var equip = FindLoadedEquipment(winRef.RefEquip);
            if (equip == null)
                return (null, null);

            var group = EquipTypeRegistry.GetGroup(equip.Type ?? "");
            if (group != EquipTypeGroup.Atv)
                return (null, null);

            var linkedEquipName = (equip.Equipment ?? winRef.RefEquip).Trim();
            if (string.IsNullOrWhiteSpace(linkedEquipName))
                return (null, null);

            var param = await _equipmentService.ReadEquipParamsAsync<AtvParam>(linkedEquipName, ct);
            if (param == null)
                return (null, null);

            return (linkedEquipName, new AtvModel(param));
        }

    }
}