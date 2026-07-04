# ADR 006：RecordedEvent 双构造路径与零拷贝读取

> 状态：已采纳  
> 日期：2026-06-29  

## 背景

`RecordedEvent`（`src/PalDDD.EventLog/RecordedEvent.cs`）是事件从持久化存储读回后的只读投影。它在两个不同上下文中被构造，性能要求截然不同：

1. **写入路径** — `AppendAsync` 写入事件后立即构造一个 `RecordedEvent` 返回给调用方（用于追溯 `GlobalPosition` 等）。此路径吞吐由写入瓶颈主导，多两次 `byte[]` 拷贝可忽略。
2. **读取路径** — 全量重放 / 投影重建时从存储读取大量事件，每事件零拷贝路径可省 2 次 `ToArray()` 分配，在 K 级事件量级带来可观 GC 压力下降，需与写入路径 P0 优化对标。

历史实现仅一个 `internal RecordedEvent(EventData data, ...)` 构造路径，读取路径需要将存储读出的 `byte[]` 包装为 `EventData`，再在构造函数内 `ToArray()` 拷贝出来——`byte[]` 在读路径上至少被拷贝两次。

## 决策

为写入与读取两条路径提供**两个独立构造函数**：

- `internal RecordedEvent(string streamName, long streamVersion, long globalPosition, DateTimeOffset recordedAt, EventData data)`
  - 写入路径专用。从 `EventData.Payload` / `EventData.Metadata`（`ReadOnlyMemory<byte>`）`ToArray()` 拷贝得到 `_payload` / `_metadata`。
  - 行为保守：写入事件瞬时可忽略两次拷贝，且对外 API 稳定。

- `internal RecordedEvent(string streamName, long streamVersion, long globalPosition, DateTimeOffset recordedAt, Guid eventId, string eventName, int schemaVersion, string contentType, byte[] payload, byte[] metadata, EventAuditMetadata audit)`
  - 读取路径专用。`payload` / `metadata` 直接引用赋值，**零拷贝**。
  - 通过 `internal static RecordedEvent RehydrateFromBytes(...)` 工厂方法暴露给 Infrastructure 包内部（Dapper / EF Core 适配层），避免外部调用方误传共享 `byte[]` 缓冲区。

对外公共 API 保持单一入口 `public static RecordedEvent Rehydrate(...)`，入参为 `ReadOnlyMemory<byte>`，内部 `ToArray()` 拷贝后走 `RehydrateFromBytes`——公共路径保守，避免外部 mutable 缓冲区被框架持有导致的潜在别名污染。

## 取舍

- **优点**：读路径零拷贝，与 P0 写入优化对齐；公共 API 单一入口稳定；mutable 缓冲区语义风险只暴露给受信任的 Infrastructure 包内部 (`internal`)。
- **代价**：两个 `internal` 构造函数签名近似重复，需在源码注释与 ADR 显式说明分工，避免后续维护时混淆。若未来引入新的读取路径（如快照加载），需评估是否复用零拷贝路径或新增第三种构造。

## 验证

- `PalDDD.EventLog.Tests` 已覆盖两条路径构造结果一致性（`StreamName` / `StreamVersion` / `Payload` / `Metadata` 字段对齐）。
- 性能基准 `PalDDD.Benchmarks` 提供 `RecordedEventReadBenchmark` 量化零拷贝路径与 `EventData` 中转路径的 GC 差异（每事件少 2 次 `byte[]` 分配）。

## 关联

- 详见源码：`src/PalDDD.EventLog/RecordedEvent.cs` 头注释及两个构造函数前的 `── EventData 构造路径 ──` / `── 零拷贝读取构造路径 ──` 分隔注释。
- 性能契约条目：`AGENTS.md` 性能契约列表中 `RecordedEvent.RehydrateFromBytes 零拷贝 — 读取路径禁止 ToArray()`。