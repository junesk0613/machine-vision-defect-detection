using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using iText.Kernel.Pdf;
using iText.Kernel.Colors;
using iText.Layout;
using iText.Layout.Element;
using iText.Layout.Properties;
using iText.Layout.Borders;
using iText.Kernel.Font;
using iText.IO.Font;
using PCBInspection.Models;

namespace PCBInspection.Services
{
    public static class ReportService
    {
        private static readonly DeviceRgb ColBorder = new(30, 41, 59);
        private static readonly DeviceRgb ColHeaderBg = new(15, 23, 42);
        private static readonly DeviceRgb ColGreen = new(34, 197, 94);
        private static readonly DeviceRgb ColRed = new(220, 38, 38);
        private static readonly DeviceRgb ColBlue = new(14, 165, 233);

        // ══════════════════════════════════════
        //  PDF 리포트
        // ══════════════════════════════════════
        public static string GeneratePdf(string operatorName, List<EventLog> logs,
            List<InspectionRecord> records, List<SensorRecord> sensors, DateTime startTime)
        {
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string path = Path.Combine(dir, $"PCB검사리포트_{operatorName}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");

            using var writer = new PdfWriter(path);
            using var pdf = new PdfDocument(writer);
            using var doc = new Document(pdf);

            PdfFont font = GetSafeFont();
            PdfFont fontBold = LoadFont("malgunbd.ttf") ?? font;
            doc.SetFont(font).SetFontSize(9);

            // ── 제목 ──
            doc.Add(new Paragraph("PCB 양면 검사 시스템")
                .SetFont(fontBold).SetFontSize(20).SetMarginBottom(2));
            doc.Add(new Paragraph("운영 리포트")
                .SetFont(fontBold).SetFontSize(14).SetFontColor(ColBlue).SetMarginBottom(14));

            // ── 세션 정보 ──
            var infoTbl = new Table(UnitValue.CreatePercentArray(new float[] { 1, 2, 1, 2 }))
                .UseAllAvailableWidth().SetMarginBottom(18);
            AddInfoRow(infoTbl, "작성자", operatorName, font, fontBold);
            AddInfoRow(infoTbl, "작성일시", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), font, fontBold);
            AddInfoRow(infoTbl, "근무 시작", startTime.ToString("yyyy-MM-dd HH:mm:ss"), font, fontBold);
            AddInfoRow(infoTbl, "근무 시간", FormatDuration(DateTime.Now - startTime), font, fontBold);
            doc.Add(infoTbl);

            // ── 검사 요약 ──
            int total = records.Count;
            int ok = records.Count(r => r.Result == "OK");
            int ng = total - ok;
            double yieldPct = total > 0 ? Math.Round((double)ok / total * 100, 1) : 0;
            double defectPct = total > 0 ? Math.Round((double)ng / total * 100, 1) : 0;

            doc.Add(SectionTitle("검사 요약", fontBold));

            var sumTbl = new Table(UnitValue.CreatePercentArray(new float[] { 1, 1, 1, 1, 1 }))
                .UseAllAvailableWidth().SetMarginBottom(18);
            AddSummaryHeaders(sumTbl, new[] { "총 검사", "양품 (OK)", "불량 (NG)", "수율", "불량률" }, fontBold);
            AddBigCell(sumTbl, total.ToString(), font, null);
            AddBigCell(sumTbl, ok.ToString(), font, ColGreen);
            AddBigCell(sumTbl, ng.ToString(), font, ColRed);
            AddBigCell(sumTbl, $"{yieldPct}%", font, ColBlue);
            AddBigCell(sumTbl, $"{defectPct}%", font, ColRed);
            doc.Add(sumTbl);

            // ── 불량유형별 분석 ──
            var ngList = records.Where(r => r.Result == "NG").ToList();
            if (ngList.Count > 0)
            {
                doc.Add(SectionTitle("불량유형별 분석", fontBold));

                var dtbl = new Table(UnitValue.CreatePercentArray(new float[] { 2, 1, 1, 1, 1, 1 }))
                    .UseAllAvailableWidth().SetMarginBottom(18);
                AddHeaders(dtbl, new[] { "불량유형", "건수", "비율(%)", "평균온도(℃)", "평균습도(%)", "평균신뢰도" }, fontBold);

                foreach (var g in ngList.GroupBy(r => r.DefectType).OrderByDescending(g => g.Count()))
                {
                    double pct = Math.Round((double)g.Count() / total * 100, 1);
                    double avgT = Math.Round(g.Average(r => r.Temperature), 1);
                    double avgH = Math.Round(g.Average(r => r.Humidity), 1);
                    double avgC = Math.Round(g.Average(r => (r.FrontConf + r.BackConf) / 2.0) * 100, 1);
                    AddRow(dtbl, new[] { g.Key, g.Count().ToString(), $"{pct}%", $"{avgT}", $"{avgH}", $"{avgC}%" }, font);
                }
                doc.Add(dtbl);
            }

