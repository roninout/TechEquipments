namespace TechEquipments
{
    /// <summary>
    /// DryRun-настройки мотора.
    /// Важно: имена свойств = имена EquipItem в SCADA (DryRunAEn, DryRunA, ...).
    /// Типы можно держать double? (как обычно в CtApi TagRead), а в UI конвертировать в bool.
    /// </summary>
    public sealed class DryRunMotor
    {
        // DIGITAL (0/1)
        public double? DryRunAEn { get; set; }   // Enable DryRun
        public double? DryRunA { get; set; }     // Alarm/Active

        // INT (но в TagRead обычно приходит как double)
        public double? DryRunLimToOff { get; set; }
        public double? DryRunTimeToOn { get; set; }
        public double? DryRunTimeToOff { get; set; }
    }
}