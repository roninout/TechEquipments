using System.Collections.Generic;

namespace TechEquipments
{
    public sealed class InfoDocumentExcelImportPlan
    {
        public string SheetName { get; set; } = "";

        public List<StationSourceRow> StationRows { get; } = new();
        public List<GroupSourceRow> GroupRows { get; } = new();
        public List<EquipmentSourceRow> EquipmentRows { get; } = new();
    }

    public sealed class StationSourceRow
    {
        public int RowNumber { get; set; }
        public string BaseFolder { get; set; } = "";
        public string Station { get; set; } = "";
        public List<string> Sources { get; } = new();
    }

    public sealed class GroupSourceRow
    {
        public int RowNumber { get; set; }
        public string BaseFolder { get; set; } = "";
        public List<string> Groups { get; } = new();
        public List<string> Sources { get; } = new();
    }

    public sealed class EquipmentSourceRow
    {
        public int RowNumber { get; set; }
        public string BaseFolder { get; set; } = "";
        public List<string> Equipments { get; } = new();
        public List<string> Sources { get; } = new();
    }
}