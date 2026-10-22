# ADR 031：断线续跑（运行注册表 + 有界回放缓冲 + 附着/取消端点）

> 状态：**已采纳（2026-10-18 完整实施轮，D1-D6 全部落地；2026-10-19 补强轮：遥测 + POST 启动形态 + AOT 烟测）**
> 日期：2026-10-18
> 关联：ADR-030（流式读形态——本 ADR 复活其分期节收窄的 per-stream Channel 项，形态收窄为单写者单附着）、EventBus 移除判决（本方案不恢复广播总线，docs/pitfalls.md D6）、ADR-006（EventLog 追加写语义边界）、docs/performance.md「流式读链路首测/收敛读数」、samples/PalDDD.AgentSample（断线续跑段已落地）

## 背景

Agent run 的真实时间形态：首 token 前可静默几十秒，工具调用轮的间隙达分钟级，单轮可长数分钟。当前 ADR-030 的 D5 语义是端点把 `HttpContext.RequestAborted` 传给 handler，**连接断开即取消整轮**——切页、弱网、换设备这些移动端常态会让已消耗的 token 直接作废。

两类「断线」必须分开看：

1. **进行中断线**（本 ADR 的靶子）：run 仍在产出，用户期望回来后接着看。当前无任何附着路径。
2. **完成后回看**：已由 `IEventLog.ReadStreamAsync` 有序读 + `Last-Event-ID` 覆盖，`PalDDD.AgentSample` 已给出参考实现，本 ADR 不改。

ADR-030 收窄 per-stream Channel 缓冲的理由是「扇出/迟附三形态未出现」。现在**迟附**作为真实消费方场景明确出现（长 run 断线重连），触发条件满足；扇出与多消费者仍然没有消费方，本 ADR 不建。

## 决策

**D1 运行注册表独立于 HTTP 请求**。run 由进程内单例 `StreamRunRegistry` 持有；HTTP 连接断开只解除附着（detach），不取消 run。取消只来自三条显式路径：取消端点、`DetachGrace` 超时（无附着宽限期）、run 预算超限（`MaxRunDuration`/`MaxItems`）。

**D2 单写者 + 单附着者 + 有界环形缓冲**。每个 run 一个写者（handler 枚举），任意时刻至多一个活跃附着连接（后到者接管，见 D6）。缓冲按**项计数**（默认 1024 项），溢出丢最旧并记录「最旧可用 index」；附着位置早于该 index 时首帧发 `event: gap`（携带最旧可用 index），客户端自行决定重开整轮或接受缺口。

**D3 run 生命周期四态与三条硬边界**：`Running → (Completed | Failed | Cancelled)`，终态不可迁移。硬边界：`MaxRunDuration`（默认 10 分钟）、`MaxItems`（默认 100,000）、`DetachGrace`（默认 5 分钟无附着即取消）。宽限期是「不白烧 token」语义的落点——用户关页永不回来时，run 在宽限期后自行终止。

**D4 附着语义**。`GET {pattern}/{runId}/stream`，遵守 `Last-Event-ID`：先回放缓冲中位置之后的项，再转 live。三态答复：未知或已过期 run → 404；位置早于缓冲起点 → 200 + 首帧 `event: gap`；run 已终态且在 `RetentionAfterCompletion` 窗口内 → 回放剩余缓冲后以 `event: done` 收尾（窗口为尽力而为：总跟踪数达到 2×`MaxConcurrentRuns` 时最旧终态 run 提前逐出）。

**D5 取消端点**。`POST {pattern}/{runId}/cancel`，幂等（终态 run 返回 204 不报错）。取消令牌是 run 自己的 `CancellationTokenSource`（不是 `RequestAborted`），handler 的 finally 照常执行，D6 审计与 EventLog 追加不受影响。

**D6 附着接管（takeover）**。新附着到达时先向旧连接发 `event: detached` 再关闭，**不返回 409**。理由：切设备与弱网重连的竞态下 409 会逼客户端退避重试，体验更差；单写者语义不受影响（仍只有一个活跃写入方向）。选项 `RejectNewAttach = true` 可切回 409 形态（对要求强独占的消费方）。

**D7 不持久化、不跨进程**。注册表是进程内状态：进程重启 → 进行中 run 丢失（已写出部分不可恢复）；已完成 run 的审计与回放仍全部走 EventLog。理由：逐 token 持久化等于把流式做成事件溯源写放大（ADR-006 边界：EventLog 承载领域事件，不承载传输分片）。多实例部署的会话亲和问题登记为已知约束。

