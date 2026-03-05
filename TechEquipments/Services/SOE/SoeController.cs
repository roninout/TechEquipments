using DevExpress.Xpf.Core;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using static TechEquipments.IEquipmentService;

namespace TechEquipments
{
    /// <summary>
    /// Вынесенная логика загрузки SOE:
    /// - отмена предыдущей загрузки
    /// - gate от параллельных загрузок
    /// - overlay/progress обновление
    /// - запись результата в EquipmentSoeRows
    /// </summary>
    public sealed class SoeController
    {
        private readonly IEquipmentService _equipmentService;
        private readonly ISoeHost _host;

        private readonly SemaphoreSlim _loadGate = new(1, 1);
        private CancellationTokenSource? _loadCts;

        public SoeController(IEquipmentService equipmentService, ISoeHost host)
        {
            _equipmentService = equipmentService ?? throw new ArgumentNullException(nameof(equipmentService));
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        /// <summary>
        /// Загрузка SOE по выбранному оборудованию.
        /// </summary>
        public async Task LoadAndShowAsync(string equipName)
        {
            var name = (equipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name))
                return;

            // отменяем предыдущую загрузку
            try { _loadCts?.Cancel(); } catch { }

            await _loadGate.WaitAsync();

            CancellationTokenSource? myCts = null;

            try
            {
                _loadCts?.Dispose();
                myCts = new CancellationTokenSource();
                _loadCts = myCts;
                var ct = myCts.Token;

                // включаем overlay и сбрасываем прогресс на UI
                await _host.Dispatcher.InvokeAsync(() =>
                {
                    _host.IsLoading = true;

                    _host.LoadedCount = 0;
                    _host.CurrentCount = 0;
                    _host.CurrentTrendIndex = 0;
                    _host.CurrentTrendName = "";
                    _host.TotalTrends = 0;
                }, DispatcherPriority.Render);

                // прогресс можно обновлять прямо через Dispatcher, чтобы не зависеть от контекста
                var progress = new Progress<LoadingProgress>(p =>
                {
                    _host.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _host.TotalTrends = p.TotalTrends;
                        _host.CurrentTrendIndex = p.CurrentTrendIndex;
                        _host.CurrentTrendName = p.CurrentTrendName;
                        _host.CurrentCount = p.CurrentTrendCount;
                        _host.LoadedCount = p.TotalLoaded;
                    }), DispatcherPriority.Background);
                });

                var rows = await _equipmentService.GetDataFromEquipAsync(
                    name,
                    progress,
                    ct,
                    perTrendMax: _host.PerTrendMax,
                    totalMax: _host.TotalMax);

                ct.ThrowIfCancellationRequested();

                await _host.Dispatcher.InvokeAsync(() =>
                {
                    _host.EquipmentSoeRows.Clear();
                    foreach (var r in rows)
                        _host.EquipmentSoeRows.Add(r);
                }, DispatcherPriority.Background);
            }
            catch (OperationCanceledException)
            {
                await _host.Dispatcher.InvokeAsync(() => _host.CurrentTrendName = "Cancelled");
            }
            catch (Exception ex)
            {
                DXMessageBox.Show(_host.OwnerWindow, ex.ToString(), "Error",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
            finally
            {
                // выключаем overlay только если это "наша" актуальная загрузка
                if (ReferenceEquals(_loadCts, myCts))
                {
                    await _host.Dispatcher.InvokeAsync(() => _host.IsLoading = false, DispatcherPriority.Render);

                    _loadCts?.Dispose();
                    _loadCts = null;
                }

                _loadGate.Release();
            }
        }

        /// <summary>Отмена текущей загрузки (кнопка Cancel).</summary>
        public void Cancel()
        {
            try { _loadCts?.Cancel(); } catch { }
        }
    }
}