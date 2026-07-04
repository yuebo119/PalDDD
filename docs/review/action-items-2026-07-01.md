# Pal.DDD 评审改进任务清单 v4（2026-07-01）

> 来源：`docs/review/audit-2026-07-01-v8.md` 独立深度评审（code-explorer 子代理 57 源文件逐文件分析 + grep 验证 + 架构测试审查）
> 前序：`docs/review/action-items-2026-06-30.md`（ITM-031~041，P1/P2 全部落地，ITM-041 待 .NET 11 GA）
> 用法：每项含 ID / 优先级 / 维度 / 问题 / 建议 / 风险 / 验证 / 涉及文件。完成后将 `[ ]` 改为 `[x]` 并填完成日期与 commit。
> 优先级：**危害 × 复杂度** 双维度（P0 紧急 / P1 近期 / P2 / P3 / 评估）
> 立场：每项均标注风险与取舍，避免投机抽象；"评估类"任务不要求必做，只要求给出结论 ADR。

---

## 评审基线快照（2026-07-01）

| 指标 | 实测值 | 证据 |
|------|--------|------|
| 源项目数 | 31 | `Get-ChildItem src -Recurse -Filter *.csproj` |
| 测试项目数 | 15 | `Get-ChildItem test -Recurse -Filter *.csproj` |
| 源文件数 | 168 | 基线快照值 |
| 架构边界测试 | 711 行 / 25+ 测试方法 | `ArchitectureBoundaryTests.cs` read_file 实测 |
| DDD 合规 | 6/6 通过 | grep + csproj 验证 |
| 零反射验证 | 10 处匹配全注释 | search_content grep 实测 |
| IRepository<T> | 0 匹配 | search_content grep 实测 |
| async void | 0 匹配 | search_content grep 实测 |
| 本次发现 | 0 P0 / 0 P1 / 0 P2 / 6 观察项 | audit-2026-07-01-v8.md |
| 综合评分 | 8.6/10（无保留） | audit-2026-07-01-v8.md |

---

## P3 — 计划修复（低危害 · 易修复，文档级）

### [x] ITM-055 · Saga 补偿顺序注释偏差修正 · 2026-07-01
- **维度**：可读性 / 可维护性
- **优先级**：P3 · 危害: 低 · 复杂度: 易（< 1h，纯注释）
- **问题**：`SagaCompensation.cs:56-66` 构建补偿列表时，`failedStep` 被追加到 `targets` 末尾，Backward 策略从 `targets.Count-1` 逆序执行，导致**失败步骤第一个被补偿**，然后才是已执行步骤的逆序。实际语义为"失败步骤优先 → 已执行步骤逆序"，但注释描述为"补偿按实际已执行步骤的逆序执行"，存在微妙偏差。
  - **可信度 ⚠**：基于 code-explorer 子代理分析，输出截断，建议实施前 read_file 确认完整方法体。
- **建议**：更新 `SagaCompensation.cs` 中补偿列表构建处的注释，明确补偿顺序为"失败步骤优先 → 已执行步骤逆序"，并说明语义合理性（失败的步骤可能部分执行，应优先回滚）。
- **风险**：极低（纯注释，无行为变更）
- **验证**：
  - `dotnet build PalDDD.slnx` 零警告
  - read_file 确认 `SagaCompensation.cs:56-66` 完整方法体后修改注释
- **涉及文件**：
  - `src/PalDDD.Transactions/SagaCompensation.cs`（补偿列表构建处注释，行 56-66 附近）

---

