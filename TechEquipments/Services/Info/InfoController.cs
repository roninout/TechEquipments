using DevExpress.Xpf.Core;
using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace TechEquipments
{
    /// <summary>
    /// Контроллер вкладки Info:
    /// - загрузка карточки
    /// - edit/save
    /// - page switching
    /// - PDF cache/export flow через .\PdfFiles
    /// </summary>
    public sealed class InfoController
    {
        private readonly IEquipInfoService _equipInfoService;
        private readonly IInfoHost _host;

        public InfoController(IEquipInfoService equipInfoService, IInfoHost host)
        {
            _equipInfoService = equipInfoService ?? throw new ArgumentNullException(nameof(equipInfoService));
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        /// <summary>
        /// Текущее выбранное оборудование для вкладки Info.
        /// Приоритет: selected item в ListBox, затем текст поиска.
        /// </summary>
        private string ResolveSelectedEquipForInfo()
        {
            var text = (_host.EquipName ?? "").Trim();

            var sel = (_host.SelectedListBoxEquipment?.Equipment ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(sel))
                text = sel;

            return text;
        }

        /// <summary>
        /// Загружает карточку Info для текущего выбранного оборудования.
        /// Если записи в БД нет — создаём пустую DTO в памяти.
        /// </summary>
        public async Task LoadCurrentAsync()
        {
            var equipName = ResolveSelectedEquipForInfo();

            if (string.IsNullOrWhiteSpace(equipName))
            {
                _host.CurrentEquipInfo = null;
                _host.InfoStatusText = "";
                _host.InfoDocumentMessage = "";
                _host.IsInfoDocumentExportVisible = false;
                _host.IsInfoEditMode = false;
                return;
            }

            if (!_host.IsDbConnected)
            {
                _host.CurrentEquipInfo = EquipmentInfoDto.CreateEmpty(equipName);
                _host.InfoStatusText = "Info: DB is disconnected.";
                _host.InfoDocumentMessage = "";
                _host.IsInfoDocumentExportVisible = false;
                _host.IsInfoEditMode = false;
                return;
            }

            try
            {
                _host.IsInfoLoading = true;
                _host.InfoStatusText = $"Loading info: {equipName}...";

                _host.CurrentEquipInfo = await _equipInfoService.GetAsync(equipName);

                // Сбрасываем document-area state для новой карточки.
                _host.CurrentEquipInfo.PdfPreviewPath = null;
                _host.InfoDocumentMessage = "";
                _host.IsInfoDocumentExportVisible = false;

                // Если пользователь уже находится на Pdf/Scheme —
                // сразу готовим документную область под новую карточку.
                if (_host.IsInfoDocumentPage)
                    await PrepareCurrentDocumentAsync();

                _host.IsInfoEditMode = false;
                _host.InfoStatusText = $"Info loaded: {equipName}";
            }
            catch (Exception ex)
            {
                _host.CurrentEquipInfo = EquipmentInfoDto.CreateEmpty(equipName);
                _host.InfoDocumentMessage = "";
                _host.IsInfoDocumentExportVisible = false;
                _host.IsInfoEditMode = false;
                _host.InfoStatusText = $"Info error: {ex.Message}";
            }
            finally
            {
                _host.IsInfoLoading = false;
            }
        }

        /// <summary>
        /// Переводим вкладку Info в режим редактирования.
        /// </summary>
        public void BeginEdit()
        {
            var equipName = ResolveSelectedEquipForInfo();
            if (string.IsNullOrWhiteSpace(equipName))
                return;

            _host.CurrentEquipInfo ??= EquipmentInfoDto.CreateEmpty(equipName);
            _host.CurrentEquipInfo.EquipName = equipName;

            _host.IsInfoEditMode = true;
            _host.InfoStatusText = $"Editing info: {equipName}";
        }

        /// <summary>
        /// Сохраняем карточку Info в БД.
        /// </summary>
        public async Task SaveAsync()
        {
            if (_host.CurrentEquipInfo == null)
                return;

            var equipName = ResolveSelectedEquipForInfo();
            if (string.IsNullOrWhiteSpace(equipName))
                return;

            try
            {
                _host.IsInfoLoading = true;

                _host.CurrentEquipInfo.EquipName = equipName;
                await _equipInfoService.SaveAsync(_host.CurrentEquipInfo);

                _host.IsInfoEditMode = false;
                _host.InfoStatusText = $"Info saved: {equipName}";
            }
            catch (Exception ex)
            {
                _host.InfoStatusText = $"Info save error: {ex.Message}";
                DXMessageBox.Show(_host.OwnerWindow, ex.Message, "Save info",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _host.IsInfoLoading = false;
            }
        }

        /// <summary>
        /// Загружаем PDF с диска:
        /// - в DTO как byte[]
        /// - в .\PdfFiles как cached-файл
        /// - сразу открываем через PdfPreviewPath
        /// </summary>
        public async Task LoadPdfFromFileAsync()
        {
            if (!_host.IsInfoEditMode)
                return;

            var equipName = ResolveSelectedEquipForInfo();
            if (string.IsNullOrWhiteSpace(equipName))
                return;

            _host.CurrentEquipInfo ??= EquipmentInfoDto.CreateEmpty(equipName);
            _host.CurrentEquipInfo.EquipName = equipName;

            var dlg = new OpenFileDialog
            {
                Title = "Select PDF file",
                Filter = "PDF files (*.pdf)|*.pdf|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dlg.ShowDialog(_host.OwnerWindow) != true)
                return;

            var bytes = await File.ReadAllBytesAsync(dlg.FileName);

            _host.CurrentEquipInfo.PdfData = bytes;
            _host.CurrentEquipInfo.PdfFileName = Path.GetFileName(dlg.FileName);

            var path = GetExpectedInfoPdfPath(
                _host.CurrentInfoPage,
                _host.CurrentEquipInfo.EquipName,
                _host.CurrentEquipInfo.PdfFileName);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes);

            _host.CurrentEquipInfo.PdfPreviewPath = path;

            _host.InfoDocumentMessage = "";
            _host.IsInfoDocumentExportVisible = false;

            _host.InfoStatusText = $"PDF loaded: {_host.CurrentEquipInfo.PdfFileName}";
        }

        /// <summary>
        /// Очищаем PDF в DTO.
        /// </summary>
        public void ClearPdf()
        {
            if (!_host.IsInfoEditMode || _host.CurrentEquipInfo == null)
                return;

            _host.CurrentEquipInfo.PdfData = null;
            _host.CurrentEquipInfo.PdfFileName = null;
            _host.CurrentEquipInfo.PdfPreviewPath = null;

            _host.InfoDocumentMessage = "PDF cleared.";
            _host.IsInfoDocumentExportVisible = false;

            _host.InfoStatusText = "PDF cleared.";
        }

        /// <summary>
        /// Переключение страниц Info.
        /// General -> просто скрываем document-area state.
        /// Pdf/Scheme -> пытаемся сразу показать cached-файл.
        /// </summary>
        public async Task ShowPageAsync(InfoPageKind page)
        {
            _host.CurrentInfoPage = page;

            if (page == InfoPageKind.General)
            {
                _host.InfoDocumentMessage = "";
                _host.IsInfoDocumentExportVisible = false;

                if (_host.CurrentEquipInfo != null)
                    _host.CurrentEquipInfo.PdfPreviewPath = null;

                return;
            }

            await PrepareCurrentDocumentAsync();
        }

        /// <summary>
        /// При открытии Pdf/Scheme:
        /// 1) если cached-файл уже есть в .\PdfFiles — сразу показываем;
        /// 2) если cached-файла нет, но PDF есть в БД — показываем кнопку выгрузки;
        /// 3) если PDF нет вообще — показываем сообщение.
        /// </summary>
        public Task PrepareCurrentDocumentAsync()
        {
            if (!_host.IsInfoDocumentPage)
                return Task.CompletedTask;

            var model = _host.CurrentEquipInfo;
            if (model == null)
            {
                _host.InfoDocumentMessage = "No equipment selected.";
                _host.IsInfoDocumentExportVisible = false;
                return Task.CompletedTask;
            }

            model.PdfPreviewPath = null;

            var equipName = (model.EquipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(equipName))
            {
                _host.InfoDocumentMessage = "Equipment name is empty.";
                _host.IsInfoDocumentExportVisible = false;
                return Task.CompletedTask;
            }

            var hasDbPdf = model.PdfData is { Length: > 0 };
            var dbFileName = model.PdfFileName;

            if (string.IsNullOrWhiteSpace(dbFileName) && !hasDbPdf)
            {
                _host.InfoDocumentMessage = "No PDF file is stored for this equipment.";
                _host.IsInfoDocumentExportVisible = false;
                return Task.CompletedTask;
            }

            var expectedPath = GetExpectedInfoPdfPath(_host.CurrentInfoPage, equipName, dbFileName);

            // 1) файл уже есть в кеше -> сразу показываем
            if (File.Exists(expectedPath))
            {
                model.PdfPreviewPath = expectedPath;
                _host.InfoDocumentMessage = "";
                _host.IsInfoDocumentExportVisible = false;
                return Task.CompletedTask;
            }

            // 2) в БД есть PDF, но локально его ещё нет
            if (hasDbPdf)
            {
                _host.InfoDocumentMessage =
                    $"File '{dbFileName}' is stored in DB but not cached locally. Click 'Export PDF' to save it to .\\PdfFiles and open it.";
                _host.IsInfoDocumentExportVisible = true;
                return Task.CompletedTask;
            }

            // 3) имя есть, но byte[] нет
            _host.InfoDocumentMessage = $"PDF '{dbFileName}' is not available in DB.";
            _host.IsInfoDocumentExportVisible = false;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Выгружает PDF из БД в .\PdfFiles и сразу открывает его в viewer.
        /// </summary>
        public async Task ExportCurrentDocumentAsync()
        {
            var model = _host.CurrentEquipInfo;
            if (model == null)
                return;

            if (model.PdfData == null || model.PdfData.Length == 0)
                return;

            var equipName = (model.EquipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(equipName))
                return;

            try
            {
                _host.IsInfoLoading = true;

                var path = GetExpectedInfoPdfPath(_host.CurrentInfoPage, equipName, model.PdfFileName);

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                if (!File.Exists(path))
                    await File.WriteAllBytesAsync(path, model.PdfData);

                model.PdfPreviewPath = path;
                _host.InfoDocumentMessage = "";
                _host.IsInfoDocumentExportVisible = false;

                _host.InfoStatusText = $"PDF exported: {Path.GetFileName(path)}";
            }
            catch (Exception ex)
            {
                _host.InfoStatusText = $"PDF export error: {ex.Message}";
                DXMessageBox.Show(_host.OwnerWindow, ex.Message, "Export PDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _host.IsInfoLoading = false;
            }
        }

        /// <summary>
        /// Папка кеша PDF рядом с exe, по аналогии с QRCodes:
        /// .\PdfFiles
        /// </summary>
        private static string GetPdfFilesFolder()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PdfFiles");
        }

        /// <summary>
        /// Делает имя файла безопасным для файловой системы.
        /// </summary>
        private static string MakeSafeFileName(string text)
        {
            var name = (text ?? "").Trim();
            if (name.Length == 0)
                name = "document";

            var invalid = Path.GetInvalidFileNameChars();
            var safe = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());

            if (safe.Length > 120)
                safe = safe.Substring(0, 120);

            return safe;
        }

        /// <summary>
        /// Детерминированный путь cached PDF-файла.
        /// Пока Pdf и Scheme используют один и тот же PdfFileName/PdfData,
        /// но page включаем в имя уже сейчас — на будущее.
        /// </summary>
        private string GetExpectedInfoPdfPath(InfoPageKind page, string equipName, string? originalFileName)
        {
            var folder = GetPdfFilesFolder();

            var safeEquip = MakeSafeFileName(equipName);
            var safeName = MakeSafeFileName(string.IsNullOrWhiteSpace(originalFileName) ? "document.pdf" : originalFileName!);

            if (!safeName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                safeName += ".pdf";

            var pagePart = page.ToString(); // Pdf / Scheme
            var fileName = $"{safeEquip}_{pagePart}_{safeName}";

            return Path.Combine(folder, fileName);
        }
    }
}