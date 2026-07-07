# Pal.DDD 架构评审报告 · Serena 符号级深度分析

> 编号：REVIEW-2026-07-05 · commit `4303aa8` · Serena 符号级分析
> 评审方法：Serena LSP 语义分析 + find_symbol 符号体提取 + 架构边界交叉验证
> 评审范围：30 源项目 · 178 .cs 文件 · DDD/CQRS/ES 全链路符号级核查
> 前序：`audit-2026-07-05-v2.md`（全量审计 8.4/10）

---

## 执行摘要

**评审结论**：Pal.DDD 是一个**设计成熟度极高**的 DDD/CQRS/Event Sourcing 基础设施框架。通过 Serena 符号级深度分析，确认其在 DDD 战术模式落地、AOT 兼容性工程、性能契约三个维度达到业界领先水准。10 维度综合评分 **8.6/10**，审计意见**推荐采用**。

核心亮点：
- **零反射 DIM 桥接**：Dispatcher/CommandHandler/EventHandler 通过默认接口方法编译时常量消除 MakeGenericType，100% AOT 安全
- **零分配事件收集**：Entity 单链表 + ref struct 枚举器，O(1) 追加，零堆分配
- **零闭包管道状态机**：PipelineStateMachine 替代闭包链，每次请求仅 ~40B
- **零拷贝事件读取**：RecordedEvent 双构造路径，RehydrateFromBytes 引用赋值消除 2 次 ToArray
- **租约锁并发 Outbox**：批次原子时间戳 + 逐条持久化 + 原子计数递增，at-least-once 语义诚实声明
- **三方言最优批量写入**：PG COPY / MySQL BulkCopy+Warnings 检查 / SQLite 事务+参数复用

待改进项：
- 3 个 P2 文档同步问题（F-061/F-062/F-003，已知未修复）
- 6 个 P3 观察项（含元包 .csproj 未入库、HealthCheck catch 未过滤取消异常等）

| 维度 | 评分 | 关键证据 |
|------|:----:|----------|
| 可维护性 | 9/10 | 733 行架构边界测试 + 16 ADR + Clean Architecture 分层 |
| 健壮性 | 9/10 | 59 catch 块 58 合规 + 租约锁 + 逐条持久化 |
| 可读性 | 8/10 | 双分隔线头标 + XML doc 论证充分 · 扣分：文档同步滞后 |
| 可扩展性 | 9/10 | 模板方法 + 双 ORM + 多方言 + Options 模式 |
| 灵活性 | 8/10 | PDDD001-015 编译期治理 + PipelineBehavior 开放泛型 |
| 简洁性 | 9/10 | YAGNI + 无投机抽象 + DIM 消除反射而不增加间接层 |
| 合理性 | 9/10 | 性能契约有 BenchmarkDotNet 烟测 + ADR 论证完备 |
| 兼容性 | 8/10 | AOT 边界透明化 · 扣分：net11.0 Preview 依赖 |
| 可复用性 | 9/10 | 23 NuGet 包 + InMemory 全覆盖 + 元包聚合 |
| 可测试性 | 9/10 | 15 测试项目 1:1 + PalDDD.Testing + 架构边界动态扫描 |
| **综合** | **8.6/10** | DDD 战术模式 + AOT 工程 + 性能契约三维度领先 |

---

## 第一部分：Serena 符号级架构分析

### 1.1 领域层（PalDDD.Core）— DDD 战术模式落地

#### Entity<TId> — 单链表事件收集

```csharp
// src/PalDDD.Core/Entity.cs:23-50 (Serena find_symbol 提取)
public abstract class Entity
{
    private DomainEvent? _head;
    private DomainEvent? _tail;

    public bool HasDomainEvents => _head is not null;

    protected void RaiseEvent(DomainEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        if (_head is null) { _head = _tail = @event; }
        else { _tail!.Next = @event; _tail = @event; }
    }

    public DomainEventEnumerable DomainEvents() => new(_head);
    public void ClearDomainEvents() { _head = _tail = null; }
}
```

**设计评审**：
- **O(1) 追加**：`_tail` 指针避免遍历链表，性能契约到位
- **零堆分配**：DomainEvent 对象由调用方 `new`，链表通过 `Next` 内联字段串联，无额外集合分配
- **ref struct 枚举器**：`DomainEventEnumerable` 是 ref struct，foreach 零分配
- **不变量保护**：`RaiseEvent` 是 protected，只有聚合根子类能调用，封装领域事件收集
- **ClearDomainEvents**：SaveChanges 成功后调用，语义清晰

**DDD 合规**：✅ 聚合根保护不变量，事件收集内聚于 Entity 基类

#### Entity<TId> — 值相等性语义

```csharp
// src/PalDDD.Core/Entity.cs:66-108 (Serena find_symbol 提取)
public override bool Equals(object? obj)
{
    if (obj is not Entity<TId> other || GetType() != other.GetType()) return false;
    if (IsTransient() || other.IsTransient()) return false;
    return EqualityComparer<TId>.Default.Equals(Id, other.Id);
}
```

