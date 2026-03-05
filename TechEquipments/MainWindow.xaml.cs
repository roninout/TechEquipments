using CtApi;
using DevExpress.Xpf.Charts;
using DevExpress.Xpf.Core;
using DevExpress.Xpf.Editors;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using System.IO;
using static TechEquipments.IEquipmentService;

namespace TechEquipments
{
    /// <summary>
    /// Главное окно приложения:
    /// - Левая панель: список оборудования + фильтры Station/Type + посимвольный поиск.
    /// - Правая панель: вкладки SOE / Operation actions / Alarm history.
    /// - Нижняя панель прогресса: используется для загрузки списка оборудования и DB (индетерминантно).
    /// - Overlay: используется для загрузки SOE (тренды).
    /// </summary>
    public partial class MainWindow : ThemedWindow, INotifyPropertyChanged, IParamHost, IDbHost, IQrHost, ISoeHost, IUiStateHost
    {
        private ParamController _paramController;
        private ParamWriteController _paramWriteController;
        private readonly DbController _dbController;
        private readonly QrController _qrController;
        private readonly SoeController _soeController;
        private readonly UiStateController _uiState;

        #region Fields

        #region Services
        private readonly IEquipmentService _equipmentService;
        private readonly ICtApiService _ctApiService;
        private readonly IConfiguration _config;
        private readonly IUserStateService _stateService;

        #endregion

        #region UI Collections (data sources)

        /// <summary>Строки SOE (вкладка SOE).</summary>
        public ObservableCollection<EquipmentSOEDto> equipmentSOEDtos { get; } = new();

        /// <summary>Список оборудования (левая панель).</summary>
        public ObservableCollection<EquipListBoxItem> Equipments { get; } = new();

        /// <summary>Список станций для фильтра (Station).</summary>
        public ObservableCollection<string> Stations { get; } = new();

        /// <summary>Данные вкладки "Operation actions".</summary>
        public ObservableCollection<OperatorActDTO> OperatorActRows { get; } = new();

        /// <summary>Данные вкладки "Alarm history".</summary>
        public ObservableCollection<AlarmHistoryDTO> AlarmHistoryRows { get; } = new();

        /// <summary>Параметры AIParam для вкладки Param (TextBox -> Name)</summary>
        public ObservableCollection<ParamItem> ParamItems { get; } = new();

        #endregion

