using DevExpress.Xpf.Core;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using TechEquipments.ViewModels;
using TechEquipments.Views.Info;

namespace TechEquipments
{
    /// <summary>
    /// Контроллер вкладки Info:
    /// - загрузка карточки
    /// - edit/save
    /// - page switching
    /// - работа с фото / instruction / scheme
    /// - cache PDF рядом с exe
    /// </summary>
    public sealed class InfoController
    {
        private readonly IEquipInfoService _equipInfoService;
        private readonly IQrScannerService _qrScannerService;

        private readonly InfoViewModel _vm;
        private readonly EquipmentListViewModel _equipmentVm;
        private readonly DatabaseViewModel _databaseVm;
        private readonly Window _ownerWindow;

        private int _loadCurrentRequestId;
        private bool _suppressLibrarySelectionSync;

        public InfoController(IEquipInfoService equipInfoService, InfoViewModel vm, EquipmentListViewModel equipmentVm, DatabaseViewModel databaseVm, Window ownerWindow, IQrScannerService qrScannerService)
        {
            _equipInfoService = equipInfoService ?? throw new ArgumentNullException(nameof(equipInfoService));
            _vm = vm ?? throw new ArgumentNullException(nameof(vm));
            _equipmentVm = equipmentVm ?? throw new ArgumentNullException(nameof(equipmentVm));
            _databaseVm = databaseVm ?? throw new ArgumentNullException(nameof(databaseVm));
            _ownerWindow = ownerWindow ?? throw new ArgumentNullException(nameof(ownerWindow));
            _qrScannerService = qrScannerService ?? throw new ArgumentNullException(nameof(qrScannerService));
        }

        private string ResolveSelectedEquipForInfo()
        {
            var text = (_equipmentVm.EquipName ?? "").Trim();

            var sel = (_equipmentVm.SelectedListBoxEquipment?.Equipment ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(sel))
                text = sel;

            return text;
        }

