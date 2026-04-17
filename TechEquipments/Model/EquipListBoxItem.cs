using System;

namespace TechEquipments
{
    public sealed class EquipListBoxItem
    {
        public string Equipment { get; init; } = "";
        public string Tag { get; init; } = "";
        public string Type { get; set; } = "";
        public string Station { get; set; } = "";

        /// <summary>
        /// Реальная группа типа оборудования.
        /// Для обычных equipment/child nodes это Motor/VGD/...
        /// Для group nodes это Equipment.
        /// </summary>
        public EquipTypeGroup TypeGroup { get; set; } = EquipTypeGroup.All;

        /// <summary>
        /// Service field для TreeListControl.
        /// Строковый ключ, чтобы спокойно работать с RootValue="0".
        /// </summary>
        public string NodeId { get; set; } = "";

        /// <summary>
        /// 0 = root node.
        /// Для child nodes = NodeId родительской группы.
        /// </summary>
        public string ParentNodeId { get; set; } = "0";

        /// <summary>
        /// Родительская группа Equipment (_EQUIP).
        /// </summary>
        public bool IsGroup { get; set; }

        /// <summary>
        /// Дочерний equipment под Equipment-group.
        /// Это не "новое оборудование", а ссылка на уже существующее.
        /// </summary>
        public bool IsEquipmentChildNode { get; set; }

        /// <summary>
        /// Обычный equipment из плоского списка.
        /// </summary>
        public bool IsPlainEquipmentNode => !IsGroup && !IsEquipmentChildNode;

        private string _description = "";
        public string Description
        {
            get => _description;
            set => _description = CleanDescription(value);
        }

        /// <summary>
        /// Текст узла для дерева.
        /// Пока и для group node, и для обычного equipment показываем Equipment.
        /// Description остаётся только в tooltip / для будущего использования.
        /// </summary>
        public string DisplayText => Equipment;

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