using CtApi;
using DevExpress.Xpf.Charts;
using DevExpress.Xpf.Core;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TechEquipments.Views.Settings;
using TechEquipments.ViewModels;

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
        private ParamController _paramController;
        private ParamWriteController _paramWriteController;
        private readonly ParamRefsController _paramRefs;
        private readonly DbController _dbController;
        private readonly QrController _qrController;
        private readonly SoeController _soeController;
        private readonly UiStateController _uiState;
        private readonly InfoController _infoController;

        public MainViewModel Vm { get; }

        #region Fields

        #region Services
        private readonly IEquipmentService _equipmentService;
        private readonly ICtApiService _ctApiService;
        private readonly IConfiguration _config;

        #endregion

        #region UI Collections (data sources)

        /// <summary>Строки SOE (вкладка SOE).</summary>
        public ObservableCollection<EquipmentSOEDto> equipmentSOEDtos { get; } = new();

        /// <summary>Список оборудования (левая панель).</summary>
        public ObservableCollection<EquipListBoxItem> Equipments => Vm.EquipmentList.Equipments;

        /// <summary>Список станций для фильтра (Station).</summary>
        public ObservableCollection<string> Stations => Vm.EquipmentList.Stations;

        #endregion

        #region Left pane: search + filters + selection

        /// <summary>
        /// Текст в поле поиска (правый верх).
        /// При изменении запускается “посимвольный” поиск в ListBox.
        /// </summary>
        public string EquipName
        {
            get => Vm.EquipmentList.EquipName;
            set
            {
                if (Vm.EquipmentList.EquipName == value) return;
                Vm.EquipmentList.EquipName = value;
                OnPropertyChanged();

                ScheduleSearch(Vm.EquipmentList.EquipName);     // твой debounce поиска
                _uiState.ScheduleSave();            // debounce сохранения состояния
                NotifyParamQrUiChanged();       // пересчитать Visibility кнопки Generate QR
            }
        }

        /// <summary>
        /// Выбранный элемент в ListBox.
        /// </summary>
        public EquipListBoxItem? SelectedListBoxEquipment
        {
            get => Vm.EquipmentList.SelectedListBoxEquipment;
            set
            {
                if (ReferenceEquals(Vm.EquipmentList.SelectedListBoxEquipment, value))
                    return;

                Vm.EquipmentList.SelectedListBoxEquipment = value;
                OnPropertyChanged();
                NotifyParamQrUiChanged();   // пересчитать Visibility кнопки Generate QR
            }
        }

        /// <summary>
        /// View для Equipments с фильтром Station/Type.
        /// </summary>
        public ICollectionView EquipmentsView { get; private set; } = null!;

        /// <summary>
        /// Значения для фильтра Type.
        /// </summary>
        public Array TypeFilters { get; } = Enum.GetValues(typeof(EquipTypeGroup));

        public EquipTypeGroup SelectedTypeFilter
        {
            get => Vm.EquipmentList.SelectedTypeFilter;
            set
            {
                if (Vm.EquipmentList.SelectedTypeFilter == value) return;

                Vm.EquipmentList.SelectedTypeFilter = value;
                OnPropertyChanged();

                ApplyFilters();
                RestoreOrSelectEquipmentAfterFilterChanged();

                _uiState.ScheduleSave();
                NotifyParamQrUiChanged();   // пересчитать Visibility кнопки Generate QR
            }
        }

        public string SelectedStation
        {
            get => Vm.EquipmentList.SelectedStation;
            set
            {
                value = string.IsNullOrWhiteSpace(value) ? "All" : value.Trim();
                if (string.Equals(Vm.EquipmentList.SelectedStation, value, StringComparison.OrdinalIgnoreCase)) return;

                Vm.EquipmentList.SelectedStation = value;
                OnPropertyChanged();

                ApplyFilters();
                RestoreOrSelectEquipmentAfterFilterChanged();

                _uiState.ScheduleSave();
                NotifyParamQrUiChanged();   // пересчитать Visibility кнопки Generate QR
            }
        }

        // --- incremental search support ---
        private DispatcherTimer _searchTimer = null!;
        private string _pendingSearch = "";

        // Подавляем побочные эффекты во время программного выбора оборудования.
        private bool _isApplyingFilterSelection;

        // Память последнего выбранного оборудования для комбинации Station + Type.
        // Ключ: "Station|TypeGroup".
        private readonly Dictionary<string, string> _lastEquipByFilterKey = new(StringComparer.OrdinalIgnoreCase);

        private bool _suppressEquipNameFromSelection;

        #endregion

        #region Equipments list loading (bottom bar determinate)

        /// <summary>
        /// CTS для отмены загрузки списка оборудования.
        /// </summary>
        private CancellationTokenSource? _equipListCts;

        /// <summary>
        /// Максимум для прогрессбара списка оборудования.
        /// </summary>
        public int EquipListMax => Math.Max(1, Vm.EquipmentList.EquipListTotal);

        /// <summary>
        /// Текст статуса списка оборудования (для нижней панели).
        /// </summary>
        public string EquipListText =>
            Vm.EquipmentList.IsEquipListLoading
                ? $"Loading equipments: {Vm.EquipmentList.EquipListDone}/{Vm.EquipmentList.EquipListTotal}"
                : $"Equipments: {Equipments.Count}";

        #endregion

        #region DB loading (bottom bar indeterminate)

        /// <summary>
        /// Param-загрузка влияет на нижний progress bar только на вкладке Param.
        /// Иначе при переходе на Info/DB/SOE можно получить "залипший" нижний индикатор.
        /// </summary>
        public bool IsBottomLoading =>
            Vm.EquipmentList.IsEquipListLoading ||
            Vm.Database.IsDbLoading ||
            (!Vm.Shell.UseParamAreaOverlay && SelectedMainTab == MainTabKind.Param && Vm.Shell.IsParamCenterLoading);

        /// <summary>
        /// Текст внизу:
        /// - при загрузке списка показывает прогресс списка
        /// - при загрузке DB показывает прогресс DB
        /// </summary>
        public string BottomText
        {
            get
            {
                if (Vm.EquipmentList.IsEquipListLoading)
                    return EquipListText;

                if (!Vm.Shell.UseParamAreaOverlay && SelectedMainTab == MainTabKind.Param && Vm.Shell.IsParamCenterLoading)
                    return ParamBottomLoadingText;

                return Vm.Shell.BottomText;
            }
            set
            {
                Vm.Shell.BottomText = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(BottomStatusText));
            }
        }

        /// <summary>
        /// Нижняя панель видна либо когда что-то грузится,
        /// либо когда потеряна связь с CtApi.
        /// </summary>
        public bool IsBottomStatusVisible => IsBottomLoading || !Vm.Shell.IsCtApiConnected;

        /// <summary>
        /// Если CtApi disconnected — показываем сообщение о связи.
        /// Иначе используем обычный BottomText.
        /// </summary>
        public string BottomStatusText =>
            !Vm.Shell.IsCtApiConnected && !string.IsNullOrWhiteSpace(Vm.Shell.CtApiStatusText)
                ? Vm.Shell.CtApiStatusText
                : BottomText;

        /// <summary>
        /// Красный цвет при потере связи, обычный — во всех остальных случаях.
        /// </summary>
        public Brush BottomStatusBrush => !Vm.Shell.IsCtApiConnected ? Brushes.Red : Brushes.Black;

        public int SelectedMainTabIndex
        {
            get => Vm.SelectedMainTabIndex;
            set
            {
                if (Vm.SelectedMainTabIndex == value) return;
                Vm.SelectedMainTabIndex = value;
                OnPropertyChanged();

                // уведомляем всё, что зависит от выбранной вкладки
                OnPropertyChanged(nameof(SelectedMainTab));
                OnPropertyChanged(nameof(IsDbTabSelected));
                OnPropertyChanged(nameof(MainActionButtonText));
                OnPropertyChanged(nameof(CanMainAction));
                OnPropertyChanged(nameof(IsBottomLoading));

                OnPropertyChanged(nameof(ShowToolbarScanQrButton));
                OnPropertyChanged(nameof(ShowMainActionButton));

                // ВАЖНО: во время восстановления состояния никаких автодействий
                if (_uiState.IsRestoringState) return;

                _dbController.CancelCurrentLoad(); // отменяем предыдущую DB-загрузку при смене вкладки

                _uiState.ScheduleSave(); // сохраняем состояние (debounce)

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

                // Если мы на DB вкладке и есть коннект — планируем авто-загрузку (debounce)
                _dbController.ScheduleReload();

                _uiState.ScheduleSave();
            }
        }

        /// <summary>Текущая вкладка как enum (задел на будущие вкладки)</summary>
        public MainTabKind SelectedMainTab => Vm.SelectedMainTab;

        /// <summary>Показывать DateEdit только на DB вкладках</summary>
        public bool IsDbTabSelected => SelectedMainTab is MainTabKind.OperationActions or MainTabKind.AlarmHistory;

        /// <summary>Текст основной кнопки (одна на все режимы)</summary>
        public string MainActionButtonText => SelectedMainTab switch
        {
            MainTabKind.SOE => "Load",
            MainTabKind.OperationActions => "Search",
            MainTabKind.AlarmHistory => "Search",
            MainTabKind.Info => "",
            _ => "Run",
        };

        /// <summary>Можно ли нажимать основную кнопку</summary>
        public bool CanMainAction => SelectedMainTab switch
        {
            MainTabKind.SOE => !Vm.Shell.IsLoading,
            MainTabKind.Info => false,
            MainTabKind.Param => false,
            _ => Vm.Database.IsDbConnected && !Vm.Database.IsDbLoading,
        };

        /// <summary>
        /// Показываем кнопку Scan QR только на вкладке Param.
        /// </summary>
        public bool ShowToolbarScanQrButton => SelectedMainTab == MainTabKind.Param;

        /// <summary>
        /// Основную кнопку Run/Search/Load на вкладках Param и Info скрываем.
        /// </summary>
        public bool ShowMainActionButton => SelectedMainTab != MainTabKind.Param && SelectedMainTab != MainTabKind.Info;

        /// <summary>Показываем нижний прогресс только когда что-то грузим</summary>
        public bool IsBottomProgressVisible =>
            Vm.EquipmentList.IsEquipListLoading ||
            Vm.Database.IsDbLoading ||
            (!Vm.Shell.UseParamAreaOverlay && SelectedMainTab == MainTabKind.Param && Vm.Shell.IsParamCenterLoading);

        /// <summary>
        /// Режим нижнего прогресса:
        /// - список оборудования: детерминированный
        /// - DB: индетерминированный
        /// </summary>
        public bool BottomProgressIsIndeterminate =>
            ((!Vm.Shell.UseParamAreaOverlay && SelectedMainTab == MainTabKind.Param && Vm.Shell.IsParamCenterLoading && !Vm.EquipmentList.IsEquipListLoading))
            || (Vm.Database.IsDbLoading && !Vm.EquipmentList.IsEquipListLoading);

        /// <summary>Максимум для нижнего прогресса</summary>
        public int BottomProgressMaximum => Vm.EquipmentList.IsEquipListLoading ? EquipListMax : 100;

        /// <summary>Текущее значение для нижнего прогресса</summary>
        public int BottomProgressValue => Vm.EquipmentList.IsEquipListLoading ? Vm.EquipmentList.EquipListDone : 0;

        #endregion

        #region Left pane toggle state

        private bool _layoutReady;
        private GridLength _leftSavedWidth = new(260);

        #endregion

        #region Params

        /// <summary>
        /// Текст загрузки Param для нижней панели,
        /// когда overlay в центре отключен.
        /// </summary>
        public string ParamBottomLoadingText => string.IsNullOrWhiteSpace(Vm.Shell.ParamStatusText) ? "Updating data..." : Vm.Shell.ParamStatusText;

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

        // 1) Общий “замок” на чтение/запись Param (чтение и запись не пересекаются)
        private readonly SemaphoreSlim _paramRwGate = new(1, 1);

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


        // ===== Param editing (anti-overwrite during typing) =====

        // 0/1 флаг (Interlocked/Volatile, чтобы безопасно читать из background polling)
        private int _isEditingField;

        // Быстрая проверка из polling
        private bool IsEditingField => System.Threading.Volatile.Read(ref _isEditingField) == 1;

        private static bool IsFinalParamLoadState(ParamLoadState state)
            => state is ParamLoadState.Ready or ParamLoadState.Unavailable or ParamLoadState.Error;

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

        #endregion

        #region Trend

        public ParamTrendVm Trend { get; }

        private ParamTrendController _trendCtl;

        #endregion

        #region Security

        private bool _isLogoLoginToggleInProgress;

        #endregion

        #endregion


        public MainWindow(IEquipmentService equipmentService, IDbService dbService, IEquipInfoService equipInfoService, IUserStateService stateService, ICtApiService ctApiService, IConfiguration config, IQrCodeService qrCodeService, IQrScannerService qrScannerService, MainViewModel vm)
        {
            InitializeComponent();

            Vm = vm;
            SubscribeVmNotifications();
            Vm.EquipmentList.Equipments.CollectionChanged += (_, __) =>
            {
                OnPropertyChanged(nameof(EquipListText));
                OnPropertyChanged(nameof(BottomText));
                OnPropertyChanged(nameof(BottomStatusText));
            };

            _equipmentService = equipmentService;
            _ctApiService = ctApiService;

            _ctApiService.ConnectionStateChanged += OnCtApiConnectionStateChanged;
            Closed += (_, __) => _ctApiService.ConnectionStateChanged -= OnCtApiConnectionStateChanged;

            Vm.Shell.IsCtApiConnected = _ctApiService.IsConnectionAvailable;

            _config = config;
            _dbController = new DbController(dbService, Vm, Dispatcher);
            _infoController = new InfoController(equipInfoService, Vm.Info, Vm.EquipmentList, Vm.Database, this);
            
            _qrController = new QrController(
                _equipmentService,
                qrCodeService,
                qrScannerService,
                Vm,
                this,
                text => EquipName = text,
                station => SelectedStation = station,
                type => SelectedTypeFilter = type,
                tabIndex => SelectedMainTabIndex = tabIndex,
                DoIncrementalSearch,
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
                equipName => EquipName = equipName,
                dbDate => DbDate = dbDate,
                station => SelectedStation = station,
                type => SelectedTypeFilter = type,
                tabIndex => SelectedMainTabIndex = tabIndex,
                ExportRememberedEquipmentsByFilter,
                ImportRememberedEquipmentsByFilter);

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
                pollTrendOnceSafeAsync: ct => _trendCtl.PollOnceSafeAsync(ct, txt => BottomText = txt),
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
                FilterEquipment,
                ApplyFilters,
                DoIncrementalSearch,
                ShowParamChart,
                StartParamPolling,
                tabIndex => SelectedMainTabIndex = tabIndex,
                equipName => EquipName = equipName,
                station => SelectedStation = station,
                type => SelectedTypeFilter = type,
                NotifySectionLoadedCore,
                () => Vm.Param.SuppressParamWritesFromPolling = true,
                () => Dispatcher.BeginInvoke(new Action(() =>
                {
                    Vm.Param.SuppressParamWritesFromPolling = false;
                }), DispatcherPriority.ContextIdle));

            DataContext = this; // DataContext на себя: используется во всём XAML (binding)

            InitEquipmentsView();
            InitSearchTimer();

            Loaded += async (_, __) =>
            {
                _layoutReady = true;
                InitLeftPaneState();

                // Сразу на старте проверяем CtApi.
                // Если связи нет — показываем сообщение и закрываем приложение.
                //if (!await EnsureCtApiAvailableAtStartupAsync())
                //    return;

                // 1) ExternalTag имеет приоритет над user-state.json
                //    Если ExternalTag пустой -> восстановим с файла
                var usedExt = await _uiState.TryApplyStartupStateFromExternalTagAsync();
                if (!usedExt)
                    await _uiState.RestoreStateAsync();

                // 2) Параллельные загрузки
                await LoadEquipmentsListAsync();
                await _dbController.CheckDbAsync();

                // 3) И как будто нажали “поиск/лоад” на текущей вкладке
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

                BottomText = "Loading equipments...";

                await Dispatcher.Yield(DispatcherPriority.Background);

                var progress = new Progress<(int done, int total)>(p =>
                {
                    Vm.EquipmentList.EquipListDone = p.done;
                    Vm.EquipmentList.EquipListTotal = p.total;
                    BottomText = $"Loading equipments: {p.done}/{p.total}";
                });

                var items = await _equipmentService.GetAllEquipmentsAsync(progress, ct);

                Equipments.Clear();
                foreach (var it in items)
                    Equipments.Add(it);

                Stations.Clear();
                Stations.Add("All");

                foreach (var st in items
                    .Select(x => x.Station)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
                {
                    Stations.Add(st);
                }

                // Если выбранная станция исчезла — откатываемся на All
                if (!Stations.Any(s => string.Equals(s, SelectedStation, StringComparison.OrdinalIgnoreCase)))
                    SelectedStation = "All";

                // На этом этапе Equipments уже загружены.
                // Безопасно нормализуем выбор: remembered -> current -> first item.
                ApplyFilters();
                RestoreOrSelectEquipmentAfterFilterChanged();

                // Если на старте использовали ExternalTag — попробуем выставить Station/TypeGroup
                // по найденному оборудованию. Если это оборудование уже исчезло,
                // RestoreOrSelectEquipmentAfterFilterChanged() выберет первый доступный элемент.
                if (_uiState.StartupUsedExternalTag && !string.IsNullOrWhiteSpace(_uiState.StartupExternalTag))
                {
                    _qrController.TryApplyStationTypeFiltersFromQr(_uiState.StartupExternalTag);
                    RestoreOrSelectEquipmentAfterFilterChanged();
                }

                BottomText = $"Equipments: {Equipments.Count}";
            }
            catch (OperationCanceledException)
            {
                BottomText = "Equipments loading cancelled";
            }
            catch (Exception ex)
            {
                BottomText = $"Equip list error: {ex.Message}";
                DXMessageBox.Show(this, ex.ToString(), "Equip list error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Vm.EquipmentList.IsEquipListLoading = false;

                // Даём UI шанс перерисовать нижнюю панель (скрыть/показать)
                await Dispatcher.Yield(DispatcherPriority.Render);

                // Финальная нормализация выбора после загрузки списка.
                // Если сохранённого/введённого оборудования уже нет,
                // просто выберем первое доступное под текущим фильтром.
                RestoreOrSelectEquipmentAfterFilterChanged();
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

                    OnPropertyChanged(nameof(IsBottomStatusVisible));
                    OnPropertyChanged(nameof(BottomStatusText));
                    OnPropertyChanged(nameof(BottomStatusBrush));
                    return;
                }

                Vm.Shell.CtApiStatusText = "";

                // Сообщение о восстановлении связи показываем в обычном BottomText.
                BottomText = string.IsNullOrWhiteSpace(message)
                    ? $"CtApi connection restored at {DateTime.Now:HH:mm:ss}"
                    : message;

                OnPropertyChanged(nameof(IsBottomStatusVisible));
                OnPropertyChanged(nameof(BottomStatusText));
                OnPropertyChanged(nameof(BottomStatusBrush));
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
                BottomText = "Checking CtApi connection...";
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

        #endregion

        #region Left pane init

        /// <summary>Начальное состояние: левая панель скрыта.</summary>
        private void InitLeftPaneState()
        {
            LeftPaneToggle.IsChecked = false;
            ApplyLeftPane(false);
        }

        /// <summary>Создаёт ICollectionView для Equipments и вешает фильтр/сортировку.</summary>
        private void InitEquipmentsView()
        {
            EquipmentsView = CollectionViewSource.GetDefaultView(Equipments);
            EquipmentsView.Filter = FilterEquipment;

            EquipmentsView.SortDescriptions.Clear();
            EquipmentsView.SortDescriptions.Add(new SortDescription(nameof(EquipListBoxItem.Equipment), ListSortDirection.Ascending));

            OnPropertyChanged(nameof(EquipmentsView)); // важно, если метод вызвали после того как UI уже связан
        }

        /// <summary>Таймер для посимвольного поиска (debounce 150мс).</summary>
        private void InitSearchTimer()
        {
            _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _searchTimer.Tick += (_, __) =>
            {
                _searchTimer.Stop();
                DoIncrementalSearch(_pendingSearch);
            };
        }

        #endregion

        #region Search

        /// <summary>Запускает отложенный поиск (debounce).</summary>
        private void ScheduleSearch(string text)
        {
            _pendingSearch = text ?? "";
            _searchTimer.Stop();
            _searchTimer.Start();
        }

        /// <summary>Ищет элемент в EquipmentsView и выделяет его в ListBox.</summary>
        private void DoIncrementalSearch(string text)
        {
            if (EquipmentsView == null) return;

            text = (text ?? "").Trim();
            if (text.Length == 0) return;

            var match =
                EquipmentsView.Cast<object>()
                    .OfType<EquipListBoxItem>()
                    .FirstOrDefault(x => x.Equipment.StartsWith(text, StringComparison.OrdinalIgnoreCase))
                ?? EquipmentsView.Cast<object>()
                    .OfType<EquipListBoxItem>()
                    .FirstOrDefault(x => x.Equipment.Contains(text, StringComparison.OrdinalIgnoreCase));

            if (match == null) return;

            _suppressEquipNameFromSelection = true;
            try
            {
                SelectedListBoxEquipment = match;
                EquipmentsView.MoveCurrentTo(match);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    EquipmentsListBox?.ScrollIntoView(match);
                }), DispatcherPriority.Background);
            }
            finally
            {
                _suppressEquipNameFromSelection = false;
            }
        }

        #endregion

        #region Filters

        /// <summary>Фильтр для EquipmentsView: Station + Type.</summary>
        private bool FilterEquipment(object obj)
        {
            if (obj is not EquipListBoxItem it)
                return false;

            // Station filter
            var st = (SelectedStation ?? "All").Trim();
            if (!string.Equals(st, "All", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(it.Station, st, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            // Type filter
            if (SelectedTypeFilter == EquipTypeGroup.All)
                return true;

            return it.TypeGroup == SelectedTypeFilter;
        }

        /// <summary>Применяет фильтры (перерисовка представления).</summary>
        private void ApplyFilters()
        {
            EquipmentsView.Refresh();
        }

        /// <summary>
        /// Формирует ключ словаря памяти выбора.
        /// </summary>
        private string BuildFilterSelectionKey(string? station, EquipTypeGroup typeGroup)
        {
            var st = string.IsNullOrWhiteSpace(station) ? "All" : station.Trim();
            return $"{st}|{typeGroup}";
        }

        /// <summary>
        /// Запоминает выбранное оборудование для текущей комбинации фильтров.
        /// </summary>
        private void RememberEquipmentForCurrentFilters(string? equipName)
        {
            var eq = (equipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(eq))
                return;

            var key = BuildFilterSelectionKey(SelectedStation, SelectedTypeFilter);
            _lastEquipByFilterKey[key] = eq;
        }

        /// <summary>
        /// Импортирует карту из user-state.json.
        /// Здесь мы только нормализуем входные данные.
        /// Проверка фактического наличия оборудования будет позже,
        /// когда список Equipments уже загрузится.
        /// </summary>
        private void ImportRememberedEquipmentsByFilter(Dictionary<string, string>? state)
        {
            _lastEquipByFilterKey.Clear();

            if (state == null || state.Count == 0)
                return;

            foreach (var pair in state)
            {
                var key = (pair.Key ?? "").Trim();
                var equip = (pair.Value ?? "").Trim();

                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(equip))
                    continue;

                _lastEquipByFilterKey[key] = equip;
            }
        }

        /// <summary>
        /// Экспортирует карту памяти выбора для сохранения.
        /// Здесь же чистим "мусор":
        /// - пустые ключи,
        /// - пустые значения,
        /// - оборудование, которого уже нет в проекте.
        /// </summary>
        private Dictionary<string, string> ExportRememberedEquipmentsByFilter()
        {
            var existingEquipments = Equipments
                .Select(x => (x.Equipment ?? "").Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var pair in _lastEquipByFilterKey)
            {
                var key = (pair.Key ?? "").Trim();
                var equip = (pair.Value ?? "").Trim();

                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(equip))
                    continue;

                // Если оборудование уже исчезло из проекта —
                // не сохраняем его обратно в user-state.json.
                if (!existingEquipments.Contains(equip))
                    continue;

                result[key] = equip;
            }

            return result;
        }

        /// <summary>
        /// После смены фильтров:
        /// 1) пробуем восстановить оборудование, запомненное для текущей пары Station+Type;
        /// 2) если оно уже исчезло, удаляем устаревшую запись из памяти;
        /// 3) если текущее EquipName подходит под фильтр — используем его;
        /// 4) иначе берём первый элемент в отфильтрованном списке.
        /// </summary>
        private void RestoreOrSelectEquipmentAfterFilterChanged()
        {
            if (EquipmentsView == null)
                return;

            var visibleItems = EquipmentsView
                .Cast<object>()
                .OfType<EquipListBoxItem>()
                .Where(x => !string.IsNullOrWhiteSpace(x.Equipment))
                .ToList();

            // Вообще ничего не найдено под текущий фильтр.
            if (visibleItems.Count == 0)
            {
                _isApplyingFilterSelection = true;
                _suppressEquipNameFromSelection = true;

                try
                {
                    SelectedListBoxEquipment = null;

                    // Важно:
                    // очищаем строку текущего оборудования,
                    // иначе Info продолжит жить на старом EquipName.
                    if (!string.IsNullOrWhiteSpace(EquipName))
                        EquipName = "";
                }
                finally
                {
                    _suppressEquipNameFromSelection = false;
                    _isApplyingFilterSelection = false;
                }

                StopParamOverlayWait();

                // Если пользователь сейчас на Info,
                // нужно явно очистить / перегрузить карточку.
                if (!_uiState.IsRestoringState && SelectedMainTab == MainTabKind.Info)
                    _ = _infoController.LoadCurrentAsync();

                return;
            }

            var key = BuildFilterSelectionKey(SelectedStation, SelectedTypeFilter);

            _lastEquipByFilterKey.TryGetValue(key, out var rememberedEquip);
            rememberedEquip = (rememberedEquip ?? "").Trim();

            var currentEquip = (EquipName ?? "").Trim();

            EquipListBoxItem? rememberedMatch = null;
            if (!string.IsNullOrWhiteSpace(rememberedEquip))
            {
                rememberedMatch = visibleItems.FirstOrDefault(x =>
                    string.Equals((x.Equipment ?? "").Trim(), rememberedEquip, StringComparison.OrdinalIgnoreCase));

                // Было запомнено оборудование, которого уже нет
                // или оно больше не попадает под эту комбинацию фильтров.
                if (rememberedMatch == null)
                    _lastEquipByFilterKey.Remove(key);
            }

            EquipListBoxItem? currentMatch = null;
            if (!string.IsNullOrWhiteSpace(currentEquip))
            {
                currentMatch = visibleItems.FirstOrDefault(x =>
                    string.Equals((x.Equipment ?? "").Trim(), currentEquip, StringComparison.OrdinalIgnoreCase));
            }

            var match = rememberedMatch ?? currentMatch ?? visibleItems[0];
            var selectedEquip = (match.Equipment ?? "").Trim();

            _isApplyingFilterSelection = true;
            _suppressEquipNameFromSelection = true;

            try
            {
                SelectedListBoxEquipment = match;
                EquipmentsView.MoveCurrentTo(match);

                // Синхронизируем строку поиска с фактическим выбранным оборудованием.
                if (!string.Equals((EquipName ?? "").Trim(), selectedEquip, StringComparison.OrdinalIgnoreCase))
                    EquipName = selectedEquip;

                // Запоминаем уже валидный выбор для текущего фильтра.
                RememberEquipmentForCurrentFilters(selectedEquip);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    EquipmentsListBox?.ScrollIntoView(match);
                }), DispatcherPriority.Background);
            }
            finally
            {
                _suppressEquipNameFromSelection = false;
                _isApplyingFilterSelection = false;
            }

            // Если сейчас уже открыта вкладка Param или Info —
            // сразу обновляем соответствующую область.
            if (!_uiState.IsRestoringState)
            {
                if (SelectedMainTab == MainTabKind.Param)
                    StartParamPolling();
                else if (SelectedMainTab == MainTabKind.Info)
                    _ = _infoController.LoadCurrentAsync();
            }
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
            if (_suppressEquipNameFromSelection)
                return;

            // Если фокус в поиске — значит печатаем, не трогаем выбор/вкладки
            if (SearchTextEdit?.IsKeyboardFocusWithin == true)
                return;

            // Во время восстановления состояния — не запускаем автодействия
            if (_uiState.IsRestoringState)
                return;

            // Подставляем выбранное оборудование в строку поиска (EquipName)
            if (SelectedListBoxEquipment?.Equipment is string eq && !string.IsNullOrWhiteSpace(eq))
            {
                EquipName = eq;
                RememberEquipmentForCurrentFilters(eq);

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
            var text = (EquipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(text))
                return;

            var sel = SelectedListBoxEquipment?.Equipment;
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

            var text = (EquipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(text))
                return;

            // если есть выделение — грузим его
            var sel = SelectedListBoxEquipment?.Equipment;
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

        #region Param polling

        private void StartParamPolling()
        {
            _paramController?.Start();
        }

        private void StopParamPolling()
        {
            _paramController?.Stop();
        }

        private (string equipName, string equipType, string equipDescription) ResolveSelectedEquipForParam()
        {
            var selected = SelectedListBoxEquipment;
            if (selected == null)
                return ("", "", "");

            return (selected.Equipment ?? "", selected.Type ?? "", selected.Description ?? "");
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

        #endregion

        #region Refs

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

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

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
            var currentEquip = (EquipName ?? "").Trim();

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

        #endregion

        private void RaiseBottomBarBindings()
        {
            OnPropertyChanged(nameof(IsBottomProgressVisible));
            OnPropertyChanged(nameof(IsBottomLoading));
            OnPropertyChanged(nameof(BottomProgressIsIndeterminate));
            OnPropertyChanged(nameof(BottomProgressMaximum));
            OnPropertyChanged(nameof(BottomProgressValue));

            OnPropertyChanged(nameof(IsBottomStatusVisible));
            OnPropertyChanged(nameof(BottomStatusText));
            OnPropertyChanged(nameof(BottomStatusBrush));

            OnPropertyChanged(nameof(BottomText));
        }

        private void RaiseSelectedTabBindings()
        {
            OnPropertyChanged(nameof(SelectedMainTab));
            OnPropertyChanged(nameof(IsDbTabSelected));
            OnPropertyChanged(nameof(MainActionButtonText));
            OnPropertyChanged(nameof(CanMainAction));
            OnPropertyChanged(nameof(ShowToolbarScanQrButton));
            OnPropertyChanged(nameof(ShowMainActionButton));

            RaiseBottomBarBindings();
        }

        private void SubscribeVmNotifications()
        {
            Vm.Shell.PropertyChanged += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.PropertyName))
                    OnPropertyChanged(e.PropertyName);

                switch (e.PropertyName)
                {
                    case nameof(ShellViewModel.IsLoading):
                        break;

                    case nameof(ShellViewModel.UseParamAreaOverlay):
                    case nameof(ShellViewModel.IsParamCenterLoading):
                    case nameof(ShellViewModel.ParamStatusText):
                    case nameof(ShellViewModel.BottomText):
                    case nameof(ShellViewModel.IsCtApiConnected):
                    case nameof(ShellViewModel.CtApiStatusText):
                        RaiseBottomBarBindings();

                        if (e.PropertyName == nameof(ShellViewModel.ParamStatusText))
                            OnPropertyChanged(nameof(ParamBottomLoadingText));
                        break;
                }
            };

            Vm.EquipmentList.PropertyChanged += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.PropertyName))
                    OnPropertyChanged(e.PropertyName);

                switch (e.PropertyName)
                {
                    case nameof(EquipmentListViewModel.EquipListDone):
                    case nameof(EquipmentListViewModel.EquipListTotal):
                        OnPropertyChanged(nameof(EquipListMax));
                        OnPropertyChanged(nameof(EquipListText));
                        RaiseBottomBarBindings();
                        break;

                    case nameof(EquipmentListViewModel.IsEquipListLoading):
                        OnPropertyChanged(nameof(EquipListText));
                        RaiseBottomBarBindings();
                        break;
                }
            };

            Vm.Param.PropertyChanged += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.PropertyName))
                    OnPropertyChanged(e.PropertyName);
            };

            Vm.Database.PropertyChanged += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.PropertyName))
                    OnPropertyChanged(e.PropertyName);

                switch (e.PropertyName)
                {
                    case nameof(DatabaseViewModel.IsDbLoading):
                    case nameof(DatabaseViewModel.IsDbConnected):
                        RaiseBottomBarBindings();
                        OnPropertyChanged(nameof(CanMainAction));
                        break;
                }
            };

            Vm.PropertyChanged += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.PropertyName))
                    OnPropertyChanged(e.PropertyName);

                switch (e.PropertyName)
                {
                    case nameof(MainViewModel.SelectedMainTab):
                    case nameof(MainViewModel.SelectedMainTabIndex):
                        RaiseSelectedTabBindings();
                        break;
                }
            };
        }
    }
}