            // ── 검사 이력 ──
            doc.Add(SectionTitle("검사 이력", fontBold));

            var htbl = new Table(UnitValue.CreatePercentArray(
                    new float[] { 0.5f, 1.2f, 0.6f, 1.2f, 0.8f, 0.8f, 0.8f, 0.8f }))
                .UseAllAvailableWidth().SetMarginBottom(18);
            AddHeaders(htbl, new[] { "No", "시간", "판정", "불량유형", "온도(℃)", "습도(%)", "앞면신뢰도", "뒷면신뢰도" }, fontBold);

            foreach (var r in records.OrderBy(r => r.No))
            {
                var vals = new[]
                {
                    r.No.ToString(), r.Time.ToString("HH:mm:ss.ff"), r.Result, r.DefectType,
                    $"{r.Temperature:F1}", $"{r.Humidity:F1}", $"{r.FrontConf:P1}", $"{r.BackConf:P1}"
                };
                bool even = r.No % 2 == 0;
                for (int i = 0; i < vals.Length; i++)
                {
                    var cell = new Cell().Add(new Paragraph(vals[i]).SetFont(font).SetFontSize(8))
                        .SetBorder(new SolidBorder(ColBorder, 0.5f)).SetPadding(4);
                    if (even) cell.SetBackgroundColor(new DeviceRgb(245, 247, 250));
                    if (i == 2)
                    {
                        cell.SetBold();
                        cell.SetFontColor(r.Result == "OK" ? ColGreen : ColRed);
                    }
                    htbl.AddCell(cell);
                }
            }
            doc.Add(htbl);

            // ── 이벤트 로그 ──
            doc.Add(SectionTitle("이벤트 로그", fontBold));

            var ltbl = new Table(UnitValue.CreatePercentArray(new float[] { 1, 0.8f, 3 }))
                .UseAllAvailableWidth();
            AddHeaders(ltbl, new[] { "시간", "분류", "상세" }, fontBold);
            foreach (var l in logs.OrderByDescending(l => l.Time).Take(200))
                AddRow(ltbl, new[] { l.Time.ToString("HH:mm:ss"), l.Category, l.Detail }, font);
            doc.Add(ltbl);

