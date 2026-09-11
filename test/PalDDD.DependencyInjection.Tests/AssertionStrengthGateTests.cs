using System.Text.RegularExpressions;

namespace PalDDD.DependencyInjection.Tests;

/// <summary>断言强度门禁（MIG-003 下沉，原 .ai/scripts/assertion-strength-check.sh 整体替代）。
/// <para>
/// 背景：Stryker.NET 不支持 TUnit 的 Microsoft.Testing.Platform（stryker-net#3094），变异测试
/// 不可用；本门禁覆盖其最高价值子集——ITM-319 类恒真断言（builder 链式返回 this 后 IsNotNull，
/// 实际零行为覆盖）与零断言测试方法。
/// </para>
/// <para>与原 bash+python 实现的口径差异（方向均为"更强或等价"）：
/// ① 零断言扫描先做原始字符串字面量（"""..."""）净化——python 在原文上匹配，曾把
/// ArchitectureBoundaryTests 负向自证 raw string 样本里的 [Test] 伪签名（BadParameterizedName、
/// BadMultiLineArgumentsName）计入基线；净化后天然排除。
/// ② 花括号配对在净化文本上进行——raw string 内的 { } 不再扰动真实方法体的深度计数。
/// ③ 无 python 进程依赖（原脚本在 python 缺失时模式 2 直接 FAIL）。</para></summary>
public sealed class AssertionStrengthGateTests
{
    /// <summary>弱断言总数基线上限（棘轮：只许下调，下调需评审）。
    /// 2026-09-11 下沉时实测存量 185 + 余量 5。与任务书预估 175（"当前 166"）的偏差来源：
    /// ① IsNotNull 按出现次数计 155（原 bash grep 按行计 150——5 行各含 2 处）；
    /// ② 零断言方法 30（原 python 口径 20）：新增 10 个 DialectProbeTests 编排壳方法
    ///    （断言在共享辅助方法内、壳方法体内零 Assert）——python 的花括号配对在原文上进行，
    ///    SQL raw string 内不平衡花括号使块边界错位而漏检；同时剔除 2 个 raw string 内的
    ///    [Test] 伪签名（BadParameterizedName 等，python 曾误计入）。净化后计数为真实存量。
    /// 新增测试必须使用行为断言。</summary>
    internal const int MaxWeak = 190;

    private static readonly string Root = FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PalDDD.slnx")))
                return directory.FullName;

