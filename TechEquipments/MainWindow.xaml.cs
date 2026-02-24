using CtApi;
using DevExpress.Xpf.Charts;
using DevExpress.Xpf.Core;
using DevExpress.Xpf.Editors;
using DevExpress.XtraRichEdit.Import.Html;
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
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using TechEquipments.Services.QR;
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
    public partial class MainWindow : ThemedWindow, INotifyPropertyChanged
    {
        #region Services

        /// <summary>
        /// Сервис для получения списка оборудования и SOE (CtApi / тренды).
        /// </summary>
        private readonly IEquipmentService _equipmentService;

        /// <summary>
        /// Сервис для доступа к PostgreSQL (Operation actions / Alarm history).
        /// </summary>
        private readonly IDbService _dbService;

        private readonly ICtApiService _ctApiService;

        #endregion

        #region UI Collections (data sources)

        /// <summary>
        /// Строки SOE (вкладка SOE).
        /// </summary>
        public ObservableCollection<EquipmentSOEDto> equipmentSOEDtos { get; } = new();

        /// <summary>
        /// Список оборудования (левая панель).
        /// </summary>
        public ObservableCollection<EquipListBoxItem> Equipments { get; } = new();

        /// <summary>
        /// Список станций для фильтра (Station).
        /// </summary>
        public ObservableCollection<string> Stations { get; } = new();

        /// <summary>
        /// Данные вкладки "Operation actions".
        /// </summary>
        public ObservableCollection<OperatorActDTO> OperatorActRows { get; } = new();

        /// <summary>
        /// Данные вкладки "Alarm history".
        /// </summary>
        public ObservableCollection<AlarmHistoryDTO> AlarmHistoryRows { get; } = new();

        /// <summary>Параметры AIParam для вкладки Param (TextBox -> Name)</summary>
        public ObservableCollection<ParamItem> ParamItems { get; } = new();

        /// <summary>
        /// TrendPoint для вкладки Param
        /// </summary>
        public ObservableCollection<TrendPoint> ParamTrendPoints { get; } = new();

        #endregion

        #region SOE Loading (overlay) state

        /// <summary>
        /// Семафор: не допускаем параллельные загрузки SOE.
        /// </summary>
        private readonly SemaphoreSlim _loadGate = new(1, 1);

        /// <summary>
        /// CTS для отмены текущей загрузки SOE.
        /// </summary>
        private CancellationTokenSource? _loadCts;

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
                ScheduleStateSave();            // debounce сохранения состояния
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
            set {
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

                ScheduleStateSave();

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

                ScheduleStateSave();
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

        /// <summary>
        /// Семафор: не допускаем параллельные DB-загрузки.
        /// </summary>
        private readonly SemaphoreSlim _dbGate = new(1, 1);

        /// <summary>
        /// CTS для отмены текущей DB-загрузки.
        /// </summary>
        private CancellationTokenSource? _dbCts;

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
            private set
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
                if (_isRestoringState)
                    return;

                _dbCts?.Cancel(); // отменяем предыдущую DB-загрузку при смене вкладки

                ScheduleStateSave(); // сохраняем состояние (debounce)

                // при переходе на DB-вкладки — делаем "как будто нажали Search"
                //if (IsDbTabSelected)
                    _ = OnTabActivatedLikeSearchAsync(force: true); //_ = LoadCurrentDbTabAsync(force: true);              

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
                ScheduleDbReload();

                ScheduleStateSave();
            }
        }

        // Дебаунс-таймер автоперезагрузки DB при смене даты (как поиск)
        private DispatcherTimer _dbReloadTimer;

        /// <summary>Текущая вкладка как enum (задел на будущие вкладки)</summary>
        public MainTabKind SelectedMainTab => (MainTabKind)SelectedMainTabIndex;

        /// <summary>Показывать DateEdit только на DB вкладках</summary>
        public bool IsDbTabSelected =>SelectedMainTab is MainTabKind.OperationActions or MainTabKind.AlarmHistory;

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

        /// <summary>
        /// Ключ “что именно загружали” для DB-вкладок: дата + строка поиска.
        /// </summary>
        private readonly record struct DbQueryKey(DateTime Date, string Filter);

        /// <summary>Последний успешно загруженный запрос для Operation actions.</summary>
        private DbQueryKey? _lastOpActsQuery;

        /// <summary>Последний успешно загруженный запрос для Alarm history.</summary>
        private DbQueryKey? _lastAlarmQuery;

        /// <summary>
        /// Нормализуем фильтр (убираем пробелы). Тут можно расширять логику, если нужно.
        /// </summary>
        private string GetDbFilter() => (EquipName ?? "").Trim();

        /// <summary>
        /// Текущий запрос DB из UI: выбранная дата + текущий фильтр.
        /// </summary>
        private DbQueryKey GetCurrentDbQuery() => new(DbDate.Date, GetDbFilter());

        #endregion

        #region Left pane toggle state

        private bool _layoutReady;
        private GridLength _leftSavedWidth = new(260);

        #endregion

        #region UI state persistence

        private readonly IUserStateService _stateService;

        // debounce для сохранения состояния
        private DispatcherTimer _stateSaveTimer = null!;
        private bool _isRestoringState;

        // Если true — на старте использовали ExternalTag как источник состояния
        private bool _startupUsedExternalTag;

        // Значение ExternalTag, которое использовали на старте (для применения Station/Type после загрузки Equipments)
        private string _startupExternalTag = "";

        #endregion

        #region Params

        // Строка состояния на вкладке Param
        private string _paramStatusText = "";
        
        public string ParamStatusText
        {
            get => _paramStatusText;
            private set { _paramStatusText = value; OnPropertyChanged(); }
        }

        // Что сейчас отображаем (чтобы понимать: перестраивать список или только обновить значения)
        private Type _currentParamModelType;

        // Текущая модель параметров (AIParam / DIParam / MotorParam / ...)
        private object _currentParamModel;
        public object CurrentParamModel
        {
            get => _currentParamModel;
            set {
                _currentParamModel = value;
                OnPropertyChanged();

                // ✅ Обновляем вычисляемые свойства для шапки ParamTabHost
                OnPropertyChanged(nameof(CurrentParamChanel));
                OnPropertyChanged(nameof(IsCurrentParamChanelVisible));
            }
        }

        // polling
        private CancellationTokenSource _paramPollCts;
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
        public string CurrentParamChanel => (CurrentParamModel as IHasChanel)?.Chanel ?? "";

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

        #region QR
        private readonly IQrCodeService _qrCodeService;
        private readonly IQrScannerService _qrScannerService;
        #endregion

        public MainWindow(IEquipmentService equipmentService, IDbService dbService, IUserStateService stateService, ICtApiService ctApiService, IConfiguration config, IQrCodeService qrCodeService, IQrScannerService qrScannerService)
        {
            InitializeComponent();

            _equipmentService = equipmentService;
            _dbService = dbService;
            _stateService = stateService;
            _ctApiService = ctApiService;
            _qrCodeService = qrCodeService;
            _qrScannerService = qrScannerService;

            // Vm + Controller
            Trend = new ParamTrendVm();
            Trend.AutoLive = config.GetValue("Trend:AutoLive", true);

            _trendCtl = new ParamTrendController(
                Trend,
                Dispatcher,
                _equipmentService,
                _ctApiService,
                resolveEquip: ResolveSelectedEquipForParam,        // твой существующий метод
                getParamModel: () => CurrentParamModel,            // твоя текущая модель параметров
                getParamCycles: () => _paramReadCycles             // счетчик циклов
            );

            DataContext = this; // DataContext на себя: используется во всём XAML (binding)

            InitEquipmentsView();
            InitSearchTimer();
            InitDbReloadTimer();
            InitStateSaveTimer();

            Loaded += async (_, __) =>
            {
                _layoutReady = true;
                InitLeftPaneState();

                // 1) ExternalTag имеет приоритет над user-state.json
                //    Если ExternalTag пустой -> восстановим с файла
                var usedExt = await TryApplyStartupStateFromExternalTagAsync();
                if (!usedExt)
                    await RestoreStateAsync();

                // 2) Параллельные загрузки
                await LoadEquipmentsListAsync();
                await CheckDbAsync();

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
                if (_startupUsedExternalTag && !string.IsNullOrWhiteSpace(_startupExternalTag))
                {
                    TryApplyStationTypeFiltersFromQr(_startupExternalTag);

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

        /// <summary>
        /// Начальное состояние: левая панель скрыта.
        /// </summary>
        private void InitLeftPaneState()
        {
            LeftPaneToggle.IsChecked = false;
            ApplyLeftPane(false);
        }

        /// <summary>
        /// Создаёт ICollectionView для Equipments и вешает фильтр/сортировку.
        /// </summary>
        private void InitEquipmentsView()
        {
            EquipmentsView = CollectionViewSource.GetDefaultView(Equipments);
            EquipmentsView.Filter = FilterEquipment;

            EquipmentsView.SortDescriptions.Clear();
            EquipmentsView.SortDescriptions.Add(
                new SortDescription(nameof(EquipListBoxItem.Equipment), ListSortDirection.Ascending));

            OnPropertyChanged(nameof(EquipmentsView)); // ✅ важно, если метод вызвали после того как UI уже связан
        }

        /// <summary>
        /// Таймер для посимвольного поиска (debounce 150мс).
        /// </summary>
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

        /// <summary>
        /// Запускает отложенный поиск (debounce).
        /// </summary>
        private void ScheduleSearch(string text)
        {
            _pendingSearch = text ?? "";
            _searchTimer.Stop();
            _searchTimer.Start();
        }

        /// <summary>
        /// Ищет элемент в EquipmentsView и выделяет его в ListBox.
        /// </summary>
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

        /// <summary>
        /// Фильтр для EquipmentsView: Station + Type.
        /// </summary>
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

        /// <summary>
        /// Применяет фильтры (перерисовка представления).
        /// </summary>
        private void ApplyFilters()
        {
            EquipmentsView.Refresh();
        }

        #endregion

        #region ListBox events

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
            if (_isRestoringState)
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

        /// <summary>
        /// Двойной клик по списку: сразу загружает SOE.
        /// </summary>
        //private async void Equipments_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        //{
        //    if (IsLoading) return;

        //    if (SelectedListBoxEquipment?.Equipment is string eq && !string.IsNullOrWhiteSpace(eq))
        //    {
        //        EquipName = eq;
        //        await LoadAndShowEquipDataAsync(eq);
        //    }
        //}

        #endregion

        #region SOE load

        /// <summary>
        /// Загружает SOE по выбранному оборудованию.
        /// Показывает overlay и прогресс (Current/Rows).
        /// </summary>
        private async Task LoadAndShowEquipDataAsync(string equipName)
        {
            var name = (equipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name))
                return;

            // Отменяем предыдущую загрузку SOE (если была)
            try { _loadCts?.Cancel(); } catch { }

            await _loadGate.WaitAsync();

            CancellationTokenSource? myCts = null;

            try
            {
                _loadCts?.Dispose();
                myCts = new CancellationTokenSource();
                _loadCts = myCts;
                var ct = myCts.Token;

                IsLoading = true;

                LoadedCount = 0;
                CurrentCount = 0;
                CurrentTrendIndex = 0;
                CurrentTrendName = "";
                TotalTrends = 0;

                await Dispatcher.Yield(DispatcherPriority.Render);

                var progress = new Progress<LoadingProgress>(p =>
                {
                    TotalTrends = p.TotalTrends;
                    CurrentTrendIndex = p.CurrentTrendIndex;
                    CurrentTrendName = p.CurrentTrendName;
                    CurrentCount = p.CurrentTrendCount;
                    LoadedCount = p.TotalLoaded;
                });

                var rows = await _equipmentService.GetDataFromEquipAsync(
                    name, progress, ct, perTrendMax: PerTrendMax, totalMax: TotalMax);

                ct.ThrowIfCancellationRequested();

                equipmentSOEDtos.Clear();
                foreach (var r in rows)
                    equipmentSOEDtos.Add(r);
            }
            catch (OperationCanceledException)
            {
                CurrentTrendName = "Cancelled";
            }
            catch (Exception ex)
            {
                DXMessageBox.Show(this, ex.ToString(), "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // Выключаем overlay только если это всё ещё “наша” актуальная загрузка
                if (ReferenceEquals(_loadCts, myCts))
                {
                    IsLoading = false;
                    _loadCts?.Dispose();
                    _loadCts = null;
                }

                _loadGate.Release();
            }
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

            await LoadAndShowEquipDataAsync(text);
        }

        /// <summary>
        /// Cancel: отменяет текущую загрузку SOE.
        /// </summary>
        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _loadCts?.Cancel();
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
                    await LoadCurrentDbTabAsync(force: true);
                    break;

                default:
                    // на будущие вкладки
                    await LoadCurrentDbTabAsync(force: true);
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

            await LoadAndShowEquipDataAsync(text);
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
                    await LoadCurrentDbTabAsync(force);
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

        #region DB

        /// <summary>
        /// Проверяет подключение к БД и обновляет IsDbConnected.
        /// </summary>
        private async Task CheckDbAsync()
        {
            try
            {
                IsDbConnected = await _dbService.CanConnectAsync();
            }
            catch
            {
                IsDbConnected = false;
            }
        }

        /// <summary>
        /// Загружает данные вкладки Operation actions.
        /// Нижняя панель показывает индикатор DB (крутилка).
        /// </summary>
        private async Task LoadOperatorActsAsync()
        {
            await _dbGate.WaitAsync();
            CancellationTokenSource? myCts = null;

            try
            {
                _dbCts?.Cancel();
                _dbCts?.Dispose();
                myCts = new CancellationTokenSource();
                _dbCts = myCts;
                var ct = myCts.Token;

                IsDbLoading = true;
                BottomText = "Loading DB (Operator actions)...";
                await Dispatcher.Yield(DispatcherPriority.Render);

                var filter = (EquipName ?? "").Trim();
                var rows = await _dbService.GetOperatorActsAsync(DbDate, filter, ct);

                OperatorActRows.Clear();
                foreach (var r in rows) OperatorActRows.Add(r);

                BottomText = $"DB Operator actions: {OperatorActRows.Count}";
            }
            catch (OperationCanceledException)
            {
                BottomText = "DB cancelled";
            }
            catch (Exception ex)
            {
                BottomText = $"DB Error: {ex.Message}";
            }
            finally
            {
                IsDbLoading = false;

                if (ReferenceEquals(_dbCts, myCts))
                {
                    _dbCts?.Dispose();
                    _dbCts = null;
                }

                _dbGate.Release();
            }
        }

        /// <summary>
        /// Загружает данные вкладки Alarm history.
        /// Нижняя панель показывает индикатор DB (крутилка).
        /// </summary>
        private async Task LoadAlarmHistoryAsync()
        {
            await _dbGate.WaitAsync();
            CancellationTokenSource? myCts = null;

            try
            {
                _dbCts?.Cancel();
                _dbCts?.Dispose();
                myCts = new CancellationTokenSource();
                _dbCts = myCts;
                var ct = myCts.Token;

                IsDbLoading = true;
                BottomText = "Loading DB (Alarm history)...";
                await Dispatcher.Yield(DispatcherPriority.Render);

                var filter = (EquipName ?? "").Trim();
                var rows = await _dbService.GetAlarmHistoryAsync(DbDate, filter, ct);

                AlarmHistoryRows.Clear();
                foreach (var r in rows) AlarmHistoryRows.Add(r);

                BottomText = $"DB Alarm history: {AlarmHistoryRows.Count}";
            }
            catch (OperationCanceledException)
            {
                BottomText = "DB cancelled";
            }
            catch (Exception ex)
            {
                BottomText = $"DB Error: {ex.Message}";
            }
            finally
            {
                IsDbLoading = false;

                if (ReferenceEquals(_dbCts, myCts))
                {
                    _dbCts?.Dispose();
                    _dbCts = null;
                }

                _dbGate.Release();
            }
        }

        /// <summary>
        /// Загружает данные текущей DB-вкладки (Operation actions / Alarm history).
        /// Если force=false — грузим только если изменились дата/фильтр по сравнению с прошлой загрузкой этой вкладки.
        /// </summary>
        private async Task LoadCurrentDbTabAsync(bool force)
        {
            if (!IsDbConnected) return;

            // Текущие параметры поиска/даты
            var current = GetCurrentDbQuery();

            // 0 = SOE, 1 = Operation actions, 2 = Alarm history
            if (SelectedMainTabIndex == 1)
            {
                // Если ничего не поменялось и не force — пропускаем
                if (!force && _lastOpActsQuery.HasValue && _lastOpActsQuery.Value.Equals(current))
                    return;

                await LoadOperatorActsAsync();

                // Важно: фиксируем “последний загруженный запрос” только после попытки загрузки
                // (у тебя LoadOperatorActsAsync внутри ловит исключения и не бросает их наружу)
                _lastOpActsQuery = current;
            }
            else if (SelectedMainTabIndex == 2)
            {
                if (!force && _lastAlarmQuery.HasValue && _lastAlarmQuery.Value.Equals(current))
                    return;

                await LoadAlarmHistoryAsync();
                _lastAlarmQuery = current;
            }
        }

        /// <summary>
        /// Кнопка "Load DB": принудительная перезагрузка активной DB-вкладки.
        /// </summary>
        //private async void LoadDb_Click(object sender, RoutedEventArgs e)
        //{
        //    await LoadCurrentDbTabAsync(force: true);
        //}

        /// <summary>Инициализация debounce-таймера DB</summary>
        private void InitDbReloadTimer()
        {
            _dbReloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _dbReloadTimer.Tick += async (_, __) =>
            {
                _dbReloadTimer.Stop();

                // Загружаем только если реально на DB вкладке и DB доступна
                if (!IsDbTabSelected || !IsDbConnected) return;

                await LoadCurrentDbTabAsync(force: true);
            };
        }

        /// <summary>Планирование авто-перезагрузки DB (debounce)</summary>
        private void ScheduleDbReload()
        {
            if (_dbReloadTimer == null) return;
            _dbReloadTimer.Stop();

            // Не трогаем DB, если мы в SOE
            if (!IsDbTabSelected) return;

            _dbReloadTimer.Start();
        }

        #endregion

        #region Param polling

        private void StartParamPolling()
        {
            StopParamPolling();

            _paramReadCycles = 0;
            ParamStatusText = "Param: starting...";

            _paramPollCts = new CancellationTokenSource();
            var ct = _paramPollCts.Token;

            // 1-й цикл сразу, потом каждые 5 секунд
            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    await PollParamOnceSafeAsync(ct);

                    // это для трендов
                    if (Trend.IsChartVisible)
                        await _trendCtl.PollOnceSafeAsync(ct, txt => BottomText = txt);

                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }
            }, ct);
        }

        private void StopParamPolling()
        {
            try { _paramPollCts?.Cancel(); } catch { }
            _paramPollCts?.Dispose();
            _paramPollCts = null;

            //ResetTrendState();
        }

        private async Task PollParamOnceSafeAsync(CancellationToken ct)
        {
            try
            {
                // Если недавно писали — подождем чуть-чуть
                if (DateTime.UtcNow < _paramReadResumeAtUtc)
                    return;

                // если пользователь сейчас вводит значение — НЕ читаем, чтобы не затирать ввод
                if (IsEditingField)
                    return;

                await _paramRwGate.WaitAsync(ct);
                try
                {
                    await PollParamOnceAsync(ct);
                }
                finally
                {
                    //_suppressParamWritesFromPolling = false;
                    _paramRwGate.Release();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                BottomText = $"Param read error: {ex.Message}";
            }
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

        private async Task PollParamOnceAsync(CancellationToken ct)
        {
            var (equipName, equipType) = ResolveSelectedEquipForParam();

            if (string.IsNullOrWhiteSpace(equipName))
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    ParamStatusText = "Param: select equipment";
                    ParamItems.Clear();
                    _currentParamModelType = null;
                });
                return;
            }

            var TypeGroup = EquipTypeRegistry.GetGroup(equipType ?? "");

            object model = TypeGroup switch
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

            await Dispatcher.InvokeAsync(() =>
            {
                _suppressParamWritesFromPolling = true;

                try
                {
                    if (model == null)
                    {
                        ParamStatusText = "Updating ...";
                        ParamItems.Clear();
                        _currentParamModelType = null;
                        return;
                    }

                    ApplyParamModelToUi(model);

                    // Chanel (если модель поддерживает)
                    //CurrentParamChanel = (model as IHasChanel)?.Chanel?.Trim() ?? "";

                    _paramReadCycles++;

                    ParamStatusText = $"Last update: {DateTime.Now:HH:mm:ss} | {_paramReadCycles} cycles";
                }
                finally
                {
                    // Снимаем подавление ПОСЛЕ того, как UI применит биндинги/создаст визуальные элементы
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _suppressParamWritesFromPolling = false;
                    }), DispatcherPriority.ContextIdle); // можно Background, но ContextIdle обычно надежнее
                }
            });
        }

        private void ApplyParamModelToUi(object model)
        {
            CurrentParamModel = model;
            var modelType = model.GetType();

            // Если модель поменялась (например AI -> DI), пересоздаём строки
            if (_currentParamModelType != modelType)
            {
                ParamItems.Clear();

                var props = modelType
                    .GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
                    .Where(p => p.CanRead)
                    .OrderBy(p => p.MetadataToken) // обычно сохраняет порядок объявления в классе
                    .ToList();

                foreach (var p in props)
                {
                    ParamItems.Add(new ParamItem
                    {
                        Name = p.Name,
                        Value = p.GetValue(model)
                    });
                }

                _currentParamModelType = modelType;
                return;
            }

            // Та же модель — просто обновляем значения
            var map = modelType
                .GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
                .Where(p => p.CanRead)
                .ToDictionary(p => p.Name, p => p, StringComparer.Ordinal);

            foreach (var row in ParamItems)
            {
                if (map.TryGetValue(row.Name, out var prop))
                    row.Value = prop.GetValue(model);
            }
        }

        // Взводим, когда пользователь начал редактировать поле (focus в TextEdit)
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

        // для checkBox
        public async void ParamEditable_EditValueChanged(object sender, DevExpress.Xpf.Editors.EditValueChangedEventArgs e)
        {
            // 0) подавляем записи, если это откат значения из UI (Cancel в confirm)
            if (_suppressParamWritesFromUiRollback)
                return;

            // 1) Не пишем, если это обновление прилетело из polling-READ
            if (_suppressParamWritesFromPolling)
                return;

            // 2) Пишем только на вкладке Param
            if (SelectedMainTab != MainTabKind.Param)
                return;

            // 3) Нужно имя оборудования
            var equip = (EquipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(equip))
                return;

            // 4) Определяем EquipItem из Tag
            if (sender is not FrameworkElement fe || fe.Tag is not string equipItem || string.IsNullOrWhiteSpace(equipItem))
                return;

            // Confirm только при включении ForceCmd (false -> true).
            if (equipItem.Equals("ForceCmd", StringComparison.OrdinalIgnoreCase))
            {
                // Важно: e.OldValue/e.NewValue обычно bool? для CheckEdit
                bool oldVal = ToBool(e.OldValue);
                bool newVal = ToBool(e.NewValue);

                // Показываем окно ТОЛЬКО при включении форса
                if (!oldVal && newVal)
                {
                    var res = DXMessageBox.Show(this,"Do you really want to enable channel forcing?","Attention!!!",MessageBoxButton.OKCancel,MessageBoxImage.Warning);

                    if (res != MessageBoxResult.OK)
                    {
                        // Cancel -> откатить чекбокс и не писать в SCADA
                        _suppressParamWritesFromUiRollback = true;
                        try
                        {
                            // откатываем именно IsChecked
                            if (sender is CheckEdit ce)
                                ce.IsChecked = (e.OldValue as bool?) ?? oldVal;
                        }
                        finally
                        {
                            _suppressParamWritesFromUiRollback = false;
                        }

                        return;
                    }
                }
            }

            // 5) Пытаемся нормализовать значение (у тебя эти поля int)
            //    e.NewValue может быть string/null в процессе набора — аккуратно.
            if (!TryNormalizeWriteValue(e.NewValue, out string writeValue))
                return;

            await WriteParamAsync(equip, equipItem, writeValue);
        }

        // по нажатию кнопки Еnter
        public async void ParamEditable_EditValueChanged(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // пишем только по Enter
            if (e.Key != System.Windows.Input.Key.Enter && e.Key != System.Windows.Input.Key.Return)
                return;

            // 1) Не пишем, если это обновление прилетело из polling-READ
            if (_suppressParamWritesFromPolling)
                return;

            // 2) Пишем только на вкладке Param
            if (SelectedMainTab != MainTabKind.Param)
                return;

            // 3) Нужно имя оборудования
            var equip = (EquipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(equip))
                return;

            // 4) Определяем EquipItem из Tag
            if (sender is not FrameworkElement fe || fe.Tag is not string equipItem || string.IsNullOrWhiteSpace(equipItem))
                return;

            // 5) Берём текущее значение из редактора
            object? newValue = (sender as DevExpress.Xpf.Editors.BaseEdit)?.EditValue;

            if (!TryNormalizeWriteValue(newValue, out string writeValue))
                return;

            e.Handled = true; // чтобы Enter не "пищал" / не делал лишнего

            // закончили ввод
            EndParamFieldEdit();

            await WriteParamAsync(equip, equipItem, writeValue);
        }

        /// <summary>
        /// Универсальная запись параметра по equipItem (Tag) и значению из UI.
        /// Используется ToggleButton/кастомными контролами без DevExpress EditValueChanged.
        /// </summary>
        public async void ParamEditable_WriteFromUi(string? equipItem, object? newValue)
        {
            if (_suppressParamWritesFromPolling)
                return;

            if (SelectedMainTab != MainTabKind.Param)
                return;

            var (equipName, _) = ResolveSelectedEquipForParam();
            var equip = (equipName ?? "").Trim();

            if (string.IsNullOrWhiteSpace(equip))
                return;

            if (string.IsNullOrWhiteSpace(equipItem))
                return;

            if (!TryNormalizeWriteValue(newValue, out string writeValue))
                return;

            await WriteParamAsync(equip, equipItem, writeValue);
        }

        private static bool TryNormalizeWriteValue(object? newValue, out string str)
        {
            str = "";

            if (newValue == null)
                return false;

            if (newValue is bool b)
            {
                str = b ? "1" : "0";
                return true;
            }

            // DevExpress иногда дает string во время набора
            if (newValue is string s)
            {
                s = s.Trim();
                if (s.Length == 0) return false;

                // Разрешаем только число (под твои поля)
                if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                {
                    str = i.ToString(CultureInfo.InvariantCulture);
                    return true;
                }

                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    str = d.ToString(CultureInfo.InvariantCulture);
                    return true;
                }

                return false;
            }

            // Если пришло число напрямую
            if (newValue is int i2) { str = i2.ToString(CultureInfo.InvariantCulture); return true; }
            if (newValue is double d2) { str = d2.ToString(CultureInfo.InvariantCulture); return true; }

            // Фоллбек
            str = Convert.ToString(newValue, CultureInfo.InvariantCulture) ?? "";
            return str.Length > 0;
        }

        private async Task WriteParamAsync(string equipName, string equipItem, string writeValue)
        {
            try
            {
                // Запись должна “победить” чтение: берем тот же gate, что и polling
                await _paramRwGate.WaitAsync(CancellationToken.None);
                try
                {
                    // Пауза чтения на время записи + чуть после
                    _paramReadResumeAtUtc = DateTime.UtcNow.AddMilliseconds(400);

                    BottomText = $"Write: {equipItem}={writeValue} ...";
                    await Dispatcher.Yield(DispatcherPriority.Render);

                    // Пишем через сервис
                    await _equipmentService.WriteEquipItemAsync(equipName, equipItem, writeValue);

                    BottomText = $"Wrote: {equipItem}={writeValue} at {DateTime.Now:HH:mm:ss}";

                    // После записи можно сделать быстрый перечит (по желанию):
                    // await PollParamOnceSafeAsync(CancellationToken.None);
                }
                finally
                {
                    _paramRwGate.Release();
                }
            }
            catch (Exception ex)
            {
                BottomText = $"Write error ({equipItem}): {ex.Message}";
            }
        }

        // helper: безопасно привести object? к bool
        private static bool ToBool(object? v)
        {
            try
            {
                if (v is bool b) return b;
                //if (v is bool? bn) return bn.GetValueOrDefault(false);
                if (v == null) return false;
                return Convert.ToBoolean(v);
            }
            catch
            {
                return false;
            }
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
        public async Task Param_GenerateQrAsync()
        {
            var text = string.Empty;
            var outputDir = string.Empty;
            var path = string.Empty;

            try
            {
                // 0) Если файл уже существует — не генерируем дубликаты (UI может не успеть скрыть кнопку)
                if (Param_IsQrAlreadyGenerated())
                {
                    text = GetQrTextOrEmpty();
                    outputDir = GetQrOutputDirectory(text);
                    path = _qrCodeService.GetExpectedQrPngPath(text, outputDirectory: outputDir);

                    DXMessageBox.Show(this, $"QR уже существует:\n{path}", "QR", MessageBoxButton.OK, MessageBoxImage.Information);
                    NotifyParamQrUiChanged();

                    return;
                }

                // 1) Берём текст: сначала поле поиска, если пусто — выбранное оборудование
                text = (EquipName ?? "").Trim();
                if (string.IsNullOrWhiteSpace(text))
                    text = (SelectedListBoxEquipment?.Equipment ?? "").Trim();

                if (string.IsNullOrWhiteSpace(text))
                {
                    DXMessageBox.Show(this, "Нет текста для QR.\nВведи имя в поиск или выбери оборудование в списке.", "QR", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // 2) Авто-папка: Station\Type
                outputDir = GetQrOutputDirectory(text);

                // 3) Генерация PNG
                path = await _qrCodeService.GenerateQrPngAsync(text, outputDirectory: outputDir);

                // 4) UI-статусы (по желанию)
                ParamStatusText = $"QR saved: {System.IO.Path.GetFileName(path)}";
                //BottomText = $"QR saved: {path}";

                // 5) DevExpress модалка об успехе
                DXMessageBox.Show(this, $"QR-код успешно сохранён:\n{path}","QR",MessageBoxButton.OK,MessageBoxImage.Information);
                // после сохранения файл появился -> кнопку нужно спрятать
                NotifyParamQrUiChanged();
            }
            catch (Exception ex)
            {
                DXMessageBox.Show(this,ex.ToString(),"QR generate error",MessageBoxButton.OK,MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Param tab: сканирует QR с камеры.
        /// Затем:
        /// 1) выставляет Station/Type фильтры по найденному оборудованию,
        /// 2) пишет в ExternalTag (best-effort),
        /// 3) подставляет в поиск,
        /// 4) выделяет оборудование,
        /// 5) переключает на Param и запускает polling.
        /// </summary>
        public async Task Param_ScanQrToExternalTagAndSearchAsync()
        {
            try
            {
                // 1) Сканируем камерой (модально)
                var text = await _qrScannerService.ScanFromCameraAsync(this);
                if (string.IsNullOrWhiteSpace(text))
                    return;

                text = text.Trim();

                // 2) Ставим фильтры Station/Type по данным оборудования (если найдём)
                // Важно: это нужно сделать ДО DoIncrementalSearch, иначе элемент может быть "за фильтром".
                TryApplyStationTypeFiltersFromQr(text);

                // 3) Пишем в внешний тег (best-effort: если не выйдет — не мешаем поиску)
                try
                {
                    await _equipmentService.SetExternalTagAsync(text);
                }
                catch
                {
                    // Не критично
                }

                // 4) Подставляем в поле поиска
                EquipName = text;

                // 5) Выделяем элемент в ListBox (уже с правильными фильтрами)
                DoIncrementalSearch(text);

                // 6) Переключаем на Param + запускаем polling
                if (SelectedMainTab != MainTabKind.Param)
                    SelectedMainTabIndex = (int)MainTabKind.Param;

                StartParamPolling();

                ParamStatusText = $"QR scanned: {text}";
            }
            catch (Exception ex)
            {
                DXMessageBox.Show(this, ex.ToString(), "QR scan error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Делает безопасную часть пути (для папок Station/Type).
        /// </summary>
        private static string MakeSafePathPart(string? s)
        {
            s = (s ?? "").Trim();
            if (string.IsNullOrWhiteSpace(s))
                return "Unknown";

            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var safe = new string(s.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());

            // дополнительно: чтобы не было точек/пробелов в конце
            safe = safe.Trim().TrimEnd('.', ' ');

            if (safe.Length == 0)
                safe = "Unknown";

            // чтобы папки не были супер-длинными
            if (safe.Length > 60)
                safe = safe.Substring(0, 60);

            return safe;
        }

        /// <summary>
        /// Ищет оборудование по тексту QR в ПОЛНОМ списке (без учёта фильтров).
        /// Сначала точное совпадение, затем StartsWith/Contains по Equipment и Tag.
        /// </summary>
        private EquipListBoxItem? FindEquipmentForQrText(string qrText)
        {
            qrText = (qrText ?? "").Trim();
            if (qrText.Length == 0)
                return null;

            // 1) exact Equipment
            var it =
                Equipments.FirstOrDefault(x => string.Equals(x.Equipment, qrText, StringComparison.OrdinalIgnoreCase))
                ?? Equipments.FirstOrDefault(x => string.Equals(x.Tag, qrText, StringComparison.OrdinalIgnoreCase));

            if (it != null)
                return it;

            // 2) startswith Equipment, then Tag
            it =
                Equipments.FirstOrDefault(x => (x.Equipment ?? "").StartsWith(qrText, StringComparison.OrdinalIgnoreCase))
                ?? Equipments.FirstOrDefault(x => (x.Tag ?? "").StartsWith(qrText, StringComparison.OrdinalIgnoreCase));

            if (it != null)
                return it;

            // 3) contains Equipment, then Tag
            it =
                Equipments.FirstOrDefault(x => (x.Equipment ?? "").Contains(qrText, StringComparison.OrdinalIgnoreCase))
                ?? Equipments.FirstOrDefault(x => (x.Tag ?? "").Contains(qrText, StringComparison.OrdinalIgnoreCase));

            return it;
        }

        /// <summary>
        /// Определяет папку для сохранения QR: .\QRCodes\Station\TypeGroup
        /// Логика:
        /// - если найдено оборудование по тексту -> берём Station из него и TypeGroup из его Type
        /// - иначе -> берём текущий фильтр SelectedStation и SelectedTypeFilter (TypeGroup)
        /// </summary>
        private string GetQrOutputDirectory(string qrText)
        {
            var baseDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "QRCodes");

            var match = FindEquipmentForQrText(qrText);

            // --- Station ---
            string stationPart =
                !string.IsNullOrWhiteSpace(match?.Station) ? match!.Station :
                !string.IsNullOrWhiteSpace(SelectedStation) ? SelectedStation :
                "All";

            // --- TypeGroup ---
            // Если нашли оборудование -> вычисляем группу по его Type
            // Иначе берём текущий выбранный фильтр-группу
            EquipTypeGroup group =
                match != null
                    ? EquipTypeRegistry.GetGroup(match.Type ?? "")
                    : SelectedTypeFilter;

            string groupPart =
                group != EquipTypeGroup.All
                    ? group.ToString()      // например "AI", "DI", ...
                    : "All";

            stationPart = MakeSafePathPart(stationPart);
            groupPart = MakeSafePathPart(groupPart);

            return System.IO.Path.Combine(baseDir, stationPart, groupPart);
        }

        /// <summary>
        /// Применяет фильтры Station/Type по найденному элементу оборудования.
        /// Возвращает true, если удалось найти и применить.
        /// </summary>
        private bool TryApplyStationTypeFiltersFromQr(string qrText)
        {
            var match = FindEquipmentForQrText(qrText);
            if (match == null)
                return false;

            // 1) Station
            if (!string.IsNullOrWhiteSpace(match.Station))
                SelectedStation = match.Station.Trim();

            // 2) TypeFilter (группа по match.Type)
            var grp = EquipTypeRegistry.GetGroup(match.Type ?? "");
            SelectedTypeFilter = grp;

            return true;
        }

        /// <summary>
        /// Возвращает текст, по которому сейчас будет генерироваться QR:
        /// сначала EquipName, если пусто — выбранное оборудование.
        /// </summary>
        private string GetQrTextOrEmpty()
        {
            var text = (EquipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(text))
                text = (SelectedListBoxEquipment?.Equipment ?? "").Trim();

            return text ?? "";
        }

        /// <summary>
        /// Проверяет, существует ли уже QR PNG файл для текущего текста (с учётом Station\TypeGroup папки).
        /// Это используется для скрытия кнопки Generate QR.
        /// </summary>
        public bool Param_IsQrAlreadyGenerated()
        {
            var text = GetQrTextOrEmpty();
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var outputDir = GetQrOutputDirectory(text); // у тебя уже есть Station\TypeGroup логика
            var expectedPath = _qrCodeService.GetExpectedQrPngPath(text, outputDirectory: outputDir);

            return File.Exists(expectedPath);
        }

        /// <summary>
        /// True => показываем кнопку Generate QR.
        /// False => прячем (нет текста для QR или файл уже существует).
        /// Используется в XAML через BoolToVis.
        /// </summary>
        public bool Param_ShowGenerateQrButton => GetParamShowGenerateQrButton();

        /// <summary>
        /// Реальная логика показа кнопки (вынесена в метод, чтобы было удобно отлаживать).
        /// </summary>
        private bool GetParamShowGenerateQrButton()
        {
            var text = GetQrTextOrEmpty();
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var outputDir = GetQrOutputDirectory(text); // Station\TypeGroup
            var expectedPath = _qrCodeService.GetExpectedQrPngPath(text, outputDirectory: outputDir);

            return !File.Exists(expectedPath);
        }

        /// <summary>
        /// Уведомляет UI, что нужно пересчитать Visibility кнопки Generate QR.
        /// </summary>
        private void NotifyParamQrUiChanged()
        {
            OnPropertyChanged(nameof(Param_ShowGenerateQrButton));
        }

        #endregion

        #region UiStatePersistence

        /// <summary>
        /// Пытается восстановить стартовое состояние из ExternalTag.
        /// Правила:
        /// - если ExternalTag НЕ пустой и НЕ "Unknown":
        ///     EquipName = ExternalTag
        ///     SelectedTab = 0 (Param)
        ///     DbDate = Today
        ///     SelectedStation/SelectedTypeFilter выставим позже (после загрузки Equipments)
        /// - если пустой: возвращаем false (тогда восстановим user-state.json)
        /// </summary>
        private async Task<bool> TryApplyStartupStateFromExternalTagAsync()
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

                // Сразу очищаем ExternalTag, чтобы не конфликтовал при следующем запуске
                // Best-effort: не ломаем старт, если запись не удалась
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _equipmentService.SetExternalTagAsync("", cts.Token);
                }
                catch
                {
                    // ignore
                }


                // Важно: чтобы не запускались автодействия/автосейв во время проставления
                _isRestoringState = true;
                try
                {
                    // 1) Берём имя из ExternalTag
                    EquipName = _startupExternalTag;

                    // 2) Всегда Param tab
                    SelectedMainTabIndex = (int)MainTabKind.Param;

                    // 3) Дата = сегодня
                    DbDate = DateTime.Today;

                    // 4) Фильтры Station/TypeGroup выставим после загрузки Equipments,
                    //    пока ставим безопасные значения
                    SelectedStation = "All";
                    SelectedTypeFilter = EquipTypeGroup.All;
                }
                finally
                {
                    _isRestoringState = false;
                }

                return true;
            }
            catch
            {
                // Если что-то пошло не так — просто откатываемся на восстановление из файла
                _startupUsedExternalTag = false;
                _startupExternalTag = "";
                return false;
            }
        }

        /// <summary>
        /// Восстанавливает UI-состояние из user-state.json.
        /// Важно: защищаемся флагом _isRestoringState, чтобы не запускать автосейв во время восстановления.
        /// </summary>
        private async Task RestoreStateAsync()
        {
            _isRestoringState = true;
            try
            {
                var state = await _stateService.LoadAsync();
                if (state == null) return;

                EquipName = state.LastEquipName ?? "";
                DbDate = state.DbDate.Date;

                SelectedStation = state.SelectedStation ?? "All";
                SelectedTypeFilter = state.SelectedTypeFilter;

                SelectedMainTabIndex = (int)state.SelectedTab;
            }
            finally
            {
                _isRestoringState = false;
            }
        }

        /// <summary>
        /// Если внешний тег (ExternalTag) содержит оборудование — он имеет приоритет.
        /// Если пустой/Unknown — оставляем восстановленное состояние.
        /// </summary>
        private async Task ApplyExternalTagIfAnyAsync()
        {
            try
            {
                var ext = await _equipmentService.GetExternalTagAsync(CancellationToken.None);

                if (string.IsNullOrWhiteSpace(ext) ||
                    ext.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                    return;

                // ExternalTag найден -> используем его
                EquipName = ext.Trim();

                // Обычно логично на первую вкладку, но можно оставить как есть:
                //SelectedMainTabIndex = (int)MainTabKind.SOE;
            }
            catch
            {
                // ExternalTag не критичен
            }
        }

        /// <summary>
        /// Сохранение состояния (debounce)
        /// </summary>
        private void InitStateSaveTimer()
        {
            _stateSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _stateSaveTimer.Tick += async (_, __) =>
            {
                _stateSaveTimer.Stop();
                await SaveStateAsync();
            };
        }

        /// <summary>
        /// Планирование сохранения (вызов из setter’ов или централизованно)
        /// </summary>
        private void ScheduleStateSave()
        {
            if (_isRestoringState) return;

            _stateSaveTimer.Stop();
            _stateSaveTimer.Start();
        }

        /// <summary>
        /// Само сохранение
        /// </summary>
        private async Task SaveStateAsync()
        {
            if (_isRestoringState) return;

            var state = new UserState
            {
                LastEquipName = (EquipName ?? "").Trim(),
                DbDate = DbDate.Date,
                SelectedTab = (MainTabKind)SelectedMainTabIndex,
                SelectedStation = (SelectedStation ?? "All").Trim(),
                SelectedTypeFilter = SelectedTypeFilter
            };

            await _stateService.SaveAsync(state);
        }

        #endregion

        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        #endregion
    }
}