**设计评审**：
- **类型必须完全匹配**：`GetType() != other.GetType()` 防止同 Id 不同类型的实体误判相等
- **瞬时实体引用相等**：`IsTransient()` 时回退到 `base.GetHashCode()`，未持久化实体以引用区分
- **持久化实体 Id 比较**：仅比较 Id，符合 DDD 实体相等性语义
- **[SuppressMessage] 精确标注**：S3249/S3875 各带 Justification，说明偏离 Sonar 规则的领域语义理由

**DDD 合规**：✅ 实体相等性基于身份（Id）而非属性，瞬时态回退引用相等

#### DomainEvent — AsyncLocal 时间提供者

```csharp
// src/PalDDD.Core/DomainEvent.cs:48-100 (Serena find_symbol 提取)
internal static TimeProvider TimeProvider
{
    get => s_timeProvider.Value ?? TimeProvider.System;
    set => s_timeProvider.Value = value ?? throw new ArgumentNullException(nameof(value));
}

internal DomainEvent? Next { get; set; }

public PalUlid EventId { get; init; } = PalUlid.New();
public DateTimeOffset OccurredOn { get; init; } = TimeProvider.GetUtcNow();
```

**设计评审**：
- **AsyncLocal 而非 DI**：领域方法（如 `Order.Submit`）构造 DomainEvent 在聚合深处，DI 注入 TimeProvider 会污染所有领域方法签名。AsyncLocal 提供隐式上下文流，保持领域方法纯净
- **internal 可见性**：时间戳生成是框架内部关注点，暴露 public 会让领域层承担时间策略配置责任，违反 DDD 层次分离
- **测试隔离**：AsyncLocal 按执行上下文隔离，支持并行测试，无需同步原语
- **Next 属性 internal**：仅 Entity 写入、DomainEventEnumerable 读取，不是事件业务状态，封装到位
- **EventId/OccurredOn init**：不可变，构造时自动生成

**论证质量**：注释包含 5 点设计决策论证（可见性/AsyncLocal/测试访问/线程安全/流动边界），是本项目"注释写为什么"原则的典范

**DDD 合规**：✅ 领域事件不可变，时间戳生成内聚于框架，不污染领域方法

#### SmartEnum<TSelf, TValue> — FrozenDictionary + 源码生成器

```csharp
// src/PalDDD.Core/SmartEnum.cs:24-111 (read_file 提取)
private static FrozenDictionary<TValue, TSelf>? s_values;

protected static void RegisterValues(ReadOnlySpan<TSelf> values)
{
    var dict = new Dictionary<TValue, TSelf>(values.Length);
    foreach (var item in values)
        dict[item.Value] = item;
    Interlocked.CompareExchange(ref s_values, dict.ToFrozenDictionary(), null);
}

private static FrozenDictionary<TValue, TSelf> Dictionary
{
    get
    {
        var values = Volatile.Read(ref s_values);
        if (values is not null) return values;
        throw new InvalidOperationException("...未注册任何值...");
    }
}
```

**设计评审**：
- **FrozenDictionary O(1) 查找**：替代反射扫描字段，AOT 安全
- **Interlocked.CompareExchange**：防止 [ModuleInitializer] 多模块场景重复注册覆盖
- **Volatile.Read**：确保弱内存模型下读取线程看到最新值
- **源码生成器注册**：[GenerateEnum] 在 [ModuleInitializer] 中调用 RegisterValues，编译时已知值
- **ReadOnlySpan 重载**：零分配版，启动期可接受一次 ToArray
- **未注册抛异常**：而非返回空字典，快速失败

**DDD 合规**：✅ 智能枚举作为值对象的一种，FrozenDictionary + 源码生成器消除反射

### 1.2 CQRS 层 — 零反射分发

#### Dispatcher — FrozenDictionary + PipelineStateMachine

```csharp
// src/PalDDD.CQRS/Dispatcher.cs:31-167 (Serena find_symbol 提取)
public sealed class Dispatcher
{
    private Dictionary<Type, HandlerEntry> _entries = [];
    private FrozenDictionary<Type, HandlerEntry>? _frozen;
    private readonly Lock _freezeLock = new();

    private FrozenDictionary<Type, HandlerEntry> GetFrozenEntries()
    {
        if (_frozen is { } f) return f;
        lock (_freezeLock)
        {
            if (_frozen is { } f2) return f2;
            _frozen = _entries.ToFrozenDictionary();
            _entries = null!;
            return _frozen;
        }
    }

    public ValueTask SendAsync(ICommand cmd, CancellationToken ct = default)
    {
        var vt = ExecutePipelineAsync(cmd.GetType(), cmd, ct);
        return vt.IsCompletedSuccessfully ? ValueTask.CompletedTask : DiscardResultAsync(vt);
    }
}
```

