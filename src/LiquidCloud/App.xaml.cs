using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace LiquidCloud;

public partial class App : Application
{
    public App()
    {
        Loc.Apply(AppSettings.Load().Language);
        Log.Info("App", $"Start {typeof(App).Assembly.GetName().Version} offline={AppEnv.Offline} smoke={AppEnv.Smoke}");

        DispatcherUnhandledException += (_, e) =>
        {
            Report(e.Exception, "UI");
            e.Handled = true;
            if (AppEnv.Smoke) { Environment.Exit(3); return; }
            if (MainWindow == null || !MainWindow.IsVisible) Shutdown(); // failed while starting
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception, "Domain");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // a background task failed and nobody was waiting for it: keep running, but write it down
            Log.Warn("Task", "Unobserved background error", e.Exception?.GetBaseException());
            e.SetObserved();
        };
    }

    static void Report(Exception? ex, string where)
    {
        Log.Error(where, "Unhandled error", ex);
        string path = "";
        try
        {
            Directory.CreateDirectory(AppEnv.LocalDir);
            path = Path.Combine(AppEnv.LocalDir, "crash.log");
            File.AppendAllText(path, $"{DateTime.Now}\n{ex}\n\n");
        }
        catch (IOException) { }
        if (!AppEnv.Smoke)
            MessageBox.Show(Loc.T("LiquidCloud ran into a problem:") + "\n\n" + ex?.Message + (path.Length > 0 ? "\n\n" + Loc.T("Details saved to:") + "\n" + path : ""), "LiquidCloud");
    }
}