**D8 命名与边界：不是 EventBus**。单写者、单附着者、无订阅模型、无跨请求投递保证——这是会话缓冲，不是消息总线。EventBus 移除判决不受影响（docs/pitfalls.md D6）。

**D9 与既有组件的分工**：

| 组件 | 职责 | 本 ADR 的关系 |
|---|---|---|
| 运行注册表 | 进行中 run 的传输层会话状态（内存、可丢） | 新增 |
| EventLog | 完成态审计与回放（持久、有序） | 不变（AgentSample 已示范） |
| Saga | run 内部业务工作流的补偿编排 | 正交；run 本身不是 saga |
| Outbox | run 向外发布事件 | 不变 |
| Idempotency | 工具调用副作用去重 | 不变 |

## API 设计（定稿；2026-10-19 补强后）

```csharp
// PalDDD.Hosting.AspNetCore（新文件 DetachedStreamEndpoints.cs）
public sealed class DetachedStreamOptions
{
    public int MaxBufferedItems { get; set; } = 1024;
    public TimeSpan DetachGrace { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan MaxRunDuration { get; set; } = TimeSpan.FromMinutes(10);
    public int MaxItems { get; set; } = 100_000;
    public TimeSpan RetentionAfterCompletion { get; set; } = TimeSpan.FromMinutes(10);
    public int MaxConcurrentRuns { get; set; } = 1_000;
    public bool RejectNewAttach { get; set; } = false;
}

// 启动即附着（单次往返）：GET 形态 + POST 形态（JSON body）；响应头 X-Run-Id 供后续重连
public static IEndpointConventionBuilder MapDetachedStreamQuery<TQuery, TItem>(
    this IEndpointRouteBuilder endpoints, string pattern,
    Func<HttpContext, TQuery> bindQuery,
    JsonTypeInfo<TItem> itemJsonTypeInfo,
    Func<TItem, string>? eventSelector = null,
    Func<TItem, string>? idSelector = null,
    TimeSpan? keepAliveInterval = null)
    where TQuery : CQRS.IStreamQuery<TItem>;

public static IEndpointConventionBuilder MapDetachedStreamQuery<TQuery, TItem>(
    this IEndpointRouteBuilder endpoints, string pattern,
    JsonTypeInfo<TQuery> queryJsonTypeInfo,
    JsonTypeInfo<TItem> itemJsonTypeInfo,
    Func<TItem, string>? eventSelector = null,
    Func<TItem, string>? idSelector = null,
    TimeSpan? keepAliveInterval = null)
    where TQuery : CQRS.IStreamQuery<TItem>;

// 重连附着（遵守 Last-Event-ID；event/id 已在写入时固化，不接收选择器）
public static IEndpointConventionBuilder MapStreamRunAttach<TItem>(
    this IEndpointRouteBuilder endpoints, string pattern,
    JsonTypeInfo<TItem> itemJsonTypeInfo,
    TimeSpan? keepAliveInterval = null);

// 显式取消（幂等）
public static IEndpointConventionBuilder MapStreamRunCancel(
    this IEndpointRouteBuilder endpoints, string pattern);

public static IServiceCollection AddDetachedStreamRuns(
    this IServiceCollection services, Action<DetachedStreamOptions>? configure = null);
```

内部构件：`StreamRun`（状态机 + 环形缓冲 + 自有 CTS + 完成信号）、`StreamRunRegistry`（并发字典 + 过期清理 + 并发上限）。缓冲元素为 `(long Index, TItem Item, string? EventName, string? Id)`——**event/id 在写入时固化**，附着回放不重跑选择器（选择器可能有副作用，重跑会产出与首播不一致的帧）。

## 被否方案

| 方案 | 否决理由 |
|---|---|
| 维持现状（断线即取消，客户端整轮重开） | 长 run 白烧 token；切页/弱网是移动端常态而非异常；run 越长损失越大 |
| 逐 token 持久化到 EventLog/Outbox 再回放 | 写放大（token 一行）；EventLog 语义是领域事件而非传输分片（ADR-006），混用污染审计面 |
| 恢复通用广播总线（EventBus 名义） | 单消费者场景不需要订阅模型；广播语义与跨请求投递保证的复杂度远超本需求；EventBus 移除判决不受影响 |
| 多附着 / 扇出 | 无消费方；单附着已覆盖切页与换设备；扇出属独立设计（见复活触发器 1） |
| 两阶段 API（先 POST 建 run 再 GET 附着） | 多一次往返且首帧延迟增加；启动即附着 + `X-Run-Id` 响应头已覆盖重连场景 |
| 缓冲按字节计 | 变长项需要分段环形结构，复杂度高；按项计数 + `MaxItems` 硬上限已能界定内存（token 项几十字节量级）；大载荷条目应由应用层以引用 ID 承载 |
| 附着冲突返回 409（不接管） | 重连竞态下客户端被迫退避重试；接管语义更贴近移动端体验且单写者不变（保留为可选项） |
| 用 Saga 承载 run 生命周期 | Saga 语义是补偿链事务，不是传输会话；把 run 建模成 saga 会引入无意义的补偿步骤与持久化成本 |

