using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SocketIOClient;
using PCBInspection.Models;

namespace PCBInspection.Services
{
    public class ApiService : IDisposable
    {
        private readonly HttpClient _http;
        private readonly string _baseUrl;
        private SocketIOClient.SocketIO? _socket;
        public bool IsConnected { get; private set; }
        public bool IsSocketConnected => _socket?.Connected ?? false;

        // ── Flask → WPF 이벤트 (웹 대시보드에서 명령) ──
        public event Action? OnRemoteEstop;
        public event Action? OnRemoteStart;
        public event Action? OnRemoteStop;
        public event Action? OnRemoteEstopClear;
        public event Action<string>? OnSettingsChanged;
        public event Action<string>? OnMessage;
        public event Action<ChatMessage>? OnChatMessage;

        public ApiService()
        {
            _baseUrl = AppConfig.FlaskUrl;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        }

        // ══════════════════════════════════════
        //  Socket.IO 연결 (양방향)
        // ══════════════════════════════════════
        public async Task ConnectSocket()
        {
            try
            {
                _socket = new SocketIOClient.SocketIO(_baseUrl, new SocketIOOptions
                {
                    Transport = SocketIOClient.Transport.TransportProtocol.WebSocket,
                    Reconnection = true,
                    ReconnectionAttempts = -1,
                    ReconnectionDelay = 3000
                });

                _socket.OnConnected += (s, e) =>
                {
                    IsConnected = true;
                    OnMessage?.Invoke("Socket.IO 연결됨");
                };

                _socket.OnDisconnected += (s, e) =>
                {
                    IsConnected = false;
                    OnMessage?.Invoke("Socket.IO 연결 끊김");
                };

                _socket.OnReconnectAttempt += (s, e) =>
                {
                    OnMessage?.Invoke("Socket.IO 재연결 시도...");
                };

                // Flask → WPF: 원격 제어 명령 수신
                _socket.On("remote_command", resp =>
                {
                    var json = resp.GetValue<RemoteCommand>();
                    switch (json?.Action)
                    {
                        case "estop": OnRemoteEstop?.Invoke(); break;
                        case "start": OnRemoteStart?.Invoke(); break;
                        case "stop": OnRemoteStop?.Invoke(); break;
                        case "estop_clear": OnRemoteEstopClear?.Invoke(); break;
                    }
                });

                // Flask → WPF: 설정 변경 알림
                _socket.On("settings_changed", resp =>
                {
                    var raw = resp.GetValue<string>();
                    OnSettingsChanged?.Invoke(raw ?? "");
                });

                // Flask → WPF: 새 채팅 메시지
                _socket.On("chat_message", resp =>
                {
                    try
                    {
                        var msg = resp.GetValue<ChatMessage>();
                        if (msg != null) OnChatMessage?.Invoke(msg);
                    }
                    catch { }
                });

                await _socket.ConnectAsync();
            }
            catch (Exception ex)
            {
                OnMessage?.Invoke($"Socket.IO 연결 실패: {ex.Message}");
            }
        }

        public async Task DisconnectSocket()
        {
            if (_socket != null)
            {
                await _socket.DisconnectAsync();
                _socket.Dispose();
                _socket = null;
            }
        }

        // ══════════════════════════════════════
        //  WPF → Flask: Socket.IO 이벤트 전송
        // ══════════════════════════════════════
        public async Task EmitEvent(string eventName, object data)
        {
            if (_socket?.Connected == true)
            {
                try { await _socket.EmitAsync(eventName, data); }
                catch { }
            }
        }

        // ══════════════════════════════════════
        //  WPF → Flask: HTTP API (기존)
        // ══════════════════════════════════════
        public async Task<bool> SendInspectionResult(string result, string? defectType,
            string frontImage, string backImage, float frontConf, float backConf, string? serialNumber = null)
        {
            try
            {
                IsConnected = await PostJson("/api/inspect", new
                {
                    result, defect_type = defectType,
                    front_image = frontImage, back_image = backImage,
                    front_conf = frontConf, back_conf = backConf,
                    serial_number = serialNumber
                });
                return IsConnected;
            }
            catch { IsConnected = false; return false; }
        }

        public async Task SendSensorData(double temp, double humid, double rpm, double ng, double ok)
        {
            try { IsConnected = await PostJson("/api/sensor", new { temperature = temp, humidity = humid, conveyor_rpm = rpm, box_ng_level = ng, box_ok_level = ok }); }
            catch { IsConnected = false; }
        }

        public async Task SendAlarm(string type, string detail = "")
        { try { await PostJson("/api/alarm", new { type, detail }); } catch { } }

        public async Task SendSystemAction(string action)
        { try { await PostJson("/api/system", new { action, source = "wpf" }); } catch { } }

