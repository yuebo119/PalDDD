# ADR 023：IUnitOfWork 嵌套事务语义 — 三栈分歧的收敛方向

> 状态：**提议（待维护者裁决）**
> 日期：2026-09-16
> 关联：ADR-019（IUnitOfWork 归属 Core）、ADR-020（三栈并行策略与跨栈契约分歧的 v3.0 窗口）、
> ADR-017（Saga 租约乐观并发）、ITM-088（Dapper Begin 前置判活）、
> `docs/review/action-items-2026-09-16-ocr-scan.md` ITM-763

## 背景

`UnitOfWorkExtensions.ExecuteInTransactionAsync`（`src/PalDDD.Core/IUnitOfWork.cs:43-69`）是
无条件「Begin → work → SaveChanges → Commit，异常则 Rollback」的模板方法。接口对
`BeginTransactionAsync` 的声明是「**当无活动事务时**开启数据库事务」（同文件 :20），
但三个实现栈对「事务已活动时再次 Begin」的处置各不相同：

| 栈 | 事务已活动时 Begin 的行为 | 位置 | 嵌套 `ExecuteInTransactionAsync` 的后果 |
|----|------------------------|------|--------------------------------------|
| EF Core | 静默 no-op（`if (CurrentTransaction is null)`） | `src/PalDDD.Repository.EFCore/UnitOfWork.cs:38` | 内层 Commit **提交外层事务** → 原子性破坏 |
| Dapper | 抛 `InvalidOperationException`（ITM-088 显式声明） | `src/PalDDD.Dapper/DapperUnitOfWork.cs:41-43` | fail-fast，无破坏 |
| PalORM | 幂等 no-op（`if (_transaction is not null) return;`，注释「幂等：已有活动事务」） | `src/PalDDD.PalORM/PalOrmUnitOfWork.cs:47` | 同 EF Core → 原子性破坏 |

**原子性破坏的机理**（EF Core / PalORM 栈）：外层已开启事务 → 内层 `BeginTransactionAsync` 静默
返回（不开启新事务）→ 内层 `CommitAsync` 提交的是**外层**事务（EF Core 实现为
`if (CurrentTransaction is not null) CommitTransaction`，`UnitOfWork.cs:47-48`）→ 内层之后的
外层 work 已无事务保护，外层异常路径的 `RollbackAsync` 面对的是已提交的事务。

`ExecuteInTransactionAsync` 的 XML doc（`:37-42`）未提及嵌套语义 —— 调用方无从得知该风险。

**现状证据（2026-09-16 实测 grep 核对）**：本仓无内部调用方依赖该 no-op 语义——
`BeginTransactionAsync` 的内部调用仅 `DapperBulkCopy.cs:370` 与 `EventLogDbContext.cs:64`
的局部事务（不经 `IUnitOfWork`）；Dapper 栈的 fail-fast 已被
`DapperAmbientTransaction.cs:32`（「第二个 BeginTransactionAsync 即抛」）文档化为既定契约。

## 决策（建议）

**收敛到 fail-fast：EF Core 与 PalORM 栈的 `BeginTransactionAsync` 在事务已活动时抛
`InvalidOperationException`（对齐 Dapper 的 ITM-088 契约），并把嵌套语义写入
`ExecuteInTransactionAsync` 与接口的 XML doc。**

## 理由

1. **后果不对等**：no-op 语义把「嵌套」这一调用方错误变成**静默的原子性破坏**（数据正确性面），
   fail-fast 把它变成立刻可见的异常。三栈中已有 fail-fast 的既有契约可对齐（Dapper ITM-088）。
2. **无内部调用方依赖 no-op**（见背景证据）—— 收敛的破坏面仅限外部用户中「有意嵌套」的用法，
   而那正是本 ADR 要消除的用法。
3. **不涉接口面变更**：三栈均可在实现内检出「事务已活动」（EF Core 用
   `Database.CurrentTransaction`，PalORM 用 `_transaction`），故无需给 `IUnitOfWork` 增加
   `HasActiveTransaction` 成员 —— 与 ADR-019 理由 4「接口面保持最小、抑制抽象生长压力」一致。

## 备选方案（未采纳的理由）

- **B. 统一为幂等 no-op**（对齐 PalORM 现有注释）：需让内层 Commit 不提交外层事务，而这要求
  实现区分「我开启的事务」与「外层事务」（需引入事务深度或归属令牌）——复杂度高于 fail-fast，
  且把静默行为正当化，与 Dapper 已落地的 ITM-088 方向相反。
- **C. 仅文档化**：把三栈分歧写进 doc 而不收敛。成本最低，但保留了两栈上的原子性破坏路径 ——
  文档不能阻止调用方犯错。
- **D. 给接口加 `HasActiveTransaction`、由扩展方法守卫**：能让 `ExecuteInTransactionAsync` 自身
  fail-fast（不依赖各实现），但扩大接口面，与 ADR-019 的取舍冲突；若选此项须同窗口修订
  ADR-019 并评估三栈实现面。

## 触发重评条件

- 出现「有意嵌套 + 期望加入外层事务」的真实用户需求（如 savepoint 语义）→ 重估方案 B/D。
- v3.0 窗口执行 `IPalOutboxStore` 异步化与跨栈 fencing 契约统一时（ADR-020 决策 2）——
  同窗口的破坏性变更可一并评估，避免两次 major 震动。

## 后果（若采纳）

- EF Core 与 PalORM 栈的嵌套调用从「静默提交外层」变为抛异常；两个包的 Release note 须列为
  行为变更，`IUnitOfWork.cs` 的 doc 同步补嵌套语义段（三方一致红线）。
- 测试面：三栈各补一条「事务已活动时 Begin 抛出」的对称测试（对齐姊妹对称守卫）。
- 本仓 dialect-probe / 集成测试若存在嵌套用法需同步调整（当前 grep 未发现）。