        #region SOE Loading (overlay) state
        /// <summary>
        /// Флаг: показывать overlay загрузки SOE.
        /// </summary>
        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            private set
            {
                if (_isLoading == value) return;
                _isLoading = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsNotLoading));  // важно для кнопки Load
                OnPropertyChanged(nameof(LoadingText));
            }
        }

        /// <summary>
        /// Используется для включения/отключения кнопки Load.
        /// </summary>
        public bool IsNotLoading => !IsLoading;

        private int _loadedCount;
        public int LoadedCount
        {
            get => _loadedCount;
            private set
            {
                if (_loadedCount == value) return;
                _loadedCount = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LoadingText));
            }
        }

        private int _currentCount;
        public int CurrentCount
        {
            get => _currentCount;
            set
            {
                if (_currentCount == value) return;
                _currentCount = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LoadingText));
            }
        }

        private int _totalTrends;
        public int TotalTrends
        {
            get => _totalTrends;
            set
            {
                if (_totalTrends == value) return;
                _totalTrends = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LoadingText));
            }
        }

        private int _currentTrendIndex;
        public int CurrentTrendIndex
        {
            get => _currentTrendIndex;
            set
            {
                if (_currentTrendIndex == value) return;
                _currentTrendIndex = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LoadingText));
            }
        }

        private string _currentTrendName = "";
        public string CurrentTrendName
        {
            get => _currentTrendName;
            set
            {
                if (_currentTrendName == value) return;
                _currentTrendName = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LoadingText));
            }
        }

        /// <summary>
        /// Лимит точек из одного тренда (ускорение).
        /// </summary>
        public int PerTrendMax => 1000;

        /// <summary>
        /// Общий лимит строк (ускорение).
        /// </summary>
        public int TotalMax => 100;

        /// <summary>
        /// Текст в overlay (Trend оставляем как было).
        /// </summary>
        public string LoadingText => IsLoading ? $"{CurrentTrendIndex}/{TotalTrends}: {CurrentTrendName}" : "";

        #endregion

        #region Left pane: search + filters + selection

        /// <summary>
        /// Текст в поле поиска (правый верх).
        /// При изменении запускается “посимвольный” поиск в ListBox.
        /// </summary>
        private string _equipName = "";
        public string EquipName
        {
            get => _equipName;
            set
            {
                if (_equipName == value) return;
                _equipName = value;
                OnPropertyChanged();

                ScheduleSearch(_equipName);     // твой debounce поиска
                _uiState.ScheduleSave();            // debounce сохранения состояния
                NotifyParamQrUiChanged();       // пересчитать Visibility кнопки Generate QR
            }
        }

        /// <summary>
        /// Выбранный элемент в ListBox.
        /// </summary>
        private EquipListBoxItem? _selectedListBoxEquipment;
        public EquipListBoxItem? SelectedListBoxEquipment
        {
            get => _selectedListBoxEquipment;
            set
            {
                _selectedListBoxEquipment = value;
                OnPropertyChanged();
                NotifyParamQrUiChanged();       // пересчитать Visibility кнопки Generate QR
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

        private EquipTypeGroup _selectedTypeFilter = EquipTypeGroup.All;
        public EquipTypeGroup SelectedTypeFilter
        {
            get => _selectedTypeFilter;
            set
            {
                if (_selectedTypeFilter == value) return;
                _selectedTypeFilter = value;
                OnPropertyChanged();
                ApplyFilters();

                _uiState.ScheduleSave();

                NotifyParamQrUiChanged();       // пересчитать Visibility кнопки Generate QR
            }
        }

        private string _selectedStation = "All";
        public string SelectedStation
        {
            get => _selectedStation;
            set
            {
                value = string.IsNullOrWhiteSpace(value) ? "All" : value.Trim();
                if (string.Equals(_selectedStation, value, StringComparison.OrdinalIgnoreCase)) return;

                _selectedStation = value;
                OnPropertyChanged();
                ApplyFilters();

                _uiState.ScheduleSave();
                NotifyParamQrUiChanged();       // пересчитать Visibility кнопки Generate QR
            }
        }

        // --- incremental search support ---
        private DispatcherTimer _searchTimer = null!;
        private string _pendingSearch = "";
        private bool _suppressEquipNameFromSelection;

        #endregion

        #region Equipments list loading (bottom bar determinate)

        /// <summary>
        /// CTS для отмены загрузки списка оборудования.
        /// </summary>
        private CancellationTokenSource? _equipListCts;

        private int _equipListTotal;
        public int EquipListTotal
        {
            get => _equipListTotal;
            private set
            {
                if (_equipListTotal == value) return;
                _equipListTotal = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(EquipListMax));
                OnPropertyChanged(nameof(EquipListText));

                // прогресс/текст
                OnPropertyChanged(nameof(BottomProgressMaximum));
                OnPropertyChanged(nameof(BottomText));
            }
        }

        private int _equipListDone;
        public int EquipListDone
        {
            get => _equipListDone;
            private set
            {
                if (_equipListDone == value) return;
                _equipListDone = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(EquipListText));

                // прогресс/текст
                OnPropertyChanged(nameof(BottomProgressValue));
                OnPropertyChanged(nameof(BottomText));
            }
        }

        private bool _isEquipListLoading;
        public bool IsEquipListLoading
        {
            get => _isEquipListLoading;
            private set
            {
                if (_isEquipListLoading == value) return;
                _isEquipListLoading = value;
                OnPropertyChanged();

                // нижний прогресс
                OnPropertyChanged(nameof(IsBottomProgressVisible));
                OnPropertyChanged(nameof(IsBottomLoading));
                OnPropertyChanged(nameof(BottomProgressIsIndeterminate));
                OnPropertyChanged(nameof(BottomProgressMaximum));
                OnPropertyChanged(nameof(BottomProgressValue));

                // текст
                OnPropertyChanged(nameof(EquipListText));
                OnPropertyChanged(nameof(BottomText));
            }
        }

        /// <summary>
        /// Максимум для прогрессбара списка оборудования.
        /// </summary>
        public int EquipListMax => Math.Max(1, EquipListTotal);

        /// <summary>
        /// Текст статуса списка оборудования (для нижней панели).
        /// </summary>
        public string EquipListText =>
            IsEquipListLoading
                ? $"Loading equipments: {EquipListDone}/{EquipListTotal}"
                : $"Equipments: {Equipments.Count}";

        #endregion

        #region DB loading (bottom bar indeterminate)

        private bool _isDbConnected;
        public bool IsDbConnected
        {
            get => _isDbConnected;
            private set { _isDbConnected = value; OnPropertyChanged(); }
        }

        private bool _isDbLoading;
        public bool IsDbLoading
        {
            get => _isDbLoading;
            private set
            {
                if (_isDbLoading == value) return;
                _isDbLoading = value;
                OnPropertyChanged();

                // нижний прогресс
                OnPropertyChanged(nameof(IsBottomProgressVisible));
                OnPropertyChanged(nameof(IsBottomLoading));
                OnPropertyChanged(nameof(BottomProgressIsIndeterminate));
                OnPropertyChanged(nameof(BottomProgressMaximum));
                OnPropertyChanged(nameof(BottomProgressValue));

                // кнопка
                OnPropertyChanged(nameof(CanMainAction));
            }
        }

        /// <summary>
        /// Единый флаг видимости нижней панели: EquipList или DB.
        /// </summary>
        public bool IsBottomLoading => IsEquipListLoading || IsDbLoading;

        private string _bottomText = "";

        /// <summary>
        /// Текст внизу:
        /// - при загрузке списка показывает прогресс списка
        /// - при загрузке DB показывает прогресс DB
        /// </summary>
        public string BottomText
        {
            get => IsEquipListLoading ? EquipListText : _bottomText;
            set
            {
                _bottomText = value;
                OnPropertyChanged();
            }
        }

        private int _selectedMainTabIndex;
        public int SelectedMainTabIndex
        {
            get => _selectedMainTabIndex;
            set
            {
                if (_selectedMainTabIndex == value) return;
                _selectedMainTabIndex = value;
                OnPropertyChanged();

                // уведомляем всё, что зависит от выбранной вкладки
                OnPropertyChanged(nameof(SelectedMainTab));
                OnPropertyChanged(nameof(IsDbTabSelected));
                OnPropertyChanged(nameof(MainActionButtonText));
                OnPropertyChanged(nameof(CanMainAction));
                OnPropertyChanged(nameof(IsBottomLoading));

                // ВАЖНО: во время восстановления состояния никаких автодействий
                if (_uiState.IsRestoringState) return;                

                _dbController.CancelCurrentLoad(); // отменяем предыдущую DB-загрузку при смене вкладки

                _uiState.ScheduleSave(); // сохраняем состояние (debounce)

                // при переходе на DB-вкладки — делаем "как будто нажали Search"
                //if (IsDbTabSelected)
                _ = OnTabActivatedLikeSearchAsync(force: true);              

            }
        }

        private DateTime _dbDate = DateTime.Today;
        public DateTime DbDate
        {
            get => _dbDate;
            set
            {
                if (_dbDate.Date == value.Date) return;
                _dbDate = value.Date;
                OnPropertyChanged();

                // Если мы на DB вкладке и есть коннект — планируем авто-загрузку (debounce)
                _dbController.ScheduleReload();

                _uiState.ScheduleSave();
            }
        }

        /// <summary>Текущая вкладка как enum (задел на будущие вкладки)</summary>
        public MainTabKind SelectedMainTab => (MainTabKind)SelectedMainTabIndex;

        /// <summary>Показывать DateEdit только на DB вкладках</summary>
        public bool IsDbTabSelected => SelectedMainTab is MainTabKind.OperationActions or MainTabKind.AlarmHistory;

        /// <summary>Текст основной кнопки (одна на все режимы)</summary>
        public string MainActionButtonText => SelectedMainTab switch
        {
            MainTabKind.SOE => "Load",
            MainTabKind.OperationActions => "Search",
            MainTabKind.AlarmHistory => "Search",
            _ => "Run",
        };

        /// <summary>Можно ли нажимать основную кнопку</summary>
        public bool CanMainAction => SelectedMainTab switch
        {
            MainTabKind.SOE => IsNotLoading,
            _ => IsDbConnected && !IsDbLoading,
        };

        /// <summary>Показываем нижний прогресс только когда что-то грузим</summary>
        public bool IsBottomProgressVisible => IsEquipListLoading || IsDbLoading;

        /// <summary>
        /// Режим нижнего прогресса:
        /// - список оборудования: детерминированный
        /// - DB: индетерминированный
        /// </summary>
        public bool BottomProgressIsIndeterminate => IsDbLoading && !IsEquipListLoading;

        /// <summary>Максимум для нижнего прогресса</summary>
        public int BottomProgressMaximum => IsEquipListLoading ? EquipListMax : 100;

        /// <summary>Текущее значение для нижнего прогресса</summary>
        public int BottomProgressValue => IsEquipListLoading ? EquipListDone : 0;

        #endregion

        #region Left pane toggle state

        private bool _layoutReady;
        private GridLength _leftSavedWidth = new(260);

        #endregion

        #region Params

        // Строка состояния на вкладке Param
        private string _paramStatusText = "";

        public string ParamStatusText
        {
            get => _paramStatusText;
            set { _paramStatusText = value; OnPropertyChanged(); }
        }

        // Текущая модель параметров (AIParam / DIParam / MotorParam / ...)
        private object _currentParamModel;
        public object CurrentParamModel
        {
            get => _currentParamModel;
            set
            {
                _currentParamModel = value;
                OnPropertyChanged();

                // ✅ Обновляем вычисляемые свойства для шапки ParamTabHost
                OnPropertyChanged(nameof(CurrentParamChanel));
                OnPropertyChanged(nameof(IsCurrentParamChanelVisible));
            }
        }

        // polling
        private int _paramReadCycles;

        // 1) Общий “замок” на чтение/запись Param (чтение и запись не пересекаются)
        private readonly SemaphoreSlim _paramRwGate = new(1, 1);

        // 2) Флаг: когда мы обновляем модель из polling-чтения — запрещаем триггерить запись из EditValueChanged
        private bool _suppressParamWritesFromPolling;

        // 3) Небольшая “пауза” чтения после записи (чтобы не словить мгновенный старый read)
        private DateTime _paramReadResumeAtUtc = DateTime.MinValue;

        // Чтобы при откате чекбокса назад не улетал повторный write
        private bool _suppressParamWritesFromUiRollback;

        /// <summary>
        /// Chanel из модели, если модель поддерживает IHasChanel.
        /// Если не поддерживает — пустая строка.
        /// </summary>
        //public string CurrentParamChanel => (CurrentParamModel as IHasChanel)?.Chanel ?? "";
        public string CurrentParamChanel => FormatChanelForHeader((CurrentParamModel as IHasChanel)?.Chanel);

        /// <summary>
        /// Показывать строку Chanel только если:
        /// - модель поддерживает IHasChanel
        /// - значение не пустое
        /// - значение не "Unknown"
        /// </summary>
        public bool IsCurrentParamChanelVisible
        {
            get
            {
                var ch = (CurrentParamModel as IHasChanel)?.Chanel;
                if (string.IsNullOrWhiteSpace(ch))
                    return false;

                return !ch.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
            }
        }

        // ===== Param editing (anti-overwrite during typing) =====

        // 0/1 флаг (Interlocked/Volatile, чтобы безопасно читать из background polling)
        private int _isEditingField;

        // Быстрая проверка из polling
        private bool IsEditingField => System.Threading.Volatile.Read(ref _isEditingField) == 1;

        #endregion

        #region Trend

        public ParamTrendVm Trend { get; }

        private ParamTrendController _trendCtl;

        #endregion

        #region VGD ref

        // ====== VGD DI/DO refs (dynamic UI) ======

        /// <summary>
        /// Строки для VGD -> DI/DO связей (отображаются в VGDParamView/DiDoSettingsGroup).
        /// </summary>
        public ObservableCollection<DiDoRefRow> ParamDiRows { get; } = new();
        public ObservableCollection<DiDoRefRow> ParamDoRows { get; } = new();
        public ObservableCollection<PlcRefRow> ParamPlcRows { get; } = new();

        private ParamSettingsPage _currentParamSettingsPage = ParamSettingsPage.None;
        public ParamSettingsPage CurrentParamSettingsPage
        {
            get => _currentParamSettingsPage;
            private set { _currentParamSettingsPage = value; OnPropertyChanged(); }
        }

        // Последняя группа параметров, которую показывали на вкладке Param
        private EquipTypeGroup _lastParamTypeGroup = EquipTypeGroup.All;
        private bool _hasLastParamTypeGroup;

        #endregion

        #endregion

        public MainWindow(IEquipmentService equipmentService, IDbService dbService, IUserStateService stateService, ICtApiService ctApiService, IConfiguration config, IQrCodeService qrCodeService, IQrScannerService qrScannerService)
        {
            InitializeComponent();

            _equipmentService = equipmentService;
            _stateService = stateService;
            _ctApiService = ctApiService;
            _config = config;
            _dbController = new DbController(dbService, this);
            _qrController = new QrController(_equipmentService, qrCodeService, qrScannerService, this);
            _soeController = new SoeController(_equipmentService, this);
            _uiState = new UiStateController(_stateService, _equipmentService, this);

            // Vm + Controller
            Trend = new ParamTrendVm();
            Trend.AutoLive = _config.GetValue("Trend:AutoLive", true);

            _trendCtl = new ParamTrendController(
                Trend,
                Dispatcher,
                _equipmentService,
                _ctApiService,
                resolveEquip: ResolveSelectedEquipForParam,        // твой существующий метод
                getParamModel: () => CurrentParamModel,            // твоя текущая модель параметров
                getParamCycles: () => _paramReadCycles             // счетчик циклов
            );

            _paramController = new ParamController(_equipmentService, this);

            _paramWriteController = new ParamWriteController(
                equipmentService: _equipmentService,
                getSelectedTab: () => SelectedMainTab,
                resolveSelectedEquip: ResolveSelectedEquipForParam,
                getSuppressWritesFromPolling: () => _suppressParamWritesFromPolling,
                getSuppressWritesFromUiRollback: () => _suppressParamWritesFromUiRollback,
                setSuppressWritesFromUiRollback: v => _suppressParamWritesFromUiRollback = v,
                paramRwGate: _paramRwGate,
                setParamReadResumeAtUtc: dt => _paramReadResumeAtUtc = dt,
                setBottomText: txt => ParamStatusText = txt,
                getOwnerWindow: () => this,
                endParamFieldEdit: EndParamFieldEdit
            );

            DataContext = this; // DataContext на себя: используется во всём XAML (binding)

            InitEquipmentsView();
            InitSearchTimer();

            Loaded += async (_, __) =>
            {
                _layoutReady = true;
                InitLeftPaneState();

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
                IsEquipListLoading = true;

                EquipListDone = 0;
                EquipListTotal = 0;

                BottomText = "Loading equipments...";

                await Dispatcher.Yield(DispatcherPriority.Background);

                var progress = new Progress<(int done, int total)>(p =>
                {
                    EquipListDone = p.done;
                    EquipListTotal = p.total;
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

                ApplyFilters();

                // Если на старте использовали ExternalTag — выставляем Station/TypeGroup по найденному оборудованию
                if (_uiState.StartupUsedExternalTag && !string.IsNullOrWhiteSpace(_uiState.StartupExternalTag))
                {
                    _qrController.TryApplyStationTypeFiltersFromQr(_uiState.StartupExternalTag);

                    // После смены фильтров — снова выделим оборудование
                    if (!string.IsNullOrWhiteSpace(EquipName))
                        DoIncrementalSearch(EquipName);
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
                IsEquipListLoading = false;

                // Даём UI шанс перерисовать нижнюю панель (скрыть/показать)
                await Dispatcher.Yield(DispatcherPriority.Render);

                // Если внешний тег уже заполнил EquipName - выделяем в ListBox
                if (!string.IsNullOrWhiteSpace(EquipName))
                    DoIncrementalSearch(EquipName);
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
            if (obj is not EquipListBoxItem it) return false;

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

            return EquipTypeRegistry.GetGroup(it.Type) == SelectedTypeFilter;
        }

        /// <summary>Применяет фильтры (перерисовка представления).</summary>
        private void ApplyFilters()
        {
            EquipmentsView.Refresh();
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
            // стопаем Param чтение, если уходим
            if ((MainTabKind)SelectedMainTabIndex != MainTabKind.Param)
                StopParamPolling();

            switch ((MainTabKind)SelectedMainTabIndex)
            {
                case MainTabKind.Param:
                    StartParamPolling();
                    break;

                case MainTabKind.OperationActions:
                case MainTabKind.AlarmHistory:
                    await _dbController.LoadCurrentTabAsync(force);
                    break;

                case MainTabKind.SOE:
                    // Обычно SOE не надо автoload при каждом клике по вкладке
                    // но если хочешь — можно включить:
                    await LoadSoeFromUiAsync();
                    break;

                default:
                    break;
            }
        }

        #endregion

        #region ListBox

        /// <summary>Клик по списку: подставляет оборудование в поле поиска (если сейчас не печатаем).</summary>
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
                EquipName = eq;

            // Переключаемся на Param
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
            if (IsLoading) return;

            var text = (EquipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(text))
                return;

            // если есть выделение — грузим его
            var sel = SelectedListBoxEquipment?.Equipment;
            if (!string.IsNullOrWhiteSpace(sel))
                text = sel;

            await _soeController.LoadAndShowAsync(text);
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

        private (string equipName, string equipType) ResolveSelectedEquipForParam()
        {
            // Приоритет: выбранный элемент списка
            if (SelectedListBoxEquipment != null && !string.IsNullOrWhiteSpace(SelectedListBoxEquipment.Equipment))
            {
                return (SelectedListBoxEquipment.Equipment.Trim(), (SelectedListBoxEquipment.Type ?? "").Trim());
            }

            // Фолбэк: то, что в строке поиска
            return ((EquipName ?? "").Trim(), "");
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
        public async void ParamEditable_WriteFromUi(string? equipItem, object? newValue)
        {
            if (_paramWriteController == null)
                return;

            await _paramWriteController.WriteFromUiAsync(equipItem, newValue);
        }

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
            => TrendSeriesStyler.Apply(chart, CurrentParamModel);
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

        #region IDbHost

        Dispatcher IDbHost.Dispatcher => Dispatcher;

        bool IDbHost.IsDbConnected => IsDbConnected;
        void IDbHost.SetDbConnected(bool value) => IsDbConnected = value;

        bool IDbHost.IsDbLoading => IsDbLoading;
        void IDbHost.SetDbLoading(bool value) => IsDbLoading = value;

        string IDbHost.BottomText
        {
            get => BottomText;
            set => BottomText = value;
        }

        MainTabKind IDbHost.SelectedMainTab => SelectedMainTab;

        bool IDbHost.IsDbTabSelected => IsDbTabSelected;

        DateTime IDbHost.DbDate => DbDate;

        string IDbHost.DbFilter => (EquipName ?? "").Trim();

        System.Collections.ObjectModel.ObservableCollection<OperatorActDTO> IDbHost.OperatorActRows => OperatorActRows;
        System.Collections.ObjectModel.ObservableCollection<AlarmHistoryDTO> IDbHost.AlarmHistoryRows => AlarmHistoryRows;

        #endregion

        #region IQrHost

        System.Windows.Window IQrHost.OwnerWindow => this;

        System.Collections.ObjectModel.ObservableCollection<EquipListBoxItem> IQrHost.Equipments => Equipments;

        EquipListBoxItem? IQrHost.SelectedListBoxEquipment => SelectedListBoxEquipment;

        string IQrHost.EquipName
        {
            get => EquipName;
            set => EquipName = value;
        }

        string IQrHost.SelectedStation
        {
            get => SelectedStation;
            set => SelectedStation = value;
        }

        EquipTypeGroup IQrHost.SelectedTypeFilter
        {
            get => SelectedTypeFilter;
            set => SelectedTypeFilter = value;
        }

        MainTabKind IQrHost.SelectedMainTab => SelectedMainTab;

        int IQrHost.SelectedMainTabIndex
        {
            get => SelectedMainTabIndex;
            set => SelectedMainTabIndex = value;
        }

        void IQrHost.DoIncrementalSearch(string text) => DoIncrementalSearch(text);

        void IQrHost.StartParamPolling() => StartParamPolling();

        void IQrHost.NotifyParamQrUiChanged() => NotifyParamQrUiChanged();

        void IQrHost.SetParamStatusText(string text) => ParamStatusText = text;

        #endregion

        #region IParamHost

        // ===== IParamHost implementation =====

        Dispatcher IParamHost.Dispatcher => Dispatcher;

        MainTabKind IParamHost.SelectedMainTab => SelectedMainTab;

        bool IParamHost.TrendIsChartVisible => Trend.IsChartVisible;

        bool IParamHost.IsEditingField => IsEditingField;

        SemaphoreSlim IParamHost.ParamRwGate => _paramRwGate;

        DateTime IParamHost.ParamReadResumeAtUtc
        {
            get => _paramReadResumeAtUtc;
            set => _paramReadResumeAtUtc = value;
        }

        bool IParamHost.SuppressParamWritesFromPolling
        {
            get => _suppressParamWritesFromPolling;
            set => _suppressParamWritesFromPolling = value;
        }

        int IParamHost.ParamReadCycles
        {
            get => _paramReadCycles;
            set => _paramReadCycles = value;
        }

        string IParamHost.ParamStatusText
        {
            get => ParamStatusText;
            set => ParamStatusText = value;
        }

        string IParamHost.BottomText
        {
            get => BottomText;
            set => BottomText = value;
        }

        object IParamHost.CurrentParamModel
        {
            get => CurrentParamModel;
            set => CurrentParamModel = value;
        }

        ObservableCollection<ParamItem> IParamHost.ParamItems => ParamItems;

        (string equipName, string equipType) IParamHost.ResolveSelectedEquipForParam()
            => ResolveSelectedEquipForParam();

        void IParamHost.Param_ResetAreaIfTypeGroupChanged(EquipTypeGroup newGroup)
            => Param_ResetAreaIfTypeGroupChanged(newGroup);

        Task IParamHost.RefreshActiveParamSectionAsync(CancellationToken ct)
            => RefreshActiveParamSectionAsync(ct);

        Task IParamHost.PollTrendOnceSafeAsync(CancellationToken ct)
            => _trendCtl.PollOnceSafeAsync(ct, txt => BottomText = txt);

        #endregion

        #region ISoeHost
        Window ISoeHost.OwnerWindow => this;
        Dispatcher ISoeHost.Dispatcher => Dispatcher;

        bool ISoeHost.IsLoading { get => IsLoading; set => IsLoading = value; }
        int ISoeHost.LoadedCount { get => LoadedCount; set => LoadedCount = value; }
        int ISoeHost.CurrentCount { get => CurrentCount; set => CurrentCount = value; }
        int ISoeHost.TotalTrends { get => TotalTrends; set => TotalTrends = value; }
        int ISoeHost.CurrentTrendIndex { get => CurrentTrendIndex; set => CurrentTrendIndex = value; }
        string ISoeHost.CurrentTrendName { get => CurrentTrendName; set => CurrentTrendName = value; }

        int ISoeHost.PerTrendMax => PerTrendMax;
        int ISoeHost.TotalMax => TotalMax;

        System.Collections.ObjectModel.ObservableCollection<EquipmentSOEDto> ISoeHost.EquipmentSoeRows => equipmentSOEDtos;
        #endregion

        #region IUiStateHost

        Dispatcher IUiStateHost.Dispatcher => Dispatcher;

        string IUiStateHost.EquipName { get => EquipName; set => EquipName = value; }
        DateTime IUiStateHost.DbDate { get => DbDate; set => DbDate = value; }
        string IUiStateHost.SelectedStation { get => SelectedStation; set => SelectedStation = value; }
        EquipTypeGroup IUiStateHost.SelectedTypeFilter { get => SelectedTypeFilter; set => SelectedTypeFilter = value; }
        int IUiStateHost.SelectedMainTabIndex { get => SelectedMainTabIndex; set => SelectedMainTabIndex = value; }

        #endregion

        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        #endregion

        #region Helpers

        /// <summary>
        /// Приводит строку канала к формату: "module: X, chanel: Y".
        /// Ожидаемый исходный формат: "X.Y.Z" или "X.Y".
        /// - Берём только первые два сегмента (X и Y).
        /// - Остальные сегменты отбрасываем.
        /// Если формат неожиданный — возвращаем исходную строку (trim).
        /// </summary>
        private static string FormatChanelForHeader(string? raw)
        {
            raw = (raw ?? "").Trim();
            if (string.IsNullOrWhiteSpace(raw))
                return "";

            if (raw.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                return raw;

            // Разделяем по точке
            var parts = raw.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2)
            {
                var module = parts[0];
                var chanel = parts[1];
                return $"module: {module}, chanel: {chanel}";
            }

            // Если точек нет/меньше двух — оставляем как есть
            return raw;
        }

        #endregion

        #region VGD refs

        /// <summary>
        /// Вызывается из VGDParamView при нажатии PLC/DI_DO/Alarm/Chart.
        /// </summary>
        public void SetParamSettingsPage(ParamSettingsPage page)
        {
            CurrentParamSettingsPage = page;
        }

        /// <summary>
        /// Вызывается из StartParamPolling каждые 5 секунд.
        /// Обновляет только ту секцию Settings, которая сейчас активна.
        /// </summary>
        private async Task RefreshActiveParamSectionAsync(CancellationToken ct)
        {
            // только на вкладке Param
            if (SelectedMainTab != MainTabKind.Param)
                return;

            // если пользователь смотрит Chart — ничего не обновляем
            if (CurrentParamSettingsPage == ParamSettingsPage.None)
                return;

            // список оборудования должен быть загружен
            if (Equipments.Count == 0)
                return;

            // пока нас интересует только VGD (DI/DO refs)
            //if (CurrentParamModel is not VGDParam)
            //    return;

            switch (CurrentParamSettingsPage)
            {
                case ParamSettingsPage.DiDo:
                    if (Equipments.Count == 0)
                        return;

                    await RefreshDiDoSectionAsync(ct);
                    break;

                case ParamSettingsPage.Plc:
                    await RefreshPlcSectionAsync(ct);
                    break;

                default:
                    break;
            }
        }

        /// <summary>
        /// Обновляет DI/DO секции для VGD:
        /// - читает EquipRef(category="TabDIDO")
        /// - находит DI/DO в общем Equipments
        /// - перечитывает DIParam/DOParam (Value может меняться)
        /// - синхронизирует ObservableCollection без мигания (update in-place)
        /// </summary>
        private async Task RefreshDiDoSectionAsync(CancellationToken ct)
        {
            var (equipName, _) = ResolveSelectedEquipForParam();
            equipName = (equipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(equipName))
                return;

            // сериализуем с Param чтением/записью (CtApi не любит параллельность на одном соединении)
            await _paramRwGate.WaitAsync(ct);
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
                    await Dispatcher.InvokeAsync(() =>
                    {
                        ParamDiRows.Clear();
                        ParamDoRows.Clear();
                    });
                    return;
                }

                // 2) разложим refs на DI и DO по EquipTypeGroup
                var diEquip = new List<EquipListBoxItem>();
                var doEquip = new List<EquipListBoxItem>();

                foreach (var refName in refNames)
                {
                    ct.ThrowIfCancellationRequested();

                    // Быстрый поиск в списке (если у тебя нет _equipIndex — можно оставить FirstOrDefault)
                    var equip = Equipments.FirstOrDefault(x =>
                        string.Equals((x.Equipment ?? "").Trim(), refName, StringComparison.OrdinalIgnoreCase));

                    if (equip == null)
                        continue;

                    var grp = EquipTypeRegistry.GetGroup(equip.Type ?? "");

                    if (grp == EquipTypeGroup.DI)
                        diEquip.Add(equip);
                    else if (grp == EquipTypeGroup.DO)
                        doEquip.Add(equip);
                }

                // helper: параллельное выполнение задач с лимитом
                async Task<List<TResult>> RunLimitedAsync<TItem, TResult>(
                    List<TItem> items,
                    int maxConcurrency,
                    Func<TItem, Task<TResult>> work,
                    CancellationToken token)
                {
                    var results = new System.Collections.Concurrent.ConcurrentBag<TResult>();
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

                // nulls убрать
                var diNew = diRows.Where(x => x != null).ToDictionary(x => x!.EquipName, StringComparer.OrdinalIgnoreCase);
                var doNew = doRows.Where(x => x != null).ToDictionary(x => x!.EquipName, StringComparer.OrdinalIgnoreCase);

                // 4) sync collections on UI thread
                await Dispatcher.InvokeAsync(() =>
                {
                    SyncRows(ParamDiRows, diNew);
                    SortRowsByChanel(ParamDiRows);

                    SyncRows(ParamDoRows, doNew);
                    SortRowsByChanel(ParamDoRows);
                });
            }
            finally
            {
                _paramRwGate.Release();
            }
        }

        /// <summary>
        /// Синхронизация ObservableCollection без полного Clear/Add (меньше мигания):
        /// - удаляем отсутствующие
        /// - обновляем существующие (Update)
        /// - добавляем новые
        /// </summary>
        private static void SyncRows(ObservableCollection<DiDoRefRow> target, Dictionary<string, DiDoRefRow> newMap)
        {
            // remove missing
            for (int i = target.Count - 1; i >= 0; i--)
            {
                var key = target[i].EquipName;
                if (!newMap.ContainsKey(key))
                    target.RemoveAt(i);
            }

            // update existing + mark
            var existing = target.ToDictionary(x => x.EquipName, StringComparer.OrdinalIgnoreCase);

            foreach (var kv in newMap)
            {
                if (existing.TryGetValue(kv.Key, out var row))
                {
                    // update values/model
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

            // Разбираем "A.B.C" (или "A.B"). Если вдруг формат другой - будет в конец.
            var parts = raw.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            static int ParsePart(string? s)
                => int.TryParse(s, out var v) ? v : int.MaxValue;

            var a = parts.Length > 0 ? ParsePart(parts[0]) : int.MaxValue;
            var b = parts.Length > 1 ? ParsePart(parts[1]) : int.MaxValue;
            var c = parts.Length > 2 ? ParsePart(parts[2]) : 0;

            return (long)a * 1_000_000L + (long)b * 1_000L + (long)c;
        }

        /// <summary>
        /// Переупорядочивает ObservableCollection в нужном порядке (через Move),
        /// чтобы UI не мигал и не терял выделение.
        /// </summary>
        private static void SortRowsByChanel(ObservableCollection<DiDoRefRow> rows)
        {
            if (rows == null || rows.Count <= 1)
                return;

            var sorted = rows
                .OrderBy(GetChanelSortKey)
                .ThenBy(r => r.EquipName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Переставляем существующие объекты через Move (минимально инвазивно для UI)
            for (int targetIndex = 0; targetIndex < sorted.Count; targetIndex++)
            {
                var item = sorted[targetIndex];
                var currentIndex = rows.IndexOf(item);
                if (currentIndex >= 0 && currentIndex != targetIndex)
                    rows.Move(currentIndex, targetIndex);
            }
        }


        private async Task RefreshPlcSectionAsync(CancellationToken ct)
        {
            // PLC refs сейчас нужны только для VGD
            //if (CurrentParamModel is not VGDParam)
            //    return;

            var (equipName, _) = ResolveSelectedEquipForParam();
            equipName = (equipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(equipName))
                return;

            const string category = "TabPLC";
            const string clusterEquipItem = "State";

            // локальный helper: параллельный TagRead с лимитом
            async Task<Dictionary<string, string?>> TagReadManyAsync(List<string> tags, int maxConcurrency, CancellationToken token)
            {
                var result = new System.Collections.Concurrent.ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
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

            await _paramRwGate.WaitAsync(ct);
            try
            {
                // 1) refs
                var fresh = await _equipmentService.GetEquipRef(equipName, category, clusterEquipItem, "CUSTOM1")
                           ?? new List<PlcRefRow>();

                // 2) sync списка (чтобы не пересоздавать)
                await Dispatcher.InvokeAsync(() => SyncPlcRows(ParamPlcRows, fresh));

                // 3) snapshot
                var snapshot = await Dispatcher.InvokeAsync(() => ParamPlcRows.ToList());

                // 4) I/O: TagInfo (только при пустых кешах) -> TagRead пакетно
                var meta = new List<(PlcRefRow row, string tagName, string unit, string forcedTag)>(snapshot.Count);
                var tagsToRead = new List<string>(snapshot.Count * 2);

                foreach (var row in snapshot)
                {
                    ct.ThrowIfCancellationRequested();

                    // --- resolve TagName (кэш в row.TagName) ---
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

                    // --- resolve Unit (кэш в row.Unit) ---
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

                    // --- resolve ForcedTagName (кэш в row.ForcedTagName) ---
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

                    // сохраняем мета для UI (в UI-thread применим кеши)
                    meta.Add((row, tagName, unit, forcedTag));

                    // собираем теги на чтение (только валидные)
                    if (!string.IsNullOrWhiteSpace(tagName) && !tagName.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                        tagsToRead.Add(tagName);

                    if (!string.IsNullOrWhiteSpace(forcedTag) && !forcedTag.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                        tagsToRead.Add(forcedTag);
                }

                // ничего читать
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

                // 6) apply UI (одним заходом)
                await Dispatcher.InvokeAsync(() =>
                {
                    foreach (var u in updates)
                    {
                        // кеши мета
                        if (!string.IsNullOrWhiteSpace(u.tagName))
                            u.row.TagName = u.tagName;

                        if (!string.IsNullOrWhiteSpace(u.unit) && !u.unit.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                            u.row.Unit = u.unit;

                        if (!string.IsNullOrWhiteSpace(u.forcedTag) && !u.forcedTag.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                            u.row.ForcedTagName = u.forcedTag;

                        // значение
                        u.row.UpdateValue(u.value);

                        // forced
                        if (u.forced.HasValue)
                            u.row.ValueForced = u.forced.Value;
                        else if (u.row.Type is PlcTypeCustom.EqDigital or PlcTypeCustom.EqDigitalInOut)
                            u.row.ValueForced = false;
                    }
                });
            }
            finally
            {
                _paramRwGate.Release();
            }
        }

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
        /// Синхронизация PLC rows без затирания кэша TagName/Value.
        /// - удаляем отсутствующие
        /// - обновляем мета-данные у существующих
        /// - добавляем новые
        /// </summary>
        private static void SyncPlcRows(ObservableCollection<PlcRefRow> target, List<PlcRefRow> fresh)
        {
            // key = EquipName (то, что приходит из REFEQUIP)
            var freshMap = fresh
                .Where(x => !string.IsNullOrWhiteSpace(x.EquipName))
                .ToDictionary(x => x.EquipName, StringComparer.OrdinalIgnoreCase);

            // remove missing
            for (int i = target.Count - 1; i >= 0; i--)
            {
                if (!freshMap.ContainsKey(target[i].EquipName))
                    target.RemoveAt(i);
            }

            // update existing + add new
            var existing = target.ToDictionary(x => x.EquipName, StringComparer.OrdinalIgnoreCase);

            foreach (var kv in freshMap)
            {
                var freshRow = kv.Value;

                if (existing.TryGetValue(kv.Key, out var row))
                {
                    // обновляем только мета (Type/Comment/Title)
                    // (подстрой под твой PlcRefRow: если UpdateMeta принимает другие аргументы — поменяй)
                    row.UpdateMeta(freshRow.Type, freshRow.Comment);

                    // TagName НЕ затираем пустым (кэш)
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
        /// Переход по клику из DI/DO списка:
        /// - гарантируем видимость в ListBox (если фильтры прячут — подстроим)
        /// - подставим EquipName
        /// - выделим в ListBox
        /// - откроем вкладку Param
        /// </summary>
        public void Param_NavigateToLinkedEquip(DiDoRefRow? row)
        {
            if (row == null)
                return;

            var it = row.EquipItem;
            if (it == null)
                return;

            var targetName = (it.Equipment ?? "").Trim();
            if (string.IsNullOrWhiteSpace(targetName))
                return;

            // 1) Если текущие фильтры прячут элемент — подстраиваем фильтры так, чтобы он стал виден
            EnsureEquipmentVisibleInList(it);

            // 2) Подставляем текст поиска (это же ключ для DoIncrementalSearch)

            // если прыгаем на оборудование другого типа — показываем Chart по умолчанию сразу (без ожидания 5 сек)
            var newGroup = EquipTypeRegistry.GetGroup(row.EquipItem?.Type ?? "");
            Param_ResetAreaIfTypeGroupChanged(newGroup);

            EquipName = targetName;

            // 3) Выделяем в ListBox
            DoIncrementalSearch(targetName);

            // 4) Открываем вкладку Param (DI/DO экран появится автоматически по типу)
            if (SelectedMainTab != MainTabKind.Param)
            {
                SelectedMainTabIndex = (int)MainTabKind.Param;
                return;
            }

            // Если уже на Param — просто обновим polling
            StartParamPolling();
        }

        /// <summary>
        /// Переход к оборудованию по имени (используется из PLC settings / ссылок).
        /// НЕ зависит от QR-контроллера.
        /// </summary>
        public void Param_NavigateToLinkedEquip(string? equipName)
        {
            var key = (equipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(key))
                return;

            // 1) Пытаемся найти оборудование в полном списке (чтобы корректно подстроить Station/Type фильтры)
            var it =
                Equipments.FirstOrDefault(x => string.Equals(x.Equipment, key, StringComparison.OrdinalIgnoreCase)) ??
                Equipments.FirstOrDefault(x => string.Equals(x.Tag, key, StringComparison.OrdinalIgnoreCase));

            if (it != null)
            {
                // Если текущие фильтры скрывают элемент — подстроим фильтры так, чтобы он стал видим
                EnsureEquipmentVisibleInList(it);

                // Если прыгаем на оборудование другой группы — сбросим область Param (как у тебя уже сделано)
                var newGroup = EquipTypeRegistry.GetGroup(it.Type ?? "");
                Param_ResetAreaIfTypeGroupChanged(newGroup);

                // Нормализуем имя на реальное Equipment (а не Tag)
                key = (it.Equipment ?? key).Trim();
            }

            // 2) Выставляем оборудование
            EquipName = key;

            // 3) Выделяем слева
            DoIncrementalSearch(key);

            // 4) Уводим на вкладку Param
            if (SelectedMainTab != MainTabKind.Param)
            {
                SelectedMainTabIndex = (int)MainTabKind.Param;
                return;
            }

            // 5) Если уже на Param — обновим polling (или твой новый механизм)
            StartParamPolling();
        }

        /// <summary>
        /// Если элемент скрыт фильтрами Station/Type — меняем фильтры так, чтобы элемент был видим.
        /// </summary>
        private void EnsureEquipmentVisibleInList(EquipListBoxItem it)
        {
            try
            {
                // если уже видим — ничего не делаем
                if (FilterEquipment(it))
                    return;

                // Station
                if (!string.IsNullOrWhiteSpace(it.Station))
                    SelectedStation = it.Station.Trim();
                else
                    SelectedStation = "All";

                // TypeGroup (DI/DO)
                var grp = EquipTypeRegistry.GetGroup(it.Type ?? "");
                SelectedTypeFilter = grp != EquipTypeGroup.All ? grp : EquipTypeGroup.All;

                ApplyFilters();
            }
            catch
            {
                // best-effort: даже если что-то пошло не так, просто не ломаем навигацию
            }
        }

        /// <summary>
        /// Если группа оборудования изменилась (например VGD -> DI), сбрасываем UI Param на дефолт:
        /// - показываем Chart
        /// - сбрасываем активную секцию настроек (PLC/DI_DO/Alarm) = None
        /// Это устраняет баг "открылась не та область" и "после возврата на VGD нет активной области".
        /// </summary>
        private void Param_ResetAreaIfTypeGroupChanged(EquipTypeGroup newGroup)
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

            // 1) Сбрасываем выбранную секцию Settings (для VGD-кнопок)
            SetParamSettingsPage(ParamSettingsPage.None);

            // 2) Показываем Chart по умолчанию
            ShowParamChart(reset: false);

            // 3) (опционально) очистить DI/DO списки, чтобы не светились чужие данные
            ParamDiRows.Clear();
            ParamDoRows.Clear();
            ParamPlcRows.Clear();
        }

        /// <summary>
        /// Для PLC-строк по умолчанию используем ".Value".
        /// Для статусов (Motor/Valve) вместо Value используем ".State".
        /// </summary>
        private static string GetPlcEquipItemForTagInfo(PlcRefRow row)
        {
            if (row.Type is PlcTypeCustom.EqMotorStatus or PlcTypeCustom.EqValveStatus)
                return "State";

            return "Value";
        }

        #endregion
        
    }
}