# Pal.DDD 第三十二轮修复清单

> 来源：`docs/review/audit-2026-08-18-v3.md`
> 基线：外仓 `20aba6d`，AI 系统 `51e781b`
> 原则：先写失败测试或坏输入探针，再做单变量修复；P0/P1 完成前不得宣称全绿或 Native AOT 兼容。

## P0

### [ ] ITM-208 · 阻止测试与方言探针误删共享数据库
- **范围**：`.ai/scripts/dialect-probe.sh`、`test/PalDDD.PalORM.Tests/MultiDialectFixture.cs`、`test/PalDDD.Testing/TestEnvironment.cs`
- **问题**：固定库名/表名直接 DROP，缺少测试环境所有权证明和显式破坏确认。
- **修复**：随机会话库或 schema；写入 ownership marker；执行 DROP 前校验专用前缀、marker、环境类型与显式 `--allow-destructive-probe`；配置文件存在但解析错误时 fail-closed。
- **验证**：错误目标、无 marker、无确认参数三种情况必须 exit non-zero 且不执行 SQL；唯一测试目标可完整创建、运行、清理。

## P1 生产与发布

### [ ] ITM-209 · 修复 Native AOT `IL2075` 并建立真实运行门禁
- **范围**：`src/PalDDD.Transactions/DefaultSagaManager.cs:153`、两个 AOT sample、CI、AOT 文档。
- **修复**：消除或隔离 `GetType().GetProperty` 动态访问；restore/publish 均传 `PublishAot=true`、`SelfContained=true`、RID；不得以 NoWarn 作为兼容证明。
- **验证**：原生产物存在且不携带 CoreCLR runtime 布局；运行退出 0；AOT warning 0。

### [ ] ITM-210 · 给 EF/PalORM 租约终态写加入 fencing
- **范围**：EF Outbox MarkProcessed/MarkDead；PalORM Outbox/Inbox/Saga。
- **修复**：租约获取原子推进单调 LeaseVersion/FencingToken；完成、失败、释放均匹配 token；PG PalORM Outbox 子查询加 `FOR UPDATE SKIP LOCKED`。
- **验证**：双连接屏障并发；旧 worker 终态写影响 0 行，新 owner 状态不变；同批消息只被一个 worker 获租。

### [ ] ITM-211 · 隔离 Projection 完成确认失败
- **范围**：`ProjectionProcessor.ProcessAsync`。
- **修复**：handler 失败与 MarkCompleted 失败分离；完成确认失败不得调用 MarkFailed；记录 pending-confirmation 事件并保持 at-least-once 契约可见。
- **验证**：MarkCompleted 抛异常时 handler 仅一次、MarkFailed 零调用、返回/异常语义与文档一致。

### [ ] ITM-212 · Observer 异常不得重放 Saga 业务步骤
- **范围**：Normal/FanOut/ChildSaga/Dynamic/补偿观察路径。
- **修复**：`ISagaEventSink` best-effort 边界 no-throw；观测异常单独记录，不进入业务 retry/compensation。
- **验证**：每类 step 在 Sink 抛错时业务执行次数为 1，最终 Saga 状态由业务结果决定。

### [ ] ITM-213 · 建立 RabbitMQ 可靠发布模式
- **范围**：`RabbitMqBroker`。
- **修复**：独占 publish channel；publisher confirm；mandatory 发布标志设为 true；处理 basic.return；可配置 QoS；明确 durable topology 前提。
- **验证**：无绑定时发布失败；broker 关闭/confirm timeout 可观测；成功返回后消息能被绑定队列消费。

### [ ] ITM-214 · 修复 MySQL BulkCopy 二进制列损坏
- **范围**：`src/PalDDD.Dapper/DapperBulkCopy.cs` 的 MySQL 批量写入路径。
- **修复**：按提取值/列元数据创建强类型 DataColumn，不允许 `byte[]` 进入 string 列。
- **验证**：真实 MySQL 写入并读回 byte[] 序列完全一致；Ulid/时间/decimal 回归全过。

### [ ] ITM-215 · 修复 PostgreSQL JSONL/CSV 导出
- **范围**：`PostgreSqlReportHelper`。
- **修复**：每行独立或 Reset `Utf8JsonWriter` 并写换行；公式前缀先作用于逻辑值，再整体 CSV quoting；COPY 路径明确禁用用户字段或实现等价防护。
- **验证**：两行 JSONL 可逐行解析；`=1+1,x` 解析后仍为单列且文本化；COPY 导出公式值不执行。

### [ ] ITM-216 · 修复幂等配置与序列化失败状态
- **范围**：`IdempotencyPolicy`、三个 Store、`IdempotencyProcessor`。
- **修复**：强制 `Retention >= ProcessingTimeout`；serializeResult 失败不得返回 Executed 却留下 Processing，定义并持久化明确终态。
- **验证**：倒挂配置入口即失败；序列化失败后重试不再执行 handler。

