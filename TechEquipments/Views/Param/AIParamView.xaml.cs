using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
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

namespace TechEquipments.Views.Param
{
    /// <summary>
    /// Interaction logic for AIParamView.xaml
    /// </summary>
    public partial class AIParamView : UserControl
    {
        public AIParamView()
        {
            InitializeComponent();
        }

        // если в XAML навешиваешь EditValueChanged="ParamEditable_EditValueChanged"
        //// то обработчик ДОЛЖЕН существовать
        private void ParamEditable_EditValueChanged(object sender, DevExpress.Xpf.Editors.EditValueChangedEventArgs e)
        {
            if (DesignerProperties.GetIsInDesignMode(this))
                return;

            var host = Window.GetWindow(this) as MainWindow ?? Application.Current?.MainWindow as MainWindow;
            host?.ParamEditable_EditValueChanged(sender, e);
        }

        private void ParamEditable_EditValueChanged(object sender, KeyEventArgs e)
        {
            if (DesignerProperties.GetIsInDesignMode(this))
                return;

            var host = Window.GetWindow(this) as MainWindow ?? Application.Current?.MainWindow as MainWindow;
            host?.ParamEditable_EditValueChanged(sender, e);
        }

        private void ChartsButton_Click(object sender, RoutedEventArgs e)
        {
            if (DesignerProperties.GetIsInDesignMode(this))
                return;

            var host = Window.GetWindow(this) as MainWindow ?? Application.Current?.MainWindow as MainWindow;
            host?.ToggleParamChart();
        }
    }
}
