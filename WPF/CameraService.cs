using System;
using System.IO;
using System.Threading;
using System.Windows.Media.Imaging;
using OpenCvSharp;

namespace PCBInspection.Services
{
    public class CameraService : IDisposable
    {
        private VideoCapture? _capFront;
        private VideoCapture? _capBack;
        private bool _running;
        private bool _frontAvailable;
        private bool _backAvailable;
        private readonly object _frameLock = new();

        private Mat? _latestFront;
        private Mat? _latestBack;

        public bool FrontOpen => _frontAvailable && (_capFront?.IsOpened() ?? false);
        public bool BackOpen  => _backAvailable  && (_capBack?.IsOpened()  ?? false);
        public bool IsOpen    => FrontOpen || BackOpen;

        public event Action<Mat?, Mat?>? OnFrameCaptured;

        public bool Open(int frontIdx = 0, int backIdx = 1)
        {
            _frontAvailable = false;
            _backAvailable  = false;

            try
            {
                _capFront = new VideoCapture(frontIdx, (VideoCaptureAPIs)700);
                if (_capFront.IsOpened())
                {
                    _capFront.Set(VideoCaptureProperties.FrameWidth,  1280);
                    _capFront.Set(VideoCaptureProperties.FrameHeight, 720);
                    _capFront.Set(VideoCaptureProperties.Fps, 30);
                    _frontAvailable = true;
                }
                else { _capFront.Dispose(); _capFront = null; }
            }
            catch { _capFront = null; }

            try
            {
                _capBack = new VideoCapture(backIdx, (VideoCaptureAPIs)700);
                if (_capBack.IsOpened())
                {
                    _capBack.Set(VideoCaptureProperties.FrameWidth,  1280);
                    _capBack.Set(VideoCaptureProperties.FrameHeight, 720);
                    _capBack.Set(VideoCaptureProperties.Fps, 30);
                    _backAvailable = true;
                }
                else { _capBack.Dispose(); _capBack = null; }
            }
            catch { _capBack = null; }

            return _frontAvailable || _backAvailable;
        }

        public void StartCapture()
        {
            _running = true;
            new Thread(CaptureLoop) { IsBackground = true }.Start();
        }

        public void StopCapture() => _running = false;

        private void CaptureLoop()
        {
            while (_running)
            {
                try
                {
                    Mat? eventFront = null, eventBack = null;
                    bool anyFrame = false;

                    if (_frontAvailable && _capFront != null)
                    {
                        var f = new Mat();
                        if (_capFront.Read(f) && !f.Empty())
                        {
                            lock (_frameLock) { _latestFront?.Dispose(); _latestFront = f; }
                            // 이벤트용 클론 — 소유권은 이벤트 수신자(MainWindow.OnFrame)에게 넘어감.
                            // CaptureLoop에서 절대 Dispose하지 않음:
                            // RunInspection이 async여서 여러 await 동안 Mat을 계속 참조하기 때문.
                            eventFront = f.Clone();
                            anyFrame = true;
                        }
                        else f.Dispose();
                    }

                    if (_backAvailable && _capBack != null)
                    {
                        var b = new Mat();
                        if (_capBack.Read(b) && !b.Empty())
                        {
                            lock (_frameLock) { _latestBack?.Dispose(); _latestBack = b; }
                            eventBack = b.Clone();
                            anyFrame = true;
                        }
                        else b.Dispose();
                    }

                    if (anyFrame)
                        OnFrameCaptured?.Invoke(eventFront, eventBack);
                    else
                        Thread.Sleep(10);
                }
                catch { Thread.Sleep(100); }
            }
        }

        public static bool IsPcbInFrame(Mat frame, double minFillPct = 0.80)
        {
            using var gray  = new Mat();
            using var blur  = new Mat();
            using var thresh = new Mat();
            Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
            Cv2.GaussianBlur(gray, blur, new OpenCvSharp.Size(5, 5), 0);
            Cv2.Threshold(blur, thresh, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
            Cv2.FindContours(thresh, out var contours, out _,
                RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            double frameArea = (double)frame.Width * frame.Height;
            double maxArea = 0;
            foreach (var c in contours)
            {
                double a = Cv2.ContourArea(c);
                if (a > maxArea) maxArea = a;
            }
            return frameArea > 0 && maxArea / frameArea >= minFillPct;
        }

        public static (bool found, Mat? roi) DetectPCB(Mat frame)
        {
            using var gray  = new Mat();
            using var blur  = new Mat();
            using var thresh = new Mat();
            Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
            Cv2.GaussianBlur(gray, blur, new OpenCvSharp.Size(5, 5), 0);
            Cv2.Threshold(blur, thresh, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
            Cv2.FindContours(thresh, out var contours, out _,
                RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            double maxArea = 0;
            OpenCvSharp.Rect bestRect = new();
            foreach (var c in contours)
            {
                double a = Cv2.ContourArea(c);
                if (a > maxArea && a > frame.Width * frame.Height * 0.05)
                { maxArea = a; bestRect = Cv2.BoundingRect(c); }
            }

            if (maxArea > 0)
            {
                int pad = 10;
                int x = Math.Max(0, bestRect.X - pad);
                int y = Math.Max(0, bestRect.Y - pad);
                int w = Math.Min(frame.Width  - x, bestRect.Width  + pad * 2);
                int h = Math.Min(frame.Height - y, bestRect.Height + pad * 2);
                return (true, new Mat(frame, new OpenCvSharp.Rect(x, y, w, h)).Clone());
            }
            return (false, null);
        }

        public static byte[] MatToJpeg(Mat mat)
        {
            Cv2.ImEncode(".jpg", mat, out var buf);
            return buf;
        }

        public static BitmapSource MatToBitmapSource(Mat mat)
        {
            using var rgb = new Mat();
            Cv2.CvtColor(mat, rgb, ColorConversionCodes.BGR2RGB);
            int stride = rgb.Width * 3;
            var data = new byte[stride * rgb.Height];
            System.Runtime.InteropServices.Marshal.Copy(rgb.Data, data, 0, data.Length);
            var bmp = BitmapSource.Create(
                rgb.Width, rgb.Height, 96, 96,
                System.Windows.Media.PixelFormats.Rgb24, null, data, stride);
            bmp.Freeze();
            return bmp;
        }

        public void Dispose()
        {
            _running = false;
            Thread.Sleep(200);
            _capFront?.Dispose();
            _capBack?.Dispose();
            lock (_frameLock) { _latestFront?.Dispose(); _latestBack?.Dispose(); }
        }
    }
}
