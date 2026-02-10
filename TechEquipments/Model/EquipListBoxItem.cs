using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TechEquipments
{
    public sealed class EquipListBoxItem
    {
        public string Equipment { get; init; } = "";
        public string Tag { get; init; } = "";
        public string Type { get; set; } = "";
        public string Station { get; set; } = "";

        private string _description = "";
        public string Description
        {
            get => _description;
            set => _description = CleanDescription(value);
        }
        public override string ToString() => Equipment;

        private static string CleanDescription(string? s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return "";

            s = s.Trim();

            if (s.StartsWith("@(") && s.EndsWith(")") && s.Length >= 3)
            {
                s = s.Substring(2, s.Length - 3).Trim();

                // снять кавычки, если есть
                if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
                    s = s.Substring(1, s.Length - 2).Trim();
            }

            return s;
        }

    }
}
