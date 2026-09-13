// ============================================================================
// verify-conventions.cs——Pal.DDD 规范验证（MIG-012-A2，2026-09-11）
// 由 scripts/verify-conventions.sh（176 行）等价迁移为 C#（dotnet file-based app）。
//
// 用法：
//   dotnet run scripts/verify-conventions.cs               # 全部检查（grep + build + test）
//   dotnet run scripts/verify-conventions.cs -- --quick    # 仅 grep 静态检查（秒级）
//   dotnet run scripts/verify-conventions.cs -- --build    # grep + build（不含 test）
//
// 检查项（V5-V7，与原 bash 逐项对应）：
//   V5. TODO / HACK / FIXME / WORKAROUND 扫描（行级正则）
//   V6. dotnet build 零错误零警告（Process 调用 + 输出解析，双语锚定口径）
//   V7. dotnet test 零失败（逐项目执行——MTP 协议：slnx 批量触发 VSTest 握手
//       exit 5，2026-08-16 终验轮 B-3 实测复现；PalDDD.Testing 为支持库非测试项目）
//
// 已下沉判定（MIG-007/008/009，与原 bash 头注释一致）：
//   V1 零反射族 / V2 async void / V3 .Result / V4 .Wait() 由
//   test/PalDDD.Core.Tests/SourceCodeGuardTests.cs 承接（Roslyn 语法树判定），
//   由 V7 的 dotnet test 阶段执行；--quick 模式仅剩 V5 TODO 扫描。
//
// 迁移说明：
//   1) V6 判定复刻"锚定数字边界"：正向 (^|[^0-9])0( 个错误|…) + 负向
//      [1-9][0-9]* (个错误|…)——"10 个错误"包含"0 个错误"子串，纯子串匹配
//      会让坏构建假通过（三十五轮 A2 门禁假绿教训）。
//   2) 原版输出带 ANSI 颜色码；本版不输出颜色码，文本逐行一致（双跑归一
//      颜色码后 diff 验证）。WARN: unknown MODE 行照原版复刻。
//   3) 子进程输出编码：dotnet CLI 重定向输出为 UTF-8，显式指定
//      StandardOutputEncoding 避免 GBK 控制台默认编码把中文摘要读花。
//   4) 仓库根定位同 verify-ai.cs（CWD 向上找 PalDDD.slnx，BaseDirectory 兜底）。
// ============================================================================

// Justification: CA1303 要求 UI 文案走资源表本地化；本脚本输出是 CI 门禁的
// 固定协议行（✅/❌ + 英文关键词被 grep 消费），固定中文非用户可配文案——沿
// vuln-scan.cs / verify-ai.cs 先例整文件抑制。
#pragma warning disable CA1303

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

// Windows 控制台默认编码非 UTF-8，中文/emoji 输出会乱码——对齐 bash echo -e UTF-8
Console.OutputEncoding = Encoding.UTF8;

// Justification: CA1508 断言 `fail == 0` 恒假（--build/full 路径）。经行为验证为**假阳性**：
// 该分支可达且被实际走到——`dotnet run scripts/verify-conventions.cs -- --build` 在干净树
// 上输出「验证通过（--build 模式，跳过 test）」即证明 fail==0 成立。规则在 V8/V9 新增的
// 条件式 fail++ 之后无法正确归约计数器的取值域。非「关闭警告」，是已核实的误报。
#pragma warning disable CA1508

// ─── 自测分发（--selftest 不依赖仓库，置于仓库根定位之前）───
// 2026-09-13 增：此前本脚本无自证能力（gate-audit 矩阵中标 UNVERIFIED），
// V8/V9 判定逻辑抽为纯函数后由本条覆盖。
if (args.Contains("--selftest"))
{
    return SelfTest();
}

// ─── 仓库根定位 ───
var rootDir = FindRepoRoot();
Environment.CurrentDirectory = rootDir;

