using DevExpress.Xpf.Editors;
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace TechEquipments
{
    /// <summary>
    /// Вся логика записи параметров из UI (CheckEdit/EditValueChanged и Enter).
    /// MainWindow только проксирует события в этот контроллер.
    /// </summary>
    public sealed class ParamWriteController
    {
        private readonly IEquipmentService _equipmentService;
        private readonly Func<MainTabKind> _getSelectedTab;
        private readonly Func<(string equipName, string equipType)> _resolveSelectedEquip;
        private readonly Func<bool> _getSuppressWritesFromPolling;
        private readonly Func<bool> _getSuppressWritesFromUiRollback;
        private readonly Action<bool> _setSuppressWritesFromUiRollback;
        private readonly SemaphoreSlim _paramRwGate;
        private readonly Action<DateTime> _setParamReadResumeAtUtc;
        private readonly Action<string> _setBottomText;
        private readonly Func<Window> _getOwnerWindow;
        private readonly Action _endParamFieldEdit;

        public ParamWriteController(IEquipmentService equipmentService,Func<MainTabKind> getSelectedTab,Func<(string equipName, string equipType)> resolveSelectedEquip,Func<bool> getSuppressWritesFromPolling,
            Func<bool> getSuppressWritesFromUiRollback,Action<bool> setSuppressWritesFromUiRollback,SemaphoreSlim paramRwGate,Action<DateTime> setParamReadResumeAtUtc,Action<string> setBottomText,Func<Window> getOwnerWindow,Action endParamFieldEdit)
        {
            _equipmentService = equipmentService;
            _getSelectedTab = getSelectedTab;
            _resolveSelectedEquip = resolveSelectedEquip;
            _getSuppressWritesFromPolling = getSuppressWritesFromPolling;
            _getSuppressWritesFromUiRollback = getSuppressWritesFromUiRollback;
            _setSuppressWritesFromUiRollback = setSuppressWritesFromUiRollback;
            _paramRwGate = paramRwGate;
            _setParamReadResumeAtUtc = setParamReadResumeAtUtc;
            _setBottomText = setBottomText;
            _getOwnerWindow = getOwnerWindow;
            _endParamFieldEdit = endParamFieldEdit;
        }

        /// <summary>
        /// PLC: запись значения из UI (SimpleButton и т.п.).
        /// Важно: соблюдаем те же правила, что и для обычных параметров:
        /// - пишем только на вкладке Param
        /// - не пишем, если сейчас прилетает polling update
        /// </summary>
        public async Task WritePlcFromUiAsync(PlcRefRow row, object? newValue)
        {
            // не пишем, если это обновление из polling
            if (_getSuppressWritesFromPolling())
                return;

            // пишем только на вкладке Param
            if (_getSelectedTab() != MainTabKind.Param)
                return;

            await Plc_WriteValueAsync(row, newValue);
        }

        /// <summary>
        /// DevExpress CheckEdit/EditValueChanged (в т.ч. ForceCmd confirm)
        /// </summary>
        public async Task OnEditValueChangedAsync(object sender, EditValueChangedEventArgs e)
        {
            // 0) подавляем записи, если это откат значения из UI (Cancel в confirm)
            if (_getSuppressWritesFromUiRollback())
                return;

            // 1) Не пишем, если это обновление прилетело из polling-READ
            if (_getSuppressWritesFromPolling())
                return;

            // 2) Пишем только на вкладке Param
            if (_getSelectedTab() != MainTabKind.Param)
                return;

            // 3) Нужно имя оборудования
            var (equipName, _) = _resolveSelectedEquip();
            var equip = (equipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(equip))
                return;

            // 4) Определяем EquipItem из Tag
            if (sender is not FrameworkElement fe)
                return;

            // PLC ветка: ToggleSwitchEdit (Tag = PlcRefRow)
            if (fe.Tag is PlcRefRow plcRow)
            {
                if (_getSuppressWritesFromPolling())
                    return;

                await Plc_WriteValueAsync(plcRow, e.NewValue);
                return;
            }

            if (fe.Tag is not string equipItem || string.IsNullOrWhiteSpace(equipItem))
                return;

            // Confirm только при включении ForceCmd (false -> true)
            if (equipItem.Equals("ForceCmd", StringComparison.OrdinalIgnoreCase))
            {
                bool oldVal = ToBool(e.OldValue);
                bool newVal = ToBool(e.NewValue);

                if (!oldVal && newVal)
                {
                    var res = DevExpress.Xpf.Core.DXMessageBox.Show(
                        _getOwnerWindow(),
                        "Do you really want to enable channel forcing?",
                        "Attention!!!",
                        MessageBoxButton.OKCancel,
                        MessageBoxImage.Warning);

                    if (res != MessageBoxResult.OK)
                    {
                        // Cancel -> откатить чекбокс и не писать в SCADA
                        _setSuppressWritesFromUiRollback(true);
                        try
                        {
                            if (sender is CheckEdit ce)
                                ce.IsChecked = (e.OldValue as bool?) ?? oldVal;
                        }
                        finally
                        {
                            _setSuppressWritesFromUiRollback(false);
                        }

                        return;
                    }
                }
            }

            // 5) Нормализуем значение
            if (!TryNormalizeWriteValue(e.NewValue, out var writeValue))
                return;

            await WriteParamAsync(equip, equipItem, writeValue);
        }

        /// <summary>
        /// Enter key write (PreviewKeyDown)
        /// </summary>
        public async Task OnPreviewKeyDownAsync(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter && e.Key != Key.Return)
                return;

            // PLC ветка: если Tag = PlcRefRow
            if (sender is FrameworkElement fePlc && fePlc.Tag is PlcRefRow plcRow)
            {
                var edit = sender as BaseEdit;
                var newVal = edit?.EditValue;

                e.Handled = true;
                await Plc_WriteValueAsync(plcRow, newVal);
                return;
            }

            // 1) Не пишем, если это обновление прилетело из polling-READ
            if (_getSuppressWritesFromPolling())
                return;

            // 2) Пишем только на вкладке Param
            if (_getSelectedTab() != MainTabKind.Param)
                return;

            // 3) Нужно имя оборудования
            var (equipName, _) = _resolveSelectedEquip();
            var equip = (equipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(equip))
                return;

            // 4) EquipItem из Tag
            if (sender is not FrameworkElement fe || fe.Tag is not string equipItem || string.IsNullOrWhiteSpace(equipItem))
                return;

            // 5) Берём текущее значение из редактора
            object? newValue = (sender as BaseEdit)?.EditValue;
            if (!TryNormalizeWriteValue(newValue, out var writeValue))
                return;

            e.Handled = true;
            _endParamFieldEdit();

            await WriteParamAsync(equip, equipItem, writeValue);
        }

        /// <summary>
        /// Универсальная запись параметра без DevExpress событий.
        /// </summary>
        public async Task WriteFromUiAsync(string? equipItem, object? newValue)
        {
            if (_getSuppressWritesFromPolling())
                return;

            if (_getSelectedTab() != MainTabKind.Param)
                return;

            var (equipName, _) = _resolveSelectedEquip();
            var equip = (equipName ?? "").Trim();

            if (string.IsNullOrWhiteSpace(equip))
                return;

            if (string.IsNullOrWhiteSpace(equipItem))
                return;

            if (!TryNormalizeWriteValue(newValue, out var writeValue))
                return;

            await WriteParamAsync(equip, equipItem, writeValue);
        }

        // ====== Private helpers ======

        private async Task WriteParamAsync(string equipName, string equipItem, string writeValue)
        {
            try
            {
                await _paramRwGate.WaitAsync(CancellationToken.None);
                try
                {
                    // Пауза чтения после записи
                    _setParamReadResumeAtUtc(DateTime.UtcNow.AddMilliseconds(400));

                    _setBottomText($"Write: {equipItem}={writeValue} ...");

                    await _equipmentService.WriteEquipItemAsync(equipName, equipItem, writeValue);

                    _setBottomText($"Wrote: {equipItem}={writeValue} at {DateTime.Now:HH:mm:ss}");
                }
                finally
                {
                    _paramRwGate.Release();
                }
            }
            catch (Exception ex)
            {
                _setBottomText($"Write error ({equipItem}): {ex.Message}");
            }
        }

        private async Task Plc_WriteValueAsync(PlcRefRow row, object? newValue)
        {
            if (row == null || !row.IsWritable)
                return;

            if (!TryNormalizeWriteValue(newValue, out var writeValueStr))
                return;

            var equipItem = GetPlcEquipItemForTagInfo(row);

            await _paramRwGate.WaitAsync(CancellationToken.None);
            try
            {
                var tagName = (row.TagName ?? "").Trim();
                if (string.IsNullOrWhiteSpace(tagName) || tagName.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    tagName = (await _equipmentService.ResolveTagNameAsync(row.EquipName, equipItem) ?? "").Trim();
                    row.TagName = tagName;
                }

                if (string.IsNullOrWhiteSpace(tagName) || tagName.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                    return;

                await _equipmentService.WriteTagNameAsync(tagName, writeValueStr);
            }
            catch
            {
                // ignore / log if needed
            }
            finally
            {
                _paramRwGate.Release();
            }
        }

        private static string GetPlcEquipItemForTagInfo(PlcRefRow row)
        {
            if (row.Type is PlcTypeCustom.EqMotorStatus or PlcTypeCustom.EqValveStatus)
                return "State";

            return "Value";
        }

        private static bool TryNormalizeWriteValue(object? newValue, out string str)
        {
            str = "";
            if (newValue == null)
                return false;

            if (newValue is bool b)
            {
                str = b ? "1" : "0";
                return true;
            }

            if (newValue is string s)
            {
                s = s.Trim();
                if (s.Length == 0) return false;

                s = s.Replace(',', '.');

                if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                {
                    str = i.ToString(CultureInfo.InvariantCulture);
                    return true;
                }

                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    str = d.ToString(CultureInfo.InvariantCulture);
                    return true;
                }

                return false;
            }

            if (newValue is int i2) { str = i2.ToString(CultureInfo.InvariantCulture); return true; }
            if (newValue is double d2) { str = d2.ToString(CultureInfo.InvariantCulture); return true; }

            str = Convert.ToString(newValue, CultureInfo.InvariantCulture) ?? "";
            return str.Length > 0;
        }

        private static bool ToBool(object? v)
        {
            try
            {
                if (v is bool b) return b;
                if (v == null) return false;
                return Convert.ToBoolean(v);
            }
            catch
            {
                return false;
            }
        }
    }
}