### [x] ITM-056 · Saga 补偿动作 Outbox 提示文档补充 · 2026-07-01
- **维度**：可读性 / 健壮性（语义安全责任标注）
- **优先级**：P3 · 危害: 低 · 复杂度: 易（< 1h，纯 XML doc）
- **问题**：`SagaCompensation.cs` 中补偿动作 `step.CompensateAsync` 是直接委托调用，未经过 Outbox。若补偿需发布事件（如"库存已恢复"），补偿委托的实现者需自行将事件写入 Outbox。框架层面未强制补偿动作通过 Outbox 发布，也**未在 XML doc 中提示此责任**，实现者可能遗漏导致补偿事件丢失。
  - **可信度 ⚠**：基于子代理分析，建议实施前 read_file 确认 `SagaStep` 定义。
- **建议**：在 `SagaStep` 的 `CompensateAsync` 属性的 XML doc 中增加 `<remarks>`：
  ```xml
  /// <remarks>⚠️ 补偿动作若需发布领域事件，应通过 <see cref="IOutboxStore"/> 写入 Outbox
  /// 以保证至少一次语义。框架不强制补偿路径的事件发布策略。</remarks>
  ```
- **风险**：极低（纯文档标注，无行为变更）
- **验证**：
  - ✅ 类名 `SagaStep` 已 grep 验证存在于 `src/PalDDD.Transactions/SagaStep.cs`
  - ✅ 接口名 `IOutboxStore` 已 grep 验证存在于 `src/PalDDD.Transactions/OutboxStore.cs`
  - `dotnet build PalDDD.slnx` 零警告
- **涉及文件**：
  - `src/PalDDD.Transactions/SagaStep.cs`（`CompensateAsync` 属性的 XML doc）

---

### [x] ITM-057 · Entity<TId>.Id 的 init setter ORM 兼容性注释 · 2026-07-01
- **维度**：可读性 / 兼容性
- **优先级**：P3 · 危害: 低 · 复杂度: 易（< 1h，纯 XML doc）
- **问题**：`Entity.cs:68` `public TId Id { get; init; }` 使用 `init` setter。EF Core 8+ 支持 init 属性设置，但其他 ORM 或序列化框架可能无法设置 init 属性。框架双 ORM 策略中 Dapper 不依赖属性设置器（直接 SQL 映射），实际风险低，但**未在 XML doc 中注明 ORM 要求**。
  - **可信度 ⚠**：基于子代理分析，建议实施前 read_file 确认行号。
- **建议**：在 `Entity<TId>` 的 `Id` 属性 XML doc 中增加 `<remarks>`：
  ```xml
  /// <remarks>Id 使用 <c>init</c> setter，要求 ORM 支持 init 属性设置。
  /// EF Core 8+ 兼容；Dapper 通过直接 SQL 映射不依赖此 setter。</remarks>
  ```
- **风险**：极低（纯文档标注）
- **验证**：
  - ✅ 类名 `Entity` 已 grep 验证存在于 `src/PalDDD.Core/Entity.cs`
  - `dotnet build PalDDD.slnx` 零警告
- **涉及文件**：
  - `src/PalDDD.Core/Entity.cs`（`Entity<TId>.Id` 属性的 XML doc，行 68 附近）

---

## 评估类 — 长期演化/设计讨论（产出 ADR 结论即可）

### [x] ITM-058 · Saga 实例 freeze 机制评估 · ADR-015 结论：维持现状 · 2026-07-01
- **维度**：健壮性 / 可维护性
- **优先级**：评估 · 危害: 低 · 复杂度: 中（1-4h，需设计讨论）
- **问题**：`Saga.cs:108-109` 的 `GetFrozen()` 中 `_frozen ??= _stepsByKey.ToFrozenDictionary(...)` 非原子操作。若两个线程同时首次调用 `ProcessEventAsync`，可能同时构建 FrozenDictionary。文档已明确"在构造函数中注册，运行时只读"——这是设计约束而非缺陷（Saga 实例通常 Scoped 生命周期，单请求内不并发），但**缺少编译期或运行时守护防止误用**。
  - **可信度 ⚠**：基于子代理分析，建议评估前 read_file 确认 `Saga.cs` 完整上下文。
