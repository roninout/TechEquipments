using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TechEquipments
{
    public sealed class EquipListBoxItem
    {
        public string Equipment { get; init; } = "";
        public string Tag { get; init; } = "";
        public string Type { get; set; } = "";
        public string Station { get; set; } = "";
        public override string ToString() => Equipment;
    }
}
