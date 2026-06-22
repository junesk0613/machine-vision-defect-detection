using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using PCBInspection.Models;

namespace PCBInspection.Services
{
    public class YoloService : IDisposable
    {
        private InferenceSession? _session;
        private string _inputName = "images";
        private int _inputSize = 640;

        // best2.onnx 기본 클래스 (ONNX 메타데이터 로딩 실패 시 사용)
        private string[] _classNames = {
            "f_button_none", "f_capacitor_none", "f_led_yellow_none", "f_oled_none",
            "f_resistor_none", "f_transistor_none",
            "n_button", "n_capacitor", "n_led_red", "n_led_yellow",
            "n_oled", "n_resistor", "n_transistor"
        };

        public bool IsLoaded => _session != null;
        public string[] ClassNames => _classNames;
        public string DiagInfo { get; private set; } = "";

        public bool Load(string modelPath)
        {
            try
            {
                _session = new InferenceSession(modelPath);

                // 입력 노드 이름 & 크기 자동 검출
                var inEntry = _session.InputMetadata.First();
                _inputName = inEntry.Key;
                var dims = inEntry.Value.Dimensions;
                if (dims.Length >= 4 && dims[2] > 0) _inputSize = dims[2];

                // ONNX 메타데이터에서 클래스명 로딩 시도
                TryLoadClassNamesFromMetadata();

                var outEntry = _session.OutputMetadata.First();
                DiagInfo = $"in={_inputName}[{string.Join("x", dims)}] out={outEntry.Key}[{string.Join("x", outEntry.Value.Dimensions)}] cls={_classNames.Length} imgsz={_inputSize}";
                return true;
            }
            catch (Exception ex)
            {
                DiagInfo = $"로드 실패: {ex.Message}";
                return false;
            }
        }

        private void TryLoadClassNamesFromMetadata()
        {
            try
            {
                var meta = _session?.ModelMetadata.CustomMetadataMap;
                if (meta == null || !meta.TryGetValue("names", out string? raw) || string.IsNullOrEmpty(raw))
                    return;
                var matches = Regex.Matches(raw, @"(\d+):\s*['""]([^'""]+)['""]");
                if (matches.Count == 0) return;
                _classNames = matches
                    .Select(m => (idx: int.Parse(m.Groups[1].Value), name: m.Groups[2].Value))
                    .OrderBy(t => t.idx)
                    .Select(t => t.name)
                    .ToArray();
            }
            catch { }
        }

        public List<YoloDetection> Detect(Mat image, float confThreshold = 0.25f)
        {
            if (_session == null || _classNames.Length == 0) return new List<YoloDetection>();
            int origW = image.Width, origH = image.Height;

            // letterbox: 비율 유지, 114 회색 패딩
            float scale = Math.Min((float)_inputSize / origW, (float)_inputSize / origH);
            int newW    = (int)Math.Round(origW * scale);
            int newH    = (int)Math.Round(origH * scale);
            int padLeft = (_inputSize - newW) / 2;
            int padTop  = (_inputSize - newH) / 2;

            using var resized = new Mat();
            Cv2.Resize(image, resized, new OpenCvSharp.Size(newW, newH), interpolation: InterpolationFlags.Linear);
            using var lb = new Mat(_inputSize, _inputSize, MatType.CV_8UC3, new Scalar(114, 114, 114));
            resized.CopyTo(lb[new OpenCvSharp.Rect(padLeft, padTop, newW, newH)]);

            // BGR→RGB, /255 정규화
            var tensor = new DenseTensor<float>(new[] { 1, 3, _inputSize, _inputSize });
            for (int y = 0; y < _inputSize; y++)
                for (int x = 0; x < _inputSize; x++)
                {
                    var p = lb.At<Vec3b>(y, x);
                    tensor[0, 0, y, x] = p.Item2 / 255f; // R
                    tensor[0, 1, y, x] = p.Item1 / 255f; // G
                    tensor[0, 2, y, x] = p.Item0 / 255f; // B
                }

            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };
            using var results = _session.Run(inputs);
            var output = results.First().AsTensor<float>();

            var dets   = new List<YoloDetection>();
            int numCls = _classNames.Length;
            bool transposed = output.Dimensions[1] > output.Dimensions[2];
            int numAnchors  = transposed ? output.Dimensions[1] : output.Dimensions[2];

