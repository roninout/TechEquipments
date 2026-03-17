using DevExpress.Xpf.Charts;
using DevExpress.Xpf.Editors;
using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace TechEquipments.Views.Param
{
    /// <summary>
    /// Общий базовый класс для ParamView.
    ///
    /// Что сюда вынесено:
    /// - прокси записи в MainWindow (EditValueChanged / Enter / Toggle);
    /// - общая логика графика (Live, Crosshair, BoundDataChanged, Scroll, Zoom);
    /// - общий контроль фокуса редакторов;
    /// - общие Man_Open / Man_Close.
    ///
    /// Благодаря этому конкретные View содержат только свою уникальную логику.
    /// </summary>
    public class ParamViewBase : UserControl
    {
        public ParamViewBase()
        {
            // Ловим фокус всех вложенных редакторов
            AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnAnyEditorGotFocus), true);
            AddHandler(Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnAnyEditorLostFocus), true);
        }

        /// <summary>
        /// Удобный доступ к MainWindow.
        /// </summary>
        protected MainWindow? Host => Window.GetWindow(this) as MainWindow ?? Application.Current?.MainWindow as MainWindow;

        /// <summary>
        /// Прокси DevExpress EditValueChanged в MainWindow.
        /// </summary>
        public void ParamEditable_EditValueChanged(object sender, EditValueChangedEventArgs e)
        {
            if (DesignerProperties.GetIsInDesignMode(this))
                return;

            Host?.ParamEditable_EditValueChanged(sender, e);
        }

        /// <summary>
        /// Прокси PreviewKeyDown в MainWindow.
        /// В MainWindow запись идёт только по Enter.
        /// </summary>
        public void ParamEditable_EditValueChanged(object sender, KeyEventArgs e)
        {
            if (DesignerProperties.GetIsInDesignMode(this))
                return;

            Host?.ParamEditable_EditValueChanged(sender, e);
        }

        /// <summary>
        /// Общий обработчик ToggleButton / CheckEdit.
        /// Пишем значение по Tag через MainWindow.
        /// </summary>
        public void ParamEditable_ToggleChanged(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton tb)
                return;

            Host?.ParamEditable_WriteFromUi(tb.Tag as string, tb.IsChecked, !tb.IsChecked);
        }

        /// <summary>
        /// Показать график через единую точку MainWindow.
        /// Важно: одновременно сбрасываем CurrentParamSettingsPage = None.
        /// </summary>
        public void ShowChartButton_Click(object sender, RoutedEventArgs e)
        {
            if (DesignerProperties.GetIsInDesignMode(this))
                return;

            Host?.ShowParamPage(ParamSettingsPage.None);
        }

        /// <summary>
        /// Показать панель settings.
        /// Для AI/DI/VGA это оставляем как текущее поведение.
        /// </summary>
        public void ShowParamsButton_Click(object sender, RoutedEventArgs e)
        {
            if (DesignerProperties.GetIsInDesignMode(this))
                return;

            Host?.ShowParamSettings();
        }

        /// <summary>
        /// Когда DevExpress пересоздаёт series, заново применяем стили трендов.
        /// </summary>
        public void ParamChart_BoundDataChanged(object sender, RoutedEventArgs e)
        {
            if (DesignerProperties.GetIsInDesignMode(this))
                return;

            if (Host != null && sender is ChartControl chart)
                Host.ApplyTrendSeriesStyles(chart);
        }

        /// <summary>
        /// В crosshair показываем RawValue, а не масштабированное значение.
        /// </summary>
        public void Chart_CustomDrawCrosshair(object sender, CustomDrawCrosshairEventArgs e)
        {
            try
            {
                foreach (var group in e.CrosshairElementGroups)
                {
                    foreach (var el in group.CrosshairElements)
                    {
                        if (el.SeriesPoint?.Tag is TrendPoint tp && el.LabelElement != null)
                        {
                            var tag = (tp.Series ?? "").Trim();

                            if (string.IsNullOrEmpty(tag))
                                tag = (el.Series?.DisplayName ?? el.Series?.Name ?? "").Trim();

                            el.LabelElement.Text = $"{tag} - {tp.RawValue.ToString("0.###", CultureInfo.InvariantCulture)}";
                        }
                    }
                }
            }
            catch
            {
                // Ошибки в crosshair не должны ломать график
            }
        }

        /// <summary>
        /// Возврат тренда в Live режим.
        /// </summary>
        public void LiveButton_Click(object sender, RoutedEventArgs e)
        {
            if (DesignerProperties.GetIsInDesignMode(this))
                return;

            Host?.SetParamChartLiveMode(resetPoints: false);
        }

        /// <summary>
        /// Прокси scroll в MainWindow.
        /// </summary>
        public void ParamChart_DiagramScroll(object sender, XYDiagram2DScrollEventArgs e)
        {
            if (e.NewXRange.MinValue is DateTime min && e.NewXRange.MaxValue is DateTime max)
                Host?.OnParamChartUserRangeChanged(min, max);
        }

        /// <summary>
        /// Прокси zoom в MainWindow.
        /// </summary>
        public void ParamChart_DiagramZoom(object sender, XYDiagram2DZoomEventArgs e)
        {
            if (e.NewXRange.MinValue is DateTime min && e.NewXRange.MaxValue is DateTime max)
                Host?.OnParamChartUserRangeChanged(min, max);
        }

        /// <summary>
        /// Общая кнопка Man = 1.
        /// </summary>
        public void Man_Open_Click(object sender, RoutedEventArgs e)
        {
            WriteBoolTagValue("Man", true);
        }

        /// <summary>
        /// Общая кнопка Man = 0.
        /// </summary>
        public void Man_Close_Click(object sender, RoutedEventArgs e)
        {
            WriteBoolTagValue("Man", false);
        }

        /// <summary>
        /// Общий клик по DI/DO ссылке.
        /// Используется в MotorParamView и VGDParamView для перехода
        /// к связанному оборудованию из строк DI/DO.
        /// </summary>
        public void DiDoValue_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe)
                return;

            if (fe.DataContext is not DiDoRefRow row)
                return;

            Host?.Param_NavigateToLinkedEquip(row);
            e.Handled = true;
        }

        /// <summary>
        /// Общий helper записи bool-тега по имени EquipItem.
        /// </summary>
        protected void WriteBoolTagValue(string equipItem, bool value)
        {
            Host?.ParamEditable_WriteFromUi(equipItem, value, !value);
        }

        /// <summary>
        /// Начало ручного ввода в текстовом редакторе.
        /// </summary>
        private void OnAnyEditorGotFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (e.NewFocus is TextBox && e.NewFocus is not CheckEdit)
                Host?.BeginParamFieldEdit();
        }

        /// <summary>
        /// Окончание ручного ввода в текстовом редакторе.
        /// </summary>
        private void OnAnyEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (e.OldFocus is TextBox && e.OldFocus is not CheckEdit)
                Host?.EndParamFieldEdit();
        }
    }
}