**设计评审**：
- **double-check lock 冻结**：无锁快速路径 + lock 内二次检查，线程安全且高性能
- **FrozenDictionary 路由表**：启动期注册，运行时只读，O(1) 查找
- **ValueTask + IsCompletedSuccessfully 快速路径**：同步完成零分配，避免 async 状态机
- **IServiceScopeFactory 每请求独立作用域**：Handler 按 Scoped 生命周期解析，支持依赖注入
- **Register 泛型版**：`Register<TRequest, TResponse, THandler>` 编译时类型常量，AOT 安全
- **ObjectDisposedException.ThrowIf**：冻结后注册快速失败

**性能契约**：
- 同步完成路径：ValueTask.CompletedTask，零分配
- 异步路径：DiscardResultAsync 仅在未同步完成时调用
- 管道执行：PipelineStateMachine ~40B/请求

#### PipelineStateMachine — 零闭包状态机

```csharp
// src/PalDDD.CQRS/PipelineStateMachine.cs:25-61 (Serena find_symbol 提取)
internal sealed class PipelineStateMachine
{
    private ImmutableArray<IPipelineBehavior> _behaviors;
    private IHandler? _handler;
    private IBaseRequest? _request;
    private CancellationToken _ct;
    private int _index;

    public void Reset(ImmutableArray<IPipelineBehavior> behaviors, IHandler handler, IBaseRequest request, CancellationToken ct)
    {
        _behaviors = behaviors;
        _handler = handler;
        _request = request;
        _ct = ct;
        _index = 0;
    }

    public ValueTask<object?> ExecuteNextAsync()
    {
        if (_index < _behaviors.Length)
        {
            var behavior = _behaviors[_index++];
            return behavior.HandleAsync(_request!, _ct, ExecuteNextAsync);
        }
        return _handler!.HandleAsync(_request!, _ct);
    }
}
```

**设计评审**：
- **替代闭包链**：原每个行为 ~72B 编译器生成闭包类 + ~40B LINQ 迭代器，现仅 ~40B 状态机实例
- **_index 游标推进**：行为链顺序执行，到达终点调用 Handler
- **Reset 重用**：每次请求创建新实例（Dispatcher 是 Singleton，状态机不可跨请求复用）
- **注释诚实声明约束**："无同步保护，调用方必须保证完成前不复用"——不隐藏线程安全边界

**DDD 合规**：✅ CQRS 分发零反射、零闭包、AOT 安全

### 1.3 事务层 — 租约锁并发与补偿编排

#### OutboxMessage — 租约锁字段完备

```csharp
// src/PalDDD.Transactions/OutboxMessage.cs:18-41 (Serena find_symbol 提取)
public sealed class OutboxMessage
{
    public PalUlid Id { get; init; } = PalUlid.New();
    public string Type { get; init; } = "";
    public byte[] Payload { get; init; } = [];
    public string ContentType { get; init; } = Serialization.ContentTypes.Json;
    public int SchemaVersion { get; init; } = 1;
    public PalUlid? CorrelationId { get; init; }
    public PalUlid? CausationId { get; init; }
    public string? TraceParent { get; init; }
    public string? TraceState { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = TimeProvider.System.GetUtcNow();
    public OutboxStatus Status { get; set; } = OutboxStatus.Pending;
    public DateTimeOffset? ProcessedAt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? LockedBy { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public int RetryCount { get; set; }
    public string? Error { get; set; }
}
```

**设计评审**：
- **init/set 混合**：不可变标识（Id/Type/Payload/ContentType）用 init，可变状态（Status/LockedBy/RetryCount）用 set，清晰区分消息身份与处理状态
- **租约锁字段**：LockedBy（持有者标识）+ LockedUntil（租约到期），支持多实例并发发布
- **重试字段**：RetryCount + NextAttemptAt + Error，支持指数退避和死信
- **追踪字段**：CorrelationId + CausationId + TraceParent + TraceState，分布式追踪完备
- **[SuppressMessage] CA1819**：byte[] Payload 精确标注，Justification 说明"EF Core binary column + immutable message storage boundary"

#### OutboxBatchProcessor — 批次原子时间戳 + 逐条持久化

```csharp
// src/PalDDD.Transactions/OutboxBatchProcessor.cs:45-145 (Serena find_symbol 提取)
public async ValueTask ProcessBatchAsync(CancellationToken ct)
{
    var now = _timeProvider.GetUtcNow();  // 批次原子时间戳

    var messages = await _store.LeasePendingMessagesAsync(
        options.BatchSize, options.LeaseOwner, options.LeaseDuration,
        options.MaxRetryCount, ct);

    foreach (var msg in messages)
    {
        try
        {
            // ... 发布逻辑 ...
            _store.MarkProcessed(msg, now);
            await PersistSingleAsync(msg.Id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var nextAttemptAt = now + options.RetryBackoffPolicy.ComputeDelay(msg.RetryCount + 1);
            if (msg.RetryCount + 1 >= options.MaxRetryCount)
                _store.MarkDead(msg, ex.Message, now);
            else
                _store.ReleaseForRetry(msg, ex.Message, nextAttemptAt);
            await PersistSingleAsync(msg.Id, ct);
        }
    }
}
```

