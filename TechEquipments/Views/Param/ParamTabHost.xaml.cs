using System.Windows;
using System.Windows.Controls;

namespace TechEquipments.Views.Param
{
    /// <summary>
    /// Interaction logic for ParamTabHost.xaml
    /// </summary>
    public partial class ParamTabHost
    {
        public ParamTabHost()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Кнопка: генерация QR по текущему тексту поиска/выбранному оборудованию.
        /// </summary>
        private async void GenerateQr_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindow mw)
                await mw.Param_GenerateQrAsync();
        }

        /// <summary>
        /// Кнопка: сканирование QR камерой -> ExternalTag -> поиск -> запуск Param polling.
        /// </summary>
        private async void ScanQr_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainWindow mw)
                await mw.Param_ScanQrToExternalTagAndSearchAsync();
        }
    }
}
