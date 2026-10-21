# 2026-10-21 独立审计 P0/P1 修复轮

## 1. 范围与结论

本轮按最新独立审计实施两项正确性/性能修复：

1. **P0：Dapper PG Saga 租约释放直接报错**。`ReleaseLeasesAsync` 的 `saga_id IN @ids` 在
   Dapper.AOT + Npgsql 下被绑成单个数组参数，PG 生成 `IN $n`，报 `42601 syntax error`。
   此前路径只在 SQLite 测试覆盖，PG/MySQL 真库探针首次暴露。
2. **P1：`SaveManyAsync` 仍是逐行往返**。上一轮只把 `AdvanceNextCheckAtAsync` 改为单语句
   多行；终态保存/补偿状态保存仍按 Saga 逐条 UPDATE。

两项均已修复并补测试；PG/MySQL 的 Dapper 真库探针 30/30 通过。

## 2. 已落地

### 2.1 Dapper 租约释放改为显式 `@idN` 列表

- `DapperSagaStateStore.ReleaseLeasesAsync` 两个重载统一经 `BuildReleaseLeasesSql` 构造
  `@id0, @id1, ...`，不再使用 Dapper 数组绑定。
- 三方言同形：PG 的 `IN (@id0, @id1, ...)` 可正常解析；SQLite/MySQL 行为不变。
- `SqlTemplates.SagaReleaseLeases*` 保留为公共 legacy 常量，XML 注释明确标注 AOT 路径禁用
  （直接使用会复现 PG 42601）。
- 真库探针新增 5 行租约 → 4 参释放 → 重租 → 3 参释放，PG/MySQL 各 30/30 通过。

### 2.2 Dapper/PalORM `SaveManyAsync` 单语句多行 UPDATE

- 仅对 `Version > 0` 的已有行合批；新 Saga（Version=0）仍走插入路径。
- 每行 11 个参数：Dapper 每块 500 行（SQLite 90 行，避开 999 参数上限），PalORM 同宽。
- PG：`UPDATE ... FROM (VALUES ...)`；SQLite：`UPDATE ... FROM (SELECT ... UNION ALL ...)`；
  MySQL：`UPDATE ... JOIN (SELECT ... UNION ALL ...)`。形状与既有 `AdvanceNextCheckAtAsync`
  一致。
- 整块 `affected == count` 才回写内存 Version/Error；部分版本冲突时整块返回 0，不谎报
  成功（已落库子集由下次重载收敛）。
- 测试：Dapper/PalORM 各新增 100 行跨块测试（SQLite 90 行/块，必然执行两条 UPDATE）；
  既有两个 Saga 的批量测试继续通过。

### 2.3 其余 P1

- **自定义 store 轮转告警**：`SagaTimeoutProcessor` 构造时若 store 未实现
  `ISagaScanCursorStore<TState>`，进程内首次命中记 Warning，说明释放清 `leased_until`
  可能导致超过批大小的活跃 Saga 饿死。内置四栈均实现该接口，正常路径不触发。
- **PalORM 保留清理跨栈编码测试**：新增 SQLite 真库测试，按 provider 原生
  `DateTimeOffset` 写同一天 23:00 行，cutoff 为当日 00:00 时必须保留；与既有 EF Core
  同款测试形成两栈锁定。

## 3. 验证

- `dotnet run scripts/check-all.cs`：IDE 0 项、CA 0 错误、编译 0 错误。
- 全量测试：1775 个，1698 通过、77 跳过、0 失败。跳过分布：PalORM 54
  （Testcontainers 未启用）、Integration 14、Messaging.Integration 9（外部 broker/容器）。
- Dapper AOT 探针：wslc PG 30/30、MySQL 30/30；新增断言覆盖 3 参/4 参释放与 SaveMany
  批量持久化。
- PalORM 方言临时探针：wslc PG 9/9、MySQL 9/9（5 条插入、租约、SaveMany 全命中、
  持久化状态与 version 回读）。
- NativeAOT：SQLite 30/30（含新增 3 参/4 参释放与 SaveMany）。PG 的 NativeAOT 运行在
  既有 EventLog `QueryAsync` 处触发 Dapper 经典路径 `Reflection.Emit` 的
  `PlatformNotSupportedException`，发生在 Saga 段之前，与本轮改动无关；PG AOT 覆盖仍由
  非 AOT 真库探针承担。
  > **2026-10-21 R8 后续**：已修复。PG `AppendSingleRow` 与 MySQL `PositionRange` 由
  > private 嵌套类型改为 internal（Dapper.AOT 生成代码可见），`DapperSqlErrorClassifier`
  > 由鸭子类型反射改为 PG/MySQL/SQLite 强类型判定。win-x64 NativeAOT 探针三方言各
  > 33/33（wslc PG 18.6 / MySQL 8.4.11 真库），并新增 CI `dapper-aot-probe` 守卫。
- 公共 API 快照无变化（本轮只改实现与测试）。

## 4. 明确未做

| 项 | 原因 |
|---|---|
| Outbox 可 sargable 到期列/索引 | 需要三方言 generated column 或函数索引；SQLite 同时存在 Dapper `"O"` 与 provider 空格两种时间文本，生成列比较语义会分叉；且属在线 schema 迁移，需独立窗口与 `EXPLAIN` 验证。现有 `idx_outbox_due(status, created_at)` 已覆盖状态过滤与排序，1k 待租/20 万行实测 0.061ms（PG）/1.59ms（MySQL） |
| EF EventLog binary 读投影 | 只读 `event_id_bin` 会让未回填行的文本回退不可达（会静默返回全零 EventId）；要保留回退需按批二次查询文本列，收益只剩单列传输。待 M4 回填完成并切换 `UseBinaryEventId` 后再做，届时可把回退改为 fail-fast |
| EF EventLog DDL/EF 双源对账 | 与 binary 读投影同属 M4 收尾，需回填状态与 DDL 版本单一真源，单独迁移窗口 |

## 5. 残余风险

- PalORM 多行 UPDATE 的 PG/MySQL 真库证据来自临时探针，不在 CI 内；CI `dialect-probe`
  启用后应把 100 行 `SaveManyAsync` 断言纳入正式车道。
- 批量部分冲突时整块返回 0，成功子集已落库但调用方按失败记录；这是刻意的保守语义，
  不会造成重复补偿（终态行已不在租约候选集）。
- 自定义 store 告警每进程每 `TState` 只记一次；多实例部署时每个实例各一条，属预期。
