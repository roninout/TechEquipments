using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TechEquipments
{
    public class AIParam
    {
        public bool AlarmLAEn { get; set; }
        public bool AlarmLWEn { get; set; }
        public bool AlarmHWEn { get; set; }
        public bool AlarmHAEn { get; set; }
        public bool ForceCmd { get; set; }
        public bool NotTripLow { get; set; }
        public bool NotTripHigh { get; set; }
        public bool RealVar { get; set; }
        public bool AlarmLA { get; set; }
        public bool AlarmLW { get; set; }
        public bool AlarmHW { get; set; }
        public bool AlarmHA { get; set; }
        public bool AlarmA { get; set; }
        public bool Shunt { get; set; }
        public bool AlarmW { get; set; }
        public bool AlarmHealth { get; set; }

        public int STW { get; set; }
        public int Min { get; set; }
        public int Max { get; set; }
        public int MinR { get; set; }
        public int MaxR { get; set; }
        public int Flt { get; set; }
        public int Coef { get; set; }
        public int Value { get; set; }
        public int Hmi { get; set; }
        public int HmiTrue { get; set; }
        public int HmiForced { get; set; }
        public int SetLA { get; set; }
        public int SetLW { get; set; }
        public int SetHW { get; set; }
        public int SetHA { get; set; }
        public int SetHyst { get; set; }

        public double R { get; set; }
        public uint HashCode { get; set; }

    }
}
