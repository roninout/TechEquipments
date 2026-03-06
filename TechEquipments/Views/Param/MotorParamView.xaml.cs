using DevExpress.Xpf.Charts;
using DevExpress.Xpf.Editors;
using DevExpress.Xpf.Core;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace TechEquipments.Views.Param
{
    /// <summary>
    /// Interaction logic for VGAParamView.xaml
    /// </summary>
    public partial class MotorParamView : UserControl
    {
        // Управление "пульсом" для Ack/Reset: чтобы не было наложения 1->0,
        // если пользователь нажмёт кнопку повторно до истечения 3 сек.
        private readonly object _pulseLock = new();
        private readonly Dictionary<string, CancellationTokenSource> _pulseByEquipItem = new(StringComparer.OrdinalIgnoreCase);
        // Предыдущее значение Mode, чтобы понимать направление (1->0 или 0->1)
        private bool? _lastMode;

        public MotorParamView()
        {
            InitializeComponent();

            // ловим фокус всех вложенных редакторов (TextEdit и т.п.)
            AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnAnyEditorGotFocus), true);
            AddHandler(Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnAnyEditorLostFocus), true);

            DataContextChanged += (_, __) =>
            {
                if (DataContext is MotorParam mp)
                    _lastMode = mp.Mode;
                else
                    _lastMode = null;
            };
        }

        /// <summary>
        /// Mode: подтверждение только при переходе 1->0 (Automatic -> Service).
        /// Используем Click, чтобы не срабатывало при обновлениях биндинга (смена оборудования/polling).
        /// </summary>
        private void Mode_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton tb)
                return;

            var mw = Window.GetWindow(this) as MainWindow ?? Application.Current?.MainWindow as MainWindow;
            if (mw == null)
                return;

            // Новое значение уже применилось (после клика)
            var newValue = tb.IsChecked == true;

            // Старое значение берём из _lastMode, а если его нет — из DataContext
            var oldValue = _lastMode;
            if (oldValue == null && DataContext is MotorParam mp)
                oldValue = mp.Mode;

            // Подтверждение только для 1 -> 0
            if (oldValue == true && newValue == false)
            {
                if (!ConfirmModeToService())
                {
                    // Cancel -> откатываем UI обратно в 1
                    tb.IsChecked = true;
                    _lastMode = true;
                    return;
                }
            }

            // Пишем в SCADA через общий механизм
            mw.ParamEditable_WriteFromUi(tb.Tag as string, tb.IsChecked);

            // Фиксируем "последнее" значение
            _lastMode = newValue;
        }

        private bool ConfirmModeToService()
        {
            var owner = Window.GetWindow(this);

            const string caption = "Attention!!!";
            const string text = "In service mode, the interlock is OFF!\nAre you sure?";

            var result = owner != null
                ? DXMessageBox.Show(owner, text, caption, MessageBoxButton.OKCancel, MessageBoxImage.Warning)
                : DXMessageBox.Show(text, caption, MessageBoxButton.OKCancel, MessageBoxImage.Warning);

            return result == MessageBoxResult.OK;
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

            var host = Window.GetWindow(this) as MainWindow ?? Application.Current?.MainWindow as MainWindow;
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

        /// <summary>
        /// Switches the trend chart back to Live mode (pin the visible window to "now").
        /// </summary>
        private void LiveButton_Click(object sender, RoutedEventArgs e)
        {
            if (DesignerProperties.GetIsInDesignMode(this))
                return;

            var host = Window.GetWindow(this) as MainWindow ?? Application.Current?.MainWindow as MainWindow;

            // Кнопка возвращает в Live (как “прилипнуть к now”)
            host?.SetParamChartLiveMode(resetPoints: false);
        }

        private void ParamChart_DiagramScroll(object sender, XYDiagram2DScrollEventArgs e)
        {
            if (e.NewXRange.MinValue is DateTime min && e.NewXRange.MaxValue is DateTime max)
                (Window.GetWindow(this) as MainWindow)?.OnParamChartUserRangeChanged(min, max);
        }

        private void ParamChart_DiagramZoom(object sender, XYDiagram2DZoomEventArgs e)
        {
            if (e.NewXRange.MinValue is DateTime min && e.NewXRange.MaxValue is DateTime max)
                (Window.GetWindow(this) as MainWindow)?.OnParamChartUserRangeChanged(min, max);
        }

        // Взводим флаг, когда фокус попал в текстовый редактор (а не в CheckEdit)
        private void OnAnyEditorGotFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (e.NewFocus is TextBox && e.NewFocus is not CheckEdit)
                (System.Windows.Application.Current.MainWindow as MainWindow)?.BeginParamFieldEdit();
        }

        // Сбрасываем флаг при выходе из текстового редактора
        private void OnAnyEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (e.OldFocus is TextBox && e.OldFocus is not CheckEdit)
                (System.Windows.Application.Current.MainWindow as MainWindow)?.EndParamFieldEdit();
        }

        private void Man_Open_Click(object sender, RoutedEventArgs e)
        {
            WriteManValue(true);
        }

        private void Man_Close_Click(object sender, RoutedEventArgs e)
        {
            WriteManValue(false);
        }

        /// <summary>
        /// Пишет Man (1/0) через MainWindow общий метод записи по Tag.
        /// </summary>
        private void WriteManValue(bool man)
        {
            // ВАЖНО: DataContext у View = модель, MainWindow берём через Window.GetWindow(this)
            var mw = Window.GetWindow(this) as MainWindow ?? Application.Current?.MainWindow as MainWindow;
            if (mw == null)
                return;

            // TagName должен совпадать с EquipItem в SCADA
            mw.ParamEditable_WriteFromUi("Man", man);
        }

        #region Buttons

        /// <summary>
        /// Какие настройки показываем в панели Settings.
        /// </summary>
        private enum SettingsSection
        {
            Plc,
            DiDo,
            Alarm,
            TimeWork,
            DryRun
        }

        /// <summary>
        /// Показать Settings-панель и включить нужную секцию.
        /// </summary>
        private void ShowSettingsSection(SettingsSection section)
        {
            if (DesignerProperties.GetIsInDesignMode(this))
                return;

            var host = Window.GetWindow(this) as MainWindow ?? Application.Current?.MainWindow as MainWindow;

            // 1) гарантируем, что видна панель Settings (а не Chart)
            host?.ShowParamSettings();

            // 2) показываем ровно один блок
            PlcSettingsGroup.Visibility = section == SettingsSection.Plc ? Visibility.Visible : Visibility.Collapsed;
            DiSettingsGroup.Visibility = section == SettingsSection.DiDo ? Visibility.Visible : Visibility.Collapsed;
            DoSettingsGroup.Visibility = section == SettingsSection.DiDo ? Visibility.Visible : Visibility.Collapsed;
            AlarmSettingsGroup.Visibility = section == SettingsSection.Alarm ? Visibility.Visible : Visibility.Collapsed;
            TimeWorkInfoGroup.Visibility = section == SettingsSection.TimeWork ? Visibility.Visible : Visibility.Collapsed;
            TimeWorkSettingsGroup.Visibility = section == SettingsSection.TimeWork ? Visibility.Visible : Visibility.Collapsed;
            DryRunSettingsGroup.Visibility = section == SettingsSection.DryRun ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Show the chart panel (optionally reset points).
        /// </summary>
        private void ShowChartButton_Click(object sender, RoutedEventArgs e)
        {
            if (DesignerProperties.GetIsInDesignMode(this))
                return;

            var mw = Window.GetWindow(this) as MainWindow ?? Application.Current?.MainWindow as MainWindow;
            mw?.SetParamSettingsPage(ParamSettingsPage.None);

            var host = Window.GetWindow(this) as MainWindow ?? Application.Current?.MainWindow as MainWindow;
            host?.ShowParamChart(reset: true); // поставь true если хочешь сбрасывать график
        }

        /// <summary>Кнопка PLC settings</summary>
        private void PLCButton_Click(object sender, RoutedEventArgs e)
        {
            ShowSettingsSection(SettingsSection.Plc);

            var mw = Window.GetWindow(this) as MainWindow ?? Application.Current?.MainWindow as MainWindow;
            mw?.SetParamSettingsPage(ParamSettingsPage.Plc);
        }

        /// <summary>Кнопка DI/DO settings</summary>
        private void DI_DOButton_Click(object sender, RoutedEventArgs e)
        {
            ShowSettingsSection(SettingsSection.DiDo);

            var mw = Window.GetWindow(this) as MainWindow ?? Application.Current?.MainWindow as MainWindow;
            mw?.SetParamSettingsPage(ParamSettingsPage.DiDo);
        }

        /// <summary>Кнопка Alarm settings</summary>
        private void AlarmSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            ShowSettingsSection(SettingsSection.Alarm);

            var mw = Window.GetWindow(this) as MainWindow ?? Application.Current?.MainWindow as MainWindow;
            mw?.SetParamSettingsPage(ParamSettingsPage.Alarm);
        }

        /// <summary>Кнопка Alarm settings</summary>
        private void TimeSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            ShowSettingsSection(SettingsSection.TimeWork);

            var mw = Window.GetWindow(this) as MainWindow ?? Application.Current?.MainWindow as MainWindow;
            mw?.SetParamSettingsPage(ParamSettingsPage.TimeWork);
        }

        /// <summary>Кнопка Alarm settings</summary>
        private void DryRunSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            ShowSettingsSection(SettingsSection.DryRun);

            var mw = Window.GetWindow(this) as MainWindow ?? Application.Current?.MainWindow as MainWindow;
            mw?.SetParamSettingsPage(ParamSettingsPage.DryRun);
        }

        #endregion

        /// <summary>
        /// Клик по Value (DI/DO) — перейти к связанному оборудованию (выделить в ListBox и открыть Param).
        /// </summary>
        private void DiDoValue_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe)
                return;

            if (fe.DataContext is not DiDoRefRow row)
                return;

            var mw = Window.GetWindow(this) as MainWindow ?? Application.Current?.MainWindow as MainWindow;
            mw?.Param_NavigateToLinkedEquip(row);

            e.Handled = true;
        }

        /// <summary>
        /// Для EqButton/EqButtonUp/... : пока пишем "1" в Value (как команда).
        /// Если понадобится "pulse 1->0" или разные значения для Up/Down — сделаем позже.
        /// </summary>
        private void Plc_SimpleButton_Click(object sender, RoutedEventArgs e)
        {
            if (DesignerProperties.GetIsInDesignMode(this))
                return;

            if (sender is not FrameworkElement fe || fe.Tag is not PlcRefRow row)
                return;

            var host = Window.GetWindow(this) as MainWindow ?? Application.Current?.MainWindow as MainWindow;
            host?.ParamPlc_WriteFromUi(row, 1); // команда
        }

        /// <summary>
        /// One-shot кнопки (Ack/Reset):
        /// 1) пишем "1"
        /// 2) ждём 3 секунды
        /// 3) пишем "0"
        /// Параллельные нажатия по одному и тому же EquipItem отменяют предыдущий пульс.
        /// </summary>
        /// <summary>
        /// One-shot кнопки (Ack/Reset):
        /// 1) пишем "1"
        /// 2) ждём 3 секунды
        /// 3) пишем "0"
        /// Для Reset показываем подтверждение (OK/Cancel).
        /// </summary>
        private async void Time_PulseButton_Click(object sender, RoutedEventArgs e)
        {
            if (DesignerProperties.GetIsInDesignMode(this))
                return;

            if (sender is not DevExpress.Xpf.Core.SimpleButton btn)
                return;

            var equipItem = (btn.Tag as string ?? "").Trim();
            if (string.IsNullOrWhiteSpace(equipItem))
                return;

            var mw = Window.GetWindow(this) as MainWindow ?? Application.Current?.MainWindow as MainWindow;
            if (mw == null)
                return;

            // 0) Для Reset — подтверждение
            if (string.Equals(equipItem, "TimeReset", StringComparison.OrdinalIgnoreCase))
            {
                if (!ConfirmTimeReset())
                    return; // Cancel -> ничего не отправляем
            }

            // 1) отменяем предыдущий "пульс" для этого же EquipItem (если ещё не успел сбросить в 0)
            CancellationTokenSource cts;
            lock (_pulseLock)
            {
                if (_pulseByEquipItem.TryGetValue(equipItem, out var old))
                {
                    old.Cancel();
                    old.Dispose();
                }

                cts = new CancellationTokenSource();
                _pulseByEquipItem[equipItem] = cts;
            }

            try
            {
                // 2) Мгновенный фидбек: Done + блокируем кнопку сразу
                btn.Content = "Done";
                btn.IsEnabled = false;

                // 3) Взводим 1
                mw.ParamEditable_WriteFromUi(equipItem, 1);

                // 4) Ждём 3 секунды (если нажали повторно — отменится)
                await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);

                // 5) Сбрасываем в 0
                mw.ParamEditable_WriteFromUi(equipItem, 0);
            }
            catch (TaskCanceledException)
            {
                // Нормально: пользователь нажал кнопку ещё раз -> старый пульс отменили и запустили новый.
            }
            finally
            {
                var isStillCurrent = false;

                lock (_pulseLock)
                {
                    if (_pulseByEquipItem.TryGetValue(equipItem, out var cur) && ReferenceEquals(cur, cts))
                    {
                        _pulseByEquipItem.Remove(equipItem);
                        isStillCurrent = true;
                    }
                }

                // Возвращаем управление стилю кнопки (он сам выставит Content/IsEnabled по значению тега)
                if (isStillCurrent)
                {
                    btn.ClearValue(ContentControl.ContentProperty);
                    btn.ClearValue(UIElement.IsEnabledProperty);
                }

                cts.Dispose();
            }
        }        

        /// <summary>
        /// Подтверждение для Reset (DevExpress message box).
        /// </summary>
        private bool ConfirmTimeReset()
        {
            var owner = Window.GetWindow(this);

            const string caption = "Attention";
            const string text = "Are you sure you want to clear the recorded time intervals of the mechanism's operation?";

            // DevExpress styled message box
            var result = owner != null
                ? DXMessageBox.Show(owner, text, caption, MessageBoxButton.OKCancel, MessageBoxImage.Warning)
                : DXMessageBox.Show(text, caption, MessageBoxButton.OKCancel, MessageBoxImage.Warning);

            return result == MessageBoxResult.OK;
        }

    }
    
    
}

