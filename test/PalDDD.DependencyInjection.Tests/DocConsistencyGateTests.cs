using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PalDDD.DependencyInjection.Tests;

/// <summary>文档一致性门禁（MIG-002 下沉，原 .ai/scripts/doc-consistency-check.sh 判定项 D1-D12）。
/// <para>
/// 下沉依据（docs/design/script-migration-tasks.md）：bash+python 窗口状态机对本仓库结构的判定
/// 改由 C# 测试执行——反射与编译产物（XML doc 文件）是权威事实源，不再依赖源码文本窗口猜测：
/// ① D11 XML 文档覆盖由编译器生成的 PalDDD.Core.xml 判定（python 需 /// 窗口回看，存在伪阳/伪阴）；
/// ② D12a boundary 方法数由反射计数（天然排除注释行与原始字符串字面量内的 [Test] 伪命中——
/// python 需 """ 状态机模拟；ArchitectureBoundaryTests 的负向自证样本恰好内嵌 3 个 [Test] 字符串）。
/// </para>
/// <para>D7（.ai/README.md 文件地图）不迁移——与 verify-ai-system V9 重复，保留在 bash 薄壳。</para>
/// <para>口径对照：每个测试的注释标注原 D 编号与 bash/python 行为差异；差异方向均为"更强或等价"，
/// 弱化点全部显式登记为 s_knownLegacyGaps 棘轮白名单（只许收紧）。</para></summary>
public sealed class DocConsistencyGateTests
{
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

    private static string RepoPath(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>读文档文本。File.ReadAllText 默认探测 UTF-8 BOM——与 python 的 utf-8-sig 口径一致；
    /// 文件缺失时由 FileNotFoundException 自然失败（测试红），不静默跳过。</summary>
    private static string ReadText(string relative) => File.ReadAllText(RepoPath(relative));

    /// <summary>安全截断（python 的 s[:80] 语义——超出长度取全长而非抛异常）。</summary>
    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    // ═══════════════════════════════════════════════════════════════
    // D1-D10：文档结构完整性（文本/目录断言，与 bash 等价）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>D1：核心文档存在且非空。bash 用 `[ -s ]`（size&gt;0），此处用 FileInfo.Length 同口径；
    /// 原脚本曾只查 -f 致空文件也 PASS（自审计 P3 修复），下沉版继承该修复。</summary>
    [Test]
    public async Task CoreDocuments_ExistAndNotEmpty()
    {
        string[] coreDocs =
        [
            "docs/conventions.md", "docs/architecture.md", "docs/pitfalls.md",
            "docs/testing.md", "docs/development.md"
        ];

        var empty = coreDocs.Where(d => new FileInfo(RepoPath(d)).Length <= 0).ToList();
        await Assert.That(empty).IsEmpty();
    }

    /// <summary>D2：conventions.md 编号章节（## N.）≥13。当前 14 章。</summary>
    [Test]
    public async Task ConventionsMd_HasAtLeast13NumberedChapters()
    {
        var chapters = Regex.Count(ReadText("docs/conventions.md"), @"^## \d+\.", RegexOptions.Multiline);
        await Assert.That(chapters).IsGreaterThanOrEqualTo(13);
    }

    /// <summary>D3：docs/decisions/ 至少 1 个 ADR（继承原脚本"动态上界"修复：不硬编码 ADR 总数）。</summary>
    [Test]
    public async Task AdrDirectory_HasDecisions()
    {
        var adrCount = Directory.GetFiles(RepoPath("docs/decisions"), "*.md").Length;
        await Assert.That(adrCount).IsGreaterThanOrEqualTo(1);
    }

    /// <summary>D4：ADR 编号 001-最大编号连续无缺号（三位数命名）。当前 001-022 连续。</summary>
    [Test]
    public async Task AdrNumbering_IsContiguousFrom001ToMax()
    {
        var files = Directory.GetFiles(RepoPath("docs/decisions"), "*.md")
            .Select(Path.GetFileName)
            .OfType<string>()
            .ToList();

        var numbers = files.Select(f => Regex.Match(f, @"^(\d{3})").Groups[1].Value)
            .Where(s => s.Length > 0)
            .Select(int.Parse)
            .ToList();
        await Assert.That(numbers.Count).IsGreaterThan(0);

        var missing = Enumerable.Range(1, numbers.Max())
            .Where(i => !files.Any(f => f.StartsWith($"{i:D3}-", StringComparison.Ordinal)))
            .Select(i => $"{i:D3}")
            .ToList();
        await Assert.That(missing).IsEmpty();
    }

    /// <summary>D5：README nuget badge 版本 == Directory.Build.props 的 VersionPrefix。
    /// badge URL 形如 nuget-v2.1.0——取 nuget-v 后到下一个 '-' 前的段，与 bash `nuget-v[^-]+` 同口径。</summary>
    [Test]
    public async Task ReadmeNugetBadge_MatchesVersionPrefix()
    {
        var readmeVersion = Regex.Match(ReadText("README.md"), @"nuget-v([^-]+)").Groups[1].Value;
        var propsVersion = Regex.Match(
            ReadText("Directory.Build.props"), @"<VersionPrefix>([^<]+)</VersionPrefix>").Groups[1].Value;

        await Assert.That(readmeVersion.Length > 0).IsTrue();
        await Assert.That(propsVersion.Length > 0).IsTrue();
        await Assert.That(readmeVersion).IsEqualTo(propsVersion);
    }

    /// <summary>D6：architecture.md 有「## 稳定性约束」章节且 "- " 条目 ≥5（当前 7 条）。
    /// 章节边界 = 下一个 "## " 标题（与 awk 状态机同口径）；标题用 Contains 匹配（与 grep 子串口径一致）。</summary>
    [Test]
    public async Task ArchitectureMd_StabilityConstraintsSectionHasAtLeast5Items()
    {
        var lines = ReadText("docs/architecture.md").Split('\n');
        var inSection = false;
        var foundSection = false;
        var items = 0;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (inSection)
                    break; // 下一个二级标题 = 章节结束
                inSection = line.Contains("稳定性约束", StringComparison.Ordinal);
                foundSection |= inSection;
                continue;
            }

            if (inSection && line.StartsWith("- ", StringComparison.Ordinal))
                items++;
        }

