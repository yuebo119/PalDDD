// ---------------------------------------------------------------------------
// 依赖漏洞扫描判定（MIG-011b 自 ci.yml 内嵌 Python 迁移，逻辑等价）。
// v54 真门禁——v53 版为 exit-0 no-op；JSON 解析有漏洞即 exit 1，
// 替代 NU19xx 压制下人工审计。
//
// 输入：dotnet list package --vulnerable --include-transitive --format json 的输出文件
// 用法：dotnet run scripts/vuln-scan.cs <vuln.json 路径>
// 退出码：0=无已知漏洞；1=存在已知漏洞；2=参数缺失或输入形状异常（根缺 projects 数组）。
//         JSON 损坏/文件不可读 → 未捕获异常非零退出（与原 Python traceback 语义一致）。
//
// 遍历结构：projects[] → frameworks[] → topLevelPackages + transitivePackages →
//           vulnerabilities 非空 → 记录 "id@版本 严重度列表"。
// 说明：file-based app（dotnet run 直接运行），零 package 依赖——System.Text.Json
// 为框架内置，不写 #:package（NU1510 警告即错误）。
// ---------------------------------------------------------------------------
using System.Text;
using System.Text.Json;

// Justification: CA1303 要求 UI 文案走资源表本地化；本脚本输出是 GitHub Actions
// 控制台消息（::error 注解 / 扫描结论 / SELFTEST 协议行），固定中文非用户可配文案，
// 不适用本地化。2026-09-13 增自测后由局部抑制改为整文件抑制——自测协议行同样属
// 固定文案，且与 secret-scan.cs / verify-ai.cs / xml-guard.cs 先例一致。
#pragma warning disable CA1303

// Windows 控制台默认编码非 UTF-8，中文输出会被改写——显式对齐 Python 3 默认 UTF-8
Console.OutputEncoding = Encoding.UTF8;

// ─── 自测分发（构造合成 JSON 覆盖判定语义，不读真实文件）───
// 2026-09-13 增：本门禁有已证实的空转史（v53 为 exit-0 no-op），而漏洞扫描器的
// 失败形态是静默的（「扫不到」与「没扫」输出相同），故补自证能力。
if (args.Contains("--selftest"))
{
    return SelfTest();
}

if (args.Length < 1)
{
    Console.Error.WriteLine("用法: vuln-scan.cs <vuln.json 路径>");
    return 2;
}

using var document = JsonDocument.Parse(File.OpenRead(args[0]));

// 全仓扫描修复（输入形状假绿）：`projects` 缺失/非数组时（schema 漂移、传错文件、
// 截断但仍可解析的 JSON），ScanVulnerabilities 返回空集，与「扫过且干净」的输出
// 完全相同（头注释已承认本扫描器的失败形态是静默的）。纯函数语义不变（自测已钉住
// 「缺 projects 不崩且无命中」），在调用方对输入形状 fail-closed。
if (!HasProjectsArray(document.RootElement))
{
    Console.Error.WriteLine("vuln.json 根缺少 projects 数组——输入形状异常，扫描未真正执行（fail-closed）");
    return 2;
}

var found = ScanVulnerabilities(document);

if (found.Count > 0)
{
    Console.WriteLine($"::error::{found.Count} 个包存在已知漏洞");
    foreach (var line in found)
    {
        Console.WriteLine($"  {line}");
    }
    return 1;
}

Console.WriteLine("漏洞扫描通过：0 个已知漏洞");
return 0;

// ══════════════ 判定（纯函数，供 --selftest 覆盖）══════════════

// 遍历 projects[] → frameworks[] → topLevelPackages/transitivePackages → 收集命中。
// 命中语义（易错）：vulnerabilities 必须存在、是数组、且非空——三者缺一不算命中。
static List<string> ScanVulnerabilities(JsonDocument document)
{
    var found = new List<string>();
    foreach (var project in ArrayOrEmpty(document.RootElement, "projects"))
    foreach (var framework in ArrayOrEmpty(project, "frameworks"))
    foreach (var section in (string[])["topLevelPackages", "transitivePackages"])
    foreach (var package in ArrayOrEmpty(framework, section))
    {
        // 等价 Python `if pkg.get('vulnerabilities'):`——属性存在且为非空数组才算命中
        if (!package.TryGetProperty("vulnerabilities", out var vulnerabilities)
            || vulnerabilities.ValueKind != JsonValueKind.Array
            || vulnerabilities.GetArrayLength() == 0)
        {
            continue;
        }

        // 等价 Python pkg['id'] 严格索引：缺失直接抛异常非零退出（KeyError 同语义）；
        // resolvedVersion/severity 为宽松读取，缺失回退 "?"（等价 dict.get 默认值）
        var id = package.GetProperty("id").GetString();
        var version = StrOr(package, "resolvedVersion", "?");
        var severities = string.Join(", ", vulnerabilities.EnumerateArray()
            .Select(v => StrOr(v, "severity", "?")));
        found.Add($"{id}@{version} {severities}");
    }
    return found;
}

// 输入形状判定（纯函数，供 --selftest 覆盖）：根为对象且含 projects 数组。
// 调用方据此对「schema 漂移/传错文件/截断仍可解析」fail-closed——纯函数
// ScanVulnerabilities 的语义不变（缺 projects 仍返回空集，其自测已钉住该行为）。
static bool HasProjectsArray(JsonElement root) =>
    root.ValueKind == JsonValueKind.Object
    && root.TryGetProperty("projects", out var projects)
    && projects.ValueKind == JsonValueKind.Array;

