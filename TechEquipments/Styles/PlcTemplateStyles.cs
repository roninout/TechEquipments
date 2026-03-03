using DevExpress.Xpf.Editors;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace TechEquipments.Styles
{
    /// <summary>
    /// Code-behind нужен, потому что внутри DataTemplate используются обработчики событий
    /// (PreviewKeyDown/EditValueChanged/Click).
    /// Здесь мы просто форвардим события в MainWindow, как ты делал раньше.
    /// </summary>
    public partial class PlcTemplateStyles : ResourceDictionary
    {
        public PlcTemplateStyles()
        {
            InitializeComponent();
        }

        private static MainWindow? GetHost(object sender)
        {
            var dep = sender as DependencyObject;
            var win = dep != null ? Window.GetWindow(dep) : Application.Current?.MainWindow;
            return win as MainWindow ?? Application.Current?.MainWindow as MainWindow;
        }

        private void ParamEditable_EditValueChanged(object sender, KeyEventArgs e)
        {
            if (DesignerProperties.GetIsInDesignMode(new DependencyObject()))
                return;

            GetHost(sender)?.ParamEditable_EditValueChanged(sender, e);
        }

        private void ParamEditable_EditValueChanged(object sender, EditValueChangedEventArgs e)
        {
            if (DesignerProperties.GetIsInDesignMode(new DependencyObject()))
                return;

            GetHost(sender)?.ParamEditable_EditValueChanged(sender, e);
        }

        private void Plc_SimpleButton_Click(object sender, RoutedEventArgs e)
        {
            if (DesignerProperties.GetIsInDesignMode(new DependencyObject()))
                return;

            if (sender is FrameworkElement fe && fe.Tag is PlcRefRow row)
            {
                // Пишем "1" как команду
                GetHost(sender)?.ParamPlc_WriteFromUi(row, 1);
            }
        }

        private void Plc_ToggleClick(object sender, RoutedEventArgs e)
        {
            var host = GetHost(sender);
            if (host == null)
                return;

            if (sender is not ToggleButton tb || tb.Tag is not PlcRefRow row)
                return;

            // новое состояние после клика
            bool newState = tb.IsChecked == true;

            // ✅ пишем 1/0
            host.ParamPlc_WriteFromUi(row, newState);
        }
    }
}