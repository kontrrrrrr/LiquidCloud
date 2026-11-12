using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LiquidCloud;

/// <summary>
/// One log for the whole app: %LocalAppData%\LiquidCloud\logs\liquidcloud-YYYYMMDD.log (kept 7 days).
/// Every line says which component wrote it. Tokens, client ids and auth headers are masked before writing.
/// </summary>
static class Log
{
    static readonly object Gate = new();
    static readonly string Dir = Path.Combine(AppEnv.LocalDir, "logs");
    static bool cleaned;

    static readonly Regex Secrets = new(@"(client_id=|oauth_token=|access_token=|refresh_token=|Authorization:\s*\w+\s|OAuth\s|code=)[^&\s""']+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Mask(string s) => Secrets.Replace(s, m => m.Groups[1].Value + "***");

    public static void Info(string component, string message) => Write("INFO ", component, message, null);
    public static void Warn(string component, string message, Exception? ex = null) => Write("WARN ", component, message, ex);
    public static void Error(string component, string message, Exception? ex = null) => Write("ERROR", component, message, ex);

    static void Write(string level, string component, string message, Exception? ex)
    {
        try
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} {level} [{component}] {Mask(message)}";
            if (ex != null) line += $" | {ex.GetType().Name}: {Mask(ex.Message)}" + (level == "ERROR" ? "\n" + Mask(ex.StackTrace ?? "") : "");
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(Path.Combine(Dir, $"liquidcloud-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
                if (!cleaned)
                {
                    cleaned = true;
                    foreach (var f in new DirectoryInfo(Dir).GetFiles("liquidcloud-*.log").Where(f => f.LastWriteTime < DateTime.Now.AddDays(-7)))
                        try { f.Delete(); } catch (IOException) { }
                }
            }
            if (AppEnv.Smoke) Console.WriteLine(line);
        }
        catch (Exception)
        {
            // logging must never take the app down (disk full, folder locked)
        }
    }

    public static string FolderPath => Dir;
}

/// <summary>Runs background work whose result nobody waits for, and makes sure its errors are logged, not lost.</summary>
static class Bg
{
    public static void Run(Task task, string component, string what)
    {
        task.ContinueWith(t =>
        {
            var ex = t.Exception?.GetBaseException();
            if (ex is OperationCanceledException or OfflineException) return;
            Log.Warn(component, $"{what} failed", ex);
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    public static void Run(Func<Task> work, string component, string what) => Run(Task.Run(work), component, what);
}