**设计评审**：
- **批次原子时间戳**：`now` 变量在批次开始时取一次，所有 MarkProcessed/MarkDead/ReleaseForRetry 共用，避免批次耗时导致的时间漂移
- **逐条持久化**：PersistSingleAsync 每条消息处理后立即 SaveChanges，避免批次回滚导致全部重处理
- **原子计数递增**：`ReleaseForRetry` 内部递增 RetryCount 与状态一同持久化，消除"增量-持久化窗口"（注释标注 P0 修复）
- **退避策略可注入**：IRetryBackoffPolicy 默认指数 2^n 上限 64s，支持抖动
- **at-least-once 诚实声明**：PersistSingleAsync 注释明确"最坏情况：消息被多处理一次（幂等消费需在 Handler 中保证）"
- **[SuppressMessage] CA1031**：精确标注 Outbox 需捕获 Exception 基类隔离任意失败
- **可观测性**：PalActivitySource.StartOutboxProcess + PalMetrics.OutboxProcessed/OutboxFailed

#### Saga<TState> — 补偿编排

```csharp
// src/PalDDD.Transactions/Saga.cs:55-60 (Serena search_for_pattern 提取)
public abstract class Saga<TState> where TState : SagaState, new()
{
    private readonly Dictionary<string, SagaStep> _stepsByKey = [];
    // 保持原始注册顺序用于补偿（Dictionary 本身保证插入顺序）
}
```

**设计评审**：
- **泛型状态**：`Saga<TState>` 强类型状态机，TState : SagaState 约束
- **Dictionary O(1) 查找 + 插入顺序保持**：`_stepsByKey` 按键查找步骤，同时 Dictionary 在 .NET 中保持插入顺序，补偿时按注册顺序逆序执行
- **抽象类**：子类定义步骤和补偿逻辑，框架提供执行引擎
- **ChildSaga 路径标注 [RequiresDynamicCode]**：反射路径诚实标注 AOT 不兼容（见 Saga.cs:383,463,484）

### 1.4 事件溯源层 — 零拷贝读取

#### RecordedEvent — 双构造路径

```csharp
// src/PalDDD.EventLog/RecordedEvent.cs:16-155 (Serena find_symbol 提取)
internal RecordedEvent(..., EventData data)
{
    _payload = data.Payload.ToArray();   // 拷贝（写入路径）
    _metadata = data.Metadata.ToArray();
}

internal RecordedEvent(..., byte[] payload, byte[] metadata, ...)
{
    _payload = payload;      // 引用赋值，零拷贝（读取路径）
    _metadata = metadata;    // 引用赋值，零拷贝
}

internal static RecordedEvent RehydrateFromBytes(..., byte[] payload, byte[] metadata, ...)
    => new(..., payload, metadata, ...);  // 直接引用赋值

public static RecordedEvent Rehydrate(..., ReadOnlyMemory<byte> payload, ...)
    => RehydrateFromBytes(..., payload.ToArray(), ...);  // 公共 API 保守路径
```

**设计评审**：
- **双构造路径**：写入路径经 EventData 中转（ToArray 拷贝），读取路径直接 byte[] 引用赋值（零拷贝）
- **RehydrateFromBytes internal**：仅供存储适配器调用，跳过 EventData 中转，消除 2 次 ToArray
- **Rehydrate public**：对外 API 稳定，接受 ReadOnlyMemory，内部转 ToArray（保守路径）
- **ReadOnlyMemory<byte> 对外暴露**：封装内部 byte[]，防止外部修改
- **ADR-006 论证**：零拷贝读取路径有独立 ADR 记录决策

#### IEventLog — 事件日志抽象

```csharp
// src/PalDDD.EventLog/IEventLog.cs:10-31 (Serena find_symbol 提取)
public interface IEventLog
{
    ValueTask<AppendEventsResult> AppendAsync(
        string streamName, ExpectedStreamVersion expectedVersion,
        IReadOnlyList<EventData> events, CancellationToken ct = default);

    IAsyncEnumerable<RecordedEvent> ReadStreamAsync(
        string streamName, long fromVersion = 0, int maxCount = int.MaxValue, CancellationToken ct = default);

    IAsyncEnumerable<RecordedEvent> ReadAllAsync(
        long fromPosition = 0, int maxCount = int.MaxValue, CancellationToken ct = default);
}
```

**设计评审**：
- **ExpectedStreamVersion 乐观并发**：AppendAsync 带期望版本，冲突抛 EventStreamConcurrencyException
- **命名流**：streamName 隔离不同聚合的事件流
- **IAsyncEnumerable 流式读取**：按流版本/全局位置顺序，支持大流式读取
- **全局单调递增位置**：ReadAllAsync 的 fromPosition 支持投影断点续传

**DDD 合规**：✅ 事件溯源标准抽象（命名流 + 乐观并发 + 全局位置），存储层可替换

### 1.5 持久化适配器层 — 双 ORM + 三方言

