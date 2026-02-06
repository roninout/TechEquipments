using System;

namespace TechEquipments
{
    public class VGAParam
    {
        public bool Mode { get; set; }
        public bool AlarmLAEn { get; set; }
        public bool AlarmLWEn { get; set; }
        public bool AlarmHWEn { get; set; }
        public bool AlarmHAEn { get; set; }
        public bool ForceCmd { get; set; }
        public bool AlarmLA { get; set; }
        public bool AlarmLW { get; set; }
        public bool AlarmHW { get; set; }
        public bool AlarmHA { get; set; }
        public bool AlarmA { get; set; }
        public bool AlarmW { get; set; }
        public bool AlarmHealth { get; set; }

        public int STW { get; set; }

        public double Min { get; set; }
        public double Max { get; set; }
        public double MinR { get; set; }
        public double MaxR { get; set; }
        public double OutMin { get; set; }
        public double OutMax { get; set; }
        public double Value { get; set; }
        public double Man { get; set; }
        public double ManTrue { get; set; }
        public double ManForced { get; set; }
        public double SetLA { get; set; }
        public double SetLW { get; set; }
        public double SetHW { get; set; }
        public double SetHA { get; set; }
        public double SetHyst { get; set; }
        public double R { get; set; }

        public uint HashCode { get; set; }

    }
}
