namespace PCBInspection
{
    public static class AppConfig
    {
        public static string FlaskUrl = "http://localhost:5000";
        public static string OnnxModelPath = "best.onnx";
        public static float YoloConf = 0.5f;
        public static double TempMin = 15.0;
        public static double TempMax = 35.0;
        public static double HumidMin = 30.0;
        public static double HumidMax = 70.0;
        public static int CamFrontIndex = 0;
        public static int CamBackIndex = 1;
        public static string ImageDir = "inspection_images";
    }
}
