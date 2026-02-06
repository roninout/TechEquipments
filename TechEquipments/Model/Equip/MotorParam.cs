using System;

namespace TechEquipments
{
    public class MotorParam
    {
        public bool Mode { get; set; }
        public bool Auto { get; set; }
        public bool Man { get; set; }
        public bool AlarmAEn { get; set; }
        public bool AlarmA { get; set; }
        public bool TimeWorkAlarmW { get; set; }
        public bool TimeWorkAlarmWAck { get; set; }
        public bool TimeReset { get; set; }
        public bool On { get; set; }
        public bool NotTrip { get; set; }

        public int STW { get; set; }
        public int State { get; set; }
        public double TimeWarn { get; set; }
        public double TimeSet { get; set; }
        public double TimeHmi { get; set; }

        public long TimeWork { get; set; }

        public uint HashCode { get; set; }

    }
}
