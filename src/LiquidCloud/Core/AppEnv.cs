using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LiquidCloud;

/// <summary>
/// How LiquidCloud was started. Command-line switches:
///   --offline   never touch the network (also used by the automated UI check)
///   --smoke     run the built-in self-check, save screenshots, then exit (CI)
///   --no-welcome skip the intro
///   any audio file paths are queued and played ("Open with LiquidCloud").
/// LIQUIDCLOUD_DATA (environment) moves all saved data to another folder, so tests never touch your real library.
/// </summary>
static class AppEnv
{
    static readonly string[] Args = Environment.GetCommandLineArgs().Skip(1).ToArray();

    public static bool Offline { get; set; } = Has("--offline");
    public static bool Smoke { get; } = Has("--smoke");
    public static bool NoWelcome { get; } = Has("--no-welcome") || Has("--smoke");

    public static IReadOnlyList<string> Files { get; } = Args.Where(a => !a.StartsWith("--") && File.Exists(a)).ToList();

    static bool Has(string flag) => Args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    static readonly string? Override = Environment.GetEnvironmentVariable("LIQUIDCLOUD_DATA");

    /// <summary>Settings, library, themes (roaming).</summary>
    public static string DataDir { get; } = Override ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiquidCloud");

    /// <summary>Caches, logs, tools, web data (this PC only).</summary>
    public static string LocalDir { get; } = Override != null ? Path.Combine(Override, "local") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiquidCloud");

    public static string SmokeOut { get; } = Environment.GetEnvironmentVariable("LIQUIDCLOUD_SMOKE_OUT") ?? Path.Combine(LocalDir, "smoke");

    /// <summary>Thrown instead of making a network request while offline, so every page shows its "no connection" state.</summary>
    public static void EnsureOnline()
    {
        if (Offline) throw new OfflineException();
    }
}

public sealed class OfflineException : Exception
{
    public OfflineException() : base(Loc.T("You're offline. Check your internet connection and try again.")) { }
}
