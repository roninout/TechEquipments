using DevExpress.Xpf.Charts;
using Microsoft.Extensions.Hosting;
using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TechEquipments.Views.Param
{

     /// <summary>
     /// Param tab view for AIParam.
     ///
     /// Responsibilities:
     /// - Proxies DevExpress editor events to MainWindow (writes / Enter handling).
     /// - Re-applies trend series styles when the chart regenerates series.
     /// - Overrides crosshair labels to show TrendPoint.RawValue (non-scaled).
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

        /// <summary>
        /// Forward PreviewKeyDown to MainWindow handler.
        /// In MainWindow we write only on Enter.
        /// </summary>
        private void ParamEditable_EditValueChanged(object sender, KeyEventArgs e)
        {
            if (DesignerProperties.GetIsInDesignMode(this))
                return;

            var host = Window.GetWindow(this) as MainWindow ?? Application.Current?.MainWindow as MainWindow;
            host?.ParamEditable_EditValueChanged(sender, e);
        }

        /// <summary>
        /// Show the chart panel (optionally reset points).
        /// </summary>
        private void ShowChartButton_Click(object sender, RoutedEventArgs e)
        {
            if (DesignerProperties.GetIsInDesignMode(this))
                return;

            var host = Window.GetWindow(this) as MainWindow ?? Application.Current?.MainWindow as MainWindow;
            host?.ShowParamChart(reset: true); // поставь true если хочешь сбрасывать график
        }

        /// <summary>
        /// Show the settings panel.
        /// </summary>
        private void ShowParamsButton_Click(object sender, RoutedEventArgs e)
        {
            if (DesignerProperties.GetIsInDesignMode(this))
                return;

            var host = Window.GetWindow(this) as MainWindow ?? Application.Current?.MainWindow as MainWindow;
            host?.ShowParamSettings();
        }

        /// <summary>
        /// Called when the chart rebuilds its series because bound data has changed.
        /// Here we re-apply series style attributes to the newly created series.
        /// </summary>
        private void ParamChart_BoundDataChanged(object sender, RoutedEventArgs e)
        {
            if (DesignerProperties.GetIsInDesignMode(this))
                return;

            var host = Window.GetWindow(this) as MainWindow
                       ?? Application.Current?.MainWindow as MainWindow;

            if (host != null && sender is ChartControl chart)
                host.ApplyTrendSeriesStyles(chart);
        }

        /// <summary>
        /// Crosshair shows values by SeriesPoint.Value (which we scale for drawing).
        /// We override label text to show TrendPoint.RawValue instead.
        /// </summary>
        private void Chart_CustomDrawCrosshair(object sender, CustomDrawCrosshairEventArgs e)
        {
            try
            {
                foreach (var group in e.CrosshairElementGroups)
                {
                    foreach (var el in group.CrosshairElements)
                    {
                        // В Tag лежит TrendPoint (мы сами его туда кладём при формировании точек)
                        if (el.SeriesPoint?.Tag is TrendPoint tp && el.LabelElement != null)
                        {
                            // 1) Берём "имя" (R / STW):
                            var tag = (tp.Series ?? "").Trim();

                            if (string.IsNullOrEmpty(tag))
                                tag = (el.Series?.DisplayName ?? el.Series?.Name ?? "").Trim();

                            // 2) Пишем "R - 98"
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


        private void ParamChart_DiagramScroll(object sender, XYDiagram2DScrollEventArgs e)
        {
            if (DesignerProperties.GetIsInDesignMode(this))
                return;

            // Для DateTime-оси MinValue/MaxValue будут DateTime
            if (e.NewXRange.MinValue is DateTime min && e.NewXRange.MaxValue is DateTime max)
            {
                var host = Window.GetWindow(this) as MainWindow ?? Application.Current?.MainWindow as MainWindow;
                host?.OnParamChartUserRangeChanged(min, max);
            }
        }

        private void ParamChart_DiagramZoom(object sender, XYDiagram2DZoomEventArgs e)
        {
            if (DesignerProperties.GetIsInDesignMode(this))
                return;

            if (e.NewXRange.MinValue is DateTime min && e.NewXRange.MaxValue is DateTime max)
            {
                var host = Window.GetWindow(this) as MainWindow ?? Application.Current?.MainWindow as MainWindow;
                host?.OnParamChartUserRangeChanged(min, max);
            }
        }

        /// <summary>
        /// Switches the trend chart back to Live mode (pin the visible window to "now").
        /// </summary>
        private void LiveButton_Click(object sender, RoutedEventArgs e)
        {
            if (DesignerProperties.GetIsInDesignMode(this))
                return;

            var host = Window.GetWindow(this) as MainWindow ?? Application.Current?.MainWindow as MainWindow;

            // resetPoints:false = не чистим точки полностью, а просто обрезаем до окна live
            host?.SetParamChartLiveMode(resetPoints: false);
        }
    }
}

