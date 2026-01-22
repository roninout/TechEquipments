using CtApi;
using DevExpress.Data.Extensions;
using DevExpress.Xpf.Core;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;
using static TechEquipments.IEquipmentService;

namespace TechEquipments
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : ThemedWindow, INotifyPropertyChanged
    {
        #region Fields
        private readonly IEquipmentService _equipmentService;

        public ObservableCollection<EquipmentSOEDto> equipmentSOEDtos { get; } = new();
        public ObservableCollection<EquipListBoxItem> Equipments { get; } = new();

        private readonly SemaphoreSlim _loadGate = new(1, 1);

        private string _equipName = "S17.D02.VGA03_EL";
        public string EquipName
        {
            get => _equipName;
            set
            {
                if (_equipName == value) return;
                _equipName = value;
                OnPropertyChanged();
            }
        }

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            private set
            {
                if (_isLoading == value) return;
                _isLoading = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsNotLoading));
                OnPropertyChanged(nameof(LoadingText));
            }
        }

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
            set { _currentCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(LoadingText)); }
        }

        private int _totalTrends;
        public int TotalTrends
        {
            get => _totalTrends;
            set { _totalTrends = value; OnPropertyChanged(); OnPropertyChanged(nameof(LoadingText)); }
        }

        private int _currentTrendIndex;
        public int CurrentTrendIndex
        {
            get => _currentTrendIndex;
            set { _currentTrendIndex = value; OnPropertyChanged(); OnPropertyChanged(nameof(LoadingText)); }
        }

        private string _currentTrendName = "";
        public string CurrentTrendName
        {
            get => _currentTrendName;
            set { _currentTrendName = value; OnPropertyChanged(); OnPropertyChanged(nameof(LoadingText)); }
        }

        // сколько максимум читаем из ОДНОГО тренда (ускоряет)
        public int PerTrendMax => 1000;

        // общий лимит (ускоряет)
        public int TotalMax => 100;

        //public string LoadingText => IsLoading ? $"Loading data from trend... {LoadedCount}" : "";
        public string LoadingText => IsLoading 
            ? $"Trend {CurrentTrendIndex}/{TotalTrends}: {CurrentTrendName}\nRows: {LoadedCount}/{TotalMax} (current {CurrentCount}/{PerTrendMax})"
            : "";

        private CancellationTokenSource? _loadCts;


        //private EquipRefModel? _selectedEquipment;
        //public EquipRefModel? SelectedEquipment
        //{
        //    get => _selectedEquipment;
        //    set { _selectedEquipment = value; OnPropertyChanged(); }
        //}

        private bool _layoutReady;
        private GridLength _leftSavedWidth = new GridLength(260);

        private EquipListBoxItem? _selectedListBoxEquipment;
        public EquipListBoxItem? SelectedListBoxEquipment
        {
            get => _selectedListBoxEquipment;
            set { _selectedListBoxEquipment = value; OnPropertyChanged(); }
        }

        private CancellationTokenSource? _equipListCts;

        #endregion

        public MainWindow(IEquipmentService equipmentService)
        {
            InitializeComponent();

            DataContext = this; // чтобы биндинг equipmentSOEDtos работал
            _equipmentService = equipmentService;

            Loaded += (_, __) =>
            {
                _layoutReady = true;

                // не await — чтобы окно НЕ зависало
                _ = LoadEquipmentsListAsync();

                // стартовое состояние: показываем левую панель
                LeftPaneToggle.IsChecked = false;
                ApplyLeftPane(false);
            };
        }

        #region ListBox

        private async Task LoadEquipmentsListAsync()
        {
            // гасим прошлую загрузку списка
            _equipListCts?.Cancel();
            _equipListCts?.Dispose();
            _equipListCts = new CancellationTokenSource();
            var ct = _equipListCts.Token;

            try
            {
                // дать окну отрисоваться
                await Dispatcher.Yield(DispatcherPriority.Background);

                var items = await _equipmentService.GetAllEquipmentsAsync(ct);

                // мы уже на UI-потоке (await вернёт сюда), можно обновлять ObservableCollection
                Equipments.Clear();
                foreach (var it in items)
                    Equipments.Add(it);
            }
            catch (OperationCanceledException)
            {
                // ок
            }
            catch (Exception ex)
            {
                DXMessageBox.Show(this, ex.ToString(), "Equip list error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Equipments_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SelectedListBoxEquipment?.Equipment is string eq && !string.IsNullOrWhiteSpace(eq))
                EquipName = eq; // просто подставили в поиск
        }

        private async void Equipments_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (IsLoading) return;

            if (SelectedListBoxEquipment?.Equipment is string eq && !string.IsNullOrWhiteSpace(eq))
            {
                EquipName = eq;                 // подставили
                await LoadAndShowEquipDataAsync(eq); // и загрузили
            }
        }

        #endregion

        #region Grid

        // заполнения таблицы данными с отображением индикаторов загрузки
        private async Task LoadAndShowEquipDataAsync(string equipName)
        {
            var name = (equipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name))
                return;

            // отменяем предыдущую загрузку (если была)
            try { _loadCts?.Cancel(); } catch { }

            await _loadGate.WaitAsync();

            CancellationTokenSource? myCts = null;

            try
            {
                // создаём новый CTS (и сохраняем ссылку именно на "свою" загрузку)
                _loadCts?.Dispose();
                myCts = new CancellationTokenSource();
                _loadCts = myCts;
                var ct = myCts.Token;

                // включаем индикацию (даже если до этого IsLoading уже был true — ок)
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
                DXMessageBox.Show(this, ex.ToString(), "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // ВАЖНО: выключаем IsLoading только если это всё ещё "наша" актуальная загрузка
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

        // кнопка отмены загрузку данных
        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _loadCts?.Cancel();
            CurrentTrendName = "Cancelling...";
        }

        private async void Load_Click(object sender, RoutedEventArgs e)
        {
            // если хочешь грузить выбранный элемент из ListBox:
            var equipName = (EquipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(equipName))
                return;

            await LoadAndShowEquipDataAsync(equipName);
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _loadCts?.Cancel();
        }

        private void LeftPaneToggle_Click(object sender, RoutedEventArgs e)
        {
            if (!_layoutReady) return;

            bool show = LeftPaneToggle.IsChecked == true;
            ApplyLeftPane(show);
        }

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

        #endregion

        #region PropertyChangedEvent
        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        #endregion

    }
}