- **建议**：**评估类任务，不要求必做**。产出 ADR 结论：
  - 方案 A（推荐，若实施）：在 `ProcessEventAsync` 首次调用后 freeze 整个 Saga 实例（不仅冻结字典），后续 `When()` 注册抛 `InvalidOperationException`，提示"步骤必须在构造函数中注册"
  - 方案 B（维持现状）：文档已说明约束，Saga Scoped 生命周期保证单请求内不并发，额外守护增加复杂度
  - 决策依据：是否有真实误用场景、是否影响 API 易用性
- **风险**：中（若选择方案 A，需确保现有测试和 samples 不在构造函数外注册步骤）
- **验证**：
  - ✅ 类名 `Saga` 已 grep 验证存在于 `src/PalDDD.Transactions/Saga.cs`
  - ✅ 方法名 `ProcessEventAsync` 已 grep 验证存在
  - 产出 `docs/decisions/015-*.md` ADR
- **涉及文件**（若选择方案 A）：
  - `src/PalDDD.Transactions/Saga.cs`（`When()` 方法增加 freeze 检查）
  - `docs/decisions/`（新增 ADR-015）

---

### [x] ITM-059 · IValueObject 校验契约评估 · ADR-016 结论：维持现状 · 2026-07-01
- **维度**：可扩展性 / 健壮性
- **优先级**：评估 · 危害: 低 · 复杂度: 中（1-4h，需设计讨论）
- **问题**：`ValueObject.cs:59` 的 `ValueObject(T value)` 构造函数不做任何验证，完全依赖派生类型。文档已说明"业务值域约束应由派生类型自行实现"——合理，但 `IValueObject` 接口**缺少 `Validate()` 契约方法**供派生类型约定校验逻辑。
  - **可信度 ⚠**：基于子代理分析，建议评估前 read_file 确认 `IValueObject.cs`。
- **建议**：**评估类任务，不要求必做**。产出 ADR 结论：
  - 方案 A（推荐，若实施）：在 `IValueObject` 增加 `bool IsValid()` 或在 `ValueObject<T>` 构造函数接受可选 `Validator<T>` 委托
  - 方案 B（维持现状）：派生类型在构造函数中自校验，基类不强制——当前约定可接受
  - 决策依据：现有派生类型是否普遍需要校验、基类强制校验是否过度抽象
- **风险**：中（若选择方案 A，需更新所有 `IValueObject` 实现类；可能违反 YAGNI）
- **验证**：
  - ✅ 接口名 `IValueObject` 已 grep 验证存在于 `src/PalDDD.Core/IValueObject.cs`
  - ✅ 类名 `ValueObject` 已 grep 验证存在于 `src/PalDDD.Core/ValueObject.cs`
  - 产出 `docs/decisions/016-*.md` ADR
- **涉及文件**（若选择方案 A）：
  - `src/PalDDD.Core/IValueObject.cs`（接口增加 `IsValid()` 方法）
  - `src/PalDDD.Core/ValueObject.cs`（基类实现）
  - `docs/decisions/`（新增 ADR-016）

---

### [ ] ITM-060 · .NET 11 RC 阶段破坏性变更预验证 · 可信度 ❓
- **维度**：兼容性 / 可维护性
- **优先级**：评估 · 危害: 中 · 复杂度: 难（> 4h，需 RC 发布后执行）
- **问题**：`global.json` 锁定 `11.0.100-preview.5`，`Directory.Build.props` 启用 `<Features>runtime-async=on</Features>`。框架依赖 .NET 11 Preview 的 `static abstract`、`JsonSerializerContext` 源生成增强、`runtime-async`、`StackTraceLineNumberSupport` 等特性。Preview API 可能在 RTM 前发生破坏性变更。ADR-013 已记录迁移计划，ITM-041 待 GA 后运行 Benchmark，但**未规划 RC 阶段的预验证**。
  - **可信度 ❓**：需 .NET 11 RC 发布后实测验证，当前无法确认破坏性变更范围。