// ─── 参数解析（照原版：MODE=第一个参数，默认 full；未知值打 WARN）───
// 注：白名单需含带前缀形态——调用方一致传 `--quick`/`--build`（见 .githooks、
// docs/conventions.md、docs/testing.md），而默认值为不带前缀的 "full"。
// 2026-09-13 修：原白名单写 ("full","quick","build")，导致 `--quick` 恒被判 unknown MODE
// 而打印假 WARN（判定分支本身用 "--quick" 比较，行为不受影响，只是噪声）。
var mode = args.Length > 0 ? args[0] : "full";
if (mode is not ("full" or "--quick" or "--build"))
    Console.WriteLine($"WARN: unknown MODE: {mode}");

var fail = 0;

Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine($"  Pal.DDD 规范验证（mode: {mode}）");
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine();

// ─── V5：grep 静态检查（所有模式都执行）───
// V1-V4（零反射族/async void/.Result/.Wait）已下沉 SourceCodeGuardTests（见头注释）
{
    var matches = new List<string>();
    foreach (var f in EnumerateSrcCs("src"))
    {
        var posix = ToPosix(f);
        var lineno = 0;
        foreach (var content in File.ReadLines(f))
        {
            lineno++;
            if (!Regex.IsMatch(content, "TODO|HACK|FIXME|WORKAROUND")) continue;
            // 构造 grep -rn 输出行后按原版三重子串过滤（/obj/、/bin/、:[[:space:]]*//）
            var line = $"{posix}:{lineno}:{content}";
            if (line.Contains("/obj/") || line.Contains("/bin/")) continue;
            if (Regex.IsMatch(line, ":[ \\t\\r\\n\\f\\v]*//")) continue;
            matches.Add(line);
        }
    }
    if (matches.Count > 0)
    {
        Console.WriteLine($"❌ 禁止 TODO/HACK/FIXME 违反");
        foreach (var m in matches.Take(10)) Console.WriteLine(m);
        Console.WriteLine();
        fail = 1;
    }
    else Console.WriteLine($"✅ 禁止 TODO/HACK/FIXME 通过");
}

