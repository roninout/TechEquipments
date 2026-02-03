using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TechEquipments
{
    public class DIParam
    {
        public bool Value { get; set; }
        public bool ValueTrue { get; set; }
        public bool ValueForced { get; set; }
        public bool ForceCmd { get; set; }
        public bool AlarmHealth { get; set; }
        public bool NotTrip { get; set; }
        public bool Shunt { get; set; }

        public int STW { get; set; }

        public uint HashCode { get; set; }
    }
}
