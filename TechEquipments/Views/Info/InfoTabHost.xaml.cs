using System.Windows;
using System.Windows.Controls;
using DevExpress.Xpf.Editors;

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

        private async void LoadPhoto_Click(object sender, RoutedEventArgs e)
        {
            if (Host != null)
                await Host.Info_LoadPhotoFilesAsync();
        }

        private void RemovePhoto_Click(object sender, RoutedEventArgs e)
        {
            Host?.Info_RemoveSelectedPhoto();
        }

        private async void LoadDocument_Click(object sender, RoutedEventArgs e)
        {
            if (Host != null)
                await Host.Info_LoadCurrentDocumentFilesAsync();
        }

        private async void RemoveDocument_Click(object sender, RoutedEventArgs e)
        {
            if (Host != null)
                await Host.Info_RemoveCurrentDocumentAsync();
        }

        private async void ExportPdf_Click(object sender, RoutedEventArgs e)
        {
            if (Host != null)
                await Host.Info_ExportCurrentDocumentAsync();
        }

        private async void DocumentSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Host != null)
                await Host.Info_OnCurrentDocumentSelectionChangedAsync();
        }

        private async void PageButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not InfoPageKind page)
                return;

            if (Host != null)
                await Host.ShowInfoPageAsync(page);
        }

        private void PhotoLibraryEditValueChanged(object sender, EditValueChangedEventArgs e)
        {
            Host?.Info_OnPhotoLibraryEditValueChanged();
        }

        private async void DocumentLibraryEditValueChanged(object sender, EditValueChangedEventArgs e)
        {
            if (Host != null)
                await Host.Info_OnDocumentLibraryEditValueChangedAsync();
        }
    }
}