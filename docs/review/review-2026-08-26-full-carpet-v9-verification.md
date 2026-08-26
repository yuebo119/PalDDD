# 全量验证轮报告 v9（05dc469 修复轮后的敌对复核）

> 基线：05dc469 · 日期：2026-08-26 · 类型：验证轮（全量档覆盖 + 修复点敌对焦点）
> 上轮：v8（dfbb135 评审 → 05dc469 修复）。本轮核心职责：修复质量验证 + 残留收索。

## 1. 覆盖度声明

- 逐行覆盖：176 手写 .cs 文件（片 A 32 + B 38 + C 37 + D 65 + E 50，~26,500 行），五片各自声明零跳读。
- 机械轴全绿：gate 24/24、test-gate、doc 10/10、弱断言 152<173（+2 为既有口径漂移，非新测试引入——清单核对无 FailureReasonTests 项）、Core 246 / DI 94。
- 方言轴：SKIP——双方言连接超时（环境性）；**降级修复实证生效**（同故障上轮程序崩溃、本轮各记 fail 完整退出——工具修复的真实闭环）。

## 2. 上轮修复敌对复核总表（15/16 项通过）

| 片 | 复核项 | 结论 |
|----|--------|------|
| A×4 | LeaseOwnerFactory 注释 / 补偿告警双路径 / Saga 重放语义成文 / MarkDead 构造串 | 全过（FanOut/ChildSaga 重试行为与成文一致；无第三处构造串遗漏） |
| B×6 | 触发器示例 / SqlTemplates remarks / BulkCopy Justification / AmbientTransaction DI 口径 / **DapperEventLog auto-open 声明** / AddMessagesAsync 窗口注释 | 5 过 + **1 项修复自身引入 P1（B-1）**——声明内容准确但载体破坏接口 |
| C×2 | **P2-2 幂等守卫移除**（四子问敌对）/ 双时钟声明 | 全过（未过期保护由 expires_at 条件承载；并发一个赢；三栈契约声明与 EFCore/InMemory 逐行比对属实；response_payload 显式置 NULL） |
| D×4 | 代理对防御四子问 / 零 GC 措辞 / IsNumeric SpecialType / CodeFixes csproj | 全过（SpecialType 是安全超集，消除自造 System.Int32 假阳性；六测试用例对拍一致） |
| E×3 | 畸形 JSON ProblemDetails / Kafka 超时声明 / Any 占位声明 | 全过（响应形态对齐；JsonContext 可序列化；但带出 E6 零测试覆盖） |

## 3. 发现汇总（主线程终审）：P0=0 · **P1×1** · P2×2 · P3×16

### P1（修复轮自身引入——验证轮的直接命中）

| # | 位置 | 缺陷 | 证据 |
|---|------|------|------|
| **P1-1** | `src/PalDDD.Dapper/DapperEventLog.cs:38` | 上轮给类声明加行内注释时 `: IEventLog` 被吞进注释行尾——**类不再实现 IEventLog 接口**。外部 `IEventLog log = new DapperEventLog(...)` CS0311 编译失败、DI 注册同炸。三重拦截缺口使 build 0/0 + 测试全绿未拦截：快照刻意不覆盖 Dapper 族、集成测试全用 var 具体类型、方法结构仍满足接口（事故非有意）。XML doc 仍称"实现 IEventLog"——三方不一致 | 主线程亲验：注释行尾 `...（事务场景连接必已 open） : IEventLog`；修复=注释移到 XML doc 或独立行，恢复 `: IEventLog`。探针：最小工程接口赋值编译探针（S3 双向）+ 补回归测试（`IEventLog log = new DapperEventLog(...)` 赋值断言） |

### P2（主线程亲验）

| # | 位置 | 缺陷 | 修复方向 |
|---|------|------|---------|
| P2-1 | `Saga/Saga.cs:818` | `OnCompensationStarted` 直 await 无隔离——观察者抛异常时**真实补偿被跳过**且误并入"compensation also failed"聚合（补偿未执行却报失败）。同文件 283/303/320 已有三个 SafeObserve* 隔离方法（ITM-212 族）——姊妹漏网 | 照 SafeObserve 族补 `SafeObserveCompensationStartedAsync`（catch 记日志不传播）；探针：抛错 Sink + 必失败步骤 + 补偿执行标志断言（当前红） |
| P2-2 | `SqlTemplates.cs:263` | SagaActive 注释仍称"与 Outbox 的**字符串状态**不同"——int 化残留第四处，与本文件 v8 新 remarks 直接矛盾 | 注释措辞同步 |

### P3×16（摘要）

- **修复轮同步残留**（4）：Attributes.cs doc 示例缺 readonly（D-1，三方一致第三处漏网）；CodeFixes 注释"5 个 provider"实为 4（D-2）；SqlTemplates remarks 幻影 StatusPending 常量引用（B-3）；BulkCopy 新 Justification 引用不存在的 [DapperAot(false)] 标注（B-4）
- **存量**（9）：AddPalOutbox 校验漏 RetryBackoffPolicy null（catch 内 NRE 逐 tick 中止）；cmd is null 裸 400 第三处残留（E1）；JsonException.Message 信息策略与 HandlerNotFound 收窄不一致（E2）；RabbitMqBroker.DisposeAsync 无幂等门（姊妹不对称）；Any 注释指向 internal Kind（E4）；FromHeaders 未知头返回全 null context（E5）；SqliteServiceCollectionExtensions [module:DapperAot] 失实（B-5）；ReleaseForRetry 无租约分支无 retry CAS（不对称）；EFCore MarkProcessing payload 清空依赖片外实现（S2）
- **测试缺口**（2）：CreateInvalidBody 零测试（E6）；EventData 写入路径 2×ToArray（E7 观察）
- **挂靠微瑕**（1）：SagaStep 重放 remarks 挂在 FanOut/ChildSaga 恒 null 的 ExecuteAsync 属性上（陈述真但位置错位）

## 4. 趋势与教训

- 修复质量：15/16 通过（94%）——较历史 31%→8% 缺陷率进一步收敛，但 **P1-1 证明脚本化注释注入是真实风险形态**（机械 replace 不看行尾上下文），且现有防线（快照范围/测试类型习惯）对"接口声明被注释吞"这类语法级破坏零拦截——修复清单应含防线补强（快照扩 Dapper 族或接口赋值契约测试）。
- P2 级新发现 1 项为历史姊妹漏网（Saga 观察者隔离），非本轮引入。
- 验证轮协议价值再次实证：上轮全绿状态下 P1 静默存在，本轮敌对焦点（"假设修复有错"）直接命中。
