using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PalDDD.DependencyInjection.Tests;

// ═══════════════════════════════════════════════════════════════
// 技术债守卫测试（MIG-006/010 下沉，承接 .ai/scripts/tech-debt-scan.sh 三项内嵌 Python 判定）
// ═══════════════════════════════════════════════════════════════
// 承接来源（任务清单 docs/design/script-migration-tasks.md）：
//   #8  SuppressMessage 必带 Justification → 本文件 Roslyn AttributeSyntax 语义级判定
//   #13 方言 SQL 守卫对称（冲突安全家族） → SqlTemplates/EventLogSql 的 const 常量结构化读取
//   #14 姊妹实现乐观锁守卫对称 → 姊妹 Store 文件文本模式计数（等价 bash python 口径）
//   #15 Dapper 全局状态自足 → DapperAotInitializer 非注释行赋值断言（PD21，MIG-T5 下沉）
//   #16 MySQL IN-LIMIT 禁令 → JOIN 变体模板 + 消费方分派三点存在性（PD22，MIG-T5 下沉）
//   #17 PG 严格类型防护 → PG 常量 + jsonb CAST + 原生时间参数三点存在性（PD23，MIG-T5 下沉）
//   #18 失败原因截断守卫对称 → Inbox↔Outbox 双管线 Normalize 调用存在性（PD24，MIG-T5 下沉）
//   #19 PG naive 时间函数 → src 全扫禁词 AT TIME ZONE（PD25，MIG-T5 下沉）
//   #20 DbContext 关系型测试覆盖 → 逐 context 枚举 + 已知 gap 活账本（PD26，MIG-T5 下沉）
//
// 与 bash/python 版的判定差异（有意收紧，方向为更严）：
//   #8 bash 只查特性文本内 "Justification" 子串——① 位置参数形态
//      [SuppressMessage("Cat", "ID", "理由")] 会被误报缺失（BCL 构造器第 3 参数就是
//      justification）；② MessageId = "…"Justification…" 的字面量会误放行。
//      Roslyn 版按参数语义判定：命名参数 Justification 非 null，或第 3 个位置参数非 null。
//   #13/#14 与 bash python 同口径（正则 + 家族归并 + 计数比较），存在性断言保留
//      （检查源文件缺失必须 FAIL，不静默空转——元审计脚本#26 教训）。
//   #15-#18 与 bash 同口径（行级子串/正则存在性 + 计数下限），存在性断言保留（同上）。
//   #19 与 bash 同口径（禁词行排除注释行与"修复"说明行）。
//   #20 bash 是 allow 级（缺口列出不阻断）——C# 版收紧为"已知 gap 白名单 == 实测 gap"
//      活账本双向断言：新 DbContext 无关系型用例 → 红；白名单项补齐用例 → 也红
//      （提醒缩账本），防止 allow 项无人再看、账本漂移。
// ═══════════════════════════════════════════════════════════════

public sealed class TechDebtGuardTests
{
    private static readonly string Root = FindRepositoryRoot();