- **建议**：**评估类任务**。在 .NET 11 RC（预计 2026 Q3）发布后：
  1. 升级 `global.json` SDK 版本至 RC
  2. `dotnet build PalDDD.slnx` 验证编译
  3. `dotnet test PalDDD.slnx` 验证测试通过
  4. 若有破坏性变更，记录到 ADR-013 并制定迁移方案
  5. 与 ITM-041（Benchmark GA 后运行）联动
- **风险**：低（RC 阶段执行，无时间压力；Preview→RC 通常比 Preview→Preview 更稳定）
- **验证**：
  - ✅ `global.json` 内容已 read_file 验证
  - ✅ `runtime-async` 特性已 grep 验证存在于 `Directory.Build.props`
  - RC 发布后 `dotnet build` + `dotnet test` 零失败
- **涉及文件**：
  - `global.json`（SDK 版本升级，参考）
  - `docs/decisions/013-*.md`（ADR-013 迁移计划更新）

---

## 任务依赖与执行顺序

```
ITM-055 (Saga 补偿注释) ──> 独立，可与 ITM-056 同 commit（文档批次）
ITM-056 (Saga Outbox 文档) ──> 独立
ITM-057 (Entity.Id 注释) ──> 独立，可与 ITM-055/056 同 commit（文档批次）

ITM-058 (Saga freeze 评估) ──> 评估类，独立，产出 ADR-015
ITM-059 (IValueObject 评估) ──> 评估类，独立，产出 ADR-016

ITM-060 (.NET 11 RC 验证) ──> 联动 ITM-041（Benchmark GA 后运行）
                          ──> 待 .NET 11 RC 发布
```

**建议执行批次**：
1. **批次 1**（P3 文档批次，1 个 commit）：ITM-055 + ITM-056 + ITM-057（均为纯注释/XML doc，可合并）
2. **批次 2**（评估类，按需）：ITM-058 + ITM-059 产出 ADR-015/016 结论
3. **批次 3**（时间线项，待 RC）：ITM-060 与 ITM-041 联动，.NET 11 RC 发布后执行

---

## 完成标准

- [ ] 所有 P3 项（ITM-055、056、057）`[x]` 并附日期
- [ ] 评估类项（ITM-058、059）产出 ADR 结论（ADR-015/016）
- [ ] 时间线项（ITM-060）待 .NET 11 RC 发布后执行（联动 ITM-041）
- [ ] 最终 `dotnet build PalDDD.slnx` 零错误零警告
- [ ] 最终 `dotnet test PalDDD.slnx` 零失败
- [ ] `bash scripts/verify-conventions.sh` 通过

---

## 外部任务合并检查清单

> 本次任务清单源自 `audit-2026-07-01-v8.md` 评审报告，所有方法名/类名/路径已执行交叉验证：

- [x] ITM-055 `SagaCompensation.cs` → ✅ 已 grep 验证存在
- [x] ITM-056 `SagaStep` 类 → ✅ 已 grep 验证存在于 `src/PalDDD.Transactions/SagaStep.cs`
- [x] ITM-056 `IOutboxStore` 接口 → ✅ 已 grep 验证存在于 `src/PalDDD.Transactions/OutboxStore.cs`
- [x] ITM-057 `Entity` 类 → ✅ 已 grep 验证存在于 `src/PalDDD.Core/Entity.cs`
- [x] ITM-058 `Saga` 类 / `ProcessEventAsync` 方法 → ✅ 已 grep 验证存在
- [x] ITM-059 `IValueObject` 接口 / `ValueObject` 类 → ✅ 已 grep 验证存在
- [x] ITM-060 `global.json` / `runtime-async` → ✅ 已 read_file 验证

**交叉验证结果**：✅ 全部通过，无方法名/类名/路径偏差（历史教训 ITM-039 已防范）。
