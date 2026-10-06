using System.Text;
using AnimaCaptioner.Core;

// Entry point so the Core logic (the real files, not copies) can be exercised
// and parity-checked against the verified Python implementation.
//
// 数据集和词库都在仓库外，所以路径按这个顺序解析：
//   1. 命令行参数
//   2. 环境变量 AC_DATASET / AC_VOCABDB / AC_SEED
// 缺数据集或词库的模式会自己跳过并说明，不会假装通过。
internal static class Harness
{
    private static int Main(string[] argv)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var mode = argv.Length > 0 ? argv[0] : "parity";

        // 仓库根目录：bin/Release/net8.0 -> tests -> repo
        var here = AppContext.BaseDirectory;
        var repo = Path.GetFullPath(Path.Combine(here, "..", "..", "..", ".."));

        string? Arg(int i) => argv.Length > i && !string.IsNullOrWhiteSpace(argv[i]) ? argv[i] : null;
        string? Env(string n) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v : null;

        var dataset = Arg(1) ?? Env("AC_DATASET");
        var vocabDb = Arg(2) ?? Env("AC_VOCABDB");
        var seed = Arg(3) ?? Env("AC_SEED");

        switch (mode)
        {
            case "defaults":
                return DefaultsProbe.Run();

            case "models":
                // 纯解析，不联网也不需要数据集
                return CoreCheck.Models.Run();

            case "richapi":
                return CoreCheck.RichApi.Run(
                    Arg(1) ?? @"D:\NuGet\Packages\microsoft.windowsappsdk.winui\1.8.260803003\lib\net6.0-windows10.0.17763.0\Microsoft.WinUI.dll");

            case "sections":
                if (!Need(dataset, "AC_DATASET", "训练集目录")) return 2;
                return Sections.Run(dataset!);

            case "roundtrip":
                if (!Need(dataset, "AC_DATASET", "训练集目录")) return 2;
                Console.WriteLine("round-trip target: " + dataset);
                var a = RoundTrip.Run(dataset!);
                var b = RoundTrip.CheckNewFileStyle(dataset!);
                return a == 0 && b == 0 ? 0 : 1;

            case "safety":
                if (!Need(dataset, "AC_DATASET", "训练集目录")) return 2;
                return CoreCheck.Safety.Run(dataset!);

            case "prose":
                if (!Need(dataset, "AC_DATASET", "训练集目录")) return 2;
                if (!Need(vocabDb, "AC_VOCABDB", "词库文件")) return 2;
                if (!Need(seed, "AC_SEED", "seed.json")) return 2;
                return CoreCheck.Prose.Run(dataset!, vocabDb!, seed!);

            case "edit":
                if (!Need(dataset, "AC_DATASET", "训练集目录")) return 2;
                if (!Need(vocabDb, "AC_VOCABDB", "词库文件")) return 2;
                if (!Need(seed, "AC_SEED", "seed.json")) return 2;
                return CoreCheck.Edit.Run(dataset!, vocabDb!, seed!);

            case "complete":
                if (!Need(dataset, "AC_DATASET", "训练集目录")) return 2;
                if (!Need(vocabDb, "AC_VOCABDB", "词库文件")) return 2;
                if (!Need(seed, "AC_SEED", "seed.json")) return 2;
                return CoreCheck.Complete.Run(dataset!, vocabDb!, seed!);

            case "parity":
                // golden.json 随仓库分发，所以这一项 clone 下来即可运行
                var golden = Arg(1) ?? Path.Combine(repo, "tests", "golden.json");
                var outPath = Arg(2) ?? Path.Combine(Path.GetTempPath(), "parity-result.txt");
                if (!File.Exists(golden))
                {
                    Console.WriteLine($"找不到 golden.json：{golden}");
                    return 2;
                }
                Console.WriteLine("golden: " + golden);
                return Parity.Run(golden, outPath);

            default:
                Console.WriteLine("""
                    用法: corecheck <模式> [参数]

                      parity                      与 Python 实现 126 项对拍（只需 golden.json）
                      defaults                    默认路径解析
                      models                      /models 响应解析（不联网）
                      sections   <训练集>          归类 / 排序 / 颜色 / 结构编辑
                      prose      <训练集> <词库> <seed>
                      edit       <训练集> <词库> <seed>
                      complete   <训练集> <词库> <seed>   输入框候选 / Tab 补全
                      safety     <训练集>
                      roundtrip  <训练集>          逐字节往返
                      richapi    <WinUI.dll>      反射查 API 是否存在

                    数据集与词库在仓库外，也可改用环境变量：
                      AC_DATASET / AC_VOCABDB / AC_SEED
                    """);
                return 2;
        }
    }

    private static bool Need(string? v, string env, string what)
    {
        if (v is not null) return true;
        Console.WriteLine($"缺少{what}。请作为参数传入，或设环境变量 {env}。");
        return false;
    }
}