        private static string BuildCapturedPhotoFileName(string? equipName)
        {
            var baseName = (equipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = "Equipment";

            foreach (var ch in Path.GetInvalidFileNameChars())
                baseName = baseName.Replace(ch, '_');

            return $"{baseName}_{DateTime.Now:yyyyMMdd_HHmmss}.jpg";
        }

        public async Task LoadCurrentAsync()
        {
            // Версия запроса.
            // Нужна, чтобы более старый async-вызов не перетёр результат нового.
            var requestId = Interlocked.Increment(ref _loadCurrentRequestId);

            var equipName = ResolveSelectedEquipForInfo();

            void ClearInfoUiState()
            {
                _vm.SelectedInfoPhotoFile = null;
                _vm.SelectedInfoInstructionFile = null;
                _vm.SelectedInfoSchemeFile = null;

                // Важно:
                // очищаем checked-combo выбор,
                // иначе он может остаться от предыдущего equipment.
                _vm.SelectedInfoPhotoLibraryIds = new List<object>();
                _vm.SelectedInfoInstructionLibraryIds = new List<object>();
                _vm.SelectedInfoSchemeLibraryIds = new List<object>();

                _vm.CurrentInfoDocumentPreviewPath = null;
                _vm.InfoDocumentMessage = "";
                _vm.IsInfoDocumentExportVisible = false;
                _vm.IsInfoEditMode = false;
            }

            if (string.IsNullOrWhiteSpace(equipName))
            {
                _vm.CurrentEquipInfo = null;
                ClearInfoUiState();
                _vm.InfoStatusText = "";
                return;
            }

            if (!_databaseVm.IsDbConnected)
            {
                _vm.CurrentEquipInfo = EquipmentInfoDto.CreateEmpty(equipName);
                ClearInfoUiState();
                _vm.InfoStatusText = "Info: DB is disconnected.";
                return;
            }

            try
            {
                _vm.IsInfoLoading = true;
                _vm.InfoStatusText = $"Loading info: {equipName}...";

                // Сначала читаем карточку в локальную переменную.
                // Не применяем её в UI, пока не убедимся, что этот запрос ещё актуален.
                var info = await _equipInfoService.GetAsync(equipName);

                if (requestId != Volatile.Read(ref _loadCurrentRequestId))
                    return;

                await LoadLibrariesAsync();

                if (requestId != Volatile.Read(ref _loadCurrentRequestId))
                    return;

                _vm.CurrentEquipInfo = info;

                SyncCheckedSelectionsFromCurrentModel();

                _vm.SelectedInfoPhotoFile = info.Photos.FirstOrDefault();
                _vm.SelectedInfoInstructionFile = info.Instructions.FirstOrDefault();
                _vm.SelectedInfoSchemeFile = info.Schemes.FirstOrDefault();

                _vm.CurrentInfoDocumentPreviewPath = null;
                _vm.InfoDocumentMessage = "";
                _vm.IsInfoDocumentExportVisible = false;

                _vm.IsInfoEditMode = false;
                _vm.InfoStatusText = $"Info loaded: {equipName}";

                if (_vm.IsInfoDocumentPage)
                {
                    await PrepareCurrentDocumentAsync();

                    // Пока ждали PrepareCurrentDocumentAsync(),
                    // пользователь тоже мог успеть выбрать другое оборудование.
                    if (requestId != Volatile.Read(ref _loadCurrentRequestId))
                        return;
                }
            }
            catch (Exception ex)
            {
                // Старый запрос не должен ломать UI нового запроса.
                if (requestId != Volatile.Read(ref _loadCurrentRequestId))
                    return;

                _vm.CurrentEquipInfo = EquipmentInfoDto.CreateEmpty(equipName);
                ClearInfoUiState();
                _vm.InfoStatusText = $"Info error: {ex.Message}";
            }
            finally
            {
                // Только самый свежий запрос имеет право выключать loading.
                if (requestId == Volatile.Read(ref _loadCurrentRequestId))
                    _vm.IsInfoLoading = false;
            }
        }

        public void BeginEdit()
        {
            var equipName = ResolveSelectedEquipForInfo();
            if (string.IsNullOrWhiteSpace(equipName))
                return;

            _vm.CurrentEquipInfo ??= EquipmentInfoDto.CreateEmpty(equipName);
            _vm.CurrentEquipInfo.EquipName = equipName;

            _vm.IsInfoEditMode = true;
            _vm.InfoStatusText = $"Editing info: {equipName}";
        }

        public async Task SaveAsync()
        {
            if (_vm.CurrentEquipInfo == null)
                return;

            var equipName = ResolveSelectedEquipForInfo();
            if (string.IsNullOrWhiteSpace(equipName))
                return;

            try
            {
                _vm.IsInfoLoading = true;

                _vm.CurrentEquipInfo.EquipName = equipName;

                NormalizeSortOrder(_vm.CurrentEquipInfo.Photos, equipName);
                NormalizeSortOrder(_vm.CurrentEquipInfo.Instructions, equipName);
                NormalizeSortOrder(_vm.CurrentEquipInfo.Schemes, equipName);

                ValidateNoDuplicates(_vm.CurrentEquipInfo.Photos, "photo");
                ValidateNoDuplicates(_vm.CurrentEquipInfo.Instructions, "instruction");
                ValidateNoDuplicates(_vm.CurrentEquipInfo.Schemes, "scheme");

                await _equipInfoService.SaveAsync(_vm.CurrentEquipInfo);

                _vm.IsInfoEditMode = false;
                _vm.InfoStatusText = $"Info saved: {equipName}";

                if (_vm.IsInfoDocumentPage)
                    await PrepareCurrentDocumentAsync();
            }
            catch (Exception ex)
            {
                _vm.InfoStatusText = $"Info save error: {ex.Message}";
                DXMessageBox.Show(_ownerWindow, ex.Message, "Save info",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _vm.IsInfoLoading = false;
            }
        }

        public async Task LoadPhotoFilesAsync()
        {
            if (!_vm.IsInfoEditMode)
                return;

            var equipName = ResolveSelectedEquipForInfo();
            if (string.IsNullOrWhiteSpace(equipName))
                return;

            _vm.CurrentEquipInfo ??= EquipmentInfoDto.CreateEmpty(equipName);
            _vm.CurrentEquipInfo.EquipName = equipName;

            var dlg = new OpenFileDialog
            {
                Title = "Select image files",
                Filter = "Image files (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = true
            };

            if (dlg.ShowDialog(_ownerWindow) != true)
                return;

            var equipTypeGroupKey = ResolveSelectedEquipTypeGroupKey();
            if (string.IsNullOrWhiteSpace(equipTypeGroupKey))
                return;

            var addResult = await _equipInfoService.AddFilesToLibraryAsync(
                InfoFileKind.Photo,
                equipTypeGroupKey,
                dlg.FileNames);

            await LoadLibrariesAsync();

            MergeAssetsIntoSelection(_vm.CurrentEquipInfo.Photos, addResult.ResolvedAssets, equipName);
            SyncCheckedSelectionsFromCurrentModel();

            // После Add выбираем именно добавленный/подцепленный файл, а не первый в списке.
            var selectedPhoto = addResult.ResolvedAssets.LastOrDefault(asset => asset != null && asset.Id > 0);

            if (selectedPhoto != null)
            {
                _vm.SelectedInfoPhotoFile = _vm.CurrentEquipInfo.Photos
                    .FirstOrDefault(x => x.Id == selectedPhoto.Id);
            }
            else
            {
                _vm.SelectedInfoPhotoFile = _vm.CurrentEquipInfo.Photos.FirstOrDefault();
            }

            if (addResult.ExistingInLibraryFileNames.Count > 0)
            {
                DXMessageBox.Show(
                    _ownerWindow,
                    "These image files already existed in the shared library and were linked to the equipment:\n\n" +
                    string.Join("\n", addResult.ExistingInLibraryFileNames),
                    "Existing images",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            _vm.InfoStatusText = $"Images linked: {_vm.CurrentEquipInfo.Photos.Count}. New in library: {addResult.AddedToLibraryFileNames.Count}.";
        }

        public async Task CapturePhotoFromCameraAsync()
        {
            if (!_vm.IsInfoEditMode)
                return;

            var equipName = ResolveSelectedEquipForInfo();
            if (string.IsNullOrWhiteSpace(equipName))
                return;

            _vm.CurrentEquipInfo ??= EquipmentInfoDto.CreateEmpty(equipName);
            _vm.CurrentEquipInfo.EquipName = equipName;

            var equipTypeGroupKey = ResolveSelectedEquipTypeGroupKey();
            if (string.IsNullOrWhiteSpace(equipTypeGroupKey))
                return;

            string? tempFile = null;

            try
            {
                var cameras = await _qrScannerService.GetAvailableCamerasAsync();
                if (cameras == null || cameras.Count == 0)
                {
                    DXMessageBox.Show(_ownerWindow, "No camera devices were found.", "Capture photo", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var preferredIndex = await _qrScannerService.GetPreferredCameraIndexAsync();
                if (!cameras.Any(x => x.Index == preferredIndex))
                    preferredIndex = cameras[0].Index;

                var captureWindow = new PhotoCaptureWindow(_qrScannerService, cameras, preferredIndex){Owner = _ownerWindow};

                var ok = captureWindow.ShowDialog();
                if (ok != true)
                    return;

                tempFile = captureWindow.CapturedFilePath;
                if (string.IsNullOrWhiteSpace(tempFile) || !File.Exists(tempFile))
                    return;

                // Переименовываем снимок по шаблону: Equipment_yyyyMMdd_HHmmss.jpg
                var friendlyFileName = BuildCapturedPhotoFileName(equipName);
                var renamedTempFile = Path.Combine(Path.GetDirectoryName(tempFile)!, friendlyFileName);

                // Если вдруг файл с таким именем уже есть в temp-папке, добавим суффикс _01, _02 и т.д.
                if (File.Exists(renamedTempFile))
                {
                    var baseName = Path.GetFileNameWithoutExtension(friendlyFileName);
                    var ext = Path.GetExtension(friendlyFileName);

                    int i = 1;
                    do
                    {
                        renamedTempFile = Path.Combine(Path.GetDirectoryName(tempFile)!, $"{baseName}_{i:00}{ext}");
                        i++;
                    }
                    while (File.Exists(renamedTempFile));
                }

                File.Move(tempFile, renamedTempFile);
                tempFile = renamedTempFile;

                var addResult = await _equipInfoService.AddFilesToLibraryAsync(InfoFileKind.Photo, equipTypeGroupKey, new[] { tempFile });
                await LoadLibrariesAsync();

                MergeAssetsIntoSelection(_vm.CurrentEquipInfo.Photos, addResult.ResolvedAssets, equipName);
                SyncCheckedSelectionsFromCurrentModel();

                var selectedPhoto = addResult.ResolvedAssets.LastOrDefault(asset => asset != null && asset.Id > 0);

                if (selectedPhoto != null)
                    _vm.SelectedInfoPhotoFile = _vm.CurrentEquipInfo.Photos.FirstOrDefault(x => x.Id == selectedPhoto.Id);
                else
                    _vm.SelectedInfoPhotoFile = _vm.CurrentEquipInfo.Photos.FirstOrDefault();

                if (addResult.ExistingInLibraryFileNames.Count > 0)
                {
                    DXMessageBox.Show(_ownerWindow, "This photo already existed in the shared library and was linked to the equipment:\n\n" + string.Join("\n", addResult.ExistingInLibraryFileNames), "Existing photo", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                _vm.InfoStatusText = $"Photo captured and linked. Total linked images: {_vm.CurrentEquipInfo.Photos.Count}.";
            }
            catch (Exception ex)
            {
                _vm.InfoStatusText = $"Capture photo error: {ex.Message}";
                DXMessageBox.Show(_ownerWindow, ex.Message, "Capture photo", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(tempFile) && File.Exists(tempFile))
                        File.Delete(tempFile);
                }
                catch
                {
                    // ignore temp cleanup failures
                }
            }
        }

        public void RemoveSelectedPhoto()
        {
            if (!_vm.IsInfoEditMode || _vm.CurrentEquipInfo == null)
                return;

            var selected = _vm.SelectedInfoPhotoFile;
            if (selected == null)
                return;

            var list = _vm.CurrentEquipInfo.Photos;
            var index = list.IndexOf(selected);

            if (index >= 0)
                list.RemoveAt(index);

            NormalizeSortOrder(list, _vm.CurrentEquipInfo.EquipName);

            _vm.SelectedInfoPhotoFile =
                index >= 0 && index < list.Count ? list[index] : list.LastOrDefault();

            _vm.InfoStatusText = "Photo removed from current card.";

            SyncCheckedSelectionsFromCurrentModel();
        }

        public async Task LoadCurrentDocumentFilesAsync()
        {
            if (!_vm.IsInfoEditMode || !_vm.IsInfoDocumentPage)
                return;

            var equipName = ResolveSelectedEquipForInfo();
            if (string.IsNullOrWhiteSpace(equipName))
                return;

            _vm.CurrentEquipInfo ??= EquipmentInfoDto.CreateEmpty(equipName);
            _vm.CurrentEquipInfo.EquipName = equipName;

            var kind = _vm.CurrentInfoPage == InfoPageKind.Scheme
                ? InfoFileKind.Scheme
                : InfoFileKind.Instruction;

            var dlg = new OpenFileDialog
            {
                Title = kind == InfoFileKind.Scheme
                    ? "Select scheme PDF files"
                    : "Select instruction PDF files",
                Filter = "PDF files (*.pdf)|*.pdf|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = true
            };

            if (dlg.ShowDialog(_ownerWindow) != true)
                return;

            var equipTypeGroupKey = ResolveSelectedEquipTypeGroupKey();
            if (string.IsNullOrWhiteSpace(equipTypeGroupKey))
                return;

            var addResult = await _equipInfoService.AddFilesToLibraryAsync(
                kind,
                equipTypeGroupKey,
                dlg.FileNames);

            await LoadLibrariesAsync();

            var target = GetModelCollection(kind);
            MergeAssetsIntoSelection(target, addResult.ResolvedAssets, equipName);
            SyncCheckedSelectionsFromCurrentModel();

            var first = target.FirstOrDefault();
            if (kind == InfoFileKind.Instruction)
                _vm.SelectedInfoInstructionFile = first;
            else
                _vm.SelectedInfoSchemeFile = first;

            await PrepareCurrentDocumentAsync();

            if (addResult.ExistingInLibraryFileNames.Count > 0)
            {
                DXMessageBox.Show(
                    _ownerWindow,
                    "These PDF files already existed in the shared library and were linked to the equipment:\n\n" +
                    string.Join("\n", addResult.ExistingInLibraryFileNames),
                    "Existing PDF files",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            _vm.InfoStatusText =
                $"Documents linked: {target.Count}. New in library: {addResult.AddedToLibraryFileNames.Count}.";
        }

        public async Task RemoveCurrentDocumentAsync()
        {
            if (!_vm.IsInfoEditMode || !_vm.IsInfoDocumentPage)
                return;

            var selected = GetCurrentSelectedDocument();
            if (selected == null)
                return;

            var list = GetCurrentDocumentCollection();
            var index = list.IndexOf(selected);

            if (index >= 0)
                list.RemoveAt(index);

            NormalizeSortOrder(list, _vm.CurrentEquipInfo?.EquipName ?? "");

            var newSelected =
                index >= 0 && index < list.Count ? list[index] : list.LastOrDefault();

            SetCurrentSelectedDocument(newSelected);
            _vm.CurrentInfoDocumentPreviewPath = null;

            await PrepareCurrentDocumentAsync();

            _vm.InfoStatusText = "Document removed from current card.";

            SyncCheckedSelectionsFromCurrentModel();
        }

        public async Task ShowPageAsync(InfoPageKind page)
        {
            _vm.CurrentInfoPage = page;
            _vm.CurrentInfoDocumentPreviewPath = null;
            _vm.InfoDocumentMessage = "";
            _vm.IsInfoDocumentExportVisible = false;

            if (page == InfoPageKind.General)
                return;

            if (GetCurrentSelectedDocument() == null)
                SetCurrentSelectedDocument(GetCurrentDocumentCollection().FirstOrDefault());

            await PrepareCurrentDocumentAsync();
        }

        public Task PrepareCurrentDocumentAsync()
        {
            _vm.CurrentInfoDocumentPreviewPath = null;

            if (!_vm.IsInfoDocumentPage)
                return Task.CompletedTask;

            var model = _vm.CurrentEquipInfo;
            if (model == null)
            {
                _vm.InfoDocumentMessage = "No equipment selected.";
                _vm.IsInfoDocumentExportVisible = false;
                return Task.CompletedTask;
            }

            var selected = GetCurrentSelectedDocument();
            if (selected == null)
            {
                _vm.InfoDocumentMessage = _vm.CurrentInfoPage == InfoPageKind.Scheme
                    ? "No scheme file is stored for this equipment."
                    : "No instruction file is stored for this equipment.";

                _vm.IsInfoDocumentExportVisible = false;
                return Task.CompletedTask;
            }

            var equipName = (model.EquipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(equipName))
            {
                _vm.InfoDocumentMessage = "Equipment name is empty.";
                _vm.IsInfoDocumentExportVisible = false;
                return Task.CompletedTask;
            }

            var expectedPath = GetExpectedDocumentPath(_vm.CurrentInfoPage, selected.EquipTypeGroupKey, selected.FileName);

            if (File.Exists(expectedPath))
            {
                _vm.CurrentInfoDocumentPreviewPath = expectedPath;
                _vm.InfoDocumentMessage = "";
                _vm.IsInfoDocumentExportVisible = false;
                return Task.CompletedTask;
            }

            if (selected.FileData is { Length: > 0 })
            {
                var folderName = _vm.CurrentInfoPage == InfoPageKind.Scheme ? "Schemes" : "Instruction";

                _vm.InfoDocumentMessage = $"File '{selected.FileName}' is stored in DB but not cached locally. Click 'Export PDF' to save it to .\\{folderName} and open it.";

                _vm.IsInfoDocumentExportVisible = true;
                return Task.CompletedTask;
            }

            _vm.InfoDocumentMessage = $"File '{selected.FileName}' is not available in DB.";
            _vm.IsInfoDocumentExportVisible = false;
            return Task.CompletedTask;
        }

        public async Task ExportCurrentDocumentAsync()
        {
            if (!_vm.IsInfoDocumentPage)
                return;

            var model = _vm.CurrentEquipInfo;
            if (model == null)
                return;

            var selected = GetCurrentSelectedDocument();
            if (selected?.FileData == null || selected.FileData.Length == 0)
                return;

            var equipName = (model.EquipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(equipName))
                return;

            try
            {
                _vm.IsInfoLoading = true;

                var path = await EnsureDocumentCachedFromMemoryAsync(_vm.CurrentInfoPage, selected);

                _vm.CurrentInfoDocumentPreviewPath = path;
                _vm.InfoDocumentMessage = "";
                _vm.IsInfoDocumentExportVisible = false;

                _vm.InfoStatusText = $"PDF exported: {Path.GetFileName(path)}";
            }
            catch (Exception ex)
            {
                _vm.InfoStatusText = $"PDF export error: {ex.Message}";
                DXMessageBox.Show(_ownerWindow, ex.Message, "Export PDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _vm.IsInfoLoading = false;
            }
        }

        private ObservableCollection<EquipmentInfoFileDto> GetCurrentDocumentCollection()
        {
            _vm.CurrentEquipInfo ??= EquipmentInfoDto.CreateEmpty(ResolveSelectedEquipForInfo());

            return _vm.CurrentInfoPage switch
            {
                InfoPageKind.Instruction => _vm.CurrentEquipInfo.Instructions,
                InfoPageKind.Scheme => _vm.CurrentEquipInfo.Schemes,
                _ => _vm.CurrentEquipInfo.Instructions
            };
        }

        private EquipmentInfoFileDto? GetCurrentSelectedDocument()
        {
            return _vm.CurrentInfoPage switch
            {
                InfoPageKind.Instruction => _vm.SelectedInfoInstructionFile,
                InfoPageKind.Scheme => _vm.SelectedInfoSchemeFile,
                _ => null
            };
        }

        private void SetCurrentSelectedDocument(EquipmentInfoFileDto? file)
        {
            switch (_vm.CurrentInfoPage)
            {
                case InfoPageKind.Instruction:
                    _vm.SelectedInfoInstructionFile = file;
                    break;

                case InfoPageKind.Scheme:
                    _vm.SelectedInfoSchemeFile = file;
                    break;
            }
        }

        private static void NormalizeSortOrder(ObservableCollection<EquipmentInfoFileDto> files, string equipName)
        {
            if (files == null)
                return;

            for (int i = 0; i < files.Count; i++)
            {
                files[i].EquipName = equipName;
                files[i].SortOrder = i;
            }
        }

        private static void ValidateNoDuplicates(IEnumerable<EquipmentInfoFileDto> files, string sectionName)
        {
            var dup = files
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.FileHash))
                .GroupBy(x => x.FileHash, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(g => g.Count() > 1);

            if (dup != null)
                throw new InvalidOperationException($"Duplicate {sectionName} files detected in current card.");
        }

        private static string ComputeFileHash(byte[] data)
        {
            var hash = SHA256.HashData(data);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static string GetInstructionFolder() => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Instruction");

        private static string GetSchemesFolder() => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Schemes");

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

        private string GetExpectedDocumentPath(InfoPageKind page, string? equipTypeGroupKey, string? originalFileName)
        {
            var rootFolder = page == InfoPageKind.Scheme
                ? GetSchemesFolder()
                : GetInstructionFolder();

            var safeTypeGroup = MakeSafeFileName((equipTypeGroupKey ?? "").Trim());
            if (string.IsNullOrWhiteSpace(safeTypeGroup))
                safeTypeGroup = "Unknown";

            var folder = Path.Combine(rootFolder, safeTypeGroup);
            Directory.CreateDirectory(folder);

            // ВАЖНО:
            // сохраняем оригинальное имя файла без префикса,
            // но разводим кеш по подпапкам типа оборудования.
            var safeName = MakeSafeFileName((originalFileName ?? "").Trim());

            if (string.IsNullOrWhiteSpace(safeName))
                safeName = "document";

            return Path.Combine(folder, safeName);
        }

        private async Task<string> EnsureDocumentCachedFromMemoryAsync(InfoPageKind page, EquipmentInfoFileDto file)
        {
            if (file.FileData == null || file.FileData.Length == 0)
                throw new InvalidOperationException("Selected document has no data.");

            var path = GetExpectedDocumentPath(
                page,
                file.EquipTypeGroupKey,
                file.FileName);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            if (!File.Exists(path))
                await File.WriteAllBytesAsync(path, file.FileData);

            return path;
        }

        private static EquipmentInfoFileDto CloneFile(EquipmentInfoFileDto src, string equipName = "")
        {
            return new EquipmentInfoFileDto
            {
                Id = src.Id,
                EquipName = equipName,
                EquipTypeGroupKey = src.EquipTypeGroupKey,
                FileName = src.FileName,
                DisplayName = src.DisplayName,
                FileHash = src.FileHash,
                FileData = src.FileData,
                SortOrder = src.SortOrder,
                UpdatedAt = src.UpdatedAt
            };
        }

        private static void ReplaceCollection(ObservableCollection<EquipmentInfoFileDto> target, IEnumerable<EquipmentInfoFileDto> source)
        {
            target.Clear();

            foreach (var item in source ?? Enumerable.Empty<EquipmentInfoFileDto>())
                target.Add(item);
        }

        private async Task LoadLibrariesAsync()
        {
            var equipTypeGroupKey = ResolveSelectedEquipTypeGroupKey();

            if (string.IsNullOrWhiteSpace(equipTypeGroupKey))
            {
                _vm.AvailableInfoPhotoLibrary.Clear();
                _vm.AvailableInfoInstructionLibrary.Clear();
                _vm.AvailableInfoSchemeLibrary.Clear();
                return;
            }

            var photos = await _equipInfoService.GetLibraryAsync(InfoFileKind.Photo, equipTypeGroupKey);
            var instructions = await _equipInfoService.GetLibraryAsync(InfoFileKind.Instruction, equipTypeGroupKey);
            var schemes = await _equipInfoService.GetLibraryAsync(InfoFileKind.Scheme, equipTypeGroupKey);

            ReplaceCollection(_vm.AvailableInfoPhotoLibrary, photos);
            ReplaceCollection(_vm.AvailableInfoInstructionLibrary, instructions);
            ReplaceCollection(_vm.AvailableInfoSchemeLibrary, schemes);
        }

        private static List<object>? ToCheckedIds(IEnumerable<EquipmentInfoFileDto>? items)
        {
            var list = items?
                .Where(x => x != null && x.Id > 0)
                .Select(x => (object)x.Id)
                .Distinct()
                .ToList();

            return list is { Count: > 0 } ? list : new List<object>();
        }

        private void SyncCheckedSelectionsFromCurrentModel()
        {
            _suppressLibrarySelectionSync = true;

            try
            {
                _vm.SelectedInfoPhotoLibraryIds = ToCheckedIds(_vm.CurrentEquipInfo?.Photos);
                _vm.SelectedInfoInstructionLibraryIds = ToCheckedIds(_vm.CurrentEquipInfo?.Instructions);
                _vm.SelectedInfoSchemeLibraryIds = ToCheckedIds(_vm.CurrentEquipInfo?.Schemes);
            }
            finally
            {
                _suppressLibrarySelectionSync = false;
            }
        }

        private ObservableCollection<EquipmentInfoFileDto> GetLibraryCollection(InfoFileKind kind)
        {
            return kind switch
            {
                InfoFileKind.Photo => _vm.AvailableInfoPhotoLibrary,
                InfoFileKind.Instruction => _vm.AvailableInfoInstructionLibrary,
                InfoFileKind.Scheme => _vm.AvailableInfoSchemeLibrary,
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
            };
        }

        private ObservableCollection<EquipmentInfoFileDto> GetModelCollection(InfoFileKind kind)
        {
            _vm.CurrentEquipInfo ??= EquipmentInfoDto.CreateEmpty(ResolveSelectedEquipForInfo());

            return kind switch
            {
                InfoFileKind.Photo => _vm.CurrentEquipInfo.Photos,
                InfoFileKind.Instruction => _vm.CurrentEquipInfo.Instructions,
                InfoFileKind.Scheme => _vm.CurrentEquipInfo.Schemes,
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
            };
        }

        private List<object>? GetCheckedIds(InfoFileKind kind)
        {
            return kind switch
            {
                InfoFileKind.Photo => _vm.SelectedInfoPhotoLibraryIds,
                InfoFileKind.Instruction => _vm.SelectedInfoInstructionLibraryIds,
                InfoFileKind.Scheme => _vm.SelectedInfoSchemeLibraryIds,
                _ => null
            };
        }

        private void ApplyCheckedSelectionToModel(InfoFileKind kind)
        {
            if (_vm.CurrentEquipInfo == null)
                return;

            var equipName = (_vm.CurrentEquipInfo.EquipName ?? "").Trim();
            var library = GetLibraryCollection(kind);
            var target = GetModelCollection(kind);
            var checkedIds = GetCheckedIds(kind) ?? new List<object>();

            var selectedIds = checkedIds
                .Select(TryConvertToInt64)
                .Where(x => x > 0)
                .Distinct()
                .ToList();

            // Сохраняем уже существующие linked items,
            // потому что в них может быть FileData, а в library list его нет.
            var existingById = target
                .Where(x => x != null && x.Id > 0)
                .GroupBy(x => x.Id)
                .ToDictionary(g => g.Key, g => g.First());

            var rebuilt = new List<EquipmentInfoFileDto>();

            foreach (var id in selectedIds)
            {
                // 1) Если item уже есть в linked model — берём его,
                // чтобы не потерять FileData.
                if (existingById.TryGetValue(id, out var existing))
                {
                    rebuilt.Add(CloneFile(existing, equipName));
                    continue;
                }

                // 2) Иначе берём из library list
                var libItem = library.FirstOrDefault(x => x.Id == id);
                if (libItem == null)
                    continue;

                rebuilt.Add(CloneFile(libItem, equipName));
            }

            target.Clear();
            foreach (var item in rebuilt)
                target.Add(item);

            NormalizeSortOrder(target, equipName);

            switch (kind)
            {
                case InfoFileKind.Photo:
                    _vm.SelectedInfoPhotoFile = target.FirstOrDefault();
                    break;

                case InfoFileKind.Instruction:
                    _vm.SelectedInfoInstructionFile = target.FirstOrDefault();
                    break;

                case InfoFileKind.Scheme:
                    _vm.SelectedInfoSchemeFile = target.FirstOrDefault();
                    break;
            }
        }

        private async Task ApplyCheckedPhotoSelectionToModelAsync()
        {
            if (_vm.CurrentEquipInfo == null)
                return;

            var equipName = (_vm.CurrentEquipInfo.EquipName ?? "").Trim();
            var target = _vm.CurrentEquipInfo.Photos;
            var checkedIds = _vm.SelectedInfoPhotoLibraryIds ?? new List<object>();

            var selectedIds = checkedIds
                .Select(TryConvertToInt64)
                .Where(x => x > 0)
                .Distinct()
                .ToList();

            // Что уже есть в linked model — сохраняем, включая FileData
            var existingById = target
                .Where(x => x != null && x.Id > 0)
                .GroupBy(x => x.Id)
                .ToDictionary(g => g.Key, g => g.First());

            var rebuilt = new List<EquipmentInfoFileDto>();

            foreach (var id in selectedIds)
            {
                // Уже есть в linked model -> не теряем FileData
                if (existingById.TryGetValue(id, out var existing))
                {
                    rebuilt.Add(CloneFile(existing, equipName));
                    continue;
                }

                // Новый выбор из library -> догружаем полный record с FileData
                var fullPhoto = await _equipInfoService.GetLibraryFileByIdAsync(InfoFileKind.Photo, id);
                if (fullPhoto == null)
                    continue;

                rebuilt.Add(CloneFile(fullPhoto, equipName));
            }

            target.Clear();
            foreach (var item in rebuilt)
                target.Add(item);

            NormalizeSortOrder(target, equipName);

            _vm.SelectedInfoPhotoFile = target.FirstOrDefault();
        }

        private static long TryConvertToInt64(object? value)
        {
            if (value == null)
                return 0;

            return value switch
            {
                long l => l,
                int i => i,
                short s => s,
                string str when long.TryParse(str, out var parsed) => parsed,
                _ => 0
            };
        }

        private static void MergeAssetsIntoSelection(ObservableCollection<EquipmentInfoFileDto> target, IEnumerable<EquipmentInfoFileDto> assets, string equipName)
        {
            var existingIds = target
                .Where(x => x != null && x.Id > 0)
                .Select(x => x.Id)
                .ToHashSet();

            foreach (var asset in assets ?? Enumerable.Empty<EquipmentInfoFileDto>())
            {
                if (asset == null || asset.Id <= 0)
                    continue;

                if (existingIds.Contains(asset.Id))
                    continue;

                target.Add(CloneFile(asset, equipName));
                existingIds.Add(asset.Id);
            }

            NormalizeSortOrder(target, equipName);
        }

        public async Task SyncPhotoSelectionFromLibraryAsync()
        {
            if (_suppressLibrarySelectionSync)
                return;

            if (!_vm.IsInfoEditMode || _vm.CurrentEquipInfo == null)
                return;

            await ApplyCheckedPhotoSelectionToModelAsync();

            _vm.InfoStatusText = "Photo links updated.";
        }

        public async Task SyncCurrentDocumentSelectionFromLibraryAsync()
        {
            if (_suppressLibrarySelectionSync)
                return;

            if (!_vm.IsInfoEditMode || _vm.CurrentEquipInfo == null || !_vm.IsInfoDocumentPage)
                return;

            var kind = _vm.CurrentInfoPage == InfoPageKind.Scheme
                ? InfoFileKind.Scheme
                : InfoFileKind.Instruction;

            ApplyCheckedSelectionToModel(kind);

            _vm.CurrentInfoDocumentPreviewPath = null;
            await PrepareCurrentDocumentAsync();

            _vm.InfoStatusText = "Document links updated.";
        }

        private string ResolveSelectedEquipTypeGroupKey()
        {
            var group = _equipmentVm.SelectedListBoxEquipment?.TypeGroup ?? EquipTypeGroup.All;

            return group == EquipTypeGroup.All
                ? ""
                : group.ToString();
        }

        public async Task EnsureSelectedPhotoLoadedAsync()
        {
            var selected = _vm.SelectedInfoPhotoFile;
            if (selected == null)
                return;

            // Если байты уже есть - ничего делать не нужно
            if (selected.FileData is { Length: > 0 })
                return;

            if (selected.Id <= 0)
                return;

            try
            {
                var full = await _equipInfoService.GetLibraryFileByIdAsync(InfoFileKind.Photo, selected.Id);
                if (full?.FileData == null || full.FileData.Length == 0)
                {
                    _vm.InfoStatusText = $"Image '{selected.DisplayName}' is not available in DB.";
                    return;
                }

                // Догружаем недостающие поля прямо в выбранный объект
                selected.FileData = full.FileData;
                selected.FileHash = full.FileHash;
                selected.FileName = full.FileName;
                selected.UpdatedAt = full.UpdatedAt;
                selected.EquipTypeGroupKey = full.EquipTypeGroupKey;

                _vm.InfoStatusText = $"Image loaded: {selected.DisplayName}";
            }
            catch (Exception ex)
            {
                _vm.InfoStatusText = $"Image load error: {ex.Message}";
            }
        }
    }
}
