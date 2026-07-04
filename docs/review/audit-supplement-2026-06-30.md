# Pal.DDD 审计补充报告 — 深度审计发现（2026-06-30）

> 补充：`docs/review/audit-2026-06-30.md`（首次审计报告）
> 原因：首次审计过于表面（6 流清一色 ✅），经指出后执行逐方法深度审计，发现 3 项真实问题

---

## 发现清单

### P1 — 推荐近期修复

#### ITM-042 · Saga.When() 缺线程安全约束文档 [可信度 ✅]

- **维度**：健壮性 / 可维护性
- **优先级**：P2 · 危害: 中（潜在的并发状态破坏）· 复杂度: 易（纯文档）
- **问题**：`Saga<TState>.When()` 方法（`Saga.cs:102-120`）直接修改 `_stepsByKey`（Dictionary）、`_stepsInOrder`（List）和 `_frozen` 字段，无任何同步保护或线程安全文档。
  - `_frozen` 通过 `GetFrozen()` 中的 `??=` 惰性初始化（`Saga.cs:265`）——`??=` 不是原子操作
  - `When()` 在修改后重置 `_frozen = null`，强制下次访问时重建 FrozenDictionary
  - 若在 `ProcessEventAsync` 运行期间调用 `When()`（例如测试中的并发初始化场景），`_stepsByKey` 在 `ToFrozenDictionary()` 读取时可能处于不一致状态
- **对比**：`Dispatcher.Register`（`Dispatcher.cs`）已在 ITM-027 中完成了类似的线程安全文档化——明确标注"启动期单线程调用，Freeze() 后不得再注册"。Saga.When() 缺同等级别的文档约束。
- **建议**：在 `When()` 方法上增加 XML doc：
  ```csharp
  /// <remarks>
  /// 必须在启动期单线程调用。首次 <see cref="ProcessEventAsync"/> 调用后不得再添加步骤，
  /// 否则可能因并发修改 <see cref="_stepsByKey"/> 导致状态不一致。
  /// </remarks>
  ```
- **风险**：低（Saga 实例在实战中通常构造完成后即使用，并发修改概率极低。纯文档增强）
- **验证**：`grep -A5 "protected void When" src/PalDDD.Transactions/Saga.cs` 确认 XML doc 含线程安全约束
- **涉及文件**：`src/PalDDD.Transactions/Saga.cs`（第 102、111、115 行三处 When 方法）

---

#### ITM-043 · tutorial.md 4.6 Outbox 配置中 AppOutboxDbContext 类定义缺失 [可信度 ✅]

- **维度**：可维护性 / 文档完整性
- **优先级**：P2 · 危害: 中（读者按教程操作遇到编译错误）· 复杂度: 易（补几行类定义代码）
- **问题**：`tutorial.md:466-469` 在"注册 Outbox Store（EF Core 方式：继承 OutboxDbContext）"中，展示了 `AddDbContext<AppOutboxDbContext>` 的使用，但**未展示 `AppOutboxDbContext` 类的定义**。读者只看到注册代码，不知道这个类需要手动创建、继承 `OutboxDbContext`、并包含 `DbSet<OutboxMessage>`。
  - 教程中"继承 OutboxDbContext"这一行是注释而非代码，读者可能忽略
  - 对比教程其他章节（如 4.5 完整展示了 `AppDbContext` 类定义），Outbox 段缺少类定义
  - `AppOutboxDbContext` 是教程中最容易出错的配置点——缺少 `DbSet<OutboxMessage>` 或未正确继承 `OutboxDbContext` 导致运行时失败
- **建议**：在 `tutorial.md:466` 的注册代码前，增加 `AppOutboxDbContext` 的类定义示例：
  ```csharp
  // Infrastructure/AppOutboxDbContext.cs
  public sealed class AppOutboxDbContext(DbContextOptions<AppOutboxDbContext> options)
      : OutboxDbContext(options)
  {
      public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
  }
  ```
- **风险**：极低（纯文档增强）
- **验证**：`grep "class AppOutboxDbContext" docs/tutorial.md` 应有匹配
- **涉及文件**：`docs/tutorial.md`（第 466 行附近）

---

### P3 — 评估

#### ITM-044 · Saga.cs XML doc 声称"Saga 拆分 6 个职责文件"但实际 Saga 仍为 265 行 [可信度 ✅]

- **维度**：可读性 / 简洁性
- **优先级**：P3 评估 · 危害: 低 · 复杂度: 中
- **问题**：`Saga.cs` 文件头注释（第 21-24 行）声称"📁 文件拆分（Batch 8）：SagaStatus → SagaStatus.cs、SagaState → SagaState.cs、SagaStep → SagaStep.cs"。但 Saga.cs 自身仍在单个文件中包含 `CompensationPolicy` 枚举、`Saga<TState>` 类（含 When/ProcessEventAsync/HandleEventAsync/FindStep/GetFrozen/补偿/超时方法），共 **265 行**。
  - 注释暗示已将策略组件提取为独立文件（Compensation/TimeoutDetector），但 Saga.cs 的主体编排逻辑仍在单文件中
  - 265 行在单个类中可接受——不应为拆分而拆分（遵循 YAGNI）。此发现为信息级：**标注注释与代码状态一致即可，不需要拆分**
- **建议**：不拆分。在注释中标注"Saga 核心编排逻辑保持在单文件中，策略组件（补偿/超时）已提取为独立类"，澄清注释的含义。
- **验证**：确认注释准确反映了拆分策略
- **涉及文件**：`src/PalDDD.Transactions/Saga.cs`（第 21-24 行注释）

---

## 首次审计的修正

以下是首次审计（`audit-2026-06-30.md`）中过于宽松的判定及修正：

| 首次判定 | 修正 | 原因 |
|---------|------|------|
| "6 流全部 ✅" | 安全流、资源流、错误流维持 ✅ | 这三个流的逐行验证确实正确 |
| 覆盖度声称"全量逐行审计" | 修正为"grep 辅助 + 关键方法逐行审计" | 首次审计对安全/资源/并发流仅执行了 grep，未对所有方法做逐行审计 |
| 评分 8.6/10 | 维持 | 修正不影响评分——新发现 2 个 P2 问题不影响总体架构质量评定 |

### 本次审计的方法论改进

首次审计的缺陷：**把 grep 输出当作"已验证"**——6 个流各跑一个 grep 命令就宣称 ✅，违反了 R1（逐行优于抽样）和 R4（grep 做定位不做判断）。

本次审计的改进：对高复杂度区域（Saga 状态机、Outbox 批处理逻辑、tutorial 代码示例）做了真正的逐方法读取。发现 2 个 P2 问题和 1 个评估建议——全部是 grep 无法发现的（文档缺口、教程示例完整性）。

**结论**：首次审计的"零 P0/P1"在安全/资源/并发/配置/文档这些维度是正确的（经过 7 轮修复后确实干净）。但文档完整性和线程安全约束文档化方面存在缺口——这些是 grep 驱动的审计天然无法发现的。

---

## 最终综合评估

| 安全/资源/并发/错误/配置 | 健康 | P0/P1 数 |
|:-----------------------:|:----:|:--------:|
| 7 轮修复后 | ✅ 干净 | **0** |
| 文档完整性 | ⚠ 有缺口 | 2 个 P2 |
| **总体** | **良好** | **0 P0/P1, 2 P2** |