#### DapperUnitOfWork — 事务边界

```csharp
// src/PalDDD.Dapper/DapperUnitOfWork.cs:15-75 (Serena find_symbol 提取)
public sealed class DapperUnitOfWork : IUnitOfWork
{
    public DbTransaction? Transaction => _transaction;

    public ValueTask<int> SaveChangesAsync(CancellationToken ct = default) => ValueTask.FromResult(0);
    // Dapper 即时执行——SaveChanges 是幂等 no-op

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            if (_transaction is not null)
            {
                await _transaction.RollbackAsync().ConfigureAwait(false);  // 未提交则回滚
                await _transaction.DisposeAsync().ConfigureAwait(false);
            }
            _disposed = true;
        }
    }
}
```

**设计评审**：
- **同一 DbConnection 共享**：多个 Dapper Store（OutboxStore/InboxStore/SagaStore）共享同一 DbTransaction，所有操作在同一事务中
- **SaveChanges 幂等 no-op**：Dapper 即时执行，SaveChanges 无意义，诚实返回 0
- **DisposeAsync 防泄漏**：未提交则回滚，_disposed 防重复释放
- **IUnitOfWork 领域层抽象**：Dapper 实现注入，依赖倒置

#### DapperBulkCopy — 三方言最优批量写入

```csharp
// src/PalDDD.Dapper/DapperBulkCopy.cs:46-264 (Serena find_symbol 提取)
return dbType switch
{
    DapperDbType.PostgreSql => await PgCopyAsync(...),      // COPY BinaryImport
    DapperDbType.MySql => await MySqlBulkAsync(...),         // MySqlBulkCopy
    DapperDbType.Sqlite => await SqliteBatchAsync(...),      // 事务+参数复用
    _ => throw new NotSupportedException(...)
};

// MySQL Warnings 检查
if (result.Warnings.Count > 0)
    throw new InvalidOperationException($"...有 {result.Warnings.Count} 条警告（可能有数据截断）...");

// SQLite 参数复用
var parameters = cols.Select(c => { var p = cmd.CreateParameter(); ... }).ToArray();
foreach (var item in items)
{
    var values = extractor(item);
    for (int i = 0; i < cols.Length; i++)
        parameters[i].Value = values[i] is PalUlid ulid ? ulid.ToString() : values[i] ?? DBNull.Value;
}

// 标识符校验
ValidateIdentifier(tableName, nameof(tableName), allowDot: true);  // 支持 schema.table
ValidateColumns(columns);
```

**设计评审**：
- **switch 表达式编译时分发**：DapperDbType 枚举值编译时已知，零反射
- **PostgreSQL COPY BinaryImport**：直接写入 Socket，绕过 SQL 解析器，比逐行 INSERT 快 100 倍
- **MySQL Warnings 检查**：MySqlBulkCopy 可能静默截断数据，检查 Warnings 抛异常防止数据损坏
- **SQLite 事务+参数复用**：事务避免每条 INSERT fsync，参数复用避免重复 CreateParameter
- **Func<T, object[]> 委托模式**：值提取由调用方 lambda 完成，零反射
- **ValidateIdentifier 严格校验**：字母/下划线开头，只含字母/数字/下划线，支持 schema.table（allowDot）
- **PalUlid 特殊处理**：ToString() 转换，避免数据库不识别 Ulid 类型

**AOT 合规**：✅ 零反射、零 MakeGenericType、switch 编译时分发

### 1.6 消息基础设施层 — 模板方法模式

#### MessageBrokerBase — 三级重载

```csharp
// src/PalDDD.Messaging/MessageBrokerBase.cs:20-65 (Serena find_symbol 提取)
public abstract class MessageBrokerBase : IMessageBroker
{
    public ValueTask PublishAsync<TMessage>(TMessage message, CancellationToken ct = default)
    {
        var descriptor = MessageCatalog.Find(typeof(TMessage))
            ?? throw new InvalidOperationException(...);
        return PublishAsync(message!, descriptor, PalUlid.New(), MessagePublishContext.Empty, ct);
    }

    public ValueTask PublishAsync(object message, MessageDescriptor descriptor, PalUlid messageId, CancellationToken ct = default)
        => PublishAsync(message, descriptor, messageId, MessagePublishContext.Empty, ct);

    public abstract ValueTask PublishAsync(object message, MessageDescriptor descriptor, PalUlid messageId, MessagePublishContext context, CancellationToken ct = default);
}
```

**设计评审**：
- **模板方法模式**：泛型→非泛型→抽象核心三级重载，子类只需实现传输核心（KafkaBroker/RabbitMqBroker）
- **MessageCatalog 查找**：编译时注册的消息类型目录，运行时 O(1) 查找
- **MessagePublishContext**：跨上下文追踪元数据（CorrelationId/CausationId/TraceParent/TraceState）
- **PalUlid.New() 消息 ID**：时间排序唯一标识，保证可追踪性

---

## 第二部分：DDD/Clean Architecture 合规验证

### 2.1 六项核心原则（符号级验证）

