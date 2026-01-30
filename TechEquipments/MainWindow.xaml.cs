using CtApi;
using DevExpress.Xpf.Core;
using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
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

                if (!_restoringUiState)
                {
                    ScheduleSearch(_equipName);   // твой debounce поиска
                    ScheduleUiStateSave();        // debounce сохранения состояния
                }
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

                if (!_restoringUiState)
                    ScheduleUiStateSave();
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

                if (!_restoringUiState)
                    ScheduleUiStateSave();
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

                _dbCts?.Cancel(); // отменяем предыдущую DB-загрузку при смене вкладки

                // авто-загрузка при переходе на DB вкладки
                _ = LoadCurrentDbTabAsync(force: false);

                if (!_restoringUiState)
                    ScheduleUiStateSave();
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

                if (!_restoringUiState)
                    ScheduleUiStateSave();
            }
        }

        // Дебаунс-таймер автоперезагрузки DB при смене даты (как поиск)
        private DispatcherTimer _dbReloadTimer;

        /// <summary>Текущая вкладка как enum (задел на будущие вкладки)</summary>
        public MainTabKind SelectedMainTab => (MainTabKind)SelectedMainTabIndex;

        /// <summary>Показывать DateEdit только на DB вкладках</summary>
        public bool IsDbTabSelected => SelectedMainTab != MainTabKind.SOE;

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

        #region UI state persistence (appsettings.json)

        private const string UiStateSectionName = "UiState";

        // Путь к appsettings.json в папке запуска (обычно bin\Debug\net6.0-windows\)
        private string _appSettingsPath = "";

        // Защита от параллельных записей файла
        private readonly SemaphoreSlim _uiStateGate = new(1, 1);

        // Debounce, чтобы не писать файл на каждый символ
        private DispatcherTimer _uiStateSaveTimer = null!;

        // Флаг, чтобы во время восстановления состояния не триггерить автопоиск/автозагрузку/автосохранение
        private bool _restoringUiState;

        #endregion

        public MainWindow(IEquipmentService equipmentService, IDbService dbService)
        {
            InitializeComponent();

            _equipmentService = equipmentService;
            _dbService = dbService;

            // DataContext на себя: используется во всём XAML (binding)
            DataContext = this;

            Loaded += async (_, __) =>
            {
                _layoutReady = true;
                InitLeftPaneState();

                // Параллельный старт:
                _ = StartupLoadFromExternalTagAsync(); // SOE - сразу
                _ = LoadEquipmentsListAsync();         // список слева - параллельно

                await CheckDbAsync();                  // проверка подключения к DB
            };

            InitEquipmentsView();
            InitSearchTimer();
            InitDbReloadTimer();
            InitUiStatePersistence();
        }

        #region Startup loading

        /// <summary>
        /// Старт: читает внешний тег (имя оборудования), подставляет в поиск
        /// и загружает SOE.
        /// </summary>
        private async Task StartupLoadFromExternalTagAsync()
        {
            try
            {
                var eqFromTag = await _equipmentService.GetExternalTagAsync(CancellationToken.None);

                if (string.IsNullOrWhiteSpace(eqFromTag) ||
                    eqFromTag.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                    return;

                EquipName = eqFromTag;

                // Пытаемся выделить в списке (если список ещё не загружен - выделим позже)
                DoIncrementalSearch(EquipName);

                // Загружаем SOE сразу
                await LoadAndShowEquipDataAsync(EquipName);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                DXMessageBox.Show(this, ex.ToString(), "Startup tag error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

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

        /// <summary>
        /// Включает восстановление/сохранение UI-состояния в appsettings.json
        /// </summary>
        private void InitUiStatePersistence()
        {
            _appSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

            // Debounce сохранения
            _uiStateSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _uiStateSaveTimer.Tick += async (_, __) =>
            {
                _uiStateSaveTimer.Stop();
                await SaveUiStateToAppSettingsAsync();
            };

            // Восстанавливаем значения (EquipName/Station/Type/Date/Tab)
            RestoreUiStateFromAppSettings();

            // На закрытии — финальное сохранение (на всякий)
            Closing += async (_, __) => await SaveUiStateToAppSettingsAsync();
        }

        /// <summary>
        /// Восстановление состояния (если ExternalTag пустой — оно уже будет подставлено)
        /// </summary>
        private void RestoreUiStateFromAppSettings()
        {
            try
            {
                if (!File.Exists(_appSettingsPath))
                    return;

                var root = JsonNode.Parse(File.ReadAllText(_appSettingsPath)) as JsonObject;
                var ui = root?[UiStateSectionName] as JsonObject;
                if (ui == null) return;

                _restoringUiState = true;
                try
                {
                    // EquipName
                    var equip = ui["EquipName"]?.GetValue<string>()?.Trim();
                    if (!string.IsNullOrWhiteSpace(equip))
                        EquipName = equip;

                    // Station
                    var station = ui["SelectedStation"]?.GetValue<string>()?.Trim();
                    if (!string.IsNullOrWhiteSpace(station))
                        SelectedStation = station;

                    // Type
                    var typeStr = ui["SelectedTypeFilter"]?.GetValue<string>()?.Trim();
                    if (!string.IsNullOrWhiteSpace(typeStr) &&
                        Enum.TryParse(typeStr, ignoreCase: true, out EquipTypeGroup tg))
                    {
                        SelectedTypeFilter = tg;
                    }

                    // DbDate (yyyy-MM-dd)
                    var dateStr = ui["DbDate"]?.GetValue<string>()?.Trim();
                    if (!string.IsNullOrWhiteSpace(dateStr) &&
                        DateTime.TryParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out var dt))
                    {
                        DbDate = dt.Date;
                    }

                    // Tab index
                    if (ui["SelectedMainTabIndex"] is JsonValue v && v.TryGetValue<int>(out var tabIndex))
                    {
                        if (tabIndex >= 0 && tabIndex <= 10) // на будущее вкладки
                            SelectedMainTabIndex = tabIndex;
                    }
                }
                finally
                {
                    _restoringUiState = false;
                }
            }
            catch
            {
                // сознательно молча: состояние не критично
            }
        }

        private void ScheduleUiStateSave()
        {
            if (_restoringUiState) return;
            if (_uiStateSaveTimer == null) return;

            _uiStateSaveTimer.Stop();
            _uiStateSaveTimer.Start();
        }

        private async Task SaveUiStateToAppSettingsAsync()
        {
            if (_restoringUiState) return;

            await _uiStateGate.WaitAsync();
            try
            {
                JsonObject root;

                if (File.Exists(_appSettingsPath))
                {
                    root = (JsonNode.Parse(await File.ReadAllTextAsync(_appSettingsPath)) as JsonObject) ?? new JsonObject();
                }
                else
                {
                    root = new JsonObject();
                }

                var ui = (root[UiStateSectionName] as JsonObject) ?? new JsonObject();

                ui["EquipName"] = (EquipName ?? "").Trim();
                ui["SelectedStation"] = (SelectedStation ?? "All").Trim();
                ui["SelectedTypeFilter"] = SelectedTypeFilter.ToString();
                ui["DbDate"] = DbDate.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                ui["SelectedMainTabIndex"] = SelectedMainTabIndex;

                root[UiStateSectionName] = ui;

                var options = new JsonSerializerOptions { WriteIndented = true };
                await File.WriteAllTextAsync(_appSettingsPath, root.ToJsonString(options));
            }
            catch
            {
                // если нет прав на запись — просто игнорируем
            }
            finally
            {
                _uiStateGate.Release();
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
            if (_suppressEquipNameFromSelection)
                return;

            if (SearchTextEdit?.IsKeyboardFocusWithin == true)
                return;

            if (SelectedListBoxEquipment?.Equipment is string eq && !string.IsNullOrWhiteSpace(eq))
                EquipName = eq;
        }

        /// <summary>
        /// Двойной клик по списку: сразу загружает SOE.
        /// </summary>
        private async void Equipments_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (IsLoading) return;

            if (SelectedListBoxEquipment?.Equipment is string eq && !string.IsNullOrWhiteSpace(eq))
            {
                EquipName = eq;
                await LoadAndShowEquipDataAsync(eq);
            }
        }

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

        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        #endregion
    }
}
