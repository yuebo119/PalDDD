using PalDDD.Dapper;

namespace PalDDD.Integration.Tests;

// ═══════════════════════════════════════════════════════════════
// 三栈 SQLite 谓词对照测试（ITM-807，2026-09-19）
// 姊妹一致性机械化起点：Dapper/PalORM/EF 三栈的 Outbox Lease/GetPending
// 资格谓词必须逐子句等价——改一栈漂移即红。已知有意差异走显式白名单
//（ADR-024：MySQL 互斥分叉不在本对照面——本测试只对照 SQLite 方言三栈）。
// 提取形态：Dapper 用编译期常量直引；EF/PalORM 从源码文本锚定切片
//（SQL 变更必然触碰源码文本 → 切片提取使文本漂移即红，这正是目的）。
// ═══════════════════════════════════════════════════════════════

public class CrossStackPredicateParityTests
{
    // ── 归一化：列名 snake→Pascal、参数占位→?、比较符空格统一、空白折叠 ──
    private static readonly (string Snake, string Pascal)[] ColumnMap =
    [
        ("outbox_messages", "OutboxMessages"),
        ("next_attempt_at", "NextAttemptAt"),
        ("locked_until", "LockedUntil"),
        ("locked_by", "LockedBy"),
        ("retry_count", "RetryCount"),
        ("created_at", "CreatedAt"),
        ("status", "Status"),
        ("id", "Id"),
    ];

