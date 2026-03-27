using DevExpress.Xpf.Core;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using TechEquipments.ViewModels;

namespace TechEquipments
{
    /// <summary>
    /// Контролер QR для вкладки Param:
    /// - Generate QR -> файл .png у папку Station\TypeGroup
    /// - Scan QR -> виставити фільтри, записати ExternalTag (best-effort), перейти на Param, запустити polling
    /// </summary>
    public sealed class QrController
    {
        private readonly IEquipmentService _equipmentService;
        private readonly IQrCodeService _qrCodeService;
        private readonly IQrScannerService _qrScannerService;
        private readonly MainViewModel _vm;
        private readonly Window _ownerWindow;
        private readonly Action<string> _setEquipName;
        private readonly Action<string> _setSelectedStation;
        private readonly Action<EquipTypeGroup> _setSelectedTypeFilter;
        private readonly Action<int> _setSelectedMainTabIndex;
        private readonly Action<string> _doIncrementalSearch;
        private readonly Action _startParamPolling;
        private readonly Action _notifyParamQrUiChanged;

        public QrController(
            IEquipmentService equipmentService,
            IQrCodeService qrCodeService,
            IQrScannerService qrScannerService,
            MainViewModel vm,
            Window ownerWindow,
            Action<string> setEquipName,
            Action<string> setSelectedStation,
            Action<EquipTypeGroup> setSelectedTypeFilter,
            Action<int> setSelectedMainTabIndex,
            Action<string> doIncrementalSearch,
            Action startParamPolling,
            Action notifyParamQrUiChanged)
        {
            _equipmentService = equipmentService ?? throw new ArgumentNullException(nameof(equipmentService));
            _qrCodeService = qrCodeService ?? throw new ArgumentNullException(nameof(qrCodeService));
            _qrScannerService = qrScannerService ?? throw new ArgumentNullException(nameof(qrScannerService));
            _vm = vm ?? throw new ArgumentNullException(nameof(vm));
            _ownerWindow = ownerWindow ?? throw new ArgumentNullException(nameof(ownerWindow));
            _setEquipName = setEquipName ?? throw new ArgumentNullException(nameof(setEquipName));
            _setSelectedStation = setSelectedStation ?? throw new ArgumentNullException(nameof(setSelectedStation));
            _setSelectedTypeFilter = setSelectedTypeFilter ?? throw new ArgumentNullException(nameof(setSelectedTypeFilter));
            _setSelectedMainTabIndex = setSelectedMainTabIndex ?? throw new ArgumentNullException(nameof(setSelectedMainTabIndex));
            _doIncrementalSearch = doIncrementalSearch ?? throw new ArgumentNullException(nameof(doIncrementalSearch));
            _startParamPolling = startParamPolling ?? throw new ArgumentNullException(nameof(startParamPolling));
            _notifyParamQrUiChanged = notifyParamQrUiChanged ?? throw new ArgumentNullException(nameof(notifyParamQrUiChanged));
        }

        public bool ShowGenerateQrButton => GetShowGenerateQrButton();

        public bool IsQrAlreadyGenerated() => File.Exists(GetExpectedQrPathOrEmpty());

