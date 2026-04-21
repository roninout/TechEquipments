using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TechEquipments
{
    /// <summary>
    /// Raw SQL сервис для Info.
    /// *_equip_photo / *_equip_instruction / *_equip_scheme — библиотеки файлов
    /// *_equip_info_photo / *_equip_info_instruction / *_equip_info_scheme — связи equipment -> file
    /// </summary>
    public sealed class EquipInfoService : IEquipInfoService
    {
        private readonly IDbContextFactory<PgDbContext> _dbFactory;

        private readonly string _schemaName;
        private readonly string _tablePrefix;

        private readonly string _qualifiedInfoTable;
        private readonly string _qualifiedPhotoTable;
        private readonly string _qualifiedInstructionTable;
        private readonly string _qualifiedSchemeTable;

        private readonly string _qualifiedInfoPhotoLinkTable;
        private readonly string _qualifiedInfoInstructionLinkTable;
        private readonly string _qualifiedInfoSchemeLinkTable;

        private readonly string _qualifiedInfoDocumentViewTable;

        public EquipInfoService(IDbContextFactory<PgDbContext> dbFactory, IConfiguration config)
        {
            _dbFactory = dbFactory;

            var configuredPrefix = (config["EquipInfo:TableName"] ?? "srd").Trim();
            (_schemaName, _tablePrefix) = ParseQualifiedPrefix(configuredPrefix);

            _qualifiedInfoTable = Qualify($"{_tablePrefix}_equip_info");
            _qualifiedPhotoTable = Qualify($"{_tablePrefix}_equip_photo");
            _qualifiedInstructionTable = Qualify($"{_tablePrefix}_equip_instruction");
            _qualifiedSchemeTable = Qualify($"{_tablePrefix}_equip_scheme");

            _qualifiedInfoPhotoLinkTable = Qualify($"{_tablePrefix}_equip_info_photo");
            _qualifiedInfoInstructionLinkTable = Qualify($"{_tablePrefix}_equip_info_instruction");
            _qualifiedInfoSchemeLinkTable = Qualify($"{_tablePrefix}_equip_info_scheme");

            _qualifiedInfoDocumentViewTable = Qualify($"{_tablePrefix}_equip_info_pdf_view");
        }

        public async Task EnsureTableAsync(CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            // Схема
            await db.Database.ExecuteSqlRawAsync(
                $@"CREATE SCHEMA IF NOT EXISTS {QuoteIdentifier(_schemaName)};", ct);

            // Главная карточка оборудования
            var sqlInfo = $@"
                            CREATE TABLE IF NOT EXISTS {_qualifiedInfoTable}
                            (
                                equip_name      text PRIMARY KEY,
                                install_time    timestamp NULL,
                                revision_time   timestamp NULL,
                                notes           text NULL,
                                notes_updated_at timestamp NULL,
                                updated_at      timestamp NOT NULL DEFAULT now()
                            );";

            // Общая библиотека фото
            var sqlPhoto = $@"
                            CREATE TABLE IF NOT EXISTS {_qualifiedPhotoTable}
                            (
                                id                  bigserial PRIMARY KEY,
                                equip_type_group    text NOT NULL,
                                file_name           text NOT NULL,
                                display_name        text NOT NULL,
                                file_hash           text NOT NULL,
                                file_data           bytea NOT NULL,
                                updated_at          timestamp NOT NULL DEFAULT now(),

                                CONSTRAINT uq_{_tablePrefix}_equip_photo_type_hash
                                    UNIQUE (equip_type_group, file_hash)
                            );";

            // Общая библиотека инструкций
            var sqlInstruction = $@"
                                    CREATE TABLE IF NOT EXISTS {_qualifiedInstructionTable}
                                    (
                                        id                  bigserial PRIMARY KEY,
                                        equip_type_group    text NOT NULL,
                                        file_name           text NOT NULL,
                                        display_name        text NOT NULL,
                                        file_hash           text NOT NULL,
                                        file_data           bytea NOT NULL,
                                        updated_at          timestamp NOT NULL DEFAULT now(),

                                        CONSTRAINT uq_{_tablePrefix}_equip_instruction_type_hash
                                            UNIQUE (equip_type_group, file_hash)
                                    );";

            // Общая библиотека схем
            var sqlScheme = $@"
                            CREATE TABLE IF NOT EXISTS {_qualifiedSchemeTable}
                            (
                                id                  bigserial PRIMARY KEY,
                                equip_type_group    text NOT NULL,
                                file_name           text NOT NULL,
                                display_name        text NOT NULL,
                                file_hash           text NOT NULL,
                                file_data           bytea NOT NULL,
                                updated_at          timestamp NOT NULL DEFAULT now(),

                                CONSTRAINT uq_{_tablePrefix}_equip_scheme_type_hash
                                    UNIQUE (equip_type_group, file_hash)
                            );";

            // Связь equipment -> photo
            var sqlInfoPhotoLink = $@"
                                    CREATE TABLE IF NOT EXISTS {_qualifiedInfoPhotoLinkTable}
                                    (
                                        equip_name      text NOT NULL REFERENCES {_qualifiedInfoTable}(equip_name) ON DELETE CASCADE,
                                        photo_id        bigint NOT NULL REFERENCES {_qualifiedPhotoTable}(id) ON DELETE CASCADE,
                                        sort_order      integer NOT NULL DEFAULT 0,

                                        PRIMARY KEY (equip_name, photo_id)
                                    );";

            // Связь equipment -> instruction
            var sqlInfoInstructionLink = $@"
                                            CREATE TABLE IF NOT EXISTS {_qualifiedInfoInstructionLinkTable}
                                            (
                                                equip_name      text NOT NULL REFERENCES {_qualifiedInfoTable}(equip_name) ON DELETE CASCADE,
                                                instruction_id  bigint NOT NULL REFERENCES {_qualifiedInstructionTable}(id) ON DELETE CASCADE,
                                                sort_order      integer NOT NULL DEFAULT 0,

                                                PRIMARY KEY (equip_name, instruction_id)
                                            );";

            // Связь equipment -> scheme
            var sqlInfoSchemeLink = $@"
                                    CREATE TABLE IF NOT EXISTS {_qualifiedInfoSchemeLinkTable}
                                    (
                                        equip_name      text NOT NULL REFERENCES {_qualifiedInfoTable}(equip_name) ON DELETE CASCADE,
                                        scheme_id       bigint NOT NULL REFERENCES {_qualifiedSchemeTable}(id) ON DELETE CASCADE,
                                        sort_order      integer NOT NULL DEFAULT 0,

                                        PRIMARY KEY (equip_name, scheme_id)
                                    );";

            // Сохранённая позиция просмотра PDF: одна запись на equipment + page kind + file id
            var sqlInfoPdfView = $@"
                                    CREATE TABLE IF NOT EXISTS {_qualifiedInfoDocumentViewTable}
                                    (
                                        equip_name       text NOT NULL REFERENCES {_qualifiedInfoTable}(equip_name) ON DELETE CASCADE,
                                        info_page_kind   text NOT NULL,
                                        file_id          bigint NOT NULL,
                                        file_name        text NOT NULL,
                                        page_number      integer NOT NULL,
                                        zoom_factor      double precision NOT NULL,
                                        anchor_x         double precision NOT NULL,
                                        anchor_y         double precision NOT NULL,
                                        updated_at       timestamp NOT NULL DEFAULT now(),

                                        PRIMARY KEY (equip_name, info_page_kind, file_id)
                                    );";

            await db.Database.ExecuteSqlRawAsync(sqlInfo, ct);
            await db.Database.ExecuteSqlRawAsync($@"ALTER TABLE {_qualifiedInfoTable}
                               ADD COLUMN IF NOT EXISTS notes text NULL;", ct);

            await db.Database.ExecuteSqlRawAsync($@"ALTER TABLE {_qualifiedInfoTable}
                               ADD COLUMN IF NOT EXISTS notes_updated_at timestamp NULL;", ct);

            // Мягкий backfill для уже существующих записей: если notes уже есть, а notes_updated_at пустой — подставим updated_at.
            await db.Database.ExecuteSqlRawAsync(
                $@"UPDATE {_qualifiedInfoTable}
                   SET notes_updated_at = updated_at
                   WHERE notes_updated_at IS NULL
                     AND NULLIF(BTRIM(notes), '') IS NOT NULL;", ct);

            await db.Database.ExecuteSqlRawAsync(sqlPhoto, ct);
            await db.Database.ExecuteSqlRawAsync(sqlInstruction, ct);
            await db.Database.ExecuteSqlRawAsync(sqlScheme, ct);
            await db.Database.ExecuteSqlRawAsync(sqlInfoPhotoLink, ct);
            await db.Database.ExecuteSqlRawAsync(sqlInfoInstructionLink, ct);
            await db.Database.ExecuteSqlRawAsync(sqlInfoSchemeLink, ct);
            await db.Database.ExecuteSqlRawAsync(sqlInfoPdfView, ct);

            // Индексы для library-комбобоксов по группе типа
            await db.Database.ExecuteSqlRawAsync(
                $@"CREATE INDEX IF NOT EXISTS ix_{_tablePrefix}_equip_photo_type
           ON {_qualifiedPhotoTable} (equip_type_group, display_name);", ct);

            await db.Database.ExecuteSqlRawAsync(
                $@"CREATE INDEX IF NOT EXISTS ix_{_tablePrefix}_equip_instruction_type
           ON {_qualifiedInstructionTable} (equip_type_group, display_name);", ct);

            await db.Database.ExecuteSqlRawAsync(
                $@"CREATE INDEX IF NOT EXISTS ix_{_tablePrefix}_equip_scheme_type
           ON {_qualifiedSchemeTable} (equip_type_group, display_name);", ct);

            await db.Database.ExecuteSqlRawAsync(
                $@"CREATE INDEX IF NOT EXISTS ix_{_tablePrefix}_equip_pdf_view_lookup
           ON {_qualifiedInfoDocumentViewTable} (equip_name, info_page_kind, file_id);", ct);
        }

        public async Task<EquipmentInfoDto> GetAsync(string equipName, CancellationToken ct = default)
        {
            equipName = (equipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(equipName))
                return EquipmentInfoDto.CreateEmpty("");

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var conn = db.Database.GetDbConnection();
            await EnsureConnectionOpenAsync(conn, ct);

            var model = EquipmentInfoDto.CreateEmpty(equipName);

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $@"
                                    SELECT
                                        equip_name,
                                        install_time,
                                        revision_time,
                                        notes,
                                        notes_updated_at,
                                        updated_at
                                    FROM {_qualifiedInfoTable}
                                    WHERE equip_name = @equip_name
                                    LIMIT 1;";

                AddParameter(cmd, "@equip_name", equipName);

                using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    model.EquipName = reader.IsDBNull(0) ? equipName : reader.GetString(0);
                    model.InstallTime = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTime>(1);
                    model.RevisionTime = reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTime>(2);
                    model.Notes = reader.IsDBNull(3) ? null : reader.GetString(3);
                    model.NotesUpdatedAt = reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTime>(4);
                    model.UpdatedAt = reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTime>(5);
                }
            }

            await LoadLinkedFilesAsync(conn, _qualifiedInfoPhotoLinkTable, _qualifiedPhotoTable, "photo_id", model.Photos, equipName, ct);
            await LoadLinkedFilesAsync(conn, _qualifiedInfoInstructionLinkTable, _qualifiedInstructionTable, "instruction_id", model.Instructions, equipName, ct);
            await LoadLinkedFilesAsync(conn, _qualifiedInfoSchemeLinkTable, _qualifiedSchemeTable, "scheme_id", model.Schemes, equipName, ct);

            return model;
        }

        public async Task SaveAsync(EquipmentInfoDto model, CancellationToken ct = default)
        {
            if (model == null)
                throw new ArgumentNullException(nameof(model));

            var equipName = (model.EquipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(equipName))
                throw new InvalidOperationException("EquipName is empty.");

            var notesForDb = string.IsNullOrWhiteSpace(model.Notes) ? null : model.Notes;

            NormalizeSortOrders(model.Photos, equipName);
            NormalizeSortOrders(model.Instructions, equipName);
            NormalizeSortOrders(model.Schemes, equipName);

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var conn = db.Database.GetDbConnection();
            await EnsureConnectionOpenAsync(conn, ct);

            await using var tx = await conn.BeginTransactionAsync(ct);

            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = $@"
                                        INSERT INTO {_qualifiedInfoTable}
                                        (
                                            equip_name,
                                            install_time,
                                            revision_time,
                                            notes,
                                            notes_updated_at,
                                            updated_at
                                        )
                                        VALUES
                                        (
                                            @equip_name,
                                            @install_time,
                                            @revision_time,
                                            @notes,
                                            CASE
                                                WHEN NULLIF(BTRIM(@notes), '') IS NULL THEN NULL
                                                ELSE now()
                                            END,
                                            now()
                                        )
                                        ON CONFLICT (equip_name)
                                        DO UPDATE SET
                                            install_time = EXCLUDED.install_time,
                                            revision_time = EXCLUDED.revision_time,
                                            notes = EXCLUDED.notes,
                                            notes_updated_at = CASE
                                                WHEN COALESCE({_qualifiedInfoTable}.notes, '') IS DISTINCT FROM COALESCE(EXCLUDED.notes, '')
                                                    THEN CASE
                                                        WHEN NULLIF(BTRIM(EXCLUDED.notes), '') IS NULL THEN NULL
                                                        ELSE now()
                                                    END
                                                ELSE {_qualifiedInfoTable}.notes_updated_at
                                            END,
                                            updated_at = now();";

                    AddParameter(cmd, "@equip_name", equipName);
                    AddParameter(cmd, "@install_time", model.InstallTime);
                    AddParameter(cmd, "@revision_time", model.RevisionTime);
                    AddParameter(cmd, "@notes", notesForDb);

                    await cmd.ExecuteNonQueryAsync(ct);
                }

                await ReplaceLinksAsync(conn, tx, _qualifiedInfoPhotoLinkTable, "photo_id", equipName, model.Photos, ct);
                await ReplaceLinksAsync(conn, tx, _qualifiedInfoInstructionLinkTable, "instruction_id", equipName, model.Instructions, ct);
                await ReplaceLinksAsync(conn, tx, _qualifiedInfoSchemeLinkTable, "scheme_id", equipName, model.Schemes, ct);

                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }

        public async Task<IReadOnlyList<EquipmentInfoFileDto>> GetLibraryAsync(InfoFileKind kind, string equipTypeGroupKey, CancellationToken ct = default)
        {
            equipTypeGroupKey = (equipTypeGroupKey ?? "").Trim();

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var conn = db.Database.GetDbConnection();
            await EnsureConnectionOpenAsync(conn, ct);

            var table = GetLibraryTable(kind);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                                SELECT
                                    id,
                                    equip_type_group,
                                    file_name,
                                    display_name,
                                    file_hash,
                                    updated_at
                                FROM {table}
                                WHERE equip_type_group = @equip_type_group
                                ORDER BY display_name, file_name;";

            AddParameter(cmd, "@equip_type_group", equipTypeGroupKey);

            using var reader = await cmd.ExecuteReaderAsync(ct);

            var result = new List<EquipmentInfoFileDto>();
            while (await reader.ReadAsync(ct))
            {
                result.Add(new EquipmentInfoFileDto
                {
                    Id = reader.IsDBNull(0) ? 0 : reader.GetInt64(0),
                    EquipTypeGroupKey = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    FileName = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    DisplayName = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    FileHash = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    UpdatedAt = reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTime>(5),
                    
                    FileData = null // ВАЖНО: library list не тянет file_data
                });
            }

            return result;
        }

        public async Task<EquipInfoLibraryAddResult> AddFilesToLibraryAsync(InfoFileKind kind, string equipTypeGroupKey, IEnumerable<string> filePaths, CancellationToken ct = default)
        {
            equipTypeGroupKey = (equipTypeGroupKey ?? "").Trim();

            if (string.IsNullOrWhiteSpace(equipTypeGroupKey))
                throw new InvalidOperationException("Equipment type group is empty.");

            var result = new EquipInfoLibraryAddResult();

            var normalizedPaths = (filePaths ?? Enumerable.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (normalizedPaths.Count == 0)
                return result;

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var conn = db.Database.GetDbConnection();
            await EnsureConnectionOpenAsync(conn, ct);

            var table = GetLibraryTable(kind);

            await using var tx = await conn.BeginTransactionAsync(ct);

            try
            {
                foreach (var path in normalizedPaths)
                {
                    if (!File.Exists(path))
                        continue;

                    var bytes = await File.ReadAllBytesAsync(path, ct);
                    if (bytes.Length == 0)
                        continue;

                    var hash = ComputeFileHash(bytes);

                    var existing = await FindLibraryItemByTypeAndHashAsync(
                        conn, tx, table, equipTypeGroupKey, hash, ct);

                    if (existing != null)
                    {
                        if (result.ResolvedAssets.All(x => x.Id != existing.Id))
                            result.ResolvedAssets.Add(existing);

                        result.ExistingInLibraryFileNames.Add(Path.GetFileName(path));
                        continue;
                    }

                    var inserted = await InsertLibraryItemAsync(
                        conn,
                        tx,
                        table,
                        equipTypeGroupKey,
                        Path.GetFileName(path),
                        Path.GetFileName(path),
                        hash,
                        bytes,
                        ct);

                    result.ResolvedAssets.Add(inserted);
                    result.AddedToLibraryFileNames.Add(inserted.FileName);
                }

                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }

            return result;
        }

        private string GetLibraryTable(InfoFileKind kind)
        {
            return kind switch
            {
                InfoFileKind.Photo => _qualifiedPhotoTable,
                InfoFileKind.Instruction => _qualifiedInstructionTable,
                InfoFileKind.Scheme => _qualifiedSchemeTable,
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
            };
        }

        private static async Task LoadLinkedFilesAsync(DbConnection conn, string qualifiedLinkTable, string qualifiedLibraryTable, string linkIdColumn, ObservableCollection<EquipmentInfoFileDto> target, string equipName, CancellationToken ct)
        {
            target.Clear();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                                SELECT
                                    lib.id,
                                    lib.file_name,
                                    lib.display_name,
                                    lib.file_hash,
                                    lib.file_data,
                                    link.sort_order,
                                    lib.updated_at,
                                    lib.equip_type_group
                                FROM {qualifiedLinkTable} link
                                INNER JOIN {qualifiedLibraryTable} lib
                                    ON lib.id = link.{linkIdColumn}
                                WHERE link.equip_name = @equip_name
                                ORDER BY link.sort_order, lib.display_name, lib.file_name;";

            AddParameter(cmd, "@equip_name", equipName);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                target.Add(new EquipmentInfoFileDto
                {
                    Id = reader.IsDBNull(0) ? 0 : reader.GetInt64(0),
                    EquipName = equipName,
                    FileName = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    DisplayName = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    FileHash = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    FileData = reader.IsDBNull(4) ? null : (byte[])reader.GetValue(4),
                    SortOrder = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                    UpdatedAt = reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTime>(6),
                    EquipTypeGroupKey = reader.IsDBNull(7) ? "" : reader.GetString(7)
                });
            }
        }

        private static async Task ReplaceLinksAsync(DbConnection conn, DbTransaction tx, string qualifiedLinkTable, string linkIdColumn, string equipName, IEnumerable<EquipmentInfoFileDto> files, CancellationToken ct)
        {
            using (var deleteCmd = conn.CreateCommand())
            {
                deleteCmd.Transaction = tx;
                deleteCmd.CommandText = $@"DELETE FROM {qualifiedLinkTable} WHERE equip_name = @equip_name;";
                AddParameter(deleteCmd, "@equip_name", equipName);
                await deleteCmd.ExecuteNonQueryAsync(ct);
            }

            var usedIds = new HashSet<long>();
            var sortOrder = 0;

            foreach (var file in files ?? Enumerable.Empty<EquipmentInfoFileDto>())
            {
                if (file == null || file.Id <= 0)
                    continue;

                if (!usedIds.Add(file.Id))
                    continue;

                using var insertCmd = conn.CreateCommand();
                insertCmd.Transaction = tx;
                insertCmd.CommandText = $@"
INSERT INTO {qualifiedLinkTable}
(
    equip_name,
    {linkIdColumn},
    sort_order
)
VALUES
(
    @equip_name,
    @file_id,
    @sort_order
);";

                AddParameter(insertCmd, "@equip_name", equipName);
                AddParameter(insertCmd, "@file_id", file.Id);
                AddParameter(insertCmd, "@sort_order", sortOrder++);

                await insertCmd.ExecuteNonQueryAsync(ct);
            }
        }

        private static async Task<EquipmentInfoFileDto> InsertLibraryItemAsync(DbConnection conn, DbTransaction tx, string qualifiedTable, string equipTypeGroupKey, string fileName, string displayName, string fileHash, byte[] fileData, CancellationToken ct)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $@"
                                INSERT INTO {qualifiedTable}
                                (
                                    equip_type_group,
                                    file_name,
                                    display_name,
                                    file_hash,
                                    file_data,
                                    updated_at
                                )
                                VALUES
                                (
                                    @equip_type_group,
                                    @file_name,
                                    @display_name,
                                    @file_hash,
                                    @file_data,
                                    now()
                                )
                                RETURNING
                                    id,
                                    equip_type_group,
                                    file_name,
                                    display_name,
                                    file_hash,
                                    file_data,
                                    updated_at;";

            AddParameter(cmd, "@equip_type_group", equipTypeGroupKey);
            AddParameter(cmd, "@file_name", fileName);
            AddParameter(cmd, "@display_name", displayName);
            AddParameter(cmd, "@file_hash", fileHash);
            AddParameter(cmd, "@file_data", fileData);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);

            return ReadLibraryItem(reader);
        }

        private static EquipmentInfoFileDto ReadLibraryItem(DbDataReader reader)
        {
            return new EquipmentInfoFileDto
            {
                Id = reader.IsDBNull(0) ? 0 : reader.GetInt64(0),
                EquipTypeGroupKey = reader.IsDBNull(1) ? "" : reader.GetString(1),
                FileName = reader.IsDBNull(2) ? "" : reader.GetString(2),
                DisplayName = reader.IsDBNull(3) ? "" : reader.GetString(3),
                FileHash = reader.IsDBNull(4) ? "" : reader.GetString(4),
                FileData = reader.IsDBNull(5) ? null : (byte[])reader.GetValue(5),
                UpdatedAt = reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTime>(6)
            };
        }

        private static void NormalizeSortOrders(IEnumerable<EquipmentInfoFileDto> files, string equipName)
        {
            if (files == null)
                return;

            var index = 0;
            foreach (var item in files)
            {
                if (item == null)
                    continue;

                item.EquipName = equipName;
                item.SortOrder = index++;
            }
        }

        private static string ComputeFileHash(byte[] data)
        {
            var hash = SHA256.HashData(data);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static async Task EnsureConnectionOpenAsync(DbConnection conn, CancellationToken ct)
        {
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync(ct);
        }

        private static void AddParameter(DbCommand cmd, string name, object? value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        /// <summary> Поддерживаем: - srd, - public.srd </summary>
        private static (string schema, string prefix) ParseQualifiedPrefix(string raw)
        {
            var parts = (raw ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            return parts.Length switch
            {
                1 => ("public", ValidateIdentifier(parts[0])),
                2 => (ValidateIdentifier(parts[0]), ValidateIdentifier(parts[1])),
                _ => throw new InvalidOperationException($"Invalid EquipInfo:TableName '{raw}'. Use 'prefix' or 'schema.prefix'.")
            };
        }

        private static string ValidateIdentifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException("SQL identifier is empty.");

            if (!Regex.IsMatch(value, @"^[A-Za-z_][A-Za-z0-9_]*$"))
                throw new InvalidOperationException(
                    $"Invalid SQL identifier '{value}'. Allowed: letters, digits and underscore.");

            return value;
        }

        private string Qualify(string tableName) => $"{QuoteIdentifier(_schemaName)}.{QuoteIdentifier(tableName)}";

        private static string QuoteIdentifier(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

        private static async Task<EquipmentInfoFileDto?> FindLibraryItemByTypeAndHashAsync(DbConnection conn, DbTransaction tx, string qualifiedTable, string equipTypeGroupKey, string hash, CancellationToken ct)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $@"
                                SELECT
                                    id,
                                    equip_type_group,
                                    file_name,
                                    display_name,
                                    file_hash,
                                    file_data,
                                    updated_at
                                FROM {qualifiedTable}
                                WHERE equip_type_group = @equip_type_group
                                  AND file_hash = @file_hash
                                LIMIT 1;";

            AddParameter(cmd, "@equip_type_group", equipTypeGroupKey);
            AddParameter(cmd, "@file_hash", hash);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;

            return ReadLibraryItem(reader);
        }

        public async Task<EquipmentInfoFileDto?> GetLibraryFileByIdAsync(InfoFileKind kind, long id, CancellationToken ct = default)
        {
            if (id <= 0)
                return null;

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var conn = db.Database.GetDbConnection();
            await EnsureConnectionOpenAsync(conn, ct);

            var table = GetLibraryTable(kind);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                                SELECT
                                    id,
                                    equip_type_group,
                                    file_name,
                                    display_name,
                                    file_hash,
                                    file_data,
                                    updated_at
                                FROM {table}
                                WHERE id = @id
                                LIMIT 1;";

            AddParameter(cmd, "@id", id);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;

            return new EquipmentInfoFileDto
            {
                Id = reader.IsDBNull(0) ? 0 : reader.GetInt64(0),
                EquipTypeGroupKey = reader.IsDBNull(1) ? "" : reader.GetString(1),
                FileName = reader.IsDBNull(2) ? "" : reader.GetString(2),
                DisplayName = reader.IsDBNull(3) ? "" : reader.GetString(3),
                FileHash = reader.IsDBNull(4) ? "" : reader.GetString(4),
                FileData = reader.IsDBNull(5) ? null : (byte[])reader.GetValue(5),
                UpdatedAt = reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTime>(6)
            };
        }

        public async Task<EquipmentInfoDocumentViewStateDto?> GetDocumentViewStateAsync(string equipName, InfoPageKind pageKind, long fileId, CancellationToken ct = default)
        {
            equipName = (equipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(equipName) || fileId <= 0)
                return null;

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var conn = db.Database.GetDbConnection();
            await EnsureConnectionOpenAsync(conn, ct);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                                SELECT
                                    equip_name,
                                    info_page_kind,
                                    file_id,
                                    file_name,
                                    page_number,
                                    zoom_factor,
                                    anchor_x,
                                    anchor_y,
                                    updated_at
                                FROM {_qualifiedInfoDocumentViewTable}
                                WHERE equip_name = @equip_name
                                  AND info_page_kind = @info_page_kind
                                  AND file_id = @file_id
                                LIMIT 1;";

            AddParameter(cmd, "@equip_name", equipName);
            AddParameter(cmd, "@info_page_kind", pageKind.ToString());
            AddParameter(cmd, "@file_id", fileId);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;

            return new EquipmentInfoDocumentViewStateDto
            {
                EquipName = reader.IsDBNull(0) ? "" : reader.GetString(0),
                InfoPageKind = Enum.TryParse<InfoPageKind>(reader.IsDBNull(1) ? "" : reader.GetString(1), out var parsedKind)
                    ? parsedKind
                    : pageKind,
                FileId = reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                FileName = reader.IsDBNull(3) ? "" : reader.GetString(3),
                PageNumber = reader.IsDBNull(4) ? 1 : reader.GetInt32(4),
                ZoomFactor = reader.IsDBNull(5) ? 1.0 : reader.GetDouble(5),
                AnchorX = reader.IsDBNull(6) ? 0.0 : reader.GetDouble(6),
                AnchorY = reader.IsDBNull(7) ? 0.0 : reader.GetDouble(7),
                UpdatedAt = reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTime>(8)
            };
        }

        public async Task SaveDocumentViewStateAsync(EquipmentInfoDocumentViewStateDto model, CancellationToken ct = default)
        {
            if (model == null)
                throw new ArgumentNullException(nameof(model));

            var equipName = (model.EquipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(equipName))
                throw new InvalidOperationException("EquipName is empty.");

            if (model.FileId <= 0)
                throw new InvalidOperationException("FileId must be greater than 0.");

            if (model.PageNumber <= 0)
                throw new InvalidOperationException("PageNumber must be greater than 0.");

            if (model.ZoomFactor <= 0)
                throw new InvalidOperationException("ZoomFactor must be greater than 0.");

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var conn = db.Database.GetDbConnection();
            await EnsureConnectionOpenAsync(conn, ct);

            // ВАЖНО:
            // saved PDF position ссылается по FK на info table,
            // поэтому для нового equipment сначала гарантируем базовую строку.
            await EnsureInfoRowExistsAsync(conn, equipName, ct);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                                INSERT INTO {_qualifiedInfoDocumentViewTable}
                                (
                                    equip_name,
                                    info_page_kind,
                                    file_id,
                                    file_name,
                                    page_number,
                                    zoom_factor,
                                    anchor_x,
                                    anchor_y,
                                    updated_at
                                )
                                VALUES
                                (
                                    @equip_name,
                                    @info_page_kind,
                                    @file_id,
                                    @file_name,
                                    @page_number,
                                    @zoom_factor,
                                    @anchor_x,
                                    @anchor_y,
                                    now()
                                )
                                ON CONFLICT (equip_name, info_page_kind, file_id)
                                DO UPDATE SET
                                    file_name   = EXCLUDED.file_name,
                                    page_number = EXCLUDED.page_number,
                                    zoom_factor = EXCLUDED.zoom_factor,
                                    anchor_x    = EXCLUDED.anchor_x,
                                    anchor_y    = EXCLUDED.anchor_y,
                                    updated_at  = now();";

            AddParameter(cmd, "@equip_name", equipName);
            AddParameter(cmd, "@info_page_kind", model.InfoPageKind.ToString());
            AddParameter(cmd, "@file_id", model.FileId);
            AddParameter(cmd, "@file_name", model.FileName);
            AddParameter(cmd, "@page_number", model.PageNumber);
            AddParameter(cmd, "@zoom_factor", model.ZoomFactor);
            AddParameter(cmd, "@anchor_x", model.AnchorX);
            AddParameter(cmd, "@anchor_y", model.AnchorY);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        /// <summary>
        /// Гарантирует наличие базовой строки equipment в info table.
        /// Нужно для сущностей, которые ссылаются на equip_name по FK
        /// (например, saved PDF view position), даже если карточка ещё ни разу не сохранялась вручную.
        /// </summary>
        private async Task EnsureInfoRowExistsAsync(DbConnection conn, string equipName, CancellationToken ct)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                                INSERT INTO {_qualifiedInfoTable}
                                (
                                    equip_name,
                                    install_time,
                                    revision_time,
                                    updated_at
                                )
                                VALUES
                                (
                                    @equip_name,
                                    NULL,
                                    NULL,
                                    now()
                                )
                                ON CONFLICT (equip_name)
                                DO NOTHING;";

            AddParameter(cmd, "@equip_name", equipName);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task<bool> DeleteLibraryFileAsync(InfoFileKind kind, long id, CancellationToken ct = default)
        {
            if (id <= 0)
                return false;

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var conn = db.Database.GetDbConnection();
            await EnsureConnectionOpenAsync(conn, ct);

            var table = GetLibraryTable(kind);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                                DELETE FROM {table}
                                WHERE id = @id;";

            AddParameter(cmd, "@id", id);

            var affected = await cmd.ExecuteNonQueryAsync(ct);
            return affected > 0;
        }
    }
}