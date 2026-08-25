using System.Data.Common;
using System.Runtime.CompilerServices;

namespace PalDDD.Dapper;

/// <summary>
/// <see cref="DapperUnitOfWork"/> 事务边界传导给各 Dapper Store 的内部通道（二轮评审 T5）——
/// 按 <see cref="DbConnection"/> 实例键控的弱引用表，非 AsyncLocal。
/// <para>
/// <b>断链背景</b>：五个 Dapper Store（Outbox/Inbox/SagaState/ProjectionCheckpoint/EventLog）
/// 在构造函数快照 <c>DbTransaction</c>，而 DI 只注册 <c>DbConnection</c> 不注册事务——
/// DI 解析的 Store 恒无事务，Outbox"事件与业务数据同一事务提交"的核心前提靠调用方手工
/// 构造时序纪律。本通道让 <c>DapperUnitOfWork</c> 开启的活动事务自动传导给同连接的全部
/// Store（显式构造参数仍优先）。
/// </para>
/// <para>
/// <b>为什么不是 AsyncLocal（对齐 PalOrmAmbientTransaction 探针实证）</b>：异步方法内对
/// AsyncLocal 赋值只向下流动不外流——<c>BeginTransactionAsync</c> 内部 Set 后，调用方后续
/// 的 Store 调用读不到。改按 Connection 实例键控：Store 与 UoW 持有同一 Scoped
/// <c>DbConnection</c>，按引用查表无上下文流动依赖；ConditionalWeakTable 弱键防泄漏
/// （连接被 GC 时条目自动回收）。
/// </para>
/// <para>
/// 生命周期：<see cref="DapperUnitOfWork.BeginTransactionAsync"/> 成功后 Set；
/// Commit/Rollback/DisposeAsync 的 finally 清理段 Remove。
/// </para>
/// <para>
/// ⚠️ 理论边界（对齐 PalOrmAmbientTransaction P3-SRC-211 声明）：同连接双 UoW 并发 Begin
/// 时 CWT 条目只存"最后一个"事务。当前三方言 provider 均禁并行事务（连接已有活动事务时
/// 第二个 BeginTransactionAsync 即抛），本路径不可达；若未来引入 savepoint 型 provider 需
/// 重新评估键结构。
/// </para>
/// <para>
/// ⚠️ 限制：仅经 <see cref="DapperUnitOfWork"/> 开启的事务被传导；绕过 UoW 直接调
/// <c>connection.BeginTransactionAsync</c> 时 Store 不挂接（沿用构造参数显式传递语义）。
/// </para>
/// </summary>
internal static class DapperAmbientTransaction
{
    private static readonly ConditionalWeakTable<DbConnection, DbTransaction> Connections = new();

    /// <summary>读取该连接当前挂接的活动事务（无则 null）。</summary>
    public static DbTransaction? TryGet(DbConnection connection)
        => Connections.TryGetValue(connection, out var transaction) ? transaction : null;

    /// <summary>设置/清空该连接的活动事务（由同程序集 DapperUnitOfWork 在事务边界调用）。</summary>
    internal static void Set(DbConnection connection, DbTransaction? transaction)
    {
        if (transaction is null) Connections.Remove(connection);
        else Connections.AddOrUpdate(connection, transaction);
    }
}