            directory = directory.Parent!;
        }

        throw new InvalidOperationException("Unable to locate PalDDD.slnx.");
    }

    /// <summary>弱断言总数（IsNotNull 弱断言 + 零断言测试方法）钳制在基线内——防新增，存量按基线递减。
    /// IsNotNull 计数在原文上进行（与原 bash grep 口径一致）。</summary>
    [Test]
    public async Task WeakAssertionTotal_StaysUnderRatchetBaseline()
    {
        var (isNotNullCount, zeroAssertMethods) = ScanTestSources();

        var detail = $"IsNotNull 弱断言 {isNotNullCount} 处 + 零断言方法 {zeroAssertMethods.Count} 个 = " +
                     $"{isNotNullCount + zeroAssertMethods.Count}（基线上限 {MaxWeak}）";
        var overBaseline = isNotNullCount + zeroAssertMethods.Count > MaxWeak;

        await Assert.That(overBaseline).IsFalse();
        await Assert.That(isNotNullCount).IsGreaterThanOrEqualTo(0);
        await Assert.That(zeroAssertMethods.Count).IsGreaterThanOrEqualTo(0);
        Console.WriteLine(detail);
        foreach (var m in zeroAssertMethods.Take(20))
            Console.WriteLine($"  零断言: {m}");
    }

    /// <summary>扫描器负向自证（验证验证者）：注入样本证明两模式检出与 raw string 排除真实生效，
    /// 防止扫描器被改坏后静默变绿（无声 no-op）。样本走内存文本，不触碰真实测试文件。</summary>
    [Test]
    public async Task Scanner_DetectsWeakPatterns_InInjectedSamples()
    {
        const string isNotNullSample = """
            public sealed class Sample
            {
                [Test]
                public async Task Sample_HasAssertion()
                {
                    var builder = new StringBuilder();
                    Assert.That(builder).IsNotNull();
                }
            }
            """;
        const string zeroAssertSample = """
            public sealed class Sample
            {
                [Test]
                public void Fake_ZeroAssert_Method()
                {
                    var x = 42;
                }
            }
            """;
        // 外层 4 引号包 3 引号内层——模拟负向自证样本嵌 raw string 的真实形态
        //（ArchitectureBoundaryTests 同款：[Test] 伪签名写在 raw string 内）
        const string rawStringFalsePositive = """"
            public sealed class Sample
            {
                [Test]
                public async Task Sample_ParsesRawString()
                {
                    const string negativeSample = """
                        [Test]
                        public void Fake_InRawString_NotCounted()
                        {
                        }
                        """;
                    await Assert.That(negativeSample).IsNotEmpty();
                }
            }
            """";

        var (count, methods) = ScanSources(
        [
            ("isNotNullSample.cs", isNotNullSample),
            ("zeroAssertSample.cs", zeroAssertSample),
            ("rawStringFalsePositive.cs", rawStringFalsePositive),
        ]);

        await Assert.That(count).IsEqualTo(1);
        await Assert.That(methods.Count).IsEqualTo(1);
        await Assert.That(methods[0]).Contains("Fake_ZeroAssert_Method");
    }

    // ═══════════════════════════════════════════════════════════════
    // 扫描实现
    // ═══════════════════════════════════════════════════════════════

    /// <summary>扫描 test/**/*.cs（排除 obj/bin 产物与本文件自身——自身体内含注入样本字符串，
    /// 计入即伪命中）。返回 (IsNotNull 计数, 零断言方法全名清单)。</summary>
    private static (int IsNotNullCount, List<string> ZeroAssertMethods) ScanTestSources()
    {
        var rootTestDir = Path.Combine(Root, "test");
        var files = Directory.EnumerateFiles(rootTestDir, "*.cs", SearchOption.AllDirectories)
            .Where(BuildArtifactFilter.IsNotBuildArtifact)
            .Where(f => !f.EndsWith("AssertionStrengthGateTests.cs", StringComparison.Ordinal))
            .Select(f => (Path: f, Text: File.ReadAllText(f)));

        return ScanSources(files);
    }

    /// <summary>对给定源码文件集合执行两模式扫描（负向自证亦走此入口）。</summary>
    internal static (int IsNotNullCount, List<string> ZeroAssertMethods) ScanSources(
        IEnumerable<(string Path, string Text)> files)
    {
        int isNotNullCount = 0;
        var zeroAssert = new List<string>();

        foreach (var (path, text) in files)
        {
            isNotNullCount += Regex.Count(text, @"\.IsNotNull\(\)");

            var sanitized = SanitizeRawStringsAndLineComments(text);
            foreach (var (methodName, block) in ExtractTestMethodBlocks(sanitized))
            {
                if (!s_hasAssertion.IsMatch(block))
                    zeroAssert.Add($"{Path.GetFileName(path)}:{methodName}");
            }
        }

        return (isNotNullCount, zeroAssert);
    }

    /// <summary>块内断言判定（与原 python 同口径）：Assert* 调用、Throws/ThrowsAsync 调用形态、
    /// mock.Verify( 调用任一存在即视为有断言。Throws 用词边界 + 后随 ( 或 &lt;——方法名恰含
    /// Throws（如 AssertThrowsHelper）不再满足（ITM-426 修复口径）。</summary>
    private static readonly Regex s_hasAssertion = new(@"\bAssert\w*[.(]|\bThrows(Async)?\s*[<(]|\.Verify\(", RegexOptions.Compiled);

    /// <summary>[Test] 方法定位（与原 python 同款正则）：[Test] 后跨行至「返回类型 方法名(参数) {」。
    /// 在净化文本上匹配——raw string 与 // 注释行已抹为等长空白，伪 [Test] 天然不可见。</summary>
    private static readonly Regex s_testMethodStart = new(
        @"\[Test\][^{]*?(?:async\s+)?(?:Task|void|ValueTask)\s+(\w+)\s*\([^)]*\)\s*\{",
        RegexOptions.Compiled);

    private static IEnumerable<(string MethodName, string Block)> ExtractTestMethodBlocks(string sanitized)
    {
        foreach (var match in s_testMethodStart.Matches(sanitized).Cast<Match>())
        {
            // 花括号配对（朴素深度计数，在净化文本上——raw string 内花括号不参与），
            // 与原 python 等价；块 = [Test] 行起至闭合 }（尾随辅助方法的断言不计入本块，ITM-426）
            var openBrace = sanitized.IndexOf('{', match.Index);
            if (openBrace < 0)
                continue;
            var depth = 0;
            var i = openBrace;
            var close = -1;
            for (; i < sanitized.Length; i++)
            {
                if (sanitized[i] == '{')
                    depth++;
                else if (sanitized[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        close = i;
                        break;
                    }
                }
            }

            if (close >= 0)
                yield return (match.Groups[1].Value, sanitized[match.Index..(close + 1)]);
        }
    }

    /// <summary>净化：原始字符串字面量内容与整行 // 注释替换为等长空白（保留换行符与偏移）。
    /// raw string 状态机与原 D12a python 口径一致：行尾 """ 进入（且非行首），
    /// 行首 """ 退出。净化保证跨行正则匹配与方法体配对均不受字面量内容扰动。</summary>
    private static string SanitizeRawStringsAndLineComments(string text)
    {
        var lines = text.Split('\n');
        var inRaw = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimEnd('\r').Trim();
            if (inRaw)
            {
                if (trimmed.StartsWith("\"\"\"", StringComparison.Ordinal))
                    inRaw = false;
                lines[i] = BlankPreserveLength(line);
                continue;
            }

            if (trimmed.EndsWith("\"\"\"", StringComparison.Ordinal) &&
                !trimmed.StartsWith("\"\"\"", StringComparison.Ordinal))
            {
                inRaw = true;
                lines[i] = BlankPreserveLength(line);
                continue;
            }

            if (trimmed.StartsWith("//", StringComparison.Ordinal))
                lines[i] = BlankPreserveLength(line);
        }

        return string.Join('\n', lines);
    }

    /// <summary>等长空白化：除换行符（\r\n / \n）外全部替换为空格——净化不破坏任何偏移与行结构。</summary>
    private static string BlankPreserveLength(string line)
    {
        var chars = line.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] != '\r')
                chars[i] = ' ';
        }

        return new string(chars);
    }
}
