using System;

namespace TechEquipments
{
    public class VGA_ElParam
    {
        public bool Mode { get; set; }
        public bool OpenCmd { get; set; }
        public bool CloseCmd { get; set; }
        public bool AlarmEn { get; set; }
        public bool Alarm { get; set; }
        public bool SQEn { get; set; }
        public bool ActuatorEn { get; set; }
        public bool Opened { get; set; }
        public bool Closed { get; set; }
        public bool OpenAl { get; set; }
        public bool CloseAl { get; set; }

        public int State { get; set; }
        public int Man { get; set; }
        public int CurrPos { get; set; }
        public int TimeOpening { get; set; }
        public int OutMin { get; set; }
        public int OutMax { get; set; }
        
        public long STW { get; set; }

        public double R { get; set; }

        public uint HashCode { get; set; }

    }
}
