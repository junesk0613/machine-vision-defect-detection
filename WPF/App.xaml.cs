using System;
using System.Windows;
using PCBInspection.Views;

namespace PCBInspection
{
    public partial class App : Application
    {
        private void App_Startup(object sender, StartupEventArgs e)
        {
            DispatcherUnhandledException += (s, args) =>
            {
                MessageBox.Show($"UI 오류:\n{args.Exception.Message}\n\n{args.Exception.InnerException?.Message}\n\n{args.Exception.StackTrace}", "오류");
                args.Handled = true;
            };

            try
            {
                var main = new MainWindow("테스트");
                main.ShowDialog();
                Shutdown();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"시작 오류:\n{ex.Message}\n\n{ex.InnerException?.Message}", "오류");
                Shutdown();
            }
        }
    }
}