            // ── 온습도 그래프 (24h) ──
            if (sensors.Count > 0)
            {
                doc.Add(new AreaBreak()); // 새 페이지
                doc.Add(SectionTitle("온습도 추이 (24시간)", fontBold));

                // 1시간 단위로 평균 계산
                var hourly = sensors
                    .GroupBy(s => new DateTime(s.Time.Year, s.Time.Month, s.Time.Day, s.Time.Hour, 0, 0))
                    .OrderBy(g => g.Key)
                    .Select(g => new { Hour = g.Key, AvgT = g.Average(x => x.Temperature), AvgH = g.Average(x => x.Humidity) })
                    .ToList();

                if (hourly.Count >= 2)
                {
                    // 그래프 영역 확보 (Cell)
                    var graphTable = new Table(1).UseAllAvailableWidth().SetMarginBottom(10);
                    var graphCell = new Cell()
                        .SetHeight(260)
                        .SetBorder(new SolidBorder(ColBorder, 0.5f))
                        .Add(new Paragraph(" ").SetFontSize(1));
                    graphTable.AddCell(graphCell);
                    doc.Add(graphTable);

                    // 그래프 그리기 - 마지막 페이지 캔버스에 직접
                    var lastPage = pdf.GetLastPage();
                    var pdfCanvas = new iText.Kernel.Pdf.Canvas.PdfCanvas(lastPage);

                    // 그래프 영역 계산 (페이지 좌표계, Y는 아래에서 위로)
                    var pageSize = lastPage.GetPageSize();
                    float docMarginLeft = 36f;
                    float docMarginRight = 36f;
                    float graphW = pageSize.GetWidth() - docMarginLeft - docMarginRight;
                    float graphH = 240f;

                    // 셀의 위치를 추정: doc.Add 후 현재 Y 위치
                    float oy = graphTable.GetAccessibilityProperties() != null ? 0 : 0;
                    // 더 단순하게: 페이지 상단에서 현재까지 누적된 위치를 추정하기 어려우므로
                    // 페이지 가운데 정도에 그리기
                    float ox = docMarginLeft;
                    oy = pageSize.GetBottom() + 120f;

                    float padL = 50f, padR = 50f, padT = 30f, padB = 40f;
                    float plotW = graphW - padL - padR;
                    float plotH = graphH - padT - padB;

                    // 배경
                    pdfCanvas.SetFillColor(new DeviceRgb(248, 250, 252))
                        .Rectangle(ox + padL, oy + padB, plotW, plotH).Fill();

                    // 그리드
                    pdfCanvas.SetStrokeColor(new DeviceRgb(226, 232, 240)).SetLineWidth(0.3f);
                    for (int i = 0; i <= 4; i++)
                    {
                        float y = oy + padB + plotH * i / 4f;
                        pdfCanvas.MoveTo(ox + padL, y).LineTo(ox + padL + plotW, y).Stroke();
                    }

                    // 데이터 범위
                    double tMin = Math.Floor(hourly.Min(p => p.AvgT) - 2);
                    double tMax = Math.Ceiling(hourly.Max(p => p.AvgT) + 2);
                    if (tMax - tMin < 5) tMax = tMin + 5;
                    double hMin = Math.Floor(hourly.Min(p => p.AvgH) - 5);
                    double hMax = Math.Ceiling(hourly.Max(p => p.AvgH) + 5);
                    if (hMax - hMin < 10) hMax = hMin + 10;

                    // X축 시간 라벨
                    pdfCanvas.SetFillColor(new DeviceRgb(100, 116, 139)).BeginText().SetFontAndSize(font, 7);
                    int step = Math.Max(1, hourly.Count / 8);
                    for (int i = 0; i < hourly.Count; i += step)
                    {
                        float x = ox + padL + plotW * i / (float)Math.Max(1, hourly.Count - 1);
                        string lbl = hourly[i].Hour.ToString("HH:mm");
                        pdfCanvas.SetTextMatrix(x - 12, oy + padB - 14).ShowText(lbl);
                    }
                    pdfCanvas.EndText();

                    // Y축 라벨 (왼쪽: 온도)
                    pdfCanvas.SetFillColor(new DeviceRgb(239, 68, 68)).BeginText().SetFontAndSize(font, 8);
                    for (int i = 0; i <= 4; i++)
                    {
                        double t = tMin + (tMax - tMin) * i / 4.0;
                        float y = oy + padB + plotH * i / 4f;
                        pdfCanvas.SetTextMatrix(ox + 10, y - 2).ShowText($"{t:F0}°C");
                    }
                    pdfCanvas.EndText();

                    // Y축 라벨 (오른쪽: 습도)
                    pdfCanvas.SetFillColor(new DeviceRgb(59, 130, 246)).BeginText().SetFontAndSize(font, 8);
                    for (int i = 0; i <= 4; i++)
                    {
                        double h = hMin + (hMax - hMin) * i / 4.0;
                        float y = oy + padB + plotH * i / 4f;
                        pdfCanvas.SetTextMatrix(ox + padL + plotW + 8, y - 2).ShowText($"{h:F0}%");
                    }
                    pdfCanvas.EndText();

                    // 온도 라인 (빨강)
                    pdfCanvas.SetStrokeColor(new DeviceRgb(239, 68, 68)).SetLineWidth(1.8f);
                    for (int i = 0; i < hourly.Count; i++)
                    {
                        float x = ox + padL + plotW * i / (float)Math.Max(1, hourly.Count - 1);
                        float y = oy + padB + (float)((hourly[i].AvgT - tMin) / (tMax - tMin)) * plotH;
                        if (i == 0) pdfCanvas.MoveTo(x, y);
                        else pdfCanvas.LineTo(x, y);
                    }
                    pdfCanvas.Stroke();

                    // 습도 라인 (파랑)
                    pdfCanvas.SetStrokeColor(new DeviceRgb(59, 130, 246)).SetLineWidth(1.8f);
                    for (int i = 0; i < hourly.Count; i++)
                    {
                        float x = ox + padL + plotW * i / (float)Math.Max(1, hourly.Count - 1);
                        float y = oy + padB + (float)((hourly[i].AvgH - hMin) / (hMax - hMin)) * plotH;
                        if (i == 0) pdfCanvas.MoveTo(x, y);
                        else pdfCanvas.LineTo(x, y);
                    }
                    pdfCanvas.Stroke();

                    // 범례
                    float legY = oy + padB + plotH + 8;
                    pdfCanvas.SetFillColor(new DeviceRgb(239, 68, 68))
                        .Rectangle(ox + padL + 10, legY, 12, 3).Fill();
                    pdfCanvas.SetFillColor(new DeviceRgb(40, 40, 40)).BeginText().SetFontAndSize(font, 9)
                        .SetTextMatrix(ox + padL + 26, legY - 2).ShowText("온도 (°C)").EndText();
                    pdfCanvas.SetFillColor(new DeviceRgb(59, 130, 246))
                        .Rectangle(ox + padL + 110, legY, 12, 3).Fill();
                    pdfCanvas.SetFillColor(new DeviceRgb(40, 40, 40)).BeginText().SetFontAndSize(font, 9)
                        .SetTextMatrix(ox + padL + 126, legY - 2).ShowText("습도 (%)").EndText();
                }

                // 시간대별 평균 테이블
                doc.Add(SectionTitle("시간대별 평균", fontBold));
                var stbl = new Table(UnitValue.CreatePercentArray(new float[] { 1, 1, 1, 1 }))
                    .UseAllAvailableWidth();
                AddHeaders(stbl, new[] { "시간", "평균 온도 (℃)", "평균 습도 (%)", "데이터 수" }, fontBold);
                foreach (var h in hourly)
                    AddRow(stbl, new[]
                    {
                        h.Hour.ToString("MM-dd HH:00"),
                        $"{h.AvgT:F1}",
                        $"{h.AvgH:F1}",
                        sensors.Count(s => s.Time.Hour == h.Hour.Hour && s.Time.Date == h.Hour.Date).ToString()
                    }, font);
                doc.Add(stbl);
            }