### [ ] ITM-217 · Outbox 按存储版本解析消息
- **范围**：`OutboxBatchProcessor`、MessageCatalog/Evolution。
- **修复**：`Find(type, schemaVersion)`；校验 ContentType；旧版本显式走演进管线。
- **验证**：v1 payload 与 v2 同名共存时使用正确 descriptor；未知版本/格式进入受控 dead/retry 语义。

### [ ] ITM-218 · 限制 JSONL 单行与 JSON 复杂度
- **范围**：`EventStreamJsonLines`。
- **修复**：分块读取；最大行字节、payload、JSON depth；超限含行号错误。
- **验证**：无换行超长行、跨 buffer 超长行、深层 JSON 都在分配上限前拒绝。

### [ ] ITM-219 · Native 解压在分配前限制输出
- **范围**：LZ4/ZStandard/OpenZL adapter。
- **修复**：使用目标 Span 或分块 decoder，写入前检查剩余额度；统一超限异常。
- **验证**：恶意高压缩比输入的峰值分配受限，不只断言最终异常。

### [ ] ITM-220 · 重复命令/查询 Handler 启动期失败
- **范围**：`ServiceRegistration`、`HandlerRegistrar`、Dispatcher registry。
- **修复**：同请求不同 handler 抛包含双方类型的配置异常；相同 handler 重复注册保持幂等。
- **验证**：command/query 各一组冲突测试，不能出现“后注册者获胜”。

### [ ] ITM-221 · 修复分析器与 CodeFix 条件分叉
- **范围**：PDDD009/010/011/013。
- **修复**：名称/版本校验不依赖 BoundedContext；替换旧 `.vN` 后缀；CodeFix 从诊断属性或基类链获取 context。
- **验证**：无 BoundedContext 组合诊断、`.v2 -> .v1`、继承 context 三类测试。

### [ ] ITM-222 · 修复 Source Generator 不可编译输出与命名碰撞
- **范围**：Enum/MessageRegistry/Identity generator。
- **修复**：Enum 仅接收 TSelf 字段；泛型/不可访问消息明确诊断；hint/转换器名使用完整元数据名稳定编码或 hash。
- **验证**：合法输入必须检查 updatedCompilation 无 error；泛型、私有嵌套、非 TSelf、下划线碰撞测试全覆盖。

### [ ] ITM-223 · 允许 CQRS 可空查询结果
- **范围**：`Dispatcher.QueryAsync/SendAsync`。
- **修复**：按泛型可空契约返回，不能用统一 runtime null check 区分 `T` 与 `T?`。
- **验证**：`IQuery<OrderDto?>` 未找到返回 null；非空契约行为另用类型/验证表达。

### [ ] ITM-224 · 固化实体与领域事件身份
- **范围**：`Entity.Id`、`DomainEvent.EventId/OccurredOn`、事件链 owner。
- **修复**：身份 getter-only/受控构造；历史重放走专用工厂；事件实例跨实体/中链复用 fail-fast。
- **验证**：对象初始化器不能改身份；跨实体复用不截断原链。

### [ ] ITM-225 · 传播 Specification 动态代码要求
- **范围**：`Spec<T>.IsSatisfiedBy`。
- **修复**：公开 API 标注 `RequiresDynamicCode`，或把表达式查询规约与内存委托规约拆开。
- **验证**：Native AOT consumer 对不支持路径给编译警告或不包含动态编译。

### [ ] ITM-226 · 修复 EventLog Hi/Lo 回滚分叉
- **范围**：`EventLogPositionReserver` 与 `EventLogDbContext`。
- **修复**：区块预留独立提交后发布，或增加 commit/abort 语义，回滚不得继续使用未持久区间。
- **验证**：预留后外层回滚、同进程再提交、进程重启三阶段不重号。

### [ ] ITM-227 · 只清理拦截器实际注入的 Outbox 实体
- **范围**：`OutboxDomainEventInterceptor`。
- **修复**：记录本次注入引用/ID，失败只 detach 该集合。
- **验证**：第一次 SaveChanges 失败后重试；调用方 Outbox 保留，拦截器消息最终恰好一条。

### [ ] ITM-228 · 对齐 PalORM Store 数据、错误与事务契约
- **范围**：Saga JsonTypeInfo、MySQL INSERT IGNORE、raw command transaction。
- **修复**：缺 JsonTypeInfo fail-fast；普通 INSERT + 只捕获 duplicate key；所有 raw command 绑定活动事务。
- **验证**：业务字段完整往返；非法值不能静默调整；回滚后无 Store 副作用。

### [ ] ITM-229 · 修复遥测空指标与敏感高基数标签
- **范围**：`PalDiagnostics.cs`、README/tutorial。
- **修复**：7 个无记录 instrument 接入真实路径或删除；原始 id/key/stream/source 默认不写入 trace，改为低基数或 opt-in 脱敏 enrichment。
- **验证**：每个保留 instrument 有成功/失败/取消记录测试；默认 tag 不含原始业务 ID。

## P1 质量系统与测试可信度

