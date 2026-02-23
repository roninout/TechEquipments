using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace TechEquipments
{
    public interface IQrScannerService
    {
        /// <summary>
        /// Открывает окно камеры, ждёт QR и возвращает текст (или null, если отмена/ошибка).
        /// </summary>
        Task<string?> ScanFromCameraAsync(Window owner, CancellationToken ct = default);
    }
}