        public async Task<dynamic?> GetSettings()
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2));
                var r = await _http.GetAsync($"{_baseUrl}/api/settings", cts.Token);
                if (!r.IsSuccessStatusCode) return null;
                var json = await r.Content.ReadAsStringAsync();
                return JsonConvert.DeserializeObject<dynamic>(json);
            }
            catch { return null; }
        }

        public async Task<bool> SaveSettings(float yoloConf, double tMin, double tMax, double hMin, double hMax)
        {
            try
            {
                return await PostJson("/api/settings", new
                {
                    yolo_conf = yoloConf,
                    temp_min = tMin, temp_max = tMax,
                    humid_min = hMin, humid_max = hMax
                });
            }
            catch { return false; }
        }

        public async Task SendCameraFrame(byte[] frontJpeg, byte[] backJpeg)
        {
            try
            {
                if (_socket == null || !_socket.Connected) return;
                var payload = new Dictionary<string, string>();
                if (frontJpeg.Length > 0) payload["front"] = Convert.ToBase64String(frontJpeg);
                if (backJpeg.Length > 0) payload["back"] = Convert.ToBase64String(backJpeg);
                if (payload.Count > 0)
                    await _socket.EmitAsync("camera_frame_upload", payload);
            }
            catch { }
        }

        public async Task<bool> UploadImage(string filename, byte[] jpeg)
        {
            try
            {
                using var c = new MultipartFormDataContent();
                c.Add(new ByteArrayContent(jpeg), "file", filename);
                return (await _http.PostAsync($"{_baseUrl}/api/upload-image", c)).IsSuccessStatusCode;
            }
            catch { return false; }
        }

        // ══════════════════════════════════════
        // 채팅 메시지 (WPF ↔ Web)
        // ══════════════════════════════════════
        public async Task<List<ChatLine>?> GetChatLines()
        {
            try
            {
                var r = await _http.GetAsync($"{_baseUrl}/api/chat/lines");
                if (!r.IsSuccessStatusCode) return null;
                var json = await r.Content.ReadAsStringAsync();
                return JsonConvert.DeserializeObject<List<ChatLine>>(json);
            }
            catch { return null; }
        }

        public async Task<List<ChatMessage>?> GetChatMessages(string lineId, int limit = 100)
        {
            try
            {
                var r = await _http.GetAsync($"{_baseUrl}/api/chat/{lineId}/messages?limit={limit}");
                if (!r.IsSuccessStatusCode) return null;
                var json = await r.Content.ReadAsStringAsync();
                return JsonConvert.DeserializeObject<List<ChatMessage>>(json);
            }
            catch { return null; }
        }

        public async Task<int> GetChatUnread(string lineId)
        {
            try
            {
                var r = await _http.GetAsync($"{_baseUrl}/api/chat/{lineId}/unread?direction=web_to_wpf");
                if (!r.IsSuccessStatusCode) return 0;
                var json = await r.Content.ReadAsStringAsync();
                var d = JsonConvert.DeserializeObject<dynamic>(json);
                return d?.unread != null ? (int)d.unread : 0;
            }
            catch { return 0; }
        }

        public async Task<(bool ok, string? error)> SendChatMessage(string lineId, string sender, string content, string? filePath = null)
        {
            try
            {
                using var c = new MultipartFormDataContent();
                c.Add(new StringContent("wpf"), "source");
                c.Add(new StringContent(sender), "sender");
                c.Add(new StringContent(content ?? ""), "content");
                if (!string.IsNullOrEmpty(filePath) && System.IO.File.Exists(filePath))
                {
                    var bytes = await System.IO.File.ReadAllBytesAsync(filePath);
                    var fileContent = new ByteArrayContent(bytes);
                    fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                    c.Add(fileContent, "file", System.IO.Path.GetFileName(filePath));
                }
                var r = await _http.PostAsync($"{_baseUrl}/api/chat/{lineId}/send", c);
                if (r.IsSuccessStatusCode) return (true, null);
                var body = await r.Content.ReadAsStringAsync();
                return (false, $"HTTP {(int)r.StatusCode}: {body}");
            }
            catch (Exception ex) { return (false, $"{ex.GetType().Name}: {ex.Message}"); }
        }

        public async Task MarkChatRead(string lineId)
        {
            try { await PostJson($"/api/chat/{lineId}/read", new { direction = "web_to_wpf" }); } catch { }
        }

        public async Task<byte[]?> DownloadChatFile(string filename)
        {
            try
            {
                var r = await _http.GetAsync($"{_baseUrl}/api/chat/file/{Uri.EscapeDataString(filename)}");
                if (!r.IsSuccessStatusCode) return null;
                return await r.Content.ReadAsByteArrayAsync();
            }
            catch { return null; }
        }

        private async Task<bool> PostJson(string endpoint, object data)
        {
            var r = await _http.PostAsync($"{_baseUrl}{endpoint}",
                new StringContent(JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json"));
            return r.IsSuccessStatusCode;
        }

        public void Dispose()
        {
            _socket?.Dispose();
            _http.Dispose();
        }

        private class RemoteCommand { public string Action { get; set; } = ""; }
    }
}
