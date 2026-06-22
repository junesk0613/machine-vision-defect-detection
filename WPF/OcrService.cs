using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using OpenCvSharp;
using Tesseract;

namespace PCBInspection.Services
{
    public class OcrService : IDisposable
    {
        private TesseractEngine? _engine;
        public bool IsLoaded => _engine != null;

        public bool Load()
        {
            // tessdata 경로 탐색
            string[] searchPaths = {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tessdata"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "tessdata"),
                @"C:\tessdata",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tesseract-OCR", "tessdata")
            };

            foreach (var dir in searchPaths)
            {
                if (File.Exists(Path.Combine(dir, "eng.traineddata")))
                {
                    try
                    {
                        _engine = new TesseractEngine(dir, "eng", EngineMode.Default);
                        // 숫자/영문/특수문자만 허용 (한글 제외 → 속도 향상)
                        _engine.SetVariable("tessedit_char_whitelist", "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.");
                        return true;
                    }
                    catch { }
                }
            }
            return false;
        }

        /// <summary>이미지에서 흰 라벨 스티커를 자동 탐지 후 시리얼 번호 추출</summary>
        public string? ReadSerialNumber(Mat image)
        {
            if (_engine == null || image == null || image.Empty()) return null;

            try
            {
                string dbgDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ocr_debug");
                Directory.CreateDirectory(dbgDir);
                string ts = DateTime.Now.ToString("HHmmss");

                // 1차: 엄격한 흰색 (V≥130) — 밝은 프레임
                var candidates = DetectLabelRegions(image, dbgDir, ts, vMin: 130, sMax: 80);
                System.Diagnostics.Debug.WriteLine($"[OCR] 1차 후보 {candidates.Count}개");

                // 2차: 완화된 흰색 (V≥100) — 살짝 어두운 프레임 폴백
                if (candidates.Count == 0)
                {
                    candidates = DetectLabelRegions(image, dbgDir, ts + "_loose", vMin: 100, sMax: 100);
                    System.Diagnostics.Debug.WriteLine($"[OCR] 2차(loose) 후보 {candidates.Count}개");
                }

                foreach (var roi in candidates)
                {
                    var result = OcrMat(roi);
                    roi.Dispose();
                    if (result != null) return result;
                }
                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[OCR] Error: {ex.Message}");
                return null;
            }
        }

        /// <summary>흰 직사각형 스티커 후보 영역을 Mat 리스트로 반환</summary>
        private static List<Mat> DetectLabelRegions(Mat src, string? dbgDir = null, string? ts = null,
            int vMin = 130, int sMax = 80)
        {
            var results = new List<Mat>();
            using var hsv = new Mat();
            Cv2.CvtColor(src, hsv, ColorConversionCodes.BGR2HSV);

            using var whiteMask = new Mat();
            Cv2.InRange(hsv, new Scalar(0, 0, vMin), new Scalar(180, sMax, 255), whiteMask);

            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(5, 5));
            using var closed = new Mat();
            Cv2.MorphologyEx(whiteMask, closed, MorphTypes.Close, kernel);

            // 디버그: 마스크 저장
            if (dbgDir != null)
            {
                Cv2.ImWrite(Path.Combine(dbgDir, $"{ts}_mask.png"), closed);
                Cv2.ImWrite(Path.Combine(dbgDir, $"{ts}_src.jpg"), src);
            }

