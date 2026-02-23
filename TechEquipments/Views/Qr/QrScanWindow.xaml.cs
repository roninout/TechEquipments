using OpenCvSharp;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXing;
using ZXing.Common;

namespace TechEquipments.Views.Qr
{
    public partial class QrScanWindow : System.Windows.Window
    {
        private CancellationTokenSource? _cts;
        private Task? _loopTask;

        private VideoCapture? _cap;
        private WriteableBitmap? _wb;

        private readonly BarcodeReaderGeneric _reader;

        /// <summary>
        /// Результат (текст из QR), если DialogResult == true.
        /// </summary>
        public string? ScannedText { get; private set; }

        public QrScanWindow()
        {
            InitializeComponent();

            // Настраиваем ZXing строго под QR
            _reader = new BarcodeReaderGeneric
            {
                AutoRotate = true,
                Options = new DecodingOptions
                {
                    TryHarder = true,
                    PossibleFormats = new System.Collections.Generic.List<BarcodeFormat> { BarcodeFormat.QR_CODE }
                }
            };

            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        /// <summary>
        /// Старт камеры и цикла захвата кадров.
        /// </summary>
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _cts = new CancellationTokenSource();
            _loopTask = Task.Run(() => CaptureLoopAsync(_cts.Token));
        }

        /// <summary>
        /// Остановка ресурсов при закрытии окна.
        /// </summary>
        private void OnClosed(object? sender, EventArgs e)
        {
            try { _cts?.Cancel(); } catch { }
            _cts?.Dispose();
            _cts = null;

            try { _cap?.Release(); } catch { }
            _cap?.Dispose();
            _cap = null;
        }

        /// <summary>
        /// Нажатие Cancel — просто закрываем окно.
        /// </summary>
        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        /// <summary>
        /// Главный цикл: читаем кадры с камеры, показываем превью, пытаемся декодировать QR.
        /// </summary>
        private async Task CaptureLoopAsync(CancellationToken ct)
        {
            try
            {
                _cap = new VideoCapture(0);
                if (!_cap.IsOpened())
                {
                    await Dispatcher.InvokeAsync(() => StatusText.Text = "Camera: not found / can't open");
                    return;
                }

                await Dispatcher.InvokeAsync(() => StatusText.Text = "Camera: scanning...");

                using var frame = new Mat();
                using var rgb = new Mat();

                var sw = Stopwatch.StartNew();
                long lastDecodeMs = 0;

                while (!ct.IsCancellationRequested)
                {
                    _cap.Read(frame);
                    if (frame.Empty())
                    {
                        await Task.Delay(30, ct);
                        continue;
                    }

                    // 1) обновляем превью
                    await Dispatcher.InvokeAsync(() => UpdatePreview(frame));

                    // 2) декодируем не на каждом кадре (чтобы не грузить CPU)
                    var now = sw.ElapsedMilliseconds;
                    if (now - lastDecodeMs < 120)
                    {
                        await Task.Delay(10, ct);
                        continue;
                    }
                    lastDecodeMs = now;

                    string? decoded = TryDecodeQr(frame, rgb);
                    if (!string.IsNullOrWhiteSpace(decoded))
                    {
                        decoded = decoded.Trim();

                        await Dispatcher.InvokeAsync(() =>
                        {
                            ScannedText = decoded;
                            StatusText.Text = "QR: OK";
                            DialogResult = true;
                            Close();
                        });

                        return;
                    }

                    await Task.Delay(10, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() => StatusText.Text = "Camera error: " + ex.Message);
            }
        }

        /// <summary>
        /// Пытаемся декодировать QR из Mat(BGR). Конвертим в RGB24, отдаём ZXing.
        /// </summary>
        private string? TryDecodeQr(Mat bgrFrame, Mat rgb)
        {
            // BGR -> RGB
            Cv2.CvtColor(bgrFrame, rgb, ColorConversionCodes.BGR2RGB);

            int w = rgb.Width;
            int h = rgb.Height;

            // RGB24 bytes
            int bytesLen = w * h * 3;
            byte[] bytes = new byte[bytesLen];
            Marshal.Copy(rgb.Data, bytes, 0, bytesLen);

            var source = new RGBLuminanceSource(bytes, w, h, RGBLuminanceSource.BitmapFormat.RGB24);
            var result = _reader.Decode(source);

            return result?.Text;
        }

        /// <summary>
        /// Показывает кадр в Image через WriteableBitmap (Bgr24).
        /// </summary>
        private void UpdatePreview(Mat bgrFrame)
        {
            int w = bgrFrame.Width;
            int h = bgrFrame.Height;

            // Важно: BGR24
            if (_wb == null || _wb.PixelWidth != w || _wb.PixelHeight != h)
            {
                _wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr24, null);
                PreviewImage.Source = _wb;
            }

            // Mat шаг в байтах
            int stride = (int)bgrFrame.Step();
            int bufferSize = stride * h;

            _wb.WritePixels(new Int32Rect(0, 0, w, h), bgrFrame.Data, bufferSize, stride);
        }
    }
}