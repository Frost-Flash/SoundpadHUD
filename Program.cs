using System.Text;
using System.Threading;

namespace SoundpadHUD;

internal static class Program
{
    public static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "crash.log");

    public static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
    }

    [STAThread]
    private static void Main(string[] args)
    {
        try { File.Delete(LogPath); } catch { }

        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            SelfTest.Run();
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (s, e) => Log("UnhandledException: " + e.ExceptionObject);
        Application.ThreadException += (s, e) => Log("ThreadException: " + e.Exception);

        bool createdNew;
        using var mutex = new Mutex(true, "SoundpadHUD_SingleInstance_1", out createdNew);
        if (!createdNew)
        {
            Log("another instance is already running, exiting");
            return;
        }

        Log("starting");
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch { }

            AppConfig cfg = AppConfig.Load();
            Log("config loaded from " + cfg.FilePath);
            bool demo = args.Any(a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase));
            Application.Run(new OverlayForm(cfg, demo));
            Log("message loop ended");
        }
        catch (Exception ex)
        {
            Log("FATAL: " + ex);
        }
    }
}