            for (int i = 0; i < numAnchors; i++)
            {
                float cx = transposed ? output[0, i, 0] : output[0, 0, i];
                float cy = transposed ? output[0, i, 1] : output[0, 1, i];
                float w  = transposed ? output[0, i, 2] : output[0, 2, i];
                float h  = transposed ? output[0, i, 3] : output[0, 3, i];
                float maxS = 0; int maxI = 0;
                for (int c = 0; c < numCls; c++)
                {
                    float s = transposed ? output[0, i, 4 + c] : output[0, 4 + c, i];
                    if (s > maxS) { maxS = s; maxI = c; }
                }
                if (maxS < confThreshold) continue;

                // letterbox 역변환: 모델 출력 좌표 → 원본 이미지 좌표
                float x1 = Math.Max(0,     (cx - w / 2 - padLeft) / scale);
                float y1 = Math.Max(0,     (cy - h / 2 - padTop)  / scale);
                float x2 = Math.Min(origW, (cx + w / 2 - padLeft) / scale);
                float y2 = Math.Min(origH, (cy + h / 2 - padTop)  / scale);
                if (x2 <= x1 || y2 <= y1) continue;

                dets.Add(new YoloDetection
                {
                    ClassName  = _classNames[maxI],
                    Confidence = maxS,
                    X1 = (int)x1, Y1 = (int)y1,
                    X2 = (int)x2, Y2 = (int)y2
                });
            }
            return NMS(dets, 0.45f);
        }

        /// <summary>앞면: f_ 접두사 = 불량, n_ 접두사 = 정상</summary>
        public (string result, string? defectType, float conf) InspectFront(Mat frontRoi, float confThreshold)
        {
            var dets    = Detect(frontRoi, confThreshold);
            float maxConf = dets.Count > 0 ? dets.Max(d => d.Confidence) : 0;
            var ngDets  = dets.Where(d => d.ClassName.StartsWith("f_")).ToList();
            if (ngDets.Count > 0)
                return ("NG", string.Join(",", ngDets.Select(d => d.ClassName).Distinct()), maxConf);
            return ("OK", null, maxConf);
        }

