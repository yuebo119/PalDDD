using System.Data.Common;
using System.Runtime.CompilerServices;

namespace PalDDD.PalORM;

/// <summary>
/// IUnitOfWork 事务边界传导给 raw command 的内部通道（ITM-243）——按 <see cref="DataSession{TProvider}"/>
/// 实例键控的弱引用表，非 AsyncLocal。
/// <para>
/// PalORM 5.3 <c>DataSession</c> 无公开活动事务访问器（成员面：BeginTransactionAsync /
/// UseTransaction(仅 setter) / WithTransaction 均不能查询），<c>GetRawConnection()</c>
/// 创建的手动命令不自动 enlist —— MySQL 活动事务下未挂 <c>cmd.Transaction</c> 的命令抛
/// InvalidOperationException（MySqlConnector 严格校验，探针实测）。
/// </para>
/// <para>
/// <b>为什么不是 AsyncLocal（探针实证修正）</b>：异步方法内对 AsyncLocal 赋值只向下流动，
/// 不外流到调用方——<c>BeginTransactionAsync</c> 内部 Set 后，调用方后续的 Store 调用读不到
///（探针 ambient(reflect)=null 实证）。改按 Session 实例键控：Store 持有与 UoW 同一 Scoped
/// <c>DataSession</c>，按引用查表无上下文流动依赖；ConditionalWeakTable 弱键防泄漏
///（Session 被 GC 时条目自动回收）。SQLite 引擎级事务自动参与命令执行（连接内 BEGIN 后全部
/// 命令入事务），缺失挂接在 SQLite 不可观测——本通道的传感器只在 MySqlConnector 方言有效
///（多方言测试 + MySQL 探针承载）。
/// </para>
/// <para>
/// 生命周期：<see cref="PalOrmUnitOfWork{TProvider}"/>.BeginTransactionAsync 成功后 Set；
/// Commit/Rollback/Dispose 的 finally 清理段 Set(null)；三 Store（Saga/Checkpoint/Idempotency）
/// 的 raw command 构造处 TryGet 挂接。
/// </para>
/// <para>
/// ⚠️ 理论边界（P3-SRC-211 声明）：同 Session 双 UoW 并发 Begin 时，CWT 键级条目只存
/// "最后一个"事务——先结束者的 finally <c>Set(null)</c> 会按键 <c>Remove</c> 误清后者挂接
/// 的事务。当前三方言 provider 均禁并行事务（连接已有活动事务时第二个
/// <c>BeginTransactionAsync</c> 即抛），本路径不可达；若未来引入 savepoint 型 provider
///（同连接嵌套事务），需重新评估键结构（如 value 改为事务栈）。
/// </para>
/// <para>
/// ⚠️ 限制：仅经 IUnitOfWork（<see cref="PalOrmUnitOfWork{TProvider}"/>）开启的事务被传导；
/// 直接调 <c>session.BeginTransactionAsync</c> 绕过 IUnitOfWork 时 raw command 不挂接
///（该路径下连 PalORM ExecuteAsync 也需手动 UseTransaction，属既有语义）。
/// </para>
/// </summary>
internal static class PalOrmAmbientTransaction
{
    private static readonly ConditionalWeakTable<object, DbTransaction> Sessions = new();

    /// <summary>读取该 Session 当前挂接的活动事务（无则 null）。</summary>
    public static DbTransaction? TryGet(object session)
        => Sessions.TryGetValue(session, out var transaction) ? transaction : null;

    /// <summary>设置/清空该 Session 的活动事务（由同程序集 PalOrmUnitOfWork 在事务边界调用）。</summary>
    internal static void Set(object session, DbTransaction? transaction)
    {
        if (transaction is null) Sessions.Remove(session);
        else Sessions.AddOrUpdate(session, transaction);
    }
}
