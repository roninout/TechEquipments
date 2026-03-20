using DevExpress.Xpf.Core;
using Microsoft.Win32;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace TechEquipments
{
    /// <summary>
    /// Контроллер вкладки Info:
    /// - загрузка карточки по текущему оборудованию
    /// - перевод в edit mode
    /// - сохранение
    /// - загрузка/очистка PDF
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
                _host.IsInfoEditMode = false;
                return;
            }

            if (!_host.IsDbConnected)
            {
                _host.CurrentEquipInfo = EquipmentInfoDto.CreateEmpty(equipName);
                _host.InfoStatusText = "Info: DB is disconnected.";
                _host.IsInfoEditMode = false;
                return;
            }

            try
            {
                _host.IsInfoLoading = true;
                _host.InfoStatusText = $"Loading info: {equipName}...";

                _host.CurrentEquipInfo = await _equipInfoService.GetAsync(equipName);
                _host.IsInfoEditMode = false;

                _host.InfoStatusText = $"Info loaded: {equipName}";
            }
            catch (Exception ex)
            {
                _host.CurrentEquipInfo = EquipmentInfoDto.CreateEmpty(equipName);
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
        /// Загружаем PDF с диска и кладём прямо в DTO.
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

            _host.InfoStatusText = "PDF cleared.";
        }
    }
}