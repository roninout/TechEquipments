namespace TechEquipments.ViewModels
{
    /// <summary>
    /// Корневой ViewModel MainWindow.
    /// Содержит дочерние секции состояния.
    /// </summary>
    public sealed class MainViewModel : ObservableObject
    {
        public ShellViewModel Shell { get; } = new();
        public EquipmentListViewModel EquipmentList { get; } = new();
        public ParamViewModel Param { get; } = new();
        public InfoViewModel Info { get; } = new();
        public DatabaseViewModel Database { get; } = new();

        private MainTabKind _selectedMainTab = MainTabKind.Param;
        public MainTabKind SelectedMainTab
        {
            get => _selectedMainTab;
            set
            {
                if (!SetProperty(ref _selectedMainTab, value))
                    return;

                Raise(nameof(SelectedMainTabIndex));
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
    }
}