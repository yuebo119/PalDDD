// ---------------------------------------------------------------------------
// 依赖漏洞扫描判定（MIG-011b 自 ci.yml 内嵌 Python 迁移，逻辑等价）。
// v54 真门禁——v53 版为 exit-0 no-op；JSON 解析有漏洞即 exit 1，
// 替代 NU19xx 压制下人工审计。
//
// 输入：dotnet list package --vulnerable --include-transitive --format json 的输出文件
// 用法：dotnet run scripts/vuln-scan.cs <vuln.json 路径>
// 退出码：0=无已知漏洞；1=存在已知漏洞；2=参数缺失。
//         JSON 损坏/文件不可读 → 未捕获异常非零退出（与原 Python traceback 语义一致）。
//
// 遍历结构：projects[] → frameworks[] → topLevelPackages + transitivePackages →
//           vulnerabilities 非空 → 记录 "id@版本 严重度列表"。
// 说明：file-based app（dotnet run 直接运行），零 package 依赖——System.Text.Json
// 为框架内置，不写 #:package（NU1510 警告即错误）。
// ---------------------------------------------------------------------------
using System.Text;
using System.Text.Json;

// Windows 控制台默认编码非 UTF-8，中文输出会被改写——显式对齐 Python 3 默认 UTF-8
Console.OutputEncoding = Encoding.UTF8;

if (args.Length < 1)
{
    Console.Error.WriteLine("用法: vuln-scan.cs <vuln.json 路径>");
    return 2;
}

using var document = JsonDocument.Parse(File.OpenRead(args[0]));

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

// Justification: CA1303 要求 UI 文案走资源表本地化；本脚本输出是 GitHub Actions
// 控制台消息（::error 注解/扫描结论），固定中文非用户可配文案，不适用本地化。
#pragma warning disable CA1303
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
#pragma warning restore CA1303
return 0;

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
