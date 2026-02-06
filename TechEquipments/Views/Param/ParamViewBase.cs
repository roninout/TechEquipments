using DevExpress.Xpf.Editors;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace TechEquipments.Views.Param
{
    // Контракт: кто принимает события редактирования (у тебя это MainWindow)
    public interface IParamEditValueChangedHandler
    {
        void ParamEditable_EditValueChanged(object sender, EditValueChangedEventArgs e);
    }

    // Базовый класс для всех *ParamView
    public class ParamViewBase : UserControl
    {
        // Это имя должно совпадать с тем, что написано в XAML: EditValueChanged="ParamEditable_EditValueChanged"
        protected void ParamEditable_EditValueChanged(object sender, EditValueChangedEventArgs e)
        {
            if (DesignerProperties.GetIsInDesignMode(this))
                return;

            // Важно: DataContext у ParamView обычно = модель (AIParam),
            // поэтому ищем хост не через DataContext, а через Window.
            var host =
                Window.GetWindow(this) as IParamEditValueChangedHandler
                ?? Application.Current?.MainWindow as IParamEditValueChangedHandler;

            host?.ParamEditable_EditValueChanged(sender, e);
        }
    }
}