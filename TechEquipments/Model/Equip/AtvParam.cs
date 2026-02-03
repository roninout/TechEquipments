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
        public int OutMin { get; set; }
        public int OutMax { get; set; }
        public int NMax { get; set; }
        public int NHmi { get; set; }
        public int IHmi { get; set; }
        public int RpmHmi { get; set; }
        public int FHmi { get; set; }
        public int THmi { get; set; }
        public int IL1R { get; set; }
        public int Nsp { get; set; }
        public int Cli { get; set; }
        public int RemoteSet { get; set; }
        public int RemoteAcc { get; set; }
        public int RemoteDec { get; set; }
        public int LocalSet { get; set; }
        public int LocalAcc { get; set; }
        public int LocalDec { get; set; }
        public int Man { get; set; }
        public int ManTrue { get; set; }
        public int ManForced { get; set; }
        public int SetLA { get; set; }
        public int SetLW { get; set; }
        public int SetHW { get; set; }
        public int SetHA { get; set; }
        public int SetHyst { get; set; }

        public double R { get; set; }

        public uint HashCode { get; set; }

    }
}