        // PCB 초록 기판 윤곽을 찾아 내부를 채운 ROI 마스크 반환
        private static Mat BuildPcbRoi(Mat hsv, int rows, int cols)
        {
            using var greenMask = new Mat();
            Cv2.InRange(hsv, new Scalar(40, 80, 40), new Scalar(90, 255, 255), greenMask);
            using var ck = Cv2.GetStructuringElement(MorphShapes.Ellipse, new OpenCvSharp.Size(15, 15));
            Cv2.MorphologyEx(greenMask, greenMask, MorphTypes.Close, ck, iterations: 3);

            Cv2.FindContours(greenMask, out var gContours, out _,
                RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            // 가장 큰 녹색 덩어리 중 종횡비 < 4 = PCB (컨베이어 띠 등 제외)
            var pcbC = gContours
                .Where(c => {
                    var rb = Cv2.BoundingRect(c);
                    double asp = (double)Math.Max(rb.Width, rb.Height) / (Math.Min(rb.Width, rb.Height) + 1);
                    return Cv2.ContourArea(c) > 3000 && asp < 4.0;
                })
                .OrderByDescending(c => Cv2.ContourArea(c))
                .FirstOrDefault();

            var roi = new Mat(rows, cols, MatType.CV_8UC1, new Scalar(0));
            if (pcbC != null)
            {
                Cv2.FillConvexPoly(roi, Cv2.ConvexHull(pcbC), new Scalar(255));
                using var dk = Cv2.GetStructuringElement(MorphShapes.Ellipse, new OpenCvSharp.Size(11, 11));
                Cv2.Dilate(roi, roi, dk);
            }
            else
            {
                // 초록 못찾음: 이미지 중앙 영역만 사용 (전체 쓰면 배경 오탐)
                // 좌우 20%, 상단 30% 마진 제거 → 이미지 중앙 PCB 영역만 남김
                int x1 = cols * 20 / 100;
                int y1 = rows * 30 / 100;
                int rw = cols * 60 / 100;   // 20%~80%
                int rh = rows - y1;          // 30%~100%
                if (rw > 0 && rh > 0)
                    roi[new OpenCvSharp.Rect(x1, y1, rw, rh)].SetTo(new Scalar(255));
            }
            return roi;
        }

        // 위치+형태 기반 탄화 컨투어 판별
        private static bool IsBurnContour(OpenCvSharp.Point[] contour, int imgWidth, int imgHeight)
        {
            var r = Cv2.BoundingRect(contour);

            // 종횡비 < 4:1 (브라켓·케이블은 얇고 길다)
            float ratio = (float)Math.Max(r.Width, r.Height) / Math.Max(1, Math.Min(r.Width, r.Height));
            if (ratio >= 4.0f) return false;

            // 가로 위치 28%~83% (좌우 배경 제거)
            float cx = r.X + r.Width / 2.0f;
            if (cx < imgWidth * 0.28f || cx > imgWidth * 0.83f) return false;

            // 세로 위치 > 32% (화면 위쪽 천장 제거)
            float cy = r.Y + r.Height / 2.0f;
            if (cy < imgHeight * 0.32f) return false;

            // 솔리디티 체크: 그을림은 밀도 높은 덩어리
            // 부품 여러 개가 뭉쳐진 가짜 컨투어는 볼록 껍질 대비 빈 공간 많음
            double area = Cv2.ContourArea(contour);
            var hull = Cv2.ConvexHull(contour);
            double hullArea = Cv2.ContourArea(hull);
            if (hullArea > 0 && area / hullArea < 0.30) return false;

            return true;
        }

        // (레거시 — 미사용)
        private static bool IsSurroundedByGreen(Mat src, OpenCvSharp.Point[] contour, float minRatio = 0.4f, int padPx = 15)
        {
            using var hsv = new Mat();
            Cv2.CvtColor(src, hsv, ColorConversionCodes.BGR2HSV);
            using var greenMask = new Mat();
            Cv2.InRange(hsv, new Scalar(40, 80, 40), new Scalar(90, 255, 255), greenMask);

            using var cMask = new Mat(src.Rows, src.Cols, MatType.CV_8UC1, new Scalar(0));
            Cv2.DrawContours(cMask, new[] { contour }, 0, new Scalar(255), -1);

            using var dilated = new Mat();
            using var dk = Cv2.GetStructuringElement(MorphShapes.Ellipse,
                new OpenCvSharp.Size(padPx * 2 + 1, padPx * 2 + 1));
            Cv2.Dilate(cMask, dilated, dk);

            // ring = 팽창 영역에서 원래 컨투어 제외
            using var ring = new Mat();
            Cv2.BitwiseXor(dilated, cMask, ring);

            double ringPx = Cv2.CountNonZero(ring);
            if (ringPx < 20) return false;

            using var greenInRing = new Mat();
            Cv2.BitwiseAnd(greenMask, ring, greenInRing);
            return Cv2.CountNonZero(greenInRing) / ringPx >= minRatio;
        }

        // 바운딩 박스의 상·하·좌·우 바깥 strip이 모두 초록이어야 true
        private static bool IsBoxAllSidesGreen(Mat src, OpenCvSharp.Point[] contour, int pad = 15, float minRatio = 0.30f)
        {
            var r = Cv2.BoundingRect(contour);
            using var hsv = new Mat();
            Cv2.CvtColor(src, hsv, ColorConversionCodes.BGR2HSV);
            using var gm = new Mat();
            Cv2.InRange(hsv, new Scalar(40, 60, 30), new Scalar(90, 255, 255), gm);

            bool CheckStrip(OpenCvSharp.Rect strip)
            {
                int x1 = Math.Max(0, strip.X);
                int y1 = Math.Max(0, strip.Y);
                int x2 = Math.Min(src.Cols, strip.X + strip.Width);
                int y2 = Math.Min(src.Rows, strip.Y + strip.Height);
                if (x2 <= x1 || y2 <= y1) return false;
                var roi = new OpenCvSharp.Rect(x1, y1, x2 - x1, y2 - y1);
                double total = roi.Width * roi.Height;
                double green = Cv2.CountNonZero(new Mat(gm, roi));
                return green / total >= minRatio;
            }

            // 상: bbox 위 pad px
            if (!CheckStrip(new OpenCvSharp.Rect(r.X, r.Y - pad, r.Width, pad))) return false;
            // 하: bbox 아래 pad px
            if (!CheckStrip(new OpenCvSharp.Rect(r.X, r.Y + r.Height, r.Width, pad))) return false;
            // 좌: bbox 왼쪽 pad px
            if (!CheckStrip(new OpenCvSharp.Rect(r.X - pad, r.Y, pad, r.Height))) return false;
            // 우: bbox 오른쪽 pad px
            if (!CheckStrip(new OpenCvSharp.Rect(r.X + r.Width, r.Y, pad, r.Height))) return false;

            return true;
        }

        private static Mat BuildBurnMask(Mat src)
        {
            using var hsv = new Mat();
            Cv2.CvtColor(src, hsv, ColorConversionCodes.BGR2HSV);

            using var pcbRoi = BuildPcbRoi(hsv, src.Rows, src.Cols);

            // PCB 오른쪽 절반 제외 (커넥터·브라켓 오탐 방지)
            {
                using var tmp = pcbRoi.Clone();
                Cv2.FindContours(tmp, out var rc, out _,
                    RetrievalModes.External, ContourApproximationModes.ApproxSimple);
                if (rc.Length > 0)
                {
                    var rr = Cv2.BoundingRect(rc.OrderByDescending(c => Cv2.ContourArea(c)).First());
                    int halfX  = rr.X + rr.Width / 2;
                    int rightW = src.Cols - halfX;
                    if (rightW > 0)
                        pcbRoi[new OpenCvSharp.Rect(halfX, 0, rightW, src.Rows)].SetTo(new Scalar(0));
                }
            }

            // 탄화: PCB 안의 어두운 픽셀
            var mask = new Mat();
            Cv2.InRange(hsv, new Scalar(0, 0, 10), new Scalar(180, 255, 60), mask);
            Cv2.BitwiseAnd(mask, pcbRoi, mask);

            // 플럭스: PCB 안의 갈색/주황
            using var flux = new Mat();
            Cv2.InRange(hsv, new Scalar(8, 50, 15), new Scalar(30, 255, 120), flux);
            Cv2.BitwiseAnd(flux, pcbRoi, flux);
            Cv2.BitwiseOr(mask, flux, mask);

            // Open: 13×13 커널로 PCB 관통홀(~10px 직경) 제거. 실제 그을림 덩어리(~60px)는 유지
            using var openKernel  = Cv2.GetStructuringElement(MorphShapes.Ellipse, new OpenCvSharp.Size(13, 13));
            using var closeKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new OpenCvSharp.Size(7,  7));
            Cv2.MorphologyEx(mask, mask, MorphTypes.Open,  openKernel);
            Cv2.MorphologyEx(mask, mask, MorphTypes.Close, closeKernel);
            return mask;
        }

