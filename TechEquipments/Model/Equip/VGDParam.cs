using System;

namespace TechEquipments
{
    public class VGDParam
    {
        public bool Mode { get; set; }
        public bool Auto { get; set; }
        public bool Man { get; set; }
        public bool AlarmEn { get; set; }
        public bool AlarmA { get; set; }
        public bool AlarmOpen { get; set; }
        public bool AlarmClose { get; set; }
        public bool Opened { get; set; }
        public bool Closed { get; set; }
        public bool Dcs { get; set; }
        public bool NotTrip { get; set; }

        public int STW { get; set; }
        public int State { get; set; }
        public int TOpen { get; set; }
        public int TClose { get; set; }

        public uint HashCode { get; set; }

    }
}
