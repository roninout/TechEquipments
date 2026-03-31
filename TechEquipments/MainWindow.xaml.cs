using CtApi;
using DevExpress.Xpf.Bars;
using DevExpress.Xpf.Charts;
using DevExpress.Xpf.Core;
using DevExpress.XtraPrinting.Native;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using TechEquipments.ViewModels;
using TechEquipments.Views.Settings;

namespace TechEquipments
{
    /// <summary>
    /// Главное окно приложения:
    /// - Левая панель: список оборудования + фильтры Station/Type + посимвольный поиск.
    /// - Правая панель: вкладки SOE / Operation actions / Alarm history.
    /// - Нижняя панель прогресса: используется для загрузки списка оборудования и DB (индетерминантно).
    /// - Overlay: используется для загрузки SOE (тренды).
    /// </summary>
    public partial class MainWindow : ThemedWindow, INotifyPropertyChanged
    {
        private readonly IEquipmentService _equipmentService;
        private readonly ICtApiService _ctApiService;
        private readonly IConfiguration _config;
        private readonly IEquipInfoService _equipInfoService;

        private ParamController _paramController;
        private ParamWriteController _paramWriteController;
        private readonly ParamRefsController _paramRefs;
        private readonly DbController _dbController;
        private readonly QrController _qrController;
        private readonly SoeController _soeController;
        private readonly UiStateController _uiState;
        private readonly InfoController _infoController;
        private readonly EquipmentListController _equipmentListController;

        public MainViewModel Vm { get; }
        private EquipmentListViewModel EquipVm => Vm.EquipmentList;

        /// <summary>Строки SOE (вкладка SOE).</summary>
        public ObservableCollection<EquipmentSOEDto> equipmentSOEDtos { get; } = new();


        #region Fields

        #region Left pane: search + filters + selection

        public ICollectionView EquipmentsView => _equipmentListController.EquipmentsView;

        public Array TypeFilters { get; } = Enum.GetValues(typeof(EquipTypeGroup));

        #endregion

        #region Tab / date bridge

        /// <summary>
        /// CTS для отмены загрузки списка оборудования.
        /// </summary>
        private CancellationTokenSource? _equipListCts;

        public int SelectedMainTabIndex
        {
            get => Vm.SelectedMainTabIndex;
            set
            {
                if (Vm.SelectedMainTabIndex == value) return;

                Vm.SelectedMainTabIndex = value;
                OnPropertyChanged();

                // selected tab как read-only helper ещё используется в code-behind
                OnPropertyChanged(nameof(SelectedMainTab));

                // ВАЖНО: во время восстановления состояния никаких автодействий
                if (_uiState.IsRestoringState) return;

                _dbController.CancelCurrentLoad();
                _uiState.ScheduleSave();

                _ = OnTabActivatedLikeSearchAsync(force: true);
            }
        }

        public DateTime DbDate
        {
            get => Vm.Database.DbDate;
            set
            {
                if (Vm.Database.DbDate.Date == value.Date) return;
                Vm.Database.DbDate = value.Date;
                OnPropertyChanged();

                _dbController.ScheduleReload();
                _uiState.ScheduleSave();
            }
        }

        /// <summary>
        /// Оставляем как helper для code-behind.
        /// </summary>
        public MainTabKind SelectedMainTab => Vm.SelectedMainTab;

        #endregion   

        #region Left pane toggle state

        private bool _layoutReady;
        private GridLength _leftSavedWidth = new(260);

        #endregion

        #region Params

        // Что именно сейчас ждём
        private string? _pendingParamOverlayEquipName;
        private ParamSettingsPage _pendingParamOverlayPage = ParamSettingsPage.None;
        private bool _pendingParamOverlayNeedsMainModel;

        // Последний завершённый статус основной Param-модели
        private string? _lastMainLoadedEquipName;
        private ParamLoadState _lastMainLoadedState = ParamLoadState.Waiting;

        // Последний завершённый статус секции settings
        private string? _lastSectionLoadedEquipName;
        private ParamSettingsPage _lastSectionLoadedPage = ParamSettingsPage.None;
        private ParamLoadState _lastSectionLoadedState = ParamLoadState.Waiting;
        
        private readonly SemaphoreSlim _paramRwGate = new(1, 1); // Общий “замок” на чтение/запись Param (чтение и запись не пересекаются)

        // ===== Param editing (anti-overwrite during typing) =====

        private int _isEditingField; // 0/1 флаг (Interlocked/Volatile, чтобы безопасно читать из background polling)

        // Быстрая проверка из polling
        private bool IsEditingField => Volatile.Read(ref _isEditingField) == 1;

        private static bool IsFinalParamLoadState(ParamLoadState state) => state is ParamLoadState.Ready or ParamLoadState.Unavailable or ParamLoadState.Error;

        #endregion

        #region Trend

        public ParamTrendVm Trend { get; }

        private ParamTrendController _trendCtl;

        #endregion

        #region Security

        private bool _isLogoLoginToggleInProgress;

        #endregion

        #region Version
        public string AppVersionText
        {
            get
            {
                var asm = Assembly.GetExecutingAssembly();

                var info =
                    asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                       .InformationalVersion;

                if (!string.IsNullOrWhiteSpace(info))
                {
                    var clean = info.Split('+')[0].Trim();
                    return $"v{clean}";
                }

                var ver = asm.GetName().Version;
                return ver == null ? "" : $"v{ver.Major}.{ver.Minor}.{ver.Build}";
            }
        }
        #endregion

        #region Station
        private CancellationTokenSource? _stationHealthCts;
        private Task? _stationHealthTask;
        #endregion

        #endregion