            return path;
        }

        // ══════════════════════════════════════
        //  Excel 리포트
        // ══════════════════════════════════════
        public static string GenerateExcel(string operatorName, List<EventLog> logs,
            List<InspectionRecord> records, List<SensorRecord> sensors, DateTime startTime)
        {
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string path = Path.Combine(dir, $"PCB검사리포트_{operatorName}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");

            using var wb = new XLWorkbook();

            // ── 요약 시트 ──
            var ws = wb.Worksheets.Add("검사 요약");
            ws.Cell(1, 1).Value = "PCB 양면 검사 시스템 — 운영 리포트";
            ws.Range("A1:E1").Merge().Style.Font.SetBold(true).Font.SetFontSize(14);

            ws.Cell(3, 1).Value = "작성자"; ws.Cell(3, 2).Value = operatorName;
            ws.Cell(3, 3).Value = "작성일시"; ws.Cell(3, 4).Value = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            ws.Cell(4, 1).Value = "근무 시작"; ws.Cell(4, 2).Value = startTime.ToString("yyyy-MM-dd HH:mm:ss");
            ws.Cell(4, 3).Value = "근무 시간"; ws.Cell(4, 4).Value = FormatDuration(DateTime.Now - startTime);
            ws.Range("A3:A4").Style.Font.SetBold(true).Fill.SetBackgroundColor(XLColor.LightGray);
            ws.Range("C3:C4").Style.Font.SetBold(true).Fill.SetBackgroundColor(XLColor.LightGray);

            int total = records.Count, ok = records.Count(r => r.Result == "OK"), ng = total - ok;
            double yieldPct = total > 0 ? Math.Round((double)ok / total * 100, 1) : 0;

            int row = 6;
            ExcelHeader(ws, row, new[] { "총 검사", "양품 (OK)", "불량 (NG)", "수율(%)", "불량률(%)" });
            row++;
            ws.Cell(row, 1).Value = total;
            ws.Cell(row, 2).Value = ok; ws.Cell(row, 2).Style.Font.SetFontColor(XLColor.Green);
            ws.Cell(row, 3).Value = ng; ws.Cell(row, 3).Style.Font.SetFontColor(XLColor.Red);
            ws.Cell(row, 4).Value = yieldPct;
            ws.Cell(row, 5).Value = total > 0 ? Math.Round((double)ng / total * 100, 1) : 0;

            // 불량유형별
            var ngList = records.Where(r => r.Result == "NG").ToList();
            if (ngList.Count > 0)
            {
                row += 2;
                ws.Cell(row, 1).Value = "불량유형별 분석";
                ws.Cell(row, 1).Style.Font.SetBold(true).Font.SetFontSize(11);
                row++;
                ExcelHeader(ws, row, new[] { "불량유형", "건수", "비율(%)", "평균온도(℃)", "평균습도(%)", "평균신뢰도(%)" });
                row++;
                foreach (var g in ngList.GroupBy(r => r.DefectType).OrderByDescending(g => g.Count()))
                {
                    ws.Cell(row, 1).Value = g.Key;
                    ws.Cell(row, 2).Value = g.Count();
                    ws.Cell(row, 3).Value = Math.Round((double)g.Count() / total * 100, 1);
                    ws.Cell(row, 4).Value = Math.Round(g.Average(r => r.Temperature), 1);
                    ws.Cell(row, 5).Value = Math.Round(g.Average(r => r.Humidity), 1);
                    ws.Cell(row, 6).Value = Math.Round(g.Average(r => (r.FrontConf + r.BackConf) / 2.0) * 100, 1);
                    row++;
                }
            }
            ws.Columns().AdjustToContents();

            // ── 검사 이력 시트 ──
            var ws2 = wb.Worksheets.Add("검사 이력");
            ExcelHeader(ws2, 1, new[] { "No", "시간", "판정", "불량유형", "온도(℃)", "습도(%)", "앞면신뢰도", "뒷면신뢰도" });
            int r2 = 2;
            foreach (var r in records.OrderBy(r => r.No))
            {
                ws2.Cell(r2, 1).Value = r.No;
                ws2.Cell(r2, 2).Value = r.Time.ToString("HH:mm:ss.ff");
                ws2.Cell(r2, 3).Value = r.Result;
                ws2.Cell(r2, 4).Value = r.DefectType;
                ws2.Cell(r2, 5).Value = Math.Round(r.Temperature, 1);
                ws2.Cell(r2, 6).Value = Math.Round(r.Humidity, 1);
                ws2.Cell(r2, 7).Value = $"{r.FrontConf:P1}";
                ws2.Cell(r2, 8).Value = $"{r.BackConf:P1}";
                ws2.Cell(r2, 3).Style.Font.SetBold(true)
                    .Font.SetFontColor(r.Result == "OK" ? XLColor.Green : XLColor.Red);
                r2++;
            }
            ws2.Columns().AdjustToContents();

            // ── 온습도 이력 시트 ──
            if (sensors.Count > 0)
            {
                var wsS = wb.Worksheets.Add("온습도 이력");
                ExcelHeader(wsS, 1, new[] { "시간", "온도(℃)", "습도(%)" });
                int rs = 2;
                foreach (var s in sensors.OrderBy(s => s.Time))
                {
                    wsS.Cell(rs, 1).Value = s.Time.ToString("yyyy-MM-dd HH:mm:ss");
                    wsS.Cell(rs, 2).Value = Math.Round(s.Temperature, 1);
                    wsS.Cell(rs, 3).Value = Math.Round(s.Humidity, 1);
                    rs++;
                }
                wsS.Columns().AdjustToContents();

                // 시간대별 평균 시트
                var wsAvg = wb.Worksheets.Add("시간대별 평균");
                ExcelHeader(wsAvg, 1, new[] { "시간", "평균 온도(℃)", "평균 습도(%)", "데이터 수" });
                int ra = 2;
                foreach (var g in sensors.GroupBy(s => new DateTime(s.Time.Year, s.Time.Month, s.Time.Day, s.Time.Hour, 0, 0)).OrderBy(g => g.Key))
                {
                    wsAvg.Cell(ra, 1).Value = g.Key.ToString("MM-dd HH:00");
                    wsAvg.Cell(ra, 2).Value = Math.Round(g.Average(x => x.Temperature), 1);
                    wsAvg.Cell(ra, 3).Value = Math.Round(g.Average(x => x.Humidity), 1);
                    wsAvg.Cell(ra, 4).Value = g.Count();
                    ra++;
                }
                wsAvg.Columns().AdjustToContents();
            }

            // ── 이벤트 로그 시트 ──
            var ws3 = wb.Worksheets.Add("이벤트 로그");
            ExcelHeader(ws3, 1, new[] { "시간", "분류", "상세" });
            int r3 = 2;
            foreach (var l in logs.OrderByDescending(l => l.Time))
            {
                ws3.Cell(r3, 1).Value = l.Time.ToString("yyyy-MM-dd HH:mm:ss");
                ws3.Cell(r3, 2).Value = l.Category;
                ws3.Cell(r3, 3).Value = l.Detail;
                r3++;
            }
            ws3.Columns().AdjustToContents();

            wb.SaveAs(path);
            return path;
        }

