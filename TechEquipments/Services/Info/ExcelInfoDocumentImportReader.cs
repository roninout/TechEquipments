using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace TechEquipments
{
    internal static class ExcelInfoDocumentImportReader
    {
        private static readonly XNamespace MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private static readonly XNamespace PackageRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";

        public static InfoDocumentExcelImportPlan Read(string excelPath, string sheetName)
        {
            if (string.IsNullOrWhiteSpace(excelPath) || !File.Exists(excelPath))
                throw new FileNotFoundException("Excel file not found.", excelPath);

            using var archive = ZipFile.OpenRead(excelPath);

            var sharedStrings = ReadSharedStrings(archive);
            var sheetPath = ResolveSheetPath(archive, sheetName);

            var sheetEntry = archive.GetEntry(sheetPath)
                ?? throw new InvalidOperationException($"Sheet XML was not found: {sheetPath}");

            XDocument sheetDoc;

            using (var stream = sheetEntry.Open())
                sheetDoc = XDocument.Load(stream);

            var cells = ReadCells(sheetDoc, sharedStrings);

            var plan = new InfoDocumentExcelImportPlan
            {
                SheetName = sheetName,
                BaseFolder = GetCell(cells, "A1").Trim()
            };

            if (string.IsNullOrWhiteSpace(plan.BaseFolder))
                throw new InvalidOperationException($"{sheetName}!A1 does not contain base PDF folder.");

            // Header ожидаем в строке 3:
            // A3 = Station
            // B3 = Scheme
            var stationHeader = GetCell(cells, "A3").Trim();
            var schemeHeader = GetCell(cells, "B3").Trim();

            if (!stationHeader.Equals("Station", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{sheetName}!A3 must be 'Station'.");

            if (!schemeHeader.Equals("Scheme", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{sheetName}!B3 must be 'Scheme'.");

            var maxRow = cells.Keys
                .Select(x => TryGetRowIndex(x))
                .DefaultIfEmpty(0)
                .Max();

            for (var row = 4; row <= maxRow; row++)
            {
                var station = GetCell(cells, $"A{row}").Trim();
                var schemeList = GetCell(cells, $"B{row}").Trim();

                if (string.IsNullOrWhiteSpace(station) &&
                    string.IsNullOrWhiteSpace(schemeList))
                {
                    continue;
                }

                var importRow = new InfoDocumentExcelImportRow
                {
                    RowNumber = row,
                    Station = station
                };

                foreach (var fileName in SplitFileList(schemeList))
                    importRow.FileNames.Add(fileName);

                plan.Rows.Add(importRow);
            }

            return plan;
        }

        private static Dictionary<string, string> ReadSharedStrings(ZipArchive archive)
        {
            var result = new Dictionary<string, string>();

            var entry = archive.GetEntry("xl/sharedStrings.xml");
            if (entry == null)
                return result;

            XDocument doc;

            using (var stream = entry.Open())
                doc = XDocument.Load(stream);

            var index = 0;

            foreach (var si in doc.Descendants(MainNs + "si"))
            {
                // Может быть несколько <t> внутри rich text.
                var text = string.Concat(si.Descendants(MainNs + "t").Select(t => t.Value));
                result[index.ToString(CultureInfo.InvariantCulture)] = text;
                index++;
            }

            return result;
        }

        private static string ResolveSheetPath(ZipArchive archive, string sheetName)
        {
            var workbookEntry = archive.GetEntry("xl/workbook.xml")
                ?? throw new InvalidOperationException("xl/workbook.xml was not found.");

            var relsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels")
                ?? throw new InvalidOperationException("xl/_rels/workbook.xml.rels was not found.");

            XDocument workbookDoc;
            XDocument relsDoc;

            using (var stream = workbookEntry.Open())
                workbookDoc = XDocument.Load(stream);

            using (var stream = relsEntry.Open())
                relsDoc = XDocument.Load(stream);

            var sheet = workbookDoc
                .Descendants(MainNs + "sheet")
                .FirstOrDefault(x => string.Equals(
                    (string?)x.Attribute("name"),
                    sheetName,
                    StringComparison.OrdinalIgnoreCase));

            if (sheet == null)
                throw new InvalidOperationException($"Sheet '{sheetName}' was not found.");

            var relId = (string?)sheet.Attribute(RelNs + "id");
            if (string.IsNullOrWhiteSpace(relId))
                throw new InvalidOperationException($"Relationship id for sheet '{sheetName}' was not found.");

            var rel = relsDoc
                .Descendants(PackageRelNs + "Relationship")
                .FirstOrDefault(x => string.Equals(
                    (string?)x.Attribute("Id"),
                    relId,
                    StringComparison.OrdinalIgnoreCase));

            var target = (string?)rel?.Attribute("Target");
            if (string.IsNullOrWhiteSpace(target))
                throw new InvalidOperationException($"Relationship target for sheet '{sheetName}' was not found.");

            target = target.Replace('\\', '/');

            if (target.StartsWith("/xl/", StringComparison.OrdinalIgnoreCase))
                return target.TrimStart('/');

            if (target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
                return target;

            return "xl/" + target.TrimStart('/');
        }

        private static Dictionary<string, string> ReadCells(
            XDocument sheetDoc,
            Dictionary<string, string> sharedStrings)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var cell in sheetDoc.Descendants(MainNs + "c"))
            {
                var reference = ((string?)cell.Attribute("r") ?? "").Trim();
                if (string.IsNullOrWhiteSpace(reference))
                    continue;

                var type = ((string?)cell.Attribute("t") ?? "").Trim();

                string value;

                if (type.Equals("inlineStr", StringComparison.OrdinalIgnoreCase))
                {
                    value = string.Concat(cell.Descendants(MainNs + "t").Select(x => x.Value));
                }
                else
                {
                    var raw = cell.Element(MainNs + "v")?.Value ?? "";

                    if (type.Equals("s", StringComparison.OrdinalIgnoreCase))
                    {
                        value = sharedStrings.TryGetValue(raw, out var s)
                            ? s
                            : "";
                    }
                    else
                    {
                        value = raw;
                    }
                }

                result[reference] = value;
            }

            return result;
        }

        private static string GetCell(Dictionary<string, string> cells, string address)
        {
            return cells.TryGetValue(address, out var value)
                ? value ?? ""
                : "";
        }

        private static int TryGetRowIndex(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                return 0;

            var digits = new string(address.Where(char.IsDigit).ToArray());

            return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var row)
                ? row
                : 0;
        }

        private static IEnumerable<string> SplitFileList(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                yield break;

            foreach (var part in text.Split(','))
            {
                var fileName = part.Trim().Trim('"', '\'');

                if (!string.IsNullOrWhiteSpace(fileName))
                    yield return fileName;
            }
        }
    }
}