        public MainWindow(IEquipmentService equipmentService, IDbService dbService, IEquipInfoService equipInfoService, IUserStateService stateService, ICtApiService ctApiService, IConfiguration config, IQrCodeService qrCodeService, IQrScannerService qrScannerService, MainViewModel vm)
        {
            InitializeComponent();

            Vm = vm;

            _equipmentListController = new EquipmentListController(Vm, Dispatcher, () => EquipmentsListBox);

            _equipmentService = equipmentService;
            _ctApiService = ctApiService;
            _equipInfoService = equipInfoService;

            _ctApiService.ConnectionStateChanged += OnCtApiConnectionStateChanged;
            Closed += (_, __) => {
                _ctApiService.ConnectionStateChanged -= OnCtApiConnectionStateChanged;
                StopStationHealthMonitor();
            };

            Vm.Shell.IsCtApiConnected = _ctApiService.IsConnectionAvailable;

            _config = config;
            _dbController = new DbController(dbService, Vm, Dispatcher);
            _infoController = new InfoController(equipInfoService, Vm.Info, Vm.EquipmentList, Vm.Database, this, qrScannerService);

            _qrController = new QrController(
                _equipmentService,
                qrCodeService,
                qrScannerService,
                Vm,
                this,
                text => EquipVm.EquipName = text,
                station => EquipVm.SelectedStation = station,
                type => EquipVm.SelectedTypeFilter = type,
                tabIndex => SelectedMainTabIndex = tabIndex,
                _equipmentListController.DoIncrementalSearch,
                StartParamPolling,
                NotifyParamQrUiChanged);

            _soeController = new SoeController(
                _equipmentService,
                Vm.Shell,
                equipmentSOEDtos,
                Dispatcher,
                () => this);
            
            _uiState = new UiStateController(
                stateService,
                _equipmentService,
                Vm,
                Dispatcher,
                equipName => EquipVm.EquipName = equipName,
                dbDate => DbDate = dbDate,
                station => EquipVm.SelectedStation = station,
                type => EquipVm.SelectedTypeFilter = type,
                tabIndex => SelectedMainTabIndex = tabIndex,
                _equipmentListController.ExportRememberedEquipmentsByFilter,
                _equipmentListController.ImportRememberedEquipmentsByFilter);

            SubscribeEquipmentListBridge();

            // Если настройки нет — сохраняем текущее поведение (overlay включен)
            Vm.Shell.UseParamAreaOverlay = _config.GetValue("Global:Overlay", true);

            // Vm + Controller
            Trend = new ParamTrendVm();
            Trend.AutoLive = _config.GetValue("Trend:AutoLive", true);

            _trendCtl = new ParamTrendController(
                Trend,
                Dispatcher,
                _equipmentService,
                _ctApiService,
                resolveEquip: ResolveSelectedEquipForParam,        // твой существующий метод
                getParamModel: () => Vm.Param.CurrentParamModel,            // твоя текущая модель параметров
                getParamCycles: () => Vm.Param.ParamReadCycles             // счетчик циклов
            );

            _paramController = new ParamController(
                _equipmentService,
                Vm,
                Dispatcher,
                _ctApiService,
                _paramRwGate,
                getTrendIsChartVisible: () => Trend.IsChartVisible,
                getIsEditingField: () => IsEditingField,
                resolveSelectedEquipForParam: ResolveSelectedEquipForParam,
                resetAreaIfTypeGroupChanged: newGroup => _paramRefs.ResetAreaIfTypeGroupChanged(newGroup),
                refreshActiveParamSectionAsync: ct => _paramRefs.RefreshActiveParamSectionAsync(ct),
                pollTrendOnceSafeAsync: ct => _trendCtl.PollOnceSafeAsync(ct, txt => Vm.Shell.BottomText = txt),
                notifyMainParamLoaded: (equipName, state) => NotifyMainParamLoadedCore(equipName, state));

            _paramWriteController = new ParamWriteController(
                equipmentService: _equipmentService,
                ctApiService: _ctApiService,
                requiredWritePrivilege: _config.GetValue("CtApiSecurity:WritePrivilege", 1),
                requiredWriteArea: _config.GetValue("CtApiSecurity:WriteArea", 0),
                requiredUserNameContains: _config["CtApiSecurity:RequiredUserNameContains"] ?? "Tab",
                getSelectedTab: () => SelectedMainTab,
                resolveSelectedEquip: ResolveSelectedEquipForParam,
                resolveEquipNameForWrite: ResolveEquipNameForWrite,
                getSuppressWritesFromPolling: () => Vm.Param.SuppressParamWritesFromPolling,
                getSuppressWritesFromUiRollback: () => Vm.Param.SuppressParamWritesFromUiRollback,
                setSuppressWritesFromUiRollback: v => Vm.Param.SuppressParamWritesFromUiRollback = v,
                paramRwGate: _paramRwGate,
                setParamReadResumeAtUtc: dt => Vm.Param.ParamReadResumeAtUtc = dt,
                setBottomText: txt => Vm.Shell.ParamStatusText = txt,
                getOwnerWindow: () => this,
                endParamFieldEdit: EndParamFieldEdit
            );

            _paramRefs = new ParamRefsController(
                _equipmentService,
                _ctApiService,
                _config,
                Vm,
                Dispatcher,
                _paramRwGate,
                ResolveSelectedEquipForParam,
                _equipmentListController.FilterEquipment,
                _equipmentListController.ApplyFilters,
                _equipmentListController.DoIncrementalSearch,
                ShowParamChart,
                StartParamPolling,
                tabIndex => SelectedMainTabIndex = tabIndex,
                equipName => EquipVm.EquipName = equipName,
                station => EquipVm.SelectedStation = station,
                type => EquipVm.SelectedTypeFilter = type,
                NotifySectionLoadedCore,
                () => Vm.Param.SuppressParamWritesFromPolling = true,
                () => Dispatcher.BeginInvoke(new Action(() =>
                {
                    Vm.Param.SuppressParamWritesFromPolling = false;
                }), DispatcherPriority.ContextIdle));

            DataContext = this; // DataContext на себя: используется во всём XAML (binding)

            _equipmentListController.InitEquipmentsView();
            _equipmentListController.InitSearchTimer();

            OnPropertyChanged(nameof(EquipmentsView));

            Loaded += async (_, __) =>
            {
                _layoutReady = true;
                InitLeftPaneState();

                // CtApi для этого приложения считаем обязательным условием старта.
                // Если связи нет — не запускаем UI в полуживом состоянии.
                //if (!await EnsureCtApiAvailableAtStartupAsync())
                //    return;

                // ExternalTag имеет приоритет над user-state.json.
                var usedExt = await _uiState.TryApplyStartupStateFromExternalTagAsync();
                if (!usedExt)
                    await _uiState.RestoreStateAsync();

                // Загружаем equipment list, но без лишних startup side-effects.
                await LoadEquipmentsListAsync();

                // DB — мягкая деградация: если недоступна, просто отключаем DB/Info сценарии.
                await _dbController.CheckDbAsync();
                await EnsureInfoStorageReadyAsync();

                // Один финальный startup-activation по фактически выбранной вкладке.
                await OnTabActivatedLikeSearchAsync(force: true);
            };

        }

