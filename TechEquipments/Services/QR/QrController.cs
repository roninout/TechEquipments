using DevExpress.Xpf.Core;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

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
        private readonly IQrHost _host;

        public QrController(
            IEquipmentService equipmentService,
            IQrCodeService qrCodeService,
            IQrScannerService qrScannerService,
            IQrHost host)
        {
            _equipmentService = equipmentService ?? throw new ArgumentNullException(nameof(equipmentService));
            _qrCodeService = qrCodeService ?? throw new ArgumentNullException(nameof(qrCodeService));
            _qrScannerService = qrScannerService ?? throw new ArgumentNullException(nameof(qrScannerService));
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        /// <summary>Чи показувати кнопку Generate QR.</summary>
        public bool ShowGenerateQrButton => GetShowGenerateQrButton();

        /// <summary>Чи вже існує QR для поточного тексту.</summary>
        public bool IsQrAlreadyGenerated() => File.Exists(GetExpectedQrPathOrEmpty());

        /// <summary>Generate QR (Param tab).</summary>
        public async Task GenerateQrAsync()
        {
            try
            {
                // 0) Якщо вже є — не генеруємо дублікат
                if (IsQrAlreadyGenerated())
                {
                    var path = GetExpectedQrPathOrEmpty();
                    DXMessageBox.Show(_host.OwnerWindow, $"QR уже существует:\n{path}", "QR",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);

                    _host.NotifyParamQrUiChanged();
                    return;
                }

                // 1) Текст: спочатку EquipName, якщо пусто — SelectedListBoxEquipment
                var text = GetQrTextOrEmpty();
                if (string.IsNullOrWhiteSpace(text))
                {
                    DXMessageBox.Show(_host.OwnerWindow,
                        "Нет текста для QR.\nВведи имя в поиск или выбери оборудование в списке.",
                        "QR",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Information);
                    return;
                }

                // 2) Папка: Station\TypeGroup
                var outputDir = GetQrOutputDirectory(text);

                // 3) Генерація
                var pathSaved = await _qrCodeService.GenerateQrPngAsync(text, outputDirectory: outputDir);

                _host.SetParamStatusText($"QR saved: {Path.GetFileName(pathSaved)}");

                DXMessageBox.Show(_host.OwnerWindow, $"QR-код успешно сохранён:\n{pathSaved}", "QR",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);

                // файл з'явився -> треба сховати кнопку
                _host.NotifyParamQrUiChanged();
            }
            catch (Exception ex)
            {
                DXMessageBox.Show(_host.OwnerWindow, ex.ToString(), "QR generate error",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        /// <summary>Scan QR (Param tab) + write ExternalTag + перейти на Param + polling.</summary>
        public async Task ScanQrToExternalTagAndSearchAsync()
        {
            try
            {
                // 1) Скан
                var text = await _qrScannerService.ScanFromCameraAsync(_host.OwnerWindow);
                if (string.IsNullOrWhiteSpace(text))
                    return;

                text = text.Trim();

                // 2) Фільтри Station/Type (якщо знайдемо обладнання)
                TryApplyStationTypeFiltersFromQr(text);

                // 3) Пишемо в ExternalTag (best-effort)
                try
                {
                    await _equipmentService.SetExternalTagAsync(text);
                }
                catch
                {
                    // не критично
                }

                // 4) В пошук
                _host.EquipName = text;

                // 5) Виділяємо
                _host.DoIncrementalSearch(text);

                // 6) На Param + polling
                if (_host.SelectedMainTab != MainTabKind.Param)
                    _host.SelectedMainTabIndex = (int)MainTabKind.Param;

                _host.StartParamPolling();

                _host.SetParamStatusText($"QR scanned: {text}");

                _host.NotifyParamQrUiChanged();
            }
            catch (Exception ex)
            {
                DXMessageBox.Show(_host.OwnerWindow, ex.ToString(), "QR scan error",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        // ===================== helpers =====================

        private string GetQrTextOrEmpty()
        {
            var text = (_host.EquipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(text))
                text = (_host.SelectedListBoxEquipment?.Equipment ?? "").Trim();

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

            // 1) exact Equipment, then exact Tag
            var it =
                _host.Equipments.FirstOrDefault(x => string.Equals(x.Equipment, qrText, StringComparison.OrdinalIgnoreCase))
                ?? _host.Equipments.FirstOrDefault(x => string.Equals(x.Tag, qrText, StringComparison.OrdinalIgnoreCase));

            if (it != null)
                return it;

            // 2) startswith Equipment, then Tag
            it =
                _host.Equipments.FirstOrDefault(x => (x.Equipment ?? "").StartsWith(qrText, StringComparison.OrdinalIgnoreCase))
                ?? _host.Equipments.FirstOrDefault(x => (x.Tag ?? "").StartsWith(qrText, StringComparison.OrdinalIgnoreCase));

            if (it != null)
                return it;

            // 3) contains Equipment, then Tag
            it =
                _host.Equipments.FirstOrDefault(x => (x.Equipment ?? "").Contains(qrText, StringComparison.OrdinalIgnoreCase))
                ?? _host.Equipments.FirstOrDefault(x => (x.Tag ?? "").Contains(qrText, StringComparison.OrdinalIgnoreCase));

            return it;
        }

        public bool TryApplyStationTypeFiltersFromQr(string qrText)
        {
            var match = FindEquipmentForQrText(qrText);
            if (match == null)
                return false;

            // Station
            if (!string.IsNullOrWhiteSpace(match.Station))
                _host.SelectedStation = match.Station.Trim();

            // TypeGroup
            _host.SelectedTypeFilter = EquipTypeRegistry.GetGroup(match.Type ?? "");

            return true;
        }

        private string GetQrOutputDirectory(string qrText)
        {
            var baseDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "QRCodes");

            var match = FindEquipmentForQrText(qrText);

            // Station
            string stationPart =
                !string.IsNullOrWhiteSpace(match?.Station) ? match!.Station :
                !string.IsNullOrWhiteSpace(_host.SelectedStation) ? _host.SelectedStation :
                "All";

            // TypeGroup
            EquipTypeGroup group =
                match != null
                    ? EquipTypeRegistry.GetGroup(match.Type ?? "")
                    : _host.SelectedTypeFilter;

            string groupPart =
                group != EquipTypeGroup.All ? group.ToString() : "All";

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