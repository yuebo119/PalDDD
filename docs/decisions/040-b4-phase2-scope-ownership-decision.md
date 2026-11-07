# ADR 040：B4 阶段 2 决策 —— 暂不回退 ADR-026，先观测并锁定触发条件

> 状态：**已采纳（2026-11-07）**：决策是「**不实施**根因修复」，附完整设计草案与触发条件
> 日期：2026-11-07
> 关联：ADR-038（B4 阶段 1 登记）、ADR-026（Dispatcher 复用 RequestServices 性能定案）、修复方案 v4（`docs/review/fix-plan-2026-11-07-v4.md` §2.2）

## 消费状态

| 项 | 值 |
|------|-----|
| 落盘日期 | 2026-11-07 |
| 评审状态 | 已采纳：本 ADR 回答「B4 阶段 2 做不做、怎么做」——结论是**当前不做**，理由与触发条件见下 |
| 兼容性 | 无代码变更（本 ADR 只做裁决与设计备案）；阶段 1 的 ADR-038 行为保持不变 |
| 触发原因 | 修复方案 v4 把 B4 拆为阶段 1（登记+披露）与阶段 2（scope 归属根因修复），后者列为「需 ADR + 性能裁决 + 用户裁决」的未决项 |

## 背景

B4 的根因链条（ADR-038 已记）：`TimeoutBehavior` 超时只放弃等待 → handler 在后台继续 →
HTTP 路径复用 `RequestServices`（ADR-026 性能定案）→ 504 响应后 ASP.NET 释放请求 scope →
abandoned handler 触碰 scoped 依赖抛 ODE。

**硬约束（本轮复核确认）**：只要 HTTP 路径继续复用 `RequestServices`，Dispatcher 就**无权**
延迟释放该 scope——它的生命周期由 ASP.NET 的请求管道掌握。因此「根因修复」与「保留 ADR-026
性能定案」在结构上互斥，不存在第三条路：

- 路径 A：`PipelineStateMachine` 的 `next` 接受可替换的 `IServiceProvider`，由 Dispatcher 提供
  请求级工作上下文承载 scope 生命周期；
- 路径 B：引入请求级工作上下文（scope 持有者），在 `SendAsync` 返回前等 abandoned 任务收尾再释放。

两条都要求 Dispatcher 自建 scope → **每请求两个 DbContext**（中间件侧 + Dispatcher 侧），
即 ADR-026 明确消除的最坏形态回归。

## 决策

**当前不实施阶段 2，保持 ADR-026 不动。** 四条理由：

1. **代价是确定的，收益是不确定的**。回退 ADR-026 的性能损失有实测依据（ADR-026 的定案来自
   「每请求两个 DbContext/连接」的实测消除）；而 B4 的风险是**条件性**的（超时 ∧ handler 继续跑
   ∧ 触碰 scoped 依赖 ∧ 客户端重试），审计只证明机制存在、未证明发生频率。
2. **阶段 1 已把风险变可见**。`ObserveFault` 现在把 abandoned handler 失败记成
   `pal.timeout.abandoned-handler-fault` Activity 事件——若生产上真的发生，现在**可观测**。
   在没有观测数据时为一个未观测到的风险付确定性能代价，不符合「昂贵难逆决策需实测数据」的纪律。
3. **有更低成本的缓解路径**：调用方保证 handler 幂等（ADR-038 已在 usage.md 披露），
   可消化「504 重试双写」这一半风险；ODE 这一半可通过「超时预算覆盖实际耗时」的配置纪律规避。
4. **回退 ADR-026 属性能定案变更**，按项目规则必须由独立性能基准（同会话 A/B、机器漂移区间）
   支撑，不能凭推演。

## 阶段 2 设计草案（触发条件命中时按此实施）

### 路径 B（推荐）：请求级工作上下文

1. `PalDDD.CQRS` 新增 `internal sealed class RequestWorkScope`：持 `IServiceScope` +
   `ConcurrentBag<Task>`（abandoned 任务登记）+ `Task DrainAsync(TimeSpan upperBound)`。
