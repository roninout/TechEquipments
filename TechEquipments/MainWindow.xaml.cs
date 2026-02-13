using CtApi;
using DevExpress.Xpf.Charts;
using DevExpress.Xpf.Core;
using DevExpress.XtraRichEdit.Import.Html;
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

                ScheduleSearch(_equipName);   // твой debounce поиска
                ScheduleStateSave();        // debounce сохранения состояния
            }
        }

        /// <summary>
        /// Выбранный элемент в ListBox.
        /// </summary>
        private EquipListBoxItem? _selectedListBoxEquipment;
        public EquipListBoxItem? SelectedListBoxEquipment
        {
            get => _selectedListBoxEquipment;
            set { _selectedListBoxEquipment = value; OnPropertyChanged(); }
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
            set { _currentParamModel = value; OnPropertyChanged(); }
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

        #endregion

        #region Trend

        private bool _isParamChartVisible = true;
        public bool IsParamChartVisible
        {
            get => _isParamChartVisible;
            set {
                if (_isParamChartVisible == value)
                    return;

                _isParamChartVisible = value;

                OnPropertyChanged();
                OnPropertyChanged(nameof(IsParamSettingsVisible));
            }
        }

        public bool IsParamSettingsVisible => !IsParamChartVisible;

        private string? _trendEquipName; // чтобы при смене equip сбрасывать кэши
        private readonly Dictionary<string, string> _trnNameByItem = new(StringComparer.OrdinalIgnoreCase);   // кэш TrendTag
        private readonly Dictionary<string, DateTime> _lastUtcByItem = new(StringComparer.OrdinalIgnoreCase); // чтобы добирать только новые точки

        private double _axisYMin;
        public double AxisYMin
        {
            get => _axisYMin;
            set { if (_axisYMin == value) return; _axisYMin = value; OnPropertyChanged(); }
        }

        private double _axisYMax;
        public double AxisYMax
        {
            get => _axisYMax;
            set { if (_axisYMax == value) return; _axisYMax = value; OnPropertyChanged(); }
        }

        // Gate: trend обновляется и polling’ом, и автоподгрузкой истории (чтобы не было гонок)
        private readonly SemaphoreSlim _trendGate = new(1, 1);

        // Live: окно pinned к now-60..now
        // History: окно двигает пользователь
        private bool _trendLiveMode = true;

        private const int TrendLiveWindowMinutes = 60;
        private const int TrendHistoryChunkMinutes = 60; // сколько подгружаем за раз
        private const int TrendHistoryKeepHours = 24;    // страховка по памяти (0 = не резать)

        private DateTime _trendLastNavUtc = DateTime.MinValue;
        private static readonly TimeSpan TrendNavDebounce = TimeSpan.FromMilliseconds(250);

        // VisualRange
        private DateTime _axisXMin;
        private DateTime _axisXMax;
        public DateTime AxisXMin { get => _axisXMin; set { _axisXMin = value; OnPropertyChanged(); } }
        public DateTime AxisXMax { get => _axisXMax; set { _axisXMax = value; OnPropertyChanged(); } }

        // WholeRange (весь загруженный диапазон)
        private DateTime _axisXWholeMin;
        private DateTime _axisXWholeMax;
        public DateTime AxisXWholeMin { get => _axisXWholeMin; set { _axisXWholeMin = value; OnPropertyChanged(); } }
        public DateTime AxisXWholeMax { get => _axisXWholeMax; set { _axisXWholeMax = value; OnPropertyChanged(); } }

        #endregion

        public MainWindow(IEquipmentService equipmentService, IDbService dbService, IUserStateService stateService, ICtApiService ctApiService)
        {
            InitializeComponent();

            _equipmentService = equipmentService;
            _dbService = dbService;
            _stateService = stateService;
            _ctApiService = ctApiService;
            
            DataContext = this; // DataContext на себя: используется во всём XAML (binding)

            InitEquipmentsView();
            InitSearchTimer();
            InitDbReloadTimer();
            InitStateSaveTimer();

            Loaded += async (_, __) =>
            {
                _layoutReady = true;
                InitLeftPaneState();

                // 1) Сначала восстановим сохранённое состояние (включая вкладку/дату/поиск)
                await RestoreStateAsync();

                // 2) Потом пытаемся взять ExternalTag.
                //    Если ExternalTag пустой/Unknown — остаёмся на восстановленном.
                await ApplyExternalTagIfAnyAsync();

                // 3) Параллельные загрузки
                _ = LoadEquipmentsListAsync();
                await CheckDbAsync();

                // 4) И как будто нажали “поиск/лоад” на текущей вкладке
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
                    if (IsParamChartVisible)
                        await PollTrendOnceSafeAsync(ct);

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

                await _paramRwGate.WaitAsync(ct);
                try
                {
                    // --- ЧТЕНИЕ ---
                    await PollParamOnceAsync(ct);
                    // result = await _equipmentService.ReadEquipModelAsync<AIParam>(...);

                    // ВАЖНО: пока мы применяем значения в модель/биндинги — не запускать запись
                    _suppressParamWritesFromPolling = true;

                    // apply result -> VM/Model
                    // CurrentParam = result; или заполняешь свойства

                    // Статус
                    // ParamStatusText = $"Read: {count} at {DateTime.Now:HH:mm:ss}";
                }
                finally
                {
                    _suppressParamWritesFromPolling = false;
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
                if (model == null)
                {
                    //ParamStatusText = $"Param: no model for type '{equipType}'";
                    ParamStatusText = $"Updating ...";
                    ParamItems.Clear();
                    _currentParamModelType = null;
                    return;
                }

                ApplyParamModelToUi(model);

                _paramReadCycles++;
                ParamStatusText = $"Last update: {DateTime.Now:HH:mm:ss} | {_paramReadCycles} cycles";
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


        #endregion

        #region Param Write

        public async void ParamEditable_EditValueChanged(object sender, DevExpress.Xpf.Editors.EditValueChangedEventArgs e)
        {
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

            // 5) Пытаемся нормализовать значение (у тебя эти поля int)
            //    e.NewValue может быть string/null в процессе набора — аккуратно.
            if (!TryNormalizeWriteValue(e.NewValue, out string writeValue))
                return;

            await WriteParamAsync(equip, equipItem, writeValue);
        }

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

        #endregion

        #region Trend

        private void ResetTrendState(bool clearPoints = true)
        {
            if (clearPoints)
                ParamTrendPoints.Clear();

            _trnNameByItem.Clear();
            _lastUtcByItem.Clear();
            _trendEquipName = null;

            // Back to Live mode and reset ranges.
            _trendLiveMode = true;

            // Axis X: visible window.
            AxisXMax = DateTime.Now;
            AxisXMin = AxisXMax.AddMinutes(-TrendLiveWindowMinutes);

            // Axis X: whole range (scrollable area).
            AxisXWholeMin = AxisXMin;
            AxisXWholeMax = AxisXMax;
        }

        /// <summary>
        /// Toggle метод (вызов из кнопки)
        /// при включении графика можно делать resetWhenShow=true
        /// </summary>
        public void ToggleParamChart(bool resetWhenShow = false)
        {
            if (IsParamChartVisible)
            {
                ShowParamSettings();
            }
            else
            {
                ShowParamChart(reset: resetWhenShow);
            }
        }

        private async Task PollTrendOnceSafeAsync(CancellationToken ct)
        {
            try
            {
                await _trendGate.WaitAsync(ct);
                try
                {
                    await PollTrendOnceAsync(ct);
                }
                finally
                {
                    _trendGate.Release();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                // чтобы не “убить” polling
                BottomText = $"Trend error: {ex.Message}";
            }
        }

        //private async Task PollTrendOnceAsync(CancellationToken ct)
        //{
        //    // NOTE:
        //    // We use a single Y axis for all series.
        //    // The FIRST trend item defines the base Y-range (axis).
        //    // Every NEXT item can specify its own native range via [TrendItem(..., YMin=..., YMax=...)].
        //    // Those items are linearly scaled into the base range for drawing.
        //    // Tooltips/crosshair still show the original (raw) values.

        //    var (equipName, equipType) = ResolveSelectedEquipForParam();
        //    if (string.IsNullOrWhiteSpace(equipName))
        //        return;

        //    // если сменилось оборудование — сбрасываем кэши/точки
        //    if (!string.Equals(_trendEquipName, equipName, StringComparison.OrdinalIgnoreCase))
        //    {
        //        _trendEquipName = equipName;
        //        _trnNameByItem.Clear();
        //        _lastUtcByItem.Clear();

        //        await Dispatcher.InvokeAsync(() => ParamTrendPoints.Clear());
        //    }

        //    // TrendItems are declared by attributes of the CURRENT param model.
        //    // First item is treated as "base" for axis Y.
        //    var trendItems = GetTrendItemsFromModel(CurrentParamModel, "R");

        //    var endUtc = DateTime.UtcNow;

        //    foreach (var item in trendItems)
        //    {
        //        ct.ThrowIfCancellationRequested();

        //        // 1) TrendTagName для item
        //        if (!_trnNameByItem.TryGetValue(item, out var trnName) || string.IsNullOrWhiteSpace(trnName))
        //        {
        //            trnName = await _equipmentService.GetTrnName(equipName, item);
        //            if (string.IsNullOrWhiteSpace(trnName))
        //                continue;

        //            _trnNameByItem[item] = trnName;
        //        }

        //        // 2) окно чтения по последней точке данного item
        //        var startUtc = _lastUtcByItem.TryGetValue(item, out var lastUtc)
        //            ? lastUtc.AddSeconds(-2)
        //            : endUtc.AddMinutes(-60);

        //        var trn = await _ctApiService.GetTrnData(trnName, startUtc, endUtc);
        //        if (trn == null || trn.Count == 0)
        //            continue;

        //        var points = trn
        //            .Select(x => new TrendPoint
        //            {
        //                Series = item,
        //                Time = DateTime.SpecifyKind(x.DateTime, DateTimeKind.Utc).ToLocalTime(),
        //                Value = x.Value
        //            })
        //            .OrderBy(p => p.Time)
        //            .ToList();

        //        if (points.Count == 0)
        //            continue;

        //        // 3) применяем в UI
        //        await Dispatcher.InvokeAsync(() =>
        //        {
        //            // добавляем только новые точки для этой серии
        //            var lastAdded = ParamTrendPoints
        //                .Where(p => p.Series.Equals(item, StringComparison.OrdinalIgnoreCase))
        //                .Select(p => p.Time)
        //                .DefaultIfEmpty(DateTime.MinValue)
        //                .Max();

        //            foreach (var p in points)
        //                if (p.Time > lastAdded)
        //                    ParamTrendPoints.Add(p);

        //            // держим окно 60 минут (удаляем старые по всем сериям)
        //            var minKeep = DateTime.Now.AddMinutes(-60);
        //            for (int i = ParamTrendPoints.Count - 1; i >= 0; i--)
        //                if (ParamTrendPoints[i].Time < minKeep)
        //                    ParamTrendPoints.RemoveAt(i);

        //            // ось X всегда "с края до края"
        //            AxisXMax = DateTime.Now;
        //            AxisXMin = AxisXMax.AddMinutes(-60);

        //            // ось Y
        //            //if (TryGetModelScaleMinMax(out var yMin, out var yMax))
        //            if (TryGetModelScaleMinMax(out var yMin, out var yMax))
        //            {
        //                AxisYMin = yMin;
        //                AxisYMax = yMax;
        //            }

        //            ParamStatusText = $"Trends={trendItems.Length}, Points={ParamTrendPoints.Count} | {DateTime.Now:HH:mm:ss} | {_paramReadCycles} cycles";
        //        });

        //        // 4) обновляем lastUtc для item (UTC!)
        //        _lastUtcByItem[item] = trn.Max(x => x.DateTime);
        //    }
        //}

        private async Task PollTrendOnceAsync(CancellationToken ct)
        {
            // NOTE:
            // We use a single Y axis for all series.
            // The FIRST trend item defines the base Y-range (axis).
            // Every NEXT item can specify its own native range via [TrendItem(..., YMin=..., YMax=...)].
            // Those items are linearly scaled into the base range for drawing.
            // Tooltips/crosshair still show the original (raw) values.

            var (equipName, _) = ResolveSelectedEquipForParam();
            if (string.IsNullOrWhiteSpace(equipName))
                return;

            // If equipment changed — reset caches/points.
            if (!string.Equals(_trendEquipName, equipName, StringComparison.OrdinalIgnoreCase))
            {
                _trendEquipName = equipName;
                _trnNameByItem.Clear();
                _lastUtcByItem.Clear();

                await Dispatcher.InvokeAsync(() => ParamTrendPoints.Clear());
            }

            // TrendItems are declared by attributes of the CURRENT param model.
            // First item is treated as "base" for axis Y.
            var trendItems = GetTrendItemsFromModel(CurrentParamModel, "R");
            if (trendItems.Length == 0)
                trendItems = new[] { "R" };

            var baseItem = trendItems[0];

            // Resolve base Y-range (axis): attribute on baseItem OR model MinR/MaxR.
            if (!TryGetBaseYRange(baseItem, out var baseMin, out var baseMax))
            {
                baseMin = 0;
                baseMax = 1;
            }

            var endUtc = DateTime.UtcNow;

            foreach (var item in trendItems)
            {
                ct.ThrowIfCancellationRequested();

                // 1) Resolve TrendTagName for item (cached).
                if (!_trnNameByItem.TryGetValue(item, out var trnName) || string.IsNullOrWhiteSpace(trnName))
                {
                    trnName = await _equipmentService.GetTrnName(equipName, item);
                    if (string.IsNullOrWhiteSpace(trnName))
                        continue;

                    _trnNameByItem[item] = trnName;
                }

                // 2) Read window based on last read per item (UTC!).
                var startUtc = _lastUtcByItem.TryGetValue(item, out var lastUtc)
                    ? lastUtc.AddSeconds(-2)
                    : endUtc.AddMinutes(-60);

                var trn = await _ctApiService.GetTrnData(trnName, startUtc, endUtc);
                if (trn == null || trn.Count == 0)
                    continue;

                // Native Y-range for this item:
                // - base item: use base range (no scaling)
                // - other items: use attribute range if present, otherwise also base range (no scaling)
                double nativeMin = baseMin, nativeMax = baseMax;
                if (!item.Equals(baseItem, StringComparison.OrdinalIgnoreCase))
                {
                    if (TryGetYRangeForItem(item, out var aMin, out var aMax))
                    {
                        nativeMin = aMin;
                        nativeMax = aMax;
                    }
                }

                var points = trn
                    .Select(x =>
                    {
                        var raw = x.Value;

                        // Draw value (scaled to base axis if needed)
                        var plot = item.Equals(baseItem, StringComparison.OrdinalIgnoreCase)
                            ? raw
                            : MapToBase(raw, nativeMin, nativeMax, baseMin, baseMax);

                        return new TrendPoint
                        {
                            Series = item,
                            Time = DateTime.SpecifyKind(x.DateTime, DateTimeKind.Utc).ToLocalTime(),
                            RawValue = raw,
                            Value = plot
                        };
                    })
                    .OrderBy(p => p.Time)
                    .ToList();

                if (points.Count == 0)
                    continue;

                // 3) Apply to UI.
                await Dispatcher.InvokeAsync(() =>
                {
                    // Add only new points for this series.
                    var lastAdded = ParamTrendPoints
                        .Where(p => p.Series.Equals(item, StringComparison.OrdinalIgnoreCase))
                        .Select(p => p.Time)
                        .DefaultIfEmpty(DateTime.MinValue)
                        .Max();

                    foreach (var p in points)
                        if (p.Time > lastAdded)
                            ParamTrendPoints.Add(p);

                    // Y axis: base range (single axis for all series).
                    AxisYMin = baseMin;
                    AxisYMax = baseMax;

                    // --- Axis X + retention policy ---
                    // Live mode: keep only last N minutes and pin visible window to "now".
                    // History mode: do NOT change AxisXMin/AxisXMax (user controls it),
                    //              but we still update WholeRange to match loaded data.

                    var now = DateTime.Now;

                    if (_trendLiveMode)
                    {
                        AxisXMax = now;
                        AxisXMin = AxisXMax.AddMinutes(-TrendLiveWindowMinutes);

                        // WholeRange equals visible range in Live mode.
                        AxisXWholeMin = AxisXMin;
                        AxisXWholeMax = AxisXMax;

                        // Keep only the visible window.
                        var minKeep = AxisXMin;
                        for (int i = ParamTrendPoints.Count - 1; i >= 0; i--)
                            if (ParamTrendPoints[i].Time < minKeep)
                                ParamTrendPoints.RemoveAt(i);
                    }
                    else
                    {
                        // History mode: keep more data to support scrolling.
                        // WholeRange is based on loaded points.
                        UpdateAxisXWholeRangeFromPoints_NoThrow();

                        // Optional safety trimming.
                        TrimTrendPointsIfNeeded_NoThrow();
                    }

                    ParamStatusText = $"Trends={trendItems.Length}, Points={ParamTrendPoints.Count} | {now:HH:mm:ss} | {_paramReadCycles} cycles";
                });

                // 4) Update lastUtc for item (keep UTC from CtApi).
                _lastUtcByItem[item] = trn.Max(x => x.DateTime);
            }
        }

        /// <summary>
        /// Called from AIParamView when an end-user scrolls/zooms the chart.
        /// Switches the trend from Live mode to History mode and triggers auto-loading
        /// when the user reaches the left edge.
        /// </summary>
        public void OnParamChartUserRangeChanged(DateTime newMinLocal, DateTime newMaxLocal)
        {
            // Zoom/scroll events can fire very frequently.
            // Debounce to avoid launching too many history-load tasks.
            var nowUtc = DateTime.UtcNow;
            if (nowUtc - _trendLastNavUtc < TrendNavDebounce)
                return;

            _trendLastNavUtc = nowUtc;

            // User interaction means we stop pinning the axis to "now".
            _trendLiveMode = false;

            // Keep VM in sync with the current visible window.
            AxisXMin = newMinLocal;
            AxisXMax = newMaxLocal;

            // Start background auto-load (fire-and-forget).
            _ = MaybeLoadMoreTrendHistoryAsync(newMinLocal, newMaxLocal);
        }

        /// <summary>
        /// Auto-load older trend points when the user scrolls close to the left edge.
        /// </summary>
        private async Task MaybeLoadMoreTrendHistoryAsync(DateTime visibleMinLocal, DateTime visibleMaxLocal)
        {
            try
            {
                // We never block the UI thread here.
                await _trendGate.WaitAsync(CancellationToken.None);
                try
                {
                    // We need at least some data to know where the left edge is.
                    DateTime loadedMinLocal = DateTime.MinValue;
                    DateTime loadedMaxLocal = DateTime.MinValue;

                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (ParamTrendPoints.Count == 0)
                            return;

                        loadedMinLocal = ParamTrendPoints.Min(p => p.Time);
                        loadedMaxLocal = ParamTrendPoints.Max(p => p.Time);
                    });

                    if (loadedMinLocal == DateTime.MinValue || loadedMaxLocal == DateTime.MinValue)
                        return;

                    // If the user is close to the left edge of loaded data – load one more chunk.
                    var visSpan = visibleMaxLocal - visibleMinLocal;
                    if (visSpan <= TimeSpan.Zero)
                        visSpan = TimeSpan.FromMinutes(TrendLiveWindowMinutes);

                    var threshold = loadedMinLocal + TimeSpan.FromTicks((long)(visSpan.Ticks * 0.15));
                    if (visibleMinLocal > threshold)
                        return;

                    var toUtc = loadedMinLocal.ToUniversalTime();
                    var fromUtc = toUtc.AddMinutes(-TrendHistoryChunkMinutes);

                    await LoadTrendHistoryWindowAsync(fromUtc, toUtc, CancellationToken.None);

                    // Update WholeRange after loading.
                    await Dispatcher.InvokeAsync(() => UpdateAxisXWholeRangeFromPoints_NoThrow());
                }
                finally
                {
                    _trendGate.Release();
                }
            }
            catch
            {
                // History load must never break the UI/polling.
            }
        }

        /// <summary>
        /// Loads historical data for all configured trend series and merges it into ParamTrendPoints.
        /// IMPORTANT: This method does NOT touch AxisXMin/AxisXMax (user-visible window).
        /// </summary>
        private async Task LoadTrendHistoryWindowAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct)
        {
            var (equipName, _) = ResolveSelectedEquipForParam();
            if (string.IsNullOrWhiteSpace(equipName))
                return;

            // Prevent accidental mixing if the user switched equipment.
            if (!string.Equals(_trendEquipName, equipName, StringComparison.OrdinalIgnoreCase))
                return;

            var trendItems = GetTrendItemsFromModel(CurrentParamModel, "R");
            if (trendItems.Length == 0)
                trendItems = new[] { "R" };

            var baseItem = trendItems[0];
            if (!TryGetBaseYRange(baseItem, out var baseMin, out var baseMax))
            {
                baseMin = 0;
                baseMax = 1;
            }

            var newPoints = new List<TrendPoint>(capacity: 512);

            foreach (var item in trendItems)
            {
                ct.ThrowIfCancellationRequested();

                // Resolve TrendTagName for item (cached).
                if (!_trnNameByItem.TryGetValue(item, out var trnName) || string.IsNullOrWhiteSpace(trnName))
                {
                    trnName = await _equipmentService.GetTrnName(equipName, item);
                    if (string.IsNullOrWhiteSpace(trnName))
                        continue;

                    _trnNameByItem[item] = trnName;
                }

                var trn = await _ctApiService.GetTrnData(trnName, fromUtc, toUtc);
                if (trn == null || trn.Count == 0)
                    continue;

                // Native Y-range for this item.
                double nativeMin = baseMin, nativeMax = baseMax;
                if (!item.Equals(baseItem, StringComparison.OrdinalIgnoreCase))
                {
                    if (TryGetYRangeForItem(item, out var aMin, out var aMax))
                    {
                        nativeMin = aMin;
                        nativeMax = aMax;
                    }
                }

                foreach (var x in trn)
                {
                    var raw = x.Value;
                    var plot = item.Equals(baseItem, StringComparison.OrdinalIgnoreCase)
                        ? raw
                        : MapToBase(raw, nativeMin, nativeMax, baseMin, baseMax);

                    newPoints.Add(new TrendPoint
                    {
                        Series = item,
                        Time = DateTime.SpecifyKind(x.DateTime, DateTimeKind.Utc).ToLocalTime(),
                        RawValue = raw,
                        Value = plot,
                    });
                }
            }

            if (newPoints.Count == 0)
                return;

            // Merge into the bound collection on the UI thread.
            await Dispatcher.InvokeAsync(() =>
            {
                // De-duplicate by (Series, Time) and keep order by time.
                var all = ParamTrendPoints.Concat(newPoints);

                var merged = all
                    .GroupBy(p => (series: (p.Series ?? "").Trim().ToUpperInvariant(), time: p.Time))
                    .Select(g => g.First())
                    .OrderBy(p => p.Time)
                    .ThenBy(p => p.Series, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                ParamTrendPoints.Clear();
                foreach (var p in merged)
                    ParamTrendPoints.Add(p);

                UpdateAxisXWholeRangeFromPoints_NoThrow();
                TrimTrendPointsIfNeeded_NoThrow();
            });
        }

        /// <summary>
        /// Updates AxisXWholeMin/AxisXWholeMax from ParamTrendPoints.
        /// Must be called on UI thread.
        /// </summary>
        private void UpdateAxisXWholeRangeFromPoints_NoThrow()
        {
            if (ParamTrendPoints.Count == 0)
                return;

            AxisXWholeMin = ParamTrendPoints.Min(p => p.Time);
            AxisXWholeMax = ParamTrendPoints.Max(p => p.Time);
        }

        /// <summary>
        /// Safety trim for history mode.
        /// Must be called on UI thread.
        /// </summary>
        private void TrimTrendPointsIfNeeded_NoThrow()
        {
            if (TrendHistoryKeepHours <= 0)
                return;

            if (ParamTrendPoints.Count == 0)
                return;

            // Keep only last N hours relative to the newest loaded point.
            var newest = ParamTrendPoints.Max(p => p.Time);
            var cut = newest.AddHours(-TrendHistoryKeepHours);

            for (int i = ParamTrendPoints.Count - 1; i >= 0; i--)
                if (ParamTrendPoints[i].Time < cut)
                    ParamTrendPoints.RemoveAt(i);

            UpdateAxisXWholeRangeFromPoints_NoThrow();
        }

        // Получаем MinR/MaxR из текущей модели (через reflection)
        private bool TryGetModelScaleMinMax(out double scaleLo, out double scaleHi)
        {
            scaleLo = 0;
            scaleHi = 1;

            if (CurrentParamModel == null) return false;

            var t = CurrentParamModel.GetType();
            var pMinR = t.GetProperty("MinR");
            var pMaxR = t.GetProperty("MaxR");
            if (pMinR == null || pMaxR == null) return false;

            var vMin = pMinR.GetValue(CurrentParamModel);
            var vMax = pMaxR.GetValue(CurrentParamModel);
            if (vMin == null || vMax == null) return false;

            var a = Convert.ToDouble(vMin, CultureInfo.InvariantCulture);
            var b = Convert.ToDouble(vMax, CultureInfo.InvariantCulture);

            // защита от “переворота”
            scaleLo = Math.Min(a, b);
            scaleHi = Math.Max(a, b);
            return true;
        }


        /// <summary>
        /// Resolves the base Y-range (single axis) used for the whole trend chart.
        /// Priority:
        /// 1) Explicit range on the base item via [TrendItem(Item=..., YMin=..., YMax=...)]
        /// 2) Model-wide MinR/MaxR properties (typical for AI params)
        /// </summary>
        private bool TryGetBaseYRange(string baseItem, out double baseMin, out double baseMax)
        {
            // 1) If base series has an explicit attribute range — use it.
            if (!string.IsNullOrWhiteSpace(baseItem) &&
                TryGetYRangeForItem(baseItem, out baseMin, out baseMax))
                return true;

            // 2) Otherwise fall back to the model scale (MinR/MaxR).
            if (TryGetModelScaleMinMax(out baseMin, out baseMax))
                return true;

            baseMin = 0;
            baseMax = 1;
            return false;
        }

        public void ShowParamChart(bool reset = false)
        {
            if (reset)
                ResetTrendState(clearPoints: true);

            IsParamChartVisible = true;
            OnPropertyChanged(nameof(IsParamSettingsVisible)); // если используешь инверсию
        }

        public void ShowParamSettings()
        {
            IsParamChartVisible = false;
            OnPropertyChanged(nameof(IsParamSettingsVisible));
        }

        //private static string[] GetTrendItemsFromModel(object? model, params string[] fallback)
        //{
        //    if (model == null) return fallback;

        //    var t = model.GetType();
        //    var attr = (TrendItemsAttribute?)Attribute.GetCustomAttribute(t, typeof(TrendItemsAttribute), inherit: true);

        //    var items = attr?.Items;
        //    if (items == null || items.Length == 0) return fallback;

        //    return items.Where(s => !string.IsNullOrWhiteSpace(s))
        //                .Select(s => s.Trim())
        //                .ToArray();
        //}

        /// <summary>
        /// Tries to get an explicit Y-range for a specific series item from [TrendItem] attribute.
        /// Returns false if the item has no range defined.
        /// </summary>
        private bool TryGetYRangeForItem(string item, out double yMin, out double yMax)
        {
            yMin = 0; yMax = 1;

            if (CurrentParamModel == null) return false;

            var a = CurrentParamModel.GetType()
                .GetCustomAttributes(typeof(TrendItemAttribute), true)
                .OfType<TrendItemAttribute>()
                .FirstOrDefault(x => string.Equals(x.Item, item, StringComparison.OrdinalIgnoreCase));

            if (a == null || !a.HasYRange) return false;

            yMin = Math.Min(a.YMin, a.YMax);
            yMax = Math.Max(a.YMin, a.YMax);
            return true;
        }

        /// <summary>
        /// Maps a value from its native range (fromMin..fromMax) to the common/base Y range (baseMin..baseMax).
        /// We clamp to the base range so that series never disappear if the value goes slightly out of bounds.
        /// </summary>
        private static double MapToBase(double raw, double fromMin, double fromMax, double baseMin, double baseMax)
        {
            var fromSpan = fromMax - fromMin;
            if (Math.Abs(fromSpan) < 1e-12)
                return baseMin; // или (baseMin+baseMax)/2

            var t = (raw - fromMin) / fromSpan;

            // чтобы не улетало за шкалу (можно убрать, если хочешь видеть выходы)
            if (t < 0) t = 0;
            else if (t > 1) t = 1;

            return baseMin + t * (baseMax - baseMin);
        }

        /// <summary>
        /// Builds the ordered list of trend items (series keys) from [TrendItem] attributes on the model.
        /// Attribute order is respected; duplicates are removed while keeping the first occurrence.
        /// </summary>
        private static string[] GetTrendItemsFromModel(object? model, params string[] fallback)
        {
            if (model == null) return fallback;

            var items = model.GetType()
                .GetCustomAttributes(typeof(TrendItemAttribute), inherit: true)
                .OfType<TrendItemAttribute>()
                .Select(a => a.Item)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return items.Length > 0 ? items : fallback;
        }

        private static Dictionary<string, (Brush brush, double transparency)> GetSeriesStyleMap(object? model)
        {
            var map = new Dictionary<string, (Brush, double)>(StringComparer.OrdinalIgnoreCase);
            if (model == null) return map;

            var t = model.GetType();
            var attrs = t.GetCustomAttributes(typeof(TrendSeriesStyleAttribute), inherit: true).OfType<TrendSeriesStyleAttribute>();

            foreach (var a in attrs)
            {
                if (string.IsNullOrWhiteSpace(a.Item) || string.IsNullOrWhiteSpace(a.Color))
                    continue;

                Color c;
                try
                {
                    c = (Color)ColorConverter.ConvertFromString(a.Color);
                }
                catch { continue; }

                var brush = new SolidColorBrush(c);
                brush.Freeze();

                map[a.Item.Trim()] = (brush, Clamp01(a.Transparency));
            }

            return map;
        }

        private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);

        /// <summary>
        /// Вызывай это после биндинга данных у ChartControl (BoundDataChanged),
        /// чтобы раскрасить созданные авто-серии.
        /// </summary>
        public void ApplyTrendSeriesStyles(ChartControl chart)
        {
            if (chart?.Diagram is not XYDiagram2D d)
                return;

            var styleMap = GetSeriesStyleMap(CurrentParamModel);

            foreach (var s in d.Series)
            {
                var key = (s.DisplayName ?? s.Name ?? "").Trim();
                if (key.Length == 0) continue;

                if (!styleMap.TryGetValue(key, out var st))
                    continue;

                // 1) Заливка / Brush у SplineAreaSeries2D
                s.GetType().GetProperty("Brush")?.SetValue(s, st.brush);

                // 2) Transparency (DevExpress)
                var pTr = s.GetType().GetProperty("Transparency");
                if (pTr != null && pTr.CanWrite)
                {
                    if (pTr.PropertyType == typeof(double))
                        pTr.SetValue(s, st.transparency);
                    else if (pTr.PropertyType == typeof(float))
                        pTr.SetValue(s, (float)st.transparency);
                }

                // 3) Линия
                var propLineStyle = s.GetType().GetProperty("LineStyle");
                if (propLineStyle != null)
                {
                    var ls = propLineStyle.GetValue(s) ?? Activator.CreateInstance(propLineStyle.PropertyType);
                    if (ls != null)
                    {
                        ls.GetType().GetProperty("Brush")?.SetValue(ls, st.brush);
                        propLineStyle.SetValue(s, ls);
                    }
                }
                else
                {
                    // fallback (на всякий)
                    s.GetType().GetProperty("BorderBrush")?.SetValue(s, st.brush);
                }
            }
        }

        /// <summary>
        /// Switch trend chart back to Live mode:
        /// - pins X axis to "now - window .. now"
        /// - trims points to the live window (unless resetPoints=true)
        /// - keeps polling going as usual
        ///
        /// Called from the "Live" button on Param tab.
        /// </summary>
        public void SetParamChartLiveMode(bool resetPoints = false)
        {
            // Fire-and-forget: UI remains responsive.
            _ = SetParamChartLiveModeAsync(resetPoints);
        }

        private async Task SetParamChartLiveModeAsync(bool resetPoints)
        {
            try
            {
                await _trendGate.WaitAsync(CancellationToken.None);
                try
                {
                    // Back to Live mode.
                    _trendLiveMode = true;

                    var now = DateTime.Now;

                    // All collection / axis updates must run on UI thread.
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (resetPoints)
                        {
                            // Full reset (clears points + caches and resets axis ranges).
                            ResetTrendState(clearPoints: true);
                            return;
                        }

                        // Pin visible window to "now".
                        AxisXMax = now;
                        AxisXMin = AxisXMax.AddMinutes(-TrendLiveWindowMinutes);

                        // In Live mode whole range equals visible window (no scrollbars needed).
                        AxisXWholeMin = AxisXMin;
                        AxisXWholeMax = AxisXMax;

                        // Keep only points inside the live window to avoid memory growth.
                        var minKeep = AxisXMin;
                        for (int i = ParamTrendPoints.Count - 1; i >= 0; i--)
                        {
                            if (ParamTrendPoints[i].Time < minKeep)
                                ParamTrendPoints.RemoveAt(i);
                        }
                    });
                }
                finally
                {
                    _trendGate.Release();
                }
            }
            catch
            {
                // Switching mode must never break the UI.
            }
        }

        #endregion

        #region UiStatePersistence

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
