using System;
using System.Collections.Generic;
using System.Linq;

namespace TechEquipments
{
    public sealed class InfoDocumentImportResult
    {
        public InfoFileKind Kind { get; set; }

        public string SheetName { get; set; } = "";
        public string BaseFolder { get; set; } = "";

        public int RowsScanned { get; set; }
        public int FileReferencesScanned { get; set; }

        /// <summary>
        /// Количество уникальных import jobs:
        /// PDF + InfoFileKind + EquipTypeGroup.
        /// </summary>
        public int ImportJobs { get; set; }

        /// <summary>
        /// Сколько новых файлов добавлено в library.
        /// Считается по уникальным файлам/type-group, не по equipment.
        /// </summary>
        public int AddedToDb { get; set; }

        /// <summary>
        /// Сколько существующих library-файлов обновлено более новой версией с диска.
        /// </summary>
        public int UpdatedInDb { get; set; }

        /// <summary>
        /// Сколько новых links создано к уже существующим/добавленным/обновлённым файлам.
        /// </summary>
        public int LinkedExisting { get; set; }

        /// <summary>
        /// Сколько links уже существовало.
        /// </summary>
        public int AlreadyLinked { get; set; }

        public int MissingFiles { get; set; }
        public int RowsWithoutEquipment { get; set; }
        public int EmptyRowsSkipped { get; set; }
        public int Errors { get; set; }

        public HashSet<string> AffectedEquipNames { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public List<string> ErrorMessages { get; } = new();

        public string ToMessage(string title)
        {
            var msg =
                $"{title} completed.\n\n" +
                $"Sheet: {SheetName}\n" +
                $"Base folder: {BaseFolder}\n\n" +
                $"Rows scanned: {RowsScanned}\n" +
                $"File references scanned: {FileReferencesScanned}\n" +
                $"Import jobs: {ImportJobs}\n" +
                $"Added to DB: {AddedToDb}\n" +
                $"Updated in DB: {UpdatedInDb}\n" +
                $"Links created: {LinkedExisting}\n" +
                $"Already linked: {AlreadyLinked}\n" +
                $"Missing files: {MissingFiles}\n" +
                $"Rows without equipment: {RowsWithoutEquipment}\n" +
                $"Empty rows skipped: {EmptyRowsSkipped}\n" +
                $"Errors: {Errors}";

            if (ErrorMessages.Count > 0)
            {
                msg += "\n\nFirst messages:\n" +
                       string.Join("\n", ErrorMessages.Take(15));
            }

            return msg;
        }
    }

    public enum InfoDocumentImportDbStatus
    {
        AddedToDbAndLinked,
        UpdatedExistingAndLinked,
        LinkedExisting,
        AlreadyLinked
    }

    public sealed class InfoDocumentImportDbResult
    {
        public long DocumentId { get; init; }
        public InfoDocumentImportDbStatus Status { get; init; }
    }

    /// <summary>
    /// Результат оптимизированного bulk-import:
    /// один PDF импортируется в library один раз и линкуется сразу к группе equipment.
    /// </summary>
    public sealed class InfoDocumentBulkImportDbResult
    {
        public long DocumentId { get; init; }

        public bool AddedToDb { get; init; }
        public bool UpdatedInDb { get; init; }

        public int LinksCreated { get; init; }
        public int AlreadyLinked { get; init; }

        public IReadOnlyCollection<string> AffectedEquipNames { get; init; } =
            Array.Empty<string>();
    }
}