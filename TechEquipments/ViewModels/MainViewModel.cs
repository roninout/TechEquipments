using System;
using System.Windows.Media;

namespace TechEquipments.ViewModels
{
    /// <summary>
    /// Корневой ViewModel MainWindow.
    /// Содержит дочерние секции состояния и вычисляемые UI-свойства,
    /// чтобы MainWindow оставался только bridge/view-слоем.
    /// </summary>
    public sealed class MainViewModel : ObservableObject
    {
        public ShellViewModel Shell { get; } = new();
        public EquipmentListViewModel EquipmentList { get; } = new();
        public ParamViewModel Param { get; } = new();
        public InfoViewModel Info { get; } = new();
        public DatabaseViewModel Database { get; } = new();

        public MainViewModel()
        {
            Shell.PropertyChanged += (_, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(ShellViewModel.IsLoading):
                        Raise(nameof(CanMainAction));
                        break;

                    case nameof(ShellViewModel.UseParamAreaOverlay):
                    case nameof(ShellViewModel.IsParamCenterLoading):
                    case nameof(ShellViewModel.ParamStatusText):
                    case nameof(ShellViewModel.BottomText):
                    case nameof(ShellViewModel.IsCtApiConnected):
                    case nameof(ShellViewModel.CtApiStatusText):
                        RaiseBottomBarComputed();
                        break;
                }
            };

            EquipmentList.PropertyChanged += (_, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(EquipmentListViewModel.EquipListDone):
                    case nameof(EquipmentListViewModel.EquipListTotal):
                    case nameof(EquipmentListViewModel.IsEquipListLoading):
                        RaiseBottomBarComputed();
                        break;
                }
            };

            EquipmentList.Equipments.CollectionChanged += (_, __) =>
            {
                Raise(nameof(EquipListText), nameof(BottomText), nameof(BottomStatusText));
            };

            Database.PropertyChanged += (_, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(DatabaseViewModel.IsDbLoading):
                        Raise(nameof(CanMainAction));
                        RaiseBottomBarComputed();
                        break;

                    case nameof(DatabaseViewModel.IsDbConnected):
                        Raise(nameof(CanMainAction));
                        break;
                }
            };
        }

        private MainTabKind _selectedMainTab = MainTabKind.Param;
        public MainTabKind SelectedMainTab
        {
            get => _selectedMainTab;
            set
            {
                if (!SetProperty(ref _selectedMainTab, value))
                    return;

                Raise(nameof(SelectedMainTabIndex));
                RaiseSelectedTabComputed();
                RaiseBottomBarComputed();
            }
        }

        public int SelectedMainTabIndex
        {
            get => (int)SelectedMainTab;
            set
            {
                var tab = (MainTabKind)value;
                if (SelectedMainTab == tab)
                    return;

                SelectedMainTab = tab;
            }
        }

        // ===== toolbar / tabs =====

        public bool IsDbTabSelected => SelectedMainTab is MainTabKind.OperationActions or MainTabKind.AlarmHistory;

        public string MainActionButtonText => SelectedMainTab switch
        {
            MainTabKind.SOE => "Load",
            MainTabKind.OperationActions => "Search",
            MainTabKind.AlarmHistory => "Search",
            MainTabKind.Info => "",
            _ => "Run",
        };

        public bool CanMainAction => SelectedMainTab switch
        {
            MainTabKind.SOE => !Shell.IsLoading,
            MainTabKind.Info => false,
            MainTabKind.Param => false,
            _ => Database.IsDbConnected && !Database.IsDbLoading,
        };

        public bool ShowToolbarScanQrButton => SelectedMainTab == MainTabKind.Param;

        public bool ShowMainActionButton => SelectedMainTab != MainTabKind.Param && SelectedMainTab != MainTabKind.Info;

        // ===== bottom bar =====

        public int EquipListMax => Math.Max(1, EquipmentList.EquipListTotal);

        public string EquipListText =>
            EquipmentList.IsEquipListLoading
                ? $"Loading equipments: {EquipmentList.EquipListDone}/{EquipmentList.EquipListTotal}"
                : $"Equipments: {EquipmentList.Equipments.Count}";

        public string ParamBottomLoadingText =>
            string.IsNullOrWhiteSpace(Shell.ParamStatusText)
                ? "Updating data..."
                : Shell.ParamStatusText;

        public bool IsBottomLoading =>
            EquipmentList.IsEquipListLoading ||
            Database.IsDbLoading ||
            (!Shell.UseParamAreaOverlay &&
             SelectedMainTab == MainTabKind.Param &&
             Shell.IsParamCenterLoading);

        public string BottomText
        {
            get
            {
                if (EquipmentList.IsEquipListLoading)
                    return EquipListText;

                if (!Shell.UseParamAreaOverlay &&
                    SelectedMainTab == MainTabKind.Param &&
                    Shell.IsParamCenterLoading)
                {
                    return ParamBottomLoadingText;
                }

                return Shell.BottomText;
            }
        }

        public bool IsBottomStatusVisible => IsBottomLoading || !Shell.IsCtApiConnected;

        public string BottomStatusText =>
            !Shell.IsCtApiConnected && !string.IsNullOrWhiteSpace(Shell.CtApiStatusText)
                ? Shell.CtApiStatusText
                : BottomText;

        public Brush BottomStatusBrush =>
            !Shell.IsCtApiConnected ? Brushes.Red : Brushes.Black;

        public bool IsBottomProgressVisible =>
            EquipmentList.IsEquipListLoading ||
            Database.IsDbLoading ||
            (!Shell.UseParamAreaOverlay &&
             SelectedMainTab == MainTabKind.Param &&
             Shell.IsParamCenterLoading);

        public bool BottomProgressIsIndeterminate =>
            ((!Shell.UseParamAreaOverlay &&
              SelectedMainTab == MainTabKind.Param &&
              Shell.IsParamCenterLoading &&
              !EquipmentList.IsEquipListLoading))
            || (Database.IsDbLoading && !EquipmentList.IsEquipListLoading);

        public int BottomProgressMaximum => EquipmentList.IsEquipListLoading ? EquipListMax : 100;

        public int BottomProgressValue => EquipmentList.IsEquipListLoading ? EquipmentList.EquipListDone : 0;

        private void RaiseSelectedTabComputed()
        {
            Raise(
                nameof(IsDbTabSelected),
                nameof(MainActionButtonText),
                nameof(CanMainAction),
                nameof(ShowToolbarScanQrButton),
                nameof(ShowMainActionButton));
        }

        private void RaiseBottomBarComputed()
        {
            Raise(
                nameof(EquipListMax),
                nameof(EquipListText),
                nameof(ParamBottomLoadingText),
                nameof(IsBottomLoading),
                nameof(BottomText),
                nameof(IsBottomStatusVisible),
                nameof(BottomStatusText),
                nameof(BottomStatusBrush),
                nameof(IsBottomProgressVisible),
                nameof(BottomProgressIsIndeterminate),
                nameof(BottomProgressMaximum),
                nameof(BottomProgressValue));
        }
    }
}