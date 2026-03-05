using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace TechEquipments
{
    /// <summary>
    /// Вся логіка Param-polling винесена з MainWindow:
    /// - цикл раз в 5 секунд
    /// - PollParamOnceSafeAsync (gate, pause-after-write, editing protection)
    /// - PollParamOnceAsync (определение типа и чтение модели)
    /// - ApplyParamModelToUi (кеш props + обновление значений без лагов)
    /// </summary>
    public sealed class ParamController
    {
        private readonly IEquipmentService _equipmentService;
        private readonly IParamHost _host;

        // защита от гонок Start/Stop (SelectionChanged + TabChanged могут дернуть одновременно)
        private readonly object _sync = new();

        // ключ "что именно сейчас поллим" (equip + type)
        // если ключ поменялся -> перезапускаем polling (как раньше)
        private string _pollKey = "";

        private CancellationTokenSource? _cts;

        // --- UI apply cache ---
        private Type? _currentParamModelType;
        private readonly Dictionary<Type, PropertyInfo[]> _uiPropsCache = new();
        private readonly Dictionary<string, int> _rowIndexByName = new(StringComparer.Ordinal);

        public ParamController(IEquipmentService equipmentService, IParamHost host)
        {
            _equipmentService = equipmentService;
            _host = host;
        }

        public void Start()
        {
            // Polling имеет смысл только на вкладке Param
            if (_host.SelectedMainTab != MainTabKind.Param)
                return;

            var newKey = BuildPollKey();

            lock (_sync)
            {
                // 1) если polling еще не запущен — запускаем как обычно
                if (_cts == null)
                {
                    StartInternal_NoLock(newKey);
                    return;
                }

                // 2) если polling запущен и ключ тот же — ничего не делаем
                if (string.Equals(_pollKey, newKey, StringComparison.OrdinalIgnoreCase))
                    return;

                // 3) ключ поменялся => выбрали другое оборудование: restart + сброс статуса/счетчика
                StopInternal_NoLock();

                StartInternal_NoLock(newKey);
            }
        }

        /// <summary>
        /// Старт polling (предполагается, что lock уже взят).
        /// </summary>
        private void StartInternal_NoLock(string pollKey)
        {
            _pollKey = pollKey;

            // ✅ Сброс как раньше
            _host.ParamReadCycles = 0;
            _host.ParamStatusText = "Param: starting...";

            // опционально: можно сбросить только UI-кеш типа модели,
            // чтобы при смене оборудования того же типа все равно не было "старого состояния"
            _currentParamModelType = null;
            _rowIndexByName.Clear();

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        // polling только на Param вкладке
                        if (_host.SelectedMainTab != MainTabKind.Param)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(1), ct);
                            continue;
                        }

                        await PollParamOnceSafeAsync(ct);

                        // секции
                        await _host.RefreshActiveParamSectionAsync(ct);

                        // тренды
                        if (_host.TrendIsChartVisible)
                            await _host.PollTrendOnceSafeAsync(ct);

                        await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(ex);
                }
            }, ct);
        }

        public void Stop()
        {
            lock (_sync)
            {
                StopInternal_NoLock();
                _pollKey = "";
            }
        }

        /// <summary>
        /// Стоп polling (предполагается, что lock уже взят).
        /// </summary>
        private void StopInternal_NoLock()
        {
            try { _cts?.Cancel(); } catch { }
            _cts?.Dispose();
            _cts = null;
        }

        private async Task PollParamOnceSafeAsync(CancellationToken ct)
        {
            try
            {
                // Если недавно писали — подождем чуть-чуть
                if (DateTime.UtcNow < _host.ParamReadResumeAtUtc)
                    return;

                // если пользователь сейчас вводит значение — НЕ читаем, чтобы не затирать ввод
                if (_host.IsEditingField)
                    return;

                await _host.ParamRwGate.WaitAsync(ct);
                try
                {
                    await PollParamOnceAsync(ct);
                }
                finally
                {
                    _host.ParamRwGate.Release();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _host.BottomText = $"Param read error: {ex.Message}";
            }
        }

        private async Task PollParamOnceAsync(CancellationToken ct)
        {
            var (equipName, equipType) = _host.ResolveSelectedEquipForParam();

            equipName = (equipName ?? "").Trim();
            equipType = (equipType ?? "").Trim();

            if (string.IsNullOrWhiteSpace(equipName))
            {
                await _host.Dispatcher.InvokeAsync(() =>
                {
                    _host.ParamStatusText = "Param: select equipment";
                    _host.ParamItems.Clear();
                    _currentParamModelType = null;
                    _host.CurrentParamModel = null!;
                });
                return;
            }

            var typeGroup = EquipTypeRegistry.GetGroup(equipType);

            object? model = typeGroup switch
            {
                EquipTypeGroup.AI => await _equipmentService.ReadEquipParamsAsync<AIParam>(equipName, ct),
                EquipTypeGroup.DI => await _equipmentService.ReadEquipParamsAsync<DIParam>(equipName, ct),
                EquipTypeGroup.DO => await _equipmentService.ReadEquipParamsAsync<DOParam>(equipName, ct),
                EquipTypeGroup.Atv => await _equipmentService.ReadEquipParamsAsync<AtvParam>(equipName, ct),
                EquipTypeGroup.Motor => await _equipmentService.ReadEquipParamsAsync<MotorParam>(equipName, ct),
                EquipTypeGroup.VGA_EL => await _equipmentService.ReadEquipParamsAsync<VGA_ElParam>(equipName, ct),
                EquipTypeGroup.VGA => await _equipmentService.ReadEquipParamsAsync<VGAParam>(equipName, ct),
                EquipTypeGroup.VGD => await _equipmentService.ReadEquipParamsAsync<VGDParam>(equipName, ct),
                _ => null
            };

            ct.ThrowIfCancellationRequested();

            await _host.Dispatcher.InvokeAsync(() =>
            {
                // Сброс области при смене типа группы
                _host.Param_ResetAreaIfTypeGroupChanged(typeGroup);

                _host.SuppressParamWritesFromPolling = true;

                try
                {
                    if (model == null)
                    {
                        _host.ParamStatusText = "Updating ...";
                        _host.ParamItems.Clear();
                        _currentParamModelType = null;
                        _host.CurrentParamModel = null!;
                        return;
                    }

                    ApplyParamModelToUi(model);

                    _host.ParamReadCycles++;
                    _host.ParamStatusText = $"Last update: {DateTime.Now:HH:mm:ss} | {_host.ParamReadCycles} cycles";
                }
                finally
                {
                    // Снимаем подавление ПОСЛЕ того, как UI применит биндинги/создаст визуальные элементы
                    _host.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _host.SuppressParamWritesFromPolling = false;
                    }), DispatcherPriority.ContextIdle);
                }
            });
        }

        private void ApplyParamModelToUi(object model)
        {
            _host.CurrentParamModel = model;

            var modelType = model.GetType();

            // --- props cache ---
            if (!_uiPropsCache.TryGetValue(modelType, out var props))
            {
                props = modelType
                    .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                    // служебные поля не показываем строками Param:
                    .Where(p =>
                        !p.Name.Equals("Unit", StringComparison.OrdinalIgnoreCase) &&
                        !p.Name.Equals("Chanel", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => p.MetadataToken)
                    .ToArray();

                _uiPropsCache[modelType] = props;
            }

            // Если модель поменялась (например AI -> DI), пересоздаём строки
            if (_currentParamModelType != modelType)
            {
                _host.ParamItems.Clear();
                _rowIndexByName.Clear();

                for (int i = 0; i < props.Length; i++)
                {
                    var p = props[i];

                    _host.ParamItems.Add(new ParamItem
                    {
                        Name = p.Name,
                        Value = p.GetValue(model)
                    });

                    _rowIndexByName[p.Name] = i;
                }

                _currentParamModelType = modelType;
                return;
            }

            // Та же модель — обновляем только Value без построения словаря каждый цикл
            for (int i = 0; i < props.Length; i++)
            {
                var p = props[i];

                if (_rowIndexByName.TryGetValue(p.Name, out var rowIndex) &&
                    rowIndex >= 0 && rowIndex < _host.ParamItems.Count)
                {
                    _host.ParamItems[rowIndex].Value = p.GetValue(model);
                }
            }
        }

        /// <summary>
        /// Формируем ключ polling (что именно сейчас поллим).
        /// Если изменится — считаем что выбрали другое оборудование.
        /// </summary>
        private string BuildPollKey()
        {
            var (equipName, equipType) = _host.ResolveSelectedEquipForParam();

            equipName = (equipName ?? "").Trim();
            equipType = (equipType ?? "").Trim();

            return $"{equipName}|{equipType}";
        }
    }
}