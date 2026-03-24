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

namespace TechEquipments
{
    /// <summary>
    /// Контроллер вкладки Info:
    /// - загрузка карточки
    /// - edit/save
    /// - page switching
    /// - работа с фото / instruction / scheme
    /// - cache PDF рядом с exe: .\Instruction и .\Schemes
    /// </summary>
    public sealed class InfoController
    {
        private readonly IEquipInfoService _equipInfoService;
        private readonly IInfoHost _host;
        private int _loadCurrentRequestId;
        private bool _suppressLibrarySelectionSync;

        public InfoController(IEquipInfoService equipInfoService, IInfoHost host)
        {
            _equipInfoService = equipInfoService ?? throw new ArgumentNullException(nameof(equipInfoService));
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        private string ResolveSelectedEquipForInfo()
        {
            var text = (_host.EquipName ?? "").Trim();

            var sel = (_host.SelectedListBoxEquipment?.Equipment ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(sel))
                text = sel;

            return text;
        }

        public async Task LoadCurrentAsync()
        {
            // Версия запроса.
            // Нужна, чтобы более старый async-вызов не перетёр результат нового.
            var requestId = Interlocked.Increment(ref _loadCurrentRequestId);

            var equipName = ResolveSelectedEquipForInfo();

            void ClearInfoUiState()
            {
                _host.SelectedInfoPhotoFile = null;
                _host.SelectedInfoInstructionFile = null;
                _host.SelectedInfoSchemeFile = null;

                // Важно:
                // очищаем checked-combo выбор,
                // иначе он может остаться от предыдущего equipment.
                _host.SelectedInfoPhotoLibraryIds = new List<object>();
                _host.SelectedInfoInstructionLibraryIds = new List<object>();
                _host.SelectedInfoSchemeLibraryIds = new List<object>();

                _host.CurrentInfoDocumentPreviewPath = null;
                _host.InfoDocumentMessage = "";
                _host.IsInfoDocumentExportVisible = false;
                _host.IsInfoEditMode = false;
            }

            if (string.IsNullOrWhiteSpace(equipName))
            {
                _host.CurrentEquipInfo = null;
                ClearInfoUiState();
                _host.InfoStatusText = "";
                return;
            }

            if (!_host.IsDbConnected)
            {
                _host.CurrentEquipInfo = EquipmentInfoDto.CreateEmpty(equipName);
                ClearInfoUiState();
                _host.InfoStatusText = "Info: DB is disconnected.";
                return;
            }

            try
            {
                _host.IsInfoLoading = true;
                _host.InfoStatusText = $"Loading info: {equipName}...";

                // Сначала читаем карточку в локальную переменную.
                // Не применяем её в UI, пока не убедимся, что этот запрос ещё актуален.
                var info = await _equipInfoService.GetAsync(equipName);

                if (requestId != Volatile.Read(ref _loadCurrentRequestId))
                    return;

                await LoadLibrariesAsync();

                if (requestId != Volatile.Read(ref _loadCurrentRequestId))
                    return;

                _host.CurrentEquipInfo = info;

                SyncCheckedSelectionsFromCurrentModel();

                _host.SelectedInfoPhotoFile = info.Photos.FirstOrDefault();
                _host.SelectedInfoInstructionFile = info.Instructions.FirstOrDefault();
                _host.SelectedInfoSchemeFile = info.Schemes.FirstOrDefault();

                _host.CurrentInfoDocumentPreviewPath = null;
                _host.InfoDocumentMessage = "";
                _host.IsInfoDocumentExportVisible = false;

                _host.IsInfoEditMode = false;
                _host.InfoStatusText = $"Info loaded: {equipName}";

                if (_host.IsInfoDocumentPage)
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

                _host.CurrentEquipInfo = EquipmentInfoDto.CreateEmpty(equipName);
                ClearInfoUiState();
                _host.InfoStatusText = $"Info error: {ex.Message}";
            }
            finally
            {
                // Только самый свежий запрос имеет право выключать loading.
                if (requestId == Volatile.Read(ref _loadCurrentRequestId))
                    _host.IsInfoLoading = false;
            }
        }

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

                NormalizeSortOrder(_host.CurrentEquipInfo.Photos, equipName);
                NormalizeSortOrder(_host.CurrentEquipInfo.Instructions, equipName);
                NormalizeSortOrder(_host.CurrentEquipInfo.Schemes, equipName);

                ValidateNoDuplicates(_host.CurrentEquipInfo.Photos, "photo");
                ValidateNoDuplicates(_host.CurrentEquipInfo.Instructions, "instruction");
                ValidateNoDuplicates(_host.CurrentEquipInfo.Schemes, "scheme");

                await _equipInfoService.SaveAsync(_host.CurrentEquipInfo);

                _host.IsInfoEditMode = false;
                _host.InfoStatusText = $"Info saved: {equipName}";

                if (_host.IsInfoDocumentPage)
                    await PrepareCurrentDocumentAsync();
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

        public async Task LoadPhotoFilesAsync()
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
                Title = "Select image files",
                Filter = "Image files (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = true
            };

