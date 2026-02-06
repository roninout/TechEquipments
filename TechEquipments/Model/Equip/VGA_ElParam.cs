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

        public double Man { get; set; }
        public double CurrPos { get; set; }
        public double TimeOpening { get; set; }
        public double OutMin { get; set; }
        public double OutMax { get; set; }
        public double R { get; set; }

        public long STW { get; set; }

        public uint HashCode { get; set; }

    }
}