            Cv2.FindContours(closed, out var contours, out _,
                RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            int imgArea = src.Rows * src.Cols;
            System.Diagnostics.Debug.WriteLine($"[OCR] 전체 컨투어={contours.Length}, imgArea={imgArea}");

            int candidateIdx = 0;
            foreach (var c in contours)
            {
                double area = Cv2.ContourArea(c);
                if (area < imgArea * 0.001 || area > imgArea * 0.08)
                {
                    System.Diagnostics.Debug.WriteLine($"[OCR] 면적 탈락: {area:F0} (한계: {imgArea*0.001:F0}~{imgArea*0.08:F0})");
                    continue;
                }

                var rect = Cv2.MinAreaRect(c);
                float w = rect.Size.Width, h = rect.Size.Height;
                if (w < 1 || h < 1) continue;

                float ratio = Math.Max(w, h) / Math.Min(w, h);
                if (ratio < 2.0f || ratio > 8.0f)
                {
                    System.Diagnostics.Debug.WriteLine($"[OCR] 비율 탈락: {w:F0}x{h:F0} ratio={ratio:F1}");
                    continue;
                }

                // 이미지 가장자리에 걸친 영역 제외
                var center = rect.Center;
                if (center.X < src.Width * 0.10f || center.X > src.Width * 0.90f ||
                    center.Y < src.Height * 0.10f || center.Y > src.Height * 0.90f)
                {
                    System.Diagnostics.Debug.WriteLine($"[OCR] 가장자리 탈락: center=({center.X:F0},{center.Y:F0})");
                    continue;
                }

                System.Diagnostics.Debug.WriteLine($"[OCR] 후보 통과: area={area:F0}, {w:F0}x{h:F0}, ratio={ratio:F1}, center=({center.X:F0},{center.Y:F0})");

                // BoundingRect 크롭 → 세로면 90도 회전 → 3배 확대
                var br = Cv2.BoundingRect(c);
                int bx = Math.Max(0, br.X - 6);
                int by = Math.Max(0, br.Y - 6);
                int bw = Math.Min(src.Width - bx, br.Width + 12);
                int bh = Math.Min(src.Height - by, br.Height + 12);
                using var cropped = new Mat(src, new OpenCvSharp.Rect(bx, by, bw, bh));

                var oriented = new Mat();
                if (cropped.Height > cropped.Width)
                    Cv2.Rotate(cropped, oriented, RotateFlags.Rotate90Clockwise);
                else
                    cropped.CopyTo(oriented);

                var big = new Mat();
                Cv2.Resize(oriented, big,
                    new OpenCvSharp.Size(oriented.Width * 3, oriented.Height * 3),
                    interpolation: InterpolationFlags.Cubic);
                oriented.Dispose();

                // 디버그: 후보 이미지 저장
                if (dbgDir != null)
                    Cv2.ImWrite(Path.Combine(dbgDir, $"{ts}_cand{candidateIdx}.png"), big);
                candidateIdx++;

                results.Add(big);
            }

            return results;
        }

        /// <summary>Mat → 전처리 → OCR → 오인식 교정 → 패턴 매칭</summary>
        private string? OcrMat(Mat roi)
        {
            string dbgDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ocr_debug");
            string ts = DateTime.Now.ToString("HHmmss_fff");

            using var gray = new Mat();
            Cv2.CvtColor(roi, gray, ColorConversionCodes.BGR2GRAY);

            // CLAHE: 약하게만 사용 (clipLimit 낮춤 — 노이즈 증폭 방지)
            using var clahe = Cv2.CreateCLAHE(clipLimit: 1.5, tileGridSize: new OpenCvSharp.Size(8, 8));
            using var claheOut = new Mat();
            clahe.Apply(gray, claheOut);
            Cv2.ImWrite(Path.Combine(dbgDir, $"{ts}_gray.png"), claheOut);

            // 샤프닝 — 약하게 (2.0→1.4)
            using var blurred = new Mat();
            Cv2.GaussianBlur(claheOut, blurred, new OpenCvSharp.Size(0, 0), 1);
            using var sharp = new Mat();
            Cv2.AddWeighted(claheOut, 1.4, blurred, -0.4, 0, sharp);

            // 이진화 3가지 시도 — 첫 번째로 매칭되는 결과 사용
            var threshModes = new[]
            {
                (ThresholdTypes.Binary | ThresholdTypes.Otsu,    "otsu"),
                (ThresholdTypes.BinaryInv | ThresholdTypes.Otsu, "otsu_inv"),
                (ThresholdTypes.Binary,                           "fixed"),   // 고정 임계값 128
            };

            foreach (var (mode, label) in threshModes)
            {
                using var binary = new Mat();
                double fixedThresh = label == "fixed" ? 128 : 0;
                Cv2.Threshold(sharp, binary, fixedThresh, 255, mode);

                // 작은 노이즈 제거 (배경 잡음 → 오인식 원인)
                using var denoised = new Mat();
                using var noiseKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(2, 2));
                Cv2.MorphologyEx(binary, denoised, MorphTypes.Open, noiseKernel);
                Cv2.ImWrite(Path.Combine(dbgDir, $"{ts}_{label}.png"), denoised);

                Cv2.ImEncode(".png", denoised, out var buf);
                using var pix = Pix.LoadFromMemory(buf);
                using var page = _engine!.Process(pix, PageSegMode.SingleLine);
                string raw = page.GetText()?.Trim() ?? "";
                File.WriteAllText(Path.Combine(dbgDir, $"{ts}_{label}_raw.txt"), raw);
                System.Diagnostics.Debug.WriteLine($"[OCR:{label}] raw='{raw}'");

                if (string.IsNullOrEmpty(raw)) continue;

                string clean = Regex.Replace(raw, @"[^A-Za-z0-9\-_]", "").ToUpper();
                // 숫자 자리 오인식 교정
                // U→제거 (0이 U로 오인식될 때 → 삭제하면 자리수 보존됨)
                // V→제거 (P/노이즈가 V로 오인식)
                // O→0, I→1, Z→2, S→5, B→8
                clean = clean.Replace("U", "").Replace("V", "").Replace("Q", "0")
                             .Replace("O", "0").Replace("I", "1").Replace("N", "1")
                             .Replace("Z", "2").Replace("S", "5").Replace("B", "8");

                string? extracted = TryExtractSerial(clean);
                if (extracted != null) return extracted;
            }

