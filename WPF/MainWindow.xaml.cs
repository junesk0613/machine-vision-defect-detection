using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using OpenCvSharp;
using PCBInspection.Models;
using PCBInspection.Services;
using Window = System.Windows.Window;

namespace PCBInspection.Views
{
    public partial class MainWindow : Window
    {
        private readonly SerialService _serial = new();
        private readonly ApiService _api = new();
        private readonly CameraService _camera = new();
        private readonly YoloService _yolo = new();
        private readonly OcrService _ocr = new();
        private readonly TcpServerService _tcpServer = new();
        private readonly string _operator;
        private readonly List<EventLog> _logs = new();
        private readonly ObservableCollection<EventLog> _logView = new();
        private readonly ObservableCollection<InspectionRecord> _inspRecords = new();
        private readonly List<SensorRecord> _sensorHistory = new();
        private readonly DispatcherTimer _sessionTimer = new();
        private readonly DispatcherTimer _diagTimer = new();
        private DateTime _lastActivity = DateTime.Now;
        private DateTime _startTime = DateTime.Now;
        private int _todayOk = 0, _todayNg = 0;
        private DateTime? _lastInspect = null;
        private bool _inspecting = false;
        private double _curTemp = 0, _curHumid = 0;
        private List<YoloDetection> _lastDetections = new();
        private readonly object _detLock = new();
        private volatile bool _pcbWasAbsent = true;
        private int _absentFrameCount = 0;
        private volatile bool _quickDetecting = false;

        public MainWindow(string operatorName)
        {
            try
            {
                InitializeComponent();
                _operator = operatorName;
                lblOperator.Text = $"사용자: {operatorName.ToUpper()}";
                dgvLog.ItemsSource = _logView;
                dgvInspect.ItemsSource = _inspRecords;

                foreach (var p in SerialService.GetAvailablePorts()) cmbPort.Items.Add(p);
                if (cmbPort.Items.Count > 0) cmbPort.SelectedIndex = 0;

                _sessionTimer.Interval = TimeSpan.FromMinutes(1);
                _sessionTimer.Tick += (s, e) => { if ((DateTime.Now - _lastActivity).TotalMinutes >= 30) { _sessionTimer.Stop(); MessageBox.Show("30분 미조작 — 자동 로그아웃"); Logout(); } };
                _sessionTimer.Start();

                _diagTimer.Interval = TimeSpan.FromSeconds(1);
                _diagTimer.Tick += (s, e) => UpdateDiag();
                _diagTimer.Start();

                MouseMove += (s, e) => _lastActivity = DateTime.Now;
                KeyDown += (s, e) => _lastActivity = DateTime.Now;

                if (File.Exists(AppConfig.OnnxModelPath))
                { _yolo.Load(AppConfig.OnnxModelPath); Log("INFO", "YOLO 모델 로드 완료"); }
                else Log("WARN", "YOLO 모델 없음");

                if (_ocr.Load()) { Log("INFO", "OCR 엔진 로드 완료 (Tesseract)"); }
                else { Log("WARN", "OCR 엔진 로드 실패 — tessdata/eng.traineddata 확인"); }

                try
                {
                    if (_camera.Open(AppConfig.CamFrontIndex, AppConfig.CamBackIndex))
                    { _camera.StartCapture(); Log("INFO", "카메라 연결 완료"); }
                    else Log("WARN", "카메라 연결 실패");
                }
                catch { Log("ERROR", "카메라 초기화 실패"); }

                Wire();
                _tcpServer.OnMessage += msg => UI(() => Log("INFO", msg));
                _tcpServer.Start();
                Log("INFO", "시스템 시작");

                // 로그인 성공 = Flask 연결 확인됨
                dotApi.Fill = new SolidColorBrush(Color.FromRgb(5, 150, 105));

                // Socket.IO 양방향 연결
                _ = ConnectSocketIO();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"초기화 오류:\n{ex.Message}\n\n{ex.InnerException?.Message}\n\n{ex.StackTrace}", "오류");
            }
        }

        private async Task ConnectSocketIO()
        {
            _api.OnRemoteEstop += () => UI(() => { Log("WARN", "웹에서 원격 비상정지"); DoEstopSync(); });
            _api.OnRemoteStart += () => UI(() => { Log("INFO", "웹에서 원격 가동"); _serial.Send("START"); SetLineStatus("RUNNING"); });
            _api.OnRemoteStop += () => UI(() => { Log("INFO", "웹에서 원격 정지"); _serial.Send("STOP"); SetLineStatus("STOPPED"); });
            _api.OnRemoteEstopClear += () => UI(() => { Log("INFO", "웹에서 비상정지 해제"); DoEstopClr(); });
            _api.OnSettingsChanged += msg => UI(() => { Log("INFO", $"웹에서 설정 변경: {msg}"); });
            _api.OnChatMessage += msg => UI(() =>
            {
                if (msg.line_id != _chatLineId) return;
                // 채팅 패널 열려 있으면 즉시 표시 + 읽음 처리
                if (panelChat.Visibility == Visibility.Visible)
                {
                    // "메시지 없음" 안내가 표시 중이면 제거
                    if (chatMessages.Children.Count == 1 && chatMessages.Children[0] is System.Windows.Controls.TextBlock)
                    {
                        chatMessages.Children.Clear();
                    }
                    AppendChatBubble(msg);
                    chatScroll.ScrollToBottom();
                    if (msg.direction == "web_to_wpf")
                        Task.Run(async () => await _api.MarkChatRead(_chatLineId));
                }
                else if (msg.direction == "web_to_wpf")
                {
                    // 패널 닫혀있고 관제실에서 온 메시지 → 배지 갱신 + 로그
                    string preview = msg.content?.Length > 30 ? msg.content.Substring(0, 30) + "..." : (msg.content ?? "(파일)");
                    Log("INFO", $"관제실 메시지: {msg.sender} — {preview}");
                    _ = RefreshChatBadgeFromServer();
                }
            });
            _api.OnMessage += msg => UI(() => Log("INFO", msg));

            await _api.ConnectSocket();
            UI(() =>
            {
                if (_api.IsSocketConnected)
                {
                    dotApi.Fill = new SolidColorBrush(Color.FromRgb(5, 150, 105));
                    Log("INFO", "Flask Socket.IO 연결 완료");
                }
            });

            // 채팅 배지 초기 갱신 + 30초 주기 타이머
            _ = RefreshChatBadgeFromServer();
            var chatBadgeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            chatBadgeTimer.Tick += async (s, e) =>
            {
                if (panelChat.Visibility != Visibility.Visible)
                    await RefreshChatBadgeFromServer();
            };
            chatBadgeTimer.Start();
        }

        private void Wire()
        {
            _serial.OnSensorData += d => UI(() => UpdSen(d));
            _serial.OnEstop += () => UI(() => DoEstopSync());
            _serial.OnEstopClear += () => UI(() => DoEstopClr());
            _serial.OnBoxFull += t => UI(() => Log("WARN", $"박스 만차: {t}"));
            _serial.OnCommLost += () => UI(() => { UpdSer(false); Log("ERROR", "시리얼 끊김"); Task.Run(async () => await _api.SendAlarm("COMM_LOST")); });
            _serial.OnRawMessage += m => UI(() => { rtbLog.AppendText(m + "\n"); rtbLog.ScrollToEnd(); });
            _camera.OnFrameCaptured += (f, b) => UI(() => OnFrame(f, b));
        }

        private int _frameSendCounter = 0;

