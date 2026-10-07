using AnimaCaptioner.Core;
using Microsoft.UI.Xaml;

namespace AnimaCaptioner;

public partial class App : Application
{
    /// <summary>供设置对话框等取窗口句柄（文件选择器需要）。</summary>
    public static MainWindow? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();

        // WinUI 起不来时不会有任何可见输出（unpackaged 也不写事件日志），
        // 所以三类未处理异常全部落盘，否则失败时无从查起。
        UnhandledException += (_, e) => Log.Write("!! UNHANDLED (XAML): " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("!! UNHANDLED (AppDomain): " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => Log.Write("!! UNOBSERVED TASK: " + e.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var cli = Environment.GetCommandLineArgs();

        // 对拍模式：不起窗口，跑完即退。WinExe 没有控制台，
        // 所以结果既写文件也（若能附加到父控制台）打到 stdout。
        if (HasFlag(cli, "--parity"))
        {
            var golden = ArgValue(cli, "--parity") ?? "";
            var outPath = ArgValue(cli, "--out");
            Log.Write("=== parity mode golden=" + golden + " ===");

            AttachConsole(-1);   // 从终端启动时争取一个 stdout
            var code = Parity.Run(golden, outPath ?? "parity-result.txt");
            Log.Write("=== parity exit=" + code + " ===");
            Environment.Exit(code);
            return;
        }

        Log.Write("=== start pid=" + Environment.ProcessId
                  + " base=" + AppContext.BaseDirectory
                  + " os=" + Environment.OSVersion.Version + " ===");
        MainWindow = new MainWindow();
        MainWindow.Activate();

        // --settings：启动后直接打开设置对话框。
        // 加这个开关是因为菜单在自动化下不好点（点了只出 tooltip，菜单不展开），
        // 而设置页里有取色器这种东西，改完必须真看一眼才敢说没问题。
        if (HasFlag(cli, "--settings"))
            MainWindow.OpenSettingsForTest();

        if (HasFlag(cli, "--help"))
            MainWindow.OpenHelpForTest();

        if (HasFlag(cli, "--tagsearch"))
            MainWindow.OpenTagSearchForTest();
    }

    private static bool HasFlag(string[] cli, string flag) =>
        ArgValue(cli, flag) is not null;

    /// <summary>取 "--flag value" 或 "--flag=value" 的值。</summary>
    private static string? ArgValue(string[] cli, string flag)
    {
        for (var i = 0; i < cli.Length; i++)
        {
            if (cli[i] == flag) return i + 1 < cli.Length ? cli[i + 1] : "";
            if (cli[i].StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase))
                return cli[i][(flag.Length + 1)..];
        }
        return null;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);
}
