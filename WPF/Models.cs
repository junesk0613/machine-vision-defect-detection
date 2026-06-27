using System;

namespace PCBInspection.Models
{
    public class InspectionResult
    {
        public int Id { get; set; }
        public string Result { get; set; } = "OK";
        public string? DefectType { get; set; }
        public string? FrontImage { get; set; }
        public string? BackImage { get; set; }
        public float FrontConf { get; set; }
        public float BackConf { get; set; }
        public string? Timestamp { get; set; }
    }

    public class YoloDetection
    {
        public string ClassName { get; set; } = "";
        public float Confidence { get; set; }
        public int X1 { get; set; }
        public int Y1 { get; set; }
        public int X2 { get; set; }
        public int Y2 { get; set; }
    }

    public class SensorData
    {
        public double Temperature { get; set; }
        public double Humidity { get; set; }
        public double ConveyorRpm { get; set; }
        public double BoxNgLevel { get; set; }
        public double BoxOkLevel { get; set; }
    }

    public class EventLog
    {
        public DateTime Time { get; set; }
        public string Category { get; set; } = "";
        public string Detail { get; set; } = "";
    }

    public class InspectionRecord
    {
        public int No { get; set; }
        public DateTime Time { get; set; }
        public string Result { get; set; } = "OK";
        public string DefectType { get; set; } = "-";
        public string SerialNumber { get; set; } = "-";
        public double Temperature { get; set; }
        public double Humidity { get; set; }
        public float FrontConf { get; set; }
        public float BackConf { get; set; }
    }

    public class SensorRecord
    {
        public DateTime Time { get; set; }
        public double Temperature { get; set; }
        public double Humidity { get; set; }
    }

    public class ChatLine
    {
        public string id { get; set; } = "";
        public string name { get; set; } = "";
        public string? factory { get; set; }
        public string? model { get; set; }
    }

    public class ChatMessage
    {
        public int id { get; set; }
        public string line_id { get; set; } = "";
        public string sender { get; set; } = "";
        public string sender_role { get; set; } = "";
        public string direction { get; set; } = "";
        public string content { get; set; } = "";
        public string? file_name { get; set; }
        public long? file_size { get; set; }
        public string timestamp { get; set; } = "";
        public bool read { get; set; }
    }
}