        private async void OnFrame(Mat? front, Mat? back)
        {
            // 라이브 표시: 마지막 검출 박스를 현재 프레임에 오버레이해서 표시
            try
            {
                if (front != null && !front.Empty())
                {
                    using var displayF = front.Clone();
                    List<YoloDetection> dets;
                    lock (_detLock) { dets = new List<YoloDetection>(_lastDetections); }
                    if (dets.Count > 0) YoloService.DrawDetections(displayF, dets);
                    var s1 = CameraService.MatToBitmapSource(displayF);
                    imgFront.Source = s1; imgMonFront.Source = s1;
                }
                if (back != null && !back.Empty() && !_inspecting)
                {
                    var s2 = CameraService.MatToBitmapSource(back);
                    imgBack.Source = s2; imgMonBack.Source = s2;
                }
            }
            catch { }

            // Flask 스트리밍 (~10fps)
            _frameSendCounter++;
            if (_frameSendCounter >= 3)
            {
                _frameSendCounter = 0;
                try
                {
                    var fJpeg = (front != null && !front.Empty()) ? MatToSmallJpeg(front) : Array.Empty<byte>();
                    var bJpeg = (back != null && !back.Empty()) ? MatToSmallJpeg(back) : Array.Empty<byte>();
                    if (fJpeg.Length > 0 || bJpeg.Length > 0)
                        _ = _api.SendCameraFrame(fJpeg, bJpeg);
                }
                catch { }
            }

            if (front == null || front.Empty()) return;
            if (_inspecting || _quickDetecting) return;

            _quickDetecting = true;
            var fc = front.Clone();
            var bc = back?.Clone();

            bool hasPcb = await Task.Run(() => CameraService.IsPcbInFrame(fc));

            if (!hasPcb)
            {
                _absentFrameCount++;
                lock (_detLock) { _lastDetections = new List<YoloDetection>(); }
                if (_absentFrameCount >= 8) { _pcbWasAbsent = true; _absentFrameCount = 0; }
                fc.Dispose(); bc?.Dispose();
                _quickDetecting = false;
                lblPcbFront.Text = "PCB:WAIT";
                lblPcbFront.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184));
                return;
            }

            // PCB 감지됨
            _absentFrameCount = 0;
            lblPcbFront.Text = "PCB:DETECTED";
            lblPcbFront.Foreground = new SolidColorBrush(Color.FromRgb(34, 211, 238));

            if (_pcbWasAbsent)
            {
                // PCB ROI 진입 즉시 캡처 + 검사
                var detResult = _yolo.IsLoaded
                    ? await Task.Run(() => _yolo.Detect(fc, AppConfig.YoloConf))
                    : new List<YoloDetection>();
                lock (_detLock) { _lastDetections = detResult; }
                _pcbWasAbsent = false;
                _inspecting = true;
                _quickDetecting = false;
                try { await RunInspection(fc, bc, detResult); }
                finally
                {
                    _inspecting = false;
                    fc.Dispose(); bc?.Dispose();
                    lock (_detLock) { _lastDetections = new List<YoloDetection>(); }
                }
            }
            else
            {
                // 이미 검사 완료된 PCB → 박스 없음
                lock (_detLock) { _lastDetections = new List<YoloDetection>(); }
                fc.Dispose(); bc?.Dispose();
                _quickDetecting = false;
            }
        }

        private async Task RunInspection(Mat fullF, Mat? fullB, List<YoloDetection> dets)
        {
            Log("INFO", "검사 시작");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // OCR + 뒷면 번 검사를 백그라운드에서 실행 (YOLO는 이미 완료된 dets 사용)
            string? serialNumber;
            string backResult; string? backDefect; double burnPct;

            (serialNumber, backResult, backDefect, burnPct) = await Task.Run(() =>
            {
                string? sn = _ocr.IsLoaded ? _ocr.ReadSerialNumber(fullF) : null;
                string br = "OK"; string? bd = null; double bp = 0;
                if (fullB != null && !fullB.Empty())
                    (br, bd, bp) = YoloService.InspectBackBurn(fullB, 1.5);
                return (sn, br, bd, bp);
            });

            var ngDets = dets.Where(d => d.ClassName.StartsWith("f_")).ToList();
            float frontConf = dets.Count > 0 ? dets.Max(d => d.Confidence) : 0f;

            sw.Stop();
            lblInferFront.Text = $"Infer:{sw.ElapsedMilliseconds}ms";
            lblDetFront.Text = $"Det:{dets.Count}";

            // YOLO(앞면) + burn(뒷면) 실제 검사 결과 그대로 사용
            string frontResult = ngDets.Count > 0 ? "NG" : "OK";
            string? frontDefect = ngDets.Count > 0
                ? string.Join(",", ngDets.Select(d => d.ClassName).Distinct())
                : null;

            if (serialNumber != null) Log("INFO", $"OCR 시리얼: {serialNumber}");

            if (fullB != null && !fullB.Empty())
            {
                if (backResult == "NG")
                    YoloService.DrawBurnOverlay(fullB, fullB, 0, 0, false);
                try
                {
                    var bs = CameraService.MatToBitmapSource(fullB);
                    imgBack.Source = bs;
                    imgRecB.Source = bs;
                    bdRecB.Visibility = Visibility.Visible;
                }
                catch { }
                lblInferBack.Text = $"Burn:{burnPct:F1}%";
                lblDetBack.Text = backResult == "NG" ? "BURN!" : "OK";
            }
            else { lblInferBack.Text = "N/A"; lblDetBack.Text = "SKIP"; }

            // 최종 판정
            string result = (frontResult == "NG" || backResult == "NG") ? "NG" : "OK";
            string? defect = frontResult == "NG" && backResult == "NG" ? $"{frontDefect},{backDefect}"
                           : frontResult == "NG" ? frontDefect
                           : backResult == "NG" ? backDefect : null;
            float fConf = frontConf;
            float bConf = (float)(burnPct / 100.0);

            bool ok = result == "OK";
            lblResult.Text = result; lblMonResult.Text = result;
            lblRecDefect.Text = defect ?? "";
            resultPanel.Background = new SolidColorBrush(ok ? Color.FromRgb(5, 46, 22)   : Color.FromRgb(69, 10, 10));
            resultPanel.BorderBrush = new SolidColorBrush(ok ? Color.FromRgb(34, 197, 94) : Color.FromRgb(239, 68, 68));
            lblResult.Foreground    = new SolidColorBrush(ok ? Color.FromRgb(74, 222, 128) : Color.FromRgb(252, 165, 165));
            lblMonResult.Foreground = lblResult.Foreground;

            _serial.SendResult(result);

            // 박스 그린 복사본 생성 → 즉시 결과 패널에 표시 (API 호출 전)
            using var saveF = fullF.Clone();
            YoloService.DrawDetections(saveF, dets);
            try { imgRecF.Source = CameraService.MatToBitmapSource(saveF); bdRecF.Visibility = Visibility.Visible; } catch { }
            lblRecTime.Text = DateTime.Now.ToString("HH:mm:ss.ff");

            // 저장 및 Flask 전송
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string fN = $"{ts}_{result}_front.jpg";
            string bN = (fullB != null && !fullB.Empty()) ? $"{ts}_{result}_back.jpg" : "";
            Directory.CreateDirectory(AppConfig.ImageDir);
            Cv2.ImWrite(Path.Combine(AppConfig.ImageDir, fN), saveF);
            if (!string.IsNullOrEmpty(bN))
            {
                Cv2.ImWrite(Path.Combine(AppConfig.ImageDir, bN), fullB!);
                // HSV 마스크 디버그 이미지 저장 (빨간 픽셀 = 감지된 영역)
                using var dbg = YoloService.GetBurnDebugImage(fullB!);
                Cv2.ImWrite(Path.Combine(AppConfig.ImageDir, $"{ts}_{result}_back_mask.jpg"), dbg);
            }

            await _api.SendInspectionResult(result, defect, fN, bN, fConf, bConf, serialNumber);
            await _api.UploadImage(fN, CameraService.MatToJpeg(saveF));
            if (!string.IsNullOrEmpty(bN)) await _api.UploadImage(bN, CameraService.MatToJpeg(fullB!));

            if (ok) _todayOk++; else _todayNg++;
            _lastInspect = DateTime.Now;

            var record = new InspectionRecord
            {
                No = _inspRecords.Count + 1,
                Time = DateTime.Now,
                Result = result,
                DefectType = defect ?? "-",
                SerialNumber = serialNumber ?? "-",
                Temperature = _curTemp,
                Humidity = _curHumid,
                FrontConf = fConf,
                BackConf = bConf
            };
            _inspRecords.Insert(0, record);
            UpdateHistBadges();

            UpdateTodayStats();
            Log(ok ? "OK" : "ERROR", ok ? "양품 (OK)" : $"불량 (NG) — {defect}");
        }

        private void DoEstopSync()
        {
            estopBanner.Visibility = Visibility.Visible;
            SetLineStatus("ESTOP");
            Log("ERROR", "비상정지 작동");

            var darkBg = new SolidColorBrush(Color.FromRgb(15, 23, 42));
            var darkCard = new SolidColorBrush(Color.FromRgb(30, 41, 59));
            var accentRed = new SolidColorBrush(Color.FromRgb(220, 38, 38));
            var whiteBrush = Brushes.White;
            var grayBrush = new SolidColorBrush(Color.FromRgb(148, 163, 184));

            var dlg = new Window
            {
                Title = "비상정지 사유",
                Width = 560, Height = 480,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                WindowStyle = WindowStyle.ToolWindow,
                Owner = this,
                Background = darkBg,
                ResizeMode = ResizeMode.NoResize
            };

            var sp = new System.Windows.Controls.StackPanel { Margin = new Thickness(32) };

            // 제목
            sp.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "⚠ 비상정지 사유 선택",
                FontSize = 20, FontWeight = FontWeights.Bold,
                Foreground = accentRed,
                Margin = new Thickness(0, 0, 0, 20)
            });

            // 라디오 버튼 4개
            var reasons = new[] { "설비이상", "안전문제", "품질이상", "기타 (직접 입력)" };
            var radioGroup = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            var radios = new System.Windows.Controls.RadioButton[4];
            for (int i = 0; i < reasons.Length; i++)
            {
                radios[i] = new System.Windows.Controls.RadioButton
                {
                    Content = reasons[i],
                    FontSize = 15, Foreground = whiteBrush,
                    Margin = new Thickness(0, 6, 0, 6),
                    GroupName = "reason"
                };
                if (i == 0) radios[i].IsChecked = true;
                radioGroup.Children.Add(radios[i]);
            }
            sp.Children.Add(radioGroup);

            // 기타 입력칸 (기본 숨김)
            var txtLabel = new System.Windows.Controls.TextBlock
            {
                Text = "사유 입력",
                FontSize = 11, FontWeight = FontWeights.Bold,
                Foreground = grayBrush,
                Margin = new Thickness(0, 4, 0, 4),
                Visibility = Visibility.Collapsed
            };
            sp.Children.Add(txtLabel);

            var txtCustom = new System.Windows.Controls.TextBox
            {
                Height = 44, FontSize = 14,
                Background = darkCard,
                Foreground = whiteBrush,
                CaretBrush = whiteBrush,
                BorderBrush = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 10, 10, 10),
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 0, 0, 4),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            txtCustom.GotFocus += (s, e) =>
            {
                if (txtCustom.Text == "사유를 직접 입력하세요")
                { txtCustom.Text = ""; txtCustom.Foreground = whiteBrush; }
            };
            txtCustom.Text = "사유를 직접 입력하세요";
            txtCustom.Foreground = grayBrush;
            sp.Children.Add(txtCustom);

            // 기타 선택 시 입력칸 표시
            radios[3].Checked += (s, e) => { txtLabel.Visibility = Visibility.Visible; txtCustom.Visibility = Visibility.Visible; txtCustom.Focus(); };
            radios[0].Checked += (s, e) => { txtLabel.Visibility = Visibility.Collapsed; txtCustom.Visibility = Visibility.Collapsed; };
            radios[1].Checked += (s, e) => { txtLabel.Visibility = Visibility.Collapsed; txtCustom.Visibility = Visibility.Collapsed; };
            radios[2].Checked += (s, e) => { txtLabel.Visibility = Visibility.Collapsed; txtCustom.Visibility = Visibility.Collapsed; };

            // 확인 버튼
            var btn = new System.Windows.Controls.Button
            {
                Content = "확인", Height = 50,
                Margin = new Thickness(0, 22, 0, 0),
                FontSize = 16, FontWeight = FontWeights.Bold,
                Background = accentRed, Foreground = whiteBrush,
                BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand
            };
            btn.Click += (s, e) => { dlg.DialogResult = true; dlg.Close(); };
            sp.Children.Add(btn);
            dlg.Content = sp;

            string reason = "N/A";
            if (dlg.ShowDialog() == true)
            {
                for (int i = 0; i < radios.Length; i++)
                {
                    if (radios[i].IsChecked == true)
                    {
                        if (i == 3) // 기타
                        {
                            string custom = txtCustom.Text.Trim();
                            reason = string.IsNullOrEmpty(custom) || custom == "사유를 직접 입력하세요"
                                ? "기타" : $"기타: {custom}";
                        }
                        else reason = reasons[i];
                        break;
                    }
                }
            }

            Task.Run(async () => { try { await _api.SendAlarm("ESTOP", reason); await _api.SendSystemAction("estop", _operator); } catch { } });
            _serial.Send("ESTOP");
            Log("ERROR", $"비상정지 사유: {reason}");
        }

        private void DoEstopClr()
        {
            if (MessageBox.Show("재가동하시겠습니까?", "비상정지 해제", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            { _serial.SendResume(); Task.Run(async () => await _api.SendSystemAction("estop_clear", _operator)); estopBanner.Visibility = Visibility.Collapsed; SetLineStatus("RUNNING"); Log("INFO", "재가동"); }
        }

        private DateTime _lastSensorLog = DateTime.MinValue;

        private DateTime _lastSensorSend = DateTime.MinValue;

        private void UpdSen(SensorData d)
        {
            _curTemp = d.Temperature; _curHumid = d.Humidity;
            lblTemp.Text = $"{d.Temperature:F1}"; lblHumid.Text = $"{d.Humidity:F1}"; lblRpm.Text = $"{d.ConveyorRpm:F0}";
            barNg.Value = Math.Min(100, d.BoxNgLevel); barOk.Value = Math.Min(100, d.BoxOkLevel);
            lblNgPct.Text = $"{d.BoxNgLevel:F0}%"; lblOkPct.Text = $"{d.BoxOkLevel:F0}%";

            // Flask DB 전송: 5초 간격
            if ((DateTime.Now - _lastSensorSend).TotalSeconds >= 5)
            {
                _ = _api.SendSensorData(d.Temperature, d.Humidity, d.ConveyorRpm, d.BoxNgLevel, d.BoxOkLevel);
                _lastSensorSend = DateTime.Now;
            }

            // WPF 내부 이력: 1분 간격 (PDF 그래프용)
            if ((DateTime.Now - _lastSensorLog).TotalSeconds >= 60)
            {
                _sensorHistory.Add(new SensorRecord
                {
                    Time = DateTime.Now,
                    Temperature = d.Temperature,
                    Humidity = d.Humidity
                });
                _lastSensorLog = DateTime.Now;
                // 24시간 넘는 오래된 기록 제거
                var cutoff = DateTime.Now.AddHours(-24);
                _sensorHistory.RemoveAll(r => r.Time < cutoff);
            }
        }

        private void UpdateTodayStats()
        {
            int total = _todayOk + _todayNg;
            lblTodayTotal.Text = total.ToString();
            lblTodayOk.Text = $"{_todayOk} OK";
            lblTodayNg.Text = $"{_todayNg} NG";
            double y = total > 0 ? Math.Round((double)_todayOk / total * 100, 1) : 0;
            lblYield.Text = $"YIELD {y}%";
            lblMonTotal.Text = total.ToString();
            lblMonYield.Text = $"{y}%";
        }

        private void UpdateHistBadges()
        {
            int total = _inspRecords.Count;
            int ok = 0, ng = 0;
            foreach (var r in _inspRecords) { if (r.Result == "OK") ok++; else ng++; }
            double y = total > 0 ? Math.Round((double)ok / total * 100, 1) : 0;
            lblRecordCount.Text = $"{total}건";
            lblHistOk.Text = $"OK {ok}";
            lblHistNg.Text = $"NG {ng}";
            lblHistYield.Text = $"수율 {y}%";
            DrawPieChart();
        }

        private void UpdateDiag()
        {
            lblMonTime.Text = DateTime.Now.ToString("yyyy/MM/dd  HH:mm:ss");
        }

        private void UpdSer(bool on)
        {
            lblSerial.Text = on ? "SERIAL" : "SERIAL";
            dotSerial.Fill = new SolidColorBrush(on ? Color.FromRgb(5, 150, 105) : Color.FromRgb(220, 38, 38));
        }

        private void Log(string cat, string detail)
        {
            var e = new EventLog { Time = DateTime.Now, Category = cat, Detail = detail };
            _logs.Add(e);

            // ── 1) 시스템 이벤트 탭: 중요한 사건만 표시 ──
            //    - WARN/ERROR 카테고리는 무조건 포함
            //    - INFO라도 운영상 의미 있는 이벤트 키워드 포함되면 표시
            bool isSystemEvent = (cat == "WARN" || cat == "ERROR");
            if (!isSystemEvent && cat == "INFO")
            {
                string[] keywords = { "비상", "가동", "정지", "재가동", "만차", "알람", "온도", "습도", "통신", "불량" };
                foreach (var k in keywords)
                {
                    if (detail.Contains(k)) { isSystemEvent = true; break; }
                }
            }
            if (isSystemEvent)
            {
                _logView.Insert(0, e);
                if (_logView.Count > 500) _logView.RemoveAt(_logView.Count - 1);
            }

            // ── 2) 통합 로그 탭: 모든 시스템 로그 + 시리얼 메시지 ──
            //    rtbLog에 timestamp 포함해서 append
            var color = cat == "ERROR" ? "[ERR]" : cat == "WARN" ? "[WRN]" : "[INF]";
            try
            {
                rtbLog.AppendText($"{DateTime.Now:HH:mm:ss} {color} {detail}\n");
                rtbLog.ScrollToEnd();
            }
            catch { }
        }

        private void NavOp_Click(object s, RoutedEventArgs e)
        { panelOp.Visibility = Visibility.Visible; panelMon.Visibility = Visibility.Collapsed; panelChat.Visibility = Visibility.Collapsed;
          btnNavOp.Style = (Style)FindResource("NavActive"); btnNavMon.Style = (Style)FindResource("NavBtn"); btnNavChat.Style = (Style)FindResource("NavBtn"); }
        private void NavMon_Click(object s, RoutedEventArgs e)
        { panelMon.Visibility = Visibility.Visible; panelOp.Visibility = Visibility.Collapsed; panelChat.Visibility = Visibility.Collapsed;
          btnNavMon.Style = (Style)FindResource("NavActive"); btnNavOp.Style = (Style)FindResource("NavBtn"); btnNavChat.Style = (Style)FindResource("NavBtn"); }
        private async void NavChat_Click(object s, RoutedEventArgs e)
        {
            panelChat.Visibility = Visibility.Visible; panelOp.Visibility = Visibility.Collapsed; panelMon.Visibility = Visibility.Collapsed;
            btnNavChat.Style = (Style)FindResource("NavActive"); btnNavOp.Style = (Style)FindResource("NavBtn"); btnNavMon.Style = (Style)FindResource("NavBtn");
            // 배지 즉시 제거 (서버 응답 기다리지 않음)
            UpdateChatBadge(0);
            await InitChatPanel();
        }

        private void TabEvent_Click(object s, RoutedEventArgs e)
        { dgvLog.Visibility = Visibility.Visible; rtbLog.Visibility = Visibility.Collapsed;
          btnTabEvent.Background = new SolidColorBrush(Color.FromRgb(14, 165, 233)); btnTabEvent.Foreground = Brushes.White;
          btnTabSerial.Background = new SolidColorBrush(Color.FromRgb(30, 41, 59)); btnTabSerial.Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)); }
        private void TabSerial_Click(object s, RoutedEventArgs e)
        { rtbLog.Visibility = Visibility.Visible; dgvLog.Visibility = Visibility.Collapsed;
          btnTabSerial.Background = new SolidColorBrush(Color.FromRgb(14, 165, 233)); btnTabSerial.Foreground = Brushes.White;
          btnTabEvent.Background = new SolidColorBrush(Color.FromRgb(30, 41, 59)); btnTabEvent.Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)); }

        private void TabHist_Click(object s, RoutedEventArgs e)
        {
            dgvInspect.Visibility = Visibility.Visible; trendPanel.Visibility = Visibility.Collapsed;
            btnTabHist.Background = new SolidColorBrush(Color.FromRgb(14, 165, 233)); btnTabHist.Foreground = Brushes.White;
            btnTabTrend.Background = new SolidColorBrush(Color.FromRgb(26, 34, 53)); btnTabTrend.Foreground = new SolidColorBrush(Color.FromRgb(90, 107, 133));
        }
        private void TabTrend_Click(object s, RoutedEventArgs e)
        {
            dgvInspect.Visibility = Visibility.Collapsed; trendPanel.Visibility = Visibility.Visible;
            btnTabTrend.Background = new SolidColorBrush(Color.FromRgb(14, 165, 233)); btnTabTrend.Foreground = Brushes.White;
            btnTabHist.Background = new SolidColorBrush(Color.FromRgb(26, 34, 53)); btnTabHist.Foreground = new SolidColorBrush(Color.FromRgb(90, 107, 133));
            DrawTrendChart();
        }
        private void TrendCanvas_SizeChanged(object s, SizeChangedEventArgs e) => DrawTrendChart();
        private void PieCanvas_SizeChanged(object s, SizeChangedEventArgs e) => DrawPieChart();

        private void DrawTrendChart()
        {
            if (trendCanvas == null) return;
            trendCanvas.Children.Clear();
            double w = trendCanvas.ActualWidth, h = trendCanvas.ActualHeight;
            if (w < 50 || h < 50) return;

            if (_sensorHistory.Count < 2)
            {
                trendEmpty.Visibility = Visibility.Visible;
                return;
            }
            trendEmpty.Visibility = Visibility.Collapsed;

            // 24시간 내 데이터 → 1시간 평균으로 집계
            var cutoff = DateTime.Now.AddHours(-24);
            var raw = _sensorHistory.Where(r => r.Time >= cutoff).OrderBy(r => r.Time).ToList();
            if (raw.Count < 2) { trendEmpty.Visibility = Visibility.Visible; return; }

            var hourlyData = raw.GroupBy(r => new DateTime(r.Time.Year, r.Time.Month, r.Time.Day, r.Time.Hour, 0, 0))
                .Select(g => new SensorRecord
                {
                    Time = g.Key,
                    Temperature = Math.Round(g.Average(x => x.Temperature), 1),
                    Humidity = Math.Round(g.Average(x => x.Humidity), 1)
                })
                .OrderBy(r => r.Time).ToList();

            if (hourlyData.Count < 2) { trendEmpty.Visibility = Visibility.Visible; return; }
            var data = hourlyData;

            double padL = 50, padR = 50, padT = 24, padB = 36;
            double plotW = w - padL - padR;
            double plotH = h - padT - padB;
            if (plotW < 10 || plotH < 10) return;

            double tMin = Math.Floor(data.Min(r => r.Temperature) - 2);
            double tMax = Math.Ceiling(data.Max(r => r.Temperature) + 2);
            if (tMax - tMin < 5) tMax = tMin + 5;
            double hMin = Math.Floor(data.Min(r => r.Humidity) - 5);
            double hMax = Math.Ceiling(data.Max(r => r.Humidity) + 5);
            if (hMax - hMin < 10) hMax = hMin + 10;

            var gridBrush = new SolidColorBrush(Color.FromRgb(31, 42, 64));
            var labelBrush = new SolidColorBrush(Color.FromRgb(100, 116, 139));
            var tempBrush = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            var humidBrush = new SolidColorBrush(Color.FromRgb(59, 130, 246));

            for (int i = 0; i <= 4; i++)
            {
                double y = padT + plotH * i / 4.0;
                trendCanvas.Children.Add(new System.Windows.Shapes.Line { X1 = padL, Y1 = y, X2 = padL + plotW, Y2 = y, Stroke = gridBrush, StrokeThickness = 0.5 });
                double t = tMax - (tMax - tMin) * i / 4.0;
                var tLbl = new TextBlock { Text = $"{t:F0}°C", FontSize = 9, Foreground = tempBrush, FontFamily = new System.Windows.Media.FontFamily("Consolas") };
                System.Windows.Controls.Canvas.SetLeft(tLbl, 10); System.Windows.Controls.Canvas.SetTop(tLbl, y - 7);
                trendCanvas.Children.Add(tLbl);
                double hVal = hMax - (hMax - hMin) * i / 4.0;
                var hLbl = new TextBlock { Text = $"{hVal:F0}%", FontSize = 9, Foreground = humidBrush, FontFamily = new System.Windows.Media.FontFamily("Consolas") };
                System.Windows.Controls.Canvas.SetLeft(hLbl, padL + plotW + 8); System.Windows.Controls.Canvas.SetTop(hLbl, y - 7);
                trendCanvas.Children.Add(hLbl);
            }

            int step = Math.Max(1, data.Count / 10);
            for (int i = 0; i < data.Count; i += step)
            {
                double x = padL + plotW * i / (double)(data.Count - 1);
                var lbl = new TextBlock { Text = data[i].Time.ToString("HH:mm"), FontSize = 9, Foreground = labelBrush, FontFamily = new System.Windows.Media.FontFamily("Consolas") };
                System.Windows.Controls.Canvas.SetLeft(lbl, x - 14); System.Windows.Controls.Canvas.SetTop(lbl, padT + plotH + 6);
                trendCanvas.Children.Add(lbl);
            }

            var tempLine = new System.Windows.Shapes.Polyline { Stroke = tempBrush, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
            var humidLine = new System.Windows.Shapes.Polyline { Stroke = humidBrush, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
            for (int i = 0; i < data.Count; i++)
            {
                double x = padL + plotW * i / (double)(data.Count - 1);
                double yT = padT + plotH - (data[i].Temperature - tMin) / (tMax - tMin) * plotH;
                double yH = padT + plotH - (data[i].Humidity - hMin) / (hMax - hMin) * plotH;
                tempLine.Points.Add(new System.Windows.Point(x, yT));
                humidLine.Points.Add(new System.Windows.Point(x, yH));
            }
            trendCanvas.Children.Add(tempLine);
            trendCanvas.Children.Add(humidLine);

            var legT = new TextBlock { Text = "● 온도(°C)", FontSize = 10, Foreground = tempBrush, FontWeight = FontWeights.Bold };
            System.Windows.Controls.Canvas.SetLeft(legT, padL + 10); System.Windows.Controls.Canvas.SetTop(legT, 4);
            trendCanvas.Children.Add(legT);
            var legH = new TextBlock { Text = "● 습도(%)", FontSize = 10, Foreground = humidBrush, FontWeight = FontWeights.Bold };
            System.Windows.Controls.Canvas.SetLeft(legH, padL + 90); System.Windows.Controls.Canvas.SetTop(legH, 4);
            trendCanvas.Children.Add(legH);
            var cnt = new TextBlock { Text = $"{data.Count}h avg · raw {raw.Count} pts", FontSize = 9, Foreground = labelBrush, FontFamily = new System.Windows.Media.FontFamily("Consolas") };
            System.Windows.Controls.Canvas.SetRight(cnt, 10); System.Windows.Controls.Canvas.SetTop(cnt, 4);
            trendCanvas.Children.Add(cnt);
        }

        // ── 듀얼 도넛 차트 (좌: OK/NG 비율, 우: 불량 유형 분석) ──
        private void DrawPieChart()
        {
            if (pieCanvas == null) return;
            pieCanvas.Children.Clear();
            double w = pieCanvas.ActualWidth, h = pieCanvas.ActualHeight;
            if (w < 200 || h < 60) return;

            int total = _inspRecords.Count;
            if (total == 0) { pieEmpty.Visibility = Visibility.Visible; return; }
            pieEmpty.Visibility = Visibility.Collapsed;

            int okCount = _inspRecords.Count(r => r.Result == "OK");
            int ngCount = total - okCount;
            double yieldPct = Math.Round((double)okCount / total * 100, 1);

            var bgRing = Color.FromRgb(16, 22, 34);
            var okColor = Color.FromRgb(34, 120, 74);
            var ngColor = Color.FromRgb(140, 40, 40);
            var textDim = new SolidColorBrush(Color.FromRgb(90, 110, 140));
            var textMid = new SolidColorBrush(Color.FromRgb(150, 170, 195));
            var textBright = new SolidColorBrush(Color.FromRgb(220, 230, 240));
            var mono = new System.Windows.Media.FontFamily("Consolas");

            // ── 좌측: OK/NG 비율 도넛 ──
            double donutR = Math.Min(w * 0.12, h * 0.38);
            double donutThick = donutR * 0.3;
            double cx1 = w * 0.15, cy1 = h * 0.48;

            DrawArc(pieCanvas, cx1, cy1, donutR, donutThick, 0, 360, bgRing);
            if (okCount > 0)
                DrawArc(pieCanvas, cx1, cy1, donutR, donutThick, -90, (double)okCount / total * 360, okColor);
            if (ngCount > 0)
                DrawArc(pieCanvas, cx1, cy1, donutR, donutThick, -90 + (double)okCount / total * 360, (double)ngCount / total * 360, ngColor);

            // 중앙 텍스트
            AddText(pieCanvas, "OK RATE", 9, textDim, mono, cx1 - 16, cy1 - 14, FontWeights.Bold);
            AddText(pieCanvas, $"{yieldPct}%", 18, textBright, mono, cx1 - 20, cy1 - 2, FontWeights.Bold);

            // 좌측 아래 요약
            double sumY = cy1 + donutR + 10;
            AddText(pieCanvas, $"OK {okCount}   NG {ngCount}   TOTAL {total}", 10, textDim, mono, cx1 - 48, sumY, FontWeights.Bold);

            // ── 우측: 불량 유형 도넛 ──
            var defectGroups = _inspRecords.Where(r => r.Result == "NG" && r.DefectType != "-")
                .SelectMany(r => r.DefectType.Split(',', StringSplitOptions.RemoveEmptyEntries))
                .Select(s => s.Trim())
                .GroupBy(s => s)
                .Select(g => new { Type = g.Key, Count = g.Count() })
                .OrderByDescending(g => g.Count)
                .ToList();

            double cx2 = w * 0.42, cy2 = h * 0.48;
            var defectColors = new[] {
                Color.FromRgb(140, 40, 40),
                Color.FromRgb(150, 100, 25),
                Color.FromRgb(80, 55, 130),
                Color.FromRgb(35, 100, 120),
                Color.FromRgb(120, 50, 90),
            };

            DrawArc(pieCanvas, cx2, cy2, donutR, donutThick, 0, 360, bgRing);

            if (defectGroups.Count > 0)
            {
                double startDeg = -90;
                for (int i = 0; i < defectGroups.Count; i++)
                {
                    double sweep = (double)defectGroups[i].Count / ngCount * 360;
                    DrawArc(pieCanvas, cx2, cy2, donutR, donutThick, startDeg, sweep, defectColors[i % defectColors.Length]);
                    startDeg += sweep;
                }

                // 중앙: 최다 불량
                var top = defectGroups[0];
                double topPct = Math.Round((double)top.Count / ngCount * 100, 0);
                AddText(pieCanvas, "TOP DEFECT", 8, textDim, mono, cx2 - 22, cy2 - 16, FontWeights.Bold);
                AddText(pieCanvas, top.Type.ToUpper(), 13, textBright, mono, cx2 - (top.Type.Length * 3), cy2 - 4, FontWeights.Bold);
                AddText(pieCanvas, $"{topPct}%", 14, textMid, mono, cx2 - 10, cy2 + 8, FontWeights.Bold);
            }
            else
            {
                AddText(pieCanvas, "NO", 10, textDim, mono, cx2 - 8, cy2 - 10, FontWeights.Bold);
                AddText(pieCanvas, "DEFECT", 9, textDim, mono, cx2 - 14, cy2 + 2, FontWeights.Bold);
            }

            // ── 우측 범례 ──
            double legX = w * 0.58, legY = h * 0.10;

            AddText(pieCanvas, "DEFECT ANALYSIS", 12, textDim, mono, legX, legY, FontWeights.Bold);
            legY += 26;

            if (defectGroups.Count == 0)
            {
                AddText(pieCanvas, "불량 없음", 13, textDim, mono, legX, legY);
            }
            else
            {
                for (int i = 0; i < defectGroups.Count && i < 5; i++)
                {
                    var g = defectGroups[i];
                    double pct = Math.Round((double)g.Count / ngCount * 100, 1);
                    var color = defectColors[i % defectColors.Length];

                    // 색상 인디케이터
                    var rect = new System.Windows.Shapes.Rectangle { Width = 14, Height = 14, RadiusX = 2, RadiusY = 2, Fill = new SolidColorBrush(color) };
                    System.Windows.Controls.Canvas.SetLeft(rect, legX);
                    System.Windows.Controls.Canvas.SetTop(rect, legY + 1);
                    pieCanvas.Children.Add(rect);

                    AddText(pieCanvas, $"{g.Type}", 14, textMid, mono, legX + 16, legY - 1, FontWeights.Bold);
                    AddText(pieCanvas, $"{g.Count}건 ({pct}%)", 12, textDim, mono, legX + 16 + g.Type.Length * 9 + 16, legY, FontWeights.Normal);
                    legY += 28;
                }
            }

            // 우측 하단: 전체 요약
            legY += 6;
            AddText(pieCanvas, $"NG TOTAL  {ngCount}건  /  전체 {total}건", 12, textDim, mono, legX, legY, FontWeights.Bold);
        }

        private void AddText(Canvas c, string text, double size, System.Windows.Media.Brush fg, System.Windows.Media.FontFamily font, double x, double y, FontWeight? weight = null)
        {
            var tb = new TextBlock { Text = text, FontSize = size, Foreground = fg, FontFamily = font };
            if (weight.HasValue) tb.FontWeight = weight.Value;
            System.Windows.Controls.Canvas.SetLeft(tb, x);
            System.Windows.Controls.Canvas.SetTop(tb, y);
            c.Children.Add(tb);
        }

        /// <summary>도넛 호(arc) 그리기</summary>
        private static void DrawArc(Canvas canvas, double cx, double cy, double outerR, double thickness, double startDeg, double sweepDeg, Color color)
        {
            if (sweepDeg <= 0) return;
            double innerR = outerR - thickness;
            // 각도를 라디안으로
            double s = startDeg * Math.PI / 180;
            double e = (startDeg + sweepDeg) * Math.PI / 180;
            bool large = sweepDeg > 180;

            // 360도 처리 (약간 줄여서 완전한 원 방지)
            if (sweepDeg >= 359.9) e = s + Math.PI * 2 - 0.001;

            var p1 = new System.Windows.Point(cx + outerR * Math.Cos(s), cy + outerR * Math.Sin(s));
            var p2 = new System.Windows.Point(cx + outerR * Math.Cos(e), cy + outerR * Math.Sin(e));
            var p3 = new System.Windows.Point(cx + innerR * Math.Cos(e), cy + innerR * Math.Sin(e));
            var p4 = new System.Windows.Point(cx + innerR * Math.Cos(s), cy + innerR * Math.Sin(s));

            var fig = new PathFigure { StartPoint = p1, IsClosed = true };
            fig.Segments.Add(new ArcSegment(p2, new System.Windows.Size(outerR, outerR), 0, large, SweepDirection.Clockwise, true));
            fig.Segments.Add(new LineSegment(p3, true));
            fig.Segments.Add(new ArcSegment(p4, new System.Windows.Size(innerR, innerR), 0, large, SweepDirection.Counterclockwise, true));

            var geom = new PathGeometry();
            geom.Figures.Add(fig);
            var path = new System.Windows.Shapes.Path { Data = geom, Fill = new SolidColorBrush(color) };
            canvas.Children.Add(path);
        }

        private async void BtnStart_Click(object s, RoutedEventArgs e) { _serial.SendStart(); await _api.SendSystemAction("start", _operator); Log("INFO", "라인 가동"); SetLineStatus("RUNNING"); }


        private void SetLineStatus(string status)
        {
            UI(() =>
            {
                switch (status)
                {
                    case "RUNNING":
                        lblLineStatus.Text = "RUNNING";
                        lblLineStatus.Foreground = new SolidColorBrush(Color.FromRgb(74, 222, 128));
                        dotLineStatus.Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94));
                        bdLineStatus.Background = new SolidColorBrush(Color.FromRgb(5, 46, 22));
                        break;
                    case "STOPPED":
                        lblLineStatus.Text = "STOPPED";
                        lblLineStatus.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184));
                        dotLineStatus.Fill = new SolidColorBrush(Color.FromRgb(100, 116, 139));
                        bdLineStatus.Background = new SolidColorBrush(Color.FromRgb(30, 41, 59));
                        break;
                    case "ESTOP":
                        lblLineStatus.Text = "EMERGENCY";
                        lblLineStatus.Foreground = new SolidColorBrush(Color.FromRgb(252, 165, 165));
                        dotLineStatus.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                        bdLineStatus.Background = new SolidColorBrush(Color.FromRgb(69, 10, 10));
                        break;
                    default:
                        lblLineStatus.Text = "READY";
                        lblLineStatus.Foreground = new SolidColorBrush(Color.FromRgb(74, 222, 128));
                        dotLineStatus.Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94));
                        bdLineStatus.Background = new SolidColorBrush(Color.FromRgb(5, 46, 22));
                        break;
                }
            });
        }
        private void BtnEstop_Click(object s, RoutedEventArgs e) => DoEstopSync();
        private void BtnEstopClear_Click(object s, RoutedEventArgs e)
        {
            if (estopBanner.Visibility != Visibility.Visible)
            {
                MessageBox.Show("현재 비상정지 상태가 아닙니다.", "안내");
                return;
            }
            DoEstopClr();
        }
        private void BtnSettings_Click(object s, RoutedEventArgs e) => ShowSettingsDialog();
        private void BtnSerialConn_Click(object s, RoutedEventArgs e) { if (cmbPort.SelectedItem != null && _serial.Connect(cmbPort.SelectedItem.ToString()!)) { UpdSer(true); Log("INFO", $"시리얼 연결: {cmbPort.SelectedItem}"); } }
        private void BtnSerialDisc_Click(object s, RoutedEventArgs e) { _serial.Disconnect(); UpdSer(false); Log("INFO", "시리얼 끊김"); }

        private void ShowSettingsDialog()
        {
            var darkBg = new SolidColorBrush(Color.FromRgb(15, 23, 42));
            var cardBg = new SolidColorBrush(Color.FromRgb(30, 41, 59));
            var whiteBrush = Brushes.White;
            var grayBrush = new SolidColorBrush(Color.FromRgb(148, 163, 184));
            var accentBrush = new SolidColorBrush(Color.FromRgb(14, 165, 233));
            var borderBrush = new SolidColorBrush(Color.FromRgb(71, 85, 105));

            var dlg = new Window
            {
                Title = "검사 설정",
                Width = 580, Height = 620,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                WindowStyle = WindowStyle.None,
                Owner = this,
                Background = darkBg,
                ResizeMode = ResizeMode.NoResize,
                BorderBrush = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                BorderThickness = new Thickness(1)
            };

            var sp = new System.Windows.Controls.StackPanel { Margin = new Thickness(28, 8, 28, 28) };

            // 타이틀 (제거 - 커스텀 헤더로 이동)
            sp.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "⚙ 검사 설정",
                FontSize = 20, FontWeight = FontWeights.Bold,
                Foreground = whiteBrush, Margin = new Thickness(0, 0, 0, 6)
            });
            sp.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "YOLO 임계값 및 온습도 정상 범위 설정 (Flask DB에 저장됩니다)",
                FontSize = 11, Foreground = grayBrush, Margin = new Thickness(0, 0, 0, 20)
            });

            // ── 시리얼 포트 (RS232) ──
            sp.Children.Add(new System.Windows.Controls.TextBlock { Text = "시리얼 포트 (RS232)", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = grayBrush, Margin = new Thickness(0, 0, 0, 6) });
            var serialPanel = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 18) };
            var cmbSerialPort = new System.Windows.Controls.ComboBox { Width = 180, Height = 38, FontSize = 13, Background = cardBg, Foreground = whiteBrush, BorderBrush = borderBrush, BorderThickness = new Thickness(1) };
            foreach (var p in SerialService.GetAvailablePorts()) cmbSerialPort.Items.Add(p);
            if (cmbSerialPort.Items.Count > 0) cmbSerialPort.SelectedIndex = 0;
            // 현재 연결된 포트가 있으면 선택
            if (_serial.IsConnected)
            {
                for (int i = 0; i < cmbSerialPort.Items.Count; i++)
                    if (cmbSerialPort.Items[i].ToString() == AppConfig.LastSerialPort)
                    { cmbSerialPort.SelectedIndex = i; break; }
            }
            var lblSerialStatus = new System.Windows.Controls.TextBlock
            {
                Text = _serial.IsConnected ? "● 연결됨" : "○ 미연결",
                FontSize = 12, FontWeight = FontWeights.Bold,
                Foreground = _serial.IsConnected ? new SolidColorBrush(Color.FromRgb(34, 197, 94)) : new SolidColorBrush(Color.FromRgb(239, 68, 68)),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0)
            };
            var btnSerialConnect = new System.Windows.Controls.Button
            {
                Content = "연결", Width = 70, Height = 38, FontSize = 12, FontWeight = FontWeights.Bold,
                Background = new SolidColorBrush(Color.FromRgb(22, 120, 74)), Foreground = whiteBrush,
                BorderThickness = new Thickness(0), Margin = new Thickness(8, 0, 0, 0), Cursor = System.Windows.Input.Cursors.Hand
            };
            var btnSerialDisc = new System.Windows.Controls.Button
            {
                Content = "해제", Width = 70, Height = 38, FontSize = 12, FontWeight = FontWeights.Bold,
                Background = cardBg, Foreground = grayBrush,
                BorderThickness = new Thickness(0), Margin = new Thickness(4, 0, 0, 0), Cursor = System.Windows.Input.Cursors.Hand
            };
            btnSerialConnect.Click += (s, ev) =>
            {
                if (cmbSerialPort.SelectedItem == null) return;
                string port = cmbSerialPort.SelectedItem.ToString()!;
                if (_serial.IsConnected) _serial.Disconnect();
                if (_serial.Connect(port))
                {
                    AppConfig.LastSerialPort = port;
                    lblSerialStatus.Text = "● 연결됨";
                    lblSerialStatus.Foreground = new SolidColorBrush(Color.FromRgb(34, 197, 94));
                    UpdSer(true);
                    Log("INFO", $"시리얼 연결: {port}");
                }
                else
                {
                    lblSerialStatus.Text = "✗ 연결 실패";
                    lblSerialStatus.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                }
            };
            btnSerialDisc.Click += (s, ev) =>
            {
                _serial.Disconnect();
                UpdSer(false);
                lblSerialStatus.Text = "○ 미연결";
                lblSerialStatus.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                Log("INFO", "시리얼 해제");
            };
            serialPanel.Children.Add(cmbSerialPort);
            serialPanel.Children.Add(btnSerialConnect);
            serialPanel.Children.Add(btnSerialDisc);
            serialPanel.Children.Add(lblSerialStatus);
            sp.Children.Add(serialPanel);

            // ── YOLO 신뢰도 ──
            sp.Children.Add(new System.Windows.Controls.TextBlock { Text = "YOLO 신뢰도 임계값", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = grayBrush, Margin = new Thickness(0, 0, 0, 6) });
            var yoloGrid = new Grid { Margin = new Thickness(0, 0, 0, 18) };
            yoloGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            yoloGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var sldYolo = new Slider { Minimum = 10, Maximum = 100, Value = AppConfig.YoloConf * 100, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            var lblYolo = new System.Windows.Controls.TextBlock { Text = $"{AppConfig.YoloConf:F2}", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = accentBrush, FontFamily = new System.Windows.Media.FontFamily("Consolas"), MinWidth = 70, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            sldYolo.ValueChanged += (s, ev) => lblYolo.Text = $"{sldYolo.Value / 100.0:F2}";
            yoloGrid.Children.Add(sldYolo);
            System.Windows.Controls.Grid.SetColumn(lblYolo, 1);
            yoloGrid.Children.Add(lblYolo);
            sp.Children.Add(yoloGrid);

            // ── 온도 범위 ──
            sp.Children.Add(new System.Windows.Controls.TextBlock { Text = "온도 정상 범위 (°C)", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = grayBrush, Margin = new Thickness(0, 0, 0, 6) });
            var tempPanel = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 18) };
            var txtTmin = new System.Windows.Controls.TextBox { Text = AppConfig.TempMin.ToString("F1"), Width = 110, Height = 40, FontSize = 15, Background = cardBg, Foreground = whiteBrush, CaretBrush = whiteBrush, BorderBrush = borderBrush, BorderThickness = new Thickness(1), Padding = new Thickness(10, 8, 10, 8), VerticalContentAlignment = VerticalAlignment.Center };
            var txtTmax = new System.Windows.Controls.TextBox { Text = AppConfig.TempMax.ToString("F1"), Width = 110, Height = 40, FontSize = 15, Background = cardBg, Foreground = whiteBrush, CaretBrush = whiteBrush, BorderBrush = borderBrush, BorderThickness = new Thickness(1), Padding = new Thickness(10, 8, 10, 8), VerticalContentAlignment = VerticalAlignment.Center };
            tempPanel.Children.Add(txtTmin);
            tempPanel.Children.Add(new System.Windows.Controls.TextBlock { Text = "  ~  ", FontSize = 14, Foreground = grayBrush, VerticalAlignment = VerticalAlignment.Center });
            tempPanel.Children.Add(txtTmax);
            tempPanel.Children.Add(new System.Windows.Controls.TextBlock { Text = "  °C", FontSize = 13, Foreground = grayBrush, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(tempPanel);

            // ── 습도 범위 ──
            sp.Children.Add(new System.Windows.Controls.TextBlock { Text = "습도 정상 범위 (%)", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = grayBrush, Margin = new Thickness(0, 0, 0, 6) });
            var humidPanel = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 22) };
            var txtHmin = new System.Windows.Controls.TextBox { Text = AppConfig.HumidMin.ToString("F1"), Width = 110, Height = 40, FontSize = 15, Background = cardBg, Foreground = whiteBrush, CaretBrush = whiteBrush, BorderBrush = borderBrush, BorderThickness = new Thickness(1), Padding = new Thickness(10, 8, 10, 8), VerticalContentAlignment = VerticalAlignment.Center };
            var txtHmax = new System.Windows.Controls.TextBox { Text = AppConfig.HumidMax.ToString("F1"), Width = 110, Height = 40, FontSize = 15, Background = cardBg, Foreground = whiteBrush, CaretBrush = whiteBrush, BorderBrush = borderBrush, BorderThickness = new Thickness(1), Padding = new Thickness(10, 8, 10, 8), VerticalContentAlignment = VerticalAlignment.Center };
            humidPanel.Children.Add(txtHmin);
            humidPanel.Children.Add(new System.Windows.Controls.TextBlock { Text = "  ~  ", FontSize = 14, Foreground = grayBrush, VerticalAlignment = VerticalAlignment.Center });
            humidPanel.Children.Add(txtHmax);
            humidPanel.Children.Add(new System.Windows.Controls.TextBlock { Text = "  %", FontSize = 13, Foreground = grayBrush, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(humidPanel);

            // 상태 메시지
            var status = new System.Windows.Controls.TextBlock { FontSize = 11, Margin = new Thickness(0, 0, 0, 8) };
            sp.Children.Add(status);

            // 버튼
            var btnPanel = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var btnCancel = new System.Windows.Controls.Button { Content = "취소", Width = 90, Height = 42, FontSize = 13, Background = cardBg, Foreground = whiteBrush, BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 8, 0), Cursor = System.Windows.Input.Cursors.Hand };
            btnCancel.Click += (s, ev) => dlg.Close();
            var btnSave = new System.Windows.Controls.Button { Content = "저장", Width = 110, Height = 42, FontSize = 13, FontWeight = FontWeights.Bold, Background = accentBrush, Foreground = whiteBrush, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand };
            btnPanel.Children.Add(btnCancel);
            btnPanel.Children.Add(btnSave);
            sp.Children.Add(btnPanel);

            // 저장 핸들러
            btnSave.Click += async (s, ev) =>
            {
                if (!double.TryParse(txtTmin.Text, out double tMin) ||
                    !double.TryParse(txtTmax.Text, out double tMax) ||
                    !double.TryParse(txtHmin.Text, out double hMin) ||
                    !double.TryParse(txtHmax.Text, out double hMax))
                {
                    status.Text = "❌ 숫자만 입력하세요";
                    status.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                    return;
                }
                if (tMin >= tMax || hMin >= hMax)
                {
                    status.Text = "❌ 최솟값이 최댓값보다 작아야 합니다";
                    status.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                    return;
                }
                float yConf = (float)(sldYolo.Value / 100.0);

                status.Text = "저장 중...";
                status.Foreground = grayBrush;

                bool ok = await _api.SaveSettings(yConf, tMin, tMax, hMin, hMax);
                if (ok)
                {
                    AppConfig.YoloConf = yConf;
                    AppConfig.TempMin = tMin; AppConfig.TempMax = tMax;
                    AppConfig.HumidMin = hMin; AppConfig.HumidMax = hMax;
                    // 메인 화면 슬라이더도 동기화
                    sldConf.Value = yConf * 100;
                    Log("INFO", $"설정 저장 — YOLO:{yConf:F2}, T:{tMin}~{tMax}, H:{hMin}~{hMax}");
                    status.Text = "✓ 저장 완료";
                    status.Foreground = new SolidColorBrush(Color.FromRgb(34, 197, 94));
                    await Task.Delay(800);
                    dlg.Close();
                }
                else
                {
                    status.Text = "❌ 서버 저장 실패";
                    status.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                }
            };

            // ━━━━━━ 커스텀 헤더 (시스템 타이틀바 대체) ━━━━━━
            var headerBg = new SolidColorBrush(Color.FromRgb(10, 18, 32));
            var header = new Grid
            {
                Background = headerBg,
                Height = 42
            };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var headerTitle = new System.Windows.Controls.TextBlock
            {
                Text = "⚙  검사 설정 · INSPECTION SETTINGS",
                FontSize = 12, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(18, 0, 0, 0)
            };
            header.Children.Add(headerTitle);

            // 커스텀 ✕ 닫기 버튼 (Border 기반)
            var closeBorder = new System.Windows.Controls.Border
            {
                Width = 42, Height = 42,
                Background = System.Windows.Media.Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            var closeText = new System.Windows.Controls.TextBlock
            {
                Text = "✕",
                FontSize = 14, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            closeBorder.Child = closeText;
            closeBorder.MouseEnter += (s, ev) =>
            {
                closeBorder.Background = new SolidColorBrush(Color.FromRgb(220, 38, 38));
                closeText.Foreground = System.Windows.Media.Brushes.White;
            };
            closeBorder.MouseLeave += (s, ev) =>
            {
                closeBorder.Background = System.Windows.Media.Brushes.Transparent;
                closeText.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184));
            };
            closeBorder.MouseLeftButtonDown += (s, ev) => dlg.Close();
            System.Windows.Controls.Grid.SetColumn(closeBorder, 1);
            header.Children.Add(closeBorder);

            // 헤더 영역 드래그로 윈도우 이동
            header.MouseLeftButtonDown += (s, ev) =>
            {
                if (ev.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
                {
                    try { dlg.DragMove(); } catch { }
                }
            };

            // 루트 Grid: 헤더 + 본문
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.Children.Add(header);
            System.Windows.Controls.Grid.SetRow(sp, 1);
            root.Children.Add(sp);

            dlg.Content = root;

            // 다이얼로그가 보이면 백그라운드로 서버 설정 갱신
            dlg.Loaded += async (s, ev) =>
            {
                try
                {
                    var settings = await _api.GetSettings();
                    if (settings != null)
                    {
                        if (settings.yolo_conf != null) { sldYolo.Value = (double)settings.yolo_conf * 100; }
                        if (settings.temp_min != null) txtTmin.Text = ((double)settings.temp_min).ToString("F1");
                        if (settings.temp_max != null) txtTmax.Text = ((double)settings.temp_max).ToString("F1");
                        if (settings.humid_min != null) txtHmin.Text = ((double)settings.humid_min).ToString("F1");
                        if (settings.humid_max != null) txtHmax.Text = ((double)settings.humid_max).ToString("F1");
                    }
                }
                catch { }
            };

            dlg.ShowDialog();
        }

        private void SldConf_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
        { if (lblConfVal != null) { lblConfVal.Text = $"{sldConf.Value / 100.0:F2}"; AppConfig.YoloConf = (float)(sldConf.Value / 100.0); } }

        private void BtnLogout_Click(object s, RoutedEventArgs e) => Logout();

        /// <summary>Flask 전송용: 480px 폭으로 축소 + JPEG 품질 50%</summary>
        private static byte[] MatToSmallJpeg(Mat src)
        {
            try
            {
                using var small = new Mat();
                double scale = 480.0 / Math.Max(src.Width, 1);
                if (scale < 1.0)
                    OpenCvSharp.Cv2.Resize(src, small, new OpenCvSharp.Size(480, (int)(src.Height * scale)));
                else
                    src.CopyTo(small);
                OpenCvSharp.Cv2.ImEncode(".jpg", small, out var buf, new[] { (int)OpenCvSharp.ImwriteFlags.JpegQuality, 50 });
                return buf;
            }
            catch { return CameraService.MatToJpeg(src); }
        }

        // ── 커스텀 타이틀바 ──
        private void TitleBar_MouseDown(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
            {
                if (e.ClickCount == 2)
                    WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                else
                    try { DragMove(); } catch { }
            }
        }
        private void BtnMinimize_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void BtnMaxRestore_Click(object s, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        private void BtnClose_Click(object s, RoutedEventArgs e) => Close();

        // ══════════════════════════════════════
        // 채팅 (관제실 ↔ WPF)
        // ══════════════════════════════════════
        private string _chatLineId = "line-01";
        private string? _chatPendingFilePath = null;

        private async Task InitChatPanel()
        {
            // 라인 정보 표시
            try
            {
                var lines = await _api.GetChatLines();
                if (lines != null && lines.Count > 0)
                {
                    var first = lines[0];
                    _chatLineId = first.id;
                    lblChatLineInfo.Text = $"{first.name} · {first.factory} · {first.model}";
                }
            }
            catch { lblChatLineInfo.Text = "라인 정보 없음"; }

            // 메시지 불러오기
            await RefreshChatMessages();

            // 미읽음 처리
            await _api.MarkChatRead(_chatLineId);
            UpdateChatBadge(0);
        }

        private async Task RefreshChatMessages()
        {
            try
            {
                var msgs = await _api.GetChatMessages(_chatLineId, 100);
                chatMessages.Children.Clear();
                if (msgs == null || msgs.Count == 0)
                {
                    var empty = new System.Windows.Controls.TextBlock
                    {
                        Text = "메시지 없음. 첫 메시지를 보내보세요.",
                        Foreground = new SolidColorBrush(Color.FromRgb(90, 107, 133)),
                        FontSize = 12,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 40, 0, 0)
                    };
                    chatMessages.Children.Add(empty);
                    return;
                }
                foreach (var m in msgs) AppendChatBubble(m);
                chatScroll.ScrollToBottom();
            }
            catch (Exception ex) { Log("WARN", $"채팅 로드 실패: {ex.Message}"); }
        }

        private void AppendChatBubble(ChatMessage m)
        {
            bool fromWeb = m.direction == "web_to_wpf";
            var wrapper = new System.Windows.Controls.StackPanel
            {
                Margin = new Thickness(0, 0, 0, 12),
                HorizontalAlignment = fromWeb ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                MaxWidth = 480
            };
            var bubbleColor = fromWeb
                ? new SolidColorBrush(Color.FromRgb(20, 27, 45))
                : new SolidColorBrush(Color.FromRgb(14, 165, 233));
            var textColor = fromWeb
                ? new SolidColorBrush(Color.FromRgb(232, 238, 247))
                : System.Windows.Media.Brushes.White;

            if (!string.IsNullOrEmpty(m.content))
            {
                var bubble = new System.Windows.Controls.Border
                {
                    Background = bubbleColor,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(31, 42, 64)),
                    BorderThickness = new Thickness(fromWeb ? 1 : 0),
                    CornerRadius = new System.Windows.CornerRadius(12, 12, fromWeb ? 12 : 3, fromWeb ? 3 : 12),
                    Padding = new Thickness(12, 8, 12, 8)
                };
                bubble.Child = new System.Windows.Controls.TextBlock
                {
                    Text = m.content,
                    Foreground = textColor,
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap
                };
                wrapper.Children.Add(bubble);
            }

            // 파일 첨부
            if (!string.IsNullOrEmpty(m.file_name))
            {
                var fileBubble = new System.Windows.Controls.Border
                {
                    Background = bubbleColor,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(31, 42, 64)),
                    BorderThickness = new Thickness(fromWeb ? 1 : 0),
                    CornerRadius = new System.Windows.CornerRadius(8),
                    Padding = new Thickness(10, 6, 10, 6),
                    Margin = new Thickness(0, m.content != null && m.content.Length > 0 ? 4 : 0, 0, 0),
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                var fileText = GetChatDisplayFileName(m.file_name);
                var fileSize = m.file_size.HasValue ? FormatBytes(m.file_size.Value) : "";
                var sp = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                sp.Children.Add(new System.Windows.Controls.TextBlock { Text = "📎 ", FontSize = 13, Foreground = textColor, VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(new System.Windows.Controls.TextBlock { Text = fileText, FontSize = 12, Foreground = textColor, VerticalAlignment = VerticalAlignment.Center });
                if (!string.IsNullOrEmpty(fileSize))
                    sp.Children.Add(new System.Windows.Controls.TextBlock { Text = $"  ({fileSize})", FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)), VerticalAlignment = VerticalAlignment.Center, FontFamily = new System.Windows.Media.FontFamily("Consolas") });
                fileBubble.Child = sp;
                var capturedFile = m.file_name;
                fileBubble.MouseLeftButtonDown += async (s, e) => await DownloadChatFile(capturedFile);
                wrapper.Children.Add(fileBubble);
            }

            // 메타 (시간, 발신자)
            var meta = new System.Windows.Controls.TextBlock
            {
                Text = $"{(fromWeb ? m.sender + " · 관제" : m.sender + " · 현장")}  ·  {m.timestamp}",
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(90, 107, 133)),
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                Margin = new Thickness(4, 4, 4, 0),
                HorizontalAlignment = fromWeb ? HorizontalAlignment.Left : HorizontalAlignment.Right
            };
            wrapper.Children.Add(meta);

            chatMessages.Children.Add(wrapper);
        }

        private static string GetChatDisplayFileName(string fn)
        {
            // 형식: line-01_20260517_211230_abc123_원본.pdf
            var parts = fn.Split('_');
            if (parts.Length >= 5) return string.Join("_", parts.Skip(4));
            return fn;
        }

        private static string FormatBytes(long b)
        {
            if (b < 1024) return $"{b} B";
            if (b < 1048576) return $"{b / 1024.0:F1} KB";
            return $"{b / 1048576.0:F1} MB";
        }

        private async Task DownloadChatFile(string filename)
        {
            try
            {
                var bytes = await _api.DownloadChatFile(filename);
                if (bytes == null) { MessageBox.Show("파일 다운로드 실패"); return; }
                var sfd = new Microsoft.Win32.SaveFileDialog { FileName = GetChatDisplayFileName(filename) };
                if (sfd.ShowDialog() == true)
                {
                    await System.IO.File.WriteAllBytesAsync(sfd.FileName, bytes);
                    Log("INFO", $"채팅 파일 저장: {sfd.FileName}");
                }
            }
            catch (Exception ex) { MessageBox.Show($"실패: {ex.Message}"); }
        }

        private void AttachChatFile_Click(object s, RoutedEventArgs e)
        {
            var ofd = new Microsoft.Win32.OpenFileDialog { Title = "전송할 파일 선택" };
            if (ofd.ShowDialog() == true)
            {
                var info = new System.IO.FileInfo(ofd.FileName);
                if (info.Length > 30 * 1024 * 1024) { MessageBox.Show("30MB 이하 파일만 가능"); return; }
                _chatPendingFilePath = ofd.FileName;
                lblFilePreview.Text = $"{info.Name}  ({FormatBytes(info.Length)})";
                filePreview.Visibility = Visibility.Visible;
            }
        }

        private void CancelChatFile_Click(object s, RoutedEventArgs e)
        {
            _chatPendingFilePath = null;
            filePreview.Visibility = Visibility.Collapsed;
        }

        private void ChatInput_KeyDown(object s, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter && !System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.LeftShift))
            {
                e.Handled = true;
                SendChat_Click(s, e);
            }
        }

        private async void SendChat_Click(object s, RoutedEventArgs e)
        {
            var text = txtChatInput.Text.Trim();
            if (string.IsNullOrEmpty(text) && string.IsNullOrEmpty(_chatPendingFilePath))
                return;

            // 비동기 전송, UI 즉시 반응
            string contentSnapshot = text;
            string? fileSnapshot = _chatPendingFilePath;
            txtChatInput.Text = "";
            _chatPendingFilePath = null;
            filePreview.Visibility = Visibility.Collapsed;

            try
            {
                var (ok, err) = await _api.SendChatMessage(_chatLineId, _operator, contentSnapshot, fileSnapshot);
                if (!ok)
                {
                    MessageBox.Show($"메시지 전송 실패:\n\n{err ?? "원인 미상"}", "전송 실패");
                    txtChatInput.Text = contentSnapshot;
                    _chatPendingFilePath = fileSnapshot;
                    if (!string.IsNullOrEmpty(fileSnapshot))
                    {
                        var info = new System.IO.FileInfo(fileSnapshot);
                        lblFilePreview.Text = $"{info.Name}  ({FormatBytes(info.Length)})";
                        filePreview.Visibility = Visibility.Visible;
                    }
                }
            }
            catch (Exception ex) { MessageBox.Show($"전송 실패: {ex.Message}"); }
        }

        private void UpdateChatBadge(int unread)
        {
            UI(() =>
            {
                if (unread > 0)
                {
                    lblChatBadge.Text = unread > 99 ? "99+" : unread.ToString();
                    chatBadge.Visibility = Visibility.Visible;
                }
                else
                {
                    chatBadge.Visibility = Visibility.Collapsed;
                }
            });
        }

        private async Task RefreshChatBadgeFromServer()
        {
            try
            {
                int unread = await _api.GetChatUnread(_chatLineId);
                UpdateChatBadge(unread);
            }
            catch { }
        }

        private void Logout()
        {
            var records = new List<InspectionRecord>(_inspRecords);
            var sensors = new List<SensorRecord>(_sensorHistory);
            string pdfResult = "", excelResult = "";

            try { pdfResult = ReportService.GeneratePdf(_operator, _logs, records, sensors, _startTime); }
            catch (Exception ex) { pdfResult = $"PDF 실패:\n{ex.GetType().Name}: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}"; }

            try { excelResult = ReportService.GenerateExcel(_operator, _logs, records, sensors, _startTime); }
            catch (Exception ex) { excelResult = $"Excel 실패: {ex.Message}"; }

            MessageBox.Show($"{pdfResult}\n\n{excelResult}", "리포트");
            DialogResult = true; Close();
        }

        private void Window_Closing(object s, System.ComponentModel.CancelEventArgs e)
        { _serial.Dispose(); _api.Dispose(); _camera.Dispose(); _yolo.Dispose(); _ocr.Dispose(); _tcpServer.Dispose(); _diagTimer.Stop(); _sessionTimer.Stop(); }


        private void UI(Action a) => Dispatcher.BeginInvoke(a);
    }
}