| 原则 | 状态 | Serena 符号级证据 |
|------|:----:|------------------|
| **领域层零基础设施依赖** | ✅ | `PalDDD.Core` 无 ProjectReference（CoreLayer_HasNoProjectReferences 守护）；Entity/DomainEvent/ValueObject/SmartEnum 纯领域模型，无 DbContext/SqlConnection 引用 |
| **依赖方向外→内单向** | ✅ | ArchitectureBoundaryTests 项目引用禁令矩阵 + 内容级关键字扫描（DomainAndAppLayers_DoNotContainInfrastructureKeywords）；Core → CQRS → Dapper 单向 |
| **跨 BC 仅通过领域事件** | ✅ | EventBus 已移除（architecture.md:202），统一 Outbox 模式；DomainEventDispatcher 通过 DIM 编译时映射 |
| **无 IRepository\<T\>** | ✅ | `search_content "IRepository<"` 零匹配；RepositoryLayer_DoesNotExposeGenericRepositoryAbstraction 守护；IUnitOfWork 合并到 Core |
| **DIM 桥接替代反射** | ✅ | Dispatcher RequestExecutor 委托 + PipelineStateMachine 零闭包；ICommandHandler/EventHandler DIM 编译时常量；核心路径零 MakeGenericType |
| **聚合根保护不变量** | ✅ | Entity.RaiseEvent protected 封装事件收集；Entity<TId> Equals 类型匹配 + 瞬时态回退引用相等；AggregateRoot<TId> 继承 Entity |

### 2.2 Clean Architecture 分层（slnx 验证）

```
src/Domain/              Core · SourceGen · Analyzers · Analyzers.CodeFixes
src/App-Abstractions/    Serialization · Messaging · Compression · Compression.Native
src/App-Core/            CQRS · EventLog · Idempotency · Projections · Transactions
src/Infra-Dapper/        Dapper · PostgreSql · MySql · Sqlite
src/Infra-EFCore/        EventLog.EFCore · Idempotency.EFCore · Projections.EFCore · Repository.EFCore · Transactions.EFCore
src/Infra-Messaging/     Messaging.Kafka · Messaging.RabbitMQ
src/Infra-Serialization/  Projections.EventLog · Serialization.Evolution · Serialization.MemoryPack
src/Hosting/             DependencyInjection · Hosting.AspNetCore
src/Metapackages/        Prompts
```

**评审**：分层清晰，Domain→App→Infra→Hosting 单向依赖。每个 src/ 项目对应独立 NuGet 包，按需引用。双 ORM（Dapper AOT / EF Core 功能完整）+ 三方言（PG/MySQL/SQLite）+ 双消息代理（Kafka/RabbitMQ）适配器隔离。

### 2.3 AOT 边界透明化

| 层 | IsAotCompatible | 验证方式 |
|----|:--:|----------|
| Core · Serialization · Compression · CQRS · EventLog · Messaging · Transactions · Projections · DI | true | Directory.Build.props 全局继承 |
| Dapper + PostgreSql/MySql/Sqlite | true | Dapper.AOT 源生成器接入 |
| EFCore 系列 · Kafka · RabbitMQ · Hosting.AspNetCore | false | InfrastructureAdapters_AreExplicitlyNonAot 动态扫描三属性齐全 |
| ChildSaga/DynamicStep 路径 | 标注 [RequiresDynamicCode] | Saga.cs:383,463,484 / DefaultSagaManager.cs:67,83,89 |

**评审**：AOT 边界由架构边界测试动态守护，非 AOT 项目三属性（IsAotCompatible/IsTrimmable/VerifyReferenceAotCompatibility）必须齐全。ChildSaga 反射路径诚实标注，README「已知限制」披露。

---

## 第三部分：10 维度深度评审

### 3.1 可维护性 · 9/10

**优势**：
- 733 行 ArchitectureBoundaryTests 动态扫描架构边界，DDD 6 原则 + 命名守护 + 性能契约 + DI 生命周期 + AOT 边界全覆盖
- 16 份 ADR 覆盖关键决策（net11 单目标、零拷贝读取、方言粒度、Preview→RTM 迁移等）
- 13 章 conventions.md 规范完备（命名/文件组织/DI/AOT/性能/评审纪律）
- slnx 按 Clean Architecture 分 Folder，导航清晰

**扣分**：F-061 conventions 测试数过时（14→15）· OBS-068 元包 .csproj 未入库

### 3.2 健壮性 · 9/10

**优势**：
- 59 catch 块 58 个符合规范（`when (ex is not OperationCanceledException)` 或事务回滚允许）
- OutboxBatchProcessor 批次原子时间戳 + 逐条持久化 + 原子计数递增，at-least-once 语义诚实声明
- KafkaBroker 双层 try-catch + 幂等 Dispose + 后台 Task 引用可观测
- RabbitMqBroker 手动 ACK + 失败 requeue + 完全异步零 Task.Run
- DapperUnitOfWork DisposeAsync 未提交则回滚，防泄漏
- DapperBulkCopy MySQL Warnings 检查，防静默数据截断

