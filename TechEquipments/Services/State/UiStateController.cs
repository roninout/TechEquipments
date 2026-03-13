using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace TechEquipments
{
    /// <summary>
    /// Контролер сохранения/восстановления состояния UI:
    /// - Restore из user-state.json
    /// - Startup из ExternalTag (с приоритетом)
    /// - Debounce Save
    /// - Флаги StartupExternalTag для применения фильтров после загрузки Equipments
    /// </summary>
    public sealed class UiStateController
    {
        private readonly IUserStateService _stateService;
        private readonly IEquipmentService _equipmentService;
        private readonly IUiStateHost _host;

        private readonly DispatcherTimer _saveTimer;

        private bool _isRestoringState;

        // Startup info (нужно MainWindow после загрузки Equipments)
        private bool _startupUsedExternalTag;
        private string _startupExternalTag = "";

        public UiStateController(IUserStateService stateService, IEquipmentService equipmentService, IUiStateHost host)
        {
            _stateService = stateService ?? throw new ArgumentNullException(nameof(stateService));
            _equipmentService = equipmentService ?? throw new ArgumentNullException(nameof(equipmentService));
            _host = host ?? throw new ArgumentNullException(nameof(host));

            _saveTimer = new DispatcherTimer(DispatcherPriority.Background, _host.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(400)
            };

            _saveTimer.Tick += async (_, __) =>
            {
                _saveTimer.Stop();
                try
                {
                    await SaveAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(ex);
                    // опционально: _host.BottomText = $"State save error: {ex.Message}";
                }                
            };
        }

        public bool IsRestoringState => _isRestoringState;

        public bool StartupUsedExternalTag => _startupUsedExternalTag;
        public string StartupExternalTag => _startupExternalTag;

        /// <summary>
        /// Планируем сохранение (debounce).
        /// Вызывай из setter’ов MainWindow вместо ScheduleStateSave().
        /// </summary>
        public void ScheduleSave()
        {
            if (_isRestoringState)
                return;

            _saveTimer.Stop();
            _saveTimer.Start();
        }

        /// <summary>
        /// Восстановление из ExternalTag (приоритет над user-state.json).
        /// </summary>
        public async Task<bool> TryApplyStartupStateFromExternalTagAsync()
        {
            try
            {
                var ext = await _equipmentService.GetExternalTagAsync(CancellationToken.None);

                if (string.IsNullOrWhiteSpace(ext) ||
                    ext.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    _startupUsedExternalTag = false;
                    _startupExternalTag = "";
                    return false;
                }

                _startupUsedExternalTag = true;
                _startupExternalTag = ext.Trim();

                // Очищаем ExternalTag (best-effort)
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _equipmentService.SetExternalTagAsync("", cts.Token);
                }
                catch
                {
                    // ignore
                }

                _isRestoringState = true;
                try
                {
                    // применяем стартовое состояние на UI потоке
                    await _host.Dispatcher.InvokeAsync(() =>
                    {
                        _host.EquipName = _startupExternalTag;
                        _host.SelectedMainTabIndex = (int)MainTabKind.Param;
                        _host.DbDate = DateTime.Today;

                        _host.SelectedStation = "All";
                        _host.SelectedTypeFilter = EquipTypeGroup.All;
                    }, DispatcherPriority.Background);
                }
                finally
                {
                    _isRestoringState = false;
                }

                return true;
            }
            catch
            {
                _startupUsedExternalTag = false;
                _startupExternalTag = "";
                return false;
            }
        }

        /// <summary>
        /// Восстановление состояния из user-state.json.
        /// </summary>
        public async Task RestoreStateAsync()
        {
            _isRestoringState = true;
            try
            {
                var state = await _stateService.LoadAsync();
                if (state == null)
                    return;

                await _host.Dispatcher.InvokeAsync(() =>
                {
                    // Сначала восстанавливаем карту "Station+Type -> EquipName".
                    // Проверка на реальное наличие оборудования произойдёт позже,
                    // когда список Equipments уже будет загружен.
                    _host.ImportRememberedEquipmentsByFilter(state.LastEquipmentsByFilter);

                    _host.EquipName = state.LastEquipName ?? "";
                    _host.DbDate = state.DbDate.Date;

                    _host.SelectedStation = state.SelectedStation ?? "All";
                    _host.SelectedTypeFilter = state.SelectedTypeFilter;

                    _host.SelectedMainTabIndex = (int)state.SelectedTab;
                }, DispatcherPriority.Background);
            }
            finally
            {
                _isRestoringState = false;
            }
        }

        /// <summary>
        /// Сохранение состояния сразу (обычно вызывается таймером).
        /// </summary>
        public async Task SaveAsync()
        {
            if (_isRestoringState)
                return;

            var state = new UserState
            {
                LastEquipName = (_host.EquipName ?? "").Trim(),
                DbDate = _host.DbDate.Date,
                SelectedTab = (MainTabKind)_host.SelectedMainTabIndex,
                SelectedStation = (_host.SelectedStation ?? "All").Trim(),
                SelectedTypeFilter = _host.SelectedTypeFilter,

                // Хост вернёт уже очищенную карту:
                // без пустых ключей и без оборудования, которого больше нет в проекте.
                LastEquipmentsByFilter = _host.ExportRememberedEquipmentsByFilter()
            };

            await _stateService.SaveAsync(state);
        }

        ///// <summary>
        ///// Восстановление состояния из user-state.json.
        ///// </summary>
        //public async Task RestoreStateAsync()
        //{
        //    _isRestoringState = true;
        //    try
        //    {
        //        var state = await _stateService.LoadAsync();
        //        if (state == null)
        //            return;

        //        await _host.Dispatcher.InvokeAsync(() =>
        //        {
        //            _host.EquipName = state.LastEquipName ?? "";
        //            _host.DbDate = state.DbDate.Date;

        //            _host.SelectedStation = state.SelectedStation ?? "All";
        //            _host.SelectedTypeFilter = state.SelectedTypeFilter;

        //            _host.SelectedMainTabIndex = (int)state.SelectedTab;
        //        }, DispatcherPriority.Background);
        //    }
        //    finally
        //    {
        //        _isRestoringState = false;
        //    }
        //}

        ///// <summary>
        ///// Сохранение состояния сразу (обычно вызывается таймером).
        ///// </summary>
        //public async Task SaveAsync()
        //{
        //    if (_isRestoringState)
        //        return;

        //    var state = new UserState
        //    {
        //        LastEquipName = (_host.EquipName ?? "").Trim(),
        //        DbDate = _host.DbDate.Date,
        //        SelectedTab = (MainTabKind)_host.SelectedMainTabIndex,
        //        SelectedStation = (_host.SelectedStation ?? "All").Trim(),
        //        SelectedTypeFilter = _host.SelectedTypeFilter
        //    };

        //    await _stateService.SaveAsync(state);
        //}
    }
}