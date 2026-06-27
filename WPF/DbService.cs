using System;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json.Linq;

namespace PCBInspection.Services
{
    public static class DbService
    {
        private static readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(5) };

        public static (bool success, string role, string error) Login(string username, string password)
        {
            try
            {
                string json = $"{{\"username\":\"{username}\",\"password\":\"{password}\"}}";
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = _client.PostAsync($"{AppConfig.FlaskUrl}/api/auth", content).Result;
                string body = response.Content.ReadAsStringAsync().Result;

                JObject data;
                try { data = JObject.Parse(body); }
                catch { return (false, "", "서버 응답을 파싱할 수 없습니다."); }

                bool success = data.Value<bool>("success");
                if (success)
                    return (true, data.Value<string>("role") ?? "", "");
                else
                    return (false, "", data.Value<string>("error") ?? "로그인 실패");
            }
            catch (AggregateException)
            {
                return (false, "", "Flask 서버에 연결할 수 없습니다.\n서버가 실행 중인지 확인하세요.");
            }
            catch (Exception ex)
            {
                return (false, "", $"연결 오류: {ex.Message}");
            }
        }
    }
}