        await Assert.That(foundSection).IsTrue();
        await Assert.That(items).IsGreaterThanOrEqualTo(5);
    }

    /// <summary>D8：test/ 下项目命名 = PalDDD.*.Tests；PalDDD.Testing 是基础设施库（例外）。</summary>
    [Test]
    public async Task TestProjectDirs_FollowNamingConvention()
    {
        var bad = Directory.GetDirectories(RepoPath("test"))
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => name != "PalDDD.Testing" && !Regex.IsMatch(name, @"^PalDDD\..+\.Tests$"))
            .ToList();

        await Assert.That(bad).IsEmpty();
    }

    /// <summary>D9：CHANGELOG.md 存在且 ≥20 行（当前 492 行）。</summary>
    [Test]
    public async Task ChangelogMd_ExistsWithAtLeast20Lines()
    {
        await Assert.That(File.Exists(RepoPath("CHANGELOG.md"))).IsTrue();
        var lines = File.ReadAllLines(RepoPath("CHANGELOG.md")).Length;
        await Assert.That(lines).IsGreaterThanOrEqualTo(20);
    }

    /// <summary>D10：无过期 ORM 陈述（849/849、PalORM.slnx）。
    /// 扫描面：docs/ **全部文件**（不限扩展名——对齐 bash grep -R 递归不限后缀口径；
    /// 此前仅 *.md 时 docs/sql/*.sql 等非 Markdown 文件对守卫不可见）+ README.md。
    /// 排除口径与 bash 一致：.ai/review/history/**、.ai/lessons.md、docs/design/** 不扫；
    /// 含迁移叙事关键词（删除/不套用/迁移/from ORM/版本/v1.0）的行跳过——这些是合法的 ORM 出处陈述。</summary>
    [Test]
    public async Task Docs_HaveNoStaleOrmStatements()
    {
        var scanTargets = new List<string>();
        scanTargets.AddRange(Directory.EnumerateFiles(RepoPath("docs"), "*", SearchOption.AllDirectories));
        scanTargets.Add(RepoPath("README.md"));
        // .ai 是独立 git 仓库（主仓 .gitignore 排除）——CI fresh checkout 不存在，本地存在才扫
        // （bash 版 D10 对不存在路径 grep 静默空结果同语义；CI 上 docs 面仍然全量守护）。
        if (Directory.Exists(RepoPath(".ai")))
        {
            scanTargets.AddRange(Directory.EnumerateFiles(RepoPath(".ai"), "*.md"));
            scanTargets.AddRange(Directory.EnumerateFiles(RepoPath(".ai/review"), "*.md"));
        }

        var stale = new Regex(@"849/849|PalORM\.slnx");
        var narrative = new Regex(@"删除|不套用|迁移|from ORM|版本|v1\.0");
        var violations = new List<string>();

        foreach (var path in scanTargets)
        {
            var relative = Path.GetRelativePath(Root, path).Replace('\\', '/');
            if (relative.StartsWith(".ai/review/history/", StringComparison.Ordinal) ||
                relative == ".ai/lessons.md" ||
                relative.StartsWith("docs/design/", StringComparison.Ordinal))
                continue;

            foreach (var (line, index) in File.ReadLines(path).Select((l, i) => (l, i)))
            {
                if (stale.IsMatch(line) && !narrative.IsMatch(line))
                    violations.Add($"{relative}:{index + 1}: {Truncate(line.Trim(), 80)}");
            }
        }

        await Assert.That(violations).IsEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    // D11：公共 API XML 文档覆盖（反射 + 编译器 XML doc 双权威源）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>已知未文档化存量（棘轮白名单，只许清空：补 /// 或改显式属性声明后必须删除条目；
    /// 新增公共成员不得进此清单）。与原 python 口径的差异登记：
    /// <list type="bullet">
    /// <item>record struct 位置属性 ×4（Deleted.Value 等）：编译器合成、无声明行，无法在 record
    /// 位置参数处写 ///——需文档时改显式属性声明。python 行级扫描天然跳过（无源码行）。</item>
    /// <item>IPalIdentity&lt;T&gt;.Value：接口成员声明无 public 修饰符，python 的 is_member 正则
    /// （要求行首 public）从未覆盖接口成员——python 口径盲区，此处显式登记而非静默扩大豁免。</item>
    /// <item>DomainEventEnumerator.Current：表达式体属性（=&gt; ...;），python 正则要求行尾 ( 或 {
    /// 的盲区形态；补 /// 后删除本条。</item>
    /// </list></summary>
    private static readonly string[] s_knownLegacyGaps =
    [
        "P:PalDDD.Core.Deleted.Value",
        "P:PalDDD.Core.DeletedTime.Value",
        "P:PalDDD.Core.UpdateTime.Value",
        "P:PalDDD.Core.RowVersion.Value",
        "P:PalDDD.Core.IPalIdentity`1.Value",
        "P:PalDDD.Core.DomainEventEnumerator.Current",
    ];

    /// <summary>D11：PalDDD.Core 公共 API 的 XML 文档覆盖（阻断门）。
    /// 判定源：反射枚举公共面（权威，天然排除 internal 嵌套/显式接口实现）+ 编译器生成的
    /// PalDDD.Core.xml（权威，/// 存在与否由 Roslyn 输出，无窗口回看伪阳/伪阴）。
    /// 程序集经传递复制（DI → Core）落在本测试输出目录，与 CI 构建顺序天然一致。</summary>
    [Test]
    public async Task CorePublicApi_HasXmlDocCoverage()
    {
        var (assembly, documented) = LoadCoreAssemblyWithXmlDoc();

        var missing = FindUndocumentedMembers(assembly, documented)
            .Where(id => !s_knownLegacyGaps.Contains(id))
            .ToList();

        // 棘轮防腐化：白名单条目若已补 ///（出现在 XML doc 集合中）必须同步删除，防止白名单腐化为死条目
        var staleGapEntries = s_knownLegacyGaps
            .Where(documented.Contains)
            .ToList();
        await Assert.That(staleGapEntries).IsEmpty();

        await Assert.That(missing).IsEmpty();
    }

    /// <summary>D11 负向自证（验证验证者）：从已文档化集合人为移除一个成员，
    /// FindUndocumentedMembers 必须报出它——证明覆盖判定真实执行而非无声 no-op。
    /// 与 ArchitectureBoundaryTests 负向自证样本同风格。</summary>
    [Test]
    public async Task CoverageScanner_DetectsUndocumentedMember_OnInjectedGap()
    {
        var (assembly, documented) = LoadCoreAssemblyWithXmlDoc();

        // 注入样本：从 XML doc 取一个确定有文档的类型条目（T: 前缀必在反射公共面上）
        var victim = documented.First(id => id.StartsWith("T:", StringComparison.Ordinal));
        var withVictimRemoved = new HashSet<string>(documented);
        withVictimRemoved.Remove(victim);

        var missing = FindUndocumentedMembers(assembly, withVictimRemoved).ToList();
        await Assert.That(missing.Contains(victim)).IsTrue();
    }

    private static (System.Reflection.Assembly Core, HashSet<string> Documented) LoadCoreAssemblyWithXmlDoc()
    {
        // 缺失即抛（不静默跳过）：dll/xml 经 DI → Core 传递复制到测试输出目录，
        // 缺失意味着构建配置变更破坏了传递复制——必须显式失败
        var coreDll = Path.Combine(AppContext.BaseDirectory, "PalDDD.Core.dll");
        var coreXml = Path.Combine(AppContext.BaseDirectory, "PalDDD.Core.xml");
        if (!File.Exists(coreDll))
            throw new FileNotFoundException("PalDDD.Core.dll 未随 DI 项目传递复制到测试输出目录", coreDll);
        if (!File.Exists(coreXml))
            throw new FileNotFoundException("PalDDD.Core.xml（GenerateDocumentationFile 产物）未复制到测试输出目录", coreXml);

        var assembly = System.Reflection.Assembly.LoadFrom(coreDll);
        var documented = XDocument.Load(coreXml)
            .Descendants("member")
            .Select(e => (string?)e.Attribute("name"))
            .Where(n => n is not null)
            .Select(n => n!)
            .ToHashSet(StringComparer.Ordinal);
        return (assembly, documented);
    }

    /// <summary>反射枚举公共 API 面，返回未出现在 XML doc 集合中的成员 docId。
    /// 豁免规则（每条对齐原 python 口径或编译器合成事实，详见各注释）：
    /// ① override 成员——契约文档在基类（python 扫描面修正③同口径）；
    /// ② op_* 操作符——python 的 is_member 正则（public … name( / name{）对
    ///    `operator ==(` 形态恒不匹配，原口径即不覆盖；操作符语义由类型级 summary 承载；
    /// ③ Equals(单参=声明类型)——record 合成或 IEquatable 表达式体实现，python 口径均不覆盖/不报；
    /// ④ Deconstruct(全 out/byref 参数)——record 合成解构；
    /// ⑤ 零参构造——`class X : Attribute;` 无体类与隐式默认构造为编译器合成（无源码声明行）；
    ///    带参构造一律严格检查。</summary>
    internal static IEnumerable<string> FindUndocumentedMembers(System.Reflection.Assembly assembly, IReadOnlySet<string> documented)
    {
        var missing = new List<string>();

        foreach (var type in assembly.GetTypes().Where(IsPubliclyVisible))
        {
            if (type.Name.Contains('<'))
                continue; // 编译器生成闭包类型

            var typeId = "T:" + XmlDocName.TypeName(type);
            if (!documented.Contains(typeId))
                missing.Add(typeId);

            foreach (var method in type.GetMethods(PublicDeclared))
            {
                if (method.IsSpecialName &&
                    (method.Name.StartsWith("get_", StringComparison.Ordinal) ||
                     method.Name.StartsWith("set_", StringComparison.Ordinal)))
                    continue; // 属性访问器由属性判定覆盖
                if (method.IsVirtual && method.GetBaseDefinition() != method)
                    continue; // 豁免①：override
                if (method.Name.StartsWith("op_", StringComparison.Ordinal))
                    continue; // 豁免②：操作符
                var parameters = method.GetParameters();
                if (method.Name == "Equals" && parameters.Length == 1 && parameters[0].ParameterType == type)
                    continue; // 豁免③：自类型 Equals
                if (method.Name == "Deconstruct" && parameters.Length > 0 &&
                    parameters.All(p => p.ParameterType.IsByRef || p.IsOut))
                    continue; // 豁免④：解构

                var name = method.IsGenericMethod ? method.Name + "``" + method.GetGenericArguments().Length : method.Name;
                var docId = "M:" + XmlDocName.MemberName(type, name, parameters);
                if (!documented.Contains(docId))
                    missing.Add(docId);
            }

            foreach (var ctor in type.GetConstructors(PublicDeclared))
            {
                if (ctor.GetParameters().Length == 0)
                    continue; // 豁免⑤：零参合成构造
                var docId = "M:" + XmlDocName.MemberName(type, "#ctor", ctor.GetParameters());
                if (!documented.Contains(docId))
                    missing.Add(docId);
            }

            foreach (var property in type.GetProperties(PublicDeclared))
            {
                var accessor = property.GetMethod ?? property.SetMethod;
                if (accessor is null)
                    continue;
                if (accessor.IsVirtual && accessor.GetBaseDefinition() != accessor)
                    continue; // 豁免①：override
                var docId = "P:" + XmlDocName.MemberName(type, property.Name, property.GetIndexParameters());
                if (!documented.Contains(docId))
                    missing.Add(docId);
            }
        }

        return missing;
    }

    private const BindingFlags PublicDeclared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static bool IsPubliclyVisible(Type t)
        => t.IsNested ? t.IsNestedPublic && IsPubliclyVisible(t.DeclaringType!) : t.IsPublic;

    /// <summary>C# XML 文档 ID（docId）生成器：反射 MemberInfo → Roslyn 输出的 member name。
    /// 格式规则（与编译器 XML 输出实测对齐，2026-09-11 探测记录）：
    /// 泛型定义类型带 `arity；构造泛型不带 arity，直接 Name{args}；方法类型参数 ``N（0 基）、
    /// 类型类型参数 `N；嵌套类型用 '.' 连接。转换操作符（op_Implicit/Explicit）带 ~ReturnType
    /// 后缀——本扫描器对 op_* 整体豁免，故生成器不含该分支。</summary>
    private static class XmlDocName
    {
        public static string TypeName(Type t)
        {
            var self = t.IsGenericType && !t.IsGenericTypeDefinition
                ? t.Name[..t.Name.IndexOf('`')] + GenericArgs(t)
                : t.Name;
            return t.IsNested
                ? TypeName(t.DeclaringType!) + "." + self
                : t.Namespace + "." + self;
        }

        public static string MemberName(Type declaringType, string memberName, ParameterInfo[] parameters)
            => TypeName(declaringType) + "." + memberName +
               (parameters.Length > 0 ? "(" + string.Join(",", parameters.Select(p => ParamType(p.ParameterType))) + ")" : "");

        private static string GenericArgs(Type t)
            => "{" + string.Join(",", t.GetGenericArguments().Select(GenericArg)) + "}";

        private static string GenericArg(Type t)
            => t.IsGenericParameter
                ? (t.DeclaringMethod is not null ? "``" : "`") + t.GenericParameterPosition
                : ParamType(t); // 数组/ByRef/指针等非泛型实参形状与参数序列化同规则

        private static string ConstructedType(Type t)
            => t.Namespace + "." + t.Name[..t.Name.IndexOf('`')] + GenericArgs(t);

        private static string ParamType(Type t)
        {
            if (t.IsGenericParameter)
                return (t.DeclaringMethod is not null ? "``" : "`") + t.GenericParameterPosition;
            if (t.IsArray)
                return ParamType(t.GetElementType()!) +
                       (t.GetArrayRank() == 1 ? "[]" : "[" + new string(',', t.GetArrayRank() - 1) + "]");
            if (t.IsByRef)
                return ParamType(t.GetElementType()!) + "@";
            if (t.IsPointer)
                return ParamType(t.GetElementType()!) + "*";
            if (t.IsGenericType)
                return ConstructedType(t);
            return t.FullName!;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // D12：计数自激振荡锚（PD34——修复轮自己的提交增删计数对象，裸数字注定振荡）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>D12a 声称提取正则：与 python 同口径（(?&lt;![0-9.]) 跳过章节号尾巴如 "3.6 测试方法命名"）。</summary>
    private static readonly Regex s_claimPattern = new(@"(?<![0-9.])[0-9]+ ?个?(测试)?方法", RegexOptions.Compiled);

    /// <summary>D12b 违规口径：ConfigureAwait 行含裸数字"N 处 / N+ 处"（每次提交都漂移，锚数字必振荡）。</summary>
    private static readonly Regex s_configureAwaitBareNumber = new(@"[0-9]+\+? ?处", RegexOptions.Compiled);

    /// <summary>D12a：ArchitectureBoundaryTests 的 [Test] 实测数（反射权威计数）必须等于
    /// 文档声称（docs/*.md 顶层 + .ai/README.md 的「N 方法」表述，当前 37）。
    /// 反射天然排除注释行与 raw string 内的 [Test] 字符串（grep 字面计数 40，含 3 处伪命中；
    /// python 需 """ 状态机 + 注释跳过才得到同样的 37）。声称提取零命中 = 锚空转，同样判失败。</summary>
    [Test]
    public async Task BoundaryTestMethodCount_MatchesDocClaims()
    {
        var actual = typeof(ArchitectureBoundaryTests)
            .GetMethods(PublicDeclared)
            .Count(m => m.GetCustomAttributes(inherit: false)
                .Any(a => a.GetType().Name == "TestAttribute"));
        await Assert.That(actual).IsGreaterThan(0);

        var claims = CollectDocClaims();
        await Assert.That(claims.Count).IsGreaterThan(0); // 零声称 = 锚空转，须保留至少一处真值声称

        var mismatches = claims
            .Where(c => c.Number != actual)
            .Select(c => $"{c.File}:{c.Line}: 声称「{c.Text}」≠ 反射实测 {actual}")
            .ToList();
        await Assert.That(mismatches).IsEmpty();
    }

    /// <summary>D12b：文档 ConfigureAwait 表述禁止裸数字"N 处"口径（统一「全层显式（G12 零违规）」）。
    /// CHANGELOG 历史段快照值例外不扫（不可改写历史）。</summary>
    [Test]
    public async Task ConfigureAwaitDocs_HaveNoBareNumberClaims()
    {
        var violations = DocClaimScanFiles()
            .SelectMany(file => File.ReadLines(file.Path)
                .Select((line, index) => (line, index))
                .Where(x => x.line.Contains("ConfigureAwait", StringComparison.Ordinal) &&
                            s_configureAwaitBareNumber.IsMatch(x.line))
                .Select(x => $"{file.Relative}:{x.index + 1}: {Truncate(x.line.Trim(), 80)}"))
            .ToList();

        await Assert.That(violations).IsEmpty();
    }

    /// <summary>D12a 负向自证：声称提取正则对 "38 测试方法"（错误数字）与 "### 3.6 测试方法命名"
    /// （章节号尾巴，须被 lookbehind 排除）的行为锁定——防止正则被改坏后锚静默失效。</summary>
    [Test]
    public async Task ClaimPattern_ExtractsNumbersButSkipsSectionTails()
    {
        var wrong = s_claimPattern.Matches("ArchitectureBoundaryTests 38 测试方法机械守护");
        await Assert.That(wrong.Count).IsEqualTo(1);
        await Assert.That(wrong[0].Value).IsEqualTo("38 测试方法");

        var sectionTail = s_claimPattern.Matches("### 3.6 测试方法命名");
        await Assert.That(sectionTail.Count).IsEqualTo(0);
    }

    private sealed record DocClaim(string File, int Line, string Text, int Number);

    private static List<(string Path, string Relative)> DocClaimScanFiles()
    {
        var files = Directory.GetFiles(RepoPath("docs"), "*.md")
            .Select(p => (p, Path.GetRelativePath(Root, p).Replace('\\', '/')))
            .ToList();
        var aiReadme = RepoPath(".ai/README.md");
        if (File.Exists(aiReadme))
            files.Add((aiReadme, ".ai/README.md"));
        return files;
    }

    private static List<DocClaim> CollectDocClaims() =>
        DocClaimScanFiles()
            .SelectMany(file => File.ReadLines(file.Path)
                .Select((line, index) => (line, index))
                .SelectMany(x => s_claimPattern.Matches(x.line)
                    .Select(m => new DocClaim(
                        file.Relative,
                        x.index + 1,
                        m.Value,
                        int.Parse(Regex.Match(m.Value, @"^[0-9]+").Value)))))
            .ToList();
}