        // ══════════════════════════════════════
        //  헬퍼
        // ══════════════════════════════════════
        private static PdfFont? LoadFont(string fileName)
        {
            // 경로 후보 목록
            var paths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), fileName),
                Path.Combine(@"C:\Windows\Fonts", fileName),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Microsoft\Windows\Fonts", fileName),
            };

            foreach (var p in paths)
            {
                if (!File.Exists(p)) continue;
                try
                {
                    return PdfFontFactory.CreateFont(p, PdfEncodings.IDENTITY_H,
                        PdfFontFactory.EmbeddingStrategy.PREFER_EMBEDDED);
                }
                catch { }
                try
                {
                    return PdfFontFactory.CreateFont(p, PdfEncodings.IDENTITY_H);
                }
                catch { }
            }
            return null;
        }

        private static PdfFont GetSafeFont()
        {
            // 맑은 고딕 → 굴림 → 기본 폰트 순으로 시도
            return LoadFont("malgun.ttf")
                ?? LoadFont("gulim.ttc")
                ?? PdfFontFactory.CreateFont();
        }

        private static string FormatDuration(TimeSpan ts)
            => ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}시간 {ts.Minutes}분" : $"{ts.Minutes}분 {ts.Seconds}초";

        private static Paragraph SectionTitle(string text, PdfFont fontBold)
            => new Paragraph(text).SetFont(fontBold).SetFontSize(12).SetMarginBottom(6);

        private static void AddInfoRow(Table t, string label, string value, PdfFont font, PdfFont fontBold)
        {
            t.AddCell(new Cell().Add(new Paragraph(label).SetFont(fontBold).SetFontSize(9))
                .SetBackgroundColor(new DeviceRgb(240, 242, 245))
                .SetBorder(new SolidBorder(ColBorder, 0.5f)).SetPadding(5));
            t.AddCell(new Cell().Add(new Paragraph(value).SetFont(font).SetFontSize(9))
                .SetBorder(new SolidBorder(ColBorder, 0.5f)).SetPadding(5));
        }

        private static void AddSummaryHeaders(Table t, string[] headers, PdfFont fontBold)
        {
            foreach (var h in headers)
                t.AddHeaderCell(new Cell().Add(new Paragraph(h).SetFont(fontBold).SetFontSize(9))
                    .SetBackgroundColor(ColHeaderBg).SetFontColor(ColorConstants.WHITE)
                    .SetTextAlignment(TextAlignment.CENTER)
                    .SetBorder(new SolidBorder(ColBorder, 0.5f)).SetPadding(6));
        }

        private static void AddBigCell(Table t, string val, PdfFont font, DeviceRgb color)
        {
            var p = new Paragraph(val).SetFont(font).SetFontSize(14).SetBold()
                .SetTextAlignment(TextAlignment.CENTER);
            if (color != null) p.SetFontColor(color);
            t.AddCell(new Cell().Add(p).SetTextAlignment(TextAlignment.CENTER)
                .SetBorder(new SolidBorder(ColBorder, 0.5f)).SetPadding(8));
        }

        private static void AddHeaders(Table t, string[] headers, PdfFont fontBold)
        {
            foreach (var h in headers)
                t.AddHeaderCell(new Cell().Add(new Paragraph(h).SetFont(fontBold).SetFontSize(8))
                    .SetBackgroundColor(new DeviceRgb(240, 242, 245))
                    .SetBorder(new SolidBorder(ColBorder, 0.5f)).SetPadding(4));
        }

        private static void AddRow(Table t, string[] cells, PdfFont font)
        {
            foreach (var c in cells)
                t.AddCell(new Cell().Add(new Paragraph(c).SetFont(font).SetFontSize(8))
                    .SetBorder(new SolidBorder(ColBorder, 0.5f)).SetPadding(4));
        }

        private static void ExcelHeader(IXLWorksheet ws, int row, string[] headers)
        {
            for (int i = 0; i < headers.Length; i++)
            {
                ws.Cell(row, i + 1).Value = headers[i];
                ws.Cell(row, i + 1).Style.Font.SetBold(true)
                    .Fill.SetBackgroundColor(XLColor.LightGray);
            }
        }
    }
}
