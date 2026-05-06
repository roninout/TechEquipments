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

        public static InfoDocumentExcelImportPlan ReadSchemePlan(string excelPath)
        {
            return ReadPlan(excelPath, "SCHEME");
        }

        private static InfoDocumentExcelImportPlan ReadPlan(string excelPath, string sheetName)
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
                SheetName = sheetName
            };

            ParseStationTable(cells, plan);
            ParseGroupTable(cells, plan);
            ParseEquipmentTable(cells, plan);

            return plan;
        }

        private static void ParseStationTable(Dictionary<string, string> cells, InfoDocumentExcelImportPlan plan)
        {
            var baseFolder = GetCell(cells, "A1").Trim();

            EnsureHeader(cells, "A3", "Station");
            EnsureHeader(cells, "B3", "Source");

            var maxRow = GetMaxRow(cells);

            for (var row = 4; row <= maxRow; row++)
            {
                var station = GetCell(cells, $"A{row}").Trim();
                var sourceText = GetCell(cells, $"B{row}").Trim();

                if (string.IsNullOrWhiteSpace(station) &&
                    string.IsNullOrWhiteSpace(sourceText))
                    continue;

                var item = new StationSourceRow
                {
                    RowNumber = row,
                    BaseFolder = baseFolder,
                    Station = station
                };

                foreach (var source in SplitCsv(sourceText))
                    item.Sources.Add(source);

                plan.StationRows.Add(item);
            }
        }

        private static void ParseGroupTable(Dictionary<string, string> cells, InfoDocumentExcelImportPlan plan)
        {
            var baseFolder = GetCell(cells, "D1").Trim();

            EnsureHeader(cells, "D3", "Group");
            EnsureHeader(cells, "E3", "Source");

            var maxRow = GetMaxRow(cells);

            for (var row = 4; row <= maxRow; row++)
            {
                var groupText = GetCell(cells, $"D{row}").Trim();
                var sourceText = GetCell(cells, $"E{row}").Trim();

                if (string.IsNullOrWhiteSpace(groupText) &&
                    string.IsNullOrWhiteSpace(sourceText))
                    continue;

                var item = new GroupSourceRow
                {
                    RowNumber = row,
                    BaseFolder = baseFolder
                };

                foreach (var group in SplitCsv(groupText))
                    item.Groups.Add(group);

                foreach (var source in SplitCsv(sourceText))
                    item.Sources.Add(source);

                plan.GroupRows.Add(item);
            }
        }

        private static void ParseEquipmentTable(Dictionary<string, string> cells, InfoDocumentExcelImportPlan plan)
        {
            var baseFolder = GetCell(cells, "G1").Trim();

            EnsureHeader(cells, "G3", "Equipment");
            EnsureHeader(cells, "H3", "Source");

            var maxRow = GetMaxRow(cells);

            for (var row = 4; row <= maxRow; row++)
            {
                var equipmentText = GetCell(cells, $"G{row}").Trim();
                var sourceText = GetCell(cells, $"H{row}").Trim();

                if (string.IsNullOrWhiteSpace(equipmentText) &&
                    string.IsNullOrWhiteSpace(sourceText))
                    continue;

                var item = new EquipmentSourceRow
                {
                    RowNumber = row,
                    BaseFolder = baseFolder
                };

                foreach (var equipment in SplitCsv(equipmentText))
                    item.Equipments.Add(equipment);

                foreach (var source in SplitCsv(sourceText))
                    item.Sources.Add(source);

                plan.EquipmentRows.Add(item);
            }
        }

        private static void EnsureHeader(Dictionary<string, string> cells, string address, string expected)
        {
            var actual = GetCell(cells, address).Trim();

            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Invalid Excel header at {address}. Expected '{expected}', actual '{actual}'.");
            }
        }

        private static int GetMaxRow(Dictionary<string, string> cells)
        {
            return cells.Keys
                .Select(TryGetRowIndex)
                .DefaultIfEmpty(0)
                .Max();
        }

        private static IEnumerable<string> SplitCsv(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                yield break;

            foreach (var part in text.Split(','))
            {
                var value = part.Trim().Trim('"', '\'');

                if (!string.IsNullOrWhiteSpace(value))
                    yield return value;
            }
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

        private static Dictionary<string, string> ReadCells(XDocument sheetDoc, Dictionary<string, string> sharedStrings)
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
    }
}