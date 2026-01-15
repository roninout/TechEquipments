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

        #endregion

        public MainWindow(IEquipmentService equipmentService)
        {
            InitializeComponent();

            DataContext = this; // чтобы биндинг equipmentSOEDtos работал
            _equipmentService = equipmentService;
        }

        #region Grid

        // заполнения таблицы данными с отображением индикаторов загрузки
        private async Task LoadAndShowEquipDataAsync(string equipName)
        {
            if (IsLoading) return;

            try
            {
                IsLoading = true;

                // гасим предыдущий CTS
                _loadCts?.Cancel();
                _loadCts?.Dispose();
                _loadCts = new CancellationTokenSource();
                var ct = _loadCts.Token;

                LoadedCount = 0;
                CurrentCount = 0;
                CurrentTrendIndex = 0;
                CurrentTrendName = "";
                TotalTrends = 0;

                await Dispatcher.Yield(DispatcherPriority.Render);

                var allRows = await GetDataFromEquipAsync(equipName, ct);

                ct.ThrowIfCancellationRequested();

                equipmentSOEDtos.Clear();
                foreach (var r in allRows)
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
                IsLoading = false;
                _loadCts?.Dispose();
                _loadCts = null;
            }
        }

        private async Task<List<EquipmentSOEDto>> GetDataFromEquipAsync(string equipName, CancellationToken ct)
        {
            var model = await _equipmentService.GetEquipModelWithRef(equipName);
            if (model?.MainModel == null)
                return new List<EquipmentSOEDto>();

            ct.ThrowIfCancellationRequested();

            // общий список: main + refs
            var equipList = new List<EquipRefModel> { model.MainModel };

            if (model.RefEquipments != null && model.RefEquipments.Count > 0)
                equipList.AddRange(model.RefEquipments);

            // фильтр мусора + уникальность по TrnName
            equipList = equipList
                .Where(e => e != null && !string.IsNullOrWhiteSpace(e.TrnName))
                .GroupBy(e => e.TrnName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            TotalTrends = equipList.Count;

            var allRows = new List<EquipmentSOEDto>(capacity: TotalMax);

            for (int i = 0; i < equipList.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (allRows.Count >= TotalMax) break;

                var equip = equipList[i];

                CurrentTrendIndex = i + 1;
                CurrentTrendName = equip.TrnName;
                CurrentCount = 0;

                // прогресс по одному тренду + общий
                var progress = new Progress<int>(c =>
                {
                    CurrentCount = c;
                    LoadedCount = allRows.Count + c;
                });

                var rows = await _equipmentService.GetTrnByEquipment(
                    equip,
                    progress,
                    ct,
                    maxRows: PerTrendMax);

                if (rows != null && rows.Count > 0)
                {
                    int remaining = TotalMax - allRows.Count;
                    if (rows.Count > remaining)
                        allRows.AddRange(rows.Take(remaining));
                    else
                        allRows.AddRange(rows);
                }

                LoadedCount = allRows.Count;
            }

            ct.ThrowIfCancellationRequested();

            // сортировка по времени (новые сверху)
            allRows = allRows.OrderByDescending(r => r.TimeUtc).ToList();

            return allRows;
        }

        #endregion

        #region Buttons
        private async void SimpleButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadAndShowEquipDataAsync("S17.D02.VGA03_EL");
        }

        // кнопка отмены загрузку данных
        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _loadCts?.Cancel();
            CurrentTrendName = "Cancelling...";
        }
        #endregion

        #region PropertyChangedEvent
        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        #endregion

    }
}