// ─── V8：.pal/prompts/ 模板结构（2026-09-13 由「人工」约束机械化）───
// 依据实测：9 个模板段数为 5/6/7 不等——7 个为「角色/框架约束/必须遵守/禁止/输出格式」
// （其中 5 个另加「示例」段，为可选），bounded-context 以「项目引用指南」替代「输出格式」，
// task-intake 为验收断言门专用结构。此处只断言**必填段**，不限制可选段与段序。
{
    var promptsDir = Path.Combine(rootDir, "src", "PalDDD.Prompts", ".pal", "prompts");
    // 必填段清单（每项为该模板的最小段集；可选段不入表）
    var requirements = new (string File, string[] Sections)[]
    {
        ("aggregate-root.prompt.md", ["角色", "框架约束", "必须遵守", "禁止", "输出格式"]),
        ("bounded-context.prompt.md", ["角色", "框架约束", "必须遵守", "禁止", "项目引用指南"]),
        ("command-handler.prompt.md", ["角色", "框架约束", "必须遵守", "禁止", "输出格式"]),
        ("domain-event.prompt.md", ["角色", "框架约束", "必须遵守", "禁止", "输出格式"]),
        ("projection-handler.prompt.md", ["角色", "框架约束", "必须遵守", "禁止", "输出格式"]),
        ("query-handler.prompt.md", ["角色", "框架约束", "必须遵守", "禁止", "输出格式"]),
        ("saga-orchestrator.prompt.md", ["角色", "框架约束", "必须遵守", "禁止", "输出格式"]),
        ("task-intake.prompt.md", ["角色", "档位判据", "必填字段", "验收断言", "断言清单的设计约束", "拒绝路径", "与既有体系的衔接"]),
        ("value-object.prompt.md", ["角色", "框架约束", "必须遵守", "禁止", "输出格式"]),
    };
    var problems = new List<string>();
    var known = new HashSet<string>(StringComparer.Ordinal);
    foreach (var fallback in requirements)
    {
        known.Add(fallback.File);
        var path = Path.Combine(promptsDir, fallback.File);
        if (!File.Exists(path)) { problems.Add($"{fallback.File}：文件不存在"); continue; }
        var present = SectionNames(File.ReadLines(path));
        foreach (var section in fallback.Sections)
            if (!present.Contains(section))
                problems.Add($"{fallback.File}：缺必填段「{section}」");
    }

    // 未登记的模板只 WARN 不 FAIL（新增模板须同步本表；避免把「新模板」误判为违规）
    if (Directory.Exists(promptsDir))
    {
        var unregistered = Directory.GetFiles(promptsDir, "*.prompt.md")
            .Select(Path.GetFileName)
            .Where(n => n is not null && !known.Contains(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        foreach (var u in unregistered)
            Console.WriteLine($"WARN V8 未登记的模板（须同步 requirements 表）：{u}");
    }

    if (problems.Count > 0)
    {
        Console.WriteLine($"❌ V8 .pal/prompts/ 模板缺必填段：");
        foreach (var p in problems) Console.WriteLine($"   {p}");
        fail++;
    }
    else Console.WriteLine($"✅ V8 .pal/prompts/ 模板必填段齐全（{requirements.Length} 个已登记）");
}

// ─── V9：文档引用的脚本路径必须存在（MIG 迁移收口已二次回归的类）───
// 只检查**命令形态**引用（`bash X.sh` / `dotnet run X.cs`）——命令是要执行的，断链必炸；
// 历史提及（「下沉自 X.sh」「已删除」等）由 IsHistoricalMention 排除，避免误报。
{
    var docs = new List<string>();
    // 范围：docs（**排除 docs/review/**——历史审计记录是时点产物，其引用反映当时状态，
    // 不追求与当前代码库一致，纳入扫描会产生大量误报并淹没真问题）、.github、根级文档
    foreach (var dir in (string[])["docs", ".github"])
        if (Directory.Exists(dir))
            docs.AddRange(Directory.GetFiles(dir, "*.md", SearchOption.AllDirectories)
                .Where(f => !ToPosix(f).Contains("/review/", StringComparison.Ordinal)));
    foreach (var f in (string[])["README.md", "README.en.md", "AGENTS.md", "CHANGELOG.md"])
        if (File.Exists(f)) docs.Add(f);

    var broken = new List<string>();
    // 命令形态：`bash X.sh` / `dotnet run X.cs`（引号可选）——这两种形态是要被执行的
    var refPatterns = new Regex[]
    {
        new("bash\\s+\"?([A-Za-z0-9_./-]+\\.sh)\"?"),
        new("dotnet\\s+run\\s+\"?([A-Za-z0-9_./-]+\\.cs)\"?"),
    };
    foreach (var doc in docs)
    {
        var posix = ToPosix(doc);
        var lineno = 0;
        foreach (var content in File.ReadLines(doc))
        {
            lineno++;
            if (IsHistoricalMention(content)) continue;
            foreach (var reference in ExtractScriptRefs(content, refPatterns))
            {
                // 只校验本仓路径（.ai/ 为独立 git 仓库，其内容不随主仓分发）
                if (reference.StartsWith(".ai/", StringComparison.Ordinal)) continue;
                if (!File.Exists(Path.Combine(rootDir, reference)))
                    broken.Add($"{posix}:{lineno} 引用不存在：{reference}");
            }
        }
    }

    if (broken.Count > 0)
    {
        Console.WriteLine($"❌ V9 文档引用了不存在的脚本路径（{broken.Count} 处）：");
        foreach (var b in broken) Console.WriteLine($"   {b}");
        Console.WriteLine("   修法：改为现行路径，或若属历史提及则加「下沉自/已删除/迁移自」之一。");
        fail++;
    }
    else Console.WriteLine("✅ V9 文档命令形态引用的脚本路径均存在");
}

// ─── V10：文档内部链接必须可解析（2026-09-13 增）───
// 实测来源：`docs/review/action-items-2026-09-13-v5.md` 引用 `review-2026-09-13-full-v5.md`
// 而该文件不存在——实际文件名是 09-12，但其标题与报告编号均为 2026-09-13（错在文件名）。
// V9 只查命令形态引用的脚本路径，不查文档互链，故该断链此前无守护。
// 精度设计（首版检查器自身的教训）：只查以 `.md` 结尾的目标（排除 `[标注](说明)` 形态，
// 如 `[事实](代码可查)`），且按**所在文件目录**解析相对路径。
{
    var docFiles = new List<string>();
    foreach (var dir in (string[])["docs"])
        if (Directory.Exists(dir))
            docFiles.AddRange(Directory.GetFiles(dir, "*.md", SearchOption.AllDirectories));
    foreach (var f in (string[])["README.md", "README.en.md", "AGENTS.md", "CHANGELOG.md"])
        if (File.Exists(f)) docFiles.Add(f);

    var brokenLinks = new List<string>();
    foreach (var doc in docFiles)
    {
        var dir = Path.GetDirectoryName(doc) ?? ".";
        foreach (var target in ExtractDocLinkTargets(File.ReadLines(doc)))
        {
            if (!File.Exists(Path.Combine(dir, target)))
                brokenLinks.Add($"{ToPosix(doc)} → {target}");
        }
    }

    if (brokenLinks.Count > 0)
    {
        Console.WriteLine($"❌ V10 文档内部链接断链（{brokenLinks.Count} 处）：");
        foreach (var b in brokenLinks) Console.WriteLine($"   {b}");
        Console.WriteLine("   修法：改指向实际文件，或（若文件名本身有误）改名并同步引用处。");
        fail++;
    }
    else Console.WriteLine($"✅ V10 文档内部链接全部可解析（{docFiles.Count} 个文档）");
}

// ─── --quick 模式：仅 grep 检查，跳过 build/test ───
if (mode == "--quick")
{
    Console.WriteLine();
    Console.WriteLine("═══════════════════════════════════════════════════════════════");
    Console.WriteLine(fail == 0
        ? "  ✅ 静态检查通过（--quick 模式，跳过 build/test）"
        : "  ❌ 静态检查未通过");
    Console.WriteLine("═══════════════════════════════════════════════════════════════");
    return fail;
}

// ─── V6：dotnet build（--build 和 full 模式）───
Console.WriteLine();
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine("  构建");
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine();

{
    var (buildRc, buildOutput) = RunCapture("dotnet", "build PalDDD.slnx", rootDir);
    // 双语锚定：数字边界（前面不是数字）的 "0 个错误"/"0 Error(s)" 才算零；
    // 同时任一非零计数行出现即坏（三十五轮 A2：纯子串匹配的门禁假绿）
    var zeroOk = Regex.IsMatch(buildOutput, @"(^|[^0-9])0( 个错误| 个警告| Error| Warning)", RegexOptions.Multiline);
    var anyBad = Regex.IsMatch(buildOutput, @"[1-9][0-9]* (个错误|个警告|Error|Warning)");
    if (buildRc == 0 && zeroOk && !anyBad)
    {
        Console.WriteLine($"✅ dotnet build 零错误零警告");
    }
    else
    {
        Console.WriteLine($"❌ dotnet build 有错误或警告（rc={buildRc}）");
        foreach (var l in buildOutput.Split('\n').Reverse().Take(10).Reverse()) Console.WriteLine(l.TrimEnd('\r'));
        fail = 1;
    }
}

// ─── --build 模式：跳过 test ───
if (mode == "--build")
{
    Console.WriteLine();
    Console.WriteLine("═══════════════════════════════════════════════════════════════");
    Console.WriteLine(fail == 0
        ? "  ✅ 验证通过（--build 模式，跳过 test）"
        : "  ❌ 验证未通过");
    Console.WriteLine("═══════════════════════════════════════════════════════════════");
    return fail;
}

// ─── V7：dotnet test（full 模式；build 已失败则跳过——照原版）───
Console.WriteLine();
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine("  测试");
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine();

if (fail == 0)
{
    // MTP 手写协议：一次只跑一个测试项目；PalDDD.Testing 为支持库非测试项目
    var testFail = 0;
    var csprojs = new List<string>();
    if (Directory.Exists("test"))
        foreach (var f in Directory.EnumerateFiles("test", "*.csproj", SearchOption.AllDirectories))
            if (Path.GetFileName(f) != "PalDDD.Testing.csproj")
                csprojs.Add(ToPosix(f));
    csprojs.Sort(StringComparer.Ordinal);   // find | sort：C locale 字节序 = Ordinal
    foreach (var csproj in csprojs)
    {
        Console.WriteLine($"  测试: {csproj}");
        var (rc, _) = RunCapture("dotnet", $"test {csproj} --no-restore --no-build", rootDir);
        if (rc != 0)
        {
            Console.WriteLine($"❌ {csproj} 测试失败");
            testFail = 1;
        }
    }
    if (testFail == 0) Console.WriteLine($"✅ dotnet test 零失败（全部测试项目逐个执行）");
    else fail = 1;
}

Console.WriteLine();
Console.WriteLine("═══════════════════════════════════════════════════════════════");
if (fail == 0)
{
    Console.WriteLine("  ✅ 规范验证全部通过");
    Console.WriteLine("═══════════════════════════════════════════════════════════════");
    return 0;
}
Console.WriteLine("  ❌ 规范验证未通过，请修复上述问题");
Console.WriteLine("═══════════════════════════════════════════════════════════════");
return 1;

// ══════════════ static 局部函数 ══════════════

// 仓库根定位：CWD 向上找 PalDDD.slnx 为主、BaseDirectory 兜底（file-based app
// 的 BaseDirectory 实测指向 %TEMP%\dotnet\runfile\...，向上不可达仓库根）
static string FindRepoRoot()
{
    foreach (var start in (string?[])[Environment.CurrentDirectory, AppContext.BaseDirectory])
    {
        var dir = start;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "PalDDD.slnx"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
    }
    Console.Error.WriteLine("错误：未定位到仓库根（无 PalDDD.slnx）——请在仓库内执行");
    Environment.Exit(2);
    return ""; // 不可达
}

// src/ 递归 .cs（路径 /obj/、/bin/ 排除在调用侧行级过滤，对齐 grep -v 输出行过滤语义）
static IEnumerable<string> EnumerateSrcCs(string root) =>
    Directory.Exists(root) ? Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories) : [];

// 子进程调用并捕获合并输出（等价 bash $(cmd 2>&1)；dotnet CLI 重定向输出为 UTF-8）
static (int Rc, string Output) RunCapture(string fileName, string arguments, string workingDir)
{
    var psi = new ProcessStartInfo(fileName, arguments)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
        UseShellExecute = false,
        WorkingDirectory = workingDir,
    };
    using var p = Process.Start(psi)!;
    var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
    p.WaitForExit();
    return (p.ExitCode, output);
}