### [ ] ITM-230 · 修复 gate-check fail-open
- **范围**：G4/G12/G18/G22。
- **修复**：预期目录/真源文件存在性硬门；逐 await 语法检查；可靠 DI 符号解析；G22 同时检查外仓与 `.ai`。
- **验证**：目录删除、裸 await、非法 AddX、`.ai` dirty 四种 mutation 均 exit non-zero。

### [ ] ITM-231 · 修复 AI 自检、test-gate、tech-debt fail-open
- **范围**：V1-V16、T-DEF-1/4、Python/Perl 子检查。
- **修复**：比较精确 ID 集合；WARN 铁律改 FAIL；捕获检查器退出码；根/AI 镜像都纳入语法门。
- **验证**：删除中间编号、删 PD32、坏 shell、坏 python、缺 timeout 全部必须失败。

### [ ] ITM-232 · 让 CI 和 review scope 覆盖双仓全集
- **范围**：CI、`review-scope.sh`、安装链。
- **修复**：fresh checkout 必须获得 pinned `.ai`；缺失即失败；以双 `git ls-files` 为全集，排除项需显式列理由和数量。
- **验证**：scope 与 556 文件补集为 0；fresh CI 实跑完整门禁。

### [ ] ITM-233 · 让格式与覆盖率真正阻断
- **范围**：`check-all.sh`、`ci-coverage.sh`、CI。
- **修复**：传播 format 原始退出码；清理旧覆盖产物；统一单一阈值；CI 合并后比较 line/branch。
- **验证**：当前 72 format 项必须使门禁失败；低于阈值的覆盖报告必须 exit non-zero。

### [ ] ITM-234 · 补强测试验证器
- **范围**：EF transaction/outbox、Public API snapshot、Broker、PalORM、Activity/Meter、生成器测试。
- **修复**：测试生产实现而非覆写替身；真实关系型 commit/rollback；禁止测试内自动改快照；双连接并发；精确顺序/标签/计数；检查生成 compilation diagnostics。
- **验证**：将关键生产方法替换为 no-op 时，对应套件必须失败。

## P2

### [ ] ITM-235 · 明确 Kafka 与 HITL 产品契约
- Kafka 保持 at-most-once 就写入公共契约；若要 at-least-once，关闭 auto offset store，handler/DLQ 成功后 StoreOffset。
- HITL 明确无限等待或增加 HumanDecisionDeadline；后者需单独扫描 AwaitingHumanDecision。

### [ ] ITM-236 · 同步文档与提示模板事实
- 统一 PalORM 5.2.0、977 测试、遥测名称/命名空间/instrument 类型。
- 修复教程与 `.pal` prompts 的不存在 API、不可访问重载、不可编译值对象和缺失属性。
- 把 prompt 代码块纳入编译冒烟。

### [ ] ITM-237 · 清理格式和断言债务
- 修复 45 个 IDE0005 与 27 个 whitespace 问题。
- 逐步把 152 个 IsNotNull 和 21 个零断言候选改为行为断言；识别 FsCheck 等合法无 Assert 测试。

### [ ] ITM-238 · 补资源、边界和性能契约
- Dapper 真流式 reader、连接异常释放、MySQL 每物理连接 session 配置。
- TestSession/ServiceProvider/async scope 生命周期；统一 leaseDuration/owner/ct 参数守卫。
- 性能声明必须有基准，禁止“零 GC”“仍然快”等无数据注释。

### [ ] ITM-239 · 修复 AI 辅助工具与安装链
- `verify-action-items` 只解析结构化验证锚点，并校验 file:line。
- probe template 未实现时 exit non-zero。
- 修复两条安装路径，安装后自检是成功条件。
- MTP 能力按 SDK 探测，删除永久“批量必 exit 5”结论。

### [ ] ITM-240 · 对齐压缩 wire format、NoWarn 和公共元数据
- OpenZL 必须使用 OpenZL API 或删除标识。
- 清理重复/无理由 NoWarn，Analyzer 加 release tracking。
- 补 public XML docs，修正 Base package dependency description。

### [ ] ITM-241 · 补齐跨 Provider 与高级路径矩阵
- PG/MySQL 双连接并发租约；完整 payload/metadata/audit/trace 往返。
- ChildSaga/Interrupt/Dynamic 成功、失败、取消、补偿。
- JSONL/CSV 超限与解析；Generator 增量缓存；Native AOT publish+run。

## 完成定义

1. ITM-208 完成前，不得运行指向非一次性隔离目标的方言/多方言清理。
2. 所有 P1 必须有 red-green 或 mutation 证据。
3. 全量 fresh restore/build/test 通过；测试 0 skipped。
4. Native AOT 两个样例 publish 并运行通过，0 AOT warning。
5. format 0 issue；覆盖率达到裁决后的唯一阈值。
6. AI 门禁 mutation 套件全部能拒绝坏输入。
7. `review-scope --all` 双仓补集为 0。
8. 代码、README/docs、XML/行内注释三方一致。
