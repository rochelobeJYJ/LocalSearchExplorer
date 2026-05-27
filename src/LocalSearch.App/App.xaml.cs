using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace LocalSearch.App;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += AppDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        base.OnStartup(e);
    }

    private static void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogUnhandledException(e.Exception);
        System.Windows.MessageBox.Show(
            "예기치 못한 오류가 발생했습니다.\n앱을 계속 사용할 수 있도록 오류를 기록했습니다.",
            "Local File Search Explorer",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void AppDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            LogUnhandledException(exception);
        }
    }

    private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogUnhandledException(e.Exception);
        e.SetObserved();
    }

    private static void LogUnhandledException(Exception exception)
    {
        try
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LocalSearchExplorer",
                "logs");
            Directory.CreateDirectory(logDirectory);

            var builder = new StringBuilder()
                .AppendLine(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"))
                .AppendLine(exception.ToString())
                .AppendLine();

            File.AppendAllText(Path.Combine(logDirectory, "unhandled.log"), builder.ToString(), Encoding.UTF8);
        }
        catch
        {
            // Last-resort handler: logging must never trigger another crash path.
        }
    }
}
