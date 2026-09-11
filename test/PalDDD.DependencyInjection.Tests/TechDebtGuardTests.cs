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
//
// 与 bash/python 版的判定差异（有意收紧，方向为更严）：
//   #8 bash 只查特性文本内 "Justification" 子串——① 位置参数形态
//      [SuppressMessage("Cat", "ID", "理由")] 会被误报缺失（BCL 构造器第 3 参数就是
//      justification）；② MessageId = "…Justification…" 的字面量会误放行。
//      Roslyn 版按参数语义判定：命名参数 Justification 非 null，或第 3 个位置参数非 null。
//   #13/#14 与 bash python 同口径（正则 + 家族归并 + 计数比较），存在性断言保留
//      （检查源文件缺失必须 FAIL，不静默空转——元审计脚本#26 教训）。
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
}
