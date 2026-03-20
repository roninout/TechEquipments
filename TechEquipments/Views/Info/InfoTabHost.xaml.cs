using System.Windows;
using System.Windows.Controls;

namespace TechEquipments.Views.Info
{
    /// <summary>
    /// Interaction logic for InfoTabHost.xaml
    /// </summary>
    public partial class InfoTabHost : UserControl
    {
        public InfoTabHost()
        {
            InitializeComponent();
        }

        private MainWindow? Host =>
            Window.GetWindow(this) as MainWindow
            ?? Application.Current?.MainWindow as MainWindow;

        private void Edit_Click(object sender, RoutedEventArgs e)
        {
            Host?.Info_BeginEdit();
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            if (Host != null)
                await Host.Info_SaveAsync();
        }

        private async void LoadPdf_Click(object sender, RoutedEventArgs e)
        {
            if (Host != null)
                await Host.Info_LoadPdfFromFileAsync();
        }

        private void ClearPdf_Click(object sender, RoutedEventArgs e)
        {
            Host?.Info_ClearPdf();
        }
    }
}