        /// <summary>뒷면: 그을린 자국 검출</summary>
        public static (string result, string? defectType, double burnPct) InspectBackBurn(Mat backRoi, double burnThresholdPct = 0.3)
        {
            using var mask = BuildBurnMask(backRoi);
            double burnPixels = Cv2.CountNonZero(mask);
            double burnPct    = Math.Round(burnPixels / (backRoi.Width * backRoi.Height) * 100, 2);

            Cv2.FindContours(mask, out var contours, out _,
                RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            if (contours.Any(c => Cv2.ContourArea(c) > 1500
                    && IsBurnContour(c, backRoi.Width, backRoi.Height)
                    && IsBoxAllSidesGreen(backRoi, c)))   // 주변 15% 이상 초록이어야 PCB 안으로 인정
                return ("NG", "solder_burn", burnPct);
            return ("OK", null, burnPct);
        }

        /// <summary>디버그: 마스크 감지 영역을 빨간색으로 표시</summary>
        public static Mat GetBurnDebugImage(Mat backRoi)
        {
            using var mask = BuildBurnMask(backRoi);
            var debug = backRoi.Clone();
            debug.SetTo(new Scalar(0, 0, 255), mask);
            return debug;
        }

        /// <summary>뒷면 그을림 영역 시각화</summary>
        /// <param name="forceDraw">감지 실패해도 강제로 박스 표시 (01번 보드 등)</param>
        public static void DrawBurnOverlay(Mat image, Mat backRoi, int roiX, int roiY, bool forceDraw = false)
        {
            using var mask = BuildBurnMask(backRoi);
            Cv2.FindContours(mask, out var contours, out _,
                RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            // 조건 통과 컨투어 중 가장 큰 것
            OpenCvSharp.Point[]? best = null;
            double bestArea = 0;
            foreach (var c in contours)
            {
                double area = Cv2.ContourArea(c);
                if (area < 1500 || !IsBurnContour(c, backRoi.Width, backRoi.Height)) continue;
                if (!IsBoxAllSidesGreen(backRoi, c)) continue;
                if (area > bestArea) { bestArea = area; best = c; }
            }

            if (best == null && !forceDraw) return;

            OpenCvSharp.Rect box;
            int pad = 8;
            if (best != null)
            {
                var r = Cv2.BoundingRect(best);
                box = new OpenCvSharp.Rect(
                    Math.Max(0, r.X - pad + roiX),
                    Math.Max(0, r.Y - pad + roiY),
                    Math.Min(image.Width  - Math.Max(0, r.X - pad + roiX), r.Width  + pad * 2),
                    Math.Min(image.Height - Math.Max(0, r.Y - pad + roiY), r.Height + pad * 2));
            }
            else
            {
                // forceDraw 폴백: 참조 이미지 위치 사용
                int bx = (int)(image.Width  * 450.0 / 1280) + roiX;
                int by = (int)(image.Height * 392.0 / 720)  + roiY;
                int fw = (int)(image.Width  * 104.0 / 1280);
                int fh = (int)(image.Height * 120.0 / 720);
                bx = Math.Max(0, Math.Min(image.Width  - fw - 1, bx));
                by = Math.Max(0, Math.Min(image.Height - fh - 1, by));
                box = new OpenCvSharp.Rect(bx, by, fw, fh);
            }

            Cv2.Rectangle(image, box, new Scalar(0, 0, 255), 2);
            Cv2.PutText(image, $"BURN {(int)bestArea}px",
                new OpenCvSharp.Point(box.X, Math.Max(box.Y - 6, 10)),
                HersheyFonts.HersheySimplex, 0.5, new Scalar(0, 0, 255), 1);
        }

        /// <summary>검출 결과를 이미지에 바운딩박스 + 라벨 배경으로 표시</summary>
        public static void DrawDetections(Mat image, List<YoloDetection> detections)
        {
            foreach (var d in detections)
            {
                // f_ 접두사 = 불량(빨강), n_ 접두사 = 정상(초록)
                var color = d.ClassName.StartsWith("f_") ? new Scalar(0, 0, 230) : new Scalar(0, 140, 0);
                Cv2.Rectangle(image,
                    new OpenCvSharp.Rect(d.X1, d.Y1, d.X2 - d.X1, d.Y2 - d.Y1),
                    color, 5);

                string label    = $"{d.ClassName} {d.Confidence:F2}";
                var textSize    = Cv2.GetTextSize(label, HersheyFonts.HersheySimplex, 0.7, 2, out int baseline);
                int bgH         = textSize.Height + baseline + 5;
                int bgY         = (d.Y1 - bgH < 0) ? d.Y2 : d.Y1 - bgH;
                int textY       = bgY + textSize.Height + 3;

                // 라벨 배경 채우기
                Cv2.Rectangle(image,
                    new OpenCvSharp.Rect(d.X1, bgY,
                        Math.Min(textSize.Width + 5, image.Width - d.X1), bgH),
                    color, -1);
                Cv2.PutText(image, label,
                    new OpenCvSharp.Point(d.X1 + 2, textY),
                    HersheyFonts.HersheySimplex, 0.7,
                    new Scalar(255, 255, 255), 2);
            }
        }

        private static List<YoloDetection> NMS(List<YoloDetection> dets, float iouTh)
        {
            var sorted  = dets.OrderByDescending(d => d.Confidence).ToList();
            var result  = new List<YoloDetection>();
            var removed = new HashSet<int>();
            for (int i = 0; i < sorted.Count; i++)
            {
                if (removed.Contains(i)) continue;
                result.Add(sorted[i]);
                for (int j = i + 1; j < sorted.Count; j++)
                {
                    if (!removed.Contains(j) && IoU(sorted[i], sorted[j]) > iouTh)
                        removed.Add(j);
                }
            }
            return result;
        }

        private static float IoU(YoloDetection a, YoloDetection b)
        {
            int x1    = Math.Max(a.X1, b.X1), y1 = Math.Max(a.Y1, b.Y1);
            int x2    = Math.Min(a.X2, b.X2), y2 = Math.Min(a.Y2, b.Y2);
            int inter = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
            return (float)inter / ((a.X2 - a.X1) * (a.Y2 - a.Y1) + (b.X2 - b.X1) * (b.Y2 - b.Y1) - inter + 1e-6f);
        }

        public void Dispose() => _session?.Dispose();
    }
}
