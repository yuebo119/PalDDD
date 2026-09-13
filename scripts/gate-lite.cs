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

// Justification: CA1031 禁止宽泛 catch；本文件的捕获只出现在 --selftest 的临时目录
// 清理路径（清理失败不得翻转自测结论）。限定在自测 finally 内。
#pragma warning disable CA1031

using System.Text;
using System.Text.RegularExpressions;

// Windows 控制台默认编码非 UTF-8，中文/emoji 会乱码——对齐 bash UTF-8 输出；
// 重定向行尾默认 \r\n（bash 为 \n），统一为 \n 保证双跑逐字节可比
Console.OutputEncoding = Encoding.UTF8;
Console.Out.NewLine = "\n";

var src = "src";   // 与原脚本 SRC="$ROOT/src" 等价（约定仓库根执行）
var pass = 0;
var fail = 0;

// 2026-09-13 增：G1-G3 均为「计数 == 0」判定，模式写细一点就会静默漏报违规
// （与本会话早前发现的 encoding-gate E2/E3 范围缺口同类）。三个计数函数都接收
// 目录根，故自测可对着临时目录做真实判定，而非只测纯逻辑。
if (args.Contains("--selftest"))
{
    return SelfTest();
}

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

// ══════════════ 自测（临时目录 + 真实文件，验证 G1/G2/G3 的计数判定与排除口径）══════════════

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

    var tmp = Path.Combine(Path.GetTempPath(), "gate-lite-selftest-" + Guid.NewGuid().ToString("N"));

    // 建文件（自动建目录）
    static void Put(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    try
    {
        // ── G1：异常类未 sealed/abstract（整文件子串语义 + 路径过滤）──
        var g1 = Path.Combine(tmp, "g1");
        Put(g1, "PlainException.cs", "public class PlainException { }\n");
        Put(g1, "SealedException.cs", "public sealed class SealedException { }\n");
        Put(g1, "AbstractException.cs", "public abstract class AbstractException { }\n");
        Put(g1, "Extensions/ExtException.cs", "public class ExtException { }\n");
        // 已知宽口径：sealed 出现在注释里也算「整文件含 sealed」——原 grep -L 语义即如此，
        // 此用例把该行为钉住（改宽/改严都必须显式改测试）
        Put(g1, "CommentSealedException.cs", "// TODO sealed\npublic class CommentSealedException { }\n");
        Case("G1 只计数「未 sealed/abstract 且路径未过滤」的异常类", G1Count(g1) == 1);

        // G1 的另一处已知宽口径：正则 `public.*class.*Exception` 是**子串**匹配，
        // 故类名含 Exception 的普通类（并无继承关系）也会被计为违规。方向是「多报」
        // 而非漏报（对门禁而言是安全方向），且属 MIG-012 要求保持的原 bash 语义，
        // 故此用例显式钉住该行为而非视为缺陷。
        var g1Substring = Path.Combine(tmp, "g1-substring");
        Put(g1Substring, "NotException.cs", "public class NotException { }\n");
        Case("G1 子串语义：类名含 Exception 的普通类亦被计数（多报方向，已知）", G1Count(g1Substring) == 1);

        // ── G2：文件头（排除 obj/bin/SourceGen/Analyzers）──
        var g2 = Path.Combine(tmp, "g2");
        Put(g2, "UsingFirst.cs", "using System;\n");
        Put(g2, "CommentFirst.cs", "// 头部注释\n");
        Put(g2, "NamespaceFirst.cs", "namespace X;\n");
        Put(g2, "EmptyFirst.cs", "\nclass X { }\n");
        Put(g2, "BadFirst.cs", "var x = 1;\n");
        Put(g2, "SourceGen/GenFirst.cs", "var y = 2;\n");
        Put(g2, "Analyzers/AnFirst.cs", "var z = 3;\n");
        Put(g2, "obj/ObjFirst.cs", "var w = 4;\n");
        Put(g2, "bin/BinFirst.cs", "var v = 5;\n");
        Case("G2 只计数首行不合规且未被排除的文件", G2Count(g2) == 1);

        // ── G3：文件名字符集（**仅**排除 obj/bin——与 G2 的排除口径差异是原脚本行为）──
        var g3 = Path.Combine(tmp, "g3");
        Put(g3, "Good_Name-1.cs", "class A { }\n");
        Put(g3, "Bad Name.cs", "class B { }\n");
        Put(g3, "中文名.cs", "class C { }\n");
        Put(g3, "SourceGen/Bad Gen.cs", "class D { }\n");
        Put(g3, "obj/Obj Good.cs", "class E { }\n");
        Case("G3 计数非 [A-Za-z0-9._-] 文件名，且不排除 SourceGen（口径差异）", G3Count(g3) == 3);

        // 负向对照：全部合规时计数为 0（防「恒计数」型假红）
        var clean = Path.Combine(tmp, "clean");
        Put(clean, "Ok.cs", "using System;\npublic sealed class OkException { }\n");
        Case("负向对照：全合规目录三项均为 0",
            G1Count(clean) == 0 && G2Count(clean) == 0 && G3Count(clean) == 0);

        // 不存在目录退化为 0（bash find 输出不完整的等价语义）
        Case("不存在的目录退化为 0", G1Count(Path.Combine(tmp, "missing")) == 0);
    }
    finally
    {
        try { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); } catch { /* 清理失败不翻转结论 */ }
    }

    Console.WriteLine();
    Console.WriteLine($"SELFTEST {passed}/{total} 通过");
    return passed == total ? 0 : 1;
}