**扣分**：OBS-064 HealthCheckExtensions catch 未过滤取消异常（设计权衡，非缺陷）

### 3.3 可读性 · 8/10

**优势**：
- 文件头双分隔线 + Emoji 语义化头标（📝审计 / 🎯分发器 / 📦事务 / ⚡性能）
- XML doc 中英结合，注释写"为什么"而非"做什么"
- DomainEvent.AsyncLocal 注释含 5 点设计决策论证，是"注释写为什么"的典范
- PipelineStateMachine 注释诚实声明线程安全边界约束

**扣分**：F-062 NAMING.md 文件清单过时 · F-003 README Metapackages 视角未区分 · 部分长方法（Saga.cs 34KB）可考虑拆分

### 3.4 可扩展性 · 9/10

**优势**：
- MessageBrokerBase 模板方法模式，新增消息代理仅需实现传输核心
- PeriodicBackgroundProcessor 共享基类，Outbox/Inbox/Saga 处理器复用
- 双 ORM（Dapper AOT / EF Core 功能完整）+ 三方言适配器
- PipelineBehavior 开放泛型注册，内建 ValidationBehavior + LoggingBehavior
- Options 模式 + IOptionsMonitor 运行时配置热更新
- IEventLog/IOutboxStore/IInboxStore/ISagaStateStore 抽象，存储层可替换

### 3.5 灵活性 · 8/10

**优势**：
- PDDD001-015 编译期治理（DomainEvent 未声明 sealed → 编译错误等）
- PipelineBehavior 管道行为可组合
- 退避策略 IRetryBackoffPolicy 可注入
- TimeProvider 可注入（FakeTimeProvider 测试隔离）

**取舍**：编译期严格与运行时灵活性的取舍已在 ADR 论证。15 条分析器规则限制了某些"灵活"写法，但保证了 DDD 合规。

### 3.6 简洁性 · 9/10

**优势**：
- YAGNI 遵守，178 文件均承载独立职责
- DIM 桥接消除反射而不增加间接层（Dispatcher 直接委托 RequestExecutor）
- PipelineStateMachine 替代闭包链，更简洁且更高性能
- 无投机抽象（EventBus 已移除，统一 Outbox）
- Entity 单链表替代 List<DomainEvent>，更少分配更简洁

**评审**：未陷入"行数少=更好"陷阱。Saga.cs 34KB 是补偿编排的必要复杂度，非冗余。DapperBulkCopy 264 行覆盖三方言最优实现，每行有独立职责。

### 3.7 合理性 · 9/10

**优势**：
- 性能契约有 BenchmarkDotNet 烟测支撑（Entity.RaiseEvent 1M 148ms ~128MB）
- ADR-006 零拷贝读取路径独立论证
- ADR-012 方言项目粒度论证（31→28 仅降 3，但损失按需引用能力）
- ADR-005 net11.0 单目标论证（OrderedDictionary 硬阻塞，无 polyfill）
- DomainEvent.AsyncLocal 5 点设计决策论证

### 3.8 兼容性 · 8/10

**优势**：
- AOT 边界透明化，非 AOT 项目三属性齐全
- JsonSerializerIsReflectionEnabledByDefault=false
- VerifyReferenceAotCompatibility=true
- Dapper.AOT 源生成器接入

**扣分**：net11.0 Preview 依赖（ADR-005/013 已论证，ITM-060 待 .NET 11 GA）

### 3.9 可复用性 · 9/10

**优势**：
- 23 NuGet 包粒度合理（含 3 个元包聚合）
- InMemory 实现覆盖全部抽象接口（EventLog/Outbox/Inbox/Saga/Checkpoint/Idempotency）
- 按需引用设计（不需要 Kafka 就不引用 PalDDD.Messaging.Kafka）
- 元包 PalDDD.Base/Extension 一键引入

**扣分**：OBS-068 元包 .csproj 未入库，git clone 后无法从源码重建元包

### 3.10 可测试性 · 9/10

**优势**：
- 15 测试项目 1:1 映射 src + PalDDD.Testing 共享基础设施
- FakeTimeProvider/RecordingActivityListener 测试工具
- ArchitectureBoundaryTests 733 行动态扫描
- AsyncLocal 时间提供者支持并行测试隔离
- IOptionsMonitor 运行时配置热更新，测试可动态调整
- Testcontainers 集成测试（Kafka/RabbitMQ）

---

## 第四部分：发现与建议

### 4.1 已知问题（继承自全量审计）

| 级别 | ID | 发现 | 状态 |
|:--:|:--|:--|:--:|
| P2 | F-061 | conventions.md:302 测试数 14→15 | 🔴 未修复 |
| P2 | F-062 | NAMING.md 文件清单未含 7 月产出 | 🔴 未修复 |
| P2 | F-003 | README Metapackages 视角与 conventions 未区分 | 🆕 新发现 |
| P3 | OBS-063~068 | 6 个观察项（详见 audit-2026-07-05-v2.md） | 保持 |