// 枚举 JSON 对象上指定名字的数组属性；缺失或非数组时返回空（等价 Python dict.get(name, [])）。
static IEnumerable<JsonElement> ArrayOrEmpty(JsonElement parent, string name) =>
    parent.ValueKind == JsonValueKind.Object
    && parent.TryGetProperty(name, out var value)
    && value.ValueKind == JsonValueKind.Array
        ? value.EnumerateArray()
        : [];

// 读取字符串属性，缺失或非字符串时回退默认值（等价 Python dict.get(name, fallback)）。
static string StrOr(JsonElement parent, string name, string fallback) =>
    parent.TryGetProperty(name, out var value)
    && value.ValueKind == JsonValueKind.String
    && value.GetString() is { } text
        ? text
        : fallback;

// ══════════════ 自测（合成 JSON 覆盖判定语义；不读真实文件、不联网）══════════════

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

    static List<string> Scan(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ScanVulnerabilities(doc);
    }

    // 拼接骨架：projects[ project{ frameworks[ framework{ <section>[ package{…} ] } ] } ] }
    // 尾部需 6 个字符依次闭合：section 数组 → framework 对象 → frameworks 数组 →
    // project 对象 → projects 数组 → 根对象
    const string Proj = """{"projects":[{"frameworks":[""";
    const string Tail = """]}]}]}""";
    const string Top = """{"topLevelPackages":[""";
    const string Trans = """{"transitivePackages":[""";

    // 结构容错
    Case("空 projects 无命中", Scan("""{"projects":[]}""").Count == 0);
    Case("缺 projects 属性不崩且无命中", Scan("{}").Count == 0);
    Case("projects 非数组无命中", Scan("""{"projects":"x"}""").Count == 0);
    Case("frameworks 缺失无命中", Scan("""{"projects":[{}]}""").Count == 0);

    // 输入形状判定（全仓扫描修复的 fail-closed 路径：纯函数仍返回空集，
    // 由调用方据 HasProjectsArray 拒绝把「形状异常」解释成「扫过且干净」）
    Case("形状判定：根对象 + projects 数组 → 可判", HasProjectsArray(JsonElement.Parse("""{"projects":[]}""")));
    Case("形状判定：缺 projects → 拒绝", !HasProjectsArray(JsonElement.Parse("{}")));
    Case("形状判定：projects 非数组 → 拒绝", !HasProjectsArray(JsonElement.Parse("""{"projects":"x"}""")));
    Case("形状判定：根非对象 → 拒绝", !HasProjectsArray(JsonElement.Parse("[]")));
    Case("形状判定：根为字面量 → 拒绝", !HasProjectsArray(JsonElement.Parse("42")));

    // 命中语义（本门禁最易错处：必须「存在 + 数组 + 非空」三者同时成立）
    Case("vulnerabilities 为空数组不算命中",
        Scan(Proj + Top + """{"id":"A","resolvedVersion":"1.0","vulnerabilities":[]}""" + Tail).Count == 0);
    Case("缺 vulnerabilities 属性不算命中",
        Scan(Proj + Top + """{"id":"A"}""" + Tail).Count == 0);
    Case("vulnerabilities 非数组不算命中",
        Scan(Proj + Top + """{"id":"A","vulnerabilities":"none"}""" + Tail).Count == 0);

    // 命中与格式
    Case("单包单漏洞命中且格式为 id@版本 严重度",
        Scan(Proj + Top + """{"id":"Pkg","resolvedVersion":"1.2.3","vulnerabilities":[{"severity":"High"}]}""" + Tail)
            is [{ } one] && one == "Pkg@1.2.3 High");
    Case("transitivePackages 同样被扫描",
        Scan(Proj + Trans + """{"id":"T","resolvedVersion":"2.0","vulnerabilities":[{"severity":"Critical"}]}""" + Tail).Count == 1);
    Case("多漏洞严重度以「, 」连接",
        Scan(Proj + Top + """{"id":"Pkg","resolvedVersion":"1.0","vulnerabilities":[{"severity":"High"},{"severity":"Low"}]}""" + Tail)
            is [{ } m] && m == "Pkg@1.0 High, Low");

    // 宽松回退
    Case("缺 resolvedVersion 回退 ?",
        Scan(Proj + Top + """{"id":"Pkg","vulnerabilities":[{"severity":"Low"}]}""" + Tail)
            is [{ } v] && v == "Pkg@? Low");
    Case("缺 severity 回退 ?",
        Scan(Proj + Top + """{"id":"Pkg","resolvedVersion":"1.0","vulnerabilities":[{}]}""" + Tail)
            is [{ } s] && s == "Pkg@1.0 ?");

    // fail-closed：严格索引与解析失败都必须显式抛错（不得静默当成「无漏洞」）
    var idThrew = false;
    try { _ = Scan(Proj + Top + """{"vulnerabilities":[{"severity":"High"}]}""" + Tail); }
    catch (KeyNotFoundException) { idThrew = true; }
    Case("缺 id 抛异常（KeyError 同语义，不静默放行）", idThrew);

    var parseThrew = false;
    try { _ = Scan("{ 不是 JSON"); }
    catch (System.Text.Json.JsonException) { parseThrew = true; }
    Case("损坏 JSON 抛 JsonException（fail-closed）", parseThrew);

    Console.WriteLine();
    Console.WriteLine($"SELFTEST {passed}/{total} 通过");
    return passed == total ? 0 : 1;
}