        #region Startup loading

        /// <summary>
        /// Загружает список оборудования для левой панели + станции.
        /// Показывает внизу детерминантный прогресс.
        /// </summary>
        private async Task LoadEquipmentsListAsync()
        {
            _equipListCts?.Cancel();
            _equipListCts?.Dispose();
            _equipListCts = new CancellationTokenSource();
            var ct = _equipListCts.Token;

            try
            {
                Vm.EquipmentList.IsEquipListLoading = true;

                Vm.EquipmentList.EquipListDone = 0;
                Vm.EquipmentList.EquipListTotal = 0;

                Vm.Shell.BottomText = "Loading equipments...";

                await Dispatcher.Yield(DispatcherPriority.Background);

                var progress = new Progress<(int done, int total)>(p =>
                {
                    Vm.EquipmentList.EquipListDone = p.done;
                    Vm.EquipmentList.EquipListTotal = p.total;
                    Vm.Shell.BottomText = $"Loading equipments: {p.done}/{p.total}";
                });

                var items = await _equipmentService.GetAllEquipmentsAsync(progress, ct);

                _equipmentListController.ReplaceLoadedEquipments(items);
                RestartStationHealthMonitor();

                // Если на старте использовали ExternalTag —
                // сначала выставляем Station/TypeGroup,
                // а уже потом один раз нормализуем selection.
                if (_uiState.StartupUsedExternalTag &&
                    !string.IsNullOrWhiteSpace(_uiState.StartupExternalTag))
                {
                    _qrController.TryApplyStationTypeFiltersFromQr(_uiState.StartupExternalTag);
                }

                // ВАЖНО:
                // на этапе startup-load только нормализуем selection,
                // но не запускаем Param polling / Info load.
                // Это будет сделано один раз позже в OnTabActivatedLikeSearchAsync(force: true).
                RestoreOrSelectEquipmentAfterFilterChanged(suppressAutoActivation: true);

                Vm.Shell.BottomText = $"Equipments: {_equipmentListController.EquipmentsCount}";
            }
            catch (OperationCanceledException)
            {
                Vm.Shell.BottomText = "Equipments loading cancelled";
            }
            catch (Exception ex)
            {
                Vm.Shell.BottomText = $"Equip list error: {ex.Message}";
                DXMessageBox.Show(this, ex.ToString(), "Equip list error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Vm.EquipmentList.IsEquipListLoading = false;

                // Даём UI шанс корректно перерисовать нижнюю панель.
                await Dispatcher.Yield(DispatcherPriority.Render);
            }
        }

        /// <summary>
        /// Реакция UI на потерю/восстановление связи CtApi.
        /// </summary>
        private void OnCtApiConnectionStateChanged(bool isConnected, string? message)
        {
            void Apply()
            {
                Vm.Shell.IsCtApiConnected = isConnected;

                if (!isConnected)
                {
                    Vm.Shell.CtApiStatusText = string.IsNullOrWhiteSpace(message)
                        ? "CtApi connection lost."
                        : message;
                    MarkAllStationsOffline(true);
                    return;
                }

                Vm.Shell.CtApiStatusText = "";

                // Сообщение о восстановлении связи показываем в обычном BottomText.
                Vm.Shell.BottomText = string.IsNullOrWhiteSpace(message)
                    ? $"CtApi connection restored at {DateTime.Now:HH:mm:ss}"
                    : message;
                RestartStationHealthMonitor();
            }

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke((Action)Apply);
                return;
            }

            Apply();
        }