    private static string Normalize(string sql)
    {
        var s = sql;
        // 截到谓词段（SELECT 头与 RETURNING 尾不参与等价断言——列清单形态是声明性差异）
        var whereAt = s.IndexOf("WHERE", StringComparison.OrdinalIgnoreCase);
        if (whereAt > 0) s = s[whereAt..];
        var returningAt = s.IndexOf("RETURNING", StringComparison.OrdinalIgnoreCase);
        if (returningAt > 0) s = s[..returningAt];

        // 枚举插值与参数占位 → ? / 0（PalORM FormattableString 插值——{pending}
        // 局部变量与 {(int)OutboxStatus.Pending} 字面两种形态都是 Status 列的 0；
        // EF {N} 占位符 / Dapper @name）
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\{\(int\)OutboxStatus\.Pending\}|\{pending\}", "0");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\{[A-Za-z_][A-Za-z0-9_.]*\}|\{\d+\}", "?");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"@[A-Za-z_][A-Za-z0-9_]*", "?");

        // 列名映射（长键先行防子串误替换）
        foreach (var (snake, pascal) in ColumnMap.OrderByDescending(m => m.Snake.Length))
            s = System.Text.RegularExpressions.Regex.Replace(
                $"'{s}'", $@"\b{snake}\b", pascal)[1..^1];

        // 比较符两侧空格统一（单趟长度序替换，防 <= 被 < 二次拆解）、括号内空白
        //（EF raw string 多行缩进折叠为 "( SELECT"）归一、再折叠空白
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*(<=|>=|<>|<|>|=)\s*", " $1 ");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\(\s+", "(");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+\)", ")");

        // status 参数形态（Dapper @status，调用点传 (int)Pending=0）与字面量 0 语义等价——
        // 归一（须在比较符空格统一后：此前文本是 "status=@status" 无空格形态）
        s = System.Text.RegularExpressions.Regex.Replace(s, @"Status = \?", "Status = 0");

        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        return s;
    }

    // ── 提取：源码文本锚定切片（起点锚 + 终点锚之间，两端锚均计入）──
    private static string Slice(string filePath, string startAnchor, string endAnchor)
    {
        var text = File.ReadAllText(FindRepoRoot(filePath));
        var start = text.IndexOf(startAnchor, StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException($"起点锚未命中：{startAnchor}（{filePath}）——源码形态已变，本测试需同步更新");
        var end = text.IndexOf(endAnchor, start, StringComparison.Ordinal);
        if (end < 0) throw new InvalidOperationException($"终点锚未命中：{endAnchor}（{filePath}）");
        return text[start..(end + endAnchor.Length)];
    }

    private static string FindRepoRoot(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "PalDDD.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir is null
            ? throw new InvalidOperationException("未定位仓库根（PalDDD.slnx）")
            : Path.Combine(dir, relative);
    }

    // EF：起点锚先定位 API 调用点再找 SQL 本体——防止命中 XML 注释里的 SQL 描述
    //（注释版无 LIMIT 尾段，DIAG 实证曾先命中注释）
    private static string EfLeaseSql()
    {
        var text = File.ReadAllText(FindRepoRoot("src/PalDDD.Transactions.EFCore/SqliteOutboxDbContext.cs"));
        var api = text.IndexOf("ExecuteSqlAsync($\"\"\"", StringComparison.Ordinal);
        var start = text.IndexOf("UPDATE OutboxMessages", api, StringComparison.Ordinal);
        var anchor = "LIMIT {batchSize})";
        var end = text.IndexOf(anchor, start, StringComparison.Ordinal);
        return text[start..(end + anchor.Length)]; // 终点锚计入切片（[..end) 不含 end 处字符）
    }

    private static string EfPendingSql()
    {
        var text = File.ReadAllText(FindRepoRoot("src/PalDDD.Transactions.EFCore/SqliteOutboxDbContext.cs"));
        // GetPending 走 FromSqlRaw(sql, params) 复合参数形态（{0}/{1}/{2} 字面占位，非 $ 插值）
        var api = text.IndexOf("FromSqlRaw(\"\"\"", StringComparison.Ordinal);
        var start = text.IndexOf("SELECT * FROM OutboxMessages", api, StringComparison.Ordinal);
        var anchor = "LIMIT {2}";
        var end = text.IndexOf(anchor, start, StringComparison.Ordinal);
        return text[start..(end + anchor.Length)];
    }

    /// <summary>PalORM SQLite 分支的 Lease：同文件 PG 分支在先且 UPDATE 开头相同——
    /// 逐个候选找「自身切片内不含 FOR UPDATE SKIP LOCKED」的那次命中（PG 分支的
    /// LIMIT 后是锁子句而非右括号，故以 RETURNING 为界判定）（ADR-024：PG 锁子句
    /// 是方言白名单，不入 SQLite 对照面）。</summary>
    private static string PalOrmLeaseSql()
    {
        var text = File.ReadAllText(FindRepoRoot("src/PalDDD.PalORM/Stores/PalOrmOutboxStore.cs"));
        var searchFrom = 0;
        while (true)
        {
            var start = text.IndexOf("UPDATE outbox_messages SET locked_by", searchFrom, StringComparison.Ordinal);
            if (start < 0) throw new InvalidOperationException("PalORM SQLite Lease 锚未命中——源码形态已变，本测试需同步更新");
            var ret = text.IndexOf("RETURNING", start, StringComparison.Ordinal);
            var slice = text[start..ret];
            if (!slice.Contains("FOR UPDATE SKIP LOCKED", StringComparison.Ordinal))
                return slice; // 完整段（LIMIT 含尾）——Normalize 在 RETURNING 处截断
            searchFrom = ret;
        }
    }

    private static string PalOrmPendingSql() => Slice(
        "src/PalDDD.PalORM/Stores/PalOrmOutboxStore.cs",
        "SELECT id, type, payload, content_type, schema_version, status, retry_count, created_at, processed_at, next_attempt_at, locked_by, locked_until, error, correlation_id, causation_id, trace_parent, trace_state FROM outbox_messages",
        "LIMIT {batchSize}");

    // ── 断言：三栈归一化等价（白名单差异在本测试形态下不存在——SQLite 方言
    //    三栈子查询/谓词应完全同构；任何子句漂移都是回归）──

    [Test]
    public async Task Lease_EligibilityPredicate_ParityAcrossThreeStacks()
    {
        var dapper = Normalize(SqlTemplates.OutboxLeaseUpdateSqlite);
        var ef = Normalize(EfLeaseSql());
        var palorm = Normalize(PalOrmLeaseSql());

        await Assert.That(ef).IsEqualTo(dapper);
        await Assert.That(palorm).IsEqualTo(dapper);
    }

    [Test]
    public async Task GetPending_EligibilityPredicate_ParityAcrossThreeStacks()
    {
        var dapper = Normalize(SqlTemplates.OutboxSelectPending);
        var ef = Normalize(EfPendingSql());
        var palorm = Normalize(PalOrmPendingSql());

        await Assert.That(ef).IsEqualTo(dapper);
        await Assert.That(palorm).IsEqualTo(dapper);
    }
}
