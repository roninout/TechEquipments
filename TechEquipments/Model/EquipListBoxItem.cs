using System;

namespace TechEquipments
{
    public sealed class EquipListBoxItem
    {
        /// <summary>
        /// Service field для TreeListControl.
        /// Строковый ключ, чтобы совпадал с XAML RootValue="0".
        /// </summary>
        public string NodeId { get; set; } = "";

        /// <summary>
        /// 0 = root node.
        /// </summary>
        public string ParentNodeId { get; set; } = "0";

        public bool IsGroup { get; set; }

        public string Equipment { get; init; } = "";
        public string Tag { get; init; } = "";
        public string Type { get; set; } = "";
        public string Station { get; set; } = "";

        public EquipTypeGroup TypeGroup { get; set; } = EquipTypeGroup.All;

        private string _description = "";
        public string Description
        {
            get => _description;
            set => _description = CleanDescription(value);
        }

        public string DisplayText => IsGroup ? (string.IsNullOrWhiteSpace(Description) ? Equipment : Description) : Equipment;

        public override string ToString() => DisplayText;

        private static string CleanDescription(string? s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return "";

            s = s.Trim();

            if (s.StartsWith("@(") && s.EndsWith(")") && s.Length >= 3)
            {
                s = s.Substring(2, s.Length - 3).Trim();

                if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
                    s = s.Substring(1, s.Length - 2).Trim();
            }

            return s;
        }
    }
}