        /// <summary>
        /// Ранняя проверка связи с CtApi на старте окна.
        /// Если связи нет, показываем сообщение и закрываем приложение.
        /// </summary>
        private async Task<bool> EnsureCtApiAvailableAtStartupAsync()
        {
            try
            {
                Vm.Shell.BottomText = "Checking CtApi connection...";
                await Dispatcher.Yield(DispatcherPriority.Background);

                var isConnected = await _ctApiService.IsConnected();
                if (isConnected)
                    return true;

                DXMessageBox.Show(
                    this,
                    "There is no connection to the server.\nThe application will be closed.",
                    "Connection error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                Application.Current.Shutdown();
                return false;
            }
            catch (Exception ex)
            {
                DXMessageBox.Show(
                    this,
                    $"Failed to connect to the CtApi server.\n\n{ex.Message}\n\nThe application will be closed.",
                    "Connection error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                Application.Current.Shutdown();
                return false;
            }
        }

        private async Task EnsureInfoStorageReadyAsync()
        {
            if (!Vm.Database.IsDbConnected)
                return;

            try
            {
                await _equipInfoService.EnsureTableAsync();
            }
            catch (Exception ex)
            {
                // Для текущего мини-пакета делаем мягкую деградацию:
                // если не смогли подготовить DB storage для Info,
                // считаем DB-функционал недоступным и отключаем соответствующие вкладки.
                Vm.Database.IsDbConnected = false;
                Vm.Shell.BottomText = $"Info storage init error: {ex.Message}";
            }
        }

        #endregion

        #region Station

        private void RestartStationHealthMonitor()
        {
            StopStationHealthMonitor();

            // Если станций ещё нет — нечего мониторить.
            if (Vm.EquipmentList.Stations.Count <= 1) // только "All"
                return;

            _stationHealthCts = new CancellationTokenSource();
            _stationHealthTask = RunStationHealthMonitorAsync(_stationHealthCts.Token);
        }

        private void StopStationHealthMonitor()
        {
            try
            {
                _stationHealthCts?.Cancel();
            }
            catch
            {
                // ignore
            }

            _stationHealthCts?.Dispose();
            _stationHealthCts = null;
            _stationHealthTask = null;
        }

        private void MarkAllStationsOffline(bool isOffline)
        {
            foreach (var station in Vm.EquipmentList.Stations)
            {
                if (string.Equals(station.Name, "All", StringComparison.OrdinalIgnoreCase))
                    continue;

                station.IsOffline = isOffline;
            }
        }

        private async Task RunStationHealthMonitorAsync(CancellationToken ct)
        {
            var periodSeconds = Math.Max(5, _config.GetValue("StationHealth:PeriodSeconds", 15));
            var failThreshold = Math.Max(1, _config.GetValue("StationHealth:FailCount", 3));

            var failCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(periodSeconds));

            try
            {
                await ProbeStationsOnceAsync(failCounts, failThreshold, ct);

                while (await timer.WaitForNextTickAsync(ct))
                {
                    await ProbeStationsOnceAsync(failCounts, failThreshold, ct);
                }
            }
            catch (OperationCanceledException)
            {
                // normal shutdown
            }
        }

        private async Task ProbeStationsOnceAsync(Dictionary<string, int> failCounts, int failThreshold, CancellationToken ct)
        {
            // Snapshot коллекции берём через UI thread.
            var stations = await Dispatcher.InvokeAsync(() =>
                _equipmentListController.GetStationProbeItems());

            if (stations == null || stations.Count == 0)
                return;

            // Если глобально CtApi disconnected — сразу считаем станции offline.
            if (!_ctApiService.IsConnectionAvailable)
            {
                await Dispatcher.InvokeAsync(() => MarkAllStationsOffline(true));
                return;
            }

            foreach (var station in stations)
            {
                ct.ThrowIfCancellationRequested();

                var ok = await ProbeStationAsync(station);

                if (ok)
                {
                    failCounts[station.Name] = 0;

                    await Dispatcher.InvokeAsync(() =>
                    {
                        station.IsOffline = false;
                    });

                    continue;
                }

                failCounts.TryGetValue(station.Name, out var fails);
                fails++;
                failCounts[station.Name] = fails;

                if (fails >= failThreshold)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        station.IsOffline = true;
                    });
                }
            }
        }

        private async Task<bool> ProbeStationAsync(StationStatusItem station)
        {
            try
            {
                var probeTag = (station.ProbeTagName ?? "").Trim();

                // Нет probe tag — не считаем станцию offline.
                if (string.IsNullOrWhiteSpace(probeTag))
                    return true;

                var result = await _ctApiService.TagReadAsync(probeTag);
                return result != "Unknown";
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Left pane init, Restore

        /// <summary>Начальное состояние: левая панель скрыта.</summary>
        private void InitLeftPaneState()
        {
            LeftPaneToggle.IsChecked = false;
            ApplyLeftPane(false);
        }

        /// <summary>
        /// После смены фильтров:
        /// - core фильтрации/выбора делает EquipmentListController,
        /// - orchestration-эффекты остаются в MainWindow.
        /// </summary>
        private void RestoreOrSelectEquipmentAfterFilterChanged(bool suppressAutoActivation = false)
        {
            var result = _equipmentListController.ApplyFiltersAndRestoreSelectionCore();

            if (!result.HasVisibleItems)
            {
                StopParamOverlayWait();

                if (!suppressAutoActivation && !_uiState.IsRestoringState && SelectedMainTab == MainTabKind.Info)
                    _ = _infoController.LoadCurrentAsync();

                return;
            }

            if (_uiState.IsRestoringState || suppressAutoActivation)
                return;

            if (SelectedMainTab == MainTabKind.Param)
                StartParamPolling();
            else if (SelectedMainTab == MainTabKind.Info)
                _ = _infoController.LoadCurrentAsync();
        }

        #endregion

        #region Tab

        /// <summary>
        /// При активации вкладки делаем действие как по кнопке:
        /// SOE -> Load SOE,
        /// DB вкладки -> Search/Load DB.
        /// </summary>
        private async Task OnTabActivatedLikeSearchAsync(bool force)
        {
            // Уходим с Param:
            // останавливаем polling и сбрасываем ожидание overlay/progress.
            if ((MainTabKind)SelectedMainTabIndex != MainTabKind.Param)
            {
                StopParamPolling();
                StopParamOverlayWait();
            }

            switch ((MainTabKind)SelectedMainTabIndex)
            {
                case MainTabKind.Param:
                    StartParamPolling();
                    break;

                case MainTabKind.Info:
                    await _infoController.LoadCurrentAsync();
                    break;

                case MainTabKind.OperationActions:
                case MainTabKind.AlarmHistory:
                    await _dbController.LoadCurrentTabAsync(force);
                    break;

                case MainTabKind.SOE:
                    await LoadSoeFromUiAsync();
                    break;

                default:
                    break;
            }
        }

        #endregion

        #region ListBox

        /// <summary>
        /// Клик по списку: подставляет оборудование в поле поиска (если сейчас не печатаем).
        /// </summary>
        private void Equipments_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Защита от “программного” выделения при поиске/скролле
            if (_equipmentListController.SuppressEquipNameFromSelection)
                return;

            // Если фокус в поиске — значит печатаем, не трогаем выбор/вкладки
            if (SearchTextEdit?.IsKeyboardFocusWithin == true)
                return;

            // Во время восстановления состояния — не запускаем автодействия
            if (_uiState.IsRestoringState)
                return;

            // Подставляем выбранное оборудование в строку поиска (EquipName)
            if (EquipVm.SelectedListBoxEquipment?.Equipment is string eq && !string.IsNullOrWhiteSpace(eq))
            {
                EquipVm.EquipName = eq;
                _equipmentListController.RememberEquipmentForCurrentFilters(eq);

                // Param overlay нужен только если реально работаем с Param.
                if (SelectedMainTab == MainTabKind.Param)
                    BeginParamOverlayWait(eq, Vm.Param.CurrentParamSettingsPage, needMainModel: true);
                else
                    StopParamOverlayWait();
            }
            else
            {
                StopParamOverlayWait();
            }

            // Если сейчас открыта Info — остаёмся на Info и просто перегружаем карточку.
            if (SelectedMainTab == MainTabKind.Info)
            {
                _ = _infoController.LoadCurrentAsync();
                return;
            }

            // Старое поведение: клик по списку переводит на Param.
            if (SelectedMainTab != MainTabKind.Param)
            {
                SelectedMainTabIndex = (int)MainTabKind.Param;
                return;
            }

            // Если Param уже открыт — делаем "мгновенное обновление"
            StartParamPolling();
        }

        #endregion

        #region Buttons

        /// <summary>
        /// Кнопка Load: загружает SOE по EquipName или выделенному элементу.
        /// </summary>
        private async void Load_Click(object sender, RoutedEventArgs e)
        {
            var text = (EquipVm.EquipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(text))
                return;

            var sel = EquipVm.SelectedListBoxEquipment?.Equipment;
            if (!string.IsNullOrWhiteSpace(sel))
                text = sel;

            await _soeController.LoadAndShowAsync(text);
        }

        /// <summary>
        /// Cancel: отменяет текущую загрузку SOE.
        /// </summary>
        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _soeController.Cancel();
        }

        /// <summary>
        /// Переключатель левой панели (показать/скрыть).
        /// </summary>
        private void LeftPaneToggle_Click(object sender, RoutedEventArgs e)
        {
            if (!_layoutReady) return;

            bool show = LeftPaneToggle.IsChecked == true;
            ApplyLeftPane(show);
        }

        /// <summary>
        /// Применяет ширину левой панели (скрывает/показывает).
        /// </summary>
        private void ApplyLeftPane(bool show)
        {
            if (LeftCol == null || SepCol == null) return;

            if (show)
            {
                if (_leftSavedWidth.Value <= 0)
                    _leftSavedWidth = new GridLength(260);

                LeftCol.Width = _leftSavedWidth;
                SepCol.Width = new GridLength(1);
            }
            else
            {
                if (LeftCol.Width.Value > 0)
                    _leftSavedWidth = LeftCol.Width;

                LeftCol.Width = new GridLength(0);
                SepCol.Width = new GridLength(0);
            }
        }

        /// <summary>
        /// Единая кнопка для всех вкладок:
        /// SOE -> грузим SOE по EquipName
        /// DB  -> грузим данные выбранной DB вкладки (с фильтром EquipName и датой DbDate)
        /// </summary>
        private async void MainAction_Click(object sender, RoutedEventArgs e)
        {
            switch (SelectedMainTab)
            {
                case MainTabKind.SOE:
                    await LoadSoeFromUiAsync();
                    break;

                case MainTabKind.Info:
                    await _infoController.LoadCurrentAsync();
                    break;

                case MainTabKind.OperationActions:
                case MainTabKind.AlarmHistory:
                    await _dbController.LoadCurrentTabAsync(force: true);
                    break;

                default:
                    // на будущие вкладки
                    await _dbController.LoadCurrentTabAsync(force: true);
                    break;
            }
        }

        /// <summary>SOE: читаем имя из UI (выделение/поиск) и загружаем таблицу</summary>
        private async Task LoadSoeFromUiAsync()
        {
            if (Vm.Shell.IsLoading) return;

            var text = (EquipVm.EquipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(text))
                return;

            // если есть выделение — грузим его
            var sel = EquipVm.SelectedListBoxEquipment?.Equipment;
            if (!string.IsNullOrWhiteSpace(sel))
                text = sel;

            await _soeController.LoadAndShowAsync(text);
        }

        /// <summary>
        /// Верхняя toolbar-кнопка Scan QR:
        /// сканирует QR -> пишет во ExternalTag -> выполняет поиск и открывает Param.
        /// </summary>
        private async void ToolbarScanQr_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await Param_ScanQrToExternalTagAndSearchAsync();
            }
            catch (Exception ex)
            {
                DXMessageBox.Show(this, ex.Message, "Scan QR", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void Logo_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isLogoLoginToggleInProgress)
                return;

            _isLogoLoginToggleInProgress = true;

            try
            {
                var toggleUser = (_config["CtApiSecurity:ToggleLoginUser"] ?? "").Trim();
                var togglePassword = (_config["CtApiSecurity:ToggleLoginPassword"] ?? "").Trim();

                if (string.IsNullOrWhiteSpace(toggleUser))
                {
                    DXMessageBox.Show(this, "Toggle login user is not configured.", "CtApi security", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Current SCADA login name
                var currentUserName = (await _ctApiService.UserInfoAsync(1)).Trim();
                var currentFullName = (await _ctApiService.UserInfoAsync(2)).Trim();

                var isTabLoggedIn = currentUserName.Equals(toggleUser, StringComparison.OrdinalIgnoreCase) || currentFullName.IndexOf("Tab", StringComparison.OrdinalIgnoreCase) >= 0;

                if (isTabLoggedIn)
                {
                    await _ctApiService.LogoutAsync();

                    DXMessageBox.Show(this, "Tab user has been logged out.", "CtApi security", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var loginResult = await _ctApiService.LoginAsync(toggleUser, togglePassword);

                // Plant SCADA Login returns 0 on success
                if (string.Equals(loginResult?.Trim(), "0", StringComparison.OrdinalIgnoreCase))
                    DXMessageBox.Show(this, "Tab user has been logged in.", "CtApi security", MessageBoxButton.OK, MessageBoxImage.Information);
                else
                    DXMessageBox.Show(this, $"Login failed. Result: {loginResult}", "CtApi security", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                DXMessageBox.Show(this, $"Unable to toggle login state.\n\n{ex.Message}", "CtApi security", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isLogoLoginToggleInProgress = false;
            }
        }

        #endregion

        #region Param

        /// <summary>
        /// Определяет, поддерживает ли текущая модель конкретную страницу Param.
        /// Опираемся только на новую архитектуру IParamModel / SupportedPages.
        /// </summary>
        private bool CurrentParamSupportsPage(ParamSettingsPage page)
        {
            if (Vm.Param.CurrentParamModel is not IParamModel paramModel)
                return false;

            return paramModel.SupportedPages.Contains(page);
        }

        /// <summary>
        /// Единая точка переключения Chart / Settings для всех ParamView.
        /// Вся навигация между страницами Param должна идти только через этот метод.
        /// </summary>
        public void ShowParamPage(ParamSettingsPage page)
        {
            if (page != ParamSettingsPage.None && !CurrentParamSupportsPage(page))
                return;

            var (equipName, _, _) = ResolveSelectedEquipForParam();
            equipName = (equipName ?? "").Trim();

            SetParamSettingsPage(page);

            if (page == ParamSettingsPage.None)
            {
                StopParamOverlayWait();
                ShowParamChart(reset: true);
                return;
            }

            ShowParamSettings();

            // При клике по кнопкам страниц equipment не меняется,
            // поэтому main model уже есть и ждём только саму секцию.
            if (page == ParamSettingsPage.DiDo &&
                string.Equals(_lastSectionLoadedEquipName, equipName, StringComparison.OrdinalIgnoreCase) &&
                _lastSectionLoadedPage == ParamSettingsPage.DiDo &&
                IsFinalParamLoadState(_lastSectionLoadedState))
            {
                StopParamOverlayWait();
            }
            else
            {
                BeginParamOverlayWait(equipName, page, needMainModel: false);
            }

            _ = RefreshActiveParamSectionSafeAsync();
        }

        /// <summary>
        /// Безопасное обновление активной Param-секции после клика по кнопке.
        /// </summary>
        private async Task RefreshActiveParamSectionSafeAsync()
        {
            try
            {
                if (!_ctApiService.IsConnectionAvailable)
                    return;

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _paramRefs.RefreshActiveParamSectionAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                StopParamOverlayWait();
            }
            catch (Exception ex)
            {
                Vm.Shell.ParamStatusText = $"Param settings refresh error: {ex.Message}";
                StopParamOverlayWait();
            }
        }

        /// <summary>
        /// Начинаем ожидание обновления центральной области Param.
        /// 
        /// needMainModel = true  -> ждём и основную модель, и секцию
        /// needMainModel = false -> ждём только секцию
        /// </summary>
        private void BeginParamOverlayWait(string? equipName, ParamSettingsPage page, bool needMainModel)
        {
            var key = (equipName ?? "").Trim();

            if (string.IsNullOrWhiteSpace(key))
            {
                StopParamOverlayWait();
                return;
            }

            _pendingParamOverlayEquipName = key;
            _pendingParamOverlayPage = page;
            _pendingParamOverlayNeedsMainModel = needMainModel;

            var mainDone =
                string.Equals(_lastMainLoadedEquipName, key, StringComparison.OrdinalIgnoreCase) &&
                IsFinalParamLoadState(_lastMainLoadedState);

            // Chart: ждём только main model
            if (page == ParamSettingsPage.None)
            {
                Vm.Shell.IsParamCenterLoading = !mainDone;
                return;
            }

            var sectionDone =
                string.Equals(_lastSectionLoadedEquipName, key, StringComparison.OrdinalIgnoreCase) &&
                _lastSectionLoadedPage == page &&
                IsFinalParamLoadState(_lastSectionLoadedState);

            // При простом переключении страницы ждём только секцию
            if (!needMainModel)
            {
                Vm.Shell.IsParamCenterLoading = !sectionDone;
                return;
            }

            // При смене equipment ждём и модель, и секцию
            Vm.Shell.IsParamCenterLoading = !(mainDone && sectionDone);
        }

        /// <summary>
        /// Полностью останавливаем ожидание overlay.
        /// </summary>
        private void StopParamOverlayWait()
        {
            _pendingParamOverlayEquipName = null;
            _pendingParamOverlayPage = ParamSettingsPage.None;
            _pendingParamOverlayNeedsMainModel = false;
            Vm.Shell.IsParamCenterLoading = false;
        }

        /// <summary>
        /// Проверяем, можно ли уже скрывать overlay.
        /// </summary>
        private void TryFinishParamOverlayWait()
        {
            if (!Vm.Shell.IsParamCenterLoading)
                return;

            var key = (_pendingParamOverlayEquipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                StopParamOverlayWait();
                return;
            }

            var mainDone =
                string.Equals(_lastMainLoadedEquipName, key, StringComparison.OrdinalIgnoreCase) &&
                IsFinalParamLoadState(_lastMainLoadedState);

            // Chart: ждём только main
            if (_pendingParamOverlayPage == ParamSettingsPage.None)
            {
                if (mainDone)
                    StopParamOverlayWait();

                return;
            }

            var sectionDone =
                string.Equals(_lastSectionLoadedEquipName, key, StringComparison.OrdinalIgnoreCase) &&
                _lastSectionLoadedPage == _pendingParamOverlayPage &&
                IsFinalParamLoadState(_lastSectionLoadedState);

            // Переключение страницы без смены equipment:
            // ждём только секцию
            if (!_pendingParamOverlayNeedsMainModel)
            {
                if (sectionDone)
                    StopParamOverlayWait();

                return;
            }

            // Полная смена equipment:
            // ждём и main, и section
            if (mainDone && sectionDone)
                StopParamOverlayWait();
        }

        /// <summary>
        /// Уведомление: основная Param-модель по equipment завершила загрузку.
        /// </summary>
        private void NotifyMainParamLoadedCore(string? equipName, ParamLoadState state)
        {
            void Apply()
            {
                _lastMainLoadedEquipName = (equipName ?? "").Trim();
                _lastMainLoadedState = state;
                TryFinishParamOverlayWait();
            }

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke((Action)Apply);
                return;
            }

            Apply();
        }

        /// <summary>
        /// Уведомление: конкретная Param settings-секция завершила загрузку.
        /// </summary>
        private void NotifySectionLoadedCore(string? equipName, ParamSettingsPage page, ParamLoadState state)
        {
            void Apply()
            {
                _lastSectionLoadedEquipName = (equipName ?? "").Trim();
                _lastSectionLoadedPage = page;
                _lastSectionLoadedState = state;
                TryFinishParamOverlayWait();
            }

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke((Action)Apply);
                return;
            }

            Apply();
        }

        private (string equipName, string equipType, string equipDescription) ResolveSelectedEquipForParam()
        {
            var selected = EquipVm.SelectedListBoxEquipment;
            if (selected == null)
                return ("", "", "");

            return (selected.Equipment ?? "", selected.Type ?? "", selected.Description ?? "");
        }

        #endregion

        #region Param polling

        private void StartParamPolling()
        {
            _paramController?.Start();
        }

        private void StopParamPolling()
        {
            _paramController?.Stop();
        }

        #endregion

        #region Param Write
        /// <summary>
        /// PLC: запись значения из UI (SimpleButton и т.п.).
        /// Теперь вся логика в ParamWriteController.
        /// </summary>
        public async void ParamPlc_WriteFromUi(PlcRefRow row, object? newValue)
        {
            if (_paramWriteController == null)
                return;

            await _paramWriteController.WritePlcFromUiAsync(row, newValue);
        }

        /// <summary>
        /// DevExpress EditValueChanged (CheckEdit и др.) -> запись параметров.
        /// </summary>
        public async void ParamEditable_EditValueChanged(object sender, DevExpress.Xpf.Editors.EditValueChangedEventArgs e)
        {
            if (_paramWriteController == null)
                return;

            await _paramWriteController.OnEditValueChangedAsync(sender, e);
        }

        /// <summary>
        /// KeyDown/PreviewKeyDown: запись по Enter.
        /// </summary>
        public async void ParamEditable_EditValueChanged(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (_paramWriteController == null)
                return;

            await _paramWriteController.OnPreviewKeyDownAsync(sender, e);
        }

        /// <summary>
        /// Универсальная запись параметра (если не DevExpress событие).
        /// </summary>
        public async void ParamEditable_WriteFromUi(string? equipItem, object? newValue, object? oldValue)
        {
            if (_paramWriteController == null)
                return;

            await _paramWriteController.WriteFromUiAsync(equipItem, newValue, oldValue);
        }

        public void BeginParamFieldEdit()
        {
            Interlocked.Exchange(ref _isEditingField, 1);
        }

        // Сбрасываем, когда пользователь закончил редактирование (lost focus или Enter)
        public void EndParamFieldEdit()
        {
            Interlocked.Exchange(ref _isEditingField, 0);
        }

        #endregion

        #region Param Refs

        /// <summary>
        /// Устанавливает активную страницу Param settings.
        /// Вынесено в ParamRefsController.
        /// </summary>
        public void SetParamSettingsPage(ParamSettingsPage page)
            => _paramRefs.SetParamSettingsPage(page);

        /// <summary>
        /// Переход по клику из DI/DO списка к связанному оборудованию.
        /// Вынесено в ParamRefsController.
        /// </summary>
        public void Param_NavigateToLinkedEquip(DiDoRefRow? row)
            => _paramRefs.NavigateToLinkedEquip(row);

        /// <summary>
        /// Переход к связанному оборудованию по имени.
        /// Вынесено в ParamRefsController.
        /// </summary>
        public void Param_NavigateToLinkedEquip(string? equipName)
            => _paramRefs.NavigateToLinkedEquip(equipName);

        #endregion

        #region Trend

        // прокси для View
        public void OnParamChartUserRangeChanged(DateTime minLocal, DateTime maxLocal)
            => _trendCtl.OnUserRangeChanged(minLocal, maxLocal);

        public void SetParamChartLiveMode(bool resetPoints = false)
            => _trendCtl.SetLiveMode(resetPoints);

        public void ShowParamChart(bool reset = false)
            => _trendCtl.ShowChart(reset);

        public void ShowParamSettings()
            => _trendCtl.ShowSettings();

        /// <summary>
        /// Called from AIParamView.ParamChart_BoundDataChanged.
        /// Re-applies [TrendSeriesStyle] attributes to recreated DevExpress series.
        /// </summary>
        public void ApplyTrendSeriesStyles(ChartControl chart)
            => TrendSeriesStyler.Apply(chart, Vm.Param.CurrentParamModel);
        #endregion

        #region QR-Code       

        /// <summary>
        /// Param tab: генерирует QR по текущему тексту поиска (EquipName) или выбранному оборудованию.
        /// Автосохраняет в .\QRCodes\Station\Type\*.png и показывает DevExpress окно об успехе.
        /// </summary>
        public Task Param_GenerateQrAsync() => _qrController.GenerateQrAsync();

        /// <summary>
        /// Param tab: сканирует QR с камеры.
        /// Затем:
        /// 1) выставляет Station/Type фильтры по найденному оборудованию,
        /// 2) пишет в ExternalTag (best-effort),
        /// 3) подставляет в поиск,
        /// 4) выделяет оборудование,
        /// 5) переключает на Param и запускает polling.
        /// </summary>
        public Task Param_ScanQrToExternalTagAndSearchAsync() => _qrController.ScanQrToExternalTagAndSearchAsync();

        /// <summary>
        /// Проверяет, существует ли уже QR PNG файл для текущего текста (с учётом Station\TypeGroup папки).
        /// Это используется для скрытия кнопки Generate QR.
        /// </summary>
        public bool Param_IsQrAlreadyGenerated() => _qrController.IsQrAlreadyGenerated();

        /// <summary>
        /// True => показываем кнопку Generate QR.
        /// False => прячем (нет текста для QR или файл уже существует).
        /// Используется в XAML через BoolToVis.
        /// </summary>
        public bool Param_ShowGenerateQrButton => _qrController.ShowGenerateQrButton;

        /// <summary>
        /// Уведомляет UI, что нужно пересчитать Visibility кнопки Generate QR.
        /// </summary>
        private void NotifyParamQrUiChanged()
        {
            OnPropertyChanged(nameof(Param_ShowGenerateQrButton));
        }

        #endregion

        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        #endregion

        #region Helpers

        /// <summary>
        /// Возвращает имя оборудования для записи.
        ///
        /// По умолчанию запись идёт в текущее выбранное оборудование (EquipName).
        /// Но:
        /// - если отправитель находится в секции DryRun (DataContext = DryRunMotor),
        ///   то писать нужно в DryRunEquipName;
        /// - если отправитель находится в секции linked ATV (DataContext = AtvParam)
        ///   и сейчас открыт Motor -> ATV page,
        ///   то писать нужно в LinkedAtvEquipName.
        /// </summary>
        private string ResolveEquipNameForWrite(object sender)
        {
            // Обычная запись — в текущее выбранное оборудование
            var currentEquip = (EquipVm.EquipName ?? "").Trim();

            if (sender is FrameworkElement fe)
            {
                // DryRun секция работает с другим target-equipment
                if (fe.DataContext is DryRunMotor)
                {
                    var dryRunEquip = (Vm.Param.DryRunEquipName ?? "").Trim();

                    if (!string.IsNullOrWhiteSpace(dryRunEquip))
                        return dryRunEquip;

                    return currentEquip;
                }

                // ATV секция внутри Motor работает с linked ATV equipment
                if (fe.DataContext is AtvParam)
                {
                    var (_, equipType, _) = ResolveSelectedEquipForParam();
                    var currentGroup = EquipTypeRegistry.GetGroup(equipType ?? "");

                    if (currentGroup == EquipTypeGroup.Motor &&
                        Vm.Param.CurrentParamSettingsPage == ParamSettingsPage.Atv)
                    {
                        var linkedAtvEquip = (Vm.Param.LinkedAtvEquipName ?? "").Trim();

                        if (!string.IsNullOrWhiteSpace(linkedAtvEquip))
                            return linkedAtvEquip;
                    }
                }
            }

            return currentEquip;
        }

        #endregion

        #region Settings

        /// <summary>
        /// Глобальная горячая клавиша окна:
        /// F10 -> открыть модальное окно редактирования appsettings.json.
        /// </summary>
        private void ThemedWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.SystemKey != Key.F10)
                return;

            e.Handled = true;
            ShowAppSettingsWindow();
        }

        /// <summary>
        /// Открывает модальное окно настроек.
        /// Редактируется тот же appsettings.json, который читает Host:
        /// AppContext.BaseDirectory\appsettings.json
        /// </summary>
        private void ShowAppSettingsWindow()
        {
            try
            {
                var settingsPath = GetRuntimeAppSettingsPath();

                var wnd = new AppSettingsWindow(settingsPath)
                {
                    Owner = this
                };

                wnd.ShowDialog();
            }
            catch (Exception ex)
            {
                DXMessageBox.Show($"Failed to open settings window.\n\n{ex.Message}", "Settings", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Возвращает путь к runtime appsettings.json.
        /// Это важно:
        /// приложение читает именно файл рядом с exe, а не исходник из корня проекта.
        /// </summary>
        private static string GetRuntimeAppSettingsPath()
        {
            return Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        }

        #endregion

        #region Info

        /// <summary>
        /// Тонкий прокси для InfoTabHost.xaml.cs
        /// </summary>
        public Task LoadInfoForCurrentEquipmentAsync()
            => _infoController.LoadCurrentAsync();

        /// <summary>
        /// Тонкий прокси для InfoTabHost.xaml.cs
        /// </summary>
        public void Info_BeginEdit()
            => _infoController.BeginEdit();

        /// <summary>
        /// Тонкий прокси для InfoTabHost.xaml.cs
        /// </summary>
        public Task Info_SaveAsync()
            => _infoController.SaveAsync();

        /// <summary>
        /// Добавить фото с диска.
        /// </summary>
        public Task Info_LoadPhotoFilesAsync()
            => _infoController.LoadPhotoFilesAsync();

        /// <summary>
        /// Удалить выбранное фото из карточки.
        /// </summary>
        public void Info_RemoveSelectedPhoto()
            => _infoController.RemoveSelectedPhoto();

        /// <summary>
        /// Добавить PDF-файлы для текущей документной страницы.
        /// </summary>
        public Task Info_LoadCurrentDocumentFilesAsync()
            => _infoController.LoadCurrentDocumentFilesAsync();

        /// <summary>
        /// Удалить выбранный PDF с текущей документной страницы.
        /// </summary>
        public Task Info_RemoveCurrentDocumentAsync()
            => _infoController.RemoveCurrentDocumentAsync();

        /// <summary>
        /// Переключение страниц Info.
        /// </summary>
        public Task ShowInfoPageAsync(InfoPageKind page)
            => _infoController.ShowPageAsync(page);

        /// <summary>
        /// Подготовка preview после смены выбранного документа.
        /// </summary>
        public Task Info_OnCurrentDocumentSelectionChangedAsync()
            => _infoController.PrepareCurrentDocumentAsync();

        /// <summary>
        /// Выгрузить выбранный документ из БД/модели в локальный cache.
        /// </summary>
        public Task Info_ExportCurrentDocumentAsync()
            => _infoController.ExportCurrentDocumentAsync();

        /// <summary>
        /// Синхронизировать linked photo files из checked-combo.
        /// </summary>
        public Task Info_OnPhotoLibraryEditValueChangedAsync()
            => _infoController.SyncPhotoSelectionFromLibraryAsync();

        /// <summary>
        /// Синхронизировать linked document files из checked-combo.
        /// </summary>
        public Task Info_OnDocumentLibraryEditValueChangedAsync()
            => _infoController.SyncCurrentDocumentSelectionFromLibraryAsync();

        public Task Info_CapturePhotoFromCameraAsync()
            => _infoController.CapturePhotoFromCameraAsync();

        #endregion

        private void SubscribeEquipmentListBridge()
        {
            EquipVm.PropertyChanged += (_, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(EquipmentListViewModel.EquipName):
                        _equipmentListController.ScheduleSearch(EquipVm.EquipName);
                        _uiState.ScheduleSave();
                        NotifyParamQrUiChanged();
                        break;

                    case nameof(EquipmentListViewModel.SelectedListBoxEquipment):
                        NotifyParamQrUiChanged();
                        break;

                    case nameof(EquipmentListViewModel.SelectedTypeFilter):
                    case nameof(EquipmentListViewModel.SelectedStation):
                        RestoreOrSelectEquipmentAfterFilterChanged();
                        _uiState.ScheduleSave();
                        NotifyParamQrUiChanged();
                        break;
                }
            };
        }

    }
}