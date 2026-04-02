using DevExpress.Xpf.Editors;
using System.Windows;
using System.Windows.Input;
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

        private MainWindow? Host
        {
            get
            {
                if (Window.GetWindow(this) is MainWindow mw)
                    return mw;

                return Application.Current?.MainWindow as MainWindow;
            }
        }

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

        private async void CapturePhoto_Click(object sender, RoutedEventArgs e)
        {
            if (Host != null)
                await Host.Info_CapturePhotoFromCameraAsync();
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

        private async void DocumentSelectionChanged(object sender, DevExpress.Xpf.Editors.EditValueChangedEventArgs e)
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

        private async void PhotoLibraryEditValueChanged(object sender, EditValueChangedEventArgs e)
        {
            if (Host != null)
                await Host.Info_OnPhotoLibraryEditValueChangedAsync();
        }

        private async void DocumentLibraryEditValueChanged(object sender, EditValueChangedEventArgs e)
        {
            if (Host != null)
                await Host.Info_OnDocumentLibraryEditValueChangedAsync();
        }

        private async void PhotoThumbs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Host != null)
                await Host.Info_OnSelectedPhotoChangedAsync();
        }

        private void PhotoThumbItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not ListBoxItem item)
                return;

            item.IsSelected = true;
            item.Focus();

            // Важно: чтобы клик не "съедался" внутренним ImageEdit
            e.Handled = true;
        }
    }
}