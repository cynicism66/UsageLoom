using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using UsageLoom.Core;

namespace UsageLoom.App;

internal static class Program
{
    public static string Version => typeof(Program).Assembly.GetName().Version?.ToString(3)??"unknown";
    public static readonly string DataPath = ResolveDataPath();
    public static readonly DiagnosticLog Log = new(Path.Combine(DataPath, "logs"));

    private static string ResolveDataPath()
    {
        var preview=Environment.GetCommandLineArgs().Any(a => a is "--smoke-test" or "--demo");
        var isolated=Environment.GetEnvironmentVariable("USAGELOOM_TEST_DATA");
        if(preview&&!string.IsNullOrWhiteSpace(isolated)&&Path.IsPathFullyQualified(isolated))return Path.GetFullPath(isolated);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"UsageLoom",preview?"preview-data":"user-data");
    }

    [STAThread]
    private static void Main(string[] args)
    {
        var preview=args.Any(a=>a is "--smoke-test" or "--demo");
        using var single = new Mutex(true, preview?@"Local\UsageLoom.Preview":@"Local\UsageLoom.Desktop", out var first);
        if (!first)
        {
            Native.PostMessage(Native.FindWindow("UsageLoom.TrayHost", "UsageLoom"), Native.ActivateMessage, 0, 0);
            return;
        }
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("ERROR", "Crash", e.ExceptionObject.ToString() ?? "unknown");
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Write("ERROR", "Task", e.Exception.Message); e.SetObserved(); };
        try
        {
            Log.Write("INFO", "Startup", "正在初始化 WinUI");
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(initialization =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                UsageLoom.Core.L10n.Language=args.Contains("--preview-english")?"en-US":Settings.Load().Language;
                _ = new LoomApp(args);
            });
        }
        catch (Exception ex) { Log.Write("ERROR", "Startup", ex.ToString()); }
        finally { single.ReleaseMutex(); }
    }
}