2. `Dispatcher.ExecutePipelineAsync` 的**三个分支统一**改为：自建 `RequestWorkScope` →
   用其 `ServiceProvider` 调 `entry.Executor(...)` → 成功/超时/OCE 都在返回前
   `await DrainAsync(ShutdownTimeout)` → 释放 scope。
   （三分支统一：v3 只改 `requestServices is not null` 分支，后台/消息路径会 NRE——ADR-038 已记该否决。）
3. `TimeoutBehavior` 通过 `Activity.Current` 之外的**显式通道**（如 `RequestWorkScope.Current`
   静态 AsyncLocal）把 `running` 登记进当前工作上下文。`AsyncLocal` 必须每请求显式新建
   （v3 探针实证 `??=` 会跨请求污染）。
4. 504 时序不变：`WaitAsync` 仍按 `_timeout` 抛，`DrainAsync` 在**端点返回前**执行——
   这会推迟 504（v3 探针 118ms→505ms），因此**必须**改为「Dispatcher 返回后由后台收尾释放
   scope」，而不是「返回前 Drain」。真正的形态是：把 scope 交给一个 `Task.Run` 收尾器，
   请求线程立即返回 504 —— 这条路径的 AOT 兼容性与异常观察需专门验证。

### 路径 A（备选）：可替换 `IServiceProvider`

改 `PipelineStateMachine` 的 `next` 委托签名为 `Func<IServiceProvider, ValueTask<TResponse>>`，
其余同上。改动面更大（触及所有 `IPipelineBehavior` 实现），但 scope 归属最清晰。

### 验收判据（实施时必须全部满足）

| # | 判据 |
|---|------|
| 1 | 504 端点响应时刻 ≤ `_timeout + 50ms`（ADR-038 的 `SlowNext_TimeoutDoesNotWaitForAbandonedHandler` 保持绿） |
| 2 | abandoned handler 完成前 scope 不释放（scoped 依赖可解析，零 ODE） |
| 3 | 性能 A/B：同会话交替，Dispatcher 自建 scope vs 复用 RequestServices，附比值区间与机器漂移说明 |
| 4 | AOT 两样本（`PublishAot` + 运行）通过；`AsyncLocal`/`Task.Run` 收尾器无 trim 警告 |
| 5 | 三分支（HTTP / 无 scope 实例 / 自建 scope）均有锁定测试 |

## 触发条件（命中任一条即启动阶段 2）

1. 生产环境出现 `pal.timeout.abandoned-handler-fault` Activity 事件（阶段 1 已提供该信号）；
2. 生产环境出现与超时相关的 `ObjectDisposedException` 报告；
3. 504 端点接入请求级幂等键设计（届时 scope 归属与幂等键可合并设计）；
4. 有请求级 scope 归属的性能裁决结论（含 A/B 数据）。

## 证据锚点表

| 主张 | 锚点 |
|------|------|
| 超时只放弃等待 | `src/PalDDD.CQRS/TimeoutBehavior.cs:70-84` |
| HTTP 复用请求 scope（ADR-026 定案） | `src/PalDDD.CQRS/Dispatcher.cs:310-314` |
| 504 映射与响应写出 | `src/PalDDD.Hosting.AspNetCore/AspNetCore/ExceptionMiddleware.cs:106-128` |
| 阶段 1 的可观测信号 | `src/PalDDD.CQRS/TimeoutBehavior.cs` 的 `ObserveFault`（`pal.timeout.abandoned-handler-fault`） |
| 阶段 1 的取舍记录 | `docs/decisions/038-timeout-abandoned-scope.md` |
| 504 时序锁定测试 | `test/PalDDD.CQRS.Tests/TimeoutBehaviorTests.cs` 的 `SlowNext_TimeoutDoesNotWaitForAbandonedHandler` |

## 开放决策点

1. 路径 A 还是 B（本 ADR 推荐 B，但 A 在 scope 归属上更清晰）。
2. 「返回后由后台收尾释放 scope」的具体形态（`Task.Run` 收尾器 vs `IAsyncDisposable` 注册）及其 AOT 表现。
3. 性能基线的采集方式（新增 bench 类 vs 复用 `--infra` 车道）。
