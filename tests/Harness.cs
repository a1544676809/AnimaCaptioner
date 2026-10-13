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

        // 仓库根目录：从输出目录往上找带 tests/golden.json 的那一层。
        // 刻意不写死"往上四层"——输出深度会随 TFM / 平台配置变化
        // （bin\Release\net10.0 与 bin\x64\Release\net10.0 就差一层），
        // 写死的话路径断了只会表现为 parity 找不到 golden.json、然后以退出码 2 结束，
        // 看着像"数据集对不上所以跳过"，而不是"路径算错了"。
        var here = AppContext.BaseDirectory;
        var repo = FindRepoRoot(here) ?? Path.GetFullPath(Path.Combine(here, "..", "..", "..", ".."));

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

            case "agent":
                // 纯逻辑 + 假 host，不联网也不需要词库
                return CoreCheck.Agent.Run();

            case "reasoning":
                // 思考内容不许进译文。核心断言不需要数据集；
                // 给了数据集就额外扫一遍真实 caption 查假阳性。
                return CoreCheck.Reasoning.Run(dataset);

            case "richapi":
                // 路径按这个顺序解析：参数 -> 环境变量 AC_WINUI -> 构建输出 -> NuGet 缓存。
                var winui = Arg(1) ?? ResolveWinUi();
                if (winui is null)
                {
                    Console.WriteLine("找不到 Microsoft.WinUI.dll。请作为参数传入，" +
                                      "或设环境变量 AC_WINUI 指向它。");
                    return 2;
                }
                Console.WriteLine("winui: " + winui);
                return CoreCheck.RichApi.Run(winui);

            case "llm":
                // 临时探针：真实网络 + 当前 settings.json。不属于默认套件。
                return CoreCheck.LlmLive.Run();

            case "region":
                // 临时探针：真实网络。验证"后插的图文 user 消息模型看不看得见"。
                // 参数：探针图片目录（默认 %TEMP%\ac-imgprobe，或 AC_PROBEIMG）。
                return CoreCheck.RegionProbe.Run(Arg(1) ?? null);

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

            case "prompts":
                // 纯逻辑 + 临时文件，不需要数据集或词库
                return CoreCheck.Prompts.Run();

            case "history":
                // 纯逻辑，撤销/重做栈
                return CoreCheck.History.Run();

            case "escape":
                // 词库**可选**：没有也能跑纯逻辑与 ComfyUI 解析器对拍那几节，
                // 有词库时才跑"Anima 索引里全部括号标签"的全量验证。
                return CoreCheck.Escape.Run(vocabDb, seed);

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

            case "search":
                if (!Need(dataset, "AC_DATASET", "训练集目录")) return 2;
                if (!Need(vocabDb, "AC_VOCABDB", "词库文件")) return 2;
                if (!Need(seed, "AC_SEED", "seed.json")) return 2;
                return CoreCheck.Search.Run(dataset!, vocabDb!, seed!);

            case "vocab":
                // 构建器回归。前半段（素材清单 / 分页合并 / 归一化 / 设置落盘）是纯逻辑，
                // **不需要外部数据**，所以不在这里拦——否则这些断言在没有素材的机器上
                // 永远跑不到。后半段要重建词库，缺素材时由 Vocab.Run 自己说明并返回 2。
                // 参数：素材目录 [基准库]。
                return CoreCheck.Vocab.Run(
                    Arg(1) ?? Env("AC_VOCABSRC") ?? "",
                    Arg(2) ?? Env("AC_VOCABDB") ?? "");

            case "headers":
                // 自定义请求头：解析/校验 + 真实发一次请求看服务端收到什么。
                // 起本地假服务端，不联网，clone 下来就能跑。
                return CoreCheck.Headers.Run();

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

                      parity                      与 Python 实现 117 项对拍（只需 golden.json）
                      defaults                    默认路径解析
                      models                      /models 响应解析（不联网）
                      agent                       AI 助手（尺寸 / 提案解析 / 工具循环，不联网）
                      reasoning   [训练集]         思考内容不许进译文（不联网）
                      sections   <训练集>          归类 / 排序 / 颜色 / 结构编辑
                      prose      <训练集> <词库> <seed>
                      edit       <训练集> <词库> <seed>
                      complete   <训练集> <词库> <seed>   输入框候选 / Tab 补全
                      search     <训练集> <词库> <seed>   标签搜索窗口（复合查询 / 工具文本 / 幻觉核对）
                      safety     <训练集>
                      prompts                     提示词库（历史/预设/淘汰/标题派生）
                      history                     撤销 / 重做栈
                      escape     - <词库> [seed]   标签转义（与 ComfyUI 解析器对拍）
                      roundtrip  <训练集>          逐字节往返
                      richapi    [WinUI.dll]      反射查 API 是否存在（不给路径时自动查找）
                      vocab      <素材目录> [基准库]  词库构建：重建并与既有库对拍
                      headers                     自定义请求头：解析校验 + 真实发一次看服务端收到什么
                      llm                         真实网络：/models 能力 + 关思考 A/B

                    数据集与词库在仓库外，也可改用环境变量：
                      AC_DATASET / AC_VOCABDB / AC_SEED / AC_WINUI / AC_VOCABSRC
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

    /// <summary>
    /// 从输出目录往上找含 tests/golden.json 的那一层（仓库根）；找不到返回 null。
    /// 用标记文件而不是固定层数，输出布局变化时不会静默失效。
    /// </summary>
    private static string? FindRepoRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "tests", "golden.json")))
                return dir.FullName;
        }
        return null;
    }

    /// <summary>
    /// 定位 Microsoft.WinUI.dll，供 richapi 做 API 表面反射。
    ///
    /// 顺序：环境变量 AC_WINUI -> 构建输出 -> NuGet 缓存。
    ///
    /// **构建输出优先，且优先级高于版本号。** 应用输出目录里
    /// Microsoft.WinUI.dll 与 Microsoft.Windows.SDK.NET.dll 挨着放，于是
    /// Microsoft.UI.* 那些**被转发**的类型能就地解析；NuGet 缓存里两者在
    /// 不同目录，只能靠 RichApi 里的解析钩子兜住。两个目录的“版本”还不可比：
    /// 缓存是 …\&lt;包名&gt;\&lt;版本&gt;\lib\&lt;tfm&gt;，输出目录却叫 win-x64，
    /// 早期实现按“版本最高”挑，于是输出目录那份（版本解析失败=0.0）永远被跳过。
    /// </summary>
    private static string? ResolveWinUi()
    {
        var env = Environment.GetEnvironmentVariable("AC_WINUI");
        if (!string.IsNullOrWhiteSpace(env))
        {
            if (File.Exists(env)) return env;
            Console.WriteLine("AC_WINUI 指向的文件不存在，改按构建输出/NuGet 缓存查找：" + env);
        }

        // 构建输出优先（排除 obj：里面的中间产物不作数）
        var build = Best(AppContext.BaseDirectory, rank: 2, skipObj: true);
        if (build is null)
        {
            var repo = FindRepoRoot(AppContext.BaseDirectory);
            if (repo is not null) build = Best(repo, rank: 2, skipObj: true);
        }

        // 退回 NuGet 缓存
        return build ?? Best(NuGetRoot(), rank: 1, skipObj: false);
    }

    /// <summary>NuGet 缓存根目录；没设 NUGET_PACKAGES 时退回 ~\.nuget\packages。</summary>
    private static string NuGetRoot()
    {
        var root = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrWhiteSpace(root)) return root;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget", "packages");
    }

    /// <summary>
    /// 在 <paramref name="start"/> 及其子目录里找 Microsoft.WinUI.dll。
    /// rank 高者优先，同级再比版本（版本按父目录名解析，解析不出算 0.0）。
    /// </summary>
    private static string? Best(string start, int rank, bool skipObj)
    {
        if (string.IsNullOrEmpty(start) || !Directory.Exists(start)) return null;

        var skip = new[] { ".git", "node_modules" };
        var bestPath = (string?)null;
        var bestRank = int.MinValue;
        var bestVer = new Version(0, 0);

        var pending = new Stack<string>();
        pending.Push(start);
        for (var visited = 0; pending.Count > 0 && visited < 3000; visited++)
        {
            string dir;
            try { dir = pending.Pop(); } catch { break; }

            string[] subdirs, files;
            try { subdirs = Directory.GetDirectories(dir); files = Directory.GetFiles(dir, "Microsoft.WinUI.dll"); }
            catch { continue; }

            foreach (var f in files)
            {
                var v = VerOf(f);
                if (bestPath is null || rank > bestRank || (rank == bestRank && v > bestVer))
                {
                    bestPath = f;
                    bestRank = rank;
                    bestVer = v;
                }
            }

            foreach (var d in subdirs)
            {
                var name = Path.GetFileName(d);
                if (skip.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                if (skipObj && name.Equals("obj", StringComparison.OrdinalIgnoreCase)) continue;
                pending.Push(d);
            }
        }
        return bestPath;
    }

    /// <summary>
    /// 版本号：路径形如 …\&lt;包名&gt;\&lt;版本&gt;\lib\&lt;tfm&gt;\Microsoft.WinUI.dll。
    /// 从 dll 往上逐级找第一个能解析成版本号的目录名；都不行则 0.0。
    /// </summary>
    private static Version VerOf(string dllPath)
    {
        var dir = Directory.GetParent(dllPath);
        for (var up = 0; dir is not null && up < 4; up++, dir = dir.Parent)
        {
            if (Version.TryParse(dir.Name, out var v)) return v;
        }
        return new Version(0, 0);
    }
}
