# Pal.DDD — Serena 符号级评审报告（补充卷 II）

> **分析方法**：Serena `search_for_pattern` 全局搜索 + `find_symbol` 符号体提取
> **聚焦维度**：SQL 注入安全、线程安全原语、技术债标记、异常类型层次、DomainEventCollector、Serialization.Evolution、EFCore 配置

---

## 一、Dapper SQL 模板安全性

### 1.1 SqlTemplates（22 个 const string，177 行）

| 分组 | 模板数 | 注入风险 |
|------|:------:|:-------:|
| Outbox（Insert/Mark/Lease/Select） | 8 | ✅ 零风险（全部 `@param` 参数化） |
| Inbox（Select/Start/Mark/Insert PG/MySQL/SQLite） | 7 | ✅ 零风险 |
| Saga（Active/Lease/ById/Update/Insert） | 7 | ✅ 零风险 |

**Saga 乐观并发**：`SagaUpdate` 使用 `version=version+1 WHERE version=@v`——标准乐观并发控制。

### 1.2 SQL 插值审查（`$"` 搜索，src 范围）

| 文件 | 插值数 | 风险 | 评估 |
|------|:------:|:----:|------|
| SqlTemplates.cs | 0 | ✅ | 全部 const string |
| DapperOutboxStore.cs | 2 | ✅ | `leaseSubSql` 框架内部构造 |
| DapperBulkCopy.cs | 6 | ⚠️ | `table`/`colList` 来自方法参数 |
| PostgreSqlJsonbExtensions.cs | 14 | ✅ | `Escape()` 双引号转义 |
| PostgreSqlSoftDelete.cs | 7 | ⚠️ | `whereClause` 由调用方控制 |
| **PostgreSqlAuditor.cs** | **6** | ✅ | `NullOrJson`/`NullOrText` 已通过 `EscapeLiteral` 转义单引号，并由集成测试覆盖 |
| PostgreSqlSharding.cs | 4 | ✅ | shardId 是框架内部整数 |
| PostgreSqlOutboxNotifier.cs | 1 | ✅ | `_channelName` 来自配置 |
| SqliteJsonExtensions.cs | 13 | ✅ | `Escape()` 处理 |
| SqliteFtsExtensions.cs | 10 | ✅ | `Escape()` + `EscapeFts()` 处理 |
| SourceGen/*.cs | 20+ | ✅ | 生成编译时源代码，非运行时 SQL |
| 异常消息/日志 | 30+ | ✅ | 非 SQL 上下文 |

### 1.3 P1：PostgreSqlAuditor SQL 注入（已修复）

```csharp
// PostgreSqlAuditor.cs:106-108
private static string NullOrJson(string? v) => v is null ? "NULL" : $"'{EscapeLiteral(v)}'::jsonb";
private static string NullOrText(string? v) => v is null ? "NULL" : $"'{EscapeLiteral(v)}'";
private static string EscapeLiteral(string s) => s.Contains('\'') ? s.Replace("'", "''") : s;
```

**现状**：审计数据 `v` 包含单引号（如 `O'Brien`）时，`EscapeLiteral` 会按 PostgreSQL 标准转义为 `O''Brien`。

**验证**：`PostgreSqlAuditorTests.AppendAuditLog_EscapesTextAndJsonLiterals` 覆盖 `tableName`、`rowId`、`operation`、`oldDataJson`、`newDataJson`、`changedBy` 六类字面量边界。

---

## 二、线程安全原语

### 2.1 Interlocked（3 处）

| 文件 | 用法 | 评估 |
|------|------|------|
| `SmartEnum.cs:82,94` | `Interlocked.CompareExchange(ref s_values, dict.ToFrozenDictionary(), null)` | ✅ 防止并发覆盖 |
| `KafkaBroker.cs:202` | `Interlocked.Exchange(ref _disposed, 1)` | ✅ 标准 dispose 标志 |

### 2.2 Volatile（1 处）

| 文件 | 用法 | 评估 |
|------|------|------|
| `SmartEnum.cs:105` | `Volatile.Read(ref s_values)` | ✅ ARM 弱内存模型可见性 |

### 2.3 [ThreadStatic]（4 处，2 个文件）

| 文件 | 用途 | 评估 |
|------|------|------|
| `JsonMessageSerializer.cs:26,28` | Utf8JsonWriter + MemoryStream 池化 | ✅ 零分配热路径 |
| `JsonLinesEventStream.cs:46,48` | StreamReader + StreamWriter 池化 | ✅ 零分配流式读写 |

---

## 三、技术债标记

```
search_for_pattern("TODO|HACK|FIXME|WORKAROUND", paths_include_glob="src/**/*.cs")
→ 零命中
```

✅ **src 目录零 TODO/HACK/FIXME/WORKAROUND 标记。** 极高的工程纪律。

---

## 四、异常类型层次

### 4.1 自定义异常（6 个，全部 sealed）

| 异常类型 | 层 | 触发条件 | HTTP |
|---------|---|---------|:----:|
| `PalValidationException` | CQRS | IPalValidator 验证失败 | 400 |
| `HandlerNotFoundException` | CQRS | 请求类型未注册 | 404 |
| `EventStreamConcurrencyException` | EventLog | 乐观并发冲突 | — |
| `EventReplayException` | Projections.EventLog | 回放类型/名称/版本不匹配 | — |
| `MessageEvolutionException` | Serialization.Evolution | 演化路径缺失 | — |
| `PalPlatformVerificationException` | Serialization.Evolution | 验证失败聚合 | — |

### 4.2 异常处理策略

| 策略 | 使用处 | 评估 |
|------|:------:|------|
| `catch (Exception) when (ex is not OperationCanceledException)` | 8 处 | ✅ 正确排除取消 |
| `catch (Exception) when (attempt < MaxRetries)` | 1 处 | ✅ Saga 重试 |
| `catch (Exception) when (exception is not OutOfMemoryException)` | 1 处 | ✅ PalPlatformVerifier |
| `catch (Exception)` 无过滤 | 5 处 | ⚠️ P1（前卷已记录） |
| `AggregateException` 保留异常链 | 1 处 | ✅ Saga 重试 |

---

## 五、DomainEventCollector

```csharp
internal static class DomainEventCollector
{
    public static void Collect(DbContext? context, List<DomainEvent> events)
    {
        foreach (var entry in context.ChangeTracker.Entries())
            if (entry.Entity is IPalEntity { HasDomainEvents: true } entity)
                foreach (var evt in entity.DomainEvents())
                    events.Add(evt);
    }