## 实现清单

| 位置 | 改动 |
|---|---|
| src/PalDDD.Hosting.AspNetCore/AspNetCore/StreamRunRegistry.cs | 新建：`StreamRunRegistry` + `StreamRun` + 环形缓冲（internal） |
| src/PalDDD.Hosting.AspNetCore/AspNetCore/DetachedStreamEndpoints.cs | 新建：三个端点 + `DetachedStreamOptions` |
| src/PalDDD.Hosting.AspNetCore/AspNetCore/EndpointExtensions.cs | 复用 `StreamFrameWriter` / `StreamFrameSerializer` / `PalValidatorRunner` / `StreamResumeExtensions` |
| src/PalDDD.DependencyInjection/ServiceRegistration.cs | `AddDetachedStreamRuns`（`TryAddSingleton` + 选项校验） |
| samples/PalDDD.AgentSample | 增加断线→附着段（真 HTTP，喂样本断言） |
| test/PalDDD.Hosting.AspNetCore.Tests | 缓冲溢出/gap/接管/取消/宽限期/404 六组锁定测试 |
| docs/conventions.md §1.10 | 断线续跑一节：何时用、生命周期边界、与 EventLog 分工 |
| docs/usage.md | 完整示例（含 X-Run-Id 与 Last-Event-ID 客户端写法） |
| README.md / README.en.md / docs/pitfalls.md | ADR 计数 30 → 31 |

## 验证计划

- 单元：环形缓冲溢出与「最旧可用 index」；注册表并发上限与过期清理；状态机非法迁移拒绝。
- Hosting（DefaultHttpContext 直调 delegate，同既有流式测试形态）：启动即附着拿到 `X-Run-Id`；断线后重连按 `Last-Event-ID` 回放；gap 帧；cancel 幂等且 handler finally 执行；`DetachGrace` 超时取消；接管关闭旧连接；单附着语义；未知 run 404。
- 样本：AgentSample 增断线/重连段，断言重连后帧续接。
- 变异验证：去掉缓冲回放（直发 live）→ 重连测试判红；去掉 gap 判定 → 溢出重连测试判红。
- AOT：注册表与管理端点为纯托管代码零反射；AgentSample 为 Web SDK 不参与 AOT 发布，若需 AOT 覆盖则在 AotSample 加最小烟测。

## 分期

Phase 1 = D1-D6 全部（注册表 + 三端点 + 选项 + 测试 + 样本 + 文档）。Phase 2（触发式）：多附着/扇出；跨进程与多实例亲和；跨重启续跑（触发即另立持久化分片 ADR，先做写放大评估）。

## 实施附记（2026-10-18，完整实施轮）

**与提案的两处偏离**（实施期修正，均在代码注释成文）：

1. **`AddDetachedStreamRuns` 落在 `PalDDD.Hosting.AspNetCore` 而非 `ServiceRegistration`**（提案实现清单原写 DI 包）：DI 扩展就地在 Hosting 注册（对齐 `AddPalHealthChecks` 先例），避免 Hosting → DependencyInjection 反向依赖。
2. **run 自有 `IServiceScope`**：run 活过请求作用域，因此 handler 从 run 自有 scope 解析（scoped 依赖如 DbContext 不能在请求结束时被销毁），run 终态 `Finish` 时确定性释放该 scope。这正是 ADR-030 被否的「dispatcher 持有对象变体」缺失的释放责任，本 ADR 由 run 生命周期显式承担。测试 `RunScope_IsDisposedAfterCompletion` 用 scoped 探针锁定。

**实现期实证教训**（三条，均入 pitfalls 本仓实证族）：

- SSE 响应未读完时 `HttpResponseMessage.Dispose()` 会同步等待连接关闭，与仍在写的服务端互相等待（表现为测试/样本挂死）——断线须用「取消请求 CTS」abort 底层连接；
- `HttpClient` 连接池在 abort 后复用可能进入异常态，重连用独立实例（真实客户端重连通常也走新连接）；
- 终态收尾帧（`event: done`）不带 id，客户端按 id 解析时必须过滤无 id 帧（样本断言曾对 `int.Parse(null)` 崩溃）。

**测试覆盖**（20 件）：注册表级 11 件（泵启动与缓冲、溢出丢弃与最旧可用前移、cancel 终止与幂等、未知 run、`MaxItems` 预算、并发上限、`DetachGrace` 无附着取消、自定义 id 反查、滚出位置报 gap、`RejectNewAttach`、接管代际递增）+ HTTP 级 9 件（`X-Run-Id` 与帧序、断线后按 `Last-Event-ID` 续读、gap 帧先于数据帧、取消幂等且终态回放 `cancelled`、未知 run 404、终态回放缓冲后 `done`、接管使旧连接收 `detached`、`RejectNewAttach` 返 409、run scope 释放）。

> **`DetachGrace` 的 HTTP 级验证取舍**：其语义由注册表级测试锁定（`DetachGrace_WithoutAttach_CancelsRun`）；HTTP 层「客户端断开 → 服务端观测 `RequestAborted`」依赖 TCP 半关闭时序，测试环境不稳定，不重复断言（避免用不稳定网络信号做语义判定）。

### 实施附记（2026-10-19 补强轮）

三项能力补全 + 一处文档勘正：

1. **遥测**：注册表泵开启 `Stream Query` Activity（tag 为查询/元素类型名），首个产出项记 `first-item` 事件（TTFT = 事件时间戳 − span 起点），终态计入既有五个 `paldd.stream.*` 计数器（取消独立于失败，与 MapStreamQuery 同口径）；新增 `paldd.stream.attached`（成功附着数，含首次/重连/接管）与 `paldd.stream.gap`（gap 帧写出次数）。run 未附着时仍计 started：产出由注册表泵驱动，与客户端是否在看无关。
2. **POST 启动形态**：`MapDetachedStreamQuery` 增 `JsonTypeInfo<TQuery>` 重载，反序列化段复用 `ReadJsonBodyOrWriteErrorAsync`（415/畸形 JSON/空 body 与 MapStreamQuery POST 逐字一致）。GET/POST 共用 `StartAndAttachAsync` 单一编排，验证、容量、附着错误路径不重复。
3. **AOT 烟测**：AotSample §8 在 PublishAot 产物中真起 Kestrel，覆盖 POST 启动 → 终态回放附着 → 幂等取消 204，纳入 CI `aot-verify`。
4. **文档勘正**：本 ADR 原 API 草案列了 `MapStreamRunAttach` 的 eventSelector/idSelector 参数，实际实现不接收（event/id 在写入时固化）；`formatFrame` 也从未进入断线续跑端点。已按实现修正，并移入已知约束。

验证：Hosting 面板 88/88（新增遥测 4 件 + POST 3 件）；AotSample 普通运行全过（含 §8 四项断言）；AOT 发布由 CI `aot-verify` 执行。

## 复活触发器（本决策失效条件）

1. 出现多消费者/扇出真实需求 → 多附着与投递语义另立 ADR，不在本 ADR 上扩展；
2. 出现跨进程/跨重启续跑刚需 → 走持久化分片 ADR（写放大评估先行，禁止逐 token 写 EventLog）；
3. 断线率实测可忽略（如 <1% 且 run 平均时长 <30 秒）→ 移除本机制，回退整轮重开；
4. 「缓冲缺口」投诉成为常态 → 评估分段溢出到磁盘，但需先有实测数据支撑。

## 已知约束（随实现进 usage.md）

- 进程内状态：重启丢进行中 run；多实例部署需会话亲和（粘性路由）或把 run 固定到单实例。
- 内存上界 ≈ `2 × MaxConcurrentRuns × MaxBufferedItems × 平均项大小`：运行中与保留终态各至多 `MaxConcurrentRuns`；总跟踪数达到 2×上限时最旧的终态 run 被提前逐出（`RetentionAfterCompletion` 为尽力而为）。token 项约百字节量级时，1000 run × 1024 项 ≈ 200MB 上界，部署须按实际项大小调参。
- 附着回放不重跑 event/id 选择器（写入时固化）；断线续跑端点**不暴露** `formatFrame` 参数——需要自定义整帧的消费方使用 `MapStreamQuery`（ADR-030），或等待 Phase 2 为注册表扩展帧形态。
- 本 ADR 未实施前现状不变：断线即取消，客户端整轮重开。
