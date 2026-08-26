# 全量验证轮报告 v10（3b16212 修复轮后的敌对复核）

> 基线：3b16212 · 日期：2026-08-26 · 类型：验证轮（全量档 + 修复点敌对焦点）
> 链路：v8 评审 → 05dc469 修复 → v9 验证 → 3b16212 修复 → **本轮 v10 验证**

## 1. 覆盖度声明

- 逐行覆盖：176 文件（片 A 32+测试 / B 38+测试 / C 35+测试 / D 60 / E 50+测试），五片零跳读。
- 机械轴全绿：gate 24/24、test-gate、doc 10/10、弱断言 153<173、verify-ai-system 21/21、Core 246。
- 方言轴：SKIP（双库握手超时持续，环境性；降级路径工作正常）。
- 截断事故文件完整性（主线程）：EndpointExtensions 相对 05dc469 净变更恰好 +10 行（cmd-null×2），恢复无丢失；片 E 全文 256 行复核。

## 2. 3b16212 敌对复核：全部修复项通过

| 片 | 项 | 结论 |
|----|----|------|
| A×4 | SafeObserveCompensation（与三兄弟逐字同构、OCE 语义对齐、无第二处直调漏改）/ SagaStep remarks 挪位 / RetryBackoffPolicy 校验（默认路径零误拦）/ 观察者回归测试（AsyncLocal 作用域真实触发、Count 强断言有效） | 全过 |
| B×5 | : IEventLog 恢复 + 契约测试 / SqlTemplates 两勘正 / BulkCopy 措辞 / Sqlite 勘正 | 全过（grep 无同型吞声明残留） |
| C×3 | P2-2 无回归（守卫未复活、测试语义未漂）/ 同型风险零命中 / 七流零发现 | 全过 |
| D×2 | csproj 计数勘正 / readonly 三处形式达成 | 过 / **内容失实（→P2-1）** |
| E×5 | 事故文件完整 / Rabbit 幂等门（嵌套订阅不需门有论证）/ FromHeaders / E4 / E6 测试 | 全过 |

## 3. 发现汇总（主线程终审）：P0=0 · P1=0 · **P2×1** · P3×15

### P2-1（readonly 失实链——探针+主线程双实证）

`Attributes.cs:24-27` + `IdentityGenerator.cs:32`（PALID002 消息）：三方一致的文本声称"省略 readonly 会引发 partial 修饰符不匹配的 CS 编译错误"——**编译探针证伪**（片 D 三场景 + 主线程独立复核：用户 `partial record struct` 无 readonly + 生成物 `readonly partial record struct` = **编译成功 0 警告**；C# 规则=readonly 出现在任一 partial 部分即整体 readonly）。真实约束：用户部分声明非 readonly 实例字段 → CS8340（指向字段而非修饰符）。v8 引入 → v9 强化 → 3b16212 继续传播的未验证语言规则断言链。修复：doc 与 PALID002 消息改口（可省 readonly、不得有非 readonly 实例字段、推荐带 readonly）。

### P3×15（按族）

- 幻影标注/措辞失实族（7）：DapperStoreTests 三处"[DapperAot(false)]"注释 / CS0311 错码注释（实报 CS0029）/ DapperOutboxStore:102 + DapperInboxStore 头部"Dapper.AOT 拦截器"措辞与诚实声明矛盾 / SqliteRowFactory+SqliteTypeHandlers 行号漂移 / PostgreSqlPipeline 无事务挂接未声明
- 姊妹不对称族（4）：瞬时 DbUpdateException 幽灵 Added 残留三处（对比三十八轮已修两处）/ SqliteException 分支 IsNullOrEmpty 缺两处 / NullMessageBroker 泛型 Publish 无 null 守卫 / Saga.RetryBackoffPolicy protected set 无防护（Outbox 侧已拦）
- 传感器欠账（1）：双 Broker DisposeAsync 幂等门无回归测试
- 观察项（3）：DomainEventDispatcher 尾部死 throw / IdempotencyDbContext GetAsync 跟踪查询 / CompensateExecutedStepsAsync 重读 Current 形态

## 4. 趋势

三循环收敛轨迹（P2：3→2→1；P1：0→1→0）：v8 的 P1（注释吞接口）为修复引入、v10 的 P2（readonly 失实）为评审自身未验证断言——**缺陷源已从"代码"移向"声明的真实性"**。片 C 流程建议采纳：片交集核对用 --name-only 禁用 --stat（路径截断漏报实证）。