    public static void Clear(DbContext? context)
    {
        foreach (var entry in context.ChangeTracker.Entries())
            if (entry.Entity is IPalEntity { HasDomainEvents: true } entity)
                entity.ClearDomainEvents();
    }
}
```

**生命周期**：
```
SavingChangesAsync → Collect → WriteEventsToOutbox
[EF Core 执行数据库操作]
SavedChangesAsync  → Clear
SaveChangesFailedAsync → _pending.Clear()
```

✅ 三阶段拦截器覆盖完整——成功/失败路径均清理。

---

## 六、Serialization.Evolution

### 6.1 模块组成

| 组件 | 职责 |
|------|------|
| `MessageEvolutionPipeline` | 版本升级执行链（v1→v2→v3） |
| `MessageUpgradeStep` | 单个升级步骤 |
| `MessageEvolutionBuilder` | 注册升级步骤（Fluent API） |
| `MessageContractManifest` | 契约清单 |
| `PalPlatformVerifier` | 启动时验证所有演化路径 |
| `MessageEvolutionException` | 路径缺失异常 |
| `PalPlatformVerificationException` | 验证失败聚合 |

### 6.2 设计亮点

1. **Fail-slow 验证**：收集所有缺失路径后一次性抛出
2. **OOM 排除**：`catch when (exception is not OutOfMemoryException)`
3. **实例方法**：允许 DI 注册和未来扩展
4. **跳跃检测**：`evolution step missing` / `evolution step overshot`

---

## 七、发现清单（本卷新增）

### 已修复

| # | 文件 | 问题 | 验证 |
|---|------|------|------|
| 7 | `PostgreSqlAuditor.cs:106-108` | NullOrJson/NullOrText SQL 字面量单引号转义 | `PostgreSqlAuditorTests.AppendAuditLog_EscapesTextAndJsonLiterals` |

### P2

| # | 文件 | 问题 |
|---|------|------|
| 15 | `PostgreSqlSoftDelete.cs:39` | whereClause 参数文档已标注受信任 SQL 片段契约 |
| 16 | `DapperBulkCopy.cs` | table/colList 参数已增加标识符验证 |

### 信息性

| # | 发现 | 评级 |
|---|------|:----:|
| I1 | src 零 TODO/HACK/FIXME | ⭐⭐⭐⭐⭐ |
| I2 | 6 个 sealed 自定义异常 | ⭐⭐⭐⭐⭐ |
| I3 | 无 IEntityTypeConfiguration | ⭐⭐⭐⭐☆ |
| I4 | [ThreadStatic] 池化 | ⭐⭐⭐⭐⭐ |
| I5 | Interlocked 保护 SmartEnum | ⭐⭐⭐⭐⭐ |
| I6 | Serialization.Evolution 完整框架 | ⭐⭐⭐⭐⭐ |
| I7 | DomainEventCollector 三阶段完整 | ⭐⭐⭐⭐⭐ |
