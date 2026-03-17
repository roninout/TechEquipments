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
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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
    public partial class MainWindow : ThemedWindow, INotifyPropertyChanged, IParamHost, IParamRefsHost, IDbHost, IQrHost, ISoeHost, IUiStateHost
    {
        private ParamController _paramController;
        private ParamWriteController _paramWriteController;
        private readonly ParamRefsController _paramRefs;
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
                if (ReferenceEquals(_selectedListBoxEquipment, value))
                    return;

                _selectedListBoxEquipment = value;
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
                RestoreOrSelectEquipmentAfterFilterChanged();

                _uiState.ScheduleSave();
                NotifyParamQrUiChanged();   // пересчитать Visibility кнопки Generate QR
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
                OnPropertyChanged(nameof(BottomStatusText));
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
                OnPropertyChanged(nameof(BottomStatusText));
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

                OnPropertyChanged(nameof(IsBottomStatusVisible));
                OnPropertyChanged(nameof(BottomStatusText));

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

                OnPropertyChanged(nameof(IsBottomStatusVisible));
                OnPropertyChanged(nameof(BottomStatusText));

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
                OnPropertyChanged(nameof(BottomStatusText));
            }
        }

        private bool _isCtApiConnected = true;
        public bool IsCtApiConnected
        {
            get => _isCtApiConnected;
            private set
            {
                if (_isCtApiConnected == value) return;
                _isCtApiConnected = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsBottomStatusVisible));
                OnPropertyChanged(nameof(BottomStatusText));
                OnPropertyChanged(nameof(BottomStatusBrush));
            }
        }

        private string _ctApiStatusText = "";

        /// <summary>
        /// Нижняя панель видна либо когда что-то грузится,
        /// либо когда потеряна связь с CtApi.
        /// </summary>
        public bool IsBottomStatusVisible => IsBottomLoading || !IsCtApiConnected;

        /// <summary>
        /// Если CtApi disconnected — показываем сообщение о связи.
        /// Иначе используем обычный BottomText.
        /// </summary>
        public string BottomStatusText =>
            !IsCtApiConnected && !string.IsNullOrWhiteSpace(_ctApiStatusText)
                ? _ctApiStatusText
                : BottomText;

        /// <summary>
        /// Красный цвет при потере связи, обычный — во всех остальных случаях.
        /// </summary>
        public Brush BottomStatusBrush => !IsCtApiConnected ? Brushes.Red : Brushes.Black;

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

                // Шапка Param
                OnPropertyChanged(nameof(CurrentParamChanel));
                OnPropertyChanged(nameof(IsCurrentParamChanelVisible));
            }
        }

        // polling
        private int _paramReadCycles;

        // overlay над центральной областью Param
        private bool _isParamCenterLoading;
        public bool IsParamCenterLoading
        {
            get => _isParamCenterLoading;
            set
            {
                if (_isParamCenterLoading == value)
                    return;

                _isParamCenterLoading = value;
                OnPropertyChanged();
            }
        }

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

        /// <summary>
        /// Определяет, поддерживает ли текущая модель конкретную страницу Param.
        /// Опираемся только на новую архитектуру IParamModel / SupportedPages.
        /// </summary>
        private bool CurrentParamSupportsPage(ParamSettingsPage page)
        {
            if (CurrentParamModel is not IParamModel paramModel)
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

            var (equipName, _) = ResolveSelectedEquipForParam();
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
                ParamStatusText = $"Param settings refresh error: {ex.Message}";
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
                IsParamCenterLoading = !mainDone;
                return;
            }

            var sectionDone =
                string.Equals(_lastSectionLoadedEquipName, key, StringComparison.OrdinalIgnoreCase) &&
                _lastSectionLoadedPage == page &&
                IsFinalParamLoadState(_lastSectionLoadedState);

            // При простом переключении страницы ждём только секцию
            if (!needMainModel)
            {
                IsParamCenterLoading = !sectionDone;
                return;
            }

            // При смене equipment ждём и модель, и секцию
            IsParamCenterLoading = !(mainDone && sectionDone);
        }

        /// <summary>
        /// Полностью останавливаем ожидание overlay.
        /// </summary>
        private void StopParamOverlayWait()
        {
            _pendingParamOverlayEquipName = null;
            _pendingParamOverlayPage = ParamSettingsPage.None;
            _pendingParamOverlayNeedsMainModel = false;
            IsParamCenterLoading = false;
        }

        /// <summary>
        /// Проверяем, можно ли уже скрывать overlay.
        /// </summary>
        private void TryFinishParamOverlayWait()
        {
            if (!IsParamCenterLoading)
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

        #region Ref

        // ====== refs (dynamic UI) ======

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

        public string? DryRunEquipName { get; private set; }
        public DryRunMotor? DryRunModel { get; private set; }

        public string? LinkedAtvEquipName { get; private set; }
        public AtvModel? LinkedAtvModel { get; private set; }

        #endregion

        #endregion

        public MainWindow(IEquipmentService equipmentService, IDbService dbService, IUserStateService stateService, ICtApiService ctApiService, IConfiguration config, IQrCodeService qrCodeService, IQrScannerService qrScannerService)
        {
            InitializeComponent();

            _equipmentService = equipmentService;
            _stateService = stateService;
            _ctApiService = ctApiService;

            _ctApiService.ConnectionStateChanged += OnCtApiConnectionStateChanged;
            Closed += (_, __) => _ctApiService.ConnectionStateChanged -= OnCtApiConnectionStateChanged;

            IsCtApiConnected = _ctApiService.IsConnectionAvailable;

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

            _paramController = new ParamController(_equipmentService, this, _ctApiService);

            _paramWriteController = new ParamWriteController(
                equipmentService: _equipmentService,
                ctApiService: _ctApiService,
                getSelectedTab: () => SelectedMainTab,
                resolveSelectedEquip: ResolveSelectedEquipForParam,
                resolveEquipNameForWrite: ResolveEquipNameForWrite,
                getSuppressWritesFromPolling: () => _suppressParamWritesFromPolling,
                getSuppressWritesFromUiRollback: () => _suppressParamWritesFromUiRollback,
                setSuppressWritesFromUiRollback: v => _suppressParamWritesFromUiRollback = v,
                paramRwGate: _paramRwGate,
                setParamReadResumeAtUtc: dt => _paramReadResumeAtUtc = dt,
                setBottomText: txt => ParamStatusText = txt,
                getOwnerWindow: () => this,
                endParamFieldEdit: EndParamFieldEdit
            );

            _paramRefs = new ParamRefsController(_equipmentService, _ctApiService, _config, this);

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
                IsEquipListLoading = false;

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
                IsCtApiConnected = isConnected;

                if (!isConnected)
                {
                    _ctApiStatusText = string.IsNullOrWhiteSpace(message)
                        ? "CtApi connection lost."
                        : message;

                    OnPropertyChanged(nameof(IsBottomStatusVisible));
                    OnPropertyChanged(nameof(BottomStatusText));
                    OnPropertyChanged(nameof(BottomStatusBrush));
                    return;
                }

                _ctApiStatusText = "";

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
                }
                finally
                {
                    _suppressEquipNameFromSelection = false;
                    _isApplyingFilterSelection = false;
                }

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

            // Если сейчас уже открыта вкладка Param —
            // сразу обновляем её, чтобы не оставалась пустая страница.
            if (!_uiState.IsRestoringState && SelectedMainTab == MainTabKind.Param)
                StartParamPolling();
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

                // Ждём обновление центра Param:
                // - главной модели
                // - и текущей активной секции, если сейчас открыта Settings-страница
                BeginParamOverlayWait(eq, CurrentParamSettingsPage, needMainModel: true);
            }
            else
            {
                StopParamOverlayWait();
            }

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
            => _paramRefs.ResetAreaIfTypeGroupChanged(newGroup);

        Task IParamHost.RefreshActiveParamSectionAsync(CancellationToken ct)
            => _paramRefs.RefreshActiveParamSectionAsync(ct);

        Task IParamHost.PollTrendOnceSafeAsync(CancellationToken ct)
            => _trendCtl.PollOnceSafeAsync(ct, txt => BottomText = txt);

        void IParamHost.NotifyMainParamLoaded(string equipName, ParamLoadState state)
            => NotifyMainParamLoadedCore(equipName, state);

        #endregion

        #region IParamRefsHost

        Dispatcher IParamRefsHost.Dispatcher => Dispatcher;

        MainTabKind IParamRefsHost.SelectedMainTab => SelectedMainTab;

        int IParamRefsHost.SelectedMainTabIndex
        {
            get => SelectedMainTabIndex;
            set => SelectedMainTabIndex = value;
        }

        SemaphoreSlim IParamRefsHost.ParamRwGate => _paramRwGate;

        ObservableCollection<EquipListBoxItem> IParamRefsHost.Equipments => Equipments;
        ObservableCollection<DiDoRefRow> IParamRefsHost.ParamDiRows => ParamDiRows;
        ObservableCollection<DiDoRefRow> IParamRefsHost.ParamDoRows => ParamDoRows;
        ObservableCollection<PlcRefRow> IParamRefsHost.ParamPlcRows => ParamPlcRows;

        ParamSettingsPage IParamRefsHost.CurrentParamSettingsPage
        {
            get => CurrentParamSettingsPage;
            set => CurrentParamSettingsPage = value;
        }

        string IParamRefsHost.EquipName
        {
            get => EquipName;
            set => EquipName = value;
        }

        string IParamRefsHost.SelectedStation
        {
            get => SelectedStation;
            set => SelectedStation = value;
        }

        EquipTypeGroup IParamRefsHost.SelectedTypeFilter
        {
            get => SelectedTypeFilter;
            set => SelectedTypeFilter = value;
        }

        void IParamRefsHost.SetDryRunState(string? equipName, DryRunMotor? model)
        {
            DryRunEquipName = equipName;
            DryRunModel = model;
            OnPropertyChanged(nameof(DryRunEquipName));
            OnPropertyChanged(nameof(DryRunModel));
        }

        (string equipName, string equipType) IParamRefsHost.ResolveSelectedEquipForParam()
            => ResolveSelectedEquipForParam();

        bool IParamRefsHost.IsEquipmentVisible(EquipListBoxItem item)
            => FilterEquipment(item);

        void IParamRefsHost.ApplyFilters()
            => ApplyFilters();

        void IParamRefsHost.DoIncrementalSearch(string text)
            => DoIncrementalSearch(text);

        void IParamRefsHost.ShowParamChart(bool reset)
            => ShowParamChart(reset);

        void IParamRefsHost.StartParamPolling()
            => StartParamPolling();

        void IParamRefsHost.BeginSuppressParamWritesFromRefresh()
        {
            _suppressParamWritesFromPolling = true;
        }

        void IParamRefsHost.EndSuppressParamWritesFromRefresh()
        {
            // Снимаем suppress чуть позже, когда WPF/DevExpress успеют применить binding
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _suppressParamWritesFromPolling = false;
            }), System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        void IParamRefsHost.SetLinkedAtvState(string? equipName, AtvModel? model)
        {
            LinkedAtvEquipName = equipName;
            LinkedAtvModel = model;

            OnPropertyChanged(nameof(LinkedAtvEquipName));
            OnPropertyChanged(nameof(LinkedAtvModel));
        }

        void IParamRefsHost.NotifySectionLoaded(string equipName, ParamSettingsPage page, ParamLoadState state)
            => NotifySectionLoadedCore(equipName, page, state);

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

        Dictionary<string, string> IUiStateHost.ExportRememberedEquipmentsByFilter() => ExportRememberedEquipmentsByFilter();

        void IUiStateHost.ImportRememberedEquipmentsByFilter(Dictionary<string, string>? state) => ImportRememberedEquipmentsByFilter(state);

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
                    var dryRunEquip = (DryRunEquipName ?? "").Trim();

                    if (!string.IsNullOrWhiteSpace(dryRunEquip))
                        return dryRunEquip;

                    return currentEquip;
                }

                // ATV секция внутри Motor работает с linked ATV equipment
                if (fe.DataContext is AtvParam)
                {
                    var (_, equipType) = ResolveSelectedEquipForParam();
                    var currentGroup = EquipTypeRegistry.GetGroup(equipType ?? "");

                    if (currentGroup == EquipTypeGroup.Motor &&
                        CurrentParamSettingsPage == ParamSettingsPage.Atv)
                    {
                        var linkedAtvEquip = (LinkedAtvEquipName ?? "").Trim();

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
                DXMessageBox.Show($"Failed to open settings window.\n\n{ex.Message}","Settings",MessageBoxButton.OK,MessageBoxImage.Error);
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
    }
}