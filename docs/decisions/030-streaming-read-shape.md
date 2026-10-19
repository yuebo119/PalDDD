# ADR 030：流式读形态（IStreamQuery / IStreamQueryHandler + SSE 端点原语）

> 状态：**已采纳（2026-10-18 完整实施轮，D1-D8 全部落地；实现清单与验证计划逐项闭环，Phase 2 触发式项未启动）**
> 日期：2026-10-17
> 关联：ADR-021（无共享处理器管道基类）、EventBus 移除判决（docs/architecture.md:219、docs/pitfalls.md D6、提交 7a332d8）、docs/features.md:109（Inbox 幂等语义）、ITM-220（重复 handler 注册快速失败先例）

## 背景

agent 场景的核心形态是 LLM 推理的增量产出流式回传客户端（token 流）。PalDDD 现状实证：

- 分发层对响应类型零约束，`IQuery<IAsyncEnumerable<T>>` 可编译可跑（src/PalDDD.CQRS/IRequest.cs、src/PalDDD.CQRS/QueryHandler.cs），但命令与查询都只有单值响应契约；
- 端点层无流式原语：EndpointExtensions 仅 MapCommand×2 与 MapQuery（src/PalDDD.Hosting.AspNetCore/AspNetCore/EndpointExtensions.cs:115-180）；
- 消息层进程内 EventBus 已移除，统一 Outbox（docs/architecture.md:140/219）；单节点模式 NullMessageBroker 丢弃发布（src/PalDDD.Messaging/MessageBroker.cs:87）。

核心张力：Dispatcher 的 per-request scope 随管道完成销毁（src/PalDDD.CQRS/Dispatcher.cs:270-273），而 IAsyncEnumerable 的枚举晚于销毁。经 dispatcher 路由的流在枚举期触碰任何 scoped 依赖（仓储、DbContext、流结束追加 EventLog）即 ObjectDisposedException，且故障形态取决于 handler 体内触碰什么，属运行期地雷。

## 决策

**D1 独立读形态**。新增 `IStreamQuery<TItem>`（独立标记，不继承 IBaseRequest，不走 dispatcher）与 `IStreamQueryHandler<TQuery, TItem>`。不复用 `IQuery<IAsyncEnumerable<TItem>>`：该形态把流降格为普通返回类型，ct 语义与生命周期约定无处安放，且诱导经 dispatcher 路由踩中上述地雷。

**D2 端点即作用域边界**。流式端点从 `context.RequestServices` 解析 handler 并在端点方法内完成枚举，请求作用域天然覆盖全枚举期。成文契约：流式枚举禁止离开端点执行期；禁止经 `QueryAsync` 路由流式查询。

**D3 验证前置与原语抽取**。端点在 SSE 头写出前解析 `IEnumerable<IPalValidator<TQuery>>` 跑验证，失败走既有 `WriteValidationProblemAsync`（400 ProblemDetails，与 MapCommand/MapQuery 同形态）。验证逻辑抽 `internal static PalValidatorRunner`（PalDDD.CQRS 内，经仓内既有 IVT 链对 PalDDD.Hosting.AspNetCore 可见），ValidationBehavior 泛型/非泛型两条路径与本端点三处共用，含「IsValid=false 且 Errors 为空时构造占位条目」语义（v25/v48 两轮勘正定案）单出处。

**D4 SSE 帧最小化加可定制**。默认帧为 data-only JSON（framing 字节段经 `Response.BodyWriter` span 直写；序列化经 ThreadStatic 池化 `Utf8JsonWriter`（P1，2026-10-18，稳态每项零堆分配，实测 56B→0B/项，复用 JsonMessageSerializer 的 R8/ADR-029 形态），默认路径 framing 与序列化双零分配）；`MapStreamQuery` 提供可选 `Func<TItem, string> formatFrame` 委托，用户可返回完整帧文本（event/id 行）以支持分型事件流。选委托不选接口：零 DI、零新概念、闭合泛型 AOT 安全。

**D5 取消贯穿**。handler 契约参数带 `[EnumeratorCancellation]`；端点传 `context.RequestAborted`。客户端断开时取消传播进 handler 的 finally（停计费、释放 provider 连接）。契约要求 stream handler 必须响应 ct。

**D6 审计与生命周期日志分工**。响应内容审计由 handler 在流结束后追加 EventLog（框架不自动捕获载荷：隐私、体积、领域语义三样均属应用层）；框架负责机械部分：端点原语记录流开始/结束/item 数/错误，`IPalLogger<T>` 门面加内部标记类别，`IsEnabled` 门控加 `TimeProvider` 可注入，与 LoggingBehavior 语义对齐。

**D7 注册与重复检测**。`AddPalStreamQueryHandler<TQuery, TItem, THandler>`：`TryAddScoped<THandler>` 加 `TryAddScoped<IStreamQueryHandler<TQuery,TItem>, THandler>`，自动 `AddPalDDD`；同一 stream query 注册两个不同 THandler 时注册期快速失败（对齐 ITM-220 对命令/查询的裁决：静默覆盖是配置错误）。

**D8 端点双形态**。GET 形态（`bindQuery`，浏览器原生 EventSource 兼容）加 POST 形态（JSON body，承载 prompt 类请求体；浏览器侧需 fetch 加 ReadableStream 手工解析，usage.md 必须写明该约束）。

## API 设计

```csharp
// PalDDD.CQRS（新文件 StreamQueryHandler.cs）
public interface IStreamQuery<TItem> { }

public interface IStreamQueryHandler<TQuery, TItem>
    where TQuery : IStreamQuery<TItem>
{
    IAsyncEnumerable<TItem> HandleAsync(TQuery query, CancellationToken ct);
}

// PalDDD.DependencyInjection（ServiceRegistration.cs 增）
public static IServiceCollection AddPalStreamQueryHandler<TQuery, TItem, THandler>(
    this IServiceCollection services)
    where TQuery : CQRS.IStreamQuery<TItem>
    where THandler : class, CQRS.IStreamQueryHandler<TQuery, TItem>;

// PalDDD.Hosting.AspNetCore（EndpointExtensions.cs 增，两个重载）
public static IEndpointConventionBuilder MapStreamQuery<TQuery, TItem>(
    this IEndpointRouteBuilder endpoints, string pattern,
    Func<HttpContext, TQuery> bindQuery, JsonTypeInfo<TItem> itemJsonTypeInfo,
    Func<TItem, string>? formatFrame = null)
    where TQuery : CQRS.IStreamQuery<TItem>;

public static IEndpointConventionBuilder MapStreamQuery<TQuery, TItem>(
    this IEndpointRouteBuilder endpoints, string pattern,
    JsonTypeInfo<TQuery> queryJsonTypeInfo, JsonTypeInfo<TItem> itemJsonTypeInfo,
    Func<TItem, string>? formatFrame = null)
    where TQuery : CQRS.IStreamQuery<TItem>;
```

端点流程（两种形态同构）：绑定/反序列化 query（POST 复用 `ReadJsonBodyOrWriteErrorAsync` 泛化版，415/畸形 JSON/空 body 错误形态不变）→ `PalValidatorRunner` 验证，失败 400 且在头写出前 → 写 SSE 头（`text/event-stream; charset=utf-8`、`Cache-Control: no-cache`、`X-Accel-Buffering: no`）→ 从 `RequestServices` 取 handler → `await foreach` 枚举（ct 为 `RequestAborted`），默认帧零分配路径或 `formatFrame` 委托产出帧，逐项写并 flush → `OperationCanceledException` 静默收尾；其余中途异常补 `event: error` 帧后结束（头已写出，无法回退 500）。

## 被否方案

| 方案 | 否决理由 |
|---|---|
| 复用 `IQuery<IAsyncEnumerable<TItem>>` | scope 销毁时序地雷；「枚举前物化依赖」是不可强制约定，把正确性推给用户纪律 |
| dispatcher 集成（持有对象变体） | 消费者不枚举/不释放即泄漏 scope（IServiceScope 不可终结） |
| dispatcher 集成（调用方供 scope 变体） | 公共 API 暴露 provider 生命周期契约，非端点上下文传错即击穿正确性；Dispatcher 热路径类增二表与第二种委托形态。**触发式转向项** |
| 只给 handler 契约、端点用户自写 | 框架端点故事在 agent 最需要处断裂；错误语义与帧格式分化 |
| `IStreamQuery<TItem> : IQuery<IAsyncEnumerable<TItem>>` 继承统一 | `AddPalQueryHandler` 重新编译通过，常规注册路径踩雷，需 analyzer 禁令堵；二元性与 BCL 的 Task/IAsyncEnumerable 同构，一句话规则可治理 |
| 统一流抽象（IEventStream 类） | IAsyncEnumerable 已是平台抽象，包一层是方言重写 |
| 预建 Channel 缓冲 | 背压原生存在于拉模式；生命周期与失败模式反增；扇出/迟附三形态未出现（触发式） |
| 预建 `IStreamPipelineBehavior` | MediatR 收场如此；出现流级横切真实需求时与 dispatcher 集成同批建，不单加 |

## 实现清单

| 位置 | 改动 |
|---|---|
| src/PalDDD.CQRS/StreamQueryHandler.cs | 新建，两个接口 |
| src/PalDDD.CQRS/PipelineBehaviors.cs | 抽 `PalValidatorRunner`，ValidationBehavior 两路径改用（内部重构，公共快照零变化） |
| src/PalDDD.DependencyInjection/ServiceRegistration.cs | `AddPalStreamQueryHandler`（含重复检测） |
| src/PalDDD.Hosting.AspNetCore/EndpointExtensions.cs | `MapStreamQuery` ×2、SSE 帧 helper、生命周期日志（内部标记类别） |
| samples/PalDDD.AotSample | 流式端点样本（喂 aot-verify CI） |
| docs/conventions.md | 流式读一节：入口规则、形态选择、ct 纪律、审计模式 |
| docs/usage.md | 完整示例（端点 + handler + JsonTypeInfo + EventSource/fetch 约束） |
| README.md:525 | ADR 计数 29 改 30 |
| CHANGELOG.md | [Unreleased] 公共 API 行 |

## 验证计划

- PalDDD.CQRS.Tests：handler 取消传播与 finally 清理；`PalValidatorRunner` 正例负例（含占位条目语义）；
- PalDDD.DependencyInjection.Tests：注册解析、重复注册快速失败；
- PalDDD.Hosting.AspNetCore.Tests（WebApplicationFactory 既有设施）：happy path 帧序/帧格式/响应头；验证失败 400 且在头写出前；客户端断开取消传播；中途异常 error 帧；POST 畸形 body 400/415；formatFrame 自定义帧；
- 变异验证：默认帧路径临时换 string 形态，零分配断言判红后恢复（对齐仓内变异红证明惯例）；
- aot-verify：AotSample 流端点随 CI 真发布运行。

## 分期

Phase 1 = 上文全部。Phase 2（触发式，见复活触发器）：event/id 帧与 Last-Event-ID 恢复；心跳 keepalive；PipeWriter 化（bench 证明默认帧路径成热点）；per-stream Channel 缓冲（扇出/迟附出现）；dispatcher 集成加 `IStreamPipelineBehavior`（流级横切需求）；WebSocket 传输（另立议题）。

### 实施附记（2026-10-18，Phase 2 收窄轮）

按「对 AI agent 流式场景的用途」逐项评估后收窄实施（用户令「把值得做的都做了」，评估过程：六项中两项成立、一项半成立、三项不成立）：

**已实施**：

- **心跳 keepalive**（`keepAliveInterval`，默认 null 关闭）：agent 流的静默间隙属结构性常态（首 token 前可停几十秒、工具轮间隙达分钟级），中间设备空闲断连（nginx 默认 60s、部分云 LB 4min）会在工具轮之间掐断长 run；心跳为注释帧 `:keepalive`（规范内客户端忽略、不计入 item 数）。心跳路径为手动枚举器 + MoveNext 与心跳延时竞速（同步完成项走快路径，默认 await foreach 路径零额外开销）；取消经延时取消态优先于心跳，挂起项落地后统一走 OCE 静默收尾（等待而非丢弃挂起任务，避免未观测任务异常）。
- **event/id 帧**（`eventSelector`/`idSelector` 选择器）：复活触发器 2 的「收 event 名支持进框架」提前满足（多家 LLM API 原生说分型 SSE，且 Phase 1 的 formatFrame 每消费方一份即 fork 前兆）。框架富化帧 `event: {名}\nid: {id}\ndata: {json}\n\n`（缺项行省略，event/id 字符串 UTF-8 直写 span 零分配）；`formatFrame` 保持最高优先级。
- **Last-Event-ID 入口**（`StreamResumeExtensions.GetLastEventId()`）：只做请求头读取与 id 行写出，续传语义留应用层。实时生成流的中途缓冲（续传前提）与 per-stream Channel 项耦合，同批收窄。

**P1 性能轮（2026-10-18 晚，`--stream` 实测驱动的收尾优化）**：bench 数据显示每帧 921ns 中框架可控 CPU 约 9%，每项分配里框架可控的仅剩序列化的 56B byte[]。将默认/富化两分支的序列化换为 `StreamFrameSerializer`（ThreadStatic 池化 `Utf8JsonWriter` + `ArrayBufferWriter`，复用 `JsonMessageSerializer` 的 R8/ADR-029 形态，含重入限制与 256KB 弃槽纪律），实测每项 56B→0B、CPU 持平到更快（探针对照），端点请求分配 −13%。至此默认帧路径 framing 与序列化双零分配，D4 的「默认路径零分配」声明获得完整实证（此前序列化器输出按项产出的注记删除）。锁零分配测试三件（每项 0B、输出与 SerializeToUtf8Bytes 逐位一致、池内 span 生命周期）+ 变异红证明（byte[] 回退判红）。观察态一项：富化帧常量事件名的 UTF-8 重复编码（+23ns/帧，可按引用相等记忆降到 ~5ns，收益 0.18% 量级低于「<5% 默认不做」线）。

**收窄暂不实施**（触发条件未满足，复发时按上方原触发器走）：

- **PipeWriter 零拷贝化**：触发器 3 要求 bench 证明热点。2026-10-18 `--stream` 基准实测（`StreamReadBenchmarks`，docs/performance.md）：默认帧 framing 5.19ns/0B，占每帧「序列化 + flush」921ns 的 0.6%；零拷贝对照形态（Utf8JsonWriter 直写 PipeWriter）CPU 持平而分配 2.4×（136B vs 序列化器 56B——writer 内部缓冲 churn 更重），带 flush 慢 5%。agent 流成本地板为 provider 延迟，结构性论证与实测双向确认收窄。
- **per-stream Channel 缓冲**：单客户端单次生成形态为纯开销（Phase 1 被否方案同款论证）；迟附/扇出的真实受益场景需要流注册表、有界缓冲与重连身份的独立设计，另立议题。
- **dispatcher 集成 + IStreamPipelineBehavior`**：无已出现的流级横切消费方（验证已在头写出前、日志在端点级、计量可走 D6 finally、fallback 属 handler/provider 层）；硬建相当于把 Phase 1 已否掉的 dispatcher 集成变体换名回来，还要面对 scope 生命周期老问题。
- **WebSocket 传输**：LLM 流单向，双工无增益；中断生成用现有命令端点加取消注册表覆盖；ADR 明示另立议题。

## 复活触发器（本决策失效条件）

1. 出现必须挂管道行为的流式场景，dispatcher 集成与 `IStreamPipelineBehavior` 同批建，不单加 dispatcher 方法；
2. 两个以上消费方各自 fork 帧格式，收 event 名支持进框架（**已于 2026-10-18 提前满足并实施，见分期节实施附记**）；
3. bench 证明默认帧路径成热点，PipeWriter 零拷贝化（**2026-10-18 收窄 + bench 实测证伪触发前提：默认帧路径 5.19ns/0B 占「序列化 + flush」每帧 921ns 的 0.6%；零拷贝对照形态（Utf8JsonWriter 直写 PipeWriter）CPU 持平（77.9 vs 78.4ns，Error 带内）而分配 2.4×（136B vs 56B，writer 内部缓冲 churn 重于序列化器单次 byte[]），带 flush 后慢 5%——「默认帧路径成热点」不成立，证据见 docs/performance.md 流式读链路首测**）；
4. brokerless 单进程多消费者消息成刚需，另立最小总线决策（与流式正交，勿复用 EventBus 名义）；
5. 流式审计约定被反复违反，上流完成钩子或 analyzer 规则。

## 已知约束（随实现进 usage.md）

- 浏览器原生 EventSource 仅 GET；POST 形态面向 fetch 与服务端消费方；
- 响应压缩中间件会缓冲事件流，端点与压缩交互需实测，必要时端点级禁用并记载中间件排序；
- 中途异常无法回退 HTTP 状态码（头已写出），以 event: error 帧加流终止表达，消费方须同时处理截断。
