using PalDDD.PalORM.Stores;
using System.Data.Common;

namespace PalDDD.PalORM.Tests;

/// <summary>R12 H3：唯一冲突分类器的 AOT 安全分支与误报负例。</summary>
public sealed class SqlErrorClassifierTests
{
    [Test]
    public async Task MySql_JitErrorCode_ClassifiesDuplicateKey()
    {
        await Assert.That(SqlErrorClassifier.IsUniqueKeyViolation(
            new MySqlException("Duplicate entry", errorCode: 1062, sqlState: "23000"))).IsTrue();
        await Assert.That(SqlErrorClassifier.IsUniqueKeyViolation(
            new MySqlException("Duplicate entry", errorCode: 1586, sqlState: "23000"))).IsTrue();
    }

    [Test]
    public async Task MySql_AotFallback_SqlStateAndMessage_ClassifiesDuplicateKey()
    {
        // NativeAOT 实测：ErrorCode 退化为 E_FAIL，必须靠 SqlState + "Duplicate entry" 文案。
        await Assert.That(SqlErrorClassifier.IsUniqueKeyViolation(
            new MySqlException(
                "Duplicate entry 'a-b' for key 'idempotency_records.PRIMARY'",
                errorCode: -2147467259,
                sqlState: "23000"))).IsTrue();
    }

    [Test]
    public async Task MySql_OtherIntegrityViolation_IsNotClassifiedAsDuplicateKey()
    {
        // 23000 也覆盖 FK/NOT NULL：缺 "Duplicate entry" 时不得误判为唯一冲突。
        await Assert.That(SqlErrorClassifier.IsUniqueKeyViolation(
            new MySqlException(
                "Cannot add or update a child row: a foreign key constraint fails",
                errorCode: -2147467259,
                sqlState: "23000"))).IsFalse();
    }

    [Test]
    public async Task PostgreSql_SqlState23505_ClassifiesAndOthersDoNot()
    {
        await Assert.That(SqlErrorClassifier.IsUniqueKeyViolation(
            new PostgresException("duplicate key", sqlState: "23505"))).IsTrue();
        await Assert.That(SqlErrorClassifier.IsUniqueKeyViolation(
            new PostgresException("connection failure", sqlState: "08006"))).IsFalse();
    }

    [Test]
    public async Task Sqlite_UniqueConstraintMessage_ClassifiesAndOthersDoNot()
    {
        await Assert.That(SqlErrorClassifier.IsUniqueKeyViolation(
            new SqliteException("UNIQUE constraint failed: inbox_messages.message_id"))).IsTrue();
        await Assert.That(SqlErrorClassifier.IsUniqueKeyViolation(
            new SqliteException("database is locked"))).IsFalse();
    }

    [Test]
    public async Task InnerExceptionChain_IsTraversed()
    {
        await Assert.That(SqlErrorClassifier.IsUniqueKeyViolation(
            new InvalidOperationException(
                "outer",
                new PostgresException("duplicate key", sqlState: "23505")))).IsTrue();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design", "CA1032:Implement standard exception constructors",
        Justification = "测试桩：类型名必须与 provider 异常一致，只承载分类信号。")]
    private sealed class MySqlException(string message, int errorCode, string? sqlState) : DbException(message)
    {
        public override int ErrorCode => errorCode;
        public override string? SqlState => sqlState;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design", "CA1032:Implement standard exception constructors",
        Justification = "测试桩：类型名必须与 provider 异常一致，只承载分类信号。")]
    private sealed class PostgresException(string message, string? sqlState) : DbException(message)
    {
        public override int ErrorCode => 0;
        public override string? SqlState => sqlState;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design", "CA1032:Implement standard exception constructors",
        Justification = "测试桩：类型名必须与 provider 异常一致，只承载分类信号。")]
    private sealed class SqliteException(string message) : DbException(message)
    {
        public override int ErrorCode => 19;
    }
}
