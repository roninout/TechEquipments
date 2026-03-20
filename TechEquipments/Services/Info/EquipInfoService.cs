using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System;
using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TechEquipments
{
    /// <summary>
    /// Raw SQL сервис для таблицы EquipInfo.
    /// Имя таблицы берётся из appsettings.json.
    /// Не используем DbSet, потому что имя таблицы динамическое.
    /// </summary>
    public sealed class EquipInfoService : IEquipInfoService
    {
        private readonly IDbContextFactory<PgDbContext> _dbFactory;

        private readonly string _schemaName;
        private readonly string _tableName;
        private readonly string _qualifiedTableName;

        public EquipInfoService(IDbContextFactory<PgDbContext> dbFactory, IConfiguration config)
        {
            _dbFactory = dbFactory;

            var configuredTableName = (config["EquipInfo:TableName"] ?? "srd_equip_info").Trim();
            (_schemaName, _tableName) = ParseQualifiedName(configuredTableName);
            _qualifiedTableName = $"{QuoteIdentifier(_schemaName)}.{QuoteIdentifier(_tableName)}";
        }

        public async Task EnsureTableAsync(CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var createSchemaSql = $@"CREATE SCHEMA IF NOT EXISTS {QuoteIdentifier(_schemaName)};";
            await db.Database.ExecuteSqlRawAsync(createSchemaSql, ct);

            var sql = $@"
                CREATE TABLE IF NOT EXISTS {_qualifiedTableName}
                (
                    equip_name      text PRIMARY KEY,
                    install_time    timestamp NULL,
                    revision_time   timestamp NULL,
                    image_data      bytea NULL,
                    image_file_name text NULL,
                    pdf_data        bytea NULL,
                    pdf_file_name   text NULL,
                    updated_at      timestamp NOT NULL DEFAULT now()
                );";

            await db.Database.ExecuteSqlRawAsync(sql, ct);
        }

        public async Task<EquipmentInfoDto> GetAsync(string equipName, CancellationToken ct = default)
        {
            equipName = (equipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(equipName))
                return EquipmentInfoDto.CreateEmpty("");

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var conn = db.Database.GetDbConnection();
            await EnsureConnectionOpenAsync(conn, ct);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT
                    equip_name,
                    install_time,
                    revision_time,
                    image_data,
                    image_file_name,
                    pdf_data,
                    pdf_file_name,
                    updated_at
                FROM {_qualifiedTableName}
                WHERE equip_name = @equip_name
                LIMIT 1;";

            AddParameter(cmd, "@equip_name", equipName);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return EquipmentInfoDto.CreateEmpty(equipName);

            return new EquipmentInfoDto
            {
                EquipName = reader.IsDBNull(0) ? equipName : reader.GetString(0),
                InstallTime = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTime>(1),
                RevisionTime = reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTime>(2),
                ImageData = reader.IsDBNull(3) ? null : (byte[])reader.GetValue(3),
                ImageFileName = reader.IsDBNull(4) ? null : reader.GetString(4),
                PdfData = reader.IsDBNull(5) ? null : (byte[])reader.GetValue(5),
                PdfFileName = reader.IsDBNull(6) ? null : reader.GetString(6),
                UpdatedAt = reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTime>(7)
            };
        }

        public async Task SaveAsync(EquipmentInfoDto model, CancellationToken ct = default)
        {
            if (model == null)
                throw new ArgumentNullException(nameof(model));

            var equipName = (model.EquipName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(equipName))
                throw new InvalidOperationException("EquipName is empty.");

            var imageFileName = model.ImageData is { Length: > 0 } ? model.ImageFileName : null;
            var pdfFileName = model.PdfData is { Length: > 0 } ? model.PdfFileName : null;

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var conn = db.Database.GetDbConnection();
            await EnsureConnectionOpenAsync(conn, ct);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                INSERT INTO {_qualifiedTableName}
                (
                    equip_name,
                    install_time,
                    revision_time,
                    image_data,
                    image_file_name,
                    pdf_data,
                    pdf_file_name,
                    updated_at
                )
                VALUES
                (
                    @equip_name,
                    @install_time,
                    @revision_time,
                    @image_data,
                    @image_file_name,
                    @pdf_data,
                    @pdf_file_name,
                    now()
                )
                ON CONFLICT (equip_name)
                DO UPDATE SET
                    install_time    = EXCLUDED.install_time,
                    revision_time   = EXCLUDED.revision_time,
                    image_data      = EXCLUDED.image_data,
                    image_file_name = EXCLUDED.image_file_name,
                    pdf_data        = EXCLUDED.pdf_data,
                    pdf_file_name   = EXCLUDED.pdf_file_name,
                    updated_at      = now();";

            AddParameter(cmd, "@equip_name", equipName);
            AddParameter(cmd, "@install_time", model.InstallTime);
            AddParameter(cmd, "@revision_time", model.RevisionTime);
            AddParameter(cmd, "@image_data", model.ImageData);
            AddParameter(cmd, "@image_file_name", imageFileName);
            AddParameter(cmd, "@pdf_data", model.PdfData);
            AddParameter(cmd, "@pdf_file_name", pdfFileName);

            await cmd.ExecuteNonQueryAsync(ct);
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

        /// <summary>
        /// Поддерживаем:
        /// - srd_equip_info
        /// - public.srd_equip_info
        /// </summary>
        private static (string schema, string table) ParseQualifiedName(string raw)
        {
            var parts = (raw ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            return parts.Length switch
            {
                1 => ("public", ValidateIdentifier(parts[0])),
                2 => (ValidateIdentifier(parts[0]), ValidateIdentifier(parts[1])),
                _ => throw new InvalidOperationException($"Invalid EquipInfo:TableName '{raw}'. Use 'table' or 'schema.table'.")
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

        private static string QuoteIdentifier(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}