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
        public int TimeWarn { get; set; }
        public int TimeSet { get; set; }
        public int TimeHmi { get; set; }

        public long TimeWork { get; set; }

        public uint HashCode { get; set; }

    }
}
