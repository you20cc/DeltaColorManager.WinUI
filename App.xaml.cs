using Microsoft.UI.Xaml;

namespace DeltaColorManager;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();

        // 全局异常钩子:XAML 内部异常(0xC000027B)默认被吞掉,写日志才能看到真实原因
        UnhandledException += OnUnhandledException;
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeltaColorManager");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {e.Message}{Environment.NewLine}" +
                $"--- Stack ---{Environment.NewLine}{e.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* 日志失败就失败了 */ }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