    // ─────────────────────────────────────────────────────────────
    // 通用：仓库根定位 + 构建产物过滤（对齐 ArchitectureBoundaryTests 同款）
    // ─────────────────────────────────────────────────────────────

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PalDDD.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException("Unable to locate PalDDD.slnx.");
    }

    private static bool IsNotBuildArtifact(string path) =>
        !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
        !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}");

    /// <summary>src/ 全部 .cs 文件相对路径（正斜杠归一，排序稳定）。</summary>
    private static List<string> EnumerateSourceFiles() =>
        Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(IsNotBuildArtifact)
            .Select(p => Path.GetRelativePath(Root, p).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    // ═════════════════════════════════════════════════════════════
    // #8：SuppressMessage 必带 Justification（Roslyn 语义级）
    // ═════════════════════════════════════════════════════════════

    /// <summary>
    /// 判定单个 SuppressMessage 特性是否携带非空 Justification：
    /// 命名参数 <c>Justification = "..."</c>（非 null），或 BCL 构造器第 3 个位置参数
    /// （签名 (category, checkId, justification = null, ...)）非 null。
    /// UnconditionalSuppressMessage 不在本守卫范围（与 bash #8 口径一致）。
    /// </summary>
    private static bool SuppressMessageHasJustification(AttributeSyntax attribute)
    {
        static bool IsNotNullLiteral(ExpressionSyntax? expression) =>
            expression is LiteralExpressionSyntax literal
            && !literal.Token.IsKind(SyntaxKind.NullKeyword);

        var arguments = attribute.ArgumentList?.Arguments ?? [];
        var positionalIndex = 0;
        foreach (var argument in arguments)
        {
            // 特性命名实参语法是 NameEquals（Justification = "..."），
            // NameColon（方法调用式 name: value）在特性参数中不出现——首版误用 NameColon，
            // 命名参数全部落进位置分支，靠位置巧合误判（红绿矩阵 MessageId 样本抓出）。
            if (argument.NameEquals is { } equals)
            {
                if (equals.Name.Identifier.ValueText == "Justification"
                    && IsNotNullLiteral(argument.Expression))
                    return true;
            }
            else
            {
                // 第 3 个位置参数（index 2）即 BCL 构造器的 justification
                if (positionalIndex == 2
                    && IsNotNullLiteral(argument.Expression))
                    return true;
                positionalIndex++;
            }
        }
        return false;
    }

    /// <summary>特性简名（剥离全限定与 Attribute 后缀）：x.Y.SuppressMessage(...) → SuppressMessage。</summary>
    private static string? SimpleAttributeName(AttributeSyntax attribute) =>
        attribute.Name switch
        {
            IdentifierNameSyntax identifier => TrimAttributeSuffix(identifier.Identifier.ValueText),
            QualifiedNameSyntax { Right: var right } => TrimAttributeSuffix(right.Identifier.ValueText),
            AliasQualifiedNameSyntax { Name: var aliased } => TrimAttributeSuffix(aliased.Identifier.ValueText),
            _ => null,
        };

    private static string? TrimAttributeSuffix(string name) =>
        name.EndsWith("Attribute", StringComparison.Ordinal)
            ? name[..^"Attribute".Length]
            : name;

    /// <summary>全 src 扫描：每个 SuppressMessage 特性必须携带非空 Justification。</summary>
    [Test]
    public async Task SuppressMessageAttributes_CarryJustification()
    {
        var files = EnumerateSourceFiles();
        await Assert.That(files.Count).IsGreaterThan(0); // 扫描面存在性（防仓库根定位错误后空转）

        var violations = new List<string>();
        var scannedAttributes = 0;
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        foreach (var relative in files)
        {
            var tree = CSharpSyntaxTree.ParseText(
                File.ReadAllText(Path.Combine(Root, relative)), parseOptions, path: relative);
            foreach (var attribute in tree.GetRoot().DescendantNodes().OfType<AttributeSyntax>())
            {
                if (SimpleAttributeName(attribute) != "SuppressMessage")
                    continue;

                scannedAttributes++;
                if (!SuppressMessageHasJustification(attribute))
                    violations.Add($"{relative}:{attribute.GetLocation().GetLineSpan().StartLinePosition.Line + 1}");
            }
        }

        // 守卫活跃性断言：src 内确实存在 SuppressMessage（0 个说明解析口径漂移，锚空转）
        await Assert.That(scannedAttributes).IsGreaterThan(0);
        if (violations.Count > 0)
            Assert.Fail($"发现 {violations.Count} 处 SuppressMessage 缺 Justification:\n{string.Join("\n", violations)}");
    }

    /// <summary>
    /// #8 判定器红绿矩阵（负向自证）：
    /// 无 Justification（红）/ 命名参数（绿）/ 第 3 位置参数（绿——bash 字面子串口径会误报，
    /// 此处锁定 Roslyn 升级）/ Justification = null（红）/ MessageId 字面量冒充（红——bash 会误放行）。
    /// </summary>
    [Test]
    public async Task SuppressMessageJustificationDetector_MatchesRedGreenMatrix()
    {
        const string source = """
            using System.Diagnostics.CodeAnalysis;

            public static class Probe
            {
                [SuppressMessage("Cat", "ID0")]
                public static void MissingJustification() { }

                [SuppressMessage("Cat", "ID1", Justification = "有理由")]
                public static void NamedJustification() { }

                [SuppressMessage("Cat", "ID2", "位置参数即理由")]
                public static void PositionalJustification() { }

                [SuppressMessage("Cat", "ID3", Justification = null)]
                public static void NullJustification() { }

                [SuppressMessage("Cat", "ID4", MessageId = "包含 Justification 字样的消息")]
                public static void MessageIdImpersonatingJustification() { }
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(source);
        var attributes = tree.GetRoot().DescendantNodes().OfType<AttributeSyntax>()
            .Where(a => SimpleAttributeName(a) == "SuppressMessage")
            .ToList();

        var verdicts = attributes
            .Select(a => (Method: a.Ancestors().OfType<MethodDeclarationSyntax>().First().Identifier.ValueText,
                         Has: SuppressMessageHasJustification(a)))
            .ToList();

        await Assert.That(verdicts.Single(v => v.Method == "MissingJustification").Has).IsFalse();
        await Assert.That(verdicts.Single(v => v.Method == "NamedJustification").Has).IsTrue();
        await Assert.That(verdicts.Single(v => v.Method == "PositionalJustification").Has).IsTrue();
        await Assert.That(verdicts.Single(v => v.Method == "NullJustification").Has).IsFalse();
        await Assert.That(verdicts.Single(v => v.Method == "MessageIdImpersonatingJustification").Has).IsFalse();
    }

    // ═════════════════════════════════════════════════════════════
    // #13：方言 SQL 守卫对称性（冲突安全家族）
    // ═════════════════════════════════════════════════════════════

    /// <summary>SQL 模板检查源（存在性断言范围，对齐 bash #13 的路径清单）。</summary>
    private static readonly string[] s_dialectSqlSources =
    [
        "src/PalDDD.Dapper/SqlTemplates.cs",
        "src/PalDDD.Dapper/EventLogSql.cs",
    ];

    private static readonly Regex s_constStringPattern =
        new(@"const string (\w+)\s*=\s*""([^""]+)""", RegexOptions.Compiled);

    private static readonly Regex s_dialectSuffix =
        new(@"(PG|PostgreSql|MySql|Sqlite)$", RegexOptions.Compiled);

    private static readonly Regex s_conflictKeywords =
        new(@"ON CONFLICT|INSERT IGNORE|INSERT OR IGNORE|ON DUPLICATE KEY UPDATE", RegexOptions.Compiled);

    private static readonly Regex s_guardKeywords =
        new(@"RETURNING|changes\(\)|ROW_COUNT\(\)", RegexOptions.Compiled);

    /// <summary>
    /// #13 判定器（等价 bash python 口径）：
    /// 常量名去方言后缀（PG/PostgreSql/MySql/Sqlite）归并家族；≥2 变体且任一变体含
    /// 冲突关键词的家族中，含冲突关键词的变体必须带守卫关键词（RETURNING/changes()/ROW_COUNT()）；
    /// 普通 INSERT（不含任何冲突关键词）为合法形态②——冲突由调用方
    /// IsUniqueConstraintViolation 异常捕获兜底（InboxInsertMySql 案例）。
    /// </summary>
    private static List<string> DetectDialectGuardViolations(IEnumerable<(string Name, string Sql)> constants)
    {
        var families = new Dictionary<string, List<(string Name, string Sql)>>();
        foreach (var (name, sql) in constants)
        {
            var family = s_dialectSuffix.Replace(name, "");
            if (!families.TryGetValue(family, out var variants))
                families[family] = variants = [];
            variants.Add((name, sql));
        }

        var violations = new List<string>();
        foreach (var variants in families.Values)
        {
            if (variants.Count < 2)
                continue;
            if (!variants.Any(v => s_conflictKeywords.IsMatch(v.Sql)))
                continue;
            foreach (var (name, sql) in variants)
            {
                // 普通 INSERT（无任何冲突处理关键词）= 合法形态②，豁免守卫词检查
                if (!s_conflictKeywords.IsMatch(sql))
                    continue;
                if (!s_guardKeywords.IsMatch(sql))
                    violations.Add($"{name}: 冲突安全家族缺守卫（RETURNING/changes()/ROW_COUNT）");
            }
        }
        return violations;
    }

    /// <summary>全量判定：SqlTemplates/EventLogSql 的冲突安全家族守卫必须对称。</summary>
    [Test]
    public async Task DialectSqlGuardFamilies_RemainSymmetric()
    {
        // 存在性断言（元审计脚本#26 同族）：检查源缺失必须 FAIL，不许静默空转
        var missing = s_dialectSqlSources
            .Where(p => !File.Exists(Path.Combine(Root, p)))
            .ToList();
        await Assert.That(string.Join(", ", missing)).IsEmpty();

        var constants = s_dialectSqlSources
            .SelectMany(p => s_constStringPattern.Matches(File.ReadAllText(Path.Combine(Root, p)))
                .Select(m => (Name: m.Groups[1].Value, Sql: m.Groups[2].Value)))
            .ToList();

        // 守卫活跃性：两个源文件确实提取到常量（0 个说明正则口径漂移，锚空转）
        await Assert.That(constants.Count).IsGreaterThan(20);

        var violations = DetectDialectGuardViolations(constants);
        if (violations.Count > 0)
            Assert.Fail($"方言 SQL 守卫不对称（{violations.Count} 处）:\n{string.Join("\n", violations)}");
    }

    /// <summary>
    /// #13 判定器红绿矩阵（负向自证）：
    /// ON CONFLICT 无守卫（红）/ ON CONFLICT + RETURNING（绿）/ INSERT OR IGNORE + changes()（绿）/
    /// 普通 INSERT 家族成员（绿——合法形态②）/ 单变体不成家族（绿）/ 家族无人含冲突关键词（绿）。
    /// </summary>
    [Test]
    public async Task DialectSqlGuardDetector_MatchesRedGreenMatrix()
    {
        // 红样本：家族含冲突变体，但无守卫词
        var bad = DetectDialectGuardViolations(
        [
            ("InboxInsertPG", "INSERT INTO t VALUES (@a) ON CONFLICT (x) DO NOTHING"),
            ("InboxInsertMySql", "INSERT INTO t VALUES (@a)"),
        ]);
        await Assert.That(bad.Count).IsEqualTo(1);
        await Assert.That(bad[0].StartsWith("InboxInsertPG", StringComparison.Ordinal)).IsTrue();

        // 绿样本 1：ON CONFLICT + RETURNING
        var guarded = DetectDialectGuardViolations(
        [
            ("InboxInsertPG", "INSERT INTO t VALUES (@a) ON CONFLICT (x) DO NOTHING RETURNING id"),
            ("InboxInsertMySql", "INSERT INTO t VALUES (@a)"),
        ]);
        await Assert.That(guarded).IsEmpty();

        // 绿样本 2：INSERT OR IGNORE + changes()
        var sqliteGuarded = DetectDialectGuardViolations(
        [
            ("InboxInsertSqlite", "INSERT OR IGNORE INTO t VALUES (@a); SELECT last_insert_rowid() WHERE changes() > 0;"),
            ("InboxInsertMySql", "INSERT INTO t VALUES (@a)"),
        ]);
        await Assert.That(sqliteGuarded).IsEmpty();

        // 绿样本 3：全家族普通 INSERT（无人含冲突关键词 → 不触发守卫要求）
        var plainFamily = DetectDialectGuardViolations(
        [
            ("SagaInsertPG", "INSERT INTO s VALUES (@a, CAST(@d AS jsonb))"),
            ("SagaInsertMySql", "INSERT INTO s VALUES (@a, @d)"),
        ]);
        await Assert.That(plainFamily).IsEmpty();

        // 绿样本 4：单变体不成家族
        var single = DetectDialectGuardViolations(
        [
            ("LonelyInsertPG", "INSERT INTO t VALUES (@a) ON CONFLICT (x) DO NOTHING"),
        ]);
        await Assert.That(single).IsEmpty();
    }

    // ═════════════════════════════════════════════════════════════
    // #14：姊妹实现乐观锁守卫对称性
    // ═════════════════════════════════════════════════════════════

    /// <summary>
    /// 接口 → 实现文件名映射（对齐 bash #14 的 impls 名单；InMemory 实现无 SQL，
    /// 存在性仍断言但乐观锁判定跳过——与 bash <c>if 'InMemory' in name: continue</c> 同口径）。
    /// </summary>
    private static readonly (string Interface, string[] ImplFiles)[] s_sisterImpls =
    [
        ("IIdempotencyStore", ["PalOrmIdempotencyStore", "IdempotencyDbContext", "InMemoryIdempotencyStore"]),
        ("IProjectionCheckpointStore", ["PalOrmProjectionCheckpointStore", "DapperProjectionCheckpointStore", "ProjectionCheckpointDbContext"]),
        ("IPalOutboxStore", ["PalOrmOutboxStore", "DapperOutboxStore", "OutboxDbContext", "InMemoryOutboxStore"]),
    ];

    private static readonly Regex s_optimisticLockPredicate =
        new(@"AND (?:version|updated_at|revision) = ", RegexOptions.Compiled);

    private static readonly Regex s_affectedRowGuard =
        new(@"if \((?:affected|rows) [><=]", RegexOptions.Compiled);

    private static readonly Regex s_efCoreConcurrencyCatch =
        new(@"catch \(DbUpdateConcurrencyException\)", RegexOptions.Compiled);

    /// <summary>
    /// #14 判定器（等价 bash python 口径）：
    /// 含乐观锁 UPDATE（AND version/updated_at/revision = 谓词）的姊妹实现文件，
    /// 每处乐观锁至少需要一个对应守卫（affected/rows 检查或 EFCore
    /// DbUpdateConcurrencyException 捕获）——单实现缺失即"修一半"残留。
    /// </summary>
    private static List<string> DetectSisterGuardViolations(IEnumerable<(string FileName, string Content)> files)
    {
        var violations = new List<string>();
        foreach (var (fileName, content) in files)
        {
            if (fileName.Contains("InMemory", StringComparison.Ordinal))
                continue; // InMemory 版无 SQL，跳过（bash 同口径）

            var optimistic = s_optimisticLockPredicate.Count(content);
            if (optimistic == 0)
                continue;
            var guarded = s_affectedRowGuard.Count(content);
            var efcoreCatch = s_efCoreConcurrencyCatch.Count(content);
            if (optimistic > guarded + efcoreCatch)
                violations.Add(
                    $"{fileName}: 乐观锁 UPDATE {optimistic} 处 vs 守卫 {guarded}+catch {efcoreCatch} 处（可能存在未守卫的 UPDATE）");
        }
        return violations;
    }

    /// <summary>全量判定：三族存储接口的姊妹实现乐观锁守卫必须对称。</summary>
    [Test]
    public async Task SisterStoreOptimisticLockGuards_RemainSymmetric()
    {
        var srcFiles = Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(IsNotBuildArtifact)
            .ToList();

        var missing = new List<string>();
        var contents = new List<(string FileName, string Content)>();
        foreach (var (_, implFiles) in s_sisterImpls)
        {
            foreach (var implName in implFiles)
            {
                // 存在性断言（元审计脚本#26 同族）：名单文件改名/移动必须 FAIL，不许静默跳过
                var hits = srcFiles.Where(f =>
                    Path.GetFileName(f) == implName + ".cs").ToList();
                if (hits.Count == 0)
                {
                    missing.Add($"{implName}: 实现类不存在（#14 存在性断言）——请更新 s_sisterImpls 名单");
                    continue;
                }
                contents.AddRange(hits.Select(h => (implName, File.ReadAllText(h))));
            }
        }
        await Assert.That(string.Join("; ", missing)).IsEmpty();

        var violations = DetectSisterGuardViolations(contents);
        if (violations.Count > 0)
            Assert.Fail($"姊妹实现乐观锁守卫不对称（{violations.Count} 处）:\n{string.Join("\n", violations)}");
    }

    /// <summary>
    /// #14 判定器红绿矩阵（负向自证）：
    /// 乐观锁无守卫（红）/ affected 守卫覆盖（绿）/ EFCore catch 覆盖（绿）/ 无乐观锁谓词（绿）。
    /// </summary>
    [Test]
    public async Task SisterStoreGuardDetector_MatchesRedGreenMatrix()
    {
        List<string> Probe(string content) => DetectSisterGuardViolations([("DapperProbe", content)]);

        // 红样本：2 处乐观锁谓词、0 守卫
        var bad = Probe(
            "UPDATE t SET x=1 WHERE id=@id AND version = @v;\nUPDATE t SET y=2 WHERE id=@id AND version = @v;");
        await Assert.That(bad.Count).IsEqualTo(1);
        await Assert.That(bad[0]).Contains("乐观锁 UPDATE 2 处 vs 守卫 0");

        // 绿样本 1：affected 守卫覆盖
        var guarded = Probe(
            "UPDATE t SET x=1 WHERE id=@id AND version = @v;\nvar affected = await cmd.ExecuteNonQueryAsync();\nif (affected == 0) throw new ConcurrencyException();");
        await Assert.That(guarded).IsEmpty();

        // 绿样本 2：EFCore catch 覆盖
        var efcore = Probe(
            "UPDATE t SET x=1 WHERE id=@id AND version = @v;\ntry { await SaveChangesAsync(); }\ncatch (DbUpdateConcurrencyException) { throw; }");
        await Assert.That(efcore).IsEmpty();

        // 绿样本 3：无乐观锁谓词
        var none = Probe("UPDATE t SET x=1 WHERE id=@id;");
        await Assert.That(none).IsEmpty();
    }

    // ═════════════════════════════════════════════════════════════
    // #15-#20（MIG-T5 下沉，承接 tech-debt-scan.sh 的 PD21-PD26 守卫）
    // ═════════════════════════════════════════════════════════════

    /// <summary>存在性断言（元审计脚本#26 同族）：检查源缺失必须 FAIL，不许静默空转。</summary>
    private static void AssertFilesExist(params string[] relativePaths)
    {
        var missing = relativePaths
            .Where(p => !File.Exists(Path.Combine(Root, p)))
            .ToList();
        if (missing.Count > 0)
            Assert.Fail($"检查源不存在（存在性断言——改名/移动后须更新测试路径清单）: {string.Join(", ", missing)}");
    }

    /// <summary>目标文件中匹配正则的行数（对齐 bash grep -c 的行计数口径）。</summary>
    private static int CountMatchingLines(string relativePath, Regex pattern)
    {
        return File.ReadLines(Path.Combine(Root, relativePath))
            .Count(pattern.IsMatch);
    }

    // ─────────────────────────────────────────────────────────────
    // #15：Dapper 全局状态自足（MatchNamesWithUnderscores 在 ModuleInitializer，PD21）
    // ─────────────────────────────────────────────────────────────

    // bash 同口径 ^[^/]*：行首到匹配之间不得出现 /——注释掉的赋值（// MatchNames...）不算存在
    private static readonly Regex s_matchNamesWithUnderscores =
        new(@"^[^/]*MatchNamesWithUnderscores\s*=\s*true", RegexOptions.Compiled);

    /// <summary>
    /// #15 全量判定：DapperAotInitializer（ModuleInitializer）中必须存在未注释的
    /// MatchNamesWithUnderscores = true——只在 DI 路径/测试夹具设置时，
    /// 直连构造（公共构造签名支持）的 snake_case 列会静默映射为空（PD21）。
    /// </summary>
    [Test]
    public async Task DapperGlobalState_IsSelfSufficientInModuleInitializer()
    {
        const string source = "src/PalDDD.Dapper/DapperAotInitializer.cs";
        AssertFilesExist(source);

        var hit = File.ReadLines(Path.Combine(Root, source)).Any(s_matchNamesWithUnderscores.IsMatch);
        if (!hit)
            Assert.Fail(
                $"{source} 缺未注释的 MatchNamesWithUnderscores = true（PD21）——" +
                "直连构造时 snake_case 列映射静默失效");
    }

    // ─────────────────────────────────────────────────────────────
    // #16：MySQL IN-LIMIT 禁令（JOIN 变体模板 + 消费方分派，PD22）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 三点存在性（对齐 bash #16）：模板常量按名匹配（值不经 SqlTemplates 整体呈现），
    /// 消费方按分派调用匹配。\b 防前缀改名（如 SagaLeaseActiveMySqlV2）误命中。
    /// </summary>
    private static readonly (string Path, Regex Anchor, string Role)[] s_mySqlJoinAnchors =
    [
        ("src/PalDDD.Dapper/SqlTemplates.cs",
            new Regex(@"const string SagaLeaseActiveMySql\b", RegexOptions.Compiled),
            "JOIN 变体模板常量"),
        ("src/PalDDD.Dapper/DapperOutboxStore.cs",
            new Regex(@"SqlTemplates\.OutboxLeaseUpdateMySql\b", RegexOptions.Compiled),
            "Outbox 消费方 JOIN 分派"),
        ("src/PalDDD.Dapper/DapperSagaStateStore.cs",
            new Regex(@"SqlTemplates\.SagaLeaseActiveMySql\b", RegexOptions.Compiled),
            "Saga 消费方 JOIN 分派"),
    ];

    [Test]
    public async Task MySqlInLimitJoinVariants_AllAnchorsPresent()
    {
        AssertFilesExist(s_mySqlJoinAnchors.Select(a => a.Path).ToArray());

        var missing = s_mySqlJoinAnchors
            .Where(a => CountMatchingLines(a.Path, a.Anchor) < 1)
            .Select(a => $"{a.Path}: {a.Role}（{a.Anchor}）")
            .ToList();
        if (missing.Count > 0)
            Assert.Fail(
                $"MySQL JOIN 变体锚点缺失（PD22——MySQL 不支持 IN (SELECT ... LIMIT)，会报 1235）:\n{string.Join("\n", missing)}");
    }

    // ─────────────────────────────────────────────────────────────
    // #17：PG 严格类型防护（jsonb CAST + 原生时间参数，PD23）
    // ─────────────────────────────────────────────────────────────

    private static readonly Regex s_pgSagaConstants =
        new(@"const string Saga(Insert|Update)PG\b", RegexOptions.Compiled);

    /// <summary>
    /// #17 全量判定（三点，对齐 bash #17）：① SagaInsertPG/SagaUpdatePG 常量按名存在
    /// （值含 CAST 由其分派+探针验）；② jsonb 写入模板值含 CAST(@data AS jsonb)（≥2 处）；
    /// ③ ToTimeParam 的 PG 原生参数分派（DapperDbType.PostgreSql =&gt; value）文件 ≥1。
    /// </summary>
    [Test]
    public async Task PgStrictTypeGuards_AllAnchorsPresent()
    {
        const string templates = "src/PalDDD.Dapper/SqlTemplates.cs";
        AssertFilesExist(templates);

        var violations = new List<string>();

        var pgConsts = CountMatchingLines(templates, s_pgSagaConstants);
        if (pgConsts < 2)
            violations.Add($"{templates}: Saga(Insert|Update)PG 常量 {pgConsts} 处（需 2——按常量名，值 grep 会被改名残留误判）");

        var pgCast = File.ReadLines(Path.Combine(Root, templates))
            .Count(l => l.Contains("CAST(@data AS jsonb)", StringComparison.Ordinal));
        if (pgCast < 2)
            violations.Add($"{templates}: CAST(@data AS jsonb) {pgCast} 处（需 2）");

        var toTimeFiles = Directory.EnumerateFiles(Path.Combine(Root, "src", "PalDDD.Dapper"), "*.cs", SearchOption.AllDirectories)
            .Where(IsNotBuildArtifact)
            .Count(f => File.ReadAllText(f).Contains("DapperDbType.PostgreSql => value", StringComparison.Ordinal));
        if (toTimeFiles < 1)
            violations.Add("src/PalDDD.Dapper/*.cs: ToTimeParam PG 原生参数分派（DapperDbType.PostgreSql => value）0 个文件（需 ≥1）");

        if (violations.Count > 0)
            Assert.Fail($"PG 严格类型防护缺失（PD23——PG 实测报 42804/42883）:\n{string.Join("\n", violations)}");
    }

    // ─────────────────────────────────────────────────────────────
    // #18：失败原因截断守卫对称性（Inbox↔Outbox 双管线，PD24）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// #18 全量判定（两点 + 存在性，对齐 bash #18）：两个处理器的 ex.Message 入库前
    /// 都必须过 Core.FailureReason.Normalize——一个缺失即整批饿死/无限重发（管线姊妹对称）。
    /// </summary>
    [Test]
    public async Task FailureReasonNormalize_SymmetricAcrossPipelines()
    {
        string[] sources =
        [
            "src/PalDDD.Transactions/Outbox/OutboxBatchProcessor.cs",
            "src/PalDDD.Transactions/Inbox/InboxProcessor.cs",
        ];
        AssertFilesExist(sources);

        var missing = sources
            .Select(p => (Path: p,
                Count: File.ReadLines(Path.Combine(Root, p))
                    .Count(l => l.Contains("FailureReason.Normalize(ex.Message)", StringComparison.Ordinal))))
            .Where(x => x.Count < 1)
            .Select(x => $"{x.Path}: {x.Count} 处")
            .ToList();
        if (missing.Count > 0)
            Assert.Fail(
                $"失败原因归一缺失（PD24——须 Core.FailureReason.Normalize(ex.Message)）:\n{string.Join("\n", missing)}");
    }

    // ─────────────────────────────────────────────────────────────
    // #19：PG naive 时间函数检测（AT TIME ZONE → session tz 漂移，PD25）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// #19 判定器（bash 同口径）：行含 AT TIME ZONE 即违规，但注释行
    /// （// 与 /// 前缀——TrimStart 后 // 已覆盖 ///）与含"修复"的说明行除外。
    /// </summary>
    private static List<string> DetectNaiveTimeZoneHits(string relativePath, string[] lines)
    {
        var hits = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.Contains("AT TIME ZONE", StringComparison.Ordinal))
                continue;
            if (line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                continue;
            if (line.Contains("修复", StringComparison.Ordinal))
                continue;
            hits.Add($"{relativePath}:{i + 1}");
        }
        return hits;
    }

    [Test]
    public async Task NaiveTimeZoneUsage_IsAbsentAcrossSrc()
    {
        var files = EnumerateSourceFiles();
        await Assert.That(files.Count).IsGreaterThan(0); // 扫描面存在性

        var hits = files
            .SelectMany(p => DetectNaiveTimeZoneHits(p, File.ReadAllLines(Path.Combine(Root, p))))
            .ToList();
        if (hits.Count > 0)
            Assert.Fail(
                $"src 出现 AT TIME ZONE（PD25——naive timestamp 与 timestamptz 比较按 session tz 解释）:\n{string.Join("\n", hits)}");
    }

    /// <summary>#19 判定器红绿矩阵（负向自证）：命中（红）/ 注释行（绿）/ 修复说明行（绿）。</summary>
    [Test]
    public async Task NaiveTimeZoneDetector_MatchesRedGreenMatrix()
    {
        var hits = DetectNaiveTimeZoneHits("probe.cs",
        [
            "var sql = \"x AT TIME ZONE 'UTC'\";",
            "// 说明: 旧版曾用 AT TIME ZONE（已废）",
            "/// <summary>AT TIME ZONE 修复记录</summary>",
            "var fixed_ = \"at_time_zone\"; // 修复：移除 AT TIME ZONE",
            "var ok = \"no hit\";",
        ]);
        await Assert.That(hits.Count).IsEqualTo(1);
        await Assert.That(hits[0]).IsEqualTo("probe.cs:1");
    }

    // ─────────────────────────────────────────────────────────────
    // #20：EF Core DbContext 关系型测试覆盖（逐 context 枚举 + gap 活账本，PD26）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 已知 gap 白名单（活账本）：三个方言 Outbox 变体暂无关系型用例（bash #20 allow 级口径）。
    /// 任何一个补齐关系型用例后必须同步移除——集合双向相等断言会强制账本与实况一致。
    /// </summary>
    private static readonly string[] s_knownRelationalCoverageGaps =
    [
        "MySqlOutboxDbContext",
        "PostgreSqlOutboxDbContext",
        "SqlServerOutboxDbContext",
    ];

    /// <summary>关系型测试文件标记：任一提供程序/建库调用（bash 同口径，无词边界）。</summary>
    private static readonly Regex s_relationalProviderHint =
        new("UseSqlite|UseNpgsql|EnsureCreated|SqliteConnection", RegexOptions.Compiled);

    /// <summary>
    /// 守卫测试自身路径（自指污染排除）：本文件含 s_knownRelationalCoverageGaps 的三个
    /// ctx 名与 s_relationalProviderHint 的 "SqliteConnection" 锚串——不排除时会把
    /// 传感器文件自身伪判成"关系型测试文件"，白名单三项恒 covered（首跑实测）。
    /// </summary>
    private const string GuardTestSelfPath = "test/PalDDD.DependencyInjection.Tests/TechDebtGuardTests.cs";

    /// <summary>
    /// #20 全量判定（含 #20a 扫描面存在性）：逐 DbContext 在 test/ 关系型测试文件中整词匹配；
    /// 实测 gap 集合必须与白名单双向相等——bash 是 allow 级（缺口列出供人工核实、不阻断），
    /// C# 版收紧为活账本：新增 gap（新 DbContext 无关系型用例）红，白名单项补齐也红（提醒缩账本）。
    /// </summary>
    [Test]
    public async Task DbContextRelationalCoverage_GapLedgerStaysExact()
    {
        // #20a 存在性：src 下 *DbContext.cs 可发现（目录改名/移动后不许对空集静默通过）
        var ctxFiles = Directory.EnumerateFiles(Path.Combine(Root, "src"), "*DbContext.cs", SearchOption.AllDirectories)
            .Where(IsNotBuildArtifact)
            .Select(p => Path.GetRelativePath(Root, p).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        await Assert.That(ctxFiles.Count).IsGreaterThan(0);

        // 第一遍：test/ 下含关系型提供程序标记的测试文件（bash 同口径的两遍扫描；
        // 排除守卫测试自身——传感器文件含锚字符串，不是被测物，见 GuardTestSelfPath）
        var relationalFiles = new List<(string Path, string Content)>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, "test"), "*.cs", SearchOption.AllDirectories)
                     .Where(IsNotBuildArtifact))
        {
            var relative = Path.GetRelativePath(Root, file).Replace('\\', '/');
            if (relative == GuardTestSelfPath)
                continue;
            var content = File.ReadAllText(file);
            if (s_relationalProviderHint.IsMatch(content))
                relationalFiles.Add((relative, content));
        }

        // 活跃性：关系型测试文件确实存在（0 个说明提供程序标记全部漂移，锚空转）
        await Assert.That(relationalFiles.Count).IsGreaterThan(0);

        // 第二遍：逐 DbContext 整词匹配（bash grep -qwF 同口径）
        var actualGaps = new List<string>();
        var coveredBy = new List<string>();
        foreach (var ctx in ctxFiles)
        {
            var ctxName = Path.GetFileNameWithoutExtension(ctx);
            var wordPattern = new Regex($@"\b{Regex.Escape(ctxName)}\b", RegexOptions.Compiled);
            var witness = relationalFiles.FirstOrDefault(f => wordPattern.IsMatch(f.Content));
            if (witness.Content is null)
                actualGaps.Add(ctxName);
            else
                coveredBy.Add($"{ctxName} ← {witness.Path}");
        }
        actualGaps.Sort(StringComparer.Ordinal);

        var expected = s_knownRelationalCoverageGaps.OrderBy(g => g, StringComparer.Ordinal).ToList();
        if (!expected.SequenceEqual(actualGaps))
        {
            var added = actualGaps.Except(expected).ToList();
            var resolved = expected.Except(actualGaps).ToList();
            Assert.Fail(
                "DbContext 关系型覆盖 gap 账本漂移（PD26——InMemory-only 掩盖映射缺陷）:\n" +
                $"  新增 gap（补关系型用例，或人工核实后入 s_knownRelationalCoverageGaps）: {string.Join(", ", added)}\n" +
                $"  已补齐（请从 s_knownRelationalCoverageGaps 移除）: {string.Join(", ", resolved)}\n" +
                $"  诊断（白名单项 covered-by）: {string.Join("; ", coveredBy.Where(c => resolved.Any(r => c.StartsWith(r, StringComparison.Ordinal))))}");
        }
    }
}