            if (dlg.ShowDialog(_host.OwnerWindow) != true)
                return;

            var equipTypeGroupKey = ResolveSelectedEquipTypeGroupKey();
            if (string.IsNullOrWhiteSpace(equipTypeGroupKey))
                return;

            var addResult = await _equipInfoService.AddFilesToLibraryAsync(
                InfoFileKind.Photo,
                equipTypeGroupKey,
                dlg.FileNames);

            await LoadLibrariesAsync();

            MergeAssetsIntoSelection(_host.CurrentEquipInfo.Photos, addResult.ResolvedAssets, equipName);
            SyncCheckedSelectionsFromCurrentModel();

            // После Add выбираем именно добавленный/подцепленный файл, а не первый в списке.
            var selectedPhoto = addResult.ResolvedAssets.LastOrDefault(asset => asset != null && asset.Id > 0);

            if (selectedPhoto != null)
            {
                _host.SelectedInfoPhotoFile = _host.CurrentEquipInfo.Photos
                    .FirstOrDefault(x => x.Id == selectedPhoto.Id);
            }
            else
            {
                _host.SelectedInfoPhotoFile = _host.CurrentEquipInfo.Photos.FirstOrDefault();
            }

            if (addResult.ExistingInLibraryFileNames.Count > 0)
            {
                DXMessageBox.Show(
                    _host.OwnerWindow,
                    "These image files already existed in the shared library and were linked to the equipment:\n\n" +
                    string.Join("\n", addResult.ExistingInLibraryFileNames),
                    "Existing images",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            _host.InfoStatusText = $"Images linked: {_host.CurrentEquipInfo.Photos.Count}. New in library: {addResult.AddedToLibraryFileNames.Count}.";
        }

        public void RemoveSelectedPhoto()
        {
            if (!_host.IsInfoEditMode || _host.CurrentEquipInfo == null)
                return;

            var selected = _host.SelectedInfoPhotoFile;
            if (selected == null)
                return;

            var list = _host.CurrentEquipInfo.Photos;
            var index = list.IndexOf(selected);

            if (index >= 0)
                list.RemoveAt(index);

            NormalizeSortOrder(list, _host.CurrentEquipInfo.EquipName);

            _host.SelectedInfoPhotoFile =
                index >= 0 && index < list.Count ? list[index] : list.LastOrDefault();

            _host.InfoStatusText = "Photo removed from current card.";

            SyncCheckedSelectionsFromCurrentModel();
        }

        public async Task LoadCurrentDocumentFilesAsync()
        {
            if (!_host.IsInfoEditMode || !_host.IsInfoDocumentPage)
                return;

            var equipName = ResolveSelectedEquipForInfo();
            if (string.IsNullOrWhiteSpace(equipName))
                return;

            _host.CurrentEquipInfo ??= EquipmentInfoDto.CreateEmpty(equipName);
            _host.CurrentEquipInfo.EquipName = equipName;

            var kind = _host.CurrentInfoPage == InfoPageKind.Scheme
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

            if (dlg.ShowDialog(_host.OwnerWindow) != true)
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
                _host.SelectedInfoInstructionFile = first;
            else
                _host.SelectedInfoSchemeFile = first;

            await PrepareCurrentDocumentAsync();

            if (addResult.ExistingInLibraryFileNames.Count > 0)
            {
                DXMessageBox.Show(
                    _host.OwnerWindow,
                    "These PDF files already existed in the shared library and were linked to the equipment:\n\n" +
                    string.Join("\n", addResult.ExistingInLibraryFileNames),
                    "Existing PDF files",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            _host.InfoStatusText =
                $"Documents linked: {target.Count}. New in library: {addResult.AddedToLibraryFileNames.Count}.";
        }

        public async Task RemoveCurrentDocumentAsync()
        {
            if (!_host.IsInfoEditMode || !_host.IsInfoDocumentPage)
                return;

            var selected = GetCurrentSelectedDocument();
            if (selected == null)
                return;

            var list = GetCurrentDocumentCollection();
            var index = list.IndexOf(selected);

            if (index >= 0)
                list.RemoveAt(index);

            NormalizeSortOrder(list, _host.CurrentEquipInfo?.EquipName ?? "");

            var newSelected =
                index >= 0 && index < list.Count ? list[index] : list.LastOrDefault();

            SetCurrentSelectedDocument(newSelected);
            _host.CurrentInfoDocumentPreviewPath = null;

            await PrepareCurrentDocumentAsync();

            _host.InfoStatusText = "Document removed from current card.";

            SyncCheckedSelectionsFromCurrentModel();
        }

        public async Task ShowPageAsync(InfoPageKind page)
        {
            _host.CurrentInfoPage = page;
            _host.CurrentInfoDocumentPreviewPath = null;
            _host.InfoDocumentMessage = "";
            _host.IsInfoDocumentExportVisible = false;

            if (page == InfoPageKind.General)
                return;

            if (GetCurrentSelectedDocument() == null)
                SetCurrentSelectedDocument(GetCurrentDocumentCollection().FirstOrDefault());

            await PrepareCurrentDocumentAsync();
        }

        public Task PrepareCurrentDocumentAsync()
        {
            _host.CurrentInfoDocumentPreviewPath = null;

            if (!_host.IsInfoDocumentPage)
                return Task.CompletedTask;

            var model = _host.CurrentEquipInfo;
            if (model == null)
            {
                _host.InfoDocumentMessage = "No equipment selected.";
                _host.IsInfoDocumentExportVisible = false;
                return Task.CompletedTask;
            }

            var selected = GetCurrentSelectedDocument();
            if (selected == null)
            {
                _host.InfoDocumentMessage = _host.CurrentInfoPage == InfoPageKind.Scheme
                    ? "No scheme file is stored for this equipment."
                    : "No instruction file is stored for this equipment.";

                _host.IsInfoDocumentExportVisible = false;
                return Task.CompletedTask;
            }

            var equipName = (model.EquipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(equipName))
            {
                _host.InfoDocumentMessage = "Equipment name is empty.";
                _host.IsInfoDocumentExportVisible = false;
                return Task.CompletedTask;
            }

            var expectedPath = GetExpectedDocumentPath(_host.CurrentInfoPage, selected.EquipTypeGroupKey, selected.FileName);

            if (File.Exists(expectedPath))
            {
                _host.CurrentInfoDocumentPreviewPath = expectedPath;
                _host.InfoDocumentMessage = "";
                _host.IsInfoDocumentExportVisible = false;
                return Task.CompletedTask;
            }

            if (selected.FileData is { Length: > 0 })
            {
                var folderName = _host.CurrentInfoPage == InfoPageKind.Scheme ? "Schemes" : "Instruction";

                _host.InfoDocumentMessage = $"File '{selected.FileName}' is stored in DB but not cached locally. Click 'Export PDF' to save it to .\\{folderName} and open it.";

                _host.IsInfoDocumentExportVisible = true;
                return Task.CompletedTask;
            }

            _host.InfoDocumentMessage = $"File '{selected.FileName}' is not available in DB.";
            _host.IsInfoDocumentExportVisible = false;
            return Task.CompletedTask;
        }

        public async Task ExportCurrentDocumentAsync()
        {
            if (!_host.IsInfoDocumentPage)
                return;

            var model = _host.CurrentEquipInfo;
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
                _host.IsInfoLoading = true;

                var path = await EnsureDocumentCachedFromMemoryAsync(_host.CurrentInfoPage, selected);

                _host.CurrentInfoDocumentPreviewPath = path;
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

        private ObservableCollection<EquipmentInfoFileDto> GetCurrentDocumentCollection()
        {
            _host.CurrentEquipInfo ??= EquipmentInfoDto.CreateEmpty(ResolveSelectedEquipForInfo());

            return _host.CurrentInfoPage switch
            {
                InfoPageKind.Instruction => _host.CurrentEquipInfo.Instructions,
                InfoPageKind.Scheme => _host.CurrentEquipInfo.Schemes,
                _ => _host.CurrentEquipInfo.Instructions
            };
        }

        private EquipmentInfoFileDto? GetCurrentSelectedDocument()
        {
            return _host.CurrentInfoPage switch
            {
                InfoPageKind.Instruction => _host.SelectedInfoInstructionFile,
                InfoPageKind.Scheme => _host.SelectedInfoSchemeFile,
                _ => null
            };
        }

        private void SetCurrentSelectedDocument(EquipmentInfoFileDto? file)
        {
            switch (_host.CurrentInfoPage)
            {
                case InfoPageKind.Instruction:
                    _host.SelectedInfoInstructionFile = file;
                    break;

                case InfoPageKind.Scheme:
                    _host.SelectedInfoSchemeFile = file;
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
                _host.AvailableInfoPhotoLibrary.Clear();
                _host.AvailableInfoInstructionLibrary.Clear();
                _host.AvailableInfoSchemeLibrary.Clear();
                return;
            }

            var photos = await _equipInfoService.GetLibraryAsync(InfoFileKind.Photo, equipTypeGroupKey);
            var instructions = await _equipInfoService.GetLibraryAsync(InfoFileKind.Instruction, equipTypeGroupKey);
            var schemes = await _equipInfoService.GetLibraryAsync(InfoFileKind.Scheme, equipTypeGroupKey);

            ReplaceCollection(_host.AvailableInfoPhotoLibrary, photos);
            ReplaceCollection(_host.AvailableInfoInstructionLibrary, instructions);
            ReplaceCollection(_host.AvailableInfoSchemeLibrary, schemes);
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
                _host.SelectedInfoPhotoLibraryIds = ToCheckedIds(_host.CurrentEquipInfo?.Photos);
                _host.SelectedInfoInstructionLibraryIds = ToCheckedIds(_host.CurrentEquipInfo?.Instructions);
                _host.SelectedInfoSchemeLibraryIds = ToCheckedIds(_host.CurrentEquipInfo?.Schemes);
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
                InfoFileKind.Photo => _host.AvailableInfoPhotoLibrary,
                InfoFileKind.Instruction => _host.AvailableInfoInstructionLibrary,
                InfoFileKind.Scheme => _host.AvailableInfoSchemeLibrary,
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
            };
        }

        private ObservableCollection<EquipmentInfoFileDto> GetModelCollection(InfoFileKind kind)
        {
            _host.CurrentEquipInfo ??= EquipmentInfoDto.CreateEmpty(ResolveSelectedEquipForInfo());

            return kind switch
            {
                InfoFileKind.Photo => _host.CurrentEquipInfo.Photos,
                InfoFileKind.Instruction => _host.CurrentEquipInfo.Instructions,
                InfoFileKind.Scheme => _host.CurrentEquipInfo.Schemes,
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
            };
        }

        private List<object>? GetCheckedIds(InfoFileKind kind)
        {
            return kind switch
            {
                InfoFileKind.Photo => _host.SelectedInfoPhotoLibraryIds,
                InfoFileKind.Instruction => _host.SelectedInfoInstructionLibraryIds,
                InfoFileKind.Scheme => _host.SelectedInfoSchemeLibraryIds,
                _ => null
            };
        }

        private void ApplyCheckedSelectionToModel(InfoFileKind kind)
        {
            if (_host.CurrentEquipInfo == null)
                return;

            var equipName = (_host.CurrentEquipInfo.EquipName ?? "").Trim();
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
                    _host.SelectedInfoPhotoFile = target.FirstOrDefault();
                    break;

                case InfoFileKind.Instruction:
                    _host.SelectedInfoInstructionFile = target.FirstOrDefault();
                    break;

                case InfoFileKind.Scheme:
                    _host.SelectedInfoSchemeFile = target.FirstOrDefault();
                    break;
            }
        }

        private async Task ApplyCheckedPhotoSelectionToModelAsync()
        {
            if (_host.CurrentEquipInfo == null)
                return;

            var equipName = (_host.CurrentEquipInfo.EquipName ?? "").Trim();
            var target = _host.CurrentEquipInfo.Photos;
            var checkedIds = _host.SelectedInfoPhotoLibraryIds ?? new List<object>();

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

            _host.SelectedInfoPhotoFile = target.FirstOrDefault();
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

            if (!_host.IsInfoEditMode || _host.CurrentEquipInfo == null)
                return;

            await ApplyCheckedPhotoSelectionToModelAsync();

            _host.InfoStatusText = "Photo links updated.";
        }

        public async Task SyncCurrentDocumentSelectionFromLibraryAsync()
        {
            if (_suppressLibrarySelectionSync)
                return;

            if (!_host.IsInfoEditMode || _host.CurrentEquipInfo == null || !_host.IsInfoDocumentPage)
                return;

            var kind = _host.CurrentInfoPage == InfoPageKind.Scheme
                ? InfoFileKind.Scheme
                : InfoFileKind.Instruction;

            ApplyCheckedSelectionToModel(kind);

            _host.CurrentInfoDocumentPreviewPath = null;
            await PrepareCurrentDocumentAsync();

            _host.InfoStatusText = "Document links updated.";
        }

        private string ResolveSelectedEquipTypeGroupKey()
        {
            var group = _host.SelectedListBoxEquipment?.TypeGroup ?? EquipTypeGroup.All;

            return group == EquipTypeGroup.All
                ? ""
                : group.ToString();
        }
    }
}