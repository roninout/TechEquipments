using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TechEquipments
{
    /// <summary>
    /// Вкладки главного TabControl.
    /// Важно: значения должны совпадать с порядком вкладок в XAML (SelectedIndex).
    /// </summary>
    public enum MainTabKind
    {
        Param = 0,
        OperationActions = 1,
        AlarmHistory = 2,
        SOE = 3
    }
}