        public async Task GenerateQrAsync()
        {
            try
            {
                if (IsQrAlreadyGenerated())
                {
                    var path = GetExpectedQrPathOrEmpty();
                    DXMessageBox.Show(_ownerWindow, $"QR уже существует:{ path}", "QR", MessageBoxButton.OK, MessageBoxImage.Information);

                    _notifyParamQrUiChanged();
                    return;
                }

                var text = GetQrTextOrEmpty();
                if (string.IsNullOrWhiteSpace(text))
                {
                    DXMessageBox.Show(_ownerWindow, "Нет текста для QR. Введи имя в поиск или выбери оборудование в списке.", "QR", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var outputDir = GetQrOutputDirectory(text);
                var pathSaved = await _qrCodeService.GenerateQrPngAsync(text, outputDirectory: outputDir);

                _vm.Shell.ParamStatusText = $"QR saved: {Path.GetFileName(pathSaved)}";

                DXMessageBox.Show(_ownerWindow, $"QR-код успешно сохранён:{ pathSaved}", "QR", MessageBoxButton.OK, MessageBoxImage.Information);

                _notifyParamQrUiChanged();
            }
            catch (Exception ex)
            {
                DXMessageBox.Show(_ownerWindow, ex.ToString(), "QR generate error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public async Task ScanQrToExternalTagAndSearchAsync()
        {
            try
            {
                var text = await _qrScannerService.ScanFromCameraAsync(_ownerWindow);
                if (string.IsNullOrWhiteSpace(text))
                    return;

                text = text.Trim();

                TryApplyStationTypeFiltersFromQr(text);

                try
                {
                    await _equipmentService.SetExternalTagAsync(text);
                }
                catch
                {
                }

                _setEquipName(text);
                _doIncrementalSearch(text);

                if (_vm.SelectedMainTab != MainTabKind.Param)
                    _setSelectedMainTabIndex((int)MainTabKind.Param);

                _startParamPolling();

                _vm.Shell.ParamStatusText = $"QR scanned: {text}";
                _notifyParamQrUiChanged();
            }
            catch (Exception ex)
            {
                DXMessageBox.Show(_ownerWindow, ex.ToString(), "QR scan error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string GetQrTextOrEmpty()
        {
            var text = (_vm.EquipmentList.EquipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(text))
                text = (_vm.EquipmentList.SelectedListBoxEquipment?.Equipment ?? "").Trim();

            return text ?? "";
        }

        private string GetExpectedQrPathOrEmpty()
        {
            var text = GetQrTextOrEmpty();
            if (string.IsNullOrWhiteSpace(text))
                return "";

            var outputDir = GetQrOutputDirectory(text);
            return _qrCodeService.GetExpectedQrPngPath(text, outputDirectory: outputDir);
        }

        private bool GetShowGenerateQrButton()
        {
            var path = GetExpectedQrPathOrEmpty();
            if (string.IsNullOrWhiteSpace(path))
                return false;

            return !File.Exists(path);
        }

        private EquipListBoxItem? FindEquipmentForQrText(string qrText)
        {
            qrText = (qrText ?? "").Trim();
            if (qrText.Length == 0)
                return null;

            var items = _vm.EquipmentList.Equipments;

            var it =
                items.FirstOrDefault(x => string.Equals(x.Equipment, qrText, StringComparison.OrdinalIgnoreCase))
                ?? items.FirstOrDefault(x => string.Equals(x.Tag, qrText, StringComparison.OrdinalIgnoreCase));

            if (it != null)
                return it;

            it =
                items.FirstOrDefault(x => (x.Equipment ?? "").StartsWith(qrText, StringComparison.OrdinalIgnoreCase))
                ?? items.FirstOrDefault(x => (x.Tag ?? "").StartsWith(qrText, StringComparison.OrdinalIgnoreCase));

            if (it != null)
                return it;

            it =
                items.FirstOrDefault(x => (x.Equipment ?? "").Contains(qrText, StringComparison.OrdinalIgnoreCase))
                ?? items.FirstOrDefault(x => (x.Tag ?? "").Contains(qrText, StringComparison.OrdinalIgnoreCase));

            return it;
        }

        public bool TryApplyStationTypeFiltersFromQr(string qrText)
        {
            var match = FindEquipmentForQrText(qrText);
            if (match == null)
                return false;

            if (!string.IsNullOrWhiteSpace(match.Station))
                _setSelectedStation(match.Station.Trim());

            _setSelectedTypeFilter(EquipTypeRegistry.GetGroup(match.Type ?? ""));

            return true;
        }

        private string GetQrOutputDirectory(string qrText)
        {
            var baseDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "QRCodes");
            var match = FindEquipmentForQrText(qrText);

            string stationPart =
                !string.IsNullOrWhiteSpace(match?.Station) ? match!.Station :
                !string.IsNullOrWhiteSpace(_vm.EquipmentList.SelectedStation) ? _vm.EquipmentList.SelectedStation :
                "All";

            EquipTypeGroup group =
                match != null
                    ? EquipTypeRegistry.GetGroup(match.Type ?? "")
                    : _vm.EquipmentList.SelectedTypeFilter;

            string groupPart = group != EquipTypeGroup.All ? group.ToString() : "All";

            stationPart = MakeSafePathPart(stationPart);
            groupPart = MakeSafePathPart(groupPart);

            return Path.Combine(baseDir, stationPart, groupPart);
        }

        private static string MakeSafePathPart(string? s)
        {
            s = (s ?? "").Trim();
            if (string.IsNullOrWhiteSpace(s))
                return "Unknown";

            var invalid = Path.GetInvalidFileNameChars();
            var safe = new string(s.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());

            safe = safe.Trim().TrimEnd('.', ' ');

            if (safe.Length == 0)
                safe = "Unknown";

            if (safe.Length > 60)
                safe = safe.Substring(0, 60);

            return safe;
        }
    }
}
