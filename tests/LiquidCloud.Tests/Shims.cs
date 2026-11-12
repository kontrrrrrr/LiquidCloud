using System.Runtime.CompilerServices;

namespace LiquidCloud
{
    /// <summary>The settings class lives in the app (it needs the window); tests only need its data folder.</summary>
    static class AppSettings
    {
        public static string Dir => AppEnv.DataDir;
    }
}

namespace LiquidCloud.Tests
{
    static class TestSetup
    {
        public static readonly string Root = Path.Combine(Path.GetTempPath(), "liquidcloud-tests-" + Environment.ProcessId);

        /// <summary>Runs before any test: every file the app code writes goes to a temp folder, never to the real profile.</summary>
        [ModuleInitializer]
        internal static void Init()
        {
            Environment.SetEnvironmentVariable("LIQUIDCLOUD_DATA", Root);
            Directory.CreateDirectory(Root);
        }
    }
}
