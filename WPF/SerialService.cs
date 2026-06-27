using System;
using System.IO.Ports;
using System.Text;
using System.Timers;
using PCBInspection.Models;

namespace PCBInspection.Services
{
    public class SerialService : IDisposable
    {
        private SerialPort? _port;
        private readonly System.Timers.Timer _heartbeatTimer;
        private readonly System.Timers.Timer _watchdogTimer;
        private string _buffer = "";
        private int _retryCount = 0;
        private string? _lastSent;
        private const int MAX_RETRY = 3;

        public event Action<SensorData>? OnSensorData;
        public event Action? OnEstop;
        public event Action? OnEstopClear;
        public event Action<string>? OnBoxFull;
        public event Action? OnCommLost;
        public event Action<string>? OnRawMessage;
        public bool IsConnected => _port?.IsOpen ?? false;

        public SerialService()
        {
            _heartbeatTimer = new System.Timers.Timer(1000);
            _heartbeatTimer.Elapsed += (s, e) => Send("HEARTBEAT");
            _watchdogTimer = new System.Timers.Timer(3000);
            _watchdogTimer.Elapsed += (s, e) => { _watchdogTimer.Stop(); OnCommLost?.Invoke(); };
        }

        public bool Connect(string portName, int baudRate = 115200)
        {
            try
            {
                _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One);
                _port.DataReceived += Port_DataReceived;
                _port.Open(); _heartbeatTimer.Start(); _watchdogTimer.Start();
                return true;
            }
            catch { return false; }
        }

        public void Disconnect() { _heartbeatTimer.Stop(); _watchdogTimer.Stop(); if (_port?.IsOpen == true) _port.Close(); }

        public static string CalcChecksum(string payload)
        { byte xor = 0; foreach (byte b in Encoding.ASCII.GetBytes(payload)) xor ^= b; return xor.ToString("X2"); }

        public void Send(string command, string? data = null)
        {
            if (_port?.IsOpen != true) return;
            string payload = data != null ? $"{command},{data}" : command;
            string message = $"<{payload},{CalcChecksum(payload)}>\n";
            _lastSent = message; _retryCount = 0;
            try { _port.Write(message); OnRawMessage?.Invoke($"[TX] {message.Trim()}"); } catch { }
        }

        public void SendResult(string result) => Send("RESULT", result);
        public void SendStart() => Send("START");
        public void SendStop() => Send("STOP");
        public void SendResume() => Send("RESUME");

        private void Port_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            if (_port == null) return;
            _buffer += _port.ReadExisting();
            while (true)
            {
                int start = _buffer.IndexOf('<'), end = _buffer.IndexOf('>');
                if (start < 0 || end < 0 || end <= start) break;
                string raw = _buffer.Substring(start + 1, end - start - 1);
                _buffer = _buffer.Substring(end + 1);
                OnRawMessage?.Invoke($"[RX] <{raw}>");
                ParseMessage(raw);
            }
        }

        private void ParseMessage(string raw)
        {
            if (raw == "ACK") { _retryCount = 0; return; }
            if (raw == "NAK") { if (++_retryCount >= MAX_RETRY) OnCommLost?.Invoke(); else if (_lastSent != null && _port?.IsOpen == true) _port.Write(_lastSent); return; }
            string[] parts = raw.Split(',');
            if (parts.Length < 2) return;
            string checksum = parts[^1], payload = raw[..raw.LastIndexOf(',')];
            if (CalcChecksum(payload) != checksum.ToUpper()) { if (_port?.IsOpen == true) _port.Write("<NAK>\n"); return; }
            if (_port?.IsOpen == true) _port.Write("<ACK>\n");
            _watchdogTimer.Stop(); _watchdogTimer.Start();

            string cmd = parts[0]; string[] data = parts.Length > 2 ? parts[1..^1] : Array.Empty<string>();
            switch (cmd)
            {
                case "SENSOR" when data.Length >= 5:
                    OnSensorData?.Invoke(new SensorData {
                        Temperature = double.TryParse(data[0], out var t) ? t : 0, Humidity = double.TryParse(data[1], out var h) ? h : 0,
                        ConveyorRpm = double.TryParse(data[2], out var r) ? r : 0, BoxNgLevel = double.TryParse(data[3], out var ng) ? ng : 0,
                        BoxOkLevel = double.TryParse(data[4], out var ok) ? ok : 0 }); break;
                case "ESTOP": OnEstop?.Invoke(); break;
                case "ESTOP_CLEAR": OnEstopClear?.Invoke(); break;
                case "BOX_FULL" when data.Length >= 1: OnBoxFull?.Invoke(data[0]); break;
            }
        }

        public static string[] GetAvailablePorts() => SerialPort.GetPortNames();
        public void Dispose() { _heartbeatTimer.Dispose(); _watchdogTimer.Dispose(); _port?.Dispose(); }
    }
}
