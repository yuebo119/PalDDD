// ============================================================================
// gate-lite.cs——G1-G3 简化门禁·CI 降级路径（MIG-012-C，2026-09-11）
// 由 scripts/gate-check.sh（43 行 bash）等价迁移为 C#（dotnet file-based app）。
//
// 用法（在仓库根执行）：dotnet run scripts/gate-lite.cs
// 检查项（三个计数 == 0 判定）：
//   G1 异常 sealed——public.*class.*Exception 的 .cs 中整文件无 sealed/abstract
//      子串、路径不含 Middleware/Extensions 的文件数（grep -r 口径：不排除
//      obj/bin 生成物——原脚本如此，保持）
//   G2 文件头——src/ 下 .cs（排除 obj/bin/SourceGen/Analyzers）首行非空/
//      using /namespace // 注释 开头的文件数
//   G3 文件命名——src/ 下 .cs（仅排除 obj/bin）文件名含 [A-Za-z0-9._-] 之外
//      字符的文件数
// 退出码：0=全绿；1=任一检查非 0。
//
// 迁移说明：
//   1) ITM-172 修复语义双保持——G3 为真实命名检查（非恒过）；末尾显式
//      if 判定（原 `[ FAIL -gt 0 ] && exit 1` 在 FAIL=0 时 AND-list 返回 1，
//      导致"全绿却退出码 1"，CI 降级门禁必挂）。
//   2) G2 首行判定：bash head -1 为字节操作不剥 BOM，本版 StreamReader 检测
//      并剥离 BOM——本仓 .cs 无 BOM（与 git 工作树一致），双跑等价验证过。
// ============================================================================
#pragma warning disable CA1303 // 门禁协议输出为固定控制台文案，无本地化需求——沿 flaky-parse.cs 先例

using System.Text;
using System.Text.RegularExpressions;

// Windows 控制台默认编码非 UTF-8，中文/emoji 会乱码——对齐 bash UTF-8 输出；
// 重定向行尾默认 \r\n（bash 为 \n），统一为 \n 保证双跑逐字节可比
Console.OutputEncoding = Encoding.UTF8;
Console.Out.NewLine = "\n";

var src = "src";   // 与原脚本 SRC="$ROOT/src" 等价（约定仓库根执行）
var pass = 0;
var fail = 0;

Console.WriteLine("═══ 门禁 ═══");

Check("G1 异常sealed", G1Count(src));
Check("G2 文件头", G2Count(src));
Check("G3 文件命名", G3Count(src));

Console.WriteLine($"═══ {pass}/{fail} ═══");
return fail > 0 ? 1 : 0;

// ─── 检查输出：实际值 == 期望 0 则 ✅ 计 PASS，否则 ❌ 计 FAIL ───
void Check(string desc, int actual)
{
    if (actual == 0)
    {
        Console.WriteLine($"  ✅ {desc}: {actual}");
        pass++;
    }
    else
    {
        Console.WriteLine($"  ❌ {desc}: {actual} (期望0)");
        fail++;
    }
}

// ─── G1：异常类未 sealed/abstract（grep -r 含 obj/bin 口径保持）───
// bash 管道：grep -r 'public.*class.*Exception' -l（列出含匹配行的文件）
//   | xargs grep -L 'sealed\|abstract'（整文件无 sealed/abstract 子串）
//   | grep -v 'Middleware\|Extensions'（路径过滤）| wc -l
static int G1Count(string src)
{
    var exceptionRx = new Regex("public.*class.*Exception");   // BRE 单行匹配等价
    var count = 0;
    foreach (var file in EnumerateAll(src))
    {
        string[] lines;
        try { lines = File.ReadAllLines(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
        if (!lines.Any(exceptionRx.IsMatch)) continue;                       // -l：有匹配行才候选
        if (lines.Any(l => l.Contains("sealed") || l.Contains("abstract"))) continue;  // -L：有则剔除
        var path = file.Replace('\\', '/');
        if (path.Contains("Middleware") || path.Contains("Extensions")) continue;      // 路径过滤
        count++;
    }
    return count;
}

// ─── G2：文件头规范（排除 obj/bin/SourceGen/Analyzers）───
// bash case：首行为空 / "using "前缀 / "namespace "前缀 / "//"前缀 → 合规；其余计数
static int G2Count(string src)
{
    var count = 0;
    foreach (var file in EnumerateSrc(src, excludeGenAndAnalyzers: true))
    {
        string first;
        try
        {
            using var reader = File.OpenText(file);   // StreamReader 默认检测 BOM 并剥离
            first = reader.ReadLine() ?? "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
        if (first.Length == 0
            || first.StartsWith("using ", StringComparison.Ordinal)
            || first.StartsWith("namespace ", StringComparison.Ordinal)
            || first.StartsWith("//", StringComparison.Ordinal))
        {
            continue;
        }
        count++;
    }
    return count;
}

// ─── G3：文件名字符集（仅排除 obj/bin——与 G2 排除口径的差异是原脚本行为）───
// bash 模式 *[!A-Za-z0-9._-]*：文件名含集合外任一字符即计数
static int G3Count(string src)
{
    var count = 0;
    foreach (var file in EnumerateSrc(src, excludeGenAndAnalyzers: false))
    {
        var name = Path.GetFileName(file);
        if (!name.All(static c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or '-'))
        {
            count++;
        }
    }
    return count;
}

// ─── 文件枚举 ───
// G1 用全量口径（grep -r，不排除任何目录）；G2/G3 用 find 排除口径
static IEnumerable<string> EnumerateAll(string src) =>
    SafeEnumerate(() => Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories));

static IEnumerable<string> EnumerateSrc(string src, bool excludeGenAndAnalyzers) =>
    SafeEnumerate(() => Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        .Where(p =>
        {
            var n = p.Replace('\\', '/');
            if (n.Contains("/obj/") || n.Contains("/bin/")) return false;
            if (excludeGenAndAnalyzers && (n.Contains("SourceGen") || n.Contains("Analyzers"))) return false;
            return true;
        });

// 枚举中途目录被删（并发 build 清理 obj）时退化为空结果——bash find 同样输出不完整
static IEnumerable<string> SafeEnumerate(Func<IEnumerable<string>> enumerate)
{
    try { return enumerate(); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
}