            // ── 4번째 시도: 전처리 없이 원본 그레이 직접 투입
            // CLAHE+샤프닝이 오히려 노이즈를 증폭시키는 최악 프레임 대응
            {
                using var rawBinary = new Mat();
                Cv2.Threshold(gray, rawBinary, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
                Cv2.ImWrite(Path.Combine(dbgDir, $"{ts}_raw.png"), rawBinary);

                Cv2.ImEncode(".png", rawBinary, out var rawBuf);
                using var rawPix  = Pix.LoadFromMemory(rawBuf);
                using var rawPage = _engine!.Process(rawPix, PageSegMode.SingleLine);
                string rawText = rawPage.GetText()?.Trim() ?? "";
                File.WriteAllText(Path.Combine(dbgDir, $"{ts}_raw_raw.txt"), rawText);
                System.Diagnostics.Debug.WriteLine($"[OCR:raw] raw='{rawText}'");

                if (!string.IsNullOrEmpty(rawText))
                {
                    string rawClean = Regex.Replace(rawText, @"[^A-Za-z0-9\-_]", "").ToUpper();
                    rawClean = rawClean.Replace("U", "").Replace("V", "").Replace("Q", "0")
                                      .Replace("O", "0").Replace("I", "1").Replace("N", "1")
                                      .Replace("Z", "2").Replace("S", "5").Replace("B", "8");
                    string? rawExtracted = TryExtractSerial(rawClean);
                    if (rawExtracted != null) return rawExtracted;
                }
            }

            return null;
        }

        /// <summary>정제된 문자열에서 시리얼 번호 추출 — PG0619_0X 고정 포맷 (X = 1~5)</summary>
        private static string? TryExtractSerial(string clean)
        {
            // 앞부분(PG0619_0)은 고정 — 끝 자리 1~5만 특정하면 됨

            // 1단계: 구분자('_' 또는 '-') 뒤 끝 자리
            // 예) _01 _1 -01 -1 → 모두 처리
            var m = Regex.Match(clean, @"[_\-]0?([1-5])");
            if (m.Success)
                return "PG0619_0" + m.Groups[1].Value;

            // 2단계: 구분자 없이 숫자에 바로 붙은 경우 — 문자열 끝이 '0X'
            // 예) "PG061901" → 끝 "01"
            m = Regex.Match(clean, @"0([1-5])$");
            if (m.Success)
                return "PG0619_0" + m.Groups[1].Value;

            // 3단계: 문자열에서 '0X' 패턴 중 가장 마지막 것 사용
            // PG0619 안의 '06'은 6이 [1-5] 범위 밖이라 매칭 안됨
            var all = Regex.Matches(clean, @"0([1-5])");
            if (all.Count > 0)
                return "PG0619_0" + all[all.Count - 1].Groups[1].Value;

            return null;
        }

        /// <summary>매칭 위치 앞에서 prefix(PG 등)를 복원하여 최종 시리얼 조합</summary>
        private static string BuildResult(string clean, int matchIdx, string core)
        {
            var sb = new System.Text.StringBuilder();
            int look = 0;
            while (matchIdx - look - 1 >= 0 && look < 2)
            {
                char ch = clean[matchIdx - look - 1];
                if (char.IsLetter(ch))     { sb.Insert(0, ch); look++; }
                else if (ch == '6')        { sb.Insert(0, 'G'); look++; }  // G→6 오인식
                else break;
            }
            string prefix = sb.ToString();
            if (prefix == "G") prefix = "PG";   // P 잘린 경우 복원
            // 구분자 하이픈(-) → 언더스코어(_) 정규화
            return prefix + core.Replace("-", "_");
        }

        /// <summary>전체 텍스트 읽기 (디버깅용)</summary>
        public string? ReadAllText(Mat image)
        {
            if (_engine == null || image == null || image.Empty()) return null;
            try
            {
                using var gray = new Mat();
                if (image.Channels() == 3)
                    Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
                else
                    image.CopyTo(gray);

                Cv2.ImEncode(".png", gray, out var pngBuf);
                using var pix = Pix.LoadFromMemory(pngBuf);
                using var page = _engine.Process(pix, PageSegMode.Auto);
                return page.GetText()?.Trim();
            }
            catch { return null; }
        }

        public void Dispose() => _engine?.Dispose();
    }
}
