using System;
using System.Windows;
using System.Windows.Input;
using PCBInspection.Services;

namespace PCBInspection.Views
{
    public partial class LoginWindow : Window
    {
        private int _failCount = 0;
        private DateTime? _lockUntil = null;
        public string LoggedInUser { get; private set; } = "";
        public bool LoginSuccess { get; private set; } = false;

        public LoginWindow() { InitializeComponent(); }
        private void Window_MouseDown(object s, MouseButtonEventArgs e) { if (e.ChangedButton == MouseButton.Left) DragMove(); }
        private void BtnClose_Click(object s, RoutedEventArgs e) => Close();
        private void TxtPassword_KeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Enter) BtnLogin_Click(s, e); }

        private void BtnLogin_Click(object sender, RoutedEventArgs e)
        {
            if (_lockUntil.HasValue && DateTime.Now < _lockUntil.Value)
            { ShowError($"잠금 중 ({(int)(_lockUntil.Value - DateTime.Now).TotalSeconds}초)"); return; }

            string user = txtUsername.Text.Trim(), pass = txtPassword.Password;
            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
            { ShowError("아이디와 비밀번호를 입력하세요."); return; }

            try
            {
                var (success, role, error) = DbService.Login(user, pass);
                if (success) { LoggedInUser = user; LoginSuccess = true; DialogResult = true; }
                else
                {
                    if (++_failCount >= 5) { _lockUntil = DateTime.Now.AddSeconds(60); ShowError("5회 실패 — 60초 잠금"); _failCount = 0; }
                    else ShowError($"{error} ({_failCount}/5)");
                }
            }
            catch (Exception ex) { ShowError($"오류: {ex.Message}"); }
        }

        private void ShowError(string msg) { lblError.Text = msg; lblError.Visibility = Visibility.Visible; }
    }
}
