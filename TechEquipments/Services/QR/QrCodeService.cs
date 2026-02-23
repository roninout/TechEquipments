using QRCoder;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TechEquipments
{
    public sealed class QrCodeService : IQrCodeService
    {
        /// <summary>
        /// Возвращает детерминированный путь к файлу QR (без создания папок/файлов).
        /// </summary>
        public string GetExpectedQrPngPath(string text, string? outputDirectory = null, string? fileNameWithoutExt = null)
        {
            text = (text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("QR text is empty.", nameof(text));

            var dir = string.IsNullOrWhiteSpace(outputDirectory)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "QRCodes")
                : outputDirectory.Trim();

            var baseName = string.IsNullOrWhiteSpace(fileNameWithoutExt)
                ? MakeSafeFileName(text)
                : MakeSafeFileName(fileNameWithoutExt);

            return Path.Combine(dir, baseName + ".png");
        }

        /// <summary>
        /// Генерирует QR в PNG и сохраняет на диск.
        /// Если файл уже существует — не создаёт дубликаты.
        /// </summary>
        public async Task<string> GenerateQrPngAsync(
            string text,
            string? outputDirectory = null,
            string? fileNameWithoutExt = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            var path = GetExpectedQrPngPath(text, outputDirectory, fileNameWithoutExt);

            // Если уже есть — ничего не делаем, просто возвращаем путь (не плодим дубликаты)
            if (File.Exists(path))
                return path;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            using var gen = new QRCodeGenerator();
            using var data = gen.CreateQrCode(text.Trim(), QRCodeGenerator.ECCLevel.Q);
            var png = new PngByteQRCode(data);

            byte[] bytes = png.GetGraphic(pixelsPerModule: 12);

            await File.WriteAllBytesAsync(path, bytes, ct);
            return path;
        }

        /// <summary>
        /// Делает имя файла безопасным для файловой системы.
        /// </summary>
        private static string MakeSafeFileName(string text)
        {
            var name = (text ?? "").Trim();
            if (name.Length == 0) name = "qr";

            var invalid = Path.GetInvalidFileNameChars();
            var safe = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());

            if (safe.Length > 80)
                safe = safe.Substring(0, 80);

            return safe;
        }

    }
}