// 路径转 posix 正斜杠（对齐 bash find/grep 输出形式）
static string ToPosix(string path) => path.Replace('\\', '/');

// ══════════════ V8/V9 判定（纯函数，供 --selftest 覆盖）══════════════

// 段名集合：取 `## ` 标题，截断到首个「（」或「：」——容忍标题后缀
// （如「框架约束（编译期强制执行）」「必填字段：验收断言工件」）
static HashSet<string> SectionNames(IEnumerable<string> lines)
{
    var names = new HashSet<string>(StringComparer.Ordinal);
    foreach (var line in lines)
    {
        if (!line.StartsWith("## ", StringComparison.Ordinal)) continue;
        var title = line[3..].Trim();
        var cut = title.IndexOfAny(['（', '：', '(']);
        if (cut > 0) title = title[..cut].Trim();
        if (title.Length > 0) names.Add(title);
    }
    return names;
}

// 历史提及识别：这类行提到旧脚本名是正确的记述，不应判为断链
static bool IsHistoricalMention(string line) =>
    line.Contains("下沉自", StringComparison.Ordinal) ||
    line.Contains("迁移自", StringComparison.Ordinal) ||
    line.Contains("已删除", StringComparison.Ordinal) ||
    line.Contains("已迁移", StringComparison.Ordinal) ||
    line.Contains("随 MIG", StringComparison.Ordinal);