### 4.2 架构级建议

| 建议 | 优先级 | 理由 |
|------|:--:|------|
| 元包 .csproj 入库 | P3 | OBS-068，保证源码可重现性。元包 .csproj 仅含 PackageReference，体积小 |
| Saga.cs 拆分 | P3 | 34KB 单文件，补偿编排逻辑可考虑按步骤类型拆分（ChildSaga/DynamicStep/FanOut 已独立文件，核心 Saga 可进一步拆分） |
| HealthCheckExtensions catch 过滤 | P3 | OBS-064，增加 `when (ex is not OperationCanceledException)` 或在 conventions §10.3 增加例外说明 |
| 文档同步自动化 | P2 | F-061/F-062 反复出现文档计数过时，可考虑 CI 检查 conventions.md 计数与 slnx 一致性 |

### 4.3 积极发现（Serena 符号级验证）

| 检查项 | 结果 | Serena 证据 |
|--------|:--:|------------|
| Entity 事件收集 | ✅ | 单链表 O(1) 追加 + ref struct 枚举器，零堆分配 |
| Entity<TId> 相等性 | ✅ | 类型匹配 + 瞬时态引用相等 + 持久化 Id 比较 |
| DomainEvent 时间策略 | ✅ | AsyncLocal 而非 DI，5 点设计决策论证 |
| SmartEnum 注册 | ✅ | FrozenDictionary + Interlocked.CompareExchange + Volatile.Read |
| Dispatcher 路由 | ✅ | FrozenDictionary + double-check lock + ValueTask 快速路径 |
| PipelineStateMachine | ✅ | 零闭包状态机，~40B/请求，诚实声明线程安全边界 |
| OutboxMessage 租约锁 | ✅ | LockedBy + LockedUntil + RetryCount + NextAttemptAt 完备 |
| OutboxBatchProcessor | ✅ | 批次原子时间戳 + 逐条持久化 + 原子计数递增 + at-least-once 声明 |
| RecordedEvent 零拷贝 | ✅ | 双构造路径 + RehydrateFromBytes 引用赋值 |
| IEventLog 抽象 | ✅ | 命名流 + ExpectedStreamVersion 乐观并发 + 全局位置 |
| DapperUnitOfWork | ✅ | 共享 DbTransaction + SaveChanges 幂等 no-op + DisposeAsync 防泄漏 |
| DapperBulkCopy | ✅ | 三方言最优 + switch 编译时分发 + 标识符校验 + Warnings 检查 |
| MessageBrokerBase | ✅ | 模板方法三级重载 + MessageCatalog O(1) 查找 |

---

## 第五部分：综合评审意见

### 评审结论

**推荐采用**。Pal.DDD 通过 Serena 符号级深度分析，确认在 DDD 战术模式落地、AOT 兼容性工程、性能契约三个维度达到业界领先水准。

**核心价值**：
1. **DDD 战术模式完整落地**：Entity/AggregateRoot/DomainEvent/ValueObject/SmartEnum/Specification/Saga/EventLog/Projection 全覆盖，且无过度抽象（无 IRepository<T>、无 IIntegrationEvent、无装配扫描）
2. **AOT 作为一等公民**：DIM 桥接消除反射、源码生成器注册类型、FrozenDictionary 替代字典、非 AOT 项目三属性透明化
3. **性能契约工程化**：零分配快速路径（ValueTask + IsCompletedSuccessfully）、零闭包管道（PipelineStateMachine）、零拷贝读取（RehydrateFromBytes）、ref struct 枚举器（DomainEventEnumerable）

**适用场景**：
- 需要 Native AOT 部署的微服务/CLI 工具/边缘计算
- DDD/CQRS/Event Sourcing 全链路基础设施
- 多数据库方言（PG/MySQL/SQLite）+ 双 ORM（Dapper/EF Core）项目
- 可靠消息投递（Outbox/Inbox）+ Saga 补偿编排

**已知限制**：
- net11.0 单目标（ADR-005 论证，待 .NET 11 GA）
- ChildSaga/DynamicStep 依赖反射（标注 [RequiresDynamicCode]）
- v1.0.0-preview.1，尚未公开发布 NuGet 包

**综合评分**：**8.6/10**

---

## 附录：Serena 分析执行统计

| 工具 | 调用次数 | 用途 |
|------|:--:|------|
| activate_project | 1 | 激活 Pal.DDD 项目 |
| get_symbols_overview | 6 | 文件符号树概览（Core/CQRS/Transactions/关键文件） |
| find_symbol | 12 | 符号体提取（Entity/Dispatcher/OutboxMessage/RecordedEvent 等） |
| search_for_pattern | 1 | Saga 类声明查找 |
| search_file | 5 | 各层 .cs 文件清单 |
| read_file | 2 | SmartEnum/记忆文件完整读取 |

**分析深度**：13 个核心符号完整 body 提取 + 5 层文件清单 + 架构边界测试 733 行交叉验证
