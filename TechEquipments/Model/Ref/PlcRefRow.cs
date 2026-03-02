using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TechEquipments
{
    /// <summary>
    /// Одна строка PLC refs (для PLC области в Param):
    /// - EquipName: значение из REFEQUIP (используем в TagInfo("{EquipName}.Value",0))
    /// - Type: тип из CUSTOM1 (EqNumW/EqCheckRW/...)
    /// - Title: текст слева ("Equip: Comment")
    /// - TagName: кэш результата TagInfo(...,0)
    /// - Value: текущее значение тега (double?)
    /// </summary>
    public sealed class PlcRefRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public string EquipName { get; }

        public PlcTypeCustom Type { get; private set; }

        public string Comment { get; private set; }

        private string _tagName = "";
        public string TagName
        {
            get => _tagName;
            set
            {
                value = (value ?? "").Trim();
                if (_tagName == value) return;
                _tagName = value;
                OnPropertyChanged();
            }
        }

        private string _unit = "";
        public string Unit
        {
            get => _unit;
            set
            {
                value = (value ?? "").Trim();
                if (_unit == value) return;
                _unit = value;
                OnPropertyChanged();
            }
        }

        private double? _value;
        public double? Value
        {
            get => _value;
            private set
            {
                if (_value == value) return;
                _value = value;
                OnPropertyChanged();
            }
        }

        /// <summary>Какие типы разрешаем редактировать (можно расширять).</summary>
        public bool IsWritable =>
            Type is PlcTypeCustom.EqCheckRW
                or PlcTypeCustom.EqNumW
                or PlcTypeCustom.EqEnum;

        /// <summary>Текст слева.</summary>
        public string Title => string.IsNullOrWhiteSpace(Comment) ? EquipName : $"{EquipName}:    {Comment}";

        public PlcRefRow(string equipName, PlcTypeCustom type, string comment)
        {
            EquipName = (equipName ?? "").Trim();
            Type = type;
            Comment = (comment ?? "").Trim();
        }

        public void UpdateMeta(PlcTypeCustom type, string comment)
        {
            Type = type;
            Comment = (comment ?? "").Trim();

            OnPropertyChanged(nameof(Type));
            OnPropertyChanged(nameof(Comment));
            OnPropertyChanged(nameof(IsWritable));
            OnPropertyChanged(nameof(Title));
        }

        public void UpdateValue(double? v) => Value = v;

        public static PlcTypeCustom ParseCustom(string? sCustom)
        {
            var s = (sCustom ?? "").Trim();
            if (s.Length == 0) return PlcTypeCustom.Unknown;

            return Enum.TryParse(s, ignoreCase: true, out PlcTypeCustom e) ? e : PlcTypeCustom.Unknown;
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null)  => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}