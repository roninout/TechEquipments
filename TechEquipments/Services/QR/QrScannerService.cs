using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using TechEquipments.Views.Qr;

namespace TechEquipments.Services.QR
{
    public sealed class QrScannerService : IQrScannerService
    {
        /// <summary>
        /// Показывает модальное окно сканирования QR и возвращает результат.
        /// </summary>
        public Task<string?> ScanFromCameraAsync(Window owner, CancellationToken ct = default)
        {
            // ShowDialog блокирует UI-поток, но внутри окно само крутит захват в background Task.
            var w = new QrScanWindow
            {
                Owner = owner
            };

            bool? ok = w.ShowDialog();
            return Task.FromResult(ok == true ? w.ScannedText : null);
        }
    }
}
