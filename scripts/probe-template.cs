// ============================================================================
// probe-template.cs——探针骨架生成器（MIG-012-B2，2026-09-11）
// 由 .ai/scripts/probe-template.sh 等价迁移（review 系统 probe-first 的基建）。
//
// 用法：dotnet run scripts/probe-template.cs -- <探针名> [core|transactions|messaging|projections]
// 生成 %TEMP%/palddd-probe-<名>/ 最小工程（引用本仓库 DDD 核心包），
// 写 Program.cs 后 dotnet run 即可。成本 ~30 秒，替代每次手搓（~5 分钟）。
// DDD 化说明（沿原注释）：原 ORM 版生成 DataSession<TProvider> 探针（ORM 专属概念），
// DDD 版改为引用 DDD 核心包，用 DbContext/Outbox/Repository 等 DDD API。
//
// 与 bash 版的差异（均为迁移收益，无行为损失）：
//   1) MSYS cygpath 不再需要——bash 在 MSYS 下拿到 /c/... 路径需 cygpath -w 转
//      Windows 形式才能进 csproj 的 ProjectReference；C# 的 DirectoryInfo.FullName
//      天然给出本机路径形态，跨平台免转换；
//   2) 生成目录 = Path.GetTempPath()（bash 版的 /tmp 在 MSYS 下同样映射用户
//      Temp 目录；echo 行打印的路径形态随平台， informational 无消费方）；
//   3) 文件内容（csproj/Program.cs）逐字节一致（含 LF 行尾与 $(NoWarn) 字面量）。
// 退出码：0=生成成功；1=缺探针名或未知模块。
// ============================================================================

using System.Text;

// Justification: CA1303 要求 UI 文案走资源表；本工具输出是控制台固定中文提示行，
// 无本地化需求——沿 sibling-map.cs 先例
#pragma warning disable CA1303

// Windows 控制台默认编码非 UTF-8，中文/注释输出乱码——对齐 bash UTF-8
Console.OutputEncoding = Encoding.UTF8;

// 仓库根发现：向上找含 PalDDD.slnx 的目录（等价 bash _ai_root_find，锚点为 cwd）
var repo = FindRepoRoot();

if (args.Length < 1 || args[0].Length == 0)
{
    Console.Error.WriteLine("用法: probe-template.cs <探针名> [core|transactions|messaging|projections]");
    return 1;
}
var name = args[0];
var module = args.Length >= 2 ? args[1] : "core";

// 模块 → (ProjectReference 行, using 注释)——与 bash case 分支逐字对应。
// Include 值 = 仓库根（本机路径形态，等价 cygpath -w）+ 字面 posix 子路径——
// 与 bash 版逐字节一致（Windows 下呈混合分隔符形态，MSBuild 等价接受）
string RefLine(string project) =>
    $@"    <ProjectReference Include=""{repo}/src/{project}/{project}.csproj"" />";
string refsLine;
string usingComment;
switch (module)
{
    case "core":
        refsLine = RefLine("PalDDD.Core");
        usingComment = "// 探针: PalDDD.Core —— 领域模型/规约/值对象 等";
        break;
    case "transactions":
        refsLine = RefLine("PalDDD.Transactions");
        usingComment = "// 探针: PalDDD.Transactions —— Outbox/Inbox/Saga";
        break;
    case "messaging":
        refsLine = RefLine("PalDDD.Messaging");
        usingComment = "// 探针: PalDDD.Messaging —— 消息分发/集成事件";
        break;
    case "projections":
        refsLine = RefLine("PalDDD.Projections");
        usingComment = "// 探针: PalDDD.Projections —— 投影/检查点";
        break;
    default:
        Console.Error.WriteLine($"未知模块: {module}（core|transactions|messaging|projections）");
        return 1;
}

var dir = Path.Combine(Path.GetTempPath(), $"palddd-probe-{name}");
Directory.CreateDirectory(dir);

// probe.csproj——heredoc 逐行对应（$(NoWarn) 为字面量不插值；LF 行尾对齐 bash）
var csproj = string.Join("\n",
[
    "<Project Sdk=\"Microsoft.NET.Sdk\">",
    "  <PropertyGroup>",
    "    <OutputType>Exe</OutputType>",
    "    <TargetFramework>net11.0</TargetFramework>",
    "    <Nullable>enable</Nullable>",
    "    <ImplicitUsings>enable</ImplicitUsings>",
    "    <EnforceCodeStyleInBuild>false</EnforceCodeStyleInBuild>",
    "    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>",
    "    <AnalysisLevel>none</AnalysisLevel>",
    "    <NoWarn>$(NoWarn);CS1591</NoWarn>",
    "  </PropertyGroup>",
    "  <ItemGroup>",
    refsLine,
    "  </ItemGroup>",
    "</Project>",
]) + "\n";
File.WriteAllText(Path.Combine(dir, "probe.csproj"), csproj);

// Program.cs——已存在则不覆盖（保留用户编辑；bash [ ! -f ] 同款）
var programPath = Path.Combine(dir, "Program.cs");
if (!File.Exists(programPath))
{
    var program = string.Join("\n",
    [
        usingComment,
        "// 断言写在下方，结论打印到 stdout（证实/证伪一行说清）",
        "",
        "internal static class Program",
        "{",
        "    private static void Main()",
        "    {",
        "        // TODO: 探针主体",
        $"        Console.WriteLine(\"probe {name}: TODO\");",
        "    }",
        "}",
    ]) + "\n";
    File.WriteAllText(programPath, program);
}

Console.WriteLine($"探针工程: {dir}");
Console.WriteLine($"编辑 {Path.Combine(dir, "Program.cs")} 后: cd {dir} && dotnet run");
return 0;

static string FindRepoRoot()
{
    var d = new DirectoryInfo(Environment.CurrentDirectory);
    while (d is not null && !File.Exists(Path.Combine(d.FullName, "PalDDD.slnx")))
        d = d.Parent!;
    if (d is null)
    {
        Console.Error.WriteLine("错误：未找到仓库根（向上未发现 PalDDD.slnx）——请在仓库内运行");
        Environment.Exit(2);
    }
    return d.FullName;
}