// 抽取命令形态的脚本引用（每个模式取第 1 捕获组）
static IEnumerable<string> ExtractScriptRefs(string line, Regex[] patterns)
{
    foreach (var pattern in patterns)
    {
        var m = pattern.Match(line);
        if (m.Success) yield return m.Groups[1].Value;
    }
}

// V10：抽取 markdown 链接目标中**以 .md 结尾**者（去锚点）。
// 只取 .md 目标是有意为之——项目里有 `[事实](代码可查)` 这类证据标注，
// 它们不是文件引用；限定扩展名即天然排除，无需维护排除清单。
static List<string> ExtractDocLinkTargets(IEnumerable<string> lines)
{
    var rx = new Regex(@"\]\(([^)\s]+\.md)(?:#[^)]*)?\)");
    var targets = new List<string>();
    foreach (var line in lines)
        foreach (Match m in rx.Matches(line))
        {
            var target = m.Groups[1].Value;
            if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
            if (target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) continue;
            targets.Add(target);
        }
    return targets;
}

// ══════════════ 自测 ══════════════

static int SelfTest()
{
    var passed = 0;
    var total = 0;

    void Case(string name, bool ok)
    {
        total++;
        if (ok) passed++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} SELFTEST {name}");
    }

    // V8 段名提取
    var names = SectionNames(["# 值对象", "## 角色", "## 框架约束（编译期强制执行）", "### 子段不入集合", "## 必填字段：验收断言工件"]);
    Case("段名提取含「角色」", names.Contains("角色"));
    Case("段名截断「（」后缀", names.Contains("框架约束") && !names.Contains("框架约束（编译期强制执行）"));
    Case("段名截断「：」后缀", names.Contains("必填字段"));
    Case("三级标题不入集合", !names.Contains("子段不入集合"));
    Case("无标题文本不入集合", SectionNames(["正文", ""]).Count == 0);

    // V9 引用抽取
    var patterns = new Regex[]
    {
        new("bash\\s+\"?([A-Za-z0-9_./-]+\\.sh)\"?"),
        new("dotnet\\s+run\\s+\"?([A-Za-z0-9_./-]+\\.cs)\"?"),
    };
    Case("抽出 bash 命令引用", ExtractScriptRefs("bash scripts/old.sh --quick", patterns).Contains("scripts/old.sh"));
    Case("抽出 dotnet run 引用", ExtractScriptRefs("dotnet run scripts/new.cs -- --quick", patterns).Contains("scripts/new.cs"));
    Case("抽出带引号引用", ExtractScriptRefs("dotnet run \"scripts/a-b.cs\"", patterns).Contains("scripts/a-b.cs"));
    Case("普通散文不误报", !ExtractScriptRefs("这是 scripts 目录的说明", patterns).Any());

    // V9 历史提及排除
    Case("历史提及「下沉自」被排除", IsHistoricalMention("MIG-003 下沉自 assertion-strength-check.sh"));
    Case("历史提及「已迁移」被排除", IsHistoricalMention("已迁移为 .cs"));
    Case("普通命令不被误排除", !IsHistoricalMention("bash scripts/x.sh --quick"));

    // V10 链接抽取（正例）
    Case("V10 抽出 .md 链接", ExtractDocLinkTargets(["[a](b.md)"]).Contains("b.md"));
    Case("V10 抽出相对路径链接", ExtractDocLinkTargets(["[a](../x/b.md)"]).Contains("../x/b.md"));
    Case("V10 去锚点", ExtractDocLinkTargets(["[a](b.md#sec)"]).Contains("b.md"));
    // V10 链接抽取（反例——精度来源）
    Case("V10 不抽 http 链接", ExtractDocLinkTargets(["[a](https://x/y.md)"]).Count == 0);
    Case("V10 不抽 mailto", ExtractDocLinkTargets(["[a](mailto:x@y.z)"]).Count == 0);
    Case("V10 不抽非 .md 目标（如 [事实](代码可查) 标注）", ExtractDocLinkTargets(["[事实](代码可查)"]).Count == 0);
    Case("V10 空行不产目标", ExtractDocLinkTargets(["", "无链接"]).Count == 0);
    Case("V10 一行多链接全抽", ExtractDocLinkTargets(["[a](a.md) 与 [b](b.md)"]).Count == 2);

    Console.WriteLine();
    Console.WriteLine($"SELFTEST {passed}/{total} 通过");
    return passed == total ? 0 : 1;
}
