using System;

namespace TechEquipments
{
    public class AtvParam
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
        public bool Run { get; set; }
        public bool AlarmEn { get; set; }
        public bool Alarm { get; set; }
        public bool Start { get; set; }
        public bool StopType { get; set; }

        public int STW01 { get; set; }
        public int STW02 { get; set; }

        public double OutMin { get; set; }
        public double OutMax { get; set; }
        public double NMax { get; set; }
        public double NHmi { get; set; }
        public double IHmi { get; set; }
        public double RpmHmi { get; set; }
        public double FHmi { get; set; }
        public double THmi { get; set; }
        public double IL1R { get; set; }
        public double Nsp { get; set; }
        public double Cli { get; set; }
        public double RemoteSet { get; set; }
        public double RemoteAcc { get; set; }
        public double RemoteDec { get; set; }
        public double LocalSet { get; set; }
        public double LocalAcc { get; set; }
        public double LocalDec { get; set; }
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
