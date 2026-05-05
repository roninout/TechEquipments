using System.Collections.Generic;

namespace TechEquipments
{
    public sealed class InfoDocumentExcelImportPlan
    {
        public string SheetName { get; set; } = "";
        public string BaseFolder { get; set; } = "";

        public List<InfoDocumentExcelImportRow> Rows { get; } = new();
    }

    public sealed class InfoDocumentExcelImportRow
    {
        public int RowNumber { get; set; }
        public string Station { get; set; } = "";
        public List<string> FileNames { get; } = new